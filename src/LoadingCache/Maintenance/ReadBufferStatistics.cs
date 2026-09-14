namespace LoadingCache.Maintenance;

/// <summary>
/// A point-in-time snapshot of a bounded striped read transport.
/// </summary>
/// <remarks>
/// Cumulative counters remain zero when recording is disabled. The queued gauge and shutdown
/// state are always available and do not depend on counter recording.
/// </remarks>
internal readonly struct ReadBufferStatistics
{
    internal ReadBufferStatistics(
        bool isDisposed,
        long queued,
        long enqueued,
        long dequeued,
        long droppedFull,
        long droppedShutdown,
        long droppedFailed = 0
    )
    {
        IsDisposed = isDisposed;
        Queued = queued;
        Enqueued = enqueued;
        Dequeued = dequeued;
        DroppedFull = droppedFull;
        DroppedShutdown = droppedShutdown;
        DroppedFailed = droppedFailed;
    }

    internal bool IsDisposed { get; }

    internal long Queued { get; }

    internal long Enqueued { get; }

    internal long Dequeued { get; }

    internal long DroppedFull { get; }

    internal long DroppedShutdown { get; }

    /// <summary>
    /// Gets the number of events rejected by a bounded CAS reservation failure.
    /// </summary>
    internal long DroppedFailed { get; }

    internal long Dropped =>
        SaturatingAdd(SaturatingAdd(DroppedFull, DroppedFailed), DroppedShutdown);

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;
}
