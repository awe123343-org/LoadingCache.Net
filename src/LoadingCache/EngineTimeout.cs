using LoadingCache.Diagnostics;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private bool PrepareFlightExecution(Flight flight)
    {
        if (_loadTimeoutTicks < 0)
        {
            flight.LoadCancellationToken = _shutdownCts.Token;
            return true;
        }

        try
        {
            flight.TimeoutStartTimestamp = _timeProvider.GetTimestamp();
            if (flight is AsyncFlight)
            {
                flight.WorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdownCts.Token
                );
                // Capture the token while the CTS is owned by the flight.  A
                // synchronously firing TimeProvider may timeout and dispose
                // the CTS before StartAsyncFlight asks for the token.
                flight.LoadCancellationToken = flight.WorkCancellation.Token;
            }
            else
            {
                flight.LoadCancellationToken = _shutdownCts.Token;
            }

            ArmFlightTimeout(flight);
            return true;
        }
        catch (Exception exception)
        {
            CompleteFailure(flight, exception);
            return false;
        }
    }

    private void ArmFlightTimeout(Flight flight)
    {
        if (_loadTimeoutTicks < 0)
        {
            return;
        }

        TimeSpan due = ClampTimerDueTime(TimeSpan.FromTicks(_loadTimeoutTicks));
        ITimer timer;
        if (ExecutionContext.IsFlowSuppressed())
        {
            timer = _timeProvider.CreateTimer(
                static state =>
                    ((FlightTimeoutState)state!).Owner.OnFlightTimeout(
                        ((FlightTimeoutState)state).Flight
                    ),
                new FlightTimeoutState(this, flight),
                due,
                Timeout.InfiniteTimeSpan
            );
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                timer = _timeProvider.CreateTimer(
                    static state =>
                        ((FlightTimeoutState)state!).Owner.OnFlightTimeout(
                            ((FlightTimeoutState)state).Flight
                        ),
                    new FlightTimeoutState(this, flight),
                    due,
                    Timeout.InfiniteTimeSpan
                );
            }
        }

        bool dispose = false;
        lock (_gate)
        {
            if (
                _disposed != 0
                || flight.Retired != 0
                || Volatile.Read(ref flight.TerminalClaimed) != 0
            )
            {
                dispose = true;
            }
            else
            {
                flight.TimeoutTimer = timer;
            }
        }

        if (dispose)
        {
            DisposeFlightTimeoutTimer(timer);
        }
    }

    private void OnFlightTimeout(Flight flight)
    {
        ITimer? timer;
        CancellationTokenSource? cancellation;
        bool timedOut;
        bool removeExpired = false;
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        lock (_gate)
        {
            if (
                _disposed != 0
                || flight.Retired != 0
                || Volatile.Read(ref flight.TerminalClaimed) != 0
            )
            {
                return;
            }

            TimeSpan elapsed = _timeProvider.GetElapsedTime(
                flight.TimeoutStartTimestamp,
                _timeProvider.GetTimestamp()
            );
            TimeSpan timeout = TimeSpan.FromTicks(_loadTimeoutTicks);
            if (elapsed < timeout)
            {
                timer = flight.TimeoutTimer;
                timer?.Change(ClampTimerDueTime(timeout - elapsed), Timeout.InfiniteTimeSpan);

                return;
            }

            if (Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) != 0)
            {
                return;
            }

            Entry? refreshEntry = flight.RefreshEntry;
            bool ownsRefresh =
                flight.IsRefresh
                && refreshEntry is not null
                && IsCurrentRefreshFlightLocked(refreshEntry, flight);

            Volatile.Write(ref flight.PublishRevoked, 1);
            timer = flight.TimeoutTimer;
            flight.TimeoutTimer = null;
            cancellation = flight.WorkCancellation;
            flight.TimeoutCancellationStarted = 1;
            flight.TimeoutFinalizationCompleted = 0;
            Volatile.Write(ref flight.CancellationCleanupCompleted, cancellation is null ? 1 : 0);

            if (ownsRefresh)
            {
                lock (refreshEntry!.Sync)
                {
                    if (ReferenceEquals(refreshEntry.RefreshFlight, flight))
                    {
                        refreshEntry.RefreshFlight = null;
                        refreshEntry.RefreshFailureTimestamp = _timeProvider.GetTimestamp();
                        refreshEntry.HasRefreshFailure = true;
                        removeExpired = IsExpired(refreshEntry, _timeProvider.GetTimestamp());
                    }
                }

                if (removeExpired)
                {
                    RemoveExpiredEntryLocked(refreshEntry);
                }
            }
            else if (!flight.IsRefresh)
            {
                RemoveCurrentEntryLocked(flight);
            }

            RecordCounter(CacheCounterKind.LoadTimeouts);
            if (flight.IsRefresh)
            {
                if (TryRecordFlightDuration(flight))
                {
                    RecordCounter(CacheCounterKind.RefreshFailures);
                }
            }
            else
            {
                TryRecordFlightDuration(flight);
            }

            timedOut = true;
        }

        evictionScope.Dispatch();

        DisposeFlightTimeoutTimer(timer);

        if (cancellation is not null)
        {
            Task cancellationTask = CancelAndDisposeAsync(flight, cancellation);
            lock (_gate)
            {
                flight.CancellationTask = cancellationTask;
            }
        }

        if (!timedOut)
        {
            return;
        }

        try
        {
            CompleteTimeoutPromise(flight);
        }
        finally
        {
            lock (_gate)
            {
                flight.TimeoutFinalizationCompleted = 1;
            }

            RetireFlight(flight);
        }
    }

    private void CompleteTimeoutPromise(Flight flight)
    {
        var exception = new TimeoutException("The cache loading flight exceeded its deadline.");
        CompleteBulkTimeout(flight, exception);
        try
        {
            InvokeHook(_testHooks?.BeforeCompletion);
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    asyncFlight.TrySetException(exception);
                    break;
                case SyncFlight syncFlight:
                    syncFlight.Set(exception);
                    break;
            }
        }
        catch (Exception hookException)
        {
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    asyncFlight.TrySetException(hookException);
                    break;
                case SyncFlight syncFlight:
                    syncFlight.Set(hookException);
                    break;
            }
        }
    }

    private async Task CancelAndDisposeAsync(Flight flight, CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cancellation callbacks are user code.  Their failure must not
            // become an unobserved task fault or affect cache ownership.
        }
        finally
        {
            cancellation.Dispose();
            lock (_gate)
            {
                flight.CancellationCleanupCompleted = 1;
            }

            // Cancellation cleanup is part of the flight's reservation
            // lifetime.  This call only finalizes the flight when the
            // underlying loader has also been observed to finish.
            RetireFlight(flight);
        }
    }

    private void DisposeFlightTimeoutTimer(ITimer? timer)
    {
        try
        {
            timer?.Dispose();
        }
        catch
        {
            // A provider's cleanup failure must not replace the selected loader
            // outcome or leave a timeout promise pending. This records the failed
            // attempt; it cannot guarantee that the provider released its resource.
            RecordCounter(CacheCounterKind.TimerDisposalFailures);
        }
    }

    private sealed class FlightTimeoutState
    {
        internal FlightTimeoutState(CacheEngine<TKey, TValue> owner, Flight flight)
        {
            Owner = owner;
            Flight = flight;
        }

        internal CacheEngine<TKey, TValue> Owner { get; }

        internal Flight Flight { get; }
    }
}
