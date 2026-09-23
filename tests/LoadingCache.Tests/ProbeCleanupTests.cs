using System.Collections.Concurrent;
using System.Diagnostics;
using LoadingCache.Maintenance;
using LoadingCache.MemoryCacheProbe;

namespace LoadingCache.Tests;

public sealed class ProbeCleanupTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PausedMaintenanceMayNeedMoreThan256CleanupAttempts(bool statistics)
    {
        var scheduler = new ControlledScheduler();
        await using var hook = new BlockingTestHook(Watchdog);
        Action releaseHook = hook.Release;
        var engine = CreateEngine(scheduler, hook, statistics);
        using var cache = new ObservedCache(
            engine,
            pass =>
            {
                if (pass == 257)
                {
                    releaseHook();
                }
            }
        );
        cache.Put(1, "ready");
        Task worker = Task.Run(scheduler.RunNext, CancellationToken.None);
        Task<int>? drain = null;
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
            await Assert
                .That(engine.GetMaintenanceStatistics().State)
                .IsEqualTo(MaintenanceCoordinatorState.Running);
            drain = Task.Factory.StartNew(
                static state => ProbeCleanup.Drain((ObservedCache)state!, Watchdog),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            int passes = await drain.WaitAsync(Watchdog, CancellationToken.None);
            await Assert.That(passes).IsGreaterThan(256);
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(0);
            await Assert.That(cache.Policy.Eviction!.WeightedSize).IsLessThanOrEqualTo(4);
        }
        finally
        {
            hook.Release();
            await Task.WhenAll(worker, drain ?? Task.CompletedTask)
                .WaitAsync(Watchdog, CancellationToken.None);
        }

        await Assert.That(hook.TimedOut).IsFalse();
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WorkerWithoutProgressStillFailsAtTheWatchdog(bool statistics)
    {
        var scheduler = new ControlledScheduler();
        await using var hook = new BlockingTestHook(Watchdog);
        var engine = CreateEngine(scheduler, hook, statistics);
        using var cache = new ObservedCache(engine);
        cache.Put(1, "ready");
        Task worker = Task.Run(scheduler.RunNext, CancellationToken.None);
        Task<int>? drain = null;
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
            TimeSpan deadline = TimeSpan.FromMilliseconds(100);
            var elapsed = new System.Runtime.CompilerServices.StrongBox<TimeSpan>(TimeSpan.Zero);
            drain = Task.Factory.StartNew(
                static state =>
                {
                    (
                        ObservedCache current,
                        TimeSpan timeout,
                        System.Runtime.CompilerServices.StrongBox<TimeSpan> duration
                    ) = ((
                        ObservedCache,
                        TimeSpan,
                        System.Runtime.CompilerServices.StrongBox<TimeSpan>
                    ))
                        state!;
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        return ProbeCleanup.Drain(current, timeout);
                    }
                    finally
                    {
                        duration.Value = Stopwatch.GetElapsedTime(started);
                    }
                },
                (cache, deadline, elapsed),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            Func<Task> result = async () => await drain.WaitAsync(Watchdog, CancellationToken.None);
            await Assert
                .That(result)
                .Throws<InvalidOperationException>()
                .WithMessageMatching("LoadingCache cleanup did not converge*");
            await Assert.That(elapsed.Value).IsGreaterThanOrEqualTo(deadline);
            await Assert.That(cache.CleanupCalls).IsGreaterThan(0);
            await Assert.That(hook.Returned.IsCompleted).IsFalse();
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(1);
        }
        finally
        {
            hook.Release();
            try
            {
                await Task.WhenAll(worker, drain ?? Task.CompletedTask)
                    .WaitAsync(Watchdog, CancellationToken.None);
            }
            catch (InvalidOperationException exception)
                when (exception.Message.StartsWith(
                        "LoadingCache cleanup did not converge",
                        StringComparison.Ordinal
                    )
                )
            {
                // The test above requires this controlled watchdog failure.
            }
        }

        await Assert.That(ProbeCleanup.Drain(cache, Watchdog)).IsGreaterThan(0);
        await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(0);
        await Assert.That(hook.TimedOut).IsFalse();
        engine.AssertInvariants();
    }

    private static CacheEngine<int, string> CreateEngine(
        ControlledScheduler scheduler,
        BlockingTestHook hook,
        bool statistics
    )
    {
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 1,
                RecordStatistics = statistics,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = 4,
                MaintenanceScheduler = scheduler,
                TestHooks = new LoadingCacheTestHooks { AfterPolicyMaintenance = hook.Invoke },
            }
        );
        // Activate read recording before the controlled worker pause.
        engine.Policy.Eviction!.SetMaximum(4);
        return engine;
    }

    private sealed class ObservedCache(
        CacheEngine<int, string> engine,
        Action<int>? afterCleanup = null
    ) : Cache<int, string>(engine), ICache<int, string>
    {
        internal int CleanupCalls { get; private set; }

        void ICache<int, string>.CleanUp()
        {
            base.CleanUp();
            CleanupCalls++;
            afterCleanup?.Invoke(CleanupCalls);
        }
    }

    private sealed class ControlledScheduler : IMaintenanceScheduler
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunNext()
        {
            if (!(_callbacks.TryDequeue(out Action? callback)))
                Assert.Fail("Expected _callbacks.TryDequeue(out Action? callback) to be true ().");
            callback!();
        }
    }
}
