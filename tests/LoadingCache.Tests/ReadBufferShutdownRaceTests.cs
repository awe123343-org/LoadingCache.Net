using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ReadBufferShutdownRaceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ShutdownCountsAProducerThatFinishesAfterTheTailSnapshot()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await using BlockingTestHook reservation = new(TestTimeout);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetHooksForTesting(reservation.Invoke, beforePublish: null);
        Task<ReadBufferOfferResult> producer = Task.Run(() => buffer.TryOffer(1));
        buffer.SetShutdownHookForTesting(_ =>
        {
            reservation.Release();
            producer.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        });

        try
        {
            await reservation.Entered.WaitAsync(TestTimeout);
            buffer.Dispose();
            (await producer.WaitAsync(TestTimeout))
                .Should()
                .BeOneOf(ReadBufferOfferResult.Success, ReadBufferOfferResult.Shutdown);

            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.Enqueued.Should().Be(2);
            statistics.Dequeued.Should().Be(0);
            statistics.DroppedShutdown.Should().Be(2);
            statistics.Queued.Should().Be(0);
            reservation.TimedOut.Should().BeFalse();
        }
        finally
        {
            reservation.Release();
            await producer.WaitAsync(TestTimeout);
            buffer.SetShutdownHookForTesting(null);
            buffer.SetHooksForTesting(null, null);
        }
    }

    [Test]
    public async Task CompetingRingDisposersCannotChangeTheOwnersShutdownBoundary()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await using BlockingTestHook reservation = new(TestTimeout);
        await using BlockingTestHook publication = new(TestTimeout);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetHooksForTesting(reservation.Invoke, publication.Invoke);
        Task<ReadBufferOfferResult> producer = Task.Run(() => buffer.TryOffer(1));
        Task? competingDisposer = null;
        int snapshots = 0;
        buffer.SetShutdownHookForTesting(disposeSameRing =>
        {
            if (Interlocked.Increment(ref snapshots) != 1)
            {
                return;
            }

            // The first disposer has captured tail=1. Publish a reservation at position 1
            // before an independent disposer attempts to capture tail=2 for the same ring.
            reservation.Release();
            publication.Entered.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            competingDisposer = Task.Run(disposeSameRing);
            competingDisposer.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        });

        try
        {
            await reservation.Entered.WaitAsync(TestTimeout);
            buffer.Dispose();
            publication.Release();
            (await producer.WaitAsync(TestTimeout)).Should().Be(ReadBufferOfferResult.Shutdown);

            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.Enqueued.Should().Be(2);
            statistics.Dequeued.Should().Be(0);
            statistics.DroppedShutdown.Should().Be(2);
            statistics.DroppedFull.Should().Be(0);
            statistics.DroppedFailed.Should().Be(0);
            statistics.Queued.Should().Be(0);
            reservation.TimedOut.Should().BeFalse();
            publication.TimedOut.Should().BeFalse();
        }
        finally
        {
            reservation.Release();
            publication.Release();
            await producer.WaitAsync(TestTimeout);
            if (competingDisposer is not null)
            {
                await competingDisposer.WaitAsync(TestTimeout);
            }
            buffer.SetShutdownHookForTesting(null);
            buffer.SetHooksForTesting(null, null);
        }
    }

    [Test]
    public void ShutdownDuringTheFinalFailedReservationCountsTheRejectedOfferOnce()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        int reservations = 0;
        buffer.SetHooksForTesting(
            beforeReserve: () =>
            {
                if (++reservations == 3)
                {
                    buffer.Dispose();
                }
            },
            beforePublish: null
        );

        buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Shutdown);

        reservations.Should().Be(3);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.Enqueued.Should().Be(1);
        statistics.Dequeued.Should().Be(0);
        statistics.DroppedFull.Should().Be(0);
        statistics.DroppedFailed.Should().Be(0);
        statistics.DroppedShutdown.Should().Be(2);
        statistics.Queued.Should().Be(0);
    }
}
