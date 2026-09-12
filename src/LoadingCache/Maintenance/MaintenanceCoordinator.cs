namespace LoadingCache.Maintenance;

/// <summary>
/// Reports the result of a maintenance request.
/// </summary>
internal enum MaintenanceRequestResult
{
    Accepted,
    ScheduleRejected,
    Disposed,
}

/// <summary>
/// Coalesces maintenance requests into one worker and gives the owner a synchronous fallback.
/// </summary>
/// <remarks>
/// The supplied drain delegate performs one bounded pass and returns whether more work remains.
/// It is always invoked outside the coordinator lock. The coordinator owns only scheduling state;
/// it does not own the cache policy lock and does not call user callbacks.
/// </remarks>
internal sealed class MaintenanceCoordinator : IDisposable
{
    private const int DefaultMaxPassesPerInvocation = 32;

    [ThreadStatic]
    private static MaintenanceCoordinator? _activeWorker;

    private readonly object _gate = new();
    private readonly Func<bool> _drain;
    private readonly IMaintenanceScheduler _scheduler;
    private readonly Action _workerCallback;
    private readonly int _maxPassesPerInvocation;

    private MaintenanceCoordinatorState _state = MaintenanceCoordinatorState.Idle;
    private bool _fallbackRequired;
    private bool _inlineCallbackObserved;
    private long _requests;
    private long _coalescedRequests;
    private long _drainPasses;
    private long _moreWorkPasses;
    private long _scheduleRejections;
    private long _drainFaults;
    private long _synchronousCleanUps;
    private long _budgetExhaustions;

    internal MaintenanceCoordinator(
        Func<bool> drain,
        IMaintenanceScheduler? scheduler = null,
        int maxPassesPerInvocation = DefaultMaxPassesPerInvocation
    )
    {
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPassesPerInvocation);

        _drain = drain;
        _scheduler = scheduler ?? ThreadPoolMaintenanceScheduler.Instance;
        _workerCallback = Worker;
        _maxPassesPerInvocation = maxPassesPerInvocation;
    }

    internal MaintenanceCoordinatorState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Requests a coalesced asynchronous maintenance pass.
    /// </summary>
    /// <returns>
    /// <see cref="MaintenanceRequestResult.ScheduleRejected" /> means the caller must use
    /// <see cref="CleanUp" /> or another synchronous fallback; no work is silently stranded.
    /// </returns>
    internal MaintenanceRequestResult Request()
    {
        lock (_gate)
        {
            _requests++;
            switch (_state)
            {
                case MaintenanceCoordinatorState.Disposed:
                    return MaintenanceRequestResult.Disposed;

                case MaintenanceCoordinatorState.Idle:
                    _state = MaintenanceCoordinatorState.Scheduled;
                    _fallbackRequired = false;
                    break;

                case MaintenanceCoordinatorState.Scheduled:
                    _coalescedRequests++;
                    return MaintenanceRequestResult.Accepted;

                case MaintenanceCoordinatorState.Running:
                    _state = MaintenanceCoordinatorState.RunningRequired;
                    _coalescedRequests++;
                    return MaintenanceRequestResult.Accepted;

                case MaintenanceCoordinatorState.RunningRequired:
                    _coalescedRequests++;
                    return MaintenanceRequestResult.Accepted;

                default:
                    throw new InvalidOperationException($"Unknown maintenance state: {_state}.");
            }
        }

        if (TryScheduleWithoutContextCapture())
        {
            lock (_gate)
            {
                // An inline scheduler can run the bounded worker before returning. Propagate its
                // fallback requirement to the producer instead of hiding remaining work.
                if (_fallbackRequired && _state == MaintenanceCoordinatorState.Idle)
                {
                    return MaintenanceRequestResult.ScheduleRejected;
                }

                return MaintenanceRequestResult.Accepted;
            }
        }

        lock (_gate)
        {
            // A well-behaved scheduler that returns false did not run the callback. An inline or
            // racing scheduler may have run it before returning; in that case its state transition
            // is already sufficient and reporting rejection would create a duplicate fallback.
            if (_state != MaintenanceCoordinatorState.Scheduled)
            {
                return MaintenanceRequestResult.Accepted;
            }

            _state = MaintenanceCoordinatorState.Idle;
            _scheduleRejections++;
            return MaintenanceRequestResult.ScheduleRejected;
        }
    }

    /// <summary>
    /// Claims scheduled work, or runs bounded explicit cleanup passes while idle.
    /// </summary>
    /// <returns>
    /// A result identifying whether this caller ran work, whether more work remains, and whether a
    /// synchronous fallback is still required.
    /// </returns>
    internal MaintenanceCleanupResult CleanUp()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case MaintenanceCoordinatorState.Disposed:
                    return new MaintenanceCleanupResult(false, false, false);

                case MaintenanceCoordinatorState.Running:
                case MaintenanceCoordinatorState.RunningRequired:
                    _state = MaintenanceCoordinatorState.RunningRequired;
                    return new MaintenanceCleanupResult(false, true, _fallbackRequired);

                case MaintenanceCoordinatorState.Idle:
                case MaintenanceCoordinatorState.Scheduled:
                    _state = MaintenanceCoordinatorState.Running;
                    _fallbackRequired = false;
                    _synchronousCleanUps++;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown maintenance state: {_state}.");
            }
        }

        return RunPasses();
    }

    internal MaintenanceStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new MaintenanceStatistics(
                _state,
                _requests,
                _coalescedRequests,
                _drainPasses,
                _moreWorkPasses,
                _scheduleRejections,
                _drainFaults,
                _synchronousCleanUps,
                _budgetExhaustions,
                _fallbackRequired
            );
        }
    }

    /// <summary>
    /// Marks the coordinator disposed without waiting for an in-progress user-owned drain.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _state = MaintenanceCoordinatorState.Disposed;
        }
    }

    private bool TryScheduleWithoutContextCapture()
    {
        try
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                return _scheduler.TrySchedule(_workerCallback);
            }

            using (ExecutionContext.SuppressFlow())
            {
                return _scheduler.TrySchedule(_workerCallback);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Worker()
    {
        lock (_gate)
        {
            // CleanUp may have claimed the scheduled callback. A stale scheduled callback must be
            // a no-op rather than entering a second drain owner.
            if (_state != MaintenanceCoordinatorState.Scheduled)
            {
                return;
            }

            if (ReferenceEquals(_activeWorker, this))
            {
                // Protect against a test/host scheduler that invokes a re-arm inline. The active
                // worker will observe the flag after TrySchedule returns and report fallback
                // rather than recursively growing the stack or bypassing its pass budget.
                _inlineCallbackObserved = true;
                return;
            }

            _state = MaintenanceCoordinatorState.Running;
        }

        RunPasses();
    }

    private MaintenanceCleanupResult RunPasses()
    {
        MaintenanceCoordinator? previousWorker = _activeWorker;
        _activeWorker = this;
        try
        {
            for (int pass = 0; pass < _maxPassesPerInvocation; pass++)
            {
                bool moreWork = false;
                bool faulted = false;
                try
                {
                    moreWork = _drain();
                }
                catch (Exception)
                {
                    faulted = true;
                }

                bool rearm;
                lock (_gate)
                {
                    _drainPasses++;
                    if (faulted)
                    {
                        _drainFaults++;
                    }

                    if (_state == MaintenanceCoordinatorState.Disposed)
                    {
                        return new MaintenanceCleanupResult(true, false, false);
                    }

                    if (moreWork || _state == MaintenanceCoordinatorState.RunningRequired)
                    {
                        if (moreWork)
                        {
                            _moreWorkPasses++;
                        }

                        if (pass + 1 < _maxPassesPerInvocation)
                        {
                            // Keep this worker as owner while budget remains. Each iteration is
                            // one bounded drain pass, not an unbounded queue drain.
                            _state = MaintenanceCoordinatorState.Running;
                            continue;
                        }

                        _budgetExhaustions++;
                        _state = MaintenanceCoordinatorState.Scheduled;
                        _inlineCallbackObserved = false;
                        rearm = true;
                    }
                    else
                    {
                        _state = MaintenanceCoordinatorState.Idle;
                        _fallbackRequired = false;
                        return new MaintenanceCleanupResult(true, false, false);
                    }
                }

                if (rearm)
                {
                    return RearmAfterBudget();
                }
            }

            throw new InvalidOperationException("Maintenance pass budget did not terminate.");
        }
        finally
        {
            _activeWorker = previousWorker;
        }
    }

    private MaintenanceCleanupResult RearmAfterBudget()
    {
        if (TryScheduleWithoutContextCapture())
        {
            lock (_gate)
            {
                if (_state == MaintenanceCoordinatorState.Disposed)
                {
                    return new MaintenanceCleanupResult(true, false, false);
                }

                if (_inlineCallbackObserved)
                {
                    // The injected scheduler violated the non-inline scheduling contract. Leave
                    // work explicitly pending for CleanUp instead of recursive re-entry.
                    _inlineCallbackObserved = false;
                    _state = MaintenanceCoordinatorState.Idle;
                    _fallbackRequired = true;
                    return new MaintenanceCleanupResult(true, true, true);
                }

                _fallbackRequired = false;
                return new MaintenanceCleanupResult(true, true, false);
            }
        }

        lock (_gate)
        {
            _scheduleRejections++;
            switch (_state)
            {
                case MaintenanceCoordinatorState.Scheduled:
                    _state = MaintenanceCoordinatorState.Idle;
                    _fallbackRequired = true;
                    return new MaintenanceCleanupResult(true, true, true);

                case MaintenanceCoordinatorState.Disposed:
                    return new MaintenanceCleanupResult(true, false, false);

                case MaintenanceCoordinatorState.Idle:
                case MaintenanceCoordinatorState.Running:
                case MaintenanceCoordinatorState.RunningRequired:
                default:
                    // Another owner claimed the scheduled state while the scheduler was
                    // returning. Its worker owns the continuation; this invocation must not
                    // run a second owner.
                    return new MaintenanceCleanupResult(true, true, _fallbackRequired);
            }
        }
    }
}
