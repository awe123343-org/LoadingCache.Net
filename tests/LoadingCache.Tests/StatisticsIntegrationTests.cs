using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class StatisticsIntegrationTests
{
    [Test]
    public async Task SynchronousLoadingStatisticsSeparateHitsMissesAndLoads()
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
        await Assert.That(cache.Get(7)).IsEqualTo("value-7");
        await Assert.That(cache.Get(7)).IsEqualTo("value-7");
        CacheStatistics statistics = cache.Statistics;
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(statistics.Misses).IsEqualTo(1);
        await Assert.That(statistics.Hits).IsEqualTo(1);
        await Assert.That(statistics.LoadsStarted).IsEqualTo(1);
        await Assert.That(statistics.LoadSuccesses).IsEqualTo(1);
        await Assert.That(statistics.LoadFailures).IsEqualTo(0);
        await Assert.That(statistics.TotalLoadTimeTicks).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task RemovalStatisticsIncludeCapacityAndClearCauses()
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
        await Assert.That(statistics.Evictions).IsGreaterThanOrEqualTo(1);
        await Assert.That(statistics.SizeRemovals).IsGreaterThanOrEqualTo(1);
        await Assert.That(statistics.ClearedRemovals).IsGreaterThanOrEqualTo(1);
        await Assert.That(statistics.EvictedWeight).IsGreaterThanOrEqualTo(1);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("value-1");
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("value-1");
        await Eventually(() => cache.Statistics.RefreshSuccesses >= 1);
        await Assert.That(cache.Statistics.RefreshAttempts).IsEqualTo(1);
        await Assert.That(cache.Statistics.LoadsStarted).IsEqualTo(2);
        await Assert.That(cache.Statistics.LoadSuccesses).IsEqualTo(2);
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
        await Assert.That((Func<Task>)(() => load)).ThrowsExactly<TimeoutException>();
        await Assert.That(calls).IsEqualTo(0);
        CacheStatistics statistics = cache.Statistics;
        await Assert.That(statistics.LoadsStarted).IsEqualTo(0);
        await Assert.That(statistics.LoadSuccesses).IsEqualTo(0);
        await Assert.That(statistics.LoadFailures).IsEqualTo(0);
        await Assert.That(statistics.LoadCancellations).IsEqualTo(0);
        await Assert.That(statistics.LoadTimeouts).IsEqualTo(1);
        await Assert.That(statistics.TotalLoadTimeTicks).IsEqualTo(0);
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
        await Assert.That(cache.TryGetTask(1, out Task<string>? shared)).IsTrue();
        Assert.NotNull(shared);
        Assert.NotNull(shared);
        await Assert.That(cache.Statistics.Misses).IsEqualTo(2);
        await Assert.That(cache.Statistics.LoadsStarted).IsEqualTo(1);
        release.TrySetResult("value");
        await Assert.That((await first)).IsEqualTo("value");
        await Assert.That((await shared)).IsEqualTo("value");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionException("The condition did not become true in time.");
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
