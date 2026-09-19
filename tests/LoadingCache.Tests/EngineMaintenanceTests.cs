using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

/// <summary>Integration checks for the shared engine maintenance seam.</summary>
public sealed class EngineMaintenanceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public void ReadMaintenanceIsDeferredUntilTheStripeIsFull()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 1
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        scheduler.Pending.Should().Be(1);
        scheduler.RunNext();
        scheduler.Pending.Should().Be(0);

        cache.TryGet(1, out string? first).Should().BeTrue();
        first.Should().Be("ready");
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);
        scheduler.Pending.Should().Be(0);

        cache.TryGet(1, out string? second).Should().BeTrue();
        second.Should().Be("ready");
        engine.GetPolicyReadBufferStatistics().DroppedFull.Should().Be(1);
        scheduler.Pending.Should().Be(1);

        scheduler.RunNext();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public void DefaultReadBufferBatches64HitsBeforeSchedulingMaintenance()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 4,
                RecordStatistics = true,
                MaintenanceScheduler = scheduler,
            }
        );
        using var cache = new Cache<int, string>(engine);

        // Activate read recording without overriding either production buffer default.
        cache.Policy.Eviction!.SetMaximum(4);
        cache.Put(1, "ready");
        scheduler.Pending.Should().Be(1);
        scheduler.RunNext();
        scheduler.Pending.Should().Be(0);
        int writeScheduleCalls = scheduler.ScheduleCalls;

        for (int index = 0; index < 64; index++)
        {
            cache.TryGet(1, out string? value).Should().BeTrue();
            value.Should().Be("ready");
            scheduler.Pending.Should().Be(0);
        }

        ReadBufferStatistics full = engine.GetPolicyReadBufferStatistics();
        full.Queued.Should().Be(64);
        full.Enqueued.Should().Be(64);
        full.DroppedFull.Should().Be(0);
        scheduler.ScheduleCalls.Should().Be(writeScheduleCalls);

        cache.TryGet(1, out string? last).Should().BeTrue();
        last.Should().Be("ready");
        engine.GetPolicyReadBufferStatistics().DroppedFull.Should().Be(1);
        scheduler.Pending.Should().Be(1);
        scheduler.ScheduleCalls.Should().Be(writeScheduleCalls + 1);

        scheduler.RunNext();
        ReadBufferStatistics drained = engine.GetPolicyReadBufferStatistics();
        drained.Queued.Should().Be(0);
        drained.Dequeued.Should().Be(64);
        scheduler.Pending.Should().Be(0);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ReadyHitProgressesWhilePolicyMaintenanceIsPaused()
    {
        ManualMaintenanceScheduler scheduler = new();
        await using BlockingTestHook hook = new(Watchdog);
        var hooks = new LoadingCacheTestHooks { BeforePolicyMaintenance = hook.Invoke };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            hooks,
            readStripeCount: 1,
            readStripeCapacity: 8
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        cache.TryGet(1, out string? first).Should().BeTrue();
        first.Should().Be("ready");
        scheduler.Pending.Should().Be(1);

        Task worker = Task.Run(scheduler.RunNext);
        Task<bool>? hit = null;
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            hit = Task.Factory.StartNew(
                static state =>
                {
                    var activeCache = (Cache<int, string>)state!;
                    return activeCache.TryGet(1, out string? value) && value == "ready";
                },
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            (await hit.WaitAsync(Watchdog, CancellationToken.None)).Should().BeTrue();
        }
        finally
        {
            hook.Release();
            await Task.WhenAll(worker, hit ?? Task.CompletedTask)
                .WaitAsync(Watchdog, CancellationToken.None);
        }

        hook.TimedOut.Should().BeFalse();
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public void ReadBufferIsBoundedAndDropsOnlyBestEffortAccessEvents()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 2
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        for (int index = 0; index < 32; index++)
        {
            cache.TryGet(1, out _).Should().BeTrue();
        }

        ReadBufferStatistics statistics = engine.GetPolicyReadBufferStatistics();
        statistics.Queued.Should().BeLessThanOrEqualTo(2);
        statistics.DroppedFull.Should().BeGreaterThan(0);
        scheduler.ScheduleCalls.Should().Be(1);

        // Reliable mapping writes do not depend on admission to the lossy read
        // transport.
        cache.Put(2, "write");
        cache.TryGet(2, out string? value).Should().BeTrue();
        value.Should().Be("write");
        engine.AssertInvariants();
    }

    [Test]
    public void RejectedSchedulerUsesSynchronousFallbackForQueuedReads()
    {
        ManualMaintenanceScheduler scheduler = new() { Reject = true };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 1
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        cache.TryGet(1, out _).Should().BeTrue();
        cache.TryGet(1, out _).Should().BeTrue();

        ReadBufferStatistics buffer = engine.GetPolicyReadBufferStatistics();
        buffer.Queued.Should().Be(0);
        MaintenanceStatistics maintenance = engine.GetMaintenanceStatistics();
        maintenance.ScheduleRejections.Should().BeGreaterThan(0);
        maintenance.DrainPasses.Should().BeGreaterThan(0);
        maintenance.FallbackRequired.Should().BeFalse();
    }

    [Test]
    public void FullReadStripeCanRetryAfterARejectedBudgetedFallback()
    {
        ManualMaintenanceScheduler scheduler = new() { Reject = true };
        RearmRetryState state = new();
        var hooks = new LoadingCacheTestHooks
        {
            BeforeMaintenanceSignalClear = state.BeforeMaintenanceSignalClear,
        };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            hooks,
            readStripeCount: 1,
            readStripeCapacity: 1,
            maintenanceMaxPasses: 32
        );
        var cache = new Cache<int, string>(engine);
        state.Attach(cache);
        using (cache)
        {
            cache.Put(1, "ready");

            // The first hit fills the one-slot stripe without scheduling. The
            // second hit observes full backpressure and starts the rejected
            // maintenance path. The hook then keeps one event queued at every
            // pass; the rejected re-arm must stop at the coordinator budget,
            // leaving the signal clear while the stripe is still full.
            cache.TryGet(1, out _).Should().BeTrue();
            cache.TryGet(1, out _).Should().BeTrue();
            state.MaintenanceCalls.Should().Be(32);
            engine.GetMaintenanceStatistics().BudgetExhaustions.Should().BeGreaterThan(0);
            engine.GetMaintenanceStatistics().FallbackRequired.Should().BeTrue();
            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);

            state.StopFilling();

            // The stripe is full, so this hit's event is dropped. It must still
            // observe the clear signal, request the rejected scheduler again,
            // and synchronously drain the old event.
            cache.TryGet(1, out _).Should().BeTrue();

            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
            engine.GetMaintenanceStatistics().FallbackRequired.Should().BeFalse();
            engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
            engine.GetPolicyReadBufferStatistics().DroppedFull.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void AcceptedWorkerThatCannotRearmAllowsTheNextFullHitToRetry()
    {
        AcceptThenRejectMaintenanceScheduler scheduler = new();
        RearmRetryState state = new();
        var hooks = new LoadingCacheTestHooks
        {
            BeforeMaintenanceSignalClear = state.BeforeMaintenanceSignalClear,
        };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            hooks,
            readStripeCount: 1,
            readStripeCapacity: 1,
            maintenanceMaxPasses: 1
        );
        Cache<int, string> cache = new(engine);
        state.Attach(cache);
        using (cache)
        {
            cache.Put(1, "ready");
            cache.TryGet(1, out _).Should().BeTrue();
            scheduler.Pending.Should().Be(1);

            scheduler.RunNext();

            state.MaintenanceCalls.Should().Be(1);
            scheduler.ScheduleCalls.Should().Be(2);
            engine.GetMaintenanceStatistics().FallbackRequired.Should().BeTrue();
            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);

            state.StopFilling();
            cache.TryGet(1, out _).Should().BeTrue();

            // The first worker's rejected re-arm left the read signal set. A
            // subsequent full stripe must claim a fresh maintenance request.
            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
            engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
        }
    }

    [Test]
    public async Task LastEnqueueDuringAWorkerPassIsDrainedWithoutLostWakeup()
    {
        ManualMaintenanceScheduler scheduler = new();
        await using BlockingTestHook hook = new(Watchdog);
        var hooks = new LoadingCacheTestHooks { BeforePolicyMaintenance = hook.Invoke };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            hooks,
            readStripeCount: 1,
            readStripeCapacity: 512
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        cache.TryGet(1, out _).Should().BeTrue();
        Task worker = Task.Run(scheduler.RunNext);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            for (int index = 0; index < 300; index++)
            {
                cache.TryGet(1, out _).Should().BeTrue();
            }
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        hook.TimedOut.Should().BeFalse();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
        engine.GetMaintenanceStatistics().DrainPasses.Should().BeGreaterThan(1);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public async Task ReadEnqueuedDuringSignalClearHandoffIsNotStranded()
    {
        ManualMaintenanceScheduler scheduler = new();
        await using BlockingTestHook hook = new(Watchdog);
        var hooks = new LoadingCacheTestHooks { BeforeMaintenanceSignalClear = hook.Invoke };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            hooks,
            readStripeCount: 1,
            readStripeCapacity: 8
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "ready");
        cache.TryGet(1, out _).Should().BeTrue();
        Task worker = Task.Run(scheduler.RunNext);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            cache.TryGet(1, out _).Should().BeTrue();
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }
        hook.TimedOut.Should().BeFalse();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public void ExplicitCleanupRejectionAllowsAFullReadToRecoverAfterSchedulingResumes()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 512,
            maintenanceMaxPasses: 1
        );
        using Cache<int, string> cache = new(engine);
        cache.Put(1, "ready");
        scheduler.RunNext();
        for (int index = 0; index < 300; index++)
        {
            cache.TryGet(1, out _).Should().BeTrue();
        }

        scheduler.Reject = true;
        cache.CleanUp();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(44);
        engine.GetMaintenanceStatistics().FallbackRequired.Should().BeTrue();
        scheduler.Pending.Should().Be(0);

        scheduler.Reject = false;
        for (int index = 44; index < 512; index++)
        {
            cache.TryGet(1, out _).Should().BeTrue();
        }

        cache.TryGet(1, out _).Should().BeTrue();
        scheduler.Pending.Should().Be(1);
        scheduler.RunNext();
        scheduler.Pending.Should().Be(1);
        scheduler.RunNext();

        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
        engine.GetPolicyReadBufferStatistics().Dequeued.Should().Be(768);
        engine.GetPolicyReadBufferStatistics().DroppedFull.Should().Be(1);
        engine.GetMaintenanceStatistics().FallbackRequired.Should().BeFalse();
        scheduler.Pending.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public void OldReadEventsAfterClearAndSetCannotPolluteTheCurrentPolicy()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(scheduler, readStripeCount: 1);
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "old");
        cache.TryGet(1, out _).Should().BeTrue();
        cache.Clear();
        cache.Put(1, "after-clear");
        scheduler.RunNext();
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.TryGet(1, out string? clearValue).Should().BeTrue();
        clearValue.Should().Be("after-clear");

        cache.TryGet(1, out _).Should().BeTrue();
        cache.Put(1, "after-set");
        cache.CleanUp();
        cache.Policy.Eviction.WeightedSize.Should().Be(1);
        cache.TryGet(1, out string? setValue).Should().BeTrue();
        setValue.Should().Be("after-set");
        engine.AssertInvariants();
    }

    [Test]
    public void QuiescentEngineConvergesToSizeAndWeightBounds()
    {
        CacheEngine<int, string> sizedEngine = CreateEngine(scheduler: null, maximumSize: 2);
        using var sized = new Cache<int, string>(sizedEngine);
        for (int key = 0; key < 8; key++)
        {
            sized.Put(key, key.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        sized.CleanUp();
        sized.EstimatedCount.Should().BeLessThanOrEqualTo(2);
        sized.Policy.Eviction!.WeightedSize.Should().BeLessThanOrEqualTo(2);
        sizedEngine.AssertInvariants();

        CacheEngine<int, string> weightedEngine = new(
            new CacheEngineOptions<int, string>
            {
                MaximumWeight = 5,
                MaximumResidentCount = 4,
                Weigher = (_, value) => value.Length,
                MaxConcurrentLoads = 4,
            }
        );
        using var weighted = new Cache<int, string>(weightedEngine);
        for (int key = 0; key < 8; key++)
        {
            weighted.Put(key, "123");
        }

        weighted.CleanUp();
        weighted.Policy.Eviction!.WeightedSize.Should().BeLessThanOrEqualTo(5);
        weighted.EstimatedCount.Should().BeLessThanOrEqualTo(4);
        weightedEngine.AssertInvariants();
    }

    private static CacheEngine<int, string> CreateEngine(
        IMaintenanceScheduler? scheduler,
        LoadingCacheTestHooks? hooks = null,
        int maximumSize = 4,
        int readStripeCount = 4,
        int readStripeCapacity = 256,
        int maintenanceMaxPasses = 32
    )
    {
        CacheEngine<int, string> engine = new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = maximumSize,
                MaxConcurrentLoads = 4,
                RecordStatistics = true,
                TestHooks = hooks,
                MaintenanceScheduler = scheduler,
                MaintenanceMaxPasses = maintenanceMaxPasses,
                MaintenanceReadStripeCount = readStripeCount,
                MaintenanceReadStripeCapacity = readStripeCapacity,
            }
        );

        // These tests deliberately pause write maintenance, which also delays lazy sketch
        // initialization. A supported policy resize activates read recording before the pause;
        // cold-start bypass and its activation boundary have separate regression coverage.
        engine.Policy.Eviction!.SetMaximum(maximumSize);
        return engine;
    }

    private sealed class ManualMaintenanceScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();

        internal bool Reject { get; set; }

        internal int ScheduleCalls { get; private set; }

        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            ScheduleCalls++;
            if (Reject)
            {
                return false;
            }

            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunNext()
        {
            _callbacks.Dequeue()();
        }
    }

    private sealed class AcceptThenRejectMaintenanceScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();

        internal int ScheduleCalls { get; private set; }

        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            ScheduleCalls++;
            if (ScheduleCalls > 1)
            {
                return false;
            }

            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunNext()
        {
            _callbacks.Dequeue()();
        }
    }

    private sealed class RearmRetryState
    {
        private Cache<int, string>? _cache;
        private int _keepFilling = 1;
        private int _maintenanceCalls;

        internal int MaintenanceCalls => Volatile.Read(ref _maintenanceCalls);

        internal void Attach(Cache<int, string> cache) => _cache = cache;

        internal void StopFilling() => Volatile.Write(ref _keepFilling, 0);

        internal void BeforeMaintenanceSignalClear()
        {
            Interlocked.Increment(ref _maintenanceCalls);
            if (Volatile.Read(ref _keepFilling) != 0 && !_cache!.TryGet(1, out _))
            {
                throw new InvalidOperationException("The maintenance fill hit was not ready.");
            }
        }
    }
}
