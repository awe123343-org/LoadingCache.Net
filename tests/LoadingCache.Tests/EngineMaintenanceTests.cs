using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

/// <summary>Integration checks for the shared engine maintenance seam.</summary>
public sealed class EngineMaintenanceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ReadMaintenanceIsDeferredUntilTheStripeIsFull()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 1
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "ready");
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out string? first)).IsTrue();
        await Assert.That(first).IsEqualTo("ready");
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out string? second)).IsTrue();
        await Assert.That(second).IsEqualTo("ready");
        await Assert.That(engine.GetPolicyReadBufferStatistics().DroppedFull).IsEqualTo(1);
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public async Task DefaultReadBufferBatches64HitsBeforeSchedulingMaintenance()
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
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        int writeScheduleCalls = scheduler.ScheduleCalls;
        for (int index = 0; index < 64; index++)
        {
            await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
            await Assert.That(value).IsEqualTo("ready");
            await Assert.That(scheduler.Pending).IsEqualTo(0);
        }

        ReadBufferStatistics full = engine.GetPolicyReadBufferStatistics();
        await Assert.That(full.Queued).IsEqualTo(64);
        await Assert.That(full.Enqueued).IsEqualTo(64);
        await Assert.That(full.DroppedFull).IsEqualTo(0);
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(writeScheduleCalls);
        await Assert.That(cache.TryGet(1, out string? last)).IsTrue();
        await Assert.That(last).IsEqualTo("ready");
        await Assert.That(engine.GetPolicyReadBufferStatistics().DroppedFull).IsEqualTo(1);
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(writeScheduleCalls + 1);
        scheduler.RunNext();
        ReadBufferStatistics drained = engine.GetPolicyReadBufferStatistics();
        await Assert.That(drained.Queued).IsEqualTo(0);
        await Assert.That(drained.Dequeued).IsEqualTo(64);
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Idle);
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
        await Assert.That(cache.TryGet(1, out string? first)).IsTrue();
        await Assert.That(first).IsEqualTo("ready");
        await Assert.That(scheduler.Pending).IsEqualTo(1);
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
            await Assert.That((await hit.WaitAsync(Watchdog, CancellationToken.None))).IsTrue();
        }
        finally
        {
            hook.Release();
            await Task.WhenAll(worker, hit ?? Task.CompletedTask)
                .WaitAsync(Watchdog, CancellationToken.None);
        }

        await Assert.That(hook.TimedOut).IsFalse();
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public async Task ReadBufferIsBoundedAndDropsOnlyBestEffortAccessEvents()
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
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
        }

        ReadBufferStatistics statistics = engine.GetPolicyReadBufferStatistics();
        await Assert.That(statistics.Queued).IsLessThanOrEqualTo(2);
        await Assert.That(statistics.DroppedFull).IsGreaterThan(0);
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(1);
        // Reliable mapping writes do not depend on admission to the lossy read
        // transport.
        cache.Put(2, "write");
        await Assert.That(cache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("write");
        engine.AssertInvariants();
    }

    [Test]
    public async Task RejectedSchedulerUsesSynchronousFallbackForQueuedReads()
    {
        ManualMaintenanceScheduler scheduler = new() { Reject = true };
        CacheEngine<int, string> engine = CreateEngine(
            scheduler,
            readStripeCount: 1,
            readStripeCapacity: 1
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "ready");
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        ReadBufferStatistics buffer = engine.GetPolicyReadBufferStatistics();
        await Assert.That(buffer.Queued).IsEqualTo(0);
        MaintenanceStatistics maintenance = engine.GetMaintenanceStatistics();
        await Assert.That(maintenance.ScheduleRejections).IsGreaterThan(0);
        await Assert.That(maintenance.DrainPasses).IsGreaterThan(0);
        await Assert.That(maintenance.FallbackRequired).IsFalse();
    }

    [Test]
    public async Task FullReadStripeCanRetryAfterARejectedBudgetedFallback()
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
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(state.MaintenanceCalls).IsEqualTo(32);
            await Assert.That(engine.GetMaintenanceStatistics().BudgetExhaustions).IsGreaterThan(0);
            await Assert.That(engine.GetMaintenanceStatistics().FallbackRequired).IsTrue();
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
            state.StopFilling();
            // The stripe is full, so this hit's event is dropped. It must still
            // observe the clear signal, request the rejected scheduler again,
            // and synchronously drain the old event.
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
            await Assert.That(engine.GetMaintenanceStatistics().FallbackRequired).IsFalse();
            await Assert
                .That(engine.GetMaintenanceStatistics().State)
                .IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(engine.GetPolicyReadBufferStatistics().DroppedFull).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task AcceptedWorkerThatCannotRearmAllowsTheNextFullHitToRetry()
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
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(scheduler.Pending).IsEqualTo(1);
            scheduler.RunNext();
            await Assert.That(state.MaintenanceCalls).IsEqualTo(1);
            await Assert.That(scheduler.ScheduleCalls).IsEqualTo(2);
            await Assert.That(engine.GetMaintenanceStatistics().FallbackRequired).IsTrue();
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
            state.StopFilling();
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            // The first worker's rejected re-arm left the read signal set. A
            // subsequent full stripe must claim a fresh maintenance request.
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
            await Assert
                .That(engine.GetMaintenanceStatistics().State)
                .IsEqualTo(MaintenanceCoordinatorState.Idle);
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
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        Task worker = Task.Run(scheduler.RunNext);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            for (int index = 0; index < 300; index++)
            {
                await Assert.That(cache.TryGet(1, out _)).IsTrue();
            }
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        await Assert.That(hook.TimedOut).IsFalse();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(engine.GetMaintenanceStatistics().DrainPasses).IsGreaterThan(1);
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Idle);
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
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        Task worker = Task.Run(scheduler.RunNext);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        await Assert.That(hook.TimedOut).IsFalse();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert
            .That(engine.GetMaintenanceStatistics().State)
            .IsEqualTo(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public async Task ExplicitCleanupRejectionAllowsAFullReadToRecoverAfterSchedulingResumes()
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
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
        }

        scheduler.Reject = true;
        cache.CleanUp();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(44);
        await Assert.That(engine.GetMaintenanceStatistics().FallbackRequired).IsTrue();
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        scheduler.Reject = false;
        for (int index = 44; index < 512; index++)
        {
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
        }

        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(engine.GetPolicyReadBufferStatistics().Dequeued).IsEqualTo(768);
        await Assert.That(engine.GetPolicyReadBufferStatistics().DroppedFull).IsEqualTo(1);
        await Assert.That(engine.GetMaintenanceStatistics().FallbackRequired).IsFalse();
        await Assert.That(scheduler.Pending).IsEqualTo(0);
        engine.AssertInvariants();
    }

    [Test]
    public async Task OldReadEventsAfterClearAndSetCannotPolluteTheCurrentPolicy()
    {
        ManualMaintenanceScheduler scheduler = new();
        CacheEngine<int, string> engine = CreateEngine(scheduler, readStripeCount: 1);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        cache.Clear();
        cache.Put(1, "after-clear");
        scheduler.RunNext();
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out string? clearValue)).IsTrue();
        await Assert.That(clearValue).IsEqualTo("after-clear");
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        cache.Put(1, "after-set");
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out string? setValue)).IsTrue();
        await Assert.That(setValue).IsEqualTo("after-set");
        engine.AssertInvariants();
    }

    [Test]
    public async Task QuiescentEngineConvergesToSizeAndWeightBounds()
    {
        CacheEngine<int, string> sizedEngine = CreateEngine(scheduler: null, maximumSize: 2);
        using var sized = new Cache<int, string>(sizedEngine);
        for (int key = 0; key < 8; key++)
        {
            sized.Put(key, key.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        sized.CleanUp();
        await Assert.That(sized.EstimatedCount).IsLessThanOrEqualTo(2);
        await Assert.That(sized.Policy.Eviction!.WeightedSize).IsLessThanOrEqualTo(2);
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
        await Assert.That(weighted.Policy.Eviction!.WeightedSize).IsLessThanOrEqualTo(5);
        await Assert.That(weighted.EstimatedCount).IsLessThanOrEqualTo(4);
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
