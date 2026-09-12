using System.Threading.Channels;

namespace LoadingCache.Maintenance;

/// <summary>
/// Provides a bounded, striped transport for best-effort policy read events.
/// </summary>
/// <typeparam name="TEvent">The event type carried by the transport.</typeparam>
/// <remarks>
/// Producers select a stripe from the current managed thread id and never wait for space. The
/// maintenance owner normally is the only consumer and calls <see cref="TryRead" /> synchronously;
/// shutdown may concurrently drain completed channels. A full stripe drops the new event and
/// records the drop; the transport never uses a channel full mode that reports a successful write
/// while dropping an item. Ordering is FIFO within each stripe while the normal owner consumes;
/// concurrent producers have no global ordering guarantee.
/// </remarks>
internal sealed class StripedReadBuffer<TEvent> : IDisposable
{
    private readonly Channel<TEvent>[] _stripes;
    private readonly int _stripeMask;
    private readonly long[] _enqueuedByStripe;
    private readonly long[] _dequeuedByStripe;
    private readonly long[] _droppedFullByStripe;
    private readonly long[] _droppedShutdownByStripe;
    private int _nextReadStripe;
    private int _disposed;

    internal StripedReadBuffer(int stripeCount, int stripeCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCapacity);

        if ((stripeCount & (stripeCount - 1)) != 0)
        {
            throw new ArgumentException(
                "Stripe count must be a power of two.",
                nameof(stripeCount)
            );
        }

        _stripes = new Channel<TEvent>[stripeCount];
        _stripeMask = stripeCount - 1;
        _enqueuedByStripe = new long[stripeCount];
        _dequeuedByStripe = new long[stripeCount];
        _droppedFullByStripe = new long[stripeCount];
        _droppedShutdownByStripe = new long[stripeCount];

        for (int index = 0; index < stripeCount; index++)
        {
            _stripes[index] = Channel.CreateBounded<TEvent>(
                new BoundedChannelOptions(stripeCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    // Dispose drains completed channels while the maintenance owner may still be
                    // reading. Keep the channel contract honest instead of relying on an implicit
                    // single-consumer lifetime rule.
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                }
            );
        }
    }

    /// <summary>
    /// Attempts to publish an event without waiting or allocating asynchronous state.
    /// </summary>
    /// <returns><see langword="true" /> when the selected stripe accepted the event.</returns>
    internal bool TryEnqueue(TEvent value)
    {
        int stripeIndex = Environment.CurrentManagedThreadId & _stripeMask;
        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Increment(ref _droppedShutdownByStripe[stripeIndex]);
            return false;
        }

        if (_stripes[stripeIndex].Writer.TryWrite(value))
        {
            Interlocked.Increment(ref _enqueuedByStripe[stripeIndex]);
            return true;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Increment(ref _droppedShutdownByStripe[stripeIndex]);
        }
        else
        {
            Interlocked.Increment(ref _droppedFullByStripe[stripeIndex]);
        }

        return false;
    }

    /// <summary>
    /// Attempts to consume one event by scanning each stripe at most once.
    /// </summary>
    /// <remarks>
    /// This method is intended for one maintenance consumer. The round-robin cursor is owned by
    /// that consumer; channel readers also permit the concurrent shutdown drain.
    /// </remarks>
    internal bool TryRead(out TEvent value)
    {
        int start = _nextReadStripe;
        for (int offset = 0; offset < _stripes.Length; offset++)
        {
            int stripeIndex = (start + offset) & _stripeMask;
            if (!_stripes[stripeIndex].Reader.TryRead(out value!))
            {
                continue;
            }

            _nextReadStripe = (stripeIndex + 1) & _stripeMask;
            Interlocked.Increment(ref _dequeuedByStripe[stripeIndex]);
            return true;
        }

        _nextReadStripe = (start + 1) & _stripeMask;
        value = default!;
        return false;
    }

    internal ReadBufferStatistics GetStatistics()
    {
        long queued = 0;
        long enqueued = 0;
        long dequeued = 0;
        long droppedFull = 0;
        long droppedShutdown = 0;
        for (int index = 0; index < _stripes.Length; index++)
        {
            Channel<TEvent> stripe = _stripes[index];
            queued += stripe.Reader.Count;
            enqueued += Interlocked.Read(ref _enqueuedByStripe[index]);
            dequeued += Interlocked.Read(ref _dequeuedByStripe[index]);
            droppedFull += Interlocked.Read(ref _droppedFullByStripe[index]);
            droppedShutdown += Interlocked.Read(ref _droppedShutdownByStripe[index]);
        }

        return new ReadBufferStatistics(
            Volatile.Read(ref _disposed) != 0,
            queued,
            enqueued,
            dequeued,
            droppedFull,
            droppedShutdown
        );
    }

    /// <summary>
    /// Stops admission and drops events still buffered at shutdown.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (Channel<TEvent> stripe in _stripes)
        {
            stripe.Writer.TryComplete();
        }

        // The channels permit this shutdown drain to race the maintenance consumer. Draining here
        // releases references even when the owner never gets another pass.
        for (int index = 0; index < _stripes.Length; index++)
        {
            while (_stripes[index].Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _droppedShutdownByStripe[index]);
            }
        }
    }
}
