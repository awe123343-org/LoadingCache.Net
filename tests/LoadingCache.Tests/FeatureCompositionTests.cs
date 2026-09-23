using JetBrains.Annotations;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class FeatureCompositionTests
{
    [Test]
    public async Task OneHundredBulkCallersCountKeysButShareOneBackendOperation()
    {
        var loader = new GatedBulkLoader();
        await using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .RecordStatistics()
            .BuildAsyncLoading(loader);
        var allJoined = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var joined = new System.Runtime.CompilerServices.StrongBox<int>();
        var callers = new Task<IReadOnlyDictionary<int, int>>[100];
        for (int index = 0; index < callers.Length; index++)
        {
            callers[index] = Task
                .Factory.StartNew(
                    static async state =>
                    {
                        (
                            IAsyncLoadingCache<int, int> current,
                            System.Runtime.CompilerServices.StrongBox<int> count,
                            TaskCompletionSource signal
                        ) = ((
                            IAsyncLoadingCache<int, int>,
                            System.Runtime.CompilerServices.StrongBox<int>,
                            TaskCompletionSource
                        ))
                            state!;
                        Task<IReadOnlyDictionary<int, int>> result = current
                            .GetAllAsync([1, 2])
                            .AsTask();
                        if (Interlocked.Increment(ref count.Value) == 100)
                            signal.TrySetResult();
                        return await result.ConfigureAwait(false);
                    },
                    (cache, joined, allJoined),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default
                )
                .Unwrap();
        }

        IReadOnlyDictionary<int, int>[] results;
        try
        {
            await allJoined.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            CacheStatistics during = cache.GetStatistics();
            await Assert.That(during.Misses).IsEqualTo(200);
            await Assert.That(during.LoadsStarted).IsEqualTo(1);
            await Assert.That(during.BulkLoads).IsEqualTo(1);
            await Assert.That(during.CoalescedWaiters).IsEqualTo(198);
            await Assert.That(loader.Calls).IsEqualTo(1);
        }
        finally
        {
            loader.Result.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });
            results = await Task.WhenAll(callers)
                .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        await Assert.That(results).All(result => result[1] == 10 && result[2] == 20);
        await Assert.That(cache.GetStatistics().LoadSuccesses).IsEqualTo(1);
    }

    [Test]
    public async Task WeakDictionaryConditionalOperationsUseValueIdentity()
    {
        using var cache = CacheBuilder
            .Create<int, EqualValue>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .WeakValues()
            .Build();
        var original = new EqualValue(1);
        var equal = new EqualValue(1);
        var replacement = new EqualValue(2);
        cache.Put(1, original);
        var view = cache.AsDictionary();
        await Assert.That(view.TryUpdate(1, replacement, equal)).IsFalse();
        await Assert.That(view.TryRemove(1, equal)).IsFalse();
        await Assert
            .That(
                ((ICollection<KeyValuePair<int, EqualValue>>)view).Contains(
                    new KeyValuePair<int, EqualValue>(1, equal)
                )
            )
            .IsFalse();
        await Assert.That(view.TryUpdate(1, replacement, original)).IsTrue();
        await Assert.That(view.TryRemove(1, replacement)).IsTrue();
        GC.KeepAlive(original);
        GC.KeepAlive(equal);
        GC.KeepAlive(replacement);
    }

    [Test]
    public async Task PressureEvictionNotifiesForLiveWeakTargets()
    {
        var time = new FakeTimeProvider();
        var removal = new TaskCompletionSource<RemovalNotification<object, object>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = CacheBuilder
            .Create<object, object>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .WeakKeys()
            .WeakValues()
            .TimeProvider(time)
            .MemoryPressureSource(new HighPressureSource())
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), trimFraction: 1, maximumTrimCount: 1)
            .RecordStatistics()
            .NotificationCapacity(4)
            .EvictionListener(notification => removal.TrySetResult(notification))
            .Build();
        object key = new();
        object value = new();
        cache.Put(key, value);
        time.Advance(TimeSpan.FromSeconds(1));
        var notification = await removal.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );
        await Assert.That(ReferenceEquals(notification.Key, key)).IsTrue();
        await Assert.That(ReferenceEquals(notification.Value, value)).IsTrue();
        await Assert.That(notification.Cause).IsEqualTo(RemovalCause.MemoryPressure);
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        await Assert.That(cache.Policy.MemoryPressureStatistics!.Value.EvictedEntries).IsEqualTo(1);
        GC.KeepAlive(key);
        GC.KeepAlive(value);
    }

    [Test]
    public async Task WeakValueReplacementReportsOnlyTheOldVersion()
    {
        var removed = new TaskCompletionSource<RemovalNotification<int, object>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = CacheBuilder
            .Create<int, object>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .WeakValues()
            .NotificationCapacity(4)
            .RemovalListener(notification => removed.TrySetResult(notification))
            .Build();
        object first = new();
        object second = new();
        cache.Put(1, first);
        cache.Put(1, second);
        var notification = await removed.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );
        await Assert.That(notification.Cause).IsEqualTo(RemovalCause.Replaced);
        await Assert.That(ReferenceEquals(notification.Value, first)).IsTrue();
        await Assert.That(cache.TryGet(1, out object? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, second)).IsTrue();
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    private sealed class HighPressureSource : IMemoryPressureSource
    {
        public MemoryPressureSample GetSample() => new(1);
    }

    private sealed record EqualValue([property: UsedImplicitly] int Number);

    private sealed class GatedBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal int Calls;
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<int>(new InvalidOperationException("Expected the bulk loader."));

        public Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Calls);
            return Result.Task;
        }
    }
}
