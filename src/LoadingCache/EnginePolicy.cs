namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private (bool Found, TValue Value) QuietLookup(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            lock (entry.Sync)
            {
                if (
                    Volatile.Read(ref entry.IsReady)
                    && !IsExpired(entry, _timeProvider.GetTimestamp())
                )
                {
                    return (true, entry.Value);
                }
            }
        }

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
                lock (engine._gate)
                {
                    engine.ThrowIfDisposedLocked();
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

            lock (engine._gate)
            {
                engine.ThrowIfDisposedLocked();
                engine._policy.SetMaximum(maximum, weighted);
            }

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
            lock (engine._gate)
            {
                engine.ThrowIfDisposedLocked();
                var tokens = engine._policy.Snapshot(hottest, limit);
                var result = new List<KeyValuePair<TKey, TValue>>(tokens.Count);
                foreach (var token in tokens)
                {
                    if (
                        token is not Entry entry
                        || entry.Epoch != engine._epoch
                        || !engine._entries.TryGetValue(entry.Key, out Entry? current)
                        || !ReferenceEquals(current, entry)
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
                            result.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
                        }
                    }
                }

                return result.AsReadOnly();
            }
        }
    }
}
