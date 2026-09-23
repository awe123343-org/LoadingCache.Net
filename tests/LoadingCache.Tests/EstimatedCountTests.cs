using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class EstimatedCountTests
{
    [Test]
    public async Task EstimatedCountTracksPendingSuccessRemovalAndClear()
    {
        var started = NewSignal();
        var release = NewSignal<string>();
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .BuildAsyncLoading(
                (_, _) =>
                {
                    started.TrySetResult(true);
                    return release.Task;
                }
            );
        Task<string> pending = cache.GetAsync(1).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        release.SetResult("ready");
        await Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo("ready");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        cache.Set(2, "two");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        cache.Clear();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task EstimatedCountUsesTheWeakEntryDictionaryPath()
    {
        using ICache<object, object> cache = CacheBuilder
            .Create<object, object>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .WeakValues()
            .Build();
        object key = new();
        object value = new();
        cache.Put(key, value);
        GC.KeepAlive(value);
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Invalidate(key)).IsTrue();
        GC.KeepAlive(value);
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public Task EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry() =>
        AllocationTestProcess.VerifyAsync("estimated-count");

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewSignal() => NewSignal<bool>();

    private sealed class RejectingScheduler : IMaintenanceScheduler
    {
        public bool TrySchedule(Action callback) => false;
    }
}
