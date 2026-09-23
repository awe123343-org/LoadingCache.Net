using System.Diagnostics;
using System.Reflection;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class WeightPublicationRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ActiveFlightRegistryFencesQueuedAndRunningAutomaticRefresh(
        bool statistics,
        bool clear
    )
    {
        var clock = new FakeTimeProvider();
        await using var engine = new CacheEngine<int, WeightedPayload>(
            new CacheEngineOptions<int, WeightedPayload>
            {
                MaximumWeight = 64,
                MaximumResidentCount = 8,
                Weigher = static (_, value) => value.Weight,
                RefreshAfterWrite = TimeSpan.FromMilliseconds(50),
                MaxConcurrentLoads = 2,
                RecordStatistics = statistics,
                TimeProvider = clock,
                MaintenanceScheduler = new InlineScheduler(),
            }
        );
        var active = new System.Runtime.CompilerServices.StrongBox<int>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WeightedPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new AsyncLoadingCache<int, WeightedPayload>(
            engine,
            async (_, _) =>
            {
                Interlocked.Increment(ref active.Value);
                started.TrySetResult();
                try
                {
                    return await release.Task.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref active.Value);
                }
            }
        );
        try
        {
            cache.Set(1, new WeightedPayload(3));
            cache.CleanUp();
            await Assert.That(engine.HasActiveFlights).IsFalse();
            clock.Advance(TimeSpan.FromMilliseconds(60));
            Task<WeightedPayload> read;
            object gate = typeof(CacheEngine<int, WeightedPayload>)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)!;
            lock (gate)
            {
                // Reservation is synchronous, but the thread-pool callback cannot enter the
                // gate to count or execute its loader until this owner releases it.
                read = cache.GetAsync(1).AsTask();
                if (!(read.IsCompletedSuccessfully))
                    Assert.Fail("Expected under lock: read.IsCompletedSuccessfully");
                cache.CleanUp();
                CacheStatistics observed = cache.GetStatistics();
                if ((Volatile.Read(ref active.Value)) != (0))
                    Assert.Fail("Expected under lock: (Volatile.Read(ref active.Value)) == (0)");
                if ((observed.InFlightLoads) != (0))
                    Assert.Fail("Expected under lock: (observed.InFlightLoads) == (0)");
                if ((observed.MaintenanceBacklog) != (0))
                    Assert.Fail("Expected under lock: (observed.MaintenanceBacklog) == (0)");
                if ((observed.WriteBufferBacklog) != (0))
                    Assert.Fail("Expected under lock: (observed.WriteBufferBacklog) == (0)");
                if (!(engine.HasActiveFlights))
                    Assert.Fail("Expected under lock: engine.HasActiveFlights");
                if ((cache.Policy.Eviction!.WeightedSize) != (3))
                    Assert.Fail(
                        "Expected under lock: (cache.Policy.Eviction!.WeightedSize) == (3)"
                    );
                if (clear)
                {
                    cache.Clear();
                    cache.Set(1, new WeightedPayload(9));
                    cache.CleanUp();
                    if (!(engine.HasActiveFlights))
                        Assert.Fail("Expected under lock: engine.HasActiveFlights");
                    if ((cache.Policy.Eviction.WeightedSize) != (9))
                        Assert.Fail(
                            "Expected under lock: (cache.Policy.Eviction.WeightedSize) == (9)"
                        );
                }

                engine.AssertInvariants();
            }

            await Assert.That((await read.WaitAsync(Watchdog)).Weight).IsEqualTo(3);
            await started.Task.WaitAsync(Watchdog);
            await Assert.That(Volatile.Read(ref active.Value)).IsEqualTo(1);
            await Assert.That(cache.GetStatistics().InFlightLoads).IsEqualTo(1);
            await Assert.That(engine.HasActiveFlights).IsTrue();
            release.TrySetResult(new WeightedPayload(16));
            var retirementWatchdog = Stopwatch.StartNew();
            while (engine.HasActiveFlights)
            {
                if (retirementWatchdog.Elapsed > Watchdog)
                {
                    throw new TimeoutException("The released automatic refresh did not retire.");
                }

                await Task.Yield();
            }

            await Assert.That(Volatile.Read(ref active.Value)).IsEqualTo(0);
            await Assert.That(engine.HasActiveFlights).IsFalse();
            // Drain only after queued, running and revoked publishers are all retired.
            cache.CleanUp();
            engine.AssertInvariants();
            long weight = cache.Policy.Eviction!.WeightedSize;
            KeyValuePair<int, WeightedPayload>[] residents = engine.DictionarySnapshot();
            await Assert.That(residents).HasSingleItem();
            await Assert.That(residents[0].Value.Weight).IsEqualTo(clear ? 9 : 16);
            await Assert.That(weight).IsEqualTo(residents.Sum(pair => (long)pair.Value.Weight));
        }
        finally
        {
            release.TrySetResult(new WeightedPayload(16));
        }
    }

    private sealed record WeightedPayload(int Weight);

    private sealed class InlineScheduler : IMaintenanceScheduler
    {
        public bool TrySchedule(Action callback)
        {
            callback();
            return true;
        }
    }
}
