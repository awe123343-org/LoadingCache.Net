namespace LoadingCache.Notifications;

/// <summary>
/// A point-in-time snapshot of a bounded notification dispatcher.
/// </summary>
internal readonly struct NotificationDispatchStatistics
{
    internal NotificationDispatchStatistics(
        int queued,
        bool isDisposed,
        bool handlerRunning,
        long enqueued,
        long invoked,
        long delivered,
        long handlerFailures,
        long droppedFull,
        long droppedSchedule,
        long droppedShutdown,
        long scheduleRejections
    )
    {
        Queued = queued;
        IsDisposed = isDisposed;
        HandlerRunning = handlerRunning;
        Enqueued = enqueued;
        Invoked = invoked;
        Delivered = delivered;
        HandlerFailures = handlerFailures;
        DroppedFull = droppedFull;
        DroppedSchedule = droppedSchedule;
        DroppedShutdown = droppedShutdown;
        ScheduleRejections = scheduleRejections;
    }

    internal int Queued { get; }

    internal bool IsDisposed { get; }

    internal bool HandlerRunning { get; }

    /// <summary>
    /// Gets the number of events admitted to the bounded FIFO.
    /// Admission does not guarantee delivery when shutdown or scheduling rejection drops work.
    /// </summary>
    internal long Enqueued { get; }

    internal long Invoked { get; }

    internal long Delivered { get; }

    internal long HandlerFailures { get; }

    internal long DroppedFull { get; }

    internal long DroppedSchedule { get; }

    internal long DroppedShutdown { get; }

    internal long ScheduleRejections { get; }

    internal long Dropped
    {
        get
        {
            long total = SaturatingAdd(DroppedFull, DroppedSchedule);
            return SaturatingAdd(total, DroppedShutdown);
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;
}
