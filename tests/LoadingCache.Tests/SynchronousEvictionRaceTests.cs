using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class SynchronousEvictionRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ConcurrentRemovalsEachWaitForTheirOwnCallback()
    {
        await using var first = new BlockingTestHook(Timeout);
        await using var second = new BlockingTestHook(Timeout);
        var notifications = new ConcurrentQueue<int>();
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(notification =>
            {
                notifications.Enqueue(notification.Key);
                (notification.Key == 1 ? first : second).Invoke();
            })
            .Build();
        Task firstPut = Task.Run(() => cache.Put(1, 1));
        Task secondPut = Task.CompletedTask;
        try
        {
            await first.Entered.WaitAsync(Timeout);
            secondPut = Task.Run(() => cache.Put(2, 2));
            await second.Entered.WaitAsync(Timeout);
            firstPut.IsCompleted.Should().BeFalse();
            secondPut.IsCompleted.Should().BeFalse();
            first.Release();
            await firstPut.WaitAsync(Timeout);
            secondPut.IsCompleted.Should().BeFalse();
            await Task.Run(cache.Dispose).WaitAsync(Timeout);
            secondPut.IsCompleted.Should().BeFalse();
        }
        finally
        {
            first.Release();
            second.Release();
            await Task.WhenAll(firstPut, secondPut).WaitAsync(Timeout);
        }
        notifications.Should().BeEquivalentTo([1, 2]);
        first.TimedOut.Should().BeFalse();
        second.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task EvictionCallbackCanWaitForAnotherThreadToMutateTheCache()
    {
        var notifications = new ConcurrentQueue<int>();
        ICache<int, int>? current = null;
        Task nested = Task.CompletedTask;
        bool nestedFinished = false;
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(notification =>
            {
                notifications.Enqueue(notification.Key);
                if (notification.Key == 1)
                {
                    nested = Task.Run(() => current!.Put(2, 2));
                    nestedFinished = nested.Wait(Timeout);
                }
            })
            .Build();
        current = cache;
        cache.Put(1, 1);
        await nested.WaitAsync(Timeout);
        nestedFinished
            .Should()
            .BeTrue("no engine, entry, policy, or timer lock may surround the callback");
        notifications.Should().BeEquivalentTo([1, 2]);
    }

    [Test]
    public async Task ColdLoadPromiseWaitsForEvictionCallback()
    {
        await using var callback = new BlockingTestHook(Timeout);
        var result = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        await using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(_ =>
            {
                Interlocked.Increment(ref calls);
                callback.Invoke();
            })
            .BuildAsyncLoading((_, _) => result.Task);
        Task<int> waiting = cache.GetAsync(1).AsTask();
        result.SetResult(1);
        try
        {
            await callback.Entered.WaitAsync(Timeout);
            waiting.IsCompleted.Should().BeFalse();
        }
        finally
        {
            callback.Release();
            (await waiting.WaitAsync(Timeout)).Should().Be(1);
        }
        calls.Should().Be(1);
        callback.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task BulkPromisesWaitForRequestedAndPrefetchedEvictionCallbacks()
    {
        await using var callback = new BlockingTestHook(Timeout);
        var loader = new GatedBulkLoader();
        var notifications = new ConcurrentQueue<int>();
        await using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .EvictionListener(notification =>
            {
                notifications.Enqueue(notification.Key);
                callback.Invoke();
            })
            .BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> waiting = cache.GetAllAsync([1, 2]).AsTask();
        loader.Result.SetResult(
            new Dictionary<int, int>
            {
                [1] = 1,
                [2] = 2,
                [3] = 3,
            }
        );
        try
        {
            await callback.Entered.WaitAsync(Timeout);
            waiting.IsCompleted.Should().BeFalse();
        }
        finally
        {
            callback.Release();
            (await waiting.WaitAsync(Timeout)).Count.Should().Be(2);
        }
        notifications.Should().BeEquivalentTo([1, 2, 3]);
        callback.TimedOut.Should().BeFalse();
    }

    [Test]
    public void RuntimeShrinkDoesNotDropEvictionsAtNotificationCapacity()
    {
        var notifications = new ConcurrentQueue<int>();
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(100)
            .MaxConcurrentLoads(4)
            .NotificationCapacity(1)
            .EvictionListener(notification => notifications.Enqueue(notification.Key))
            .Build();
        for (int key = 0; key < 100; key++)
        {
            cache.Put(key, key);
        }
        cache.Policy.Eviction!.SetMaximum(1);
        notifications.Count.Should().Be(99);
        notifications.Distinct().Count().Should().Be(99);
    }

    [Test]
    public async Task PromptExpirationCallbackRunsOutsideTimerCoordination()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        ICache<int, int>? current = null;
        Task nested = Task.CompletedTask;
        bool nestedFinished = false;
        int calls = 0;
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(10)
            .MaxConcurrentLoads(4)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .EnableExpirationScheduler()
            .EvictionListener(_ =>
            {
                Interlocked.Increment(ref calls);
                nested = Task.Run(() => current!.Put(2, 2));
                nestedFinished = nested.Wait(Timeout);
            })
            .Build();
        current = cache;
        cache.Put(1, 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await nested.WaitAsync(Timeout);
        nestedFinished.Should().BeTrue();
        calls.Should().Be(1);
        cache.TryGet(2, out int value).Should().BeTrue();
        value.Should().Be(2);
    }

    private sealed class GatedBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromResult(key);

        public Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        ) => Result.Task;
    }
}
