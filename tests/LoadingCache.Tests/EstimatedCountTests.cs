using FluentAssertions;

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
        cache.EstimatedCount.Should().Be(0);
        release.SetResult("ready");
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("ready");
        cache.EstimatedCount.Should().Be(1);
        cache.Invalidate(1).Should().BeTrue();
        cache.EstimatedCount.Should().Be(0);
        cache.Set(2, "two");
        cache.EstimatedCount.Should().Be(1);
        cache.Clear();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void EstimatedCountUsesTheWeakEntryDictionaryPath()
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
        cache.EstimatedCount.Should().Be(1);
        cache.Invalidate(key).Should().BeTrue();
        GC.KeepAlive(value);
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public Task EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry() =>
        AllocationTestProcess.VerifyAsync("estimated-count");

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewSignal() => NewSignal<bool>();
}
