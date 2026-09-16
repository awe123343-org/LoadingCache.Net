using LoadingCache.Diagnostics;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private bool IsRefreshEligibleLocked(Entry entry, long now)
    {
        long refreshTicks = Volatile.Read(ref _refreshAfterWriteTicks);
        if (
            refreshTicks < 0
            || !Volatile.Read(ref entry.IsReady)
            || entry.RefreshFlight is not null
        )
        {
            return false;
        }

        bool inFailureBackoff =
            entry.HasRefreshFailure
            && _refreshFailureBackoffTicks > 0
            && _timeProvider.GetElapsedTime(entry.RefreshFailureTimestamp, now)
                < TimeSpan.FromTicks(_refreshFailureBackoffTicks);
        if (inFailureBackoff)
        {
            RecordCounter(CacheCounterKind.RefreshBackoff);
            return false;
        }

        return _timeProvider.GetElapsedTime(entry.WriteTimestamp, now)
            >= TimeSpan.FromTicks(refreshTicks);
    }

    private bool IsCurrentRefreshFlightLocked(Entry entry, Flight flight)
    {
        return IsCurrentRefreshFlightSnapshot(entry, flight)
            && flight.Epoch == _epoch
            && _entries.IsCurrent(entry);
    }

    private static bool IsCurrentRefreshFlightSnapshot(Entry entry, Flight? flight)
    {
        return flight is not null
            && ReferenceEquals(entry.RefreshFlight, flight)
            && Volatile.Read(ref flight.PublishRevoked) == 0;
    }

    private void StartAutomaticRefresh(
        Entry entry,
        Func<TKey, TValue, CancellationToken, Task<TValue>> reloadFactory
    )
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        AsyncFlight? flight = null;
        bool collected = false;
        lock (_gate)
        {
            if (_disposed != 0 || !_entries.IsCurrent(entry))
            {
                return;
            }

            lock (entry.Sync)
            {
                long now = _timeProvider.GetTimestamp();
                if (!IsRefreshEligibleLocked(entry, now) || !CanReserveFlightsLocked(1))
                {
                    RecordCounter(CacheCounterKind.RefreshSkipped);
                    return;
                }

                if (!entry.TryGetValue(out TValue? oldValue))
                {
                    RemoveCurrentEntryLocked(entry, collected: true);
                    collected = true;
                }
                else
                {
                    TimeSpan oldDuration = _expiry is null
                        ? TimeSpan.MaxValue
                        : GetRemainingDuration(entry, now, ExpirationKind.Variable);
                    flight = CreateRefreshFlightLocked(
                        entry,
                        (key, cancellationToken) =>
                            reloadFactory(key, oldValue!, cancellationToken),
                        oldDuration
                    );
                }
            }
        }

        evictionScope.Dispatch();
        if (collected)
        {
            return;
        }

        QueueAutomaticRefresh(flight!);
    }

    private void StartAutomaticRefresh(Entry entry, Func<TKey, TValue, TValue> reloadFactory)
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        SyncFlight? flight = null;
        bool collected = false;
        lock (_gate)
        {
            if (_disposed != 0 || !_entries.IsCurrent(entry))
            {
                return;
            }

            lock (entry.Sync)
            {
                long now = _timeProvider.GetTimestamp();
                if (!IsRefreshEligibleLocked(entry, now) || !CanReserveFlightsLocked(1))
                {
                    RecordCounter(CacheCounterKind.RefreshSkipped);
                    return;
                }

                if (!entry.TryGetValue(out TValue? oldValue))
                {
                    RemoveCurrentEntryLocked(entry, collected: true);
                    collected = true;
                }
                else
                {
                    TimeSpan oldDuration = _expiry is null
                        ? TimeSpan.MaxValue
                        : GetRemainingDuration(entry, now, ExpirationKind.Variable);
                    flight = CreateRefreshFlightLocked(
                        entry,
                        key => reloadFactory(key, oldValue!),
                        oldDuration
                    );
                }
            }
        }

        evictionScope.Dispatch();
        if (collected)
        {
            return;
        }

        QueueAutomaticRefresh(flight!);
    }

    private AsyncFlight CreateRefreshFlightLocked(
        Entry entry,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        TimeSpan oldDuration
    )
    {
        if (!entry.TryGetKey(out TKey? entryKey))
        {
            throw new InvalidOperationException("A refresh entry no longer has a live key.");
        }

        var flight = new AsyncFlight(entryKey, _epoch, ++_nextGeneration, factory)
        {
            IsRefresh = true,
            RefreshEntry = entry,
            PreviousVariableDuration = oldDuration,
        };
        // A refresh reservation is itself a new publication generation for
        // rollback fencing.  This prevents a late failure from an earlier
        // refresh from restoring over a newer refresh that has already
        // started and then failed without publishing a value.
        _testHooks?.BeforeEntryPublicationCommit?.Invoke(entry.Sync);
        entry.PublicationRevision++;
        entry.RefreshFlight = flight;
        _activeFlights.Add(flight);
        _reservedLoads++;
        return flight;
    }

    private SyncFlight CreateRefreshFlightLocked(
        Entry entry,
        Func<TKey, TValue> factory,
        TimeSpan oldDuration
    )
    {
        if (!entry.TryGetKey(out TKey? entryKey))
        {
            throw new InvalidOperationException("A refresh entry no longer has a live key.");
        }

        var flight = new SyncFlight(entryKey, _epoch, ++_nextGeneration, factory)
        {
            IsRefresh = true,
            RefreshEntry = entry,
            PreviousVariableDuration = oldDuration,
        };
        _testHooks?.BeforeEntryPublicationCommit?.Invoke(entry.Sync);
        entry.PublicationRevision++;
        entry.RefreshFlight = flight;
        _activeFlights.Add(flight);
        _reservedLoads++;
        return flight;
    }

    private void QueueAutomaticRefresh(AsyncFlight flight)
    {
        if (
            !ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Owner.StartAsyncFlight(state.Value),
                new RefreshStartState(this, flight),
                preferLocal: true
            )
        )
        {
            CompleteFailure(
                flight,
                new InvalidOperationException("The refresh scheduler rejected work.")
            );
        }
    }

    private void QueueAutomaticRefresh(SyncFlight flight)
    {
        QueueSyncFlight(flight);
    }

    internal ValueTask<TValue> RefreshAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> loadFactory,
        Func<TKey, TValue, CancellationToken, Task<TValue>> reloadFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ArgumentNullException.ThrowIfNull(loadFactory);
        ArgumentNullException.ThrowIfNull(reloadFactory);
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<TValue>(cancellationToken);
        }
        if (LoadChainContext.Contains(this, key))
        {
            return ValueTask.FromException<TValue>(
                new LoadingCacheReentrancyException(
                    "A refresh delegate attempted to await an equivalent key in its own logical load chain."
                )
            );
        }

        AsyncFlight? flight = null;
        bool start = false;
        bool cold = false;
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_entries.TryGetValue(key, out Entry? current))
            {
                if (!Volatile.Read(ref current.IsReady))
                {
                    flight = (AsyncFlight)current.Flight!;
                    RecordMiss();
                    RecordCoalescedWaiter();
                }
                else if (current.RefreshFlight is AsyncFlight existing)
                {
                    flight = existing;
                    RecordMiss();
                    RecordCoalescedWaiter();
                }
                else
                {
                    if (!CanReserveFlightsLocked(1))
                    {
                        RecordCounter(CacheCounterKind.LoadRejections);
                        throw new CacheLoadRejectedException();
                    }

                    lock (current.Sync)
                    {
                        long now = _timeProvider.GetTimestamp();
                        if (!current.TryGetValue(out TValue? oldValue))
                        {
                            RemoveCurrentEntryLocked(current, collected: true);
                            cold = true;
                        }
                        else
                        {
                            TimeSpan oldDuration = _expiry is null
                                ? TimeSpan.MaxValue
                                : GetRemainingDuration(current, now, ExpirationKind.Variable);
                            flight = CreateRefreshFlightLocked(
                                current,
                                (refreshKey, refreshToken) =>
                                    reloadFactory(refreshKey, oldValue!, refreshToken),
                                oldDuration
                            );
                            start = true;
                        }
                    }
                }
            }
            else
            {
                cold = true;
            }
        }

        evictionScope.Dispatch();
        if (cold)
        {
            return GetAsync(key, loadFactory, reloadFactory, cancellationToken);
        }

        AsyncFlight sharedFlight =
            flight ?? throw new InvalidOperationException("The refresh flight was not installed.");
        if (start)
        {
            StartAsyncFlight(sharedFlight);
        }

        if (Volatile.Read(ref sharedFlight.Started) == 0)
        {
            StartAsyncFlight(sharedFlight);
        }

        return WaitForFlight(sharedFlight, key, cancellationToken);
    }

    internal Task<TValue> RefreshSyncAsync(
        TKey key,
        Func<TKey, TValue> loadFactory,
        Func<TKey, TValue, TValue> reloadFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ArgumentNullException.ThrowIfNull(loadFactory);
        ArgumentNullException.ThrowIfNull(reloadFactory);
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<TValue>(cancellationToken);
        }
        if (LoadChainContext.Contains(this, key))
        {
            return Task.FromException<TValue>(
                new LoadingCacheReentrancyException(
                    "A refresh delegate attempted to await an equivalent key in its own logical load chain."
                )
            );
        }

        try
        {
            return RefreshSyncCoreAsync(key, loadFactory, reloadFactory, cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<TValue>(exception);
        }
    }

    private Task<TValue> RefreshSyncCoreAsync(
        TKey key,
        Func<TKey, TValue> loadFactory,
        Func<TKey, TValue, TValue> reloadFactory,
        CancellationToken cancellationToken
    )
    {
        SyncFlight flight = null!;
        bool start = false;
        bool cold = false;
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_entries.TryGetValue(key, out Entry? current))
            {
                if (!Volatile.Read(ref current.IsReady))
                {
                    flight = (SyncFlight)current.Flight!;
                    RecordMiss();
                    RecordCoalescedWaiter();
                }
                else if (current.RefreshFlight is SyncFlight existing)
                {
                    flight = existing;
                    RecordMiss();
                    RecordCoalescedWaiter();
                }
                else
                {
                    if (!CanReserveFlightsLocked(1))
                    {
                        RecordCounter(CacheCounterKind.LoadRejections);
                        throw new CacheLoadRejectedException();
                    }

                    lock (current.Sync)
                    {
                        long now = _timeProvider.GetTimestamp();
                        if (!current.TryGetValue(out TValue? oldValue))
                        {
                            RemoveCurrentEntryLocked(current, collected: true);
                            cold = true;
                        }
                        else
                        {
                            TimeSpan oldDuration = _expiry is null
                                ? TimeSpan.MaxValue
                                : GetRemainingDuration(current, now, ExpirationKind.Variable);
                            flight = CreateRefreshFlightLocked(
                                current,
                                refreshKey => reloadFactory(refreshKey, oldValue!),
                                oldDuration
                            );
                            start = true;
                        }
                    }
                }
            }
            else
            {
                if (!CanReserveFlightsLocked(1))
                {
                    RecordCounter(CacheCounterKind.LoadRejections);
                    throw new CacheLoadRejectedException();
                }

                flight = new SyncFlight(key, _epoch, ++_nextGeneration, loadFactory);
                _entries[key] = Entry.Loading(key, _epoch, flight.Generation, flight, _weakKeys);
                _activeFlights.Add(flight);
                _reservedLoads++;
                start = true;
            }
        }

        evictionScope.Dispatch();
        if (cold)
        {
            return Task.FromResult(GetOrAdd(key, loadFactory, cancellationToken));
        }

        if (start || Volatile.Read(ref flight.Started) == 0)
        {
            QueueSyncFlight(flight);
        }

        return WaitForSyncFlight(flight, cancellationToken);
    }

    private void QueueSyncFlight(SyncFlight flight)
    {
        if (Interlocked.CompareExchange(ref flight.StartRequested, 1, 0) != 0)
        {
            return;
        }

        if (
            !ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Owner.StartSyncFlight(state.Value),
                new SyncStartState(this, flight),
                preferLocal: true
            )
        )
        {
            CompleteFailure(
                flight,
                new InvalidOperationException("The sync load scheduler rejected work.")
            );
        }
    }

    private void CompleteRefreshSuccess(Flight flight, TValue value)
    {
        if (
            Volatile.Read(ref flight.TerminalClaimed) != 0
            && Volatile.Read(ref flight.PublishRevoked) != 0
        )
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed = false;
        bool published = false;
        Entry? publishedEntry = null;
        RefreshPublicationSnapshot previousSnapshot = default;
        bool previousSnapshotCaptured = false;
        long publishedRevision = 0;
        try
        {
            long weight = ComputeWeight(flight.Key, value);
            TimeSpan variableDuration = _expiry is null
                ? TimeSpan.MaxValue
                : ComputeUpdateDuration(flight.Key, value, flight.PreviousVariableDuration);
            lock (_gate)
            {
                claimed = Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) == 0;
                if (claimed)
                {
                    Entry? entry = flight.RefreshEntry;
                    if (
                        _disposed == 0
                        && entry is not null
                        && IsCurrentRefreshFlightLocked(entry, flight)
                    )
                    {
                        // Capture the identity before any timestamp or
                        // callback can fail.  A claimed refresh failure must
                        // either restore this exact snapshot or clear only
                        // this flight's ownership.
                        publishedEntry = entry;
                        long timestamp = _timeProvider.GetTimestamp();
                        lock (entry.Sync)
                        {
                            if (IsCurrentRefreshFlightSnapshot(entry, flight))
                            {
                                previousSnapshot = new RefreshPublicationSnapshot(entry);
                                previousSnapshotCaptured = true;
                                entry.PublicationPending = true;
                                if (_useFixedWriteSnapshots)
                                {
                                    entry.PrepareWriteSnapshotUpdate();
                                }
                                entry.SetValue(value, _weakValues);
                                entry.Weight = weight;
                                entry.WriteTimestamp = timestamp;
                                entry.AccessTimestamp = timestamp;
                                entry.VariableTimestamp = timestamp;
                                entry.VariableDuration = variableDuration;
                                entry.VariableRevision++;
                                entry.PublicationRevision++;
                                publishedRevision = entry.PublicationRevision;
                                entry.RefreshFailureTimestamp = 0;
                                entry.HasRefreshFailure = false;
                                entry.PolicyDetached = false;
                                entry.RefreshFlight = null;
                                entry.SharedTask = _weakValues ? null : Task.FromResult(value);
                                InvokeHook(_testHooks?.BeforeRefreshSnapshotPublished);
                                if (_useFixedWriteSnapshots)
                                {
                                    entry.PublishWriteSnapshot(value, timestamp);
                                }
                                published = true;
                            }

                            if (published)
                            {
                                _testHooks?.BeforeEntryPublicationCommit?.Invoke(entry.Sync);
                                PublishPolicyWriteLocked(entry.PolicyToken, entry.Weight);
                                if (_expirationWheel is not null)
                                {
                                    ulong normalizedNow = GetExpirationNowLocked();
                                    AdvanceExpirationLocked(normalizedNow);
                                    ScheduleExpirationNodeLocked(entry, normalizedNow);
                                }
                                entry.PublicationPending = false;
                            }
                        }
                    }
                }
            }

            if (!claimed)
            {
                evictionScope.Dispatch();
                RetireFlight(flight, underlyingCompleted: true);
                return;
            }

            evictionScope.Dispatch();

            // A refresh may publish a shorter expiration than the previous
            // value.  Arm the single cache timer after publication, outside
            // the gate.  If arming fails, the claimed-failure path restores
            // the previous snapshot and its deadline before completing the
            // shared promise.
            if (published)
            {
                InvokeHook(_testHooks?.AfterRefreshPublished);
            }
            RequestExpirationTimer();

            if (published && publishedEntry is not null && previousSnapshotCaptured)
            {
                lock (_gate)
                {
                    QueueReplacementNotificationLocked(flight, previousSnapshot);
                }
            }

            if (RecordFlightResult(flight, CacheCounterKind.RefreshSuccesses))
            {
                RecordCounter(CacheCounterKind.LoadSuccesses);
            }

            CompletePromise(
                flight,
                static (current, result) => current.Completion.TrySetResult(result),
                value
            );
        }
        catch (Exception exception)
        {
            evictionScope.Dispatch();
            if (claimed)
            {
                CompleteClaimedRefreshFailure(
                    flight,
                    exception,
                    publishedEntry,
                    previousSnapshot,
                    previousSnapshotCaptured,
                    publishedRevision
                );
            }
            else
            {
                CompleteRefreshFailure(flight, exception);
            }
        }
    }

    private void CompleteClaimedRefreshFailure(
        Flight flight,
        Exception exception,
        Entry? publishedEntry = null,
        RefreshPublicationSnapshot previousSnapshot = default,
        bool previousSnapshotCaptured = false,
        long publishedRevision = 0
    )
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool requestTimer = false;
        lock (_gate)
        {
            Entry? entry = publishedEntry ?? flight.RefreshEntry;
            if (
                entry is not null
                && entry.Epoch == _epoch
                && _entries.TryGetValue(flight.Key, out Entry? current)
                && ReferenceEquals(current, entry)
            )
            {
                bool restored = false;
                lock (entry.Sync)
                {
                    if (
                        previousSnapshotCaptured
                        && entry.PublicationRevision == publishedRevision
                        && entry.RefreshFlight is null
                    )
                    {
                        entry.PublicationPending = true;
                        previousSnapshot.Restore(entry, _testHooks?.BeforeRefreshSnapshotRestored);
                        restored = true;
                    }
                    else if (ReferenceEquals(entry.RefreshFlight, flight))
                    {
                        // The failure happened before the new value was
                        // mutated.  Clear only the exact refresh owner and
                        // leave the previous value and hard expiration intact.
                        entry.RefreshFlight = null;
                        try
                        {
                            entry.RefreshFailureTimestamp = _timeProvider.GetTimestamp();
                            entry.HasRefreshFailure = true;
                        }
                        catch
                        {
                            // A failing time provider must not strand the
                            // claimed flight.  The value remains governed by
                            // its original expiration timestamps.
                            entry.HasRefreshFailure = false;
                        }
                    }

                    if (restored)
                    {
                        _testHooks?.BeforeEntryPublicationCommit?.Invoke(entry.Sync);
                        bool policyRestored = RestoreRefreshPolicyLocked(entry, previousSnapshot);
                        if (!policyRestored)
                        {
                            // A policy exception must not leave a resident value
                            // with an untracked node. Remove this exact entry;
                            // the shared promise is completed below regardless of
                            // any infrastructure exception.
                            try
                            {
                                RemoveCurrentEntryLocked(entry);
                            }
                            catch
                            {
                                RetireExpirationNodeLocked(entry);
                                if (_entries.TryRemoveExact(entry))
                                {
                                    RecordDictionaryMutationLocked();
                                }

                                Volatile.Write(ref entry.Retired, true);
                                entry.RefreshFlight = null;
                            }
                        }
                        try
                        {
                            bool expired = IsExpired(entry, _timeProvider.GetTimestamp());

                            if (expired)
                            {
                                RemoveExpiredEntryLocked(entry);
                            }
                            else if (
                                policyRestored
                                && _expirationWheel is not null
                                && _entries.IsCurrent(entry)
                            )
                            {
                                ulong normalizedNow = GetExpirationNowLocked();
                                AdvanceExpirationLocked(normalizedNow);
                                ScheduleExpirationNodeLocked(entry, normalizedNow);
                                requestTimer = true;
                            }
                            if (policyRestored)
                            {
                                entry.PublicationPending = false;
                            }
                        }
                        catch
                        {
                            // Freshness is rechecked by the next lookup. Do not
                            // let a secondary clock failure strand the promise.
                        }
                    }
                }
            }
        }

        evictionScope.Dispatch();
        if (requestTimer)
        {
            try
            {
                RequestExpirationTimer();
            }
            catch
            {
                // The original claimed failure is the shared terminal result.
                // A secondary timer-arm failure must not strand it.
            }
        }

        if (RecordFlightResult(flight, CacheCounterKind.RefreshFailures))
        {
            RecordCounter(
                exception is OperationCanceledException
                    ? CacheCounterKind.LoadCancellations
                    : CacheCounterKind.LoadFailures
            );
        }

        CompletePromise(
            flight,
            static (current, error) =>
            {
                if (error is OperationCanceledException canceled)
                {
                    current.Completion.TrySetCanceled(GetCancellationToken(canceled));
                }
                else
                {
                    current.TrySetException(error);
                }
            },
            exception
        );
    }

    private bool RestoreRefreshPolicyLocked(
        Entry entry,
        RefreshPublicationSnapshot previousSnapshot
    )
    {
        try
        {
            // OnPublish may have updated an existing node or admitted a node
            // that was detached while the value was hard-expired.  Reconcile
            // the exact token before exposing the restored snapshot.
            RemovePolicyWriteLocked(entry.PolicyToken);
            if (!previousSnapshot.PolicyDetached)
            {
                PublishPolicyWriteLocked(entry.PolicyToken, previousSnapshot.Weight);
            }

            entry.PolicyDetached = previousSnapshot.PolicyDetached;
            return true;
        }
        catch
        {
            entry.PolicyDetached = previousSnapshot.PolicyDetached;
            try
            {
                RemovePolicyWriteLocked(entry.PolicyToken);
            }
            catch
            {
                // The exact entry is removed by the caller if this cleanup
                // path also fails; never leave a resident ghost relying on a
                // future policy rebuild.
            }

            return false;
        }
    }

    private void CompleteRefreshFailure(Flight flight, Exception exception)
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed;
        bool removeExpired = false;
        lock (_gate)
        {
            claimed = Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) == 0;
            if (claimed)
            {
                Entry? entry = flight.RefreshEntry;
                if (entry is not null && IsCurrentRefreshFlightLocked(entry, flight))
                {
                    lock (entry.Sync)
                    {
                        if (IsCurrentRefreshFlightSnapshot(entry, flight))
                        {
                            entry.RefreshFlight = null;
                            entry.RefreshFailureTimestamp = _timeProvider.GetTimestamp();
                            entry.HasRefreshFailure = true;
                            removeExpired = IsExpired(entry, _timeProvider.GetTimestamp());
                        }
                    }

                    if (removeExpired)
                    {
                        RemoveExpiredEntryLocked(entry);
                    }
                }
            }
        }

        evictionScope.Dispatch();
        if (!claimed)
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        if (RecordFlightResult(flight, CacheCounterKind.RefreshFailures))
        {
            RecordCounter(
                exception is OperationCanceledException
                    ? CacheCounterKind.LoadCancellations
                    : CacheCounterKind.LoadFailures
            );
        }

        CompletePromise(
            flight,
            static (current, error) =>
            {
                if (error is OperationCanceledException canceled)
                {
                    current.Completion.TrySetCanceled(GetCancellationToken(canceled));
                }
                else
                {
                    current.TrySetException(error);
                }
            },
            exception
        );
    }

    private readonly struct RefreshPublicationSnapshot
    {
        internal RefreshPublicationSnapshot(Entry entry)
        {
            if (!entry.TryGetValue(out TValue? liveValue))
            {
                throw new InvalidOperationException("A refresh snapshot lost its live value.");
            }

            Value = liveValue!;
            _weakValue = entry.WeakValue is not null;
            Weight = entry.Weight;
            _writeTimestamp = entry.WriteTimestamp;
            _accessTimestamp = entry.AccessTimestamp;
            _variableTimestamp = entry.VariableTimestamp;
            _variableDuration = entry.VariableDuration;
            _sharedTask = entry.SharedTask;
            PolicyDetached = entry.PolicyDetached;
            _refreshFailureTimestamp = entry.RefreshFailureTimestamp;
            _hasRefreshFailure = entry.HasRefreshFailure;
        }

        internal TValue Value { get; }
        private readonly bool _weakValue;
        internal readonly long Weight;
        private readonly long _writeTimestamp;
        private readonly long _accessTimestamp;
        private readonly long _variableTimestamp;
        private readonly TimeSpan _variableDuration;
        private readonly Task<TValue>? _sharedTask;
        internal readonly bool PolicyDetached;
        private readonly long _refreshFailureTimestamp;
        private readonly bool _hasRefreshFailure;

        internal void Restore(Entry entry, Action? beforeSnapshotRestored)
        {
            entry.SetValue(Value, _weakValue);
            InvokeHook(beforeSnapshotRestored);
            entry.Weight = Weight;
            entry.WriteTimestamp = _writeTimestamp;
            entry.AccessTimestamp = _accessTimestamp;
            entry.VariableTimestamp = _variableTimestamp;
            // Restoring the old value is a new publication. Never reuse an
            // earlier revision: a reader may have captured the failed value
            // before rollback and still be computing outside our locks.
            entry.VariableRevision++;
            entry.PublicationRevision++;
            entry.VariableDuration = _variableDuration;
            // A refresh can snapshot a cold completion while that cold flight
            // is concurrently claimed-failed.  Its task may still be pending
            // when this rollback runs, so checking IsFaulted is too late.  A
            // claimed-failure rollback may therefore expose the retained
            // value through a completed snapshot unless the old promise has
            // already completed successfully.  Normal successful publication
            // keeps the original shared-task identity.
            entry.SharedTask =
                _weakValue ? null
                : _sharedTask is { IsCompletedSuccessfully: true } ? _sharedTask
                : Task.FromResult(Value);
            entry.PolicyDetached = PolicyDetached;
            entry.RefreshFailureTimestamp = _refreshFailureTimestamp;
            entry.HasRefreshFailure = _hasRefreshFailure;
            entry.RefreshFlight = null;
            if (entry.PublishedWrite is not null)
            {
                // Rollback is a new publication, not a restoration of an old reference.
                entry.PublishWriteSnapshot(Value, _writeTimestamp);
            }
        }
    }

    private sealed class RefreshStartState
    {
        internal RefreshStartState(CacheEngine<TKey, TValue> owner, AsyncFlight value)
        {
            Owner = owner;
            Value = value;
        }

        internal CacheEngine<TKey, TValue> Owner { get; }

        internal AsyncFlight Value { get; }
    }

    private sealed class SyncStartState
    {
        internal SyncStartState(CacheEngine<TKey, TValue> owner, SyncFlight value)
        {
            Owner = owner;
            Value = value;
        }

        internal CacheEngine<TKey, TValue> Owner { get; }

        internal SyncFlight Value { get; }
    }
}
