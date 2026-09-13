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
    internal bool TryGetOwned(
        TKey key,
        Func<TValue, bool> tryAcquire,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(tryAcquire);
        ThrowIfDisposed();

        Entry readyEntry;
        object? policyToken;
        long variableTimestamp;
        long variableRevision;
        TimeSpan variableDuration;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_entries.TryGetValue(key, out Entry? entry) || !Volatile.Read(ref entry.IsReady))
            {
                value = default;
                RecordMiss();
                return false;
            }

            lock (entry.Sync)
            {
                long now = _timeProvider.GetTimestamp();
                if (!Volatile.Read(ref entry.IsReady) || IsExpired(entry, now))
                {
                    RemoveCurrentEntryLocked(entry, RemovalCause.Expired);
                    value = default;
                    RecordMiss();
                    return false;
                }

                if (!entry.TryGetValue(out TValue? liveValue))
                {
                    RemoveCurrentEntryLocked(entry, collected: true);
                    value = default;
                    RecordMiss();
                    return false;
                }

                if (!tryAcquire(liveValue))
                {
                    value = default;
                    RecordMiss();
                    return false;
                }

                TouchWithoutLock(entry, now);
                value = liveValue;
                readyEntry = entry;
                policyToken = entry.PolicyToken;
                variableTimestamp = entry.VariableTimestamp;
                variableRevision = entry.VariableRevision;
                variableDuration = _expiry is null
                    ? TimeSpan.MaxValue
                    : GetRemainingDuration(entry, now, ExpirationKind.Variable);
            }
        }

        if (_expiry is not null)
        {
            ApplyReadExpiryUpdate(
                readyEntry,
                key,
                value!,
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
