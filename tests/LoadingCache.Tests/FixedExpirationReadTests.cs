using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class FixedExpirationReadTests
{
    [TestCase("write", false)]
    [TestCase("write", true)]
    [TestCase("access", false)]
    [TestCase("access", true)]
    [TestCase("both", false)]
    [TestCase("both", true)]
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
        string Load(int _)
        {
            Interlocked.Increment(ref syncCalls);
            return "sync loaded";
        }

        cache.Put(1, "resident");
        cache.Put(2, "resident");
        cache.Put(3, "resident");
        cache.Policy.RefreshAfterWrite.Should().BeNull();
        clock.Advance(duration - TimeSpan.FromTicks(1));

        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
        (await loading.GetAsync(2)).Should().Be("resident");
        cache.GetOrAdd(3, Load).Should().Be("resident");
        asyncCalls.Should().Be(0);
        syncCalls.Should().Be(0);
        cache.Statistics.Hits.Should().Be(recordStatistics ? 3 : 0);
        cache.Statistics.Misses.Should().Be(0);
        cache.Statistics.LoadsStarted.Should().Be(0);

        // Access-only entries expire from the successful reads above. A write deadline
        // remains anchored to publication even when access expiration is also enabled.
        clock.Advance(expiration == "access" ? duration : TimeSpan.FromTicks(1));
        cache.TryGet(1, out _).Should().BeFalse();
        (await loading.GetAsync(2)).Should().Be("async loaded");
        cache.GetOrAdd(3, Load).Should().Be("sync loaded");

        asyncCalls.Should().Be(1);
        syncCalls.Should().Be(1);
        CacheStatistics statistics = cache.Statistics;
        statistics.Hits.Should().Be(recordStatistics ? 3 : 0);
        statistics.Misses.Should().Be(recordStatistics ? 3 : 0);
        statistics.LoadsStarted.Should().Be(recordStatistics ? 2 : 0);
        statistics.LoadSuccesses.Should().Be(recordStatistics ? 2 : 0);
        statistics.ExpiredRemovals.Should().Be(recordStatistics ? 3 : 0);
    }

    [TestCase(false)]
    [TestCase(true)]
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
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
        clock.Advance(TimeSpan.FromSeconds(9));
        (await loading.GetAsync(1)).Should().Be("resident");
        clock.Advance(TimeSpan.FromSeconds(9));
        cache
            .GetOrAdd(
                1,
                static _ =>
                    throw new InvalidOperationException("A resident read invoked its factory.")
            )
            .Should()
            .Be("resident");

        cache.Policy.ExpireAfterAccess!.AgeOf(1).Should().Be(TimeSpan.Zero);
        cache.Policy.ExpireAfterAccess.SetDuration(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [TestCase("write")]
    [TestCase("access")]
    [TestCase("both")]
    public void CustomTimestampFrequencyPreservesSubTickExpiration(string expiration)
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
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
        clock.SetTimestamp(expiration == "access" ? 5 : 3);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [TestCase(1)]
    [TestCase(4)]
    public void BackwardCustomTimestampDoesNotMoveAccessTime(int backwardUnits)
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
        cache.TryGet(1, out _).Should().BeTrue();
        // Neither a fraction of a negative TimeSpan tick nor a whole negative
        // tick may move the original access timestamp backwards. Quiet lookup
        // observes freshness without moving the deadline before the final read.
        clock.SetTimestamp(35);
        cache.Policy.TryGetQuietly(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
        clock.SetTimestamp(36);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    private sealed class SubTickTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond * 3;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        internal void SetTimestamp(long timestamp) => Volatile.Write(ref _timestamp, timestamp);
    }
}
