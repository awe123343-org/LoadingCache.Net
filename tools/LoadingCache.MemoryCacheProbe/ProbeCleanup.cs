using System.Diagnostics;

namespace LoadingCache.MemoryCacheProbe;

internal static class ProbeCleanup
{
    internal static int Drain<TKey, TValue>(ICache<TKey, TValue> cache, TimeSpan watchdog)
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchdog, TimeSpan.Zero);
        var eviction =
            cache.Policy.Eviction
            ?? throw new ArgumentException("A bounded cache is required.", nameof(cache));
        long started = Stopwatch.GetTimestamp();
        var wait = new SpinWait();
        int passes = 0;
        while (true)
        {
            passes++;
            cache.CleanUp();
            if (
                cache.Statistics.MaintenanceBacklog == 0
                && eviction.WeightedSize <= eviction.Maximum
            )
                return passes;
            if (Stopwatch.GetElapsedTime(started) >= watchdog)
            {
                throw new InvalidOperationException(
                    $"LoadingCache cleanup did not converge within {watchdog}: "
                        + $"backlog={cache.Statistics.MaintenanceBacklog}, "
                        + $"count={cache.EstimatedCount}, "
                        + $"weighted={eviction.WeightedSize}."
                );
            }
            // CleanUp may defer to an existing worker. Attempts are not elapsed time.
            wait.SpinOnce();
        }
    }
}
