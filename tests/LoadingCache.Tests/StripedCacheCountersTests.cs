using System.Collections.Concurrent;
using FluentAssertions;
using LoadingCache.Diagnostics;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class StripedCacheCountersTests
{
    [Test]
    public void ParallelAddsAreExactAfterQuiescence()
    {
        StripedCacheCounters counters = new(8);
        const int workerCount = 8;
        const int incrementsPerWorker = 20_000;

        Parallel.For(
            0,
            workerCount,
            _ =>
            {
                for (int index = 0; index < incrementsPerWorker; index++)
                {
                    counters.Add(CacheCounterKind.Hits);
                    counters.Add(CacheCounterKind.TotalLoadTimeTicks, 3);
                }
            }
        );

        CacheCounterSnapshot snapshot = counters.Snapshot();
        snapshot[CacheCounterKind.Hits].Should().Be(workerCount * incrementsPerWorker);
        snapshot[CacheCounterKind.TotalLoadTimeTicks]
            .Should()
            .Be(workerCount * incrementsPerWorker * 3L);
    }

    [Test]
    public void SingleStripeCounterSaturatesAtLongMaxValue()
    {
        StripedCacheCounters counters = new(1);

        counters.Add(CacheCounterKind.Misses, long.MaxValue - 1);
        counters.Add(CacheCounterKind.Misses, 2);

        counters.Snapshot()[CacheCounterKind.Misses].Should().Be(long.MaxValue);
    }

    [Test]
    public void AggregateSnapshotSaturatesAcrossStripes()
    {
        StripedCacheCounters counters = new(4);

        counters.AddToStripeForTesting(0, CacheCounterKind.EvictedWeight, long.MaxValue - 10);
        counters.AddToStripeForTesting(1, CacheCounterKind.EvictedWeight, 11);

        counters.Snapshot()[CacheCounterKind.EvictedWeight].Should().Be(long.MaxValue);
    }

    [Test]
    public void InvalidCounterAndDeltaAreRejectedWithoutMutation()
    {
        StripedCacheCounters counters = new(1);

        Action invalidCounter = () => counters.Add((CacheCounterKind)byte.MaxValue);
        Action invalidDelta = () => counters.Add(CacheCounterKind.Hits, -1);

        invalidCounter.Should().Throw<ArgumentOutOfRangeException>();
        invalidDelta.Should().Throw<ArgumentOutOfRangeException>();
        counters.Snapshot()[CacheCounterKind.Hits].Should().Be(0);
    }

    [Test]
    public void InvalidStripeCountsAreRejected()
    {
        Action zero = () => CreateCounters(0);
        Action nonPowerOfTwo = () => CreateCounters(3);
        Action tooLarge = () => CreateCounters(128);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        nonPowerOfTwo.Should().Throw<ArgumentOutOfRangeException>();
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void StripeNormalizationIsBoundedPowerOfTwo()
    {
        StripedCacheCounters.NormalizeStripeCount(1).Should().Be(1);
        StripedCacheCounters.NormalizeStripeCount(3).Should().Be(4);
        StripedCacheCounters.NormalizeStripeCount(64).Should().Be(64);
        StripedCacheCounters.NormalizeStripeCount(65).Should().Be(64);
    }

    [Test]
    public async Task SnapshotValuesRemainNonNegativeDuringConcurrentUpdates()
    {
        StripedCacheCounters counters = new(8);
        using CancellationTokenSource stop = new();
        CancellationToken stopToken = stop.Token;
        ConcurrentBag<Exception> failures = [];

        Task writer = Task.Run(
            () =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        counters.Add(CacheCounterKind.RefreshSuccesses);
                        counters.Add(CacheCounterKind.ListenerFailures);
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            },
            CancellationToken.None
        );

        try
        {
            for (int index = 0; index < 10_000; index++)
            {
                CacheCounterSnapshot snapshot = counters.Snapshot();
                for (int counter = 0; counter < (int)CacheCounterKind.Count; counter++)
                {
                    snapshot[(CacheCounterKind)counter].Should().BeGreaterThanOrEqualTo(0);
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public void MetadataDoesNotGrowWithThreadsOrKeys()
    {
        StripedCacheCounters counters = new(8);
        int slotCount = counters.CounterSlotCount;

        Parallel.For(
            0,
            256,
            index =>
            {
                counters.Add(CacheCounterKind.Misses, index + 1L);
            }
        );

        counters.StripeCount.Should().Be(8);
        counters.CounterSlotCount.Should().Be(slotCount);
        counters.Snapshot()[CacheCounterKind.Misses].Should().Be(256L * 257 / 2);
    }

    [Test]
    public void ZeroDeltaDoesNotChangeCounters()
    {
        StripedCacheCounters counters = new(1);

        counters.Add(CacheCounterKind.Hits, 0);
        counters.AddToStripeForTesting(0, CacheCounterKind.Hits, 0);

        counters.Snapshot()[CacheCounterKind.Hits].Should().Be(0);
    }

    [Test]
    public async Task HotCounterUpdatesDoNotAllocatePerEvent()
    {
        if (await AllocationTestProcess.RunIsolatedIfNeededAsync("counter").ConfigureAwait(false))
            return;
        StripedCacheCounters counters = new(1);
        counters.Add(CacheCounterKind.Hits);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++)
        {
            counters.Add(CacheCounterKind.Hits);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        after.Should().Be(before);
    }

    private static void CreateCounters(int stripeCount) =>
        _ = new StripedCacheCounters(stripeCount);
}
