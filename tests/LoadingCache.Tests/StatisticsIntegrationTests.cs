using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class StatisticsIntegrationTests
{
    [Test]
    public void SynchronousLoadingStatisticsSeparateHitsMissesAndLoads()
    {
        int calls = 0;
        using ILoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .RecordStatistics()
            .BuildLoading(key =>
            {
                Interlocked.Increment(ref calls);
                return $"value-{key}";
            });
        cache.Get(7).Should().Be("value-7");
        cache.Get(7).Should().Be("value-7");
        CacheStatistics statistics = cache.Statistics;
        calls.Should().Be(1);
        statistics.Misses.Should().Be(1);
        statistics.Hits.Should().Be(1);
        statistics.LoadsStarted.Should().Be(1);
        statistics.LoadSuccesses.Should().Be(1);
        statistics.LoadFailures.Should().Be(0);
        statistics.TotalLoadTimeTicks.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void RemovalStatisticsIncludeCapacityAndClearCauses()
    {
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(1)
            .MaxConcurrentLoads(4)
            .RecordStatistics()
            .Build();
        cache.Put(1, "one");
        cache.Put(2, "two");
        cache.CleanUp();
        cache.Clear();
        CacheStatistics statistics = cache.Statistics;
        statistics.Evictions.Should().BeGreaterThanOrEqualTo(1);
        statistics.SizeRemovals.Should().BeGreaterThanOrEqualTo(1);
        statistics.ClearedRemovals.Should().BeGreaterThanOrEqualTo(1);
        statistics.EvictedWeight.Should().BeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task RefreshStatisticsCountSharedRefreshOutcome()
    {
        int calls = 0;
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .RecordStatistics()
            .BuildAsyncLoading(
                (_, _) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    return Task.FromResult($"value-{call}");
                }
            );
        (await cache.GetAsync(1)).Should().Be("value-1");
        time.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("value-1");
        await Eventually(() => cache.Statistics.RefreshSuccesses >= 1);
        cache.Statistics.RefreshAttempts.Should().Be(1);
        cache.Statistics.LoadsStarted.Should().Be(2);
        cache.Statistics.LoadSuccesses.Should().Be(2);
    }

    [Test]
    public async Task TimeoutBeforeLoaderInvocationDoesNotCountAsStartedLoad()
    {
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(new ImmediateTimeoutProvider())
            .RecordStatistics()
            .BuildAsyncLoading(
                (_, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult("never");
                }
            );
        Task<string> load = cache.GetAsync(1).AsTask();
        await FluentActions.Awaiting(() => load).Should().ThrowExactlyAsync<TimeoutException>();
        calls.Should().Be(0);
        CacheStatistics statistics = cache.Statistics;
        statistics.LoadsStarted.Should().Be(0);
        statistics.LoadSuccesses.Should().Be(0);
        statistics.LoadFailures.Should().Be(0);
        statistics.LoadCancellations.Should().Be(0);
        statistics.LoadTimeouts.Should().Be(1);
        statistics.TotalLoadTimeTicks.Should().Be(0);
    }

    [Test]
    public async Task PendingTryGetTaskCountsMissWithoutStartingAnotherLoad()
    {
        var loaderStarted = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RecordStatistics()
            .BuildAsyncLoading(
                (_, _) =>
                {
                    loaderStarted.TrySetResult(null);
                    return release.Task;
                }
            );
        Task<string> first = cache.GetAsync(1).AsTask();
        await loaderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.TryGetTask(1, out Task<string>? shared).Should().BeTrue();
        shared.Should().NotBeNull();
        cache.Statistics.Misses.Should().Be(2);
        cache.Statistics.LoadsStarted.Should().Be(1);
        release.TrySetResult("value");
        (await first).Should().Be("value");
        (await shared).Should().Be("value");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionFailedException("The condition did not become true in time.");
            }

            await Task.Yield();
        }
    }

    private sealed class ImmediateTimeoutProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return new ImmediateTimer();
            }

            Interlocked.Add(ref _timestamp, dueTime.Ticks);
            callback(state);
            return new ImmediateTimer();
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
