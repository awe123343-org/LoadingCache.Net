using FluentAssertions;
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
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.TryRead(out _).Should().BeTrue();
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
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Success);
            reservation.Release();
            (await paused.WaitAsync(TestTimeout)).Should().Be(ReadBufferOfferResult.Full);
            List<int> observed = [];
            buffer.DrainTo(observed.Add, 1).Should().Be(1);
            observed.Should().Equal(2);
            buffer.DrainTo(observed.Add, 1).Should().Be(0);
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
            buffer.DrainTo(observed.Add, 1).Should().Be(1);
            observed.Should().Equal(2, 1);
            buffer.DrainTo(observed.Add, 1).Should().Be(0);
            reservation.TimedOut.Should().BeFalse();
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
