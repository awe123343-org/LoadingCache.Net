namespace LoadingCache.Maintenance;

/// <summary>
/// A bounded, lossless transport for correctness-relevant maintenance events.
/// </summary>
/// <typeparam name="TEvent">The event type carried by the buffer.</typeparam>
/// <remarks>
/// Unlike the read transport, a full write transport cannot report success while dropping an
/// event.  The policy adapter therefore uses <see cref="TryEnqueue"/> and cooperatively drains
/// an older event before retrying.  The buffer itself only owns admission and lifetime; event
/// processing is always performed by the policy owner outside this buffer's lock.
/// </remarks>
internal sealed class BoundedWriteBuffer<TEvent> : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<TEvent> _queue;
    private readonly int _capacity;
    private long _enqueued;
    private long _dequeued;
    private long _full;
    private long _droppedClear;
    private long _droppedShutdown;
    private int _disposed;

    internal BoundedWriteBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _queue = new Queue<TEvent>(capacity);
    }

    internal int Capacity => _capacity;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to publish without waiting.  A false result means that the caller must drain
    /// existing events and retry; the event has not been lost.
    /// </summary>
    internal bool TryEnqueue(TEvent value)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                SaturatingIncrement(ref _droppedShutdown);
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                SaturatingIncrement(ref _full);
                return false;
            }

            _queue.Enqueue(value);
            SaturatingIncrement(ref _enqueued);
            return true;
        }
    }

    internal bool TryDequeue(out TEvent value)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                value = default!;
                return false;
            }

            value = _queue.Dequeue();
            SaturatingIncrement(ref _dequeued);
            return true;
        }
    }

    /// <summary>
    /// Drops events invalidated by a clear barrier.  This is the only non-shutdown discard path.
    /// </summary>
    internal int Clear()
    {
        lock (_gate)
        {
            int dropped = _queue.Count;
            _queue.Clear();
            SaturatingAdd(ref _droppedClear, dropped);
            return dropped;
        }
    }

    internal WriteBufferStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new WriteBufferStatistics(
                isDisposed: _disposed != 0,
                capacity: _capacity,
                queued: _queue.Count,
                enqueued: _enqueued,
                dequeued: _dequeued,
                full: _full,
                droppedClear: _droppedClear,
                droppedShutdown: _droppedShutdown
            );
        }
    }

    /// <summary>
    /// Stops admission and releases all queued event references.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            int dropped = _queue.Count;
            _queue.Clear();
            SaturatingAdd(ref _droppedShutdown, dropped);
        }
    }

    private static void SaturatingIncrement(ref long location) => SaturatingAdd(ref location, 1);

    private static void SaturatingAdd(ref long location, long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        location = delta >= long.MaxValue - location ? long.MaxValue : location + delta;
    }
}

/// <summary>
/// A point-in-time snapshot of a bounded reliable write transport.
/// </summary>
internal readonly struct WriteBufferStatistics
{
    internal WriteBufferStatistics(
        bool isDisposed,
        int capacity,
        int queued,
        long enqueued,
        long dequeued,
        long full,
        long droppedClear,
        long droppedShutdown
    )
    {
        IsDisposed = isDisposed;
        Capacity = capacity;
        Queued = queued;
        Enqueued = enqueued;
        Dequeued = dequeued;
        Full = full;
        DroppedClear = droppedClear;
        DroppedShutdown = droppedShutdown;
    }

    internal bool IsDisposed { get; }

    internal int Capacity { get; }

    internal int Queued { get; }

    internal long Enqueued { get; }

    internal long Dequeued { get; }

    internal long Full { get; }

    internal long DroppedClear { get; }

    internal long DroppedShutdown { get; }

    internal long Dropped => SaturatingAdd(DroppedClear, DroppedShutdown);

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;
}
