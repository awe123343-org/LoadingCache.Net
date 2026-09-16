namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private enum DictionaryTransformState
    {
        Missing,
        Present,
        Pending,
    }

    private static readonly AsyncLocal<DictionaryTransformNode?> TransformContext = new();
    private long _dictionaryMutationSequence;
    private object _dictionaryMutationEra = new();

    internal TValue DictionaryAddOrUpdate(
        TKey key,
        Func<TKey, TValue> addValueFactory,
        Func<TKey, TValue, TValue> updateValueFactory
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(addValueFactory);
        ArgumentNullException.ThrowIfNull(updateValueFactory);

        CacheMutation<TValue> mutation = DictionaryComputeCore(
            key,
            (currentKey, current) =>
                CacheMutation.Set(
                    current.HasValue
                        ? updateValueFactory(currentKey, current.Value)
                        : addValueFactory(currentKey)
                )
        );
        return mutation.Value;
    }

    internal CacheMutation<TValue> DictionaryCompute(
        TKey key,
        Func<TKey, CacheValue<TValue>, CacheMutation<TValue>> transform
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(transform);

        return DictionaryComputeCore(key, transform);
    }

    internal CacheMutation<TValue> DictionaryComputeIfPresent(
        TKey key,
        Func<TKey, TValue, CacheMutation<TValue>> transform
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(transform);

        return DictionaryComputeCore(
            key,
            (currentKey, current) =>
                current.HasValue
                    ? transform(currentKey, current.Value)
                    : CacheMutation.Keep<TValue>(),
            onlyIfPresent: true
        );
    }

    internal TValue DictionaryMerge(
        TKey key,
        TValue value,
        Func<TValue, TValue, TValue> mergeFactory
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(mergeFactory);

        CacheMutation<TValue> mutation = DictionaryComputeCore(
            key,
            (_, current) =>
                CacheMutation.Set(current.HasValue ? mergeFactory(current.Value, value) : value)
        );
        return mutation.Value;
    }

    private CacheMutation<TValue> DictionaryComputeCore(
        TKey key,
        Func<TKey, CacheValue<TValue>, CacheMutation<TValue>> transform,
        bool onlyIfPresent = false
    )
    {
        DictionaryTransformNode? parent = EnterDictionaryTransform(key);
        DictionaryTransformNode scope = TransformContext.Value!;
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        try
        {
            while (true)
            {
                DictionaryTransformState state = CaptureDictionaryTransformState(
                    key,
                    out Entry? expectedEntry,
                    out long expectedEpoch,
                    out long expectedRevision,
                    out long expectedVariableRevision,
                    out long expectedMutationSequence,
                    out object expectedMutationEra,
                    out TValue? currentValue,
                    out TimeSpan currentDuration
                );
                if (
                    onlyIfPresent
                    && state is DictionaryTransformState.Missing or DictionaryTransformState.Pending
                )
                {
                    return CacheMutation.Keep<TValue>();
                }

                CacheValue<TValue> current =
                    state == DictionaryTransformState.Present
                        ? CacheValue.Present(currentValue!)
                        : CacheValue.Missing<TValue>();
                CacheMutation<TValue> mutation = transform(key, current);
                if (Volatile.Read(ref scope.SelfMutation) != 0)
                {
                    throw new LoadingCacheReentrancyException(
                        "A dictionary transform mutated its equivalent key before returning."
                    );
                }
                ValidateDictionaryMutation(mutation);

                long weight = 0;
                TimeSpan duration = TimeSpan.MaxValue;
                if (mutation.Kind == CacheMutationKind.Set)
                {
                    TValue nextValue = mutation.Value;
                    weight = ComputeWeight(key, nextValue);
                    duration =
                        state == DictionaryTransformState.Present
                            ? ComputeUpdateDuration(key, nextValue, currentDuration)
                            : ComputeCreateDuration(key, nextValue);
                }

                if (Volatile.Read(ref scope.SelfMutation) != 0)
                {
                    throw new LoadingCacheReentrancyException(
                        "A dictionary transform mutated its equivalent key before publication."
                    );
                }

                if (
                    TryApplyDictionaryMutation(
                        key,
                        expectedEntry,
                        expectedEpoch,
                        expectedRevision,
                        expectedVariableRevision,
                        expectedMutationSequence,
                        expectedMutationEra,
                        state,
                        mutation,
                        weight,
                        duration,
                        out bool retry
                    )
                )
                {
                    return mutation;
                }

                if (!retry)
                {
                    throw new InvalidOperationException(
                        "The dictionary computation was not applied."
                    );
                }
            }
        }
        finally
        {
            Volatile.Write(ref scope.Active, 0);
            TransformContext.Value = parent;
            evictionScope.Dispatch();
        }
    }

    private DictionaryTransformState CaptureDictionaryTransformState(
        TKey key,
        out Entry? expectedEntry,
        out long expectedEpoch,
        out long expectedRevision,
        out long expectedVariableRevision,
        out long expectedMutationSequence,
        out object expectedMutationEra,
        out TValue? currentValue,
        out TimeSpan currentDuration
    )
    {
        expectedEntry = null;
        expectedEpoch = 0;
        expectedRevision = 0;
        expectedVariableRevision = 0;
        expectedMutationSequence = 0;
        expectedMutationEra = null!;
        currentValue = default;
        currentDuration = TimeSpan.MaxValue;

        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            expectedEpoch = _epoch;
            expectedMutationSequence = _dictionaryMutationSequence;
            expectedMutationEra = _dictionaryMutationEra;
            if (!_entries.TryGetValue(key, out Entry? current))
            {
                return DictionaryTransformState.Missing;
            }

            lock (current.Sync)
            {
                if (!Volatile.Read(ref current.IsReady))
                {
                    expectedEntry = current;
                    expectedRevision = current.PublicationRevision;
                    expectedVariableRevision = current.VariableRevision;
                    return DictionaryTransformState.Pending;
                }

                long now = _timeProvider.GetTimestamp();
                if (IsExpired(current, now))
                {
                    if (
                        current.RefreshFlight is not null
                        && IsCurrentRefreshFlightLocked(current, current.RefreshFlight)
                    )
                    {
                        expectedEntry = current;
                        expectedRevision = current.PublicationRevision;
                        expectedVariableRevision = current.VariableRevision;
                        return DictionaryTransformState.Pending;
                    }

                    RemoveCurrentEntryLocked(current, RemovalCause.Expired);
                    expectedMutationSequence = _dictionaryMutationSequence;
                    expectedMutationEra = _dictionaryMutationEra;
                    return DictionaryTransformState.Missing;
                }

                if (!current.TryGetValue(out TValue? liveValue))
                {
                    RemoveCurrentEntryLocked(current, collected: true);
                    expectedMutationSequence = _dictionaryMutationSequence;
                    expectedMutationEra = _dictionaryMutationEra;
                    return DictionaryTransformState.Missing;
                }

                expectedEntry = current;
                expectedRevision = current.PublicationRevision;
                expectedVariableRevision = current.VariableRevision;
                currentValue = liveValue!;
                currentDuration = _expiry is null
                    ? TimeSpan.MaxValue
                    : GetRemainingDuration(current, now, ExpirationKind.Variable);
                return DictionaryTransformState.Present;
            }
        }
    }

    private bool TryApplyDictionaryMutation(
        TKey key,
        Entry? expectedEntry,
        long expectedEpoch,
        long expectedRevision,
        long expectedVariableRevision,
        long expectedMutationSequence,
        object expectedMutationEra,
        DictionaryTransformState expectedState,
        CacheMutation<TValue> mutation,
        long weight,
        TimeSpan duration,
        out bool retry
    )
    {
        retry = false;
        bool scheduleExpiration = false;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_epoch != expectedEpoch)
            {
                retry = true;
                return false;
            }

            Entry? current = null;
            if (expectedEntry is null)
            {
                if (
                    _dictionaryMutationSequence != expectedMutationSequence
                    || !ReferenceEquals(_dictionaryMutationEra, expectedMutationEra)
                    || _entries.TryGetValue(key, out _)
                )
                {
                    retry = true;
                    return false;
                }
            }
            else if (
                !_entries.TryGetValue(key, out current) || !ReferenceEquals(current, expectedEntry)
            )
            {
                retry = true;
                return false;
            }
            bool entryLockTaken = false;
            try
            {
                if (current is not null)
                {
                    Monitor.Enter(current.Sync, ref entryLockTaken);
                    if (
                        current.Epoch != expectedEpoch
                        || current.PublicationRevision != expectedRevision
                        || current.VariableRevision != expectedVariableRevision
                        || (
                            expectedState == DictionaryTransformState.Present
                            && (
                                !Volatile.Read(ref current.IsReady)
                                || IsExpired(current, _timeProvider.GetTimestamp())
                            )
                        )
                    )
                    {
                        retry = true;
                        return false;
                    }

                    _testHooks?.BeforeEntryMutationCommit?.Invoke(current.Sync);
                }

                switch (mutation.Kind)
                {
                    case CacheMutationKind.Keep:
                        return true;
                    case CacheMutationKind.Set:
                        PublishDictionaryEntryLocked(key, mutation.Value, weight, duration);
                        scheduleExpiration = true;
                        break;
                    case CacheMutationKind.Remove:
                        MarkDictionaryTransformMutation(key);
                        RecordBulkMutationLocked(key);
                        if (current is not null)
                        {
                            RemoveCurrentEntryLocked(current);
                            scheduleExpiration = true;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mutation));
                }
            }
            finally
            {
                if (entryLockTaken)
                {
                    Monitor.Exit(current!.Sync);
                }
            }
        }

        if (scheduleExpiration)
        {
            RequestExpirationTimer();
        }

        return true;
    }

    private static void ValidateDictionaryMutation(CacheMutation<TValue> mutation)
    {
        if (mutation.Kind is CacheMutationKind.Set)
        {
            ArgumentNullException.ThrowIfNull(mutation.Value);
        }
        else if (mutation.Kind is not (CacheMutationKind.Keep or CacheMutationKind.Remove))
        {
            throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private DictionaryTransformNode? EnterDictionaryTransform(TKey key)
    {
        for (
            DictionaryTransformNode? node = TransformContext.Value;
            node is not null;
            node = node.Parent
        )
        {
            if (
                Volatile.Read(ref node.Active) != 0
                && ReferenceEquals(node.Owner, this)
                && Comparer.Equals(node.Key, key)
            )
            {
                throw new LoadingCacheReentrancyException(
                    "A dictionary transform attempted to transform an equivalent key in its own logical transform chain."
                );
            }
        }

        DictionaryTransformNode? parent = TransformContext.Value;
        TransformContext.Value = new DictionaryTransformNode(this, key, parent);
        return parent;
    }

    private void MarkDictionaryTransformMutation(TKey key)
    {
        RecordDictionaryMutationLocked();
        MarkDictionaryTransformScopeMutation(key);
    }

    private void MarkDictionaryTransformScopeMutation(TKey key)
    {
        for (
            DictionaryTransformNode? node = TransformContext.Value;
            node is not null;
            node = node.Parent
        )
        {
            if (
                Volatile.Read(ref node.Active) != 0
                && ReferenceEquals(node.Owner, this)
                && Comparer.Equals(node.Key, key)
            )
            {
                Volatile.Write(ref node.SelfMutation, 1);
            }
        }
    }

    private void RecordDictionaryMutationLocked()
    {
        if (_dictionaryMutationSequence == long.MaxValue)
        {
            _dictionaryMutationEra = new object();
            _dictionaryMutationSequence = 0;
            return;
        }

        _dictionaryMutationSequence++;
    }

    internal void SetDictionaryMutationSequenceForTesting(long sequence)
    {
        lock (_gate)
        {
            _dictionaryMutationSequence = sequence;
        }
    }

    private void MarkAllDictionaryTransformsMutated()
    {
        for (
            DictionaryTransformNode? node = TransformContext.Value;
            node is not null;
            node = node.Parent
        )
        {
            if (Volatile.Read(ref node.Active) != 0 && ReferenceEquals(node.Owner, this))
            {
                Volatile.Write(ref node.SelfMutation, 1);
            }
        }
    }

    private sealed class DictionaryTransformNode(
        CacheEngine<TKey, TValue> owner,
        TKey key,
        DictionaryTransformNode? parent
    )
    {
        internal CacheEngine<TKey, TValue> Owner { get; } = owner;

        internal TKey Key { get; } = key;

        internal DictionaryTransformNode? Parent { get; } = parent;

        internal int Active = 1;

        internal int SelfMutation;
    }
}
