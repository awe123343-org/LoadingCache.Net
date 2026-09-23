using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class SynchronousEvictionRaceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ConcurrentRemovalsEachWaitForTheirOwnCallback()
    {
        await using var first = new BlockingTestHook(Timeout);
        await using var second = new BlockingTestHook(Timeout);
        Action pauseFirst = first.Invoke;
        Action pauseSecond = second.Invoke;
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
                (notification.Key == 1 ? pauseFirst : pauseSecond)();
            })
            .Build();
        Task firstPut = Task.Factory.StartNew(
            static state => ((ICache<int, int>)state!).Put(1, 1),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        Task secondPut = Task.CompletedTask;
        try
        {
            await first.Entered.WaitAsync(Timeout);
            secondPut = Task.Factory.StartNew(
                static state => ((ICache<int, int>)state!).Put(2, 2),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await second.Entered.WaitAsync(Timeout);
            await Assert.That(firstPut.IsCompleted).IsFalse();
            await Assert.That(secondPut.IsCompleted).IsFalse();
            first.Release();
            await firstPut.WaitAsync(Timeout);
            await Assert.That(secondPut.IsCompleted).IsFalse();
            await Task.Run(cache.Dispose).WaitAsync(Timeout);
            await Assert.That(secondPut.IsCompleted).IsFalse();
        }
        finally
        {
            first.Release();
            second.Release();
            await Task.WhenAll(firstPut, secondPut).WaitAsync(Timeout);
        }

        await Assert.That(notifications).IsEquivalentTo([1, 2]);
        await Assert.That(first.TimedOut).IsFalse();
        await Assert.That(second.TimedOut).IsFalse();
    }

    [Test]
    public async Task EvictionCallbackCanWaitForAnotherThreadToMutateTheCache()
    {
        var notifications = new ConcurrentQueue<int>();
        var mutation = new ReentrantMutation();
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(notification =>
            {
                notifications.Enqueue(notification.Key);
                if (notification.Key != 1)
                {
                    return;
                }

                mutation.Invoke();
            })
            .Build();
        mutation.Cache = cache;
        cache.Put(1, 1);
        await mutation.Pending.WaitAsync(Timeout);
        await Assert
            .That(mutation.Finished)
            .IsTrue()
            .Because("no engine, entry, policy, or timer lock may surround the callback");
        await Assert.That(notifications).IsEquivalentTo([1, 2]);
    }

    [Test]
    public async Task ColdLoadPromiseWaitsForEvictionCallback()
    {
        await using var callback = new BlockingTestHook(Timeout);
        Action pauseCallback = callback.Invoke;
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
                pauseCallback();
            })
            .BuildAsyncLoading((_, _) => result.Task);
        Task<int> waiting = cache.GetAsync(1).AsTask();
        result.SetResult(1);
        try
        {
            await callback.Entered.WaitAsync(Timeout);
            await Assert.That(waiting.IsCompleted).IsFalse();
        }
        finally
        {
            callback.Release();
            await Assert.That((await waiting.WaitAsync(Timeout))).IsEqualTo(1);
        }

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(callback.TimedOut).IsFalse();
    }

    [Test]
    public async Task BulkPromisesWaitForRequestedAndPrefetchedEvictionCallbacks()
    {
        await using var callback = new BlockingTestHook(Timeout);
        Action pauseCallback = callback.Invoke;
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
                pauseCallback();
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
            await Assert.That(waiting.IsCompleted).IsFalse();
        }
        finally
        {
            callback.Release();
            await Assert.That((await waiting.WaitAsync(Timeout)).Count).IsEqualTo(2);
        }

        await Assert.That(notifications).IsEquivalentTo([1, 2, 3]);
        await Assert.That(callback.TimedOut).IsFalse();
    }

    [Test]
    public async Task DisposeCompletesBulkWaitersWhileEvictionListenerIsBlocked()
    {
        var callback = new BlockingTestHook(Timeout);
        var loader = new GatedBulkLoader();
        var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(_ => callback.Invoke())
            .BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> bulk = cache.GetAllAsync([1, 2]).AsTask();
        Task<int> leader = cache.GetAsync(1).AsTask();
        Task<int> joined = cache.GetAsync(2).AsTask();
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
            await Assert.That(leader.IsCompleted).IsFalse();
            await Assert.That(joined.IsCompleted).IsFalse();
            await cache.DisposeAsync().AsTask().WaitAsync(Timeout);
            await Assert
                .That(leader.IsCompleted)
                .IsTrue()
                .Because("shutdown must end pending shared promises");
            await Assert
                .That(joined.IsCompleted)
                .IsTrue()
                .Because("a nonleader shares the same bulk outcome");
            await Assert.That(() => leader).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(() => joined).ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That((Func<Task>)(() => bulk.WaitAsync(Timeout)))
                .ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That(callback.Returned.IsCompleted)
                .IsFalse()
                .Because("shutdown cannot wait for user callbacks");
        }
        finally
        {
            callback.Release();
            try
            {
                foreach (Task pending in new Task[] { leader, joined, bulk })
                {
                    await ObserveShutdownAsync(pending);
                }
            }
            finally
            {
                await cache.DisposeAsync();
                await callback.DisposeAsync();
            }
        }

        await Assert.That(callback.TimedOut).IsFalse();
    }

    [Test]
    public async Task DisposeCompletesSyncBulkJoinerWhileEvictionListenerIsBlocked()
    {
        var backend = new BlockingTestHook(Timeout);
        var callback = new BlockingTestHook(Timeout);
        var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .Weigher(static (_, _) => 2)
            .EvictionListener(_ => callback.Invoke())
            .RecordStatistics()
            .BuildLoading(new GatedSyncBulkLoader(backend));
        Task<IReadOnlyDictionary<int, int>> bulk = Task.Factory.StartNew(
            static state => ((ILoadingCache<int, int>)state!).GetAll([1, 2]),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        Task<int> joined = Task.FromResult(0);
        try
        {
            await backend.Entered.WaitAsync(Timeout);
            joined = Task.Factory.StartNew(
                static state => ((ILoadingCache<int, int>)state!).Get(2),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            WaitForSyncJoin(cache);
            backend.Release();
            await callback.Entered.WaitAsync(Timeout);
            await Task.Run(cache.Dispose).WaitAsync(Timeout);
            await Assert
                .That(() => joined.WaitAsync(Timeout))
                .ThrowsExactly<ObjectDisposedException>();
            await Assert.That(callback.Returned.IsCompleted).IsFalse();
            await Assert
                .That(bulk.IsCompleted)
                .IsFalse()
                .Because("the owner is still executing user callback code");
        }
        finally
        {
            backend.Release();
            callback.Release();
            try
            {
                await ObserveShutdownAsync(joined);
                await ObserveShutdownAsync(bulk);
            }
            finally
            {
                cache.Dispose();
                await callback.DisposeAsync();
                await backend.DisposeAsync();
            }
        }

        await Assert.That(backend.TimedOut).IsFalse();
        await Assert.That(callback.TimedOut).IsFalse();
    }

    [Test]
    public async Task RuntimeShrinkDoesNotDropEvictionsAtNotificationCapacity()
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
        await Assert.That(notifications.Count).IsEqualTo(99);
        await Assert.That(notifications.Distinct().Count()).IsEqualTo(99);
    }

    [Test]
    public async Task PromptExpirationCallbackRunsOutsideTimerCoordination()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var mutation = new ReentrantMutation();
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
                mutation.Invoke();
            })
            .Build();
        mutation.Cache = cache;
        cache.Put(1, 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await mutation.Pending.WaitAsync(Timeout);
        await Assert.That(mutation.Finished).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(cache.TryGet(2, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(2);
    }

    private sealed class ReentrantMutation
    {
        internal ICache<int, int> Cache { private get; set; } = null!;
        internal Task Pending { get; private set; } = Task.CompletedTask;
        internal bool Finished { get; private set; }

        internal void Invoke()
        {
            Pending = Task.Run(Put);
            Finished = Pending.Wait(Timeout);
        }

        private void Put() => Cache.Put(2, 2);
    }

    private static void WaitForSyncJoin(ILoadingCache<int, int> cache)
    {
        if (!(SpinWait.SpinUntil(() => cache.Statistics.CoalescedWaiters == 1, Timeout)))
            Assert.Fail("Timed out waiting for the controlled condition.");
    }

    private static async Task ObserveShutdownAsync(Task pending)
    {
        try
        {
            await pending.WaitAsync(Timeout);
        }
        catch (ObjectDisposedException)
        {
            // Only shutdown is an expected failure when releasing the controlled callbacks.
        }
    }

    private sealed class GatedSyncBulkLoader(BlockingTestHook backend)
        : IBulkSyncCacheLoader<int, int>
    {
        public int Load(int key) =>
            throw new InvalidOperationException($"Unexpected single load {key}.");

        public IReadOnlyDictionary<int, int> LoadAll(IReadOnlyCollection<int> keys)
        {
            backend.Invoke();
            return new Dictionary<int, int> { [1] = 1, [2] = 2 };
        }
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
