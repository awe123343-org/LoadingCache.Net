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

        Entry? readyEntry = null;
        object? policyToken = null;
        long variableTimestamp = 0;
        long variableRevision = 0;
        TimeSpan variableDuration = TimeSpan.MaxValue;
        bool acquired = false;

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
                    RemoveCurrentEntryLocked(entry);
                    value = default;
                    RecordMiss();
                    return false;
                }

                acquired = tryAcquire(entry.Value);
                if (!acquired)
                {
                    value = default;
                    RecordMiss();
                    return false;
                }

                TouchWithoutLock(entry, now);
                value = entry.Value;
                readyEntry = entry;
                policyToken = entry.PolicyToken;
                variableTimestamp = entry.VariableTimestamp;
                variableRevision = entry.VariableRevision;
                variableDuration = _expiry is null
                    ? TimeSpan.MaxValue
                    : GetRemainingDuration(entry, now, ExpirationKind.Variable);
            }
        }

        if (!acquired || readyEntry is null)
        {
            value = default;
            return false;
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
