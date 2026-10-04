using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class WriteMaintenanceBackstopTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<object?> Ambient = new();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriterContentionWithAnIdleOwnerRequestsImmediately(bool load)
    {
        var clock = new TrackingClock();
        long initialTimestamp = clock.GetTimestamp();
        await using var publication = new BlockingTestHook(Watchdog);
        await using var factory = new BlockingTestHook(Watchdog);
        using var factoryReturning = new ManualResetEventSlim();
        int publications = 0;
        await using var engine = CreateEngine(
            clock,
            hooks: new LoadingCacheTestHooks
            {
                BeforeEntryPublicationCommit = _ =>
                {
                    if (Interlocked.Increment(ref publications) == 1)
                        // Keep the publication hook: engine cleanup runs before the hook scope ends, after the writer is joined.
                        // ReSharper disable once AccessToDisposedClosure
                        publication.Invoke();
                },
            }
        );
        using var cache = new Cache<int, string>(engine);
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var contender = new Thread(() =>
        {
            try
            {
                if (load)
                {
                    // Keep the captured engine: finally releases the hooks and joins this contender before engine cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    engine.GetOrAdd(
                        1,
                        _ =>
                        {
                            // Keep the factory hook: finally releases it and joins this contender before hook cleanup.
                            // ReSharper disable once AccessToDisposedClosure
                            factory.Invoke();
                            // Keep the shared return gate: finally joins this contender before the gate is disposed.
                            // ReSharper disable once AccessToDisposedClosure
                            factoryReturning.Set();
                            return "second";
                        }
                    );
                }
                else
                {
                    // Keep the captured cache: finally releases the hooks and joins this contender before cache cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    cache.Put(1, "second");
                }
                completed.SetResult();
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
        })
        {
            IsBackground = true,
        };
        Task first = Task.CompletedTask;
        bool started = false;
        try
        {
            if (load)
            {
                contender.Start();
                started = true;
                // Install the flight before the other writer holds the gate.
                await factory.Entered.WaitAsync(Watchdog, CancellationToken.None);
            }

            // Keep the captured cache: finally releases publication and awaits this writer before cache cleanup.
            // ReSharper disable once AccessToDisposedClosure
            first = Task.Run(() => cache.Put(0, "first"));
            await publication.Entered.WaitAsync(Watchdog, CancellationToken.None);
            engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Idle);
            if (load)
            {
                factory.Release();
                factoryReturning.Wait(Watchdog).Should().BeTrue();
            }
            else
            {
                contender.Start();
                started = true;
            }

            // The only remaining blocking point is the publication gate held by the first Put.
            SpinWait
                .SpinUntil(() => (contender.ThreadState & ThreadState.WaitSleepJoin) != 0, Watchdog)
                .Should()
                .BeTrue();
        }
        finally
        {
            publication.Release();
            factory.Release();
            if (started)
                contender.Join(Watchdog).Should().BeTrue();
            await first.WaitAsync(Watchdog, CancellationToken.None);
        }
        await completed.Task.WaitAsync(Watchdog, CancellationToken.None);

        engine.GetMaintenanceStatistics().Requests.Should().Be(1);
        clock.GetTimestamp().Should().Be(initialTimestamp);
        WaitForDrain(engine);
        engine.TryGet(0, out string? firstValue).Should().BeTrue();
        firstValue.Should().Be("first");
        engine.TryGet(1, out string? secondValue).Should().BeTrue();
        secondValue.Should().Be("second");
        engine.AssertInvariants();
    }

    [Test]
    public void ASubthresholdTailArmsOnceWithoutSlidingAndDrainsWithoutTraffic()
    {
        var clock = new TrackingClock();
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        cache.Put(0, "value");
        clock.Advance(TimeSpan.FromTicks(5_000));
        for (int key = 1; key < 63; key++)
            cache.Put(key, "value");

        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(63);
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].Arms.Should().Be(1);
        clock.Timers[0].CapturedContext.Should().BeNull();
        clock.Advance(TimeSpan.FromTicks(5_000));
        WaitForDrain(engine);
        engine.GetMaintenanceStatistics().Requests.Should().Be(1);
        cache.EstimatedCount.Should().Be(63);
        engine.AssertInvariants();
    }

    [Test]
    public void QuarterCapacityRequestsWithoutWaitingForTheBackstop()
    {
        var clock = new TrackingClock();
        long initialTimestamp = clock.GetTimestamp();
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 64; key++)
            cache.Put(key, "value");
        WaitForDrain(engine);
        engine.GetMaintenanceStatistics().Requests.Should().BeGreaterThan(0);
        clock.GetTimestamp().Should().Be(initialTimestamp);
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].Arms.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    public void SmallWriteBuffersRequestTheFirstWriteImmediately(int capacity)
    {
        var clock = new TrackingClock();
        using var engine = CreateEngine(clock, capacity: capacity);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        WaitForDrain(engine);
        engine.GetMaintenanceStatistics().Requests.Should().Be(1);
        clock.Timers.Should().BeEmpty();
        engine.AssertInvariants();
    }

    [Test]
    public async Task ConcurrentFirstWritersCreateAndArmOnlyOneTimer()
    {
        await using var hook = new BlockingTestHook(Watchdog);
        var clock = new TrackingClock { BeforeCreate = hook.Invoke };
        await using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        // Keep the captured cache: finally releases timer creation and awaits this writer before cache cleanup.
        // ReSharper disable once AccessToDisposedClosure
        Task first = Task.Run(() => cache.Put(0, "first"));
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            await Task.WhenAll(
                    // Keep the captured cache: Task.WhenAll joins these contending writers before cache cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    Enumerable.Range(1, 16).Select(key => Task.Run(() => cache.Put(key, "value")))
                )
                .WaitAsync(Watchdog, CancellationToken.None);
            // Contending writers may start a drain while the first timer is still being created.
            engine.GetPolicyWriteBufferStatistics().Enqueued.Should().Be(17);
        }
        finally
        {
            hook.Release();
            await first.WaitAsync(Watchdog, CancellationToken.None);
        }
        clock.CreateCalls.Should().Be(1);
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].Arms.Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        WaitForDrain(engine);
        cache.EstimatedCount.Should().Be(17);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoadPublicationUsesTheSameBackstop(bool asynchronous)
    {
        var clock = new TrackingClock();
        await using var engine = CreateEngine(clock);
        if (asynchronous)
        {
            var release = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                (_, _) => release.Task
            );
            Task<string> load = cache.GetAsync(1).AsTask();
            try
            {
                clock.Timers.Should().BeEmpty();
                release.TrySetResult("loaded");
                (await load.WaitAsync(Watchdog, CancellationToken.None)).Should().Be("loaded");
                AssertDeferredLoad();
            }
            finally
            {
                release.TrySetResult("loaded");
            }
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, _ => "loaded");
            cache.Get(1).Should().Be("loaded");
            AssertDeferredLoad();
        }

        return;

        void AssertDeferredLoad()
        {
            engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(1);
            engine.GetMaintenanceStatistics().Requests.Should().Be(0);
            clock.Timers.Should().ContainSingle();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            WaitForDrain(engine);
            engine.AssertInvariants();
        }
    }

    [Test]
    public void TheBackstopAlsoReplaysASmallReadTail()
    {
        var clock = new TrackingClock();
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        cache.Policy.Eviction!.SetMaximum(1_024);
        cache.Put(1, "value");
        cache.CleanUp();
        cache.TryGet(1, out _).Should().BeTrue();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);
        cache.Put(2, "next");
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);
        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        WaitForDrain(engine);
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public void PressureRequestsBelowThresholdAndCleanupRetainsTheCountBound()
    {
        var clock = new TrackingClock();
        using var engine = CreateEngine(clock, maximum: 2);
        using var cache = new Cache<int, string>(engine);
        cache.Put(0, "first");
        cache.Put(1, "second");
        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        cache.Put(2, "pressure");
        engine.GetMaintenanceStatistics().Requests.Should().BeGreaterThan(0);
        for (int key = 3; key < 600; key++)
        {
            cache.Put(key, "value");
            cache.EstimatedCount.Should().BeLessThanOrEqualTo(2 + 256);
        }
        cache.CleanUp();
        WaitForDrain(engine);
        cache.EstimatedCount.Should().BeLessThanOrEqualTo(2);
        engine.AssertInvariants();
    }

    [Test]
    public void ExplicitCleanupConsumesASmallTailBeforeTheTimer()
    {
        var clock = new TrackingClock();
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        cache.CleanUp();
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public async Task ALastSubthresholdWriteRequestsTheStillActiveOwner()
    {
        var clock = new TrackingClock();
        long initialTimestamp = clock.GetTimestamp();
        await using var hook = new BlockingTestHook(Watchdog);
        int passes = 0;
        await using var engine = CreateEngine(
            clock,
            hooks: new LoadingCacheTestHooks
            {
                AfterPolicyMaintenance = () =>
                {
                    if (Interlocked.Increment(ref passes) == 1)
                        // Keep the maintenance hook: drain and engine cleanup precede the hook scope ending.
                        // ReSharper disable once AccessToDisposedClosure
                        hook.Invoke();
                },
            }
        );
        using var cache = new Cache<int, string>(engine);
        try
        {
            for (int key = 0; key < 64; key++)
                cache.Put(key, "first batch");
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            cache.Put(64, "last write");
            engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(1);
            engine.GetMaintenanceStatistics().Requests.Should().Be(2);
        }
        finally
        {
            hook.Release();
        }
        WaitForDrain(engine);
        clock.GetTimestamp().Should().Be(initialTimestamp);
        cache.EstimatedCount.Should().Be(65);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void AnInjectedSchedulerStillReceivesTheFirstWriteImmediately(bool reject)
    {
        var clock = new TrackingClock();
        var scheduler = new TestScheduler(reject);
        using var engine = CreateEngine(clock, scheduler: scheduler);
        // Keep this engine capture: the injected scheduler runs within the engine scope to check lock ownership.
        // ReSharper disable once AccessToDisposedClosure
        scheduler.IsLockHeld = () => engine.IsCoordinationLockHeldForTesting;
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        scheduler.Calls.Should().Be(1);
        scheduler.SawLock.Should().BeFalse();
        clock.Timers.Should().BeEmpty();
        if (reject)
        {
            engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
            engine.GetMaintenanceStatistics().ScheduleRejections.Should().Be(1);
        }
        else
        {
            engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(1);
            scheduler.Callback!();
        }
        engine.AssertInvariants();
    }

    [Test]
    [Arguments("weighted")]
    [Arguments("prompt")]
    [Arguments("ttl")]
    [Arguments("tti")]
    [Arguments("variable")]
    [Arguments("removal")]
    [Arguments("eviction")]
    public void ImmediateConfigurationsDoNotCreateAWriteBackstop(string mode)
    {
        var clock = new TrackingClock();
        using var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = mode == "weighted" ? null : 1_024,
                MaximumWeight = mode == "weighted" ? 1_024 : null,
                MaximumResidentCount = mode == "weighted" ? 1_024 : null,
                Weigher = mode == "weighted" ? static (_, _) => 1 : null,
                TimeProvider = clock,
                EnableExpirationScheduler = mode == "prompt",
                ExpireAfterWrite = mode is "prompt" or "ttl" ? TimeSpan.FromHours(1) : null,
                ExpireAfterAccess = mode == "tti" ? TimeSpan.FromHours(1) : null,
                Expiry = mode == "variable" ? new OneHourExpiry() : null,
                RemovalListener = mode == "removal" ? static _ => { } : null,
                EvictionListener = mode == "eviction" ? static _ => { } : null,
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        if (mode != "eviction")
            engine.GetMaintenanceStatistics().Requests.Should().BeGreaterThan(0);
        if (mode is "ttl" or "tti" or "variable")
        {
            engine.GetMaintenanceStatistics().Requests.Should().Be(1);
            clock.CreateCalls.Should().Be(0);
        }
        WaitForDrain(engine);
        // Prompt expiration owns its existing timer, but must never arm a 1 ms write timer.
        clock
            .Timers.SelectMany(timer => timer.Delays)
            .Should()
            .NotContain(TimeSpan.FromMilliseconds(1));
        engine.AssertInvariants();
    }

    [Test]
    [Arguments("create")]
    [Arguments("change")]
    [Arguments("reject-change")]
    public void TimerFailureFlushesReliablyAndDoesNotRetryPerWrite(string failure)
    {
        var clock = new TrackingClock { Failure = failure };
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "first");
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        cache.Put(2, "second");
        engine.GetPolicyWriteBufferStatistics().Queued.Should().Be(0);
        clock.CreateCalls.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    public void SynchronousTimerCallbacksRunOutsideCoordinationLocks()
    {
        var clock = new TrackingClock { FireOnCreate = true, FireOnChange = true };
        using var engine = CreateEngine(clock);
        // Keep this engine capture: the fake timer runs synchronously within the engine scope to check lock ownership.
        // ReSharper disable once AccessToDisposedClosure
        clock.IsLockHeld = () => engine.IsCoordinationLockHeldForTesting;
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        WaitForDrain(engine);
        clock.SawLock.Should().BeFalse();
        clock.Timers.Should().ContainSingle();
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalClosesTheTimerAndLateCallbacksDoNothing(bool asynchronous)
    {
        var clock = new TrackingClock();
        var engine = CreateEngine(clock);
        var cache = new Cache<int, string>(engine);
        cache.Put(1, "value");
        if (asynchronous)
            await engine.DisposeAsync();
        else
            cache.Dispose();
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].Disposals.Should().Be(1);
        clock.Timers[0].FireEvenIfDisposed();
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Disposed);
        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        cache.Invoking(c => c.Put(2, "late")).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalDuringTimerCreationClosesTheLateTimer(bool asynchronous)
    {
        await using var hook = new BlockingTestHook(Watchdog);
        var clock = new TrackingClock { BeforeCreate = hook.Invoke };
        // Keep synchronous scope cleanup: this test compares Dispose and DisposeAsync while timer creation is paused.
        // ReSharper disable once UseAwaitUsing
        using var engine = CreateEngine(clock);
        using var cache = new Cache<int, string>(engine);
        // Keep this captured cache: disposal races paused timer creation and finally joins the writer.
        // ReSharper disable once AccessToDisposedClosure
        Task put = Task.Run(() => cache.Put(1, "value"));
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            if (asynchronous)
                await engine.DisposeAsync();
            else
                // Dispose here to test synchronous teardown during timer creation; retain the using guard for failure cleanup.
                // ReSharper disable once DisposeOnUsingVariable
                cache.Dispose();
        }
        finally
        {
            hook.Release();
            await put.WaitAsync(Watchdog, CancellationToken.None);
        }
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].Disposals.Should().Be(1);
        clock.Timers[0].Arms.Should().Be(0);
        clock.Timers[0].FireEvenIfDisposed();
        engine.GetMaintenanceStatistics().Requests.Should().Be(0);
        engine.GetMaintenanceStatistics().State.Should().Be(MaintenanceCoordinatorState.Disposed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingBackstopDisposalCannotHideTerminalLoadPromises(bool asynchronous)
    {
        await using var hook = new BlockingTestHook(Watchdog);
        var clock = new TrackingClock { BeforeDispose = hook.Invoke };
        // Keep synchronous scope cleanup: the test compares blocking Dispose and DisposeAsync with a pending load.
        // ReSharper disable once UseAwaitUsing
        using var engine = CreateEngine(clock);
        using var manual = new Cache<int, string>(engine);
        manual.Put(1, "ready");
        var backend = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var loading = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) => backend.Task
        );
        Task<string> pending = loading.GetAsync(2).AsTask();
        Task dispose = Task.Run(async () =>
        {
            if (asynchronous)
                // Keep the captured engine: this task exercises async disposal and is joined in finally before scope cleanup.
                // ReSharper disable once AccessToDisposedClosure
                await engine.DisposeAsync();
            else
                // Keep this explicit synchronous disposal: the test pauses timer teardown and joins the task before scope cleanup.
                // ReSharper disable once AccessToDisposedClosure, DisposeOnUsingVariable
                engine.Dispose();
        });
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            pending.IsCompleted.Should().BeTrue();
            await FluentActions
                .Awaiting(() => pending)
                .Should()
                .ThrowExactlyAsync<ObjectDisposedException>();
            clock.Timers[0].FireEvenIfDisposed();
            engine.GetMaintenanceStatistics().Requests.Should().Be(0);
            dispose.IsCompleted.Should().BeFalse();
        }
        finally
        {
            hook.Release();
            backend.TrySetResult("late");
            await dispose.WaitAsync(Watchdog, CancellationToken.None);
        }
    }

    [Test]
    public void ClearReleasesOldPayloadsAndAnOldTimerMayDrainOnlyTheNewGeneration()
    {
        var clock = new TrackingClock();
        using var engine = new CacheEngine<int, object>(
            new CacheEngineOptions<int, object> { MaximumSize = 1_024, TimeProvider = clock }
        );
        using var cache = new Cache<int, object>(engine);
        WeakReference retired = PutAndClear(cache);
        Collect(retired);
        retired.IsAlive.Should().BeFalse();
        object replacement = new();
        cache.Put(1, replacement);
        clock.Timers[0].Arms.Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        SpinWait
            // Keep the captured engine: SpinUntil reads synchronously before the engine scope ends.
            // ReSharper disable once AccessToDisposedClosure
            .SpinUntil(() => engine.GetPolicyWriteBufferStatistics().Queued == 0, Watchdog)
            .Should()
            .BeTrue();
        cache.TryGet(1, out object? found).Should().BeTrue();
        found.Should().BeSameAs(replacement);
        engine.AssertInvariants();
    }

    [Test]
    public void AnArmedTimerDoesNotRetainItsCacheEngineValuesOrExecutionContext()
    {
        var clock = new TrackingClock();
        WeakReference[] weak = CreateAbandoned(clock);
        foreach (WeakReference reference in weak)
        {
            Collect(reference);
            reference.IsAlive.Should().BeFalse();
        }
        clock.Timers.Should().ContainSingle();
        clock.Timers[0].CapturedContext.Should().BeNull();
        clock.Timers[0].FireEvenIfDisposed();
        GC.KeepAlive(clock);
    }

    private static CacheEngine<int, string> CreateEngine(
        TimeProvider clock,
        int maximum = 1_024,
        IMaintenanceScheduler? scheduler = null,
        LoadingCacheTestHooks? hooks = null,
        int capacity = 256
    ) =>
        new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = maximum,
                MaxConcurrentLoads = 1,
                SupportsBulkLoading = false,
                TimeProvider = clock,
                MaintenanceScheduler = scheduler,
                TestHooks = hooks,
                MaintenanceWriteBufferCapacity = capacity,
            }
        );

    private static void WaitForDrain(CacheEngine<int, string> engine) =>
        SpinWait
            .SpinUntil(() => engine.GetPolicyWriteBufferStatistics().Queued == 0, Watchdog)
            .Should()
            .BeTrue();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PutAndClear(Cache<int, object> cache)
    {
        object value = new();
        cache.Put(1, value);
        var weak = new WeakReference(value);
        cache.Clear();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateAbandoned(TrackingClock clock)
    {
        object ambient = new();
        Ambient.Value = ambient;
        try
        {
            var engine = new CacheEngine<int, object>(
                new CacheEngineOptions<int, object> { MaximumSize = 1_024, TimeProvider = clock }
            );
            var cache = new Cache<int, object>(engine);
            object value = new();
            cache.Put(1, value);
            return
            [
                new WeakReference(cache),
                new WeakReference(engine),
                new WeakReference(value),
                new WeakReference(ambient),
            ];
        }
        finally
        {
            Ambient.Value = null;
        }
    }

    private static void Collect(WeakReference weak)
    {
        for (int attempt = 0; attempt < 8 && weak.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class OneHourExpiry : IExpiry<int, string>
    {
        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromHours(1);

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            currentDuration;

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration) =>
            currentDuration;
    }

    private sealed class TestScheduler(bool reject) : IMaintenanceScheduler
    {
        internal int Calls;
        internal Action? Callback;
        internal Func<bool>? IsLockHeld;
        internal bool SawLock;

        public bool TrySchedule(Action callback)
        {
            Calls++;
            SawLock |= IsLockHeld?.Invoke() == true;
            Callback = callback;
            return !reject;
        }
    }

    private sealed class TrackingClock : FakeTimeProvider
    {
        internal readonly List<TrackingTimer> Timers = [];
        internal string? Failure { get; init; }
        internal bool FireOnCreate { get; init; }
        internal bool FireOnChange { get; init; }
        internal Action? BeforeCreate { get; init; }
        internal Action? BeforeDispose { get; init; }
        internal Func<bool>? IsLockHeld;
        internal bool SawLock;
        internal int CreateCalls;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            CreateCalls++;
            BeforeCreate?.Invoke();
            SawLock |= IsLockHeld?.Invoke() == true;
            if (Failure == "create")
                throw new InvalidOperationException("timer creation failure");
            var timer = new TrackingTimer(
                this,
                base.CreateTimer(callback, state, dueTime, period),
                callback,
                state
            );
            Timers.Add(timer);
            if (FireOnCreate)
                callback(state);
            return timer;
        }
    }

    private sealed class TrackingTimer(
        TrackingClock clock,
        ITimer timer,
        TimerCallback callback,
        object? state
    ) : ITimer
    {
        internal readonly ExecutionContext? CapturedContext = ExecutionContext.Capture();
        internal readonly List<TimeSpan> Delays = [];
        internal int Arms;
        internal int Disposals;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.SawLock |= clock.IsLockHeld?.Invoke() == true;
            switch (clock.Failure)
            {
                case "change":
                    throw new InvalidOperationException("timer change failure");
                case "reject-change":
                    return false;
            }
            if (dueTime == Timeout.InfiniteTimeSpan)
                return timer.Change(dueTime, period);
            Arms++;
            Delays.Add(dueTime);
            if (clock.FireOnChange)
                callback(state);
            return timer.Change(dueTime, period);
        }

        internal void FireEvenIfDisposed() => callback(state);

        public void Dispose()
        {
            Disposals++;
            clock.BeforeDispose?.Invoke();
            timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
