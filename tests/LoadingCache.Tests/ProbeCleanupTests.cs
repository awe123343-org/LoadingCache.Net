using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using LoadingCache.Maintenance;
using LoadingCache.MemoryCacheProbe;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class ProbeCleanupTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task PausedMaintenanceMayNeedMoreThan256CleanupAttempts(bool statistics)
    {
        var scheduler = new ControlledScheduler();
        await using var hook = new BlockingTestHook(Watchdog);
        var engine = CreateEngine(scheduler, hook, statistics);
        using var cache = new ObservedCache(
            engine,
            pass =>
            {
                if (pass == 257)
                {
                    hook.Release();
                }
            }
        );
        cache.Put(1, "ready");
        Task worker = Task.Run(scheduler.RunNext, CancellationToken.None);
        Task<int>? drain = null;
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            cache.TryGet(1, out _).Should().BeTrue();
            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);
            engine
                .GetMaintenanceStatistics()
                .State.Should()
                .Be(MaintenanceCoordinatorState.Running);

            drain = Task.Run(() => ProbeCleanup.Drain(cache, Watchdog), CancellationToken.None);
            int passes = await drain.WaitAsync(Watchdog, CancellationToken.None);

            passes.Should().BeGreaterThan(256);
            cache.Statistics.MaintenanceBacklog.Should().Be(0);
            cache.Policy.Eviction!.WeightedSize.Should().BeLessOrEqualTo(4);
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
            if (drain is { IsCompleted: false })
            {
                await drain.WaitAsync(Watchdog, CancellationToken.None);
            }
        }

        hook.TimedOut.Should().BeFalse();
        engine.AssertInvariants();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WorkerWithoutProgressStillFailsAtTheWatchdog(bool statistics)
    {
        var scheduler = new ControlledScheduler();
        await using var hook = new BlockingTestHook(Watchdog);
        var engine = CreateEngine(scheduler, hook, statistics);
        using var cache = new ObservedCache(engine);
        cache.Put(1, "ready");
        Task worker = Task.Run(scheduler.RunNext, CancellationToken.None);
        try
        {
            await hook.Entered.WaitAsync(Watchdog, CancellationToken.None);
            cache.TryGet(1, out _).Should().BeTrue();
            engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);

            TimeSpan deadline = TimeSpan.FromMilliseconds(100);
            TimeSpan elapsed = default;
            Task<int> drain = Task.Run(
                () =>
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        return ProbeCleanup.Drain(cache, deadline);
                    }
                    finally
                    {
                        elapsed = Stopwatch.GetElapsedTime(started);
                    }
                },
                CancellationToken.None
            );
            Func<Task> result = async () => await drain.WaitAsync(Watchdog, CancellationToken.None);

            await result
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("LoadingCache cleanup did not converge*");
            elapsed.Should().BeGreaterOrEqualTo(deadline);
            cache.CleanupCalls.Should().BeGreaterThan(0);
            hook.Returned.IsCompleted.Should().BeFalse();
            cache.Statistics.MaintenanceBacklog.Should().Be(1);
        }
        finally
        {
            hook.Release();
            await worker.WaitAsync(Watchdog, CancellationToken.None);
        }

        ProbeCleanup.Drain(cache, Watchdog).Should().BeGreaterThan(0);
        cache.Statistics.MaintenanceBacklog.Should().Be(0);
        hook.TimedOut.Should().BeFalse();
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
            _callbacks.TryDequeue(out Action? callback).Should().BeTrue();
            callback!();
        }
    }
}
