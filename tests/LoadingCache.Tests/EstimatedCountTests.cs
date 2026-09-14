using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
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
    public async Task EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry()
    {
        if (
            await AllocationTestProcess
                .RunIsolatedIfNeededAsync("estimated-count")
                .ConfigureAwait(false)
        )
            return;
        long smallAllocation = MeasureEstimatedCountAllocation(512);
        long largeAllocation = MeasureEstimatedCountAllocation(4096);

        largeAllocation.Should().BeLessThanOrEqualTo(smallAllocation + 1024);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureEstimatedCountAllocation(int entryCount)
    {
        using var engine = new CacheEngine<int, int>(
            new CacheEngineOptions<int, int>
            {
                MaximumSize = entryCount + 1,
                MaxConcurrentLoads = 1,
                MaintenanceScheduler = new RejectingScheduler(),
            }
        );
        for (int index = 0; index < entryCount; index++)
        {
            engine.Put(index, index);
        }

        engine.CleanUp();
        for (int index = 0; index < 4; index++)
        {
            engine.EstimatedCount.Should().Be(entryCount);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long count = engine.EstimatedCount;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        count.Should().Be(entryCount);
        return allocated;
    }

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewSignal() => NewSignal<bool>();

    private sealed class RejectingScheduler : IMaintenanceScheduler
    {
        public bool TrySchedule(Action callback) => false;
    }
}
