using System.Runtime.CompilerServices;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferBatchTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task AnUnpublishedHeadDoesNotPreventAnotherInitializedStripeFromDraining()
    {
        using StripedReadBuffer<int> buffer = new(2, 4);
        await using BlockingTestHook publication = new(TestTimeout);
        Action pausePublication = publication.Invoke;
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        await Assert.That(buffer.TryOffer(-1)).IsEqualTo(ReadBufferOfferResult.Failed);
        await Assert.That(buffer.StripeCountForTesting).IsEqualTo(2);
        buffer.SetForcedCasFailuresForTesting(0);
        await Assert.That(OfferOnStripe(buffer, 10, 1)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(buffer.DrainTo(static _ => { }, 4)).IsEqualTo(2);
        int publications = 0;
        buffer.SetHooksForTesting(
            beforeReserve: null,
            beforePublish: () =>
            {
                if (Interlocked.Increment(ref publications) == 1)
                {
                    pausePublication();
                }
            }
        );
        Task<ReadBufferOfferResult> paused = Task.Factory.StartNew(
            static state => OfferOnStripe((StripedReadBuffer<int>)state!, 1, 0),
            buffer,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        try
        {
            await publication.Entered.WaitAsync(TestTimeout);
            await Assert
                .That(OfferOnStripe(buffer, 11, 1))
                .IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.HasPublished).IsTrue();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(1);
            List<int> observed = [];
            await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([11], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.HasPublished).IsFalse();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
            publication.Release();
            await Assert
                .That((await paused.WaitAsync(TestTimeout)))
                .IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([11, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.GetStatistics().Enqueued).IsEqualTo(4);
            await Assert.That(buffer.GetStatistics().Dequeued).IsEqualTo(4);
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
            await Assert.That(publication.TimedOut).IsFalse();
        }
        finally
        {
            publication.Release();
            await paused.WaitAsync(TestTimeout);
            buffer.SetHooksForTesting(null, null);
        }
    }

    private static ReadBufferOfferResult OfferOnStripe(
        StripedReadBuffer<int> buffer,
        int value,
        uint stripe
    )
    {
        ulong previous = ReadBufferThreadProbe.ExchangeForTesting((1UL << 32) | stripe);
        try
        {
            return buffer.TryOffer(value);
        }
        finally
        {
            ReadBufferThreadProbe.ExchangeForTesting(previous);
        }
    }

    [Test]
    [Arguments(long.MaxValue - 2)]
    [Arguments(-2L)]
    public async Task BoundedBatchReleasesTheConsumedPrefixAcrossCounterWrap(long counter)
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetCounterForTesting(counter);
        for (int value = 1; value <= 4; value++)
        {
            await Assert.That(buffer.TryEnqueue(value)).IsTrue();
        }

        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, 3)).IsEqualTo(3);
        await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(1);
        await Assert.That(buffer.GetStatistics().Dequeued).IsEqualTo(3);
        for (int value = 5; value <= 7; value++)
        {
            await Assert.That(buffer.TryEnqueue(value)).IsTrue();
        }

        await Assert.That(buffer.TryOffer(8)).IsEqualTo(ReadBufferOfferResult.Full);
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(4);
        await Assert
            .That(observed)
            .IsEquivalentTo(
                [1, 2, 3, 4, 5, 6, 7],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(buffer.HasPublished).IsFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.Enqueued).IsEqualTo(7);
        await Assert.That(statistics.Dequeued).IsEqualTo(7);
        await Assert.That(statistics.DroppedFull).IsEqualTo(1);
    }

    [Test]
    public async Task ThrowingBatchCommitsItsConsumedPrefixAndKeepsTheRemainingEvent()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        for (int value = 1; value <= 4; value++)
        {
            await Assert.That(buffer.TryEnqueue(value)).IsTrue();
        }

        List<int> observed = [];
        Action drain = () =>
        {
            buffer.DrainTo(
                value =>
                {
                    observed.Add(value);
                    if (value == 3)
                    {
                        throw new InvalidOperationException("test callback failure");
                    }
                },
                4
            );
        };
        await Assert.That(drain).Throws<InvalidOperationException>();
        ReadBufferStatistics interrupted = buffer.GetStatistics();
        await Assert.That(interrupted.Queued).IsEqualTo(1);
        await Assert.That(interrupted.Dequeued).IsEqualTo(3);
        await Assert.That(buffer.TryEnqueue(5)).IsTrue();
        await Assert.That(buffer.TryEnqueue(6)).IsTrue();
        await Assert.That(buffer.TryEnqueue(7)).IsTrue();
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(4);
        await Assert
            .That(observed)
            .IsEquivalentTo(
                [1, 2, 3, 4, 5, 6, 7],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
        await Assert.That(buffer.GetStatistics().Dequeued).IsEqualTo(7);
    }

    [Test]
    public async Task PublishedCountIncludesEventsBeyondAPausedHeadWithoutMakingItReadable()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await using BlockingTestHook publication = new(TestTimeout);
        Action pausePublication = publication.Invoke;
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        int publishCalls = 0;
        buffer.SetHooksForTesting(
            null,
            () =>
            {
                if (Interlocked.Increment(ref publishCalls) == 1)
                {
                    pausePublication();
                }
            }
        );
        Task<bool> paused = Task.Factory.StartNew(
            static state => ((StripedReadBuffer<int>)state!).TryEnqueue(1),
            buffer,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        try
        {
            await publication.Entered.WaitAsync(TestTimeout);
            await Assert.That(buffer.TryEnqueue(2)).IsTrue();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(2);
            await Assert.That(buffer.GetStatistics().Enqueued).IsEqualTo(2);
            List<int> observed = [];
            await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([0], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(1);
            await Assert.That(buffer.HasPublished).IsFalse();
            publication.Release();
            await Assert.That((await paused.WaitAsync(TestTimeout))).IsTrue();
            await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(2);
            await Assert
                .That(observed)
                .IsEquivalentTo([0, 1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
            await Assert.That(buffer.GetStatistics().Enqueued).IsEqualTo(3);
            await Assert.That(buffer.GetStatistics().Dequeued).IsEqualTo(3);
            await Assert.That(publication.TimedOut).IsFalse();
        }
        finally
        {
            publication.Release();
            try
            {
                await paused.WaitAsync(TestTimeout);
            }
            finally
            {
                buffer.SetHooksForTesting(null, null);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeReleasesOtherQueuedValuesWhileAProducerStillHoldsTheSlotArray(
        bool recordStatistics
    )
    {
        StripedReadBuffer<object> buffer = new(1, 4, recordStatistics);
        try
        {
            await using BlockingTestHook publication = new(TestTimeout);
            WeakReference<object> queued = EnqueueCollectibleValue(buffer);
            buffer.SetHooksForTesting(null, publication.Invoke);
            Task<bool> paused = Task.Factory.StartNew(
                static state => ((StripedReadBuffer<object>)state!).TryEnqueue(new object()),
                buffer,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            try
            {
                await publication.Entered.WaitAsync(TestTimeout);
                buffer.Dispose();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Assert.That(queued.TryGetTarget(out _)).IsFalse();
                await Assert.That(paused.IsCompleted).IsFalse();
                publication.Release();
                await Assert.That((await paused.WaitAsync(TestTimeout))).IsFalse();
                await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
                await Assert.That(publication.TimedOut).IsFalse();
                GC.KeepAlive(buffer);
            }
            finally
            {
                publication.Release();
                try
                {
                    await paused.WaitAsync(TestTimeout);
                }
                finally
                {
                    buffer.SetHooksForTesting(null, null);
                }
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> EnqueueCollectibleValue(StripedReadBuffer<object> buffer)
    {
        object value = new();
        if (!(buffer.TryEnqueue(value)))
            Assert.Fail("Expected buffer.TryEnqueue(value) to be true ().");
        return new WeakReference<object>(value);
    }
}
