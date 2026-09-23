using System.Runtime.CompilerServices;
using LoadingCache.Diagnostics;
using LoadingCache.Maintenance;
using LoadingCache.ReferenceStorage;

namespace LoadingCache.AllocationProbe;

internal static class AllocationScenarios
{
    internal static async Task HotCounterUpdatesDoNotAllocatePerEvent()
    {
        StripedCacheCounters counters = new(1);
        counters.Add(CacheCounterKind.Hits);
        long allocated = MeasureCounter(counters);
        await Assert.That(allocated).IsEqualTo(0L);
    }

    // NoInlining keeps this kernel out of the async test; AggressiveOptimization
    // compiles it before the allocation baseline. Otherwise OSR can grow the CLR
    // CastCache during JIT cast analysis (6,192 B observed on Windows .NET 10).
    // Keep the zero-byte assertion; only this measurement kernel bypasses tiering.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static long MeasureCounter(StripedCacheCounters counters)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++)
        {
            counters.Add(CacheCounterKind.Hits);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    internal static async Task WeakKeyObjectComparerDoesNotAllocateDuringRawLookupComparison()
    {
        Key key = new(1);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        IEqualityComparer<object> comparer = WeakKeyObjectComparer<Key>.Instance;
        (long allocated, bool allMatches) = MeasureComparison(comparer, handle, key);
        await Assert.That(allMatches).IsTrue();
        await Assert.That(allocated).IsEqualTo(0L);
    }

    // NoInlining keeps this kernel out of the async test; AggressiveOptimization
    // compiles it before the allocation baseline. Otherwise OSR can grow the CLR
    // CastCache during JIT cast analysis (6,192 B observed on Windows .NET 10).
    // Keep the zero-byte assertion; only this measurement kernel bypasses tiering.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static (long Allocated, bool AllMatches) MeasureComparison(
        IEqualityComparer<object> comparer,
        ReferenceKey<Key> handle,
        Key key
    )
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allMatches = true;
        for (int index = 0; index < 10_000; index++)
        {
            allMatches &= comparer.Equals(handle, key);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before, allMatches);
    }

    internal static async Task RawComparisonMeasurementDetectsAllocatingComparer()
    {
        Key key = new(1);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        IEqualityComparer<object> allocating = EqualityComparer<object>.Create(
            (_, _) =>
            {
                GC.KeepAlive(new byte[1_024]);
                return true;
            }
        );
        (long allocated, bool allMatches) = MeasureComparison(allocating, handle, key);
        await Assert.That(allMatches).IsTrue();
        await Assert.That(allocated).IsGreaterThan(0L);
    }

    internal static async Task WeakKeyResidentHitDoesNotAllocateLookupProbe()
    {
        // Keep policy transport out of this measurement: its scheduler and
        // read-buffer work are separate from the authoritative key lookup.
        // The rejecting scheduler also prevents an accidental async work item.
        await using CacheEngine<Key, Value> engine = new(
            new CacheEngineOptions<Key, Value>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 1,
                WeakKeys = true,
                Policy = new NoopCacheEnginePolicy(),
                MaintenanceScheduler = new RejectingMaintenanceScheduler(),
            }
        );
        Key key = new(7);
        engine.Put(key, new Value());
        engine.CleanUp();
        for (int index = 0; index < 10_000; index++)
        {
            await Assert.That(engine.TryGet(key, out _)).IsTrue();
        }

        engine.CleanUp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allHits = true;
        for (int index = 0; index < 10_000; index++)
        {
            allHits &= engine.TryGet(key, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allHits).IsTrue();
        await Assert.That(allocated).IsEqualTo(0);
    }

    internal static async Task EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry()
    {
        long smallAllocation = MeasureEstimatedCountAllocation(512);
        long largeAllocation = MeasureEstimatedCountAllocation(4096);
        await Assert.That(largeAllocation).IsLessThanOrEqualTo(smallAllocation + 1024);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureEstimatedCountAllocation(int entryCount)
    {
        using var engine = new CacheEngine<int, int>(
            new CacheEngineOptions<int, int>
            {
                MaximumSize = entryCount + 1,
                MaxConcurrentLoads = 1,
                MaintenanceScheduler = new RejectingMaintenanceScheduler(),
            }
        );
        for (int index = 0; index < entryCount; index++)
        {
            engine.Put(index, index);
        }

        engine.CleanUp();
        for (int index = 0; index < 4; index++)
        {
            if ((engine.EstimatedCount) != (entryCount))
                Assert.Fail("Expected the warmup count to match the populated entries.");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long count = engine.EstimatedCount;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if ((count) != (entryCount))
            Assert.Fail($"Expected {entryCount} entries, found {count}.");
        return allocated;
    }

    internal static async Task RepeatedResidentPutHasBoundedAllocation()
    {
        const int iterations = 4096;
        int[] values = new int[iterations];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = index;
        }

        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16_384)
            .MaxConcurrentLoads(4)
            .RecordStatistics()
            .Build();
        cache.Put(1, 0);
        for (int index = 0; index < 256; index++)
        {
            cache.Put(1, values[index]);
        }

        cache.CleanUp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (int value in values)
        {
            cache.Put(1, value);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        cache.CleanUp();
        await Assert.That(cache.TryGet(1, out int current)).IsTrue();
        await Assert.That(current).IsEqualTo(iterations - 1);
        await Assert.That(allocated).IsLessThan(iterations * 128L);
    }

    private sealed class Key(int number)
    {
        private readonly int _number = number;

        public override bool Equals(object? obj) => obj is Key other && other._number == _number;

        public override int GetHashCode() => _number;
    }

    private sealed class RejectingMaintenanceScheduler : IMaintenanceScheduler
    {
        public bool TrySchedule(Action callback) => false;
    }

    private sealed class NoopCacheEnginePolicy : ICacheEnginePolicy
    {
        public long Maximum => long.MaxValue;
        public long WeightedSize => 0;
        public int ResidentCount => 0;

        public void SetMaximum(long maximum, bool weighted) { }

        public IReadOnlyList<object> Snapshot(bool hottest, int limit) => [];

        public void OnAccess(object? entryToken) { }

        public void OnPublish(object? entryToken, long weight) { }

        public void OnRemove(object? entryToken) { }

        public void Clear() { }

        public bool CleanUp() => false;

        public ReadBufferStatistics GetReadBufferStatistics() => default;

        public void Dispose() { }
    }

    private sealed class Value;
}
