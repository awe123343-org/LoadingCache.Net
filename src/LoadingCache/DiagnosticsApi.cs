using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>Explains why a resident value was removed from a cache.</summary>
[PublicAPI]
public enum RemovalCause
{
    /// <summary>The caller explicitly invalidated the entry.</summary>
    Explicit,

    /// <summary>A newer value replaced the entry.</summary>
    Replaced,

    /// <summary>The entry reached its configured expiration deadline.</summary>
    Expired,

    /// <summary>The size bound selected the entry for removal.</summary>
    Size,

    /// <summary>The weight bound selected the entry for removal.</summary>
    Weight,

    /// <summary>The garbage collector collected a weak key or value.</summary>
    Collected,

    /// <summary>The cache was cleared.</summary>
    Cleared,

    /// <summary>Memory-pressure policy selected the entry for removal.</summary>
    MemoryPressure,
}

/// <summary>
/// Describes one logical removal of a resident value.
/// </summary>
/// <remarks>
/// A value-version produces at most one removal notification. Weak-reference
/// collection notifications may have a <see langword="null"/> key and value;
/// listeners must not assume that either object remains strongly reachable.
/// </remarks>
[PublicAPI]
public readonly record struct RemovalNotification<TKey, TValue>(
    TKey? Key,
    TValue? Value,
    RemovalCause Cause,
    long Weight
)
    where TKey : notnull
    where TValue : notnull;

/// <summary>
/// Names the bounded instruments emitted by the cache engine through
/// <see cref="System.Diagnostics.Metrics"/>.
/// </summary>
[PublicAPI]
public static class CacheMetrics
{
    /// <summary>The meter name used by this library.</summary>
    public const string MeterName = "LoadingCache";

    /// <summary>The meter version exposed by this library.</summary>
    public const string MeterVersion = "0.1";
}

/// <summary>Diagnostics for a bounded listener dispatcher.</summary>
[PublicAPI]
public readonly record struct CacheNotificationStatistics(
    int Queued,
    bool IsDisposed,
    bool HandlerRunning,
    long Enqueued,
    long Invoked,
    long Delivered,
    long HandlerFailures,
    long DroppedFull,
    long DroppedSchedule,
    long DroppedShutdown,
    long ScheduleRejections
)
{
    /// <summary>Gets the total number of dropped notifications.</summary>
    public long Dropped
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
