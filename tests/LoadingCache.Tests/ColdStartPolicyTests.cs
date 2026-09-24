using FluentAssertions;
using LoadingCache.Maintenance;
using LoadingCache.Policy;

namespace LoadingCache.Tests;

public sealed class ColdStartPolicyTests
{
    [Test]
    public void LazySketchStaysColdUntilTheResidentHalfCapacityThreshold()
    {
        WindowTinyLfuPolicy<int> policy = new(
            maximum: 4,
            seed: 7,
            maximumCount: 4,
            lazySketch: true
        );
        policy.IsSketchInitialized.Should().BeFalse();
        policy.Add(new PolicyNode<int>(1, 1, Hash(1)));
        policy.MissesInSample.Should().Be(1);
        policy.IsSketchInitialized.Should().BeFalse();
        policy.Maintain();
        policy.MissesInSample.Should().Be(0);
        policy.IsSketchInitialized.Should().BeFalse();
        policy.Add(new PolicyNode<int>(2, 1, Hash(2)));
        policy.IsSketchInitialized.Should().BeTrue();
        policy.SketchSampleSize.Should().BeGreaterThan(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public void LazySketchInitializesForTinyCapacities(int maximum)
    {
        WindowTinyLfuPolicy<int> policy = new(
            maximum,
            seed: 11,
            maximumCount: maximum,
            lazySketch: true
        );
        policy.Add(new PolicyNode<int>(1, 1, Hash(1)));
        policy.IsSketchInitialized.Should().BeTrue();
    }

    [Test]
    public void SetMaximumInitializesTheSketchAndClearCreatesAColdPolicy()
    {
        using WindowTinyLfuEnginePolicy policy = CreateEnginePolicy(maximum: 4);
        WindowTinyLfuEnginePolicy.EngineEntryToken first = CreateToken(1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = CreateToken(2);
        policy.IsSketchInitialized.Should().BeFalse();
        policy.SetMaximum(4, weighted: false);
        policy.IsSketchInitialized.Should().BeTrue();
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.FlushWrites();
        policy.ResidentCount.Should().Be(2);
        policy.Clear();
        policy.ResidentCount.Should().Be(0);
        policy.IsSketchInitialized.Should().BeFalse();
        policy.OnAccess(first);
        policy.GetReadBufferStatistics().Enqueued.Should().Be(0);
    }

    [Test]
    public void EligibleSizeCacheBypassesReadTransportDuringColdStart()
    {
        ManualScheduler scheduler = new();
        CacheEngine<int, string> engine = new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 1,
                RecordStatistics = true,
                MaintenanceScheduler = scheduler,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = 4,
            }
        );
        using Cache<int, string> cache = new(engine);
        cache.Put(1, "value");
        scheduler.RunAll();
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("value");
        engine.GetPolicyReadBufferStatistics().Enqueued.Should().Be(0);
    }

    [Test]
    public void EligibleSizeCacheStartsRecordingReadsAfterTheThreshold()
    {
        ManualScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateSizeEngine(
            scheduler,
            maximum: 4,
            recordStatistics: true
        );
        using Cache<int, string> cache = new(engine);
        cache.Put(1, "one");
        cache.Put(2, "two");
        scheduler.RunAll();
        cache.TryGet(1, out _).Should().BeTrue();
        engine.GetPolicyReadBufferStatistics().Enqueued.Should().Be(1);
    }

    [Test]
    public void ColdStartBypassDoesNotSuppressEnabledHitStatistics()
    {
        ManualScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateSizeEngine(
            scheduler,
            maximum: 4,
            recordStatistics: true
        );
        using Cache<int, string> cache = new(engine);
        cache.Put(1, "value");
        scheduler.RunAll();
        cache.TryGet(1, out _).Should().BeTrue();
        cache.Statistics.Hits.Should().Be(1);
        engine.GetPolicyReadBufferStatistics().Enqueued.Should().Be(0);
    }

    [Test]
    [Arguments(ColdStartExclusion.ExpireAfterWrite)]
    [Arguments(ColdStartExclusion.ExpireAfterAccess)]
    [Arguments(ColdStartExclusion.VariableExpiry)]
    [Arguments(ColdStartExclusion.RefreshAfterWrite)]
    [Arguments(ColdStartExclusion.WeakValues)]
    [Arguments(ColdStartExclusion.MaximumWeight)]
    public void ColdStartIsDisabledForUnsupportedFeatureCombinations(ColdStartExclusion exclusion)
    {
        ManualScheduler scheduler = new();
        CacheEngineOptions<int, string> options = new()
        {
            MaximumSize = exclusion == ColdStartExclusion.MaximumWeight ? null : 4,
            MaximumWeight = exclusion == ColdStartExclusion.MaximumWeight ? 4 : null,
            MaximumResidentCount = exclusion == ColdStartExclusion.MaximumWeight ? 4 : null,
            Weigher = exclusion == ColdStartExclusion.MaximumWeight ? static (_, _) => 1 : null,
            WeakValues = exclusion == ColdStartExclusion.WeakValues,
            ExpireAfterWrite =
                exclusion == ColdStartExclusion.ExpireAfterWrite ? TimeSpan.FromMinutes(1) : null,
            ExpireAfterAccess =
                exclusion == ColdStartExclusion.ExpireAfterAccess ? TimeSpan.FromMinutes(1) : null,
            RefreshAfterWrite =
                exclusion == ColdStartExclusion.RefreshAfterWrite ? TimeSpan.FromMinutes(1) : null,
            Expiry = exclusion == ColdStartExclusion.VariableExpiry ? new FixedExpiry() : null,
            MaxConcurrentLoads = 1,
            RecordStatistics = true,
            MaintenanceScheduler = scheduler,
            MaintenanceReadStripeCount = 1,
            MaintenanceReadStripeCapacity = 4,
        };
        CacheEngine<int, string> engine = new(options);
        using Cache<int, string> cache = new(engine);
        cache.Put(1, "value");
        scheduler.RunAll();
        cache.TryGet(1, out _).Should().BeTrue();
        engine.GetPolicyReadBufferStatistics().Enqueued.Should().Be(1);
    }

    [Test]
    public void QueuedReadFromAnOldGenerationCannotTouchEntriesAfterClear()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 8
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken old = CreateToken(1);
        WindowTinyLfuEnginePolicy.EngineEntryToken first = CreateToken(2);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = CreateToken(3);
        policy.OnPublish(old, 1);
        policy.FlushWrites();
        policy.Clear();
        policy.OnAccess(old);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.CleanUp();
        policy
            .Snapshot(hottest: true, limit: 4)
            .Should()
            .BeEquivalentTo([first.Entry, second.Entry]);
        policy.GetReadBufferStatistics().Queued.Should().Be(0);
    }

    private static CacheEngine<int, string> CreateSizeEngine(
        ManualScheduler scheduler,
        int maximum,
        bool recordStatistics = false
    ) =>
        new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = maximum,
                MaxConcurrentLoads = 1,
                RecordStatistics = recordStatistics,
                MaintenanceScheduler = scheduler,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = 4,
            }
        );

    private static WindowTinyLfuEnginePolicy CreateEnginePolicy(long maximum)
    {
        return new WindowTinyLfuEnginePolicy(
            maximum,
            maximumResidentCount: checked((int)maximum),
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            enableColdStart: true
        );
    }

    private static WindowTinyLfuEnginePolicy.EngineEntryToken CreateToken(int value) =>
        new(value, Hash(value));

    private static uint Hash(int value) => unchecked((uint)value * 0x9E3779B9u);

    public enum ColdStartExclusion
    {
        ExpireAfterWrite,
        ExpireAfterAccess,
        VariableExpiry,
        RefreshAfterWrite,
        WeakValues,
        MaximumWeight,
    }

    private sealed class FixedExpiry : IExpiry<int, string>
    {
        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromMinutes(1);

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromMinutes(1);

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromMinutes(1);
    }

    private sealed class ManualScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunAll()
        {
            while (_callbacks.Count != 0)
            {
                _callbacks.Dequeue()();
            }
        }
    }
}
