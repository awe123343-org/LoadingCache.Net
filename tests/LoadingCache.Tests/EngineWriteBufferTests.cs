using System.Collections.Concurrent;
using FluentAssertions;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class EngineWriteBufferTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public void RejectsAnIndependentlyConstructedBuiltInPolicy()
    {
        using var policy = new WindowTinyLfuEnginePolicy(
            maximum: 4,
            maximumResidentCount: 4,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );

        var options = new CacheEngineOptions<int, string>
        {
            MaximumSize = 4,
            MaxConcurrentLoads = 1,
            Policy = policy,
        };

        options
            .Invoking(static o => _ = new CacheEngine<int, string>(o))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*built-in policy*custom test policy*");
    }

    [Test]
    public void QueuedWritesShareOneMaintenanceRequestAndRearmAfterDraining()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, capacity: 8);
        using var cache = new Cache<int, string>(engine);

        for (int key = 0; key < 128; key++)
        {
            cache.Put(key, "value");
        }

        engine.GetMaintenanceStatistics().Requests.Should().Be(1);
        scheduler.Pending.Should().Be(1);
        engine.GetPolicyWriteBufferStatistics().Full.Should().BeGreaterThan(0);

        scheduler.RunAll();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Put(128, "next batch");
        engine.GetMaintenanceStatistics().Requests.Should().Be(2);
        scheduler.Pending.Should().Be(1);
        scheduler.RunAll();
        engine.AssertInvariants();
    }

    [Test]
    public void PolicySnapshotFlushDoesNotStrandTheFollowingWrite()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "first");
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Put(2, "following write");
        scheduler.RunAll();

        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Policy.Eviction.WeightedSize.Should().Be(2);
        engine.AssertInvariants();
    }

    [Test]
    public void ReadyMappingsAreVisibleBeforeDeferredPolicyWritesRun()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");

        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(1);
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("ready");
        scheduler.Pending.Should().Be(1);
        cache.Statistics.WriteBufferBacklog.Should().Be(1);
        cache.Statistics.MaintenanceBacklog.Should().BeGreaterThanOrEqualTo(1);

        scheduler.RunAll();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.AssertInvariants();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FullBufferAssistsWithoutLosingWritesOrExceedingTheDerivedCountBound(bool statistics)
    {
        const int maximum = 2;
        const int capacity = 2;
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(
            scheduler,
            maximum: maximum,
            capacity: capacity,
            statistics: statistics
        );
        using var cache = new Cache<int, string>(engine);

        for (int key = 0; key < 64; key++)
        {
            cache.Put(key, "value");
            engine.GetPolicyWriteBufferStatistics().Queued.Should().BeLessThanOrEqualTo(capacity);
            cache.EstimatedCount.Should().BeLessThanOrEqualTo(maximum + capacity + 1);
        }

        engine.GetPolicyWriteBufferStatistics().Full.Should().BeGreaterThan(0);
        if (statistics)
        {
            cache.Statistics.WriteBufferPressure.Should().BeGreaterThan(0);
        }
        else
        {
            cache.Statistics.WriteBufferPressure.Should().Be(0);
        }

        cache.CleanUp();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(maximum);
        cache.Policy.Eviction!.WeightedSize.Should().BeLessThanOrEqualTo(maximum);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ConcurrentPublishersConvergeWithAStoppedConsumer()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, maximum: 16, capacity: 2);
        using var cache = new Cache<int, string>(engine);

        await Task.WhenAll(StartPublishers(cache)).WaitAsync(Watchdog, CancellationToken.None);

        engine.GetPolicyWriteBufferStatistics().Queued.Should().BeLessThanOrEqualTo(2);
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(19);
        cache.CleanUp();
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(16);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public void QueuedAddRemoveAndReplacementOnlyAffectTheirExactEntries()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, capacity: 8);
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "old");
        cache.Invalidate(1).Should().BeTrue();
        cache.Put(1, "replacement");
        cache.Put(2, "other");
        scheduler.RunAll();

        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("replacement");
        cache.Policy.Eviction!.WeightedSize.Should().Be(2);
        cache.EstimatedCount.Should().Be(2);
        engine.AssertInvariants();
    }

    [Test]
    public void ClearDiscardsOldEpochWritesWithoutTurningThemIntoSizeEvictions()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, maximum: 2, capacity: 8, statistics: true);
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 4; key++)
        {
            cache.Put(key, "old");
        }

        cache.Clear();

        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Statistics.SizeRemovals.Should().Be(0);
        cache.Statistics.ClearedRemovals.Should().Be(4);
        cache.Put(1, "new epoch");
        scheduler.RunAll();
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("new epoch");
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    public void DisposeReleasesPendingWritesAndMakesAnOldWorkerHarmless()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        var cache = new Cache<int, string>(engine);
        cache.Put(1, "queued");

        cache.Dispose();
        scheduler.RunAll();

        var buffer = engine.GetPolicyWriteBufferStatistics();
        buffer.IsDisposed.Should().BeTrue();
        buffer.Queued.Should().Be(0);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Disposed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RejectedOrInlineSchedulingRunsOutsideTheEngineLocks(bool inline)
    {
        var scheduler = new ControlledScheduler
        {
            Inline = inline,
            AcceptLimit = inline ? int.MaxValue : 0,
        };
        var engine = CreateEngine(scheduler);
        scheduler.IsCacheLockHeld = () => engine.IsCoordinationLockHeldForTesting;
        using var cache = new Cache<int, string>(engine);

        for (int key = 0; key < 8; key++)
        {
            cache.Put(key, "value");
        }

        scheduler.ObservedCacheLock.Should().BeFalse();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(4);
        engine.AssertInvariants();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LastWriteDuringWorkerExitSurvivesAnAcceptedOrRejectedRearm(bool rejectRearm)
    {
        var scheduler = new ControlledScheduler { AcceptLimit = rejectRearm ? 1 : int.MaxValue };
        await using var hook = new BlockingTestHook(Watchdog);
        var engine = CreateEngine(
            scheduler,
            hooks: new LoadingCacheTestHooks { AfterPolicyMaintenance = hook.Invoke },
            maxPasses: 1
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "first");
        Task worker = Task.Run(scheduler.RunNext, CancellationToken.None);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            cache.Put(2, "last write");
            engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(1);
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        scheduler.RunAll();
        hook.TimedOut.Should().BeFalse();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Policy.Eviction!.WeightedSize.Should().Be(2);
        if (rejectRearm)
        {
            engine.GetMaintenanceStatistics().ScheduleRejections.Should().BeGreaterThan(0);
        }
        engine.AssertInvariants();
    }

    [Test]
    public void FailedMaintenancePassUsesTheReliableWriteFallback()
    {
        var scheduler = new ControlledScheduler();
        var hooks = new LoadingCacheTestHooks
        {
            BeforePolicyMaintenance = () =>
                throw new InvalidOperationException("injected maintenance fault"),
        };
        var engine = CreateEngine(scheduler, hooks: hooks);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "ready");

        scheduler.RunAll();

        engine.GetMaintenanceStatistics().DrainFaults.Should().BeGreaterThan(0);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    public void ExpirationIsAuthoritativeEvenWhenTheAddEventHasNotRun()
    {
        var time = new FakeTimeProvider();
        var scheduler = new ControlledScheduler();
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 4,
                TimeProvider = time,
                ExpireAfterWrite = TimeSpan.FromSeconds(1),
                MaintenanceScheduler = scheduler,
                MaintenanceWriteBufferCapacity = 8,
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "expired");
        time.Advance(TimeSpan.FromSeconds(1));

        cache.TryGet(1, out _).Should().BeFalse();
        cache.Put(1, "new");
        scheduler.RunAll();

        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("new");
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.AssertInvariants();
    }

    [TestCase(0L)]
    [TestCase(7L)]
    [TestCase(11L)]
    [TestCase(long.MaxValue)]
    public async Task RefreshWeightUpdatesRemainTrackedOrRemoveTheOversizedVersion(long weight)
    {
        var scheduler = new ControlledScheduler();
        var engine = new CacheEngine<int, long>(
            new CacheEngineOptions<int, long>
            {
                MaximumWeight = 10,
                MaximumResidentCount = 4,
                Weigher = static (_, value) => value,
                MaxConcurrentLoads = 2,
                MaintenanceScheduler = scheduler,
                MaintenanceWriteBufferCapacity = 8,
            }
        );
        await using var cache = new AsyncLoadingCache<int, long>(
            engine,
            static (_, _) => Task.FromResult(1L),
            (_, _, _) => Task.FromResult(weight)
        );
        (await cache.GetAsync(1)).Should().Be(1);
        cache.CleanUp();

        (await cache.RefreshAsync(1)).Should().Be(weight);
        if (weight > 10)
        {
            cache.TryGet(1, out _).Should().BeFalse();
        }
        scheduler.RunAll();
        cache.CleanUp();

        cache.Policy.Eviction!.WeightedSize.Should().Be(weight > 10 ? 0 : weight);
        cache.EstimatedCount.Should().Be(weight > 10 ? 0 : 1);
        cache.Policy.Eviction.Coldest(4).Count.Should().Be((int)cache.EstimatedCount);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public async Task SeveralRefreshesBeforeReplayUseOnlyTheLatestValueWeight()
    {
        var scheduler = new ControlledScheduler();
        var engine = new CacheEngine<int, long>(
            new CacheEngineOptions<int, long>
            {
                MaximumWeight = 10,
                MaximumResidentCount = 4,
                Weigher = static (_, value) => value,
                MaxConcurrentLoads = 2,
                MaintenanceScheduler = scheduler,
                MaintenanceWriteBufferCapacity = 8,
            }
        );
        await using var cache = new AsyncLoadingCache<int, long>(
            engine,
            static (_, _) => Task.FromResult(1L),
            static (_, oldValue, _) => Task.FromResult(oldValue + 2)
        );
        (await cache.GetAsync(1)).Should().Be(1);
        cache.CleanUp();
        (await cache.RefreshAsync(1)).Should().Be(3);
        (await cache.RefreshAsync(1)).Should().Be(5);
        (await cache.RefreshAsync(1)).Should().Be(7);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(3);

        scheduler.RunAll();

        cache.TryGet(1, out long value).Should().BeTrue();
        value.Should().Be(7);
        cache.Policy.Eviction!.WeightedSize.Should().Be(7);
        cache.Policy.Eviction.Coldest(4).Should().ContainSingle();
        engine.AssertInvariants();
    }

    [Test]
    public void WeightedAndZeroWeightWritesRemainBoundedWithAStoppedConsumer()
    {
        const int residentMaximum = 4;
        const int bufferCapacity = 2;
        var scheduler = new ControlledScheduler();
        var engine = new CacheEngine<int, long>(
            new CacheEngineOptions<int, long>
            {
                MaximumWeight = 10,
                MaximumResidentCount = residentMaximum,
                Weigher = static (_, value) => value,
                MaxConcurrentLoads = 2,
                MaintenanceScheduler = scheduler,
                MaintenanceWriteBufferCapacity = bufferCapacity,
            }
        );
        using var cache = new Cache<int, long>(engine);
        for (int key = 0; key < 128; key++)
        {
            cache.Put(key, key % 2 == 0 ? 0 : 6);
            cache.EstimatedCount.Should().BeLessThanOrEqualTo(residentMaximum + bufferCapacity + 1);
        }

        cache.Policy.Eviction!.SetMaximum(3);
        cache.CleanUp();

        cache.Policy.Eviction.WeightedSize.Should().BeLessThanOrEqualTo(3);
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(residentMaximum);
        cache.Policy.Eviction.Coldest(residentMaximum).Count.Should().Be((int)cache.EstimatedCount);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        engine.AssertInvariants();
    }

    private static IEnumerable<Task> StartPublishers(Cache<int, string> cache) =>
        Enumerable
            .Range(0, 8)
            .Select(producer =>
                Task.Run(
                    () =>
                    {
                        for (int index = 0; index < 128; index++)
                        {
                            cache.Put(producer * 128 + index, "value");
                        }
                    },
                    CancellationToken.None
                )
            );

    private static CacheEngine<int, string> CreateEngine(
        ControlledScheduler scheduler,
        int maximum = 4,
        int capacity = 4,
        bool statistics = false,
        LoadingCacheTestHooks? hooks = null,
        int maxPasses = 32
    ) =>
        new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = maximum,
                MaxConcurrentLoads = 4,
                MaintenanceScheduler = scheduler,
                MaintenanceWriteBufferCapacity = capacity,
                MaintenanceMaxPasses = maxPasses,
                RecordStatistics = statistics,
                TestHooks = hooks,
            }
        );

    private sealed class ControlledScheduler : IMaintenanceScheduler
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        private int _scheduleCalls;
        internal int AcceptLimit { get; init; } = int.MaxValue;
        internal bool Inline { get; init; }
        internal Func<bool>? IsCacheLockHeld { get; set; }
        internal bool ObservedCacheLock { get; private set; }
        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            ObservedCacheLock |= IsCacheLockHeld?.Invoke() == true;
            int call = Interlocked.Increment(ref _scheduleCalls);
            if (call > AcceptLimit)
            {
                return false;
            }
            if (Inline)
            {
                callback();
            }
            else
            {
                _callbacks.Enqueue(callback);
            }
            return true;
        }

        internal void RunNext()
        {
            _callbacks.TryDequeue(out var callback).Should().BeTrue();
            callback!();
        }

        internal void RunAll()
        {
            int budget = 128;
            while (_callbacks.TryDequeue(out var callback))
            {
                if (--budget == 0)
                {
                    throw new InvalidOperationException("Maintenance did not quiesce.");
                }
                callback();
            }
        }
    }
}
