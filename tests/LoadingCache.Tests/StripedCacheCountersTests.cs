using System.Collections.Concurrent;
using System.Numerics;
using LoadingCache.Diagnostics;

namespace LoadingCache.Tests;

public sealed class StripedCacheCountersTests
{
    [Test]
    public async Task ParallelAddsAreExactAfterQuiescence()
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
        await Assert
            .That(snapshot[CacheCounterKind.Hits])
            .IsEqualTo(workerCount * incrementsPerWorker);
        await Assert
            .That(snapshot[CacheCounterKind.TotalLoadTimeTicks])
            .IsEqualTo(workerCount * incrementsPerWorker * 3L);
    }

    [Test]
    public async Task SingleStripeCounterSaturatesAtLongMaxValue()
    {
        StripedCacheCounters counters = new(1);
        counters.Add(CacheCounterKind.Misses, long.MaxValue - 1);
        counters.Add(CacheCounterKind.Misses, 2);
        await Assert.That(counters.Snapshot()[CacheCounterKind.Misses]).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task EveryCounterSaturatesAcrossRepeatedUnitAdds()
    {
        StripedCacheCounters counters = new(1);
        for (int index = 0; index < (int)CacheCounterKind.Count; index++)
        {
            CacheCounterKind counter = (CacheCounterKind)index;
            counters.Add(counter, long.MaxValue - 1);
            counters.Add(counter);
            await Assert.That(counters.Snapshot()[counter]).IsEqualTo(long.MaxValue);
            counters.Add(counter);
            await Assert.That(counters.Snapshot()[counter]).IsEqualTo(long.MaxValue);
        }
    }

    [Test]
    [Arguments(37L)]
    [Arguments(long.MaxValue - 17)]
    public async Task CollidingUnitAndArbitraryAddsAreExactAfterQuiescence(long initialValue)
    {
        StripedCacheCounters counters = new(4);
        const int stripe = 2;
        const int incrementsPerWorker = 2_048;
        long[] deltas = [0, 2, 17, 1_024];
        counters.AddToStripeForTesting(stripe, CacheCounterKind.TotalLoadTimeTicks, initialValue);
        using CountdownEvent ready = new(deltas.Length);
        using ManualResetEventSlim start = new();
        Task[] workers = new Task[deltas.Length];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Factory.StartNew(
                static state =>
                {
                    var work = ((
                        StripedCacheCounters Counters,
                        CountdownEvent Ready,
                        ManualResetEventSlim Start,
                        long Delta
                    ))
                        state!;
                    work.Ready.Signal();
                    work.Start.Wait();
                    for (int index = 0; index < incrementsPerWorker; index++)
                    {
                        work.Counters.AddToStripeForTesting(
                            stripe,
                            CacheCounterKind.TotalLoadTimeTicks
                        );
                        work.Counters.AddToStripeForTesting(
                            stripe,
                            CacheCounterKind.TotalLoadTimeTicks,
                            work.Delta
                        );
                    }
                },
                (counters, ready, start, deltas[worker]),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        try
        {
            await Assert.That(ready.Wait(TimeSpan.FromSeconds(10))).IsTrue();
        }
        finally
        {
            // The gate starts colliding writers together. Exact counts are
            // asserted only after every writer has completed.
            start.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        BigInteger sum = deltas.Aggregate(
            (BigInteger)initialValue,
            static (total, delta) => total + (BigInteger)incrementsPerWorker * (delta + 1)
        );
        long expected = (long)BigInteger.Min(sum, long.MaxValue);
        await Assert
            .That(counters.Snapshot()[CacheCounterKind.TotalLoadTimeTicks])
            .IsEqualTo(expected);
    }

    [Test]
    public async Task AggregateSnapshotSaturatesAcrossStripes()
    {
        StripedCacheCounters counters = new(4);
        counters.AddToStripeForTesting(0, CacheCounterKind.EvictedWeight, long.MaxValue - 10);
        counters.AddToStripeForTesting(1, CacheCounterKind.EvictedWeight, 11);
        await Assert
            .That(counters.Snapshot()[CacheCounterKind.EvictedWeight])
            .IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task InvalidCounterAndDeltaAreRejectedWithoutMutation()
    {
        StripedCacheCounters counters = new(1);
        Action invalidCounter = () => counters.Add((CacheCounterKind)byte.MaxValue);
        Action countSentinel = () => counters.Add(CacheCounterKind.Count);
        Action invalidDelta = () => counters.Add(CacheCounterKind.Hits, -1);
        await Assert.That(invalidCounter).Throws<ArgumentOutOfRangeException>();
        await Assert.That(countSentinel).Throws<ArgumentOutOfRangeException>();
        await Assert.That(invalidDelta).Throws<ArgumentOutOfRangeException>();
        await Assert.That(counters.Snapshot()[CacheCounterKind.Hits]).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidStripeCountsAreRejected()
    {
        Action zero = () => CreateCounters(0);
        Action nonPowerOfTwo = () => CreateCounters(3);
        Action tooLarge = () => CreateCounters(128);
        await Assert.That(zero).Throws<ArgumentOutOfRangeException>();
        await Assert.That(nonPowerOfTwo).Throws<ArgumentOutOfRangeException>();
        await Assert.That(tooLarge).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task StripeNormalizationIsBoundedPowerOfTwo()
    {
        await Assert.That(StripedCacheCounters.NormalizeStripeCount(1)).IsEqualTo(1);
        await Assert.That(StripedCacheCounters.NormalizeStripeCount(3)).IsEqualTo(4);
        await Assert.That(StripedCacheCounters.NormalizeStripeCount(64)).IsEqualTo(64);
        await Assert.That(StripedCacheCounters.NormalizeStripeCount(65)).IsEqualTo(64);
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
                    await Assert
                        .That(snapshot[(CacheCounterKind)counter])
                        .IsGreaterThanOrEqualTo(0);
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writer.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task MetadataDoesNotGrowWithThreadsOrKeys()
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
        await Assert.That(counters.StripeCount).IsEqualTo(8);
        await Assert.That(counters.CounterSlotCount).IsEqualTo(slotCount);
        await Assert.That(counters.Snapshot()[CacheCounterKind.Misses]).IsEqualTo(256L * 257 / 2);
    }

    [Test]
    public async Task ZeroDeltaDoesNotChangeCounters()
    {
        StripedCacheCounters counters = new(1);
        counters.Add(CacheCounterKind.Hits, 0);
        counters.AddToStripeForTesting(0, CacheCounterKind.Hits, 0);
        await Assert.That(counters.Snapshot()[CacheCounterKind.Hits]).IsEqualTo(0);
    }

    [Test]
    public Task HotCounterUpdatesDoNotAllocatePerEvent() =>
        AllocationTestProcess.VerifyAsync("counter");

    private static void CreateCounters(int stripeCount) =>
        _ = new StripedCacheCounters(stripeCount);
}
