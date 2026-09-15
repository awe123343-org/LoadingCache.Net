using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using LoadingCache.Diagnostics;
using LoadingCache.ReferenceStorage;

namespace LoadingCache;

/// <summary>
/// Bulk population support for the shared engine.  A bulk operation owns one
/// execution flight and exposes one completion per key to normal cache
/// callers.  This keeps the backend call and execution permit counts separate
/// from the number of keys represented by the operation.
/// </summary>
internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly ConditionalWeakTable<AsyncFlight, BulkAsyncGroup> _bulkAsyncGroups = new();
    private readonly ConditionalWeakTable<SyncFlight, BulkSyncGroup> _bulkSyncGroups = new();
    private int _bulkPendingKeyCount;
    private int _bulkGroupCount;
    private int _bulkPendingKeyLimit;
    private int _bulkInputKeyLimit;
    private int? _configuredBulkInputKeyLimit;
    private Dictionary<TKey, long>? _strongBulkMutationVersions;
    private Dictionary<object, long>? _weakBulkMutationVersions;
    private long _bulkMutationSequence;
    private object _bulkMutationLedgerToken = new();
    private const int FallbackBulkInputKeyLimit = 1024;
    private const int MaximumBulkMutationKeys = FallbackBulkInputKeyLimit;

    private int BulkPendingKeyLimit =>
        Volatile.Read(ref _bulkPendingKeyLimit) > 0
            ? Volatile.Read(ref _bulkPendingKeyLimit)
            : _maxConcurrentLoads;

    private int BulkInputKeyLimit =>
        Volatile.Read(ref _bulkInputKeyLimit) > 0
            ? Volatile.Read(ref _bulkInputKeyLimit)
            : BulkPendingKeyLimit;

    private int BulkFallbackInputLimit => _configuredBulkInputKeyLimit ?? FallbackBulkInputKeyLimit;

    private int PendingLoadKeyCountLocked() =>
        _activeFlights.Count - _bulkGroupCount + _bulkPendingKeyCount;

    private bool CanReserveFlightsLocked(int pendingKeyCount)
    {
        if (pendingKeyCount <= 0 || pendingKeyCount > BulkPendingKeyLimit)
        {
            return false;
        }

        return _reservedLoads < _maxConcurrentLoads
            && PendingLoadKeyCountLocked() <= BulkPendingKeyLimit - pendingKeyCount;
    }

    /// <summary>Applies the explicit bulk resource limits selected by the builder.</summary>
    private void ConfigureBulkLimits(int? maxPendingLoadKeys, int? maximumBulkKeys)
    {
        int pending = maxPendingLoadKeys ?? _maxConcurrentLoads;
        int input = maximumBulkKeys ?? pending;
        if (maxPendingLoadKeys is null && maximumBulkKeys.HasValue)
        {
            pending = Math.Max(pending, input);
        }

        if (
            pending <= 0
            || input <= 0
            || (maxPendingLoadKeys.HasValue && maximumBulkKeys.HasValue && input > pending)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingLoadKeys));
        }

        Volatile.Write(ref _bulkPendingKeyLimit, pending);
        Volatile.Write(ref _bulkInputKeyLimit, input);
        _configuredBulkInputKeyLimit = maximumBulkKeys;
    }

    /// <summary>
    /// Starts bounded mutation tracking for a new active bulk group. The
    /// caller holds the engine gate.
    /// </summary>
    private void BeginBulkMutationTrackingLocked()
    {
        if (_bulkGroupCount != 0)
        {
            return;
        }

        _strongBulkMutationVersions?.Clear();
        _weakBulkMutationVersions?.Clear();
        _bulkMutationSequence = 0;
        _bulkMutationLedgerToken = new object();
    }

    /// <summary>
    /// Rotates the bounded mutation ledger after it can no longer represent
    /// every distinct key. Groups attached to the previous token fail closed;
    /// newer groups can continue recording mutations without waiting for an
    /// old non-cooperative loader to retire.
    /// </summary>
    private void RotateBulkMutationLedgerLocked()
    {
        _strongBulkMutationVersions?.Clear();
        _weakBulkMutationVersions?.Clear();
        _bulkMutationSequence = 0;
        _bulkMutationLedgerToken = new object();
    }

    /// <summary>
    /// Sets the mutation sequence for an adversarial overflow test. The test
    /// only uses this seam while a bulk group is active; production paths never
    /// need to manufacture a sequence value.
    /// </summary>
    internal void SetBulkMutationSequenceForTesting(long sequence)
    {
        lock (_gate)
        {
            _bulkMutationSequence = sequence;
        }
    }

    /// <summary>
    /// Records an explicit mutation for exact-key prefetch fencing. The caller
    /// holds the engine gate. Tracking exists only while a bulk group is active
    /// and is bounded so mutation history cannot retain an unbounded key set.
    /// </summary>
    private void RecordBulkMutationLocked(TKey key)
    {
        if (_bulkGroupCount == 0)
        {
            return;
        }

        if (_bulkMutationSequence == long.MaxValue)
        {
            RotateBulkMutationLedgerLocked();
            return;
        }

        long sequence = ++_bulkMutationSequence;
        if (_weakKeys)
        {
            _weakBulkMutationVersions ??= new Dictionary<object, long>(
                WeakKeyObjectComparer<TKey>.Instance
            );
            if (_weakBulkMutationVersions.ContainsKey(key))
            {
                _weakBulkMutationVersions[ReferenceKey<TKey>.CreateWeak(key)] = sequence;
                return;
            }

            if (_weakBulkMutationVersions.Count >= MaximumBulkMutationKeys)
            {
                RotateBulkMutationLedgerLocked();
                return;
            }

            _weakBulkMutationVersions.Add(ReferenceKey<TKey>.CreateWeak(key), sequence);
            return;
        }

        _strongBulkMutationVersions ??= new Dictionary<TKey, long>(Comparer);
        if (_strongBulkMutationVersions.ContainsKey(key))
        {
            _strongBulkMutationVersions[key] = sequence;
            return;
        }

        if (_strongBulkMutationVersions.Count >= MaximumBulkMutationKeys)
        {
            RotateBulkMutationLedgerLocked();
            return;
        }

        _strongBulkMutationVersions.Add(key, sequence);
    }

    private bool IsBulkPrefetchFencedLocked(BulkGroup group, TKey key)
    {
        if (!ReferenceEquals(group.MutationLedgerToken, _bulkMutationLedgerToken))
        {
            return true;
        }

        if (_weakKeys)
        {
            return _weakBulkMutationVersions is not null
                && _weakBulkMutationVersions.TryGetValue(key, out long weakSequence)
                && weakSequence > group.MutationSequence;
        }

        return _strongBulkMutationVersions is not null
            && _strongBulkMutationVersions.TryGetValue(key, out long strongSequence)
            && strongSequence > group.MutationSequence;
    }

    internal IReadOnlyDictionary<TKey, TValue> GetAll(
        IEnumerable<TKey> keys,
        Func<TKey, TValue> singleLoader,
        Func<TKey, TValue, TValue>? reloadFactory,
        Func<IReadOnlyCollection<TKey>, IReadOnlyDictionary<TKey, TValue>>? bulkLoader
    )
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(singleLoader);
        ThrowIfDisposed();
        TKey[] requested = SnapshotBulkKeys(
            keys,
            bulkLoader is null ? BulkFallbackInputLimit : BulkInputKeyLimit
        );
        EnsureBulkReentrancy(requested);

        if (bulkLoader is null)
        {
            var fallback = new Dictionary<TKey, TValue>(Comparer);
            foreach (TKey key in requested)
            {
                fallback[key] = GetOrAdd(key, singleLoader, reloadFactory);
            }

            return fallback;
        }

        BulkPlan plan = InstallSyncBulk(requested, bulkLoader);
        ApplyBulkReadyReadsSafely(plan, reloadFactory);
        StartPendingSyncFlights(plan);

        var result = new Dictionary<TKey, TValue>(Comparer);
        foreach (TKey key in requested)
        {
            if (plan.ReadyValues.TryGetValue(key, out TValue? ready))
            {
                result[key] = ready;
                continue;
            }

            if (!plan.PendingFlights.TryGetValue(key, out Flight? pending))
            {
                throw new InvalidOperationException("The bulk operation lost a key flight.");
            }

            result[key] = WaitForBulkSync((SyncFlight)pending, key);
        }

        return result;
    }

    internal IReadOnlyDictionary<TKey, TValue> GetAllPresent(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfDisposed();
        TKey[] requested = SnapshotBulkKeys(keys, BulkFallbackInputLimit);
        var result = new Dictionary<TKey, TValue>(Comparer);
        foreach (TKey key in requested)
        {
            if (TryGet(key, out TValue? value))
            {
                result[key] = value!;
            }
        }

        return result;
    }

    internal void PutAll(IEnumerable<KeyValuePair<TKey, TValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfDisposed();
        var snapshot = new List<KeyValuePair<TKey, TValue>>();
        int examined = 0;
        foreach (KeyValuePair<TKey, TValue> pair in values)
        {
            if (++examined > BulkFallbackInputLimit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(values),
                    $"A bulk operation cannot examine more than {BulkFallbackInputLimit} input values."
                );
            }

            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            snapshot.Add(pair);
        }

        foreach (KeyValuePair<TKey, TValue> pair in snapshot)
        {
            Put(pair.Key, pair.Value);
        }
    }

    internal async ValueTask<IReadOnlyDictionary<TKey, TValue>> GetAllAsync(
        Func<TKey, CancellationToken, Task<TValue>> singleLoader,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reloadFactory,
        Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        >? bulkLoader,
        IEnumerable<TKey> keys,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(singleLoader);
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        TKey[] requested = SnapshotBulkKeys(
            keys,
            bulkLoader is null ? BulkFallbackInputLimit : BulkInputKeyLimit
        );
        EnsureBulkReentrancy(requested);

        if (bulkLoader is null)
        {
            var fallback = new Dictionary<TKey, TValue>(Comparer);
            foreach (TKey key in requested)
            {
                fallback[key] = await GetAsync(key, singleLoader, reloadFactory, cancellationToken)
                    .ConfigureAwait(false);
            }

            return fallback;
        }

        BulkPlan plan = InstallAsyncBulk(requested, bulkLoader);
        ApplyBulkReadyReadsSafely(plan, reloadFactory);
        StartPendingAsyncFlights(plan);

        Task<TValue>[] waits = new Task<TValue>[requested.Length];
        for (int index = 0; index < requested.Length; index++)
        {
            TKey key = requested[index];
            if (plan.ReadyValues.TryGetValue(key, out TValue? ready))
            {
                waits[index] = Task.FromResult(ready);
                continue;
            }

            if (!plan.PendingFlights.TryGetValue(key, out Flight? pending))
            {
                throw new InvalidOperationException("The bulk operation lost a key flight.");
            }

            waits[index] = GetBulkTask((AsyncFlight)pending, key);
        }

        Task<TValue>[] callerWaits = new Task<TValue>[waits.Length];
        for (int index = 0; index < waits.Length; index++)
        {
            callerWaits[index] = cancellationToken.CanBeCanceled
                ? waits[index].WaitAsync(cancellationToken)
                : waits[index];
        }

        TValue[] values = await Task.WhenAll(callerWaits).ConfigureAwait(false);
        var result = new Dictionary<TKey, TValue>(Comparer);
        for (int index = 0; index < requested.Length; index++)
        {
            result[requested[index]] = values[index];
        }

        return result;
    }

    private TKey[] SnapshotBulkKeys(IEnumerable<TKey> keys, int limit)
    {
        var seen = new HashSet<TKey>(Comparer);
        var snapshot = new List<TKey>();
        int examined = 0;
        foreach (TKey key in keys)
        {
            if (++examined > limit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(keys),
                    $"A bulk operation cannot examine more than {limit} input keys."
                );
            }

            ArgumentNullException.ThrowIfNull(key);
            if (!seen.Add(key))
            {
                continue;
            }

            snapshot.Add(key);
        }

        return [.. snapshot];
    }

    private BulkPlan InstallSyncBulk(
        TKey[] requested,
        Func<IReadOnlyCollection<TKey>, IReadOnlyDictionary<TKey, TValue>> bulkLoader
    )
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        var plan = new BulkPlan(Comparer);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CollectBulkStateLocked(requested, plan);
            if (plan.OwnedKeys.Count == 0)
            {
                return plan;
            }

            EnsureBulkCapacityLocked(plan.OwnedKeys.Count);
            BeginBulkMutationTrackingLocked();
            BulkSyncGroup group = new(
                [.. plan.OwnedKeys],
                _epoch,
                _bulkMutationSequence,
                _bulkMutationLedgerToken,
                bulkLoader,
                Comparer
            );
            SyncFlight owner = new(
                plan.OwnedKeys[0],
                _epoch,
                ++_nextGeneration,
                key => ExecuteSyncBulk(group, key)
            );
            group.Owner = owner;
            _bulkSyncGroups.Add(owner, group);

            foreach (TKey key in group.OwnedKeys)
            {
                Entry entry = Entry.Loading(key, _epoch, owner.Generation, owner, _weakKeys);
                entry.SharedTask = null;
                _entries[key] = entry;
                plan.PendingFlights[key] = owner;
            }

            _activeFlights.Add(owner);
            _reservedLoads++;
            _bulkPendingKeyCount += group.OwnedKeys.Length;
            _bulkGroupCount++;
        }

        return plan;
    }

    private BulkPlan InstallAsyncBulk(
        TKey[] requested,
        Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        > bulkLoader
    )
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        var plan = new BulkPlan(Comparer);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CollectBulkStateLocked(requested, plan);
            if (plan.OwnedKeys.Count == 0)
            {
                return plan;
            }

            EnsureBulkCapacityLocked(plan.OwnedKeys.Count);
            BeginBulkMutationTrackingLocked();
            BulkAsyncGroup group = new(
                [.. plan.OwnedKeys],
                _epoch,
                _bulkMutationSequence,
                _bulkMutationLedgerToken,
                bulkLoader,
                Comparer
            );
            AsyncFlight owner = new(
                plan.OwnedKeys[0],
                _epoch,
                ++_nextGeneration,
                (_, cancellationToken) => ExecuteAsyncBulk(group, cancellationToken)
            );
            group.Owner = owner;
            _bulkAsyncGroups.Add(owner, group);

            foreach (TKey key in group.OwnedKeys)
            {
                _entries[key] = Entry.Loading(key, _epoch, owner.Generation, owner, _weakKeys);
                plan.PendingFlights[key] = owner;
            }

            _activeFlights.Add(owner);
            _reservedLoads++;
            _bulkPendingKeyCount += group.OwnedKeys.Length;
            _bulkGroupCount++;
        }

        return plan;
    }

    private void CollectBulkStateLocked(TKey[] requested, BulkPlan plan)
    {
        foreach (TKey key in requested)
        {
            if (_entries.TryGetValue(key, out Entry? current))
            {
                if (Volatile.Read(ref current.IsReady))
                {
                    RemovalCause staleCause;
                    lock (current.Sync)
                    {
                        long now = _timeProvider.GetTimestamp();
                        if (
                            Volatile.Read(ref current.IsReady)
                            && !IsExpired(current, now)
                            && current.TryGetValue(out TValue? value)
                        )
                        {
                            TouchWithoutLock(current, now);
                            plan.ReadyValues[key] = value;
                            plan.ReadyReads[key] = new BulkReadyRead(
                                key,
                                current,
                                value,
                                current.VariableTimestamp,
                                current.VariableRevision,
                                _expiry is null
                                    ? TimeSpan.MaxValue
                                    : GetRemainingDuration(current, now, ExpirationKind.Variable),
                                current.PolicyToken,
                                IsRefreshEligibleLocked(current, now)
                            );
                            RecordHit();
                            continue;
                        }

                        bool collected =
                            (_weakKeys && !current.TryGetKey(out _))
                            || (_weakValues && !current.TryGetValue(out _));
                        staleCause = collected ? RemovalCause.Collected : RemovalCause.Expired;

                        if (
                            current.RefreshFlight is not null
                            && IsCurrentRefreshFlightLocked(current, current.RefreshFlight)
                        )
                        {
                            AddExistingFlight(plan, key, current.RefreshFlight);
                            RecordMiss();
                            RecordCoalescedWaiter();
                            continue;
                        }
                    }

                    RemoveCurrentEntryLocked(current, staleCause);
                }
                else if (current.Flight is { } flight)
                {
                    AddExistingFlight(plan, key, flight);
                    RecordMiss();
                    RecordCoalescedWaiter();
                    continue;
                }
            }

            plan.OwnedKeys.Add(key);
            plan.PendingFlights[key] = null!;
            RecordMiss();
        }
    }

    private void ApplyBulkReadyReadsSafely(BulkPlan plan, Func<TKey, TValue, TValue>? reloadFactory)
    {
        try
        {
            ApplyBulkReadyReads(plan, reloadFactory);
        }
        catch
        {
            StartPendingSyncFlights(plan);
            StartPendingAsyncFlights(plan);
            throw;
        }
    }

    private void ApplyBulkReadyReadsSafely(
        BulkPlan plan,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reloadFactory
    )
    {
        try
        {
            ApplyBulkReadyReads(plan, reloadFactory);
        }
        catch
        {
            StartPendingSyncFlights(plan);
            StartPendingAsyncFlights(plan);
            throw;
        }
    }

    private void ApplyBulkReadyReads(BulkPlan plan, Func<TKey, TValue, TValue>? reloadFactory)
    {
        foreach (BulkReadyRead read in plan.ReadyReads.Values)
        {
            ApplyReadExpiryUpdate(
                read.Entry,
                read.Key,
                read.Value,
                read.VariableTimestamp,
                read.VariableRevision,
                read.VariableDuration
            );
            _policy.OnAccess(read.PolicyToken);
            if (read.RefreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(read.Entry, reloadFactory);
            }
        }
    }

    private void ApplyBulkReadyReads(
        BulkPlan plan,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reloadFactory
    )
    {
        foreach (BulkReadyRead read in plan.ReadyReads.Values)
        {
            ApplyReadExpiryUpdate(
                read.Entry,
                read.Key,
                read.Value,
                read.VariableTimestamp,
                read.VariableRevision,
                read.VariableDuration
            );
            _policy.OnAccess(read.PolicyToken);
            if (read.RefreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(read.Entry, reloadFactory);
            }
        }
    }

    private void StartPendingSyncFlights(BulkPlan plan)
    {
        HashSet<SyncFlight> started = [];
        foreach (Flight pending in plan.PendingFlights.Values)
        {
            if (pending is SyncFlight syncFlight && started.Add(syncFlight))
            {
                StartSyncFlight(syncFlight);
            }
        }
    }

    private void StartPendingAsyncFlights(BulkPlan plan)
    {
        HashSet<AsyncFlight> started = [];
        foreach (Flight pending in plan.PendingFlights.Values)
        {
            if (pending is AsyncFlight asyncFlight && started.Add(asyncFlight))
            {
                StartAsyncFlight(asyncFlight);
            }
        }
    }

    private void EnsureBulkReentrancy(IEnumerable<TKey> requested)
    {
        foreach (TKey key in requested)
        {
            if (LoadChainContext.Contains(this, key))
            {
                throw new LoadingCacheReentrancyException(
                    "A bulk loading delegate attempted to await an equivalent key in its own logical load chain."
                );
            }
        }
    }

    private static void AddExistingFlight(BulkPlan plan, TKey key, Flight flight)
    {
        plan.PendingFlights[key] = flight;
    }

    private void EnsureBulkCapacityLocked(int ownedKeyCount)
    {
        if (!CanReserveFlightsLocked(ownedKeyCount))
        {
            throw new CacheLoadRejectedException();
        }
    }

    private TValue ExecuteSyncBulk(BulkSyncGroup group, TKey _)
    {
        int enteredKeys = 0;
        try
        {
            enteredKeys = EnterBulkLoadChain(group.OwnedKeys);
            RecordCounter(CacheCounterKind.BulkLoads);
            IReadOnlyDictionary<TKey, TValue> values =
                group.Loader(group.LoaderKeys)
                ?? throw new InvalidOperationException("The bulk loading delegate returned null.");
            group.Prepared = PrepareBulkResult(group.OwnedKeys, values);
            return group.Prepared.Values[group.OwnedKeys[0]];
        }
        finally
        {
            ExitBulkLoadChain(enteredKeys);
        }
    }

    private async Task<TValue> ExecuteAsyncBulk(
        BulkAsyncGroup group,
        CancellationToken cancellationToken
    )
    {
        int enteredKeys = 0;
        try
        {
            enteredKeys = EnterBulkLoadChain(group.OwnedKeys);
            RecordCounter(CacheCounterKind.BulkLoads);
            Task<IReadOnlyDictionary<TKey, TValue>> operation =
                group.Loader(group.LoaderKeys, cancellationToken)
                ?? throw new InvalidOperationException("The bulk loading delegate returned null.");
            IReadOnlyDictionary<TKey, TValue> values = await operation.ConfigureAwait(false);
            group.Prepared = PrepareBulkResult(group.OwnedKeys, values);
            return group.Prepared.Values[group.OwnedKeys[0]];
        }
        finally
        {
            ExitBulkLoadChain(enteredKeys);
        }
    }

    private int EnterBulkLoadChain(TKey[] keys)
    {
        int entered = 0;
        try
        {
            for (int index = 1; index < keys.Length; index++)
            {
                EnterLoadChain(keys[index]);
                entered++;
            }

            return entered;
        }
        catch
        {
            ExitBulkLoadChain(entered);
            throw;
        }
    }

    private static void ExitBulkLoadChain(int count)
    {
        for (int index = 0; index < count; index++)
        {
            ExitLoadChain();
        }
    }

    private BulkPrepared PrepareBulkResult(
        TKey[] ownedKeys,
        IReadOnlyDictionary<TKey, TValue> values
    )
    {
        var snapshot = new Dictionary<TKey, TValue>(Comparer);
        int limit = BulkInputKeyLimit;
        int count = 0;
        foreach (KeyValuePair<TKey, TValue> pair in values)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            if (count++ == limit)
            {
                throw new InvalidOperationException(
                    $"A bulk loader returned more than {limit} distinct keys."
                );
            }

            if (!snapshot.TryAdd(pair.Key, pair.Value))
            {
                throw new InvalidOperationException(
                    "A bulk loader returned duplicate comparer-equivalent keys."
                );
            }
        }

        foreach (TKey key in ownedKeys)
        {
            if (!snapshot.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    "A bulk loader did not return every requested cold key."
                );
            }
        }

        var prepared = new Dictionary<TKey, BulkPublication>(Comparer);
        foreach (KeyValuePair<TKey, TValue> pair in snapshot)
        {
            long weight = ComputeWeight(pair.Key, pair.Value);
            TimeSpan duration = ComputeCreateDuration(pair.Key, pair.Value);
            prepared.Add(pair.Key, new BulkPublication(pair.Key, pair.Value, weight, duration));
        }

        return new BulkPrepared(snapshot, prepared);
    }

    /// <summary>
    /// Handles the final publication for a bulk owner.  It is called by the
    /// ordinary completion path before that path computes a single-key weight,
    /// so each bulk result invokes user callbacks once.
    /// </summary>
    private bool TryCompleteBulkSuccess(Flight flight, TValue leaderValue)
    {
        if (
            flight is AsyncFlight asyncFlight
            && _bulkAsyncGroups.TryGetValue(asyncFlight, out BulkAsyncGroup? asyncGroup)
        )
        {
            return CompleteBulkSuccess(asyncGroup, leaderValue);
        }

        if (
            flight is SyncFlight syncFlight
            && _bulkSyncGroups.TryGetValue(syncFlight, out BulkSyncGroup? syncGroup)
        )
        {
            return CompleteBulkSuccess(syncGroup, leaderValue);
        }

        return false;
    }

    private bool CompleteBulkSuccess(BulkSyncGroup group, TValue leaderValue) =>
        CompleteBulkSuccessCore(group, leaderValue);

    private bool CompleteBulkSuccess(BulkAsyncGroup group, TValue leaderValue) =>
        CompleteBulkSuccessCore(group, leaderValue);

    private bool CompleteBulkSuccessCore(BulkGroup group, TValue leaderValue)
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed = Interlocked.CompareExchange(ref group.Owner.TerminalClaimed, 1, 0) == 0;
        if (!claimed)
        {
            RetireFlight(group.Owner, underlyingCompleted: true);
            return true;
        }

        try
        {
            lock (_gate)
            {
                bool canPublish =
                    _disposed == 0
                    && group.Epoch == _epoch
                    && Volatile.Read(ref group.Owner.PublishRevoked) == 0;
                if (canPublish && group.Prepared is not null)
                {
                    foreach (BulkPublication publication in group.Prepared.Publications.Values)
                    {
                        if (
                            group.IsOwnedKey(publication.Key)
                            && _entries.TryGetValue(publication.Key, out Entry? ownedEntry)
                            && !Volatile.Read(ref ownedEntry.IsReady)
                            && ReferenceEquals(ownedEntry.Flight, group.Owner)
                            && ownedEntry.Generation == group.Owner.Generation
                        )
                        {
                            PublishBulkEntryLocked(ownedEntry, publication);
                        }
                        else if (
                            !group.IsOwnedKey(publication.Key)
                            && !IsBulkPrefetchFencedLocked(group, publication.Key)
                            && !_entries.TryGetValue(publication.Key, out _)
                        )
                        {
                            PublishBulkPrefetchLocked(publication);
                        }
                    }
                }

                RecordFlightResult(group.Owner, CacheCounterKind.LoadSuccesses);
            }

            RequestExpirationTimer();
            evictionScope.Dispatch();
            CompleteBulkResults(group, leaderValue);
            CompletePromise(
                group.Owner,
                static (owner, value) => owner.Completion.TrySetResult(value),
                leaderValue
            );
            return true;
        }
        catch (Exception exception)
        {
            evictionScope.Dispatch();
            RecordFlightResult(group.Owner, CacheCounterKind.LoadFailures);
            FailBulkGroup(group, exception);
            CompletePromise(
                group.Owner,
                static (owner, error) => owner.TrySetException(error),
                exception
            );
            return true;
        }
    }

    private void PublishBulkEntryLocked(Entry entry, BulkPublication publication)
    {
        long timestamp = _timeProvider.GetTimestamp();
        lock (entry.Sync)
        {
            entry.SetValue(publication.Value, _weakValues);
            entry.Weight = publication.Weight;
            entry.WriteTimestamp = timestamp;
            entry.AccessTimestamp = timestamp;
            entry.VariableTimestamp = timestamp;
            entry.VariableDuration = publication.Duration;
            entry.VariableRevision++;
            entry.PublicationRevision++;
            // Capture ownership before clearing Flight or invoking any later
            // publication step that can throw, including BeforeReadyPublish.
            publication.PublishedEntry = entry;
            publication.PublishedRevision = entry.PublicationRevision;
            entry.Flight = null;
            entry.SharedTask = _weakValues ? null : Task.FromResult(publication.Value);
            entry.PolicyToken = new WindowTinyLfuEnginePolicy.EngineEntryToken(
                entry,
                GetPolicyHash(publication.Key)
            );
            InvokeHook(_testHooks?.BeforeReadyPublish);
            Volatile.Write(ref entry.IsReady, true);
        }

        PublishPolicyWriteLocked(entry.PolicyToken, entry.Weight);
        if (_expirationWheel is null)
        {
            return;
        }

        ulong normalizedNow = GetExpirationNowLocked();
        AdvanceExpirationLocked(normalizedNow);
        ScheduleExpirationNodeLocked(entry, normalizedNow);
    }

    private void PublishBulkPrefetchLocked(BulkPublication publication)
    {
        Entry entry = Entry.Ready(
            publication.Key,
            _epoch,
            ++_nextGeneration,
            publication.Value,
            _timeProvider.GetTimestamp(),
            publication.Weight,
            publication.Duration,
            _weakKeys,
            _weakValues
        );
        entry.PolicyToken = new WindowTinyLfuEnginePolicy.EngineEntryToken(
            entry,
            GetPolicyHash(publication.Key)
        );
        _entries[publication.Key] = entry;
        PublishPolicyWriteLocked(entry.PolicyToken, entry.Weight);
        if (_expirationWheel is null)
        {
            return;
        }

        ulong normalizedNow = GetExpirationNowLocked();
        AdvanceExpirationLocked(normalizedNow);
        ScheduleExpirationNodeLocked(entry, normalizedNow);
    }

    private static void CompleteBulkResults(BulkGroup group, TValue leaderValue)
    {
        // Resolve comparer-dependent lookups before taking the completion gate.
        // The gate protects only bounded promise completion, never user callbacks,
        // mapping cleanup, timer disposal, or reservation retirement.
        KeyValuePair<TaskCompletionSource<TValue>, TValue>[] results = [];
        if (group is BulkAsyncGroup asyncGroup)
        {
            BulkPrepared prepared =
                asyncGroup.Prepared
                ?? throw new InvalidOperationException(
                    "The bulk loading flight completed without a prepared result."
                );
            results = new KeyValuePair<TaskCompletionSource<TValue>, TValue>[
                asyncGroup.Promises.Count
            ];
            int index = 0;
            foreach (KeyValuePair<TKey, TaskCompletionSource<TValue>> pair in asyncGroup.Promises)
            {
                results[index++] = new KeyValuePair<TaskCompletionSource<TValue>, TValue>(
                    pair.Value,
                    prepared.Values[pair.Key]
                );
            }
        }

        lock (group.CompletionGate)
        {
            if (group.PromisesCompleted)
            {
                return;
            }

            foreach (KeyValuePair<TaskCompletionSource<TValue>, TValue> result in results)
            {
                result.Key.TrySetResult(result.Value);
            }

            switch (group.Owner)
            {
                case AsyncFlight owner:
                    owner.Completion.TrySetResult(leaderValue);
                    break;
                case SyncFlight owner:
                    owner.Set(leaderValue);
                    break;
            }

            group.PromisesCompleted = true;
        }
    }

    private void FailBulkGroup(BulkGroup group, Exception exception)
    {
        lock (group.CompletionGate)
        {
            if (group.PromisesCompleted)
            {
                return;
            }
        }

        // Never nest the group completion gate with the engine gate. The backend
        // terminal claim still excludes competing backend outcomes; disposal may
        // independently complete the group while this exact-owner cleanup runs.
        lock (_gate)
        {
            foreach (TKey key in group.OwnedKeys)
            {
                if (
                    _entries.TryGetValue(key, out Entry? entry)
                    && entry.Epoch == group.Epoch
                    && entry.Generation == group.Owner.Generation
                    && (
                        ReferenceEquals(entry.Flight, group.Owner)
                        || (
                            group.Prepared is { } prepared
                            && prepared.Publications.TryGetValue(
                                key,
                                out BulkPublication? publication
                            )
                            && ReferenceEquals(publication.PublishedEntry, entry)
                            && publication.PublishedRevision == entry.PublicationRevision
                        )
                    )
                )
                {
                    RemoveCurrentEntryLocked(entry);
                }
            }
        }

        CompleteBulkFailure(group, exception);
    }

    private static void CompleteBulkFailure(BulkGroup group, Exception exception)
    {
        lock (group.CompletionGate)
        {
            if (group.PromisesCompleted)
            {
                return;
            }

            if (group is BulkAsyncGroup asyncGroup)
            {
                foreach (TaskCompletionSource<TValue> promise in asyncGroup.Promises.Values)
                {
                    if (exception is OperationCanceledException canceled)
                    {
                        promise.TrySetCanceled(GetCancellationToken(canceled));
                    }
                    else
                    {
                        promise.TrySetException(exception);
                        _ = promise.Task.Exception;
                    }
                }
            }

            switch (group.Owner)
            {
                case AsyncFlight owner when exception is OperationCanceledException canceled:
                    owner.Completion.TrySetCanceled(GetCancellationToken(canceled));
                    break;
                case AsyncFlight owner:
                    owner.TrySetException(exception);
                    break;
                case SyncFlight owner:
                    owner.Set(exception);
                    break;
            }

            group.PromisesCompleted = true;
        }
    }

    private Task<TValue> GetBulkTask(AsyncFlight flight, TKey key)
    {
        if (
            _bulkAsyncGroups.TryGetValue(flight, out BulkAsyncGroup? group)
            && group.Promises.TryGetValue(key, out TaskCompletionSource<TValue>? promise)
        )
        {
            return promise.Task;
        }

        return flight.Completion.Task;
    }

    private ValueTask<TValue> WaitForFlight(
        AsyncFlight flight,
        TKey key,
        CancellationToken cancellationToken
    )
    {
        Task<TValue> task = GetBulkTask(flight, key);
        return cancellationToken.CanBeCanceled
            ? new ValueTask<TValue>(task.WaitAsync(cancellationToken))
            : new ValueTask<TValue>(task);
    }

    private TValue WaitForBulkSync(SyncFlight flight, TKey key)
    {
        TValue leaderValue = flight.Wait();
        if (
            _bulkSyncGroups.TryGetValue(flight, out BulkSyncGroup? group)
            && group.Prepared is not null
            && group.Prepared.Values.TryGetValue(key, out TValue? value)
        )
        {
            return value;
        }

        return leaderValue;
    }

    private void CompleteBulkTimeout(Flight flight, Exception exception)
    {
        if (
            flight is AsyncFlight asyncFlight
            && _bulkAsyncGroups.TryGetValue(asyncFlight, out BulkAsyncGroup? asyncGroup)
        )
        {
            FailBulkGroup(asyncGroup, exception);
        }
        else if (
            flight is SyncFlight syncFlight
            && _bulkSyncGroups.TryGetValue(syncFlight, out BulkSyncGroup? syncGroup)
        )
        {
            FailBulkGroup(syncGroup, exception);
        }
    }

    private void FailBulkFlight(Flight flight, Exception exception)
    {
        if (
            flight is AsyncFlight asyncFlight
            && _bulkAsyncGroups.TryGetValue(asyncFlight, out BulkAsyncGroup? asyncGroup)
        )
        {
            FailBulkGroup(asyncGroup, exception);
        }
        else if (
            flight is SyncFlight syncFlight
            && _bulkSyncGroups.TryGetValue(syncFlight, out BulkSyncGroup? syncGroup)
        )
        {
            FailBulkGroup(syncGroup, exception);
        }
    }

    private void ReleaseBulkKeyReservation(BulkGroup group)
    {
        if (Interlocked.Exchange(ref group.KeyReservationReleased, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _bulkPendingKeyCount -= group.OwnedKeys.Length;
            _bulkGroupCount--;
            if (_bulkGroupCount == 0)
            {
                _strongBulkMutationVersions?.Clear();
                _weakBulkMutationVersions?.Clear();
                _bulkMutationSequence = 0;
                _bulkMutationLedgerToken = new object();
            }
        }
    }

    private void RetireBulkFlight(Flight flight)
    {
        if (
            flight is AsyncFlight asyncFlight
            && _bulkAsyncGroups.TryGetValue(asyncFlight, out BulkAsyncGroup? asyncGroup)
        )
        {
            ReleaseBulkKeyReservation(asyncGroup);
        }
        else if (
            flight is SyncFlight syncFlight
            && _bulkSyncGroups.TryGetValue(syncFlight, out BulkSyncGroup? syncGroup)
        )
        {
            ReleaseBulkKeyReservation(syncGroup);
        }
    }

    private bool CompleteBulkDisposed(Flight flight)
    {
        BulkGroup? group = flight switch
        {
            AsyncFlight asyncFlight
                when _bulkAsyncGroups.TryGetValue(asyncFlight, out BulkAsyncGroup? value) => value,
            SyncFlight syncFlight
                when _bulkSyncGroups.TryGetValue(syncFlight, out BulkSyncGroup? value) => value,
            _ => null,
        };

        if (group is null)
        {
            return false;
        }

        // Backend arbitration does not mean its promises are already terminal:
        // a successful owner can still be inside a slow eviction callback.
        // Disposal terminates any unfinished group without waiting for that code.
        Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0);
        Volatile.Write(ref flight.PublishRevoked, 1);
        CompleteBulkFailure(group, new ObjectDisposedException(nameof(LoadingCache)));
        return true;
    }

    private sealed class BulkPlan
    {
        internal BulkPlan(IEqualityComparer<TKey> comparer)
        {
            ReadyValues = new Dictionary<TKey, TValue>(comparer);
            ReadyReads = new Dictionary<TKey, BulkReadyRead>(comparer);
            PendingFlights = new Dictionary<TKey, Flight>(comparer);
        }

        internal Dictionary<TKey, TValue> ReadyValues { get; }
        internal Dictionary<TKey, BulkReadyRead> ReadyReads { get; }
        internal Dictionary<TKey, Flight> PendingFlights { get; }
        internal readonly List<TKey> OwnedKeys = [];
    }

    private sealed class BulkReadyRead(
        TKey key,
        Entry entry,
        TValue value,
        long variableTimestamp,
        long variableRevision,
        TimeSpan variableDuration,
        object? policyToken,
        bool refreshEligible
    )
    {
        internal TKey Key { get; } = key;
        internal Entry Entry { get; } = entry;
        internal TValue Value { get; } = value;
        internal long VariableTimestamp { get; } = variableTimestamp;
        internal long VariableRevision { get; } = variableRevision;
        internal TimeSpan VariableDuration { get; } = variableDuration;
        internal object? PolicyToken { get; } = policyToken;
        internal bool RefreshEligible { get; } = refreshEligible;
    }

    private sealed class BulkPublication(TKey key, TValue value, long weight, TimeSpan duration)
    {
        internal TKey Key { get; } = key;
        internal TValue Value { get; } = value;
        internal long Weight { get; } = weight;
        internal TimeSpan Duration { get; } = duration;

        // Only the owning engine gate reads or writes publication ownership.
        internal Entry? PublishedEntry;
        internal long PublishedRevision;
    }

    private sealed class BulkPrepared(
        Dictionary<TKey, TValue> values,
        Dictionary<TKey, BulkPublication> publications
    )
    {
        internal Dictionary<TKey, TValue> Values { get; } = values;
        internal Dictionary<TKey, BulkPublication> Publications { get; } = publications;
    }

    private abstract class BulkGroup(
        TKey[] ownedKeys,
        long epoch,
        long mutationSequence,
        object mutationLedgerToken,
        IEqualityComparer<TKey> comparer
    )
    {
        internal TKey[] OwnedKeys { get; } = ownedKeys;
        private HashSet<TKey> OwnedKeySet { get; } = new(ownedKeys, comparer);
        internal IReadOnlyCollection<TKey> LoaderKeys { get; } =
            new ReadOnlyCollection<TKey>(ownedKeys);
        internal long Epoch { get; } = epoch;
        internal long MutationSequence { get; } = mutationSequence;
        internal object MutationLedgerToken { get; } = mutationLedgerToken;
        internal BulkPrepared? Prepared;
        internal readonly object CompletionGate = new();
        internal bool PromisesCompleted;
        internal int KeyReservationReleased;
        internal Flight Owner { get; set; } = null!;

        internal bool IsOwnedKey(TKey key) => OwnedKeySet.Contains(key);
    }

    private sealed class BulkSyncGroup(
        TKey[] ownedKeys,
        long epoch,
        long mutationSequence,
        object mutationLedgerToken,
        Func<IReadOnlyCollection<TKey>, IReadOnlyDictionary<TKey, TValue>> loader,
        IEqualityComparer<TKey> comparer
    ) : BulkGroup(ownedKeys, epoch, mutationSequence, mutationLedgerToken, comparer)
    {
        internal Func<
            IReadOnlyCollection<TKey>,
            IReadOnlyDictionary<TKey, TValue>
        > Loader { get; } = loader;
    }

    private sealed class BulkAsyncGroup(
        TKey[] ownedKeys,
        long epoch,
        long mutationSequence,
        object mutationLedgerToken,
        Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        > loader,
        IEqualityComparer<TKey> comparer
    ) : BulkGroup(ownedKeys, epoch, mutationSequence, mutationLedgerToken, comparer)
    {
        internal Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        > Loader { get; } = loader;

        internal Dictionary<TKey, TaskCompletionSource<TValue>> Promises { get; } =
            ownedKeys.ToDictionary(
                key => key,
                _ => new TaskCompletionSource<TValue>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                ),
                comparer
            );
    }
}
