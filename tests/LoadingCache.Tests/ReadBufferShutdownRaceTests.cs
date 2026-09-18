using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ReadBufferShutdownRaceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ShutdownCountsAProducerThatFinishesAfterTheTailSnapshot(bool recordStatistics)
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics);
        try
        {
            await using BlockingTestHook reservation = new(TestTimeout);
            Action releaseReservation = reservation.Release;
            buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
            buffer.SetHooksForTesting(reservation.Invoke, beforePublish: null);
            Task<ReadBufferOfferResult> producer = Task.Factory.StartNew(
                static state => ((StripedReadBuffer<int>)state!).TryOffer(1),
                buffer,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            buffer.SetShutdownHookForTesting(_ =>
            {
                releaseReservation();
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
                statistics.Enqueued.Should().Be(recordStatistics ? 2 : 0);
                statistics.Dequeued.Should().Be(0);
                statistics.DroppedShutdown.Should().Be(recordStatistics ? 2 : 0);
                statistics.Queued.Should().Be(0);
                reservation.TimedOut.Should().BeFalse();
            }
            finally
            {
                releaseReservation();
                await producer.WaitAsync(TestTimeout);
                buffer.SetShutdownHookForTesting(null);
                buffer.SetHooksForTesting(null, null);
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompetingRingDisposersCannotChangeTheOwnersShutdownBoundary(
        bool recordStatistics
    )
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics);
        try
        {
            await using BlockingTestHook reservation = new(TestTimeout);
            Action releaseReservation = reservation.Release;
            await using BlockingTestHook publication = new(TestTimeout);
            Task publicationEntered = publication.Entered;
            buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
            buffer.SetHooksForTesting(reservation.Invoke, publication.Invoke);
            Task<ReadBufferOfferResult> producer = Task.Factory.StartNew(
                static state => ((StripedReadBuffer<int>)state!).TryOffer(1),
                buffer,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
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
                releaseReservation();
                publicationEntered.WaitAsync(TestTimeout).GetAwaiter().GetResult();
                competingDisposer = Task.Factory.StartNew(
                    disposeSameRing,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                );
                competingDisposer.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            });

            try
            {
                await reservation.Entered.WaitAsync(TestTimeout);
                buffer.Dispose();
                publication.Release();
                (await producer.WaitAsync(TestTimeout)).Should().Be(ReadBufferOfferResult.Shutdown);

                ReadBufferStatistics statistics = buffer.GetStatistics();
                statistics.Enqueued.Should().Be(recordStatistics ? 2 : 0);
                statistics.Dequeued.Should().Be(0);
                statistics.DroppedShutdown.Should().Be(recordStatistics ? 2 : 0);
                statistics.DroppedFull.Should().Be(0);
                statistics.DroppedFailed.Should().Be(0);
                statistics.Queued.Should().Be(0);
                reservation.TimedOut.Should().BeFalse();
                publication.TimedOut.Should().BeFalse();
            }
            finally
            {
                releaseReservation();
                publication.Release();
                await Task.WhenAll(producer, competingDisposer ?? Task.CompletedTask)
                    .WaitAsync(TestTimeout);
                buffer.SetShutdownHookForTesting(null);
                buffer.SetHooksForTesting(null, null);
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ShutdownDuringTheFinalFailedReservationCountsTheRejectedOfferOnce(
        bool recordStatistics
    )
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics);
        try
        {
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
            buffer.SetForcedCasFailuresForTesting(3);
            int reservations = 0;
            Action shutdown = buffer.Dispose;
            buffer.SetHooksForTesting(
                beforeReserve: () =>
                {
                    if (++reservations == 3)
                    {
                        shutdown();
                    }
                },
                beforePublish: null
            );

            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Shutdown);

            reservations.Should().Be(3);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.Enqueued.Should().Be(recordStatistics ? 1 : 0);
            statistics.Dequeued.Should().Be(0);
            statistics.DroppedFull.Should().Be(0);
            statistics.DroppedFailed.Should().Be(0);
            statistics.DroppedShutdown.Should().Be(recordStatistics ? 2 : 0);
            statistics.Queued.Should().Be(0);
        }
        finally
        {
            buffer.Dispose();
        }
    }
}
