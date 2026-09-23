using System.Collections.Concurrent;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class EngineWriteBufferTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task RejectsAnIndependentlyConstructedBuiltInPolicy()
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
        await Assert
            .That(() => _ = new CacheEngine<int, string>(options))
            .Throws<ArgumentException>()
            .WithMessageMatching("*built-in policy*custom test policy*");
    }

    [Test]
    public async Task QueuedWritesShareOneMaintenanceRequestAndRearmAfterDraining()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, capacity: 8);
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 128; key++)
        {
            cache.Put(key, "value");
        }

        await Assert.That(engine.GetMaintenanceStatistics().Requests).IsEqualTo(1);
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Full).IsGreaterThan(0);
        scheduler.RunAll();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        cache.Put(128, "next batch");
        await Assert.That(engine.GetMaintenanceStatistics().Requests).IsEqualTo(2);
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunAll();
        engine.AssertInvariants();
    }

    [Test]
    public async Task PolicySnapshotFlushDoesNotStrandTheFollowingWrite()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "first");
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        cache.Put(2, "following write");
        scheduler.RunAll();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsEqualTo(2);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ReadyMappingsAreVisibleBeforeDeferredPolicyWritesRun()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "ready");
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("ready");
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        await Assert.That(cache.Statistics.WriteBufferBacklog).IsEqualTo(1);
        await Assert.That(cache.Statistics.MaintenanceBacklog).IsGreaterThanOrEqualTo(1);
        scheduler.RunAll();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FullBufferAssistsWithoutLosingWritesOrExceedingTheDerivedCountBound(
        bool statistics
    )
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
            await Assert
                .That(engine.GetPolicyWriteBufferStatistics().Queued)
                .IsLessThanOrEqualTo(capacity);
            await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(maximum + capacity + 1);
        }

        await Assert.That(engine.GetPolicyWriteBufferStatistics().Full).IsGreaterThan(0);
        if (statistics)
        {
            await Assert.That(cache.Statistics.WriteBufferPressure).IsGreaterThan(0);
        }
        else
        {
            await Assert.That(cache.Statistics.WriteBufferPressure).IsEqualTo(0);
        }

        cache.CleanUp();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(maximum);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsLessThanOrEqualTo(maximum);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ConcurrentPublishersConvergeWithAStoppedConsumer()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, maximum: 16, capacity: 2);
        using var cache = new Cache<int, string>(engine);
        await Task.WhenAll(StartPublishers(cache)).WaitAsync(Watchdog, CancellationToken.None);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsLessThanOrEqualTo(2);
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(19);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(16);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        engine.AssertInvariants();
    }

    [Test]
    public async Task QueuedAddRemoveAndReplacementOnlyAffectTheirExactEntries()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, capacity: 8);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        await Assert.That(cache.Invalidate(1)).IsTrue();
        cache.Put(1, "replacement");
        cache.Put(2, "other");
        scheduler.RunAll();
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("replacement");
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(2);
        await Assert.That(cache.EstimatedCount).IsEqualTo(2);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ClearDiscardsOldEpochWritesWithoutTurningThemIntoSizeEvictions()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler, maximum: 2, capacity: 8, statistics: true);
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 4; key++)
        {
            cache.Put(key, "old");
        }

        cache.Clear();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.Statistics.SizeRemovals).IsEqualTo(0);
        await Assert.That(cache.Statistics.ClearedRemovals).IsEqualTo(4);
        cache.Put(1, "new epoch");
        scheduler.RunAll();
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("new epoch");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        engine.AssertInvariants();
    }

    [Test]
    public async Task DisposeReleasesPendingWritesAndMakesAnOldWorkerHarmless()
    {
        var scheduler = new ControlledScheduler();
        var engine = CreateEngine(scheduler);
        var cache = new Cache<int, string>(engine);
        cache.Put(1, "queued");
        cache.Dispose();
        scheduler.RunAll();
        var buffer = engine.GetPolicyWriteBufferStatistics();
        await Assert.That(buffer.IsDisposed).IsTrue();
        await Assert.That(buffer.Queued).IsEqualTo(0);
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Disposed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedOrInlineSchedulingRunsOutsideTheEngineLocks(bool inline)
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

        await Assert.That(scheduler.ObservedCacheLock).IsFalse();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(4);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(1);
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        scheduler.RunAll();
        await Assert.That(hook.TimedOut).IsFalse();
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(2);
        if (rejectRearm)
        {
            await Assert
                .That(engine.GetMaintenanceStatistics().ScheduleRejections)
                .IsGreaterThan(0);
        }

        engine.AssertInvariants();
    }

    [Test]
    public async Task FailedMaintenancePassUsesTheReliableWriteFallback()
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
        await Assert.That(engine.GetMaintenanceStatistics().DrainFaults).IsGreaterThan(0);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ExpirationIsAuthoritativeEvenWhenTheAddEventHasNotRun()
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
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        cache.Put(1, "new");
        scheduler.RunAll();
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("new");
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(0L)]
    [Arguments(7L)]
    [Arguments(11L)]
    [Arguments(long.MaxValue)]
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
        cache.CleanUp();
        await Assert.That((await cache.RefreshAsync(1))).IsEqualTo(weight);
        if (weight > 10)
        {
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
        }

        scheduler.RunAll();
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(weight > 10 ? 0 : weight);
        await Assert.That(cache.EstimatedCount).IsEqualTo(weight > 10 ? 0 : 1);
        await Assert
            .That(cache.Policy.Eviction.Coldest(4).Count)
            .IsEqualTo((int)cache.EstimatedCount);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
        cache.CleanUp();
        await Assert.That((await cache.RefreshAsync(1))).IsEqualTo(3);
        await Assert.That((await cache.RefreshAsync(1))).IsEqualTo(5);
        await Assert.That((await cache.RefreshAsync(1))).IsEqualTo(7);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(3);
        scheduler.RunAll();
        await Assert.That(cache.TryGet(1, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(7);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(7);
        await Assert.That(cache.Policy.Eviction.Coldest(4)).HasSingleItem();
        engine.AssertInvariants();
    }

    [Test]
    public async Task WeightedAndZeroWeightWritesRemainBoundedWithAStoppedConsumer()
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
            await Assert
                .That(cache.EstimatedCount)
                .IsLessThanOrEqualTo(residentMaximum + bufferCapacity + 1);
        }

        cache.Policy.Eviction!.SetMaximum(3);
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsLessThanOrEqualTo(3);
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(residentMaximum);
        await Assert
            .That(cache.Policy.Eviction.Coldest(residentMaximum).Count)
            .IsEqualTo((int)cache.EstimatedCount);
        await Assert.That(engine.GetPolicyWriteBufferStatistics().Queued).IsEqualTo(0);
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
            if (!(_callbacks.TryDequeue(out var callback)))
                Assert.Fail("Expected _callbacks.TryDequeue(out var callback) to be true ().");
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
