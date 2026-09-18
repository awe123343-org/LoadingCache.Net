namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Reads a ready value and acquires an ownership handle while the engine
    /// gate still fences removal. The callback must only perform trusted,
    /// non-user ownership bookkeeping.
    /// </summary>
    internal bool TryGetOwned(TKey key, Func<TValue, bool> tryAcquire)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tryAcquire);
        ThrowIfDisposed();

        TValue value = default!;
        Entry readyEntry = null!;
        object? policyToken = null;
        long variableTimestamp = 0;
        long variableRevision = 0;
        TimeSpan variableDuration = TimeSpan.MaxValue;
        RemovalNotification<TKey, TValue>? pendingEviction = null;
        bool miss = false;
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_entries.TryGetValue(key, out Entry? entry) || !Volatile.Read(ref entry.IsReady))
            {
                RecordMiss();
                return false;
            }

            lock (entry.Sync)
            {
                long now = _timeProvider.GetTimestamp();
                if (!Volatile.Read(ref entry.IsReady) || IsExpired(entry, now))
                {
                    pendingEviction = RemoveCurrentEntryLocked(entry, RemovalCause.Expired);
                    RecordMiss();
                    miss = true;
                }
                else if (!entry.TryGetValue(out TValue? liveValue))
                {
                    pendingEviction = RemoveCurrentEntryLocked(entry, collected: true);
                    RecordMiss();
                    miss = true;
                }
                else if (!tryAcquire(liveValue))
                {
                    RecordMiss();
                    miss = true;
                }
                else
                {
                    TouchWithoutLock(entry, now);
                    value = liveValue!;
                    readyEntry = entry;
                    policyToken = entry.PolicyToken;
                    variableTimestamp = entry.VariableTimestamp;
                    variableRevision = entry.VariableRevision;
                    variableDuration = _expiry is null
                        ? TimeSpan.MaxValue
                        : GetRemainingDuration(entry, now, ExpirationKind.Variable);
                }
            }
        }

        if (miss)
        {
            if (pendingEviction is { } evictionNotification)
            {
                DispatchSynchronousEviction(evictionNotification);
            }
            evictionScope.Dispatch();
            return false;
        }

        if (_expiry is not null)
        {
            ApplyReadExpiryUpdate(
                readyEntry,
                key,
                value,
                variableTimestamp,
                variableRevision,
                variableDuration
            );
        }

        RecordHit();
        if (policyToken is not null)
        {
            _policy.OnAccess(policyToken);
        }

        return true;
    }
}
