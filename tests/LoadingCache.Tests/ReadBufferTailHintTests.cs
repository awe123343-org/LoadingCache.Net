using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferTailHintTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false, 0L)]
    [Arguments(true, 0L)]
    [Arguments(false, long.MaxValue)]
    [Arguments(true, long.MaxValue)]
    [Arguments(false, -1L)]
    [Arguments(true, -1L)]
    public async Task StaleTailCannotOverwriteTheWinningProducerAndAllowsReuseAfterDrain(
        bool recordStatistics,
        long initialCounter
    )
    {
        using StripedReadBuffer<int> buffer = new(1, 1, recordStatistics);
        await using BlockingTestHook reservation = new(TestTimeout);
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(buffer.TryRead(out _)).IsTrue();
        buffer.SetCounterForTesting(initialCounter);
        int reservations = 0;
        Action pause = reservation.Invoke;
        buffer.SetHooksForTesting(
            beforeReserve: () =>
            {
                if (Interlocked.Increment(ref reservations) == 1)
                {
                    pause();
                }
            },
            beforePublish: null
        );
        Task<ReadBufferOfferResult> paused = Task.Factory.StartNew(
            static state => ((StripedReadBuffer<int>)state!).TryOffer(1),
            buffer,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        try
        {
            await reservation.Entered.WaitAsync(TestTimeout);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Success);
            reservation.Release();
            await Assert
                .That((await paused.WaitAsync(TestTimeout)))
                .IsEqualTo(ReadBufferOfferResult.Full);
            List<int> observed = [];
            await Assert.That(buffer.DrainTo(observed.Add, 1)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.DrainTo(observed.Add, 1)).IsEqualTo(0);
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.DrainTo(observed.Add, 1)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.DrainTo(observed.Add, 1)).IsEqualTo(0);
            await Assert.That(reservation.TimedOut).IsFalse();
        }
        finally
        {
            reservation.Release();
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
}
