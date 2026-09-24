using System.Runtime.CompilerServices;
using FluentAssertions;
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
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.TryOffer(-1).Should().Be(ReadBufferOfferResult.Failed);
        buffer.StripeCountForTesting.Should().Be(2);
        buffer.SetForcedCasFailuresForTesting(0);
        OfferOnStripe(buffer, 10, 1).Should().Be(ReadBufferOfferResult.Success);
        buffer.DrainTo(static _ => { }, 4).Should().Be(2);
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
            OfferOnStripe(buffer, 11, 1).Should().Be(ReadBufferOfferResult.Success);
            buffer.HasPublished.Should().BeTrue();
            buffer.GetStatistics().Queued.Should().Be(1);
            List<int> observed = [];
            buffer.DrainTo(observed.Add, 4).Should().Be(1);
            observed.Should().Equal(11);
            buffer.HasPublished.Should().BeFalse();
            buffer.GetStatistics().Queued.Should().Be(0);
            publication.Release();
            (await paused.WaitAsync(TestTimeout)).Should().Be(ReadBufferOfferResult.Success);
            buffer.DrainTo(observed.Add, 4).Should().Be(1);
            observed.Should().Equal(11, 1);
            buffer.GetStatistics().Enqueued.Should().Be(4);
            buffer.GetStatistics().Dequeued.Should().Be(4);
            buffer.GetStatistics().Queued.Should().Be(0);
            publication.TimedOut.Should().BeFalse();
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
    public void BoundedBatchReleasesTheConsumedPrefixAcrossCounterWrap(long counter)
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetCounterForTesting(counter);
        for (int value = 1; value <= 4; value++)
        {
            buffer.TryEnqueue(value).Should().BeTrue();
        }

        List<int> observed = [];
        buffer.DrainTo(observed.Add, 3).Should().Be(3);
        buffer.GetStatistics().Queued.Should().Be(1);
        buffer.GetStatistics().Dequeued.Should().Be(3);
        for (int value = 5; value <= 7; value++)
        {
            buffer.TryEnqueue(value).Should().BeTrue();
        }

        buffer.TryOffer(8).Should().Be(ReadBufferOfferResult.Full);
        buffer.DrainTo(observed.Add, 4).Should().Be(4);
        observed.Should().Equal(1, 2, 3, 4, 5, 6, 7);
        buffer.HasPublished.Should().BeFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.Queued.Should().Be(0);
        statistics.Enqueued.Should().Be(7);
        statistics.Dequeued.Should().Be(7);
        statistics.DroppedFull.Should().Be(1);
    }

    [Test]
    public void ThrowingBatchCommitsItsConsumedPrefixAndKeepsTheRemainingEvent()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        for (int value = 1; value <= 4; value++)
        {
            buffer.TryEnqueue(value).Should().BeTrue();
        }

        List<int> observed = [];
        Action drain = buffer.Invoking(current =>
        {
            current.DrainTo(
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
        });
        drain.Should().Throw<InvalidOperationException>();
        ReadBufferStatistics interrupted = buffer.GetStatistics();
        interrupted.Queued.Should().Be(1);
        interrupted.Dequeued.Should().Be(3);
        buffer.TryEnqueue(5).Should().BeTrue();
        buffer.TryEnqueue(6).Should().BeTrue();
        buffer.TryEnqueue(7).Should().BeTrue();
        buffer.DrainTo(observed.Add, 4).Should().Be(4);
        observed.Should().Equal(1, 2, 3, 4, 5, 6, 7);
        buffer.GetStatistics().Queued.Should().Be(0);
        buffer.GetStatistics().Dequeued.Should().Be(7);
    }

    [Test]
    public async Task PublishedCountIncludesEventsBeyondAPausedHeadWithoutMakingItReadable()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await using BlockingTestHook publication = new(TestTimeout);
        Action pausePublication = publication.Invoke;
        buffer.TryEnqueue(0).Should().BeTrue();
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
            buffer.TryEnqueue(2).Should().BeTrue();
            buffer.GetStatistics().Queued.Should().Be(2);
            buffer.GetStatistics().Enqueued.Should().Be(2);
            List<int> observed = [];
            buffer.DrainTo(observed.Add, 4).Should().Be(1);
            observed.Should().Equal(0);
            buffer.GetStatistics().Queued.Should().Be(1);
            buffer.HasPublished.Should().BeFalse();
            publication.Release();
            (await paused.WaitAsync(TestTimeout)).Should().BeTrue();
            buffer.DrainTo(observed.Add, 4).Should().Be(2);
            observed.Should().Equal(0, 1, 2);
            buffer.GetStatistics().Queued.Should().Be(0);
            buffer.GetStatistics().Enqueued.Should().Be(3);
            buffer.GetStatistics().Dequeued.Should().Be(3);
            publication.TimedOut.Should().BeFalse();
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
                queued.TryGetTarget(out _).Should().BeFalse();
                paused.IsCompleted.Should().BeFalse();
                publication.Release();
                (await paused.WaitAsync(TestTimeout)).Should().BeFalse();
                buffer.GetStatistics().Queued.Should().Be(0);
                publication.TimedOut.Should().BeFalse();
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
        buffer.TryEnqueue(value).Should().BeTrue();
        return new WeakReference<object>(value);
    }
}
