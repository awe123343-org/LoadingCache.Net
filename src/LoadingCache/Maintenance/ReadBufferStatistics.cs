namespace LoadingCache.Maintenance;

/// <summary>
/// A point-in-time snapshot of a bounded striped read transport.
/// </summary>
internal readonly struct ReadBufferStatistics
{
    internal ReadBufferStatistics(
        bool isDisposed,
        long queued,
        long enqueued,
        long dequeued,
        long droppedFull,
        long droppedShutdown
    )
    {
        IsDisposed = isDisposed;
        Queued = queued;
        Enqueued = enqueued;
        Dequeued = dequeued;
        DroppedFull = droppedFull;
        DroppedShutdown = droppedShutdown;
    }

    internal bool IsDisposed { get; }

    internal long Queued { get; }

    internal long Enqueued { get; }

    internal long Dequeued { get; }

    internal long DroppedFull { get; }

    internal long DroppedShutdown { get; }

    internal long Dropped => DroppedFull + DroppedShutdown;
}
