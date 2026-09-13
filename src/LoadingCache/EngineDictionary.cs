using System.Diagnostics.CodeAnalysis;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal bool DictionaryTryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        return TryGet(key, out value);
    }

    internal KeyValuePair<TKey, TValue>[] DictionarySnapshot()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var snapshot = new List<KeyValuePair<TKey, TValue>>(_entries.Count);
            foreach (Entry entry in _entries.Values)
            {
                lock (entry.Sync)
                {
                    if (
                        entry.Epoch == _epoch
                        && Volatile.Read(ref entry.IsReady)
                        && !IsExpired(entry, _timeProvider.GetTimestamp())
                        && entry.TryGetKey(out TKey? key)
                        && entry.TryGetValue(out TValue? value)
                    )
                    {
                        snapshot.Add(new KeyValuePair<TKey, TValue>(key, value));
                    }
                }
            }

            return [.. snapshot];
        }
    }

    internal int DictionaryCount()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            int count = 0;
            foreach (Entry entry in _entries.Values)
            {
                lock (entry.Sync)
                {
                    if (
                        entry.Epoch == _epoch
                        && Volatile.Read(ref entry.IsReady)
                        && !IsExpired(entry, _timeProvider.GetTimestamp())
                        && entry.TryGetKey(out _)
                        && entry.TryGetValue(out _)
                    )
                        count++;
                }
            }

            return count;
        }
    }

    internal void DictionarySet(TKey key, TValue value)
    {
        Put(key, value);
    }

    internal bool DictionaryTryAdd(TKey key, TValue value)
    {
        ValidateDictionaryArguments(key, value);
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        try
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_entries.TryGetValue(key, out Entry? current))
                {
                    if (!Volatile.Read(ref current.IsReady))
                    {
                        return false;
                    }

                    bool collected;
                    lock (current.Sync)
                    {
                        if (!Volatile.Read(ref current.IsReady))
                        {
                            return false;
                        }

                        collected =
                            (_weakKeys && !current.TryGetKey(out _))
                            || (_weakValues && !current.TryGetValue(out _));
                        if (
                            !collected
                            && (
                                (
                                    current.RefreshFlight is not null
                                    && IsCurrentRefreshFlightLocked(current, current.RefreshFlight)
                                ) || !IsExpired(current, _timeProvider.GetTimestamp())
                            )
                        )
                        {
                            return false;
                        }
                    }

                    RemoveCurrentEntryLocked(
                        current,
                        collected ? RemovalCause.Collected : RemovalCause.Expired
                    );
                }
            }

            long weight = ComputeWeight(key, value);
            TimeSpan duration = ComputeCreateDuration(key, value);
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_entries.TryGetValue(key, out Entry? current))
                {
                    if (!Volatile.Read(ref current.IsReady))
                    {
                        return false;
                    }

                    bool collected;
                    lock (current.Sync)
                    {
                        if (!Volatile.Read(ref current.IsReady))
                        {
                            return false;
                        }

                        collected =
                            (_weakKeys && !current.TryGetKey(out _))
                            || (_weakValues && !current.TryGetValue(out _));
                        if (
                            !collected
                            && (
                                (
                                    current.RefreshFlight is not null
                                    && IsCurrentRefreshFlightLocked(current, current.RefreshFlight)
                                ) || !IsExpired(current, _timeProvider.GetTimestamp())
                            )
                        )
                        {
                            return false;
                        }
                    }

                    RemoveCurrentEntryLocked(
                        current,
                        collected ? RemovalCause.Collected : RemovalCause.Expired
                    );
                }

                PublishDictionaryEntryLocked(key, value, weight, duration);
            }
            RequestExpirationTimer();
            return true;
        }
        finally
        {
            evictionScope.Dispatch();
        }
    }

    internal bool DictionaryTryUpdate(TKey key, TValue value, TValue comparisonValue)
    {
        ValidateDictionaryArguments(key, value);
        if (comparisonValue is null)
        {
            throw new ArgumentNullException(nameof(comparisonValue));
        }

        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        try
        {
            Entry? expectedEntry;
            long expectedRevision;
            long expectedVariableRevision;
            TValue expectedValue;
            TimeSpan currentDuration;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (
                    !_entries.TryGetValue(key, out expectedEntry)
                    || !Volatile.Read(ref expectedEntry.IsReady)
                )
                {
                    return false;
                }

                lock (expectedEntry.Sync)
                {
                    if (!Volatile.Read(ref expectedEntry.IsReady))
                    {
                        return false;
                    }

                    if (IsExpired(expectedEntry, _timeProvider.GetTimestamp()))
                    {
                        if (
                            expectedEntry.RefreshFlight is null
                            || !IsCurrentRefreshFlightLocked(
                                expectedEntry,
                                expectedEntry.RefreshFlight
                            )
                        )
                        {
                            RemoveCurrentEntryLocked(expectedEntry, RemovalCause.Expired);
                        }

                        return false;
                    }

                    expectedRevision = expectedEntry.PublicationRevision;
                    expectedVariableRevision = expectedEntry.VariableRevision;
                    if (!expectedEntry.TryGetValue(out TValue? liveValue))
                    {
                        RemoveCurrentEntryLocked(expectedEntry, collected: true);
                        return false;
                    }
                    expectedValue = liveValue!;
                    currentDuration = _expiry is null
                        ? TimeSpan.MaxValue
                        : GetRemainingDuration(
                            expectedEntry,
                            _timeProvider.GetTimestamp(),
                            ExpirationKind.Variable
                        );
                }
            }

            if (!DictionaryValuesEqual(expectedValue, comparisonValue))
            {
                return false;
            }

            long weight = ComputeWeight(key, value);
            TimeSpan duration = ComputeUpdateDuration(key, value, currentDuration);
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (
                    !_entries.TryGetValue(key, out Entry? current)
                    || !ReferenceEquals(current, expectedEntry)
                    || current.Epoch != _epoch
                    || current.PublicationRevision != expectedRevision
                    || current.VariableRevision != expectedVariableRevision
                    || !Volatile.Read(ref current.IsReady)
                )
                {
                    return false;
                }

                lock (current.Sync)
                {
                    if (
                        !Volatile.Read(ref current.IsReady)
                        || IsExpired(current, _timeProvider.GetTimestamp())
                    )
                    {
                        return false;
                    }
                }

                PublishDictionaryEntryLocked(key, value, weight, duration);
            }

            RequestExpirationTimer();
            return true;
        }
        finally
        {
            evictionScope.Dispatch();
        }
    }

    internal bool DictionaryTryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        try
        {
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                MarkDictionaryTransformMutation(key);
                RecordBulkMutationLocked(key);
                if (
                    !_entries.TryGetValue(key, out Entry? current)
                    || !Volatile.Read(ref current.IsReady)
                )
                {
                    value = default;
                    return false;
                }

                lock (current.Sync)
                {
                    if (
                        !Volatile.Read(ref current.IsReady)
                        || IsExpired(current, _timeProvider.GetTimestamp())
                    )
                    {
                        value = default;
                        return false;
                    }

                    if (!current.TryGetValue(out value))
                    {
                        RemoveCurrentEntryLocked(current, collected: true);
                        value = default;
                        return false;
                    }
                }

                RemoveCurrentEntryLocked(current);
            }

            RequestExpirationTimer();
            return true;
        }
        finally
        {
            evictionScope.Dispatch();
        }
    }

    internal bool DictionaryTryRemove(TKey key, TValue comparisonValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (comparisonValue is null)
        {
            throw new ArgumentNullException(nameof(comparisonValue));
        }

        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        try
        {
            Entry? expectedEntry;
            long expectedRevision;
            TValue expectedValue;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                MarkDictionaryTransformMutation(key);
                RecordBulkMutationLocked(key);
                if (
                    !_entries.TryGetValue(key, out expectedEntry)
                    || !Volatile.Read(ref expectedEntry.IsReady)
                )
                {
                    return false;
                }

                lock (expectedEntry.Sync)
                {
                    if (!Volatile.Read(ref expectedEntry.IsReady))
                    {
                        return false;
                    }

                    bool expired = IsExpired(expectedEntry, _timeProvider.GetTimestamp());
                    if (expired)
                    {
                        if (
                            expectedEntry.RefreshFlight is null
                            || !IsCurrentRefreshFlightLocked(
                                expectedEntry,
                                expectedEntry.RefreshFlight
                            )
                        )
                        {
                            RemoveCurrentEntryLocked(expectedEntry, RemovalCause.Expired);
                        }

                        return false;
                    }

                    expectedRevision = expectedEntry.PublicationRevision;
                    if (!expectedEntry.TryGetValue(out TValue? liveValue))
                    {
                        RemoveCurrentEntryLocked(expectedEntry, collected: true);
                        return false;
                    }
                    expectedValue = liveValue!;
                }
            }

            if (!DictionaryValuesEqual(expectedValue, comparisonValue))
            {
                return false;
            }

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (
                    !_entries.TryGetValue(key, out Entry? current)
                    || !ReferenceEquals(current, expectedEntry)
                    || current.Epoch != _epoch
                    || current.PublicationRevision != expectedRevision
                    || !Volatile.Read(ref current.IsReady)
                )
                {
                    return false;
                }

                lock (current.Sync)
                {
                    if (
                        !Volatile.Read(ref current.IsReady)
                        || IsExpired(current, _timeProvider.GetTimestamp())
                    )
                    {
                        return false;
                    }
                }

                RemoveCurrentEntryLocked(current);
            }
            RequestExpirationTimer();
            return true;
        }
        finally
        {
            evictionScope.Dispatch();
        }
    }

    internal bool DictionaryContains(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DictionaryTryGet(key, out TValue? current) && DictionaryValuesEqual(current, value);
    }

    internal TValue DictionaryGetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        return GetOrAdd(key, valueFactory);
    }

    private bool DictionaryValuesEqual(TValue left, TValue right) =>
        _weakValues
            ? ReferenceEquals(left, right)
            : EqualityComparer<TValue>.Default.Equals(left, right);

    private void PublishDictionaryEntryLocked(
        TKey key,
        TValue value,
        long weight,
        TimeSpan variableDuration
    )
    {
        MarkDictionaryTransformMutation(key);
        RecordBulkMutationLocked(key);
        Entry entry = Entry.Ready(
            key,
            _epoch,
            ++_nextGeneration,
            value,
            _timeProvider.GetTimestamp(),
            weight,
            variableDuration,
            _weakKeys,
            _weakValues
        );
        entry.PolicyToken = new WindowTinyLfuEnginePolicy.EngineEntryToken(
            entry,
            GetPolicyHash(key)
        );
        ReplaceCurrentLocked(key, entry);
        PublishPolicyWriteLocked(entry.PolicyToken, entry.Weight);
        if (_expirationWheel is not null)
        {
            ulong normalizedNow = GetExpirationNowLocked();
            AdvanceExpirationLocked(normalizedNow);
            ScheduleExpirationNodeLocked(entry, normalizedNow);
        }
    }

    private static void ValidateDictionaryArguments(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
    }
}
