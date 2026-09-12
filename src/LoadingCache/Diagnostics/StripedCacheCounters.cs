namespace LoadingCache.Diagnostics;

/// <summary>
/// The opt-in counters maintained by <see cref="StripedCacheCounters"/>.
/// </summary>
internal enum CacheCounterKind : byte
{
    Hits,
    Misses,
    TotalLoadTimeTicks,
    RefreshSuccesses,
    EvictedWeight,
    ListenerFailures,
    Count,
}

/// <summary>
/// A bounded, fixed-stripe counter set for an enabled cache statistics path.
/// </summary>
/// <remarks>
/// The owner must not instantiate this type when statistics are disabled. Each
/// increment updates one fixed stripe and uses a saturating CAS loop, so the
/// hot path has no global counter and no per-event allocation. Snapshot values
/// are independent point-in-time reads and may be weakly consistent with one
/// another.
/// </remarks>
internal sealed class StripedCacheCounters
{
    private const int MaximumStripeCount = 64;

    private readonly long[][] _stripes;
    private readonly int _stripeMask;

    internal StripedCacheCounters(int? stripeCount = null)
    {
        int count = stripeCount ?? NormalizeStripeCount(Environment.ProcessorCount);
        ValidateStripeCount(count);

        _stripes = new long[count][];
        for (int index = 0; index < count; index++)
        {
            _stripes[index] = new long[(int)CacheCounterKind.Count];
        }

        _stripeMask = count - 1;
    }

    /// <summary>Gets the fixed number of counter stripes.</summary>
    internal int StripeCount => _stripes.Length;

    /// <summary>
    /// Gets the bounded number of counter slots allocated by this instance.
    /// </summary>
    internal int CounterSlotCount => _stripes.Length * (int)CacheCounterKind.Count;

    /// <summary>
    /// Adds a non-negative delta to a counter selected by the current managed thread.
    /// </summary>
    internal void Add(CacheCounterKind counter, long delta = 1)
    {
        ValidateCounter(counter);
        ValidateDelta(delta);
        if (delta == 0)
        {
            return;
        }

        AddToStripe(SelectStripe(Environment.CurrentManagedThreadId), counter, delta);
    }

    /// <summary>
    /// Reads all counters with saturating aggregation.
    /// </summary>
    internal CacheCounterSnapshot Snapshot()
    {
        long[] values = new long[(int)CacheCounterKind.Count];
        for (int counter = 0; counter < (int)CacheCounterKind.Count; counter++)
        {
            long total = 0;
            foreach (long[] stripe in _stripes)
            {
                total = SaturatingAdd(total, Volatile.Read(ref stripe[counter]));
                if (total == long.MaxValue)
                {
                    break;
                }
            }

            values[counter] = total;
        }

        return new CacheCounterSnapshot(values);
    }

    /// <summary>
    /// Adds to an explicit stripe for deterministic primitive tests.
    /// </summary>
    /// <remarks>
    /// Engine code must use <see cref="Add"/>. This seam avoids relying on OS
    /// thread scheduling when testing aggregate saturation.
    /// </remarks>
    internal void AddToStripeForTesting(int stripe, CacheCounterKind counter, long delta = 1)
    {
        ValidateCounter(counter);
        ValidateDelta(delta);
        if ((uint)stripe >= (uint)_stripes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(stripe));
        }

        if (delta != 0)
        {
            AddToStripe(stripe, counter, delta);
        }
    }

    /// <summary>
    /// Rounds the processor count to a bounded power-of-two stripe count.
    /// </summary>
    internal static int NormalizeStripeCount(int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processorCount);

        int count = 1;
        while (count < processorCount && count < MaximumStripeCount)
        {
            count <<= 1;
        }

        return Math.Min(count, MaximumStripeCount);
    }

    private static int SelectStripe(int managedThreadId, int stripeMask)
    {
        return unchecked((int)(uint)managedThreadId) & stripeMask;
    }

    private int SelectStripe(int managedThreadId)
    {
        return SelectStripe(managedThreadId, _stripeMask);
    }

    private void AddToStripe(int stripe, CacheCounterKind counter, long delta)
    {
        long[] values = _stripes[stripe];
        int index = (int)counter;
        while (true)
        {
            long current = Volatile.Read(ref values[index]);
            if (current == long.MaxValue)
            {
                return;
            }

            long available = long.MaxValue - current;
            long next = delta >= available ? long.MaxValue : current + delta;
            if (Interlocked.CompareExchange(ref values[index], next, current) == current)
            {
                return;
            }
        }
    }

    private static long SaturatingAdd(long left, long right)
    {
        long available = long.MaxValue - left;
        return right >= available ? long.MaxValue : left + right;
    }

    private static void ValidateCounter(CacheCounterKind counter)
    {
        if ((uint)counter >= (uint)CacheCounterKind.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(counter));
        }
    }

    private static void ValidateDelta(long delta)
    {
        if (delta < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Counter deltas must be non-negative."
            );
        }
    }

    private static void ValidateStripeCount(int stripeCount)
    {
        if (
            stripeCount <= 0
            || stripeCount > MaximumStripeCount
            || (stripeCount & (stripeCount - 1)) != 0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(stripeCount),
                stripeCount,
                "Stripe count must be a positive power of two no greater than 64."
            );
        }
    }
}

/// <summary>
/// A weakly consistent snapshot of opt-in cache counters.
/// </summary>
internal readonly struct CacheCounterSnapshot
{
    private readonly long[]? _values;

    internal CacheCounterSnapshot(long[] values)
    {
        _values = values;
    }

    /// <summary>Gets a counter value, or zero for a default snapshot.</summary>
    internal long this[CacheCounterKind counter]
    {
        get
        {
            if ((uint)counter >= (uint)CacheCounterKind.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(counter));
            }

            return _values is null ? 0 : _values[(int)counter];
        }
    }
}
