using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ReadBufferUnitIncrementTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestCase(true, long.MaxValue - 1)]
    [TestCase(true, long.MaxValue)]
    [TestCase(false, long.MaxValue - 1)]
    [TestCase(false, long.MaxValue)]
    public void ActualDroppedOffersSaturateTheCallingThreadsShard(bool full, long initial)
    {
        using StripedReadBuffer<int> buffer = new(1, full ? 1 : 2);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        if (!full)
        {
            buffer.SetForcedCasFailuresForTesting(int.MaxValue);
        }

        // Diagnostic counters use the managed thread ID, independently of the ring probe.
        int stripe =
            Environment.CurrentManagedThreadId & (buffer.DiagnosticStripeCountForTesting - 1);
        buffer.AddDropStatisticsForTesting(
            stripe,
            droppedFull: full ? initial : 0,
            droppedFailed: full ? 0 : initial
        );
        ReadBufferOfferResult expected = full
            ? ReadBufferOfferResult.Full
            : ReadBufferOfferResult.Failed;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            buffer.TryOffer(1).Should().Be(expected);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.DroppedFull.Should().Be(full ? long.MaxValue : 0);
            statistics.DroppedFailed.Should().Be(full ? 0 : long.MaxValue);
            statistics.Dropped.Should().Be(long.MaxValue);
            statistics.Enqueued.Should().Be(1);
            statistics.Queued.Should().Be(1);
            statistics.DroppedShutdown.Should().Be(0);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void UnsaturatedDroppedOffersCountExactlyOnTheCallingThreadsShard(bool full)
    {
        using StripedReadBuffer<int> buffer = new(1, full ? 1 : 2);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        if (!full)
        {
            buffer.SetForcedCasFailuresForTesting(int.MaxValue);
        }

        const int initial = 37;
        const int attempts = 4_096;
        int stripe =
            Environment.CurrentManagedThreadId & (buffer.DiagnosticStripeCountForTesting - 1);
        buffer.AddDropStatisticsForTesting(
            stripe,
            droppedFull: full ? initial : 0,
            droppedFailed: full ? 0 : initial
        );
        ReadBufferOfferResult expected = full
            ? ReadBufferOfferResult.Full
            : ReadBufferOfferResult.Failed;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            buffer.TryOffer(attempt).Should().Be(expected);
        }

        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedFull.Should().Be(full ? initial + attempts : 0);
        statistics.DroppedFailed.Should().Be(full ? 0 : initial + attempts);
        statistics.Enqueued.Should().Be(1);
        statistics.Queued.Should().Be(1);
        statistics.DroppedShutdown.Should().Be(0);
    }

    [Test]
    public async Task ConcurrentFailedOffersAreExactAfterAllWritersFinish()
    {
        const int workerCount = 4;
        const int attemptsPerWorker = 4_096;
        using StripedReadBuffer<int> buffer = new(1, 2);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(int.MaxValue);
        using Barrier start = new(workerCount + 1);
        Task<int>[] workers = new Task<int>[workerCount];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Factory.StartNew(
                static state =>
                {
                    var work = ((StripedReadBuffer<int> Buffer, Barrier Start))state!;
                    if (!work.Start.SignalAndWait(TestTimeout))
                    {
                        throw new TimeoutException(
                            "Read-buffer counter writers did not reach the start gate."
                        );
                    }

                    int unexpectedResults = 0;
                    for (int attempt = 0; attempt < attemptsPerWorker; attempt++)
                    {
                        if (work.Buffer.TryOffer(attempt) != ReadBufferOfferResult.Failed)
                        {
                            unexpectedResults++;
                        }
                    }
                    return unexpectedResults;
                },
                (buffer, start),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        Task<int[]> completion = Task.WhenAll(workers);
        try
        {
            start.SignalAndWait(TestTimeout).Should().BeTrue();
            int[] unexpectedResults = await completion.WaitAsync(TestTimeout);
            unexpectedResults.Should().OnlyContain(static count => count == 0);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.DroppedFailed.Should().Be(workerCount * attemptsPerWorker);
            statistics.DroppedFull.Should().Be(0);
            statistics.DroppedShutdown.Should().Be(0);
            statistics.Enqueued.Should().Be(1);
            statistics.Queued.Should().Be(1);
        }
        finally
        {
            await completion.WaitAsync(TestTimeout);
        }
    }
}
