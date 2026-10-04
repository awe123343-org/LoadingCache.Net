using System.Diagnostics;

namespace LoadingCache.Expiration;

// This is only a read-check clock, never the engine's TimeProvider. A stalled
// ticker may delay TTL detection indefinitely; exact timers and writes keep running.
internal sealed class CoarseExpirationClock(long initialTimestamp)
{
    private static readonly Lazy<CoarseExpirationClock> SharedInstance = new(CreateShared);
    private long _timestamp = initialTimestamp;

    internal static CoarseExpirationClock Shared => SharedInstance.Value;

    internal long GetTimestamp() => Volatile.Read(ref _timestamp);

    // Production has one writer. Tests can advance their own threadless instance.
    internal void Advance(long timestamp)
    {
        if (timestamp > _timestamp)
        {
            Volatile.Write(ref _timestamp, timestamp);
        }
    }

    private static CoarseExpirationClock CreateShared()
    {
        var clock = new CoarseExpirationClock(Stopwatch.GetTimestamp());
        var ticker = new Thread(clock.Run)
        {
            IsBackground = true,
            Name = "LoadingCache.CoarseExpiration",
        };
        // The process-lifetime thread must not retain the creating request's context.
        ticker.UnsafeStart();
        return clock;
    }

    // Keep this process-lifetime background loop: caches share one clock without per-cache timers or owner retention.
    // ReSharper disable once FunctionNeverReturns
    private void Run()
    {
        while (true)
        {
            Thread.Sleep(1);
            Advance(Stopwatch.GetTimestamp());
        }
    }
}
