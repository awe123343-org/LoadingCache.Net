namespace LoadingCache.Notifications;

/// <summary>
/// Dispatches notifications through one handler with a bounded FIFO backlog.
/// </summary>
/// <typeparam name="TNotification">The notification payload type.</typeparam>
/// <remarks>
/// This is an internal infrastructure primitive for removal and eviction events. It does not
/// dispose payload values and does not promise exactly-once delivery. A successfully enqueued
/// item is invoked at most once; queue-full, scheduling-rejection, and shutdown policies may drop
/// it. The handler always runs outside the dispatcher lock.
/// </remarks>
internal sealed class BoundedNotificationDispatcher<TNotification> : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<TNotification> _queue;
    private readonly int _capacity;
    private readonly Action<TNotification> _handler;
    private readonly INotificationScheduler _scheduler;
    private readonly Action _drainCallback;
    private readonly Action? _afterDrainReleaseHook;

    private bool _drainScheduled;
    private bool _drainActive;
    private bool _handlerRunning;
    private bool _disposed;
    private long _enqueued;
    private long _invoked;
    private long _delivered;
    private long _handlerFailures;
    private long _droppedFull;
    private long _droppedSchedule;
    private long _droppedShutdown;
    private long _scheduleRejections;

    internal BoundedNotificationDispatcher(
        int capacity,
        Action<TNotification> handler,
        INotificationScheduler? scheduler = null,
        Action? afterDrainReleaseHook = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(handler);

        _capacity = capacity;
        _queue = new Queue<TNotification>(capacity);
        _handler = handler;
        _scheduler = scheduler ?? ThreadPoolNotificationScheduler.Instance;
        _drainCallback = Drain;
        _afterDrainReleaseHook = afterDrainReleaseHook;
    }

    /// <summary>
    /// Attempts to admit one notification without waiting for the handler.
    /// </summary>
    /// <returns>
    /// <see langword="true" /> when the item was admitted to the bounded FIFO; otherwise it was
    /// dropped because the dispatcher is disposed, full, or unable to schedule its drain.
    /// </returns>
    internal bool TryEnqueue(TNotification notification)
    {
        bool shouldSchedule = false;
        lock (_gate)
        {
            if (_disposed)
            {
                _droppedShutdown++;
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                _droppedFull++;
                return false;
            }

            _queue.Enqueue(notification);
            _enqueued++;
            if (!_drainScheduled)
            {
                _drainScheduled = true;
                shouldSchedule = true;
            }
        }

        if (!shouldSchedule || TryScheduleWithoutContextCapture())
        {
            return true;
        }

        RejectScheduledBatch();
        return false;
    }

    /// <summary>
    /// Gets a consistent snapshot of queue, delivery, failure, and drop counters.
    /// </summary>
    internal NotificationDispatchStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new NotificationDispatchStatistics(
                _queue.Count,
                _disposed,
                _handlerRunning,
                _enqueued,
                _invoked,
                _delivered,
                _handlerFailures,
                _droppedFull,
                _droppedSchedule,
                _droppedShutdown,
                _scheduleRejections
            );
        }
    }

    /// <summary>
    /// Stops admission and drops queued notifications without waiting for a running user handler.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _drainScheduled = false;
            DropQueuedLocked(ref _droppedShutdown);
        }
    }

    private bool TryScheduleWithoutContextCapture()
    {
        try
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                return _scheduler.TrySchedule(_drainCallback);
            }

            using (ExecutionContext.SuppressFlow())
            {
                return _scheduler.TrySchedule(_drainCallback);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RejectScheduledBatch()
    {
        lock (_gate)
        {
            _scheduleRejections++;
            if (!_drainScheduled)
            {
                return;
            }

            _drainScheduled = false;
            DropQueuedLocked(ref _droppedSchedule);
        }
    }

    private void Drain()
    {
        bool ownsActiveDrain;
        lock (_gate)
        {
            if (_drainActive)
            {
                return;
            }

            _drainActive = true;
            ownsActiveDrain = true;
        }

        try
        {
            while (true)
            {
                TNotification notification = default!;
                Action? afterDrainReleaseHook = null;
                bool shouldReturn = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _drainScheduled = false;
                        DropQueuedLocked(ref _droppedShutdown);
                        _drainActive = false;
                        ownsActiveDrain = false;
                        shouldReturn = true;
                    }
                    else if (_queue.Count == 0)
                    {
                        _drainScheduled = false;
                        afterDrainReleaseHook = _afterDrainReleaseHook;
                        _drainActive = false;
                        ownsActiveDrain = false;
                        shouldReturn = true;
                    }
                    else
                    {
                        notification = _queue.Dequeue();
                        _handlerRunning = true;
                        _invoked++;
                    }
                }

                if (shouldReturn)
                {
                    afterDrainReleaseHook?.Invoke();
                    return;
                }

                try
                {
                    _handler(notification);
                    lock (_gate)
                    {
                        _delivered++;
                    }
                }
                catch (Exception)
                {
                    lock (_gate)
                    {
                        _handlerFailures++;
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        _handlerRunning = false;
                    }
                }
            }
        }
        finally
        {
            if (ownsActiveDrain)
            {
                lock (_gate)
                {
                    _drainActive = false;
                }
            }
        }
    }

    private void DropQueuedLocked(ref long counter)
    {
        counter += _queue.Count;
        _queue.Clear();
    }
}
