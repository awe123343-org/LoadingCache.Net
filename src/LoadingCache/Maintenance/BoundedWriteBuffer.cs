using System.Diagnostics;

namespace LoadingCache.Maintenance;

/// <summary>
/// A bounded, lossless transport for correctness-relevant maintenance events.
/// </summary>
/// <typeparam name="TEvent">The event type carried by the buffer.</typeparam>
/// <remarks>
/// Unlike the read transport, a full write transport cannot report success while dropping an
/// event.  The policy adapter therefore uses <see cref="TryEnqueue"/> and cooperatively drains
/// an older event before retrying.  The buffer itself only owns admission and lifetime; it never
/// processes events or invokes callbacks. The policy owner processes a dequeued event after the
/// buffer operation returns. When a coordination monitor is shared, that outer monitor can still
/// be held by the owner, but no user callback runs inside the buffer operation.
/// </remarks>
internal sealed class BoundedWriteBuffer<TEvent> : IDisposable
{
    private readonly object _gate;
    private readonly Queue<TEvent> _queue;
    private int _queued;
    private long _enqueued;
    private long _dequeued;
    private long _full;
    private long _droppedClear;
    private long _droppedShutdown;
    private int _disposed;

    internal BoundedWriteBuffer(int capacity, object? coordinationGate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _gate = coordinationGate ?? new object();
        _queue = new Queue<TEvent>(capacity);
    }

    internal int Capacity { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal int Queued => Volatile.Read(ref _queued);

    /// <summary>
    /// Attempts to publish without waiting.  A false result means that the caller must drain
    /// existing events and retry; the event has not been lost.
    /// </summary>
    internal bool TryEnqueue(TEvent value)
    {
        lock (_gate)
        {
            return TryEnqueueLocked(value);
        }
    }

    /// <summary>
    /// Attempts to publish while the caller owns the coordination monitor.
    /// </summary>
    internal bool TryEnqueueLocked(TEvent value)
    {
        Debug.Assert(Monitor.IsEntered(_gate));
        if (_disposed != 0)
        {
            SaturatingIncrement(ref _droppedShutdown);
            return false;
        }

        if (_queue.Count >= Capacity)
        {
            SaturatingIncrement(ref _full);
            return false;
        }

        _queue.Enqueue(value);
        Volatile.Write(ref _queued, _queued + 1);
        SaturatingIncrement(ref _enqueued);
        return true;
    }

    internal bool TryDequeue(out TEvent value)
    {
        lock (_gate)
        {
            return TryDequeueLocked(out value);
        }
    }

    /// <summary>
    /// Attempts to dequeue while the caller owns the coordination monitor.
    /// </summary>
    internal bool TryDequeueLocked(out TEvent value)
    {
        Debug.Assert(Monitor.IsEntered(_gate));
        if (_queue.Count == 0)
        {
            value = default!;
            return false;
        }

        value = _queue.Dequeue();
        Volatile.Write(ref _queued, _queued - 1);
        SaturatingIncrement(ref _dequeued);
        return true;
    }

    /// <summary>
    /// Drops events invalidated by a clear barrier.  This is the only non-shutdown discard path.
    /// </summary>
    /// <remarks>
    /// The optional callback is reserved for internal accounting. It must not re-enter the buffer,
    /// invoke user code, or throw.
    /// </remarks>
    internal int Clear(Action<TEvent>? onDiscard = null)
    {
        lock (_gate)
        {
            int dropped = 0;
            if (onDiscard is null)
            {
                dropped = _queue.Count;
                _queue.Clear();
            }
            else
            {
                while (_queue.Count != 0)
                {
                    onDiscard(_queue.Dequeue());
                    dropped++;
                }
            }

            Volatile.Write(ref _queued, 0);
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
                capacity: Capacity,
                queued: _queued,
                enqueued: _enqueued,
                dequeued: _dequeued,
                full: _full,
                droppedClear: _droppedClear,
                droppedShutdown: _droppedShutdown
            );
        }
    }

    /// <summary>
    /// Copies the queued values for invariant inspection. This is intentionally a diagnostic-only
    /// operation and is not used by the maintenance hot path.
    /// </summary>
    internal int CopyTo(List<TEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            destination.Clear();
            destination.AddRange(_queue);
            return _queue.Count;
        }
    }

    /// <summary>
    /// Stops admission and releases all queued event references.
    /// </summary>
    /// <remarks>
    /// The optional callback is reserved for internal accounting. It must not re-enter the buffer,
    /// invoke user code, or throw.
    /// </remarks>
    public void Dispose() => Dispose(onDiscard: null);

    internal void Dispose(Action<TEvent>? onDiscard)
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            int dropped = 0;
            if (onDiscard is null)
            {
                dropped = _queue.Count;
                _queue.Clear();
            }
            else
            {
                while (_queue.Count != 0)
                {
                    onDiscard(_queue.Dequeue());
                    dropped++;
                }
            }

            Volatile.Write(ref _queued, 0);
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
