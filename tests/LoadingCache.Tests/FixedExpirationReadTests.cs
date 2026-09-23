using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class FixedExpirationReadTests
{
    [Test]
    [Arguments("write", false)]
    [Arguments("write", true)]
    [Arguments("access", false)]
    [Arguments("access", true)]
    [Arguments("both", false)]
    [Arguments("both", true)]
    public async Task ResidentReadsKeepStatisticsAndExpireAtTheExactDeadline(
        string expiration,
        bool recordStatistics
    )
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan duration = TimeSpan.FromSeconds(10);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = expiration is "write" or "both" ? duration : null,
                ExpireAfterAccess = expiration is "access" or "both" ? duration : null,
                RecordStatistics = recordStatistics,
            }
        );
        using var cache = new Cache<int, string>(engine);
        int asyncCalls = 0;
        int syncCalls = 0;
        await using var loading = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) =>
            {
                Interlocked.Increment(ref asyncCalls);
                return Task.FromResult("async loaded");
            }
        );
        cache.Put(1, "resident");
        cache.Put(2, "resident");
        cache.Put(3, "resident");
        await Assert.That((cache.Policy.RefreshAfterWrite) is null).IsTrue();
        clock.Advance(duration - TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("resident");
        await Assert.That((await loading.GetAsync(2))).IsEqualTo("resident");
        await Assert.That(cache.GetOrAdd(3, Load)).IsEqualTo("resident");
        await Assert.That(asyncCalls).IsEqualTo(0);
        await Assert.That(syncCalls).IsEqualTo(0);
        await Assert.That(cache.Statistics.Hits).IsEqualTo(recordStatistics ? 3 : 0);
        await Assert.That(cache.Statistics.Misses).IsEqualTo(0);
        await Assert.That(cache.Statistics.LoadsStarted).IsEqualTo(0);
        // Access-only entries expire from the successful reads above. A write deadline
        // remains anchored to publication even when access expiration is also enabled.
        clock.Advance(expiration == "access" ? duration : TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That((await loading.GetAsync(2))).IsEqualTo("async loaded");
        await Assert.That(cache.GetOrAdd(3, Load)).IsEqualTo("sync loaded");
        await Assert.That(asyncCalls).IsEqualTo(1);
        await Assert.That(syncCalls).IsEqualTo(1);
        CacheStatistics statistics = cache.Statistics;
        await Assert.That(statistics.Hits).IsEqualTo(recordStatistics ? 3 : 0);
        await Assert.That(statistics.Misses).IsEqualTo(recordStatistics ? 3 : 0);
        await Assert.That(statistics.LoadsStarted).IsEqualTo(recordStatistics ? 2 : 0);
        await Assert.That(statistics.LoadSuccesses).IsEqualTo(recordStatistics ? 2 : 0);
        await Assert.That(statistics.ExpiredRemovals).IsEqualTo(recordStatistics ? 3 : 0);
        return;
        string Load(int _)
        {
            Interlocked.Increment(ref syncCalls);
            return "sync loaded";
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EachResidentReadTouchesAccessTimeAndHonorsDurationChanges(bool alsoWrite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterAccess = TimeSpan.FromSeconds(10),
                ExpireAfterWrite = alsoWrite ? TimeSpan.FromMinutes(1) : null,
            }
        );
        using var cache = new Cache<int, string>(engine);
        await using var loading = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) =>
                throw new InvalidOperationException("A resident read invoked its loader.")
        );
        cache.Put(1, "resident");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("resident");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.That((await loading.GetAsync(1))).IsEqualTo("resident");
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert
            .That(
                cache.GetOrAdd(
                    1,
                    static _ =>
                        throw new InvalidOperationException("A resident read invoked its factory.")
                )
            )
            .IsEqualTo("resident");
        await Assert.That(cache.Policy.ExpireAfterAccess!.AgeOf(1)).IsEqualTo(TimeSpan.Zero);
        cache.Policy.ExpireAfterAccess.SetDuration(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    [Arguments("write")]
    [Arguments("access")]
    [Arguments("both")]
    public async Task CustomTimestampFrequencyPreservesSubTickExpiration(string expiration)
    {
        var clock = new SubTickTimeProvider();
        var builder = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock);
        if (expiration is "write" or "both")
        {
            builder.ExpireAfterWrite(TimeSpan.FromTicks(1));
        }

        if (expiration is "access" or "both")
        {
            builder.ExpireAfterAccess(TimeSpan.FromTicks(1));
        }

        using ICache<int, string> cache = builder.Build();
        cache.Put(1, "resident");
        // Three provider timestamp units make one TimeSpan tick. An elapsed
        // fraction of a tick must retain TimeProvider.GetElapsedTime rounding.
        clock.SetTimestamp(2);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("resident");
        clock.SetTimestamp(expiration == "access" ? 5 : 3);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task BackwardCustomTimestampDoesNotMoveAccessTime(int backwardUnits)
    {
        var clock = new SubTickTimeProvider();
        clock.SetTimestamp(30);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .ExpireAfterAccess(TimeSpan.FromTicks(2))
            .Build();
        cache.Put(1, "resident");
        clock.SetTimestamp(30 - backwardUnits);
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        // Neither a fraction of a negative TimeSpan tick nor a whole negative
        // tick may move the original access timestamp backwards. Quiet lookup
        // observes freshness without moving the deadline before the final read.
        clock.SetTimestamp(35);
        await Assert.That(cache.Policy.TryGetQuietly(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("resident");
        clock.SetTimestamp(36);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    private sealed class SubTickTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond * 3;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void SetTimestamp(long timestamp) => Volatile.Write(ref _timestamp, timestamp);
    }
}
