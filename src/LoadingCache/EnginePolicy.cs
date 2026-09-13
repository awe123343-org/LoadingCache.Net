namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private void PublishPolicyWriteLocked(object? token, long weight)
    {
        _policy.OnPublishLocked(token, weight);
        if (_evictionListener is not null || weight > _policy.Maximum)
        {
            // Capture this publication's evictions before another maintenance
            // owner can claim it. The operation scope dispatches after unlock.
            // Oversized values likewise cannot remain deferred after publication.
            _policy.FlushWrites();
        }
    }

    private void RemovePolicyWriteLocked(object? token)
    {
        _policy.OnRemoveLocked(token);
        if (_evictionListener is not null)
        {
            _policy.FlushWrites();
        }
    }

    private (bool Found, TValue Value) QuietLookup(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        RemovalNotification<TKey, TValue>? pendingEviction = null;
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            bool collected;
            lock (entry.Sync)
            {
                if (
                    Volatile.Read(ref entry.IsReady)
                    && !IsExpired(entry, _timeProvider.GetTimestamp())
                    && entry.TryGetValue(out TValue? value)
                )
                {
                    return (true, value);
                }

                collected = Volatile.Read(ref entry.IsReady) && entry.IsValueCollected;
            }

            if (collected)
            {
                lock (_gate)
                {
                    if (_disposed == 0)
                    {
                        pendingEviction = RemoveCurrentEntryLocked(entry, collected: true);
                    }
                }
            }
        }

        if (pendingEviction is { } evictionNotification)
        {
            DispatchSynchronousEviction(evictionNotification);
        }
        evictionScope.Dispatch();
        return (false, default!);
    }

    private sealed class EngineEvictionView(CacheEngine<TKey, TValue> engine, bool weighted)
        : IEvictionPolicy<TKey, TValue>
    {
        public long Maximum
        {
            get
            {
                lock (engine._gate)
                {
                    engine.ThrowIfDisposedLocked();
                    return engine._policy.Maximum;
                }
            }
        }

        public long WeightedSize
        {
            get
            {
                using SynchronousEvictionScope evictionScope = engine.BeginSynchronousEvictionScope(
                    completePolicyWrites: false
                );
                lock (engine._gate)
                {
                    engine.ThrowIfDisposedLocked();
                    engine._policy.FlushWrites();
                    return engine._policy.WeightedSize;
                }
            }
        }

        public void SetMaximum(long maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            if (!weighted && maximum > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximum),
                    "A size maximum must fit in an Int32."
                );
            }

            using SynchronousEvictionScope evictionScope = engine.BeginSynchronousEvictionScope();
            lock (engine._gate)
            {
                engine.ThrowIfDisposedLocked();
                engine._policy.SetMaximum(maximum, weighted);
            }

            evictionScope.Dispatch();
            engine.RequestExpirationTimer();
        }

        public IReadOnlyList<KeyValuePair<TKey, TValue>> Coldest(int limit) =>
            Snapshot(limit, false);

        public IReadOnlyList<KeyValuePair<TKey, TValue>> Hottest(int limit) =>
            Snapshot(limit, true);

        private System.Collections.ObjectModel.ReadOnlyCollection<
            KeyValuePair<TKey, TValue>
        > Snapshot(int limit, bool hottest)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(limit);
            using SynchronousEvictionScope evictionScope = engine.BeginSynchronousEvictionScope(
                completePolicyWrites: false
            );
            lock (engine._gate)
            {
                engine.ThrowIfDisposedLocked();
                engine._policy.FlushWrites();
                var tokens = engine._policy.Snapshot(hottest, limit);
                var result = new List<KeyValuePair<TKey, TValue>>(tokens.Count);
                foreach (var token in tokens)
                {
                    if (
                        token is not Entry entry
                        || entry.Epoch != engine._epoch
                        || !engine._entries.IsCurrent(entry)
                    )
                    {
                        continue;
                    }

                    lock (entry.Sync)
                    {
                        if (
                            Volatile.Read(ref entry.IsReady)
                            && !entry.PolicyDetached
                            && !engine.IsExpired(entry, engine._timeProvider.GetTimestamp())
                        )
                        {
                            if (
                                entry.TryGetKey(out TKey? key)
                                && entry.TryGetValue(out TValue? value)
                            )
                            {
                                result.Add(new KeyValuePair<TKey, TValue>(key, value));
                            }
                        }
                    }
                }

                return result.AsReadOnly();
            }
        }
    }
}
