using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferShutdownRaceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownCountsAProducerThatFinishesAfterTheTailSnapshot(bool recordStatistics)
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics);
        try
        {
            await using BlockingTestHook reservation = new(TestTimeout);
            Action releaseReservation = reservation.Release;
            await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
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
                await Assert
                    .That((await producer.WaitAsync(TestTimeout)))
                    .IsIn([ReadBufferOfferResult.Success, ReadBufferOfferResult.Shutdown]);
                ReadBufferStatistics statistics = buffer.GetStatistics();
                await Assert.That(statistics.Enqueued).IsEqualTo(recordStatistics ? 2 : 0);
                await Assert.That(statistics.Dequeued).IsEqualTo(0);
                await Assert.That(statistics.DroppedShutdown).IsEqualTo(recordStatistics ? 2 : 0);
                await Assert.That(statistics.Queued).IsEqualTo(0);
                await Assert.That(reservation.TimedOut).IsFalse();
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
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
                await Assert
                    .That((await producer.WaitAsync(TestTimeout)))
                    .IsEqualTo(ReadBufferOfferResult.Shutdown);
                ReadBufferStatistics statistics = buffer.GetStatistics();
                await Assert.That(statistics.Enqueued).IsEqualTo(recordStatistics ? 2 : 0);
                await Assert.That(statistics.Dequeued).IsEqualTo(0);
                await Assert.That(statistics.DroppedShutdown).IsEqualTo(recordStatistics ? 2 : 0);
                await Assert.That(statistics.DroppedFull).IsEqualTo(0);
                await Assert.That(statistics.DroppedFailed).IsEqualTo(0);
                await Assert.That(statistics.Queued).IsEqualTo(0);
                await Assert.That(reservation.TimedOut).IsFalse();
                await Assert.That(publication.TimedOut).IsFalse();
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownDuringTheFinalFailedReservationCountsTheRejectedOfferOnce(
        bool recordStatistics
    )
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics);
        try
        {
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
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
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Shutdown);
            await Assert.That(reservations).IsEqualTo(3);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.Enqueued).IsEqualTo(recordStatistics ? 1 : 0);
            await Assert.That(statistics.Dequeued).IsEqualTo(0);
            await Assert.That(statistics.DroppedFull).IsEqualTo(0);
            await Assert.That(statistics.DroppedFailed).IsEqualTo(0);
            await Assert.That(statistics.DroppedShutdown).IsEqualTo(recordStatistics ? 2 : 0);
            await Assert.That(statistics.Queued).IsEqualTo(0);
        }
        finally
        {
            buffer.Dispose();
        }
    }
}
