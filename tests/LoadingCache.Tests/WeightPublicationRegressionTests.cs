using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
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
            engine.HasActiveFlights.Should().BeFalse();
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
                read.IsCompletedSuccessfully.Should().BeTrue();
                cache.CleanUp();
                CacheStatistics observed = cache.GetStatistics();
                Volatile.Read(ref active.Value).Should().Be(0);
                observed.InFlightLoads.Should().Be(0);
                observed.MaintenanceBacklog.Should().Be(0);
                observed.WriteBufferBacklog.Should().Be(0);
                engine.HasActiveFlights.Should().BeTrue();
                cache.Policy.Eviction!.WeightedSize.Should().Be(3);
                if (clear)
                {
                    cache.Clear();
                    cache.Set(1, new WeightedPayload(9));
                    cache.CleanUp();
                    engine.HasActiveFlights.Should().BeTrue();
                    cache.Policy.Eviction.WeightedSize.Should().Be(9);
                }

                engine.AssertInvariants();
            }

            (await read.WaitAsync(Watchdog)).Weight.Should().Be(3);
            await started.Task.WaitAsync(Watchdog);
            Volatile.Read(ref active.Value).Should().Be(1);
            cache.GetStatistics().InFlightLoads.Should().Be(1);
            engine.HasActiveFlights.Should().BeTrue();
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

            Volatile.Read(ref active.Value).Should().Be(0);
            engine.HasActiveFlights.Should().BeFalse();
            // Drain only after queued, running and revoked publishers are all retired.
            cache.CleanUp();
            engine.AssertInvariants();
            long weight = cache.Policy.Eviction!.WeightedSize;
            KeyValuePair<int, WeightedPayload>[] residents = engine.DictionarySnapshot();
            residents.Should().ContainSingle();
            residents[0].Value.Weight.Should().Be(clear ? 9 : 16);
            weight.Should().Be(residents.Sum(pair => (long)pair.Value.Weight));
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
