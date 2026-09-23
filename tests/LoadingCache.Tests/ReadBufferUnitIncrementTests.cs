using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferUnitIncrementTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(true, long.MaxValue - 1)]
    [Arguments(true, long.MaxValue)]
    [Arguments(false, long.MaxValue - 1)]
    [Arguments(false, long.MaxValue)]
    public async Task ActualDroppedOffersSaturateTheCallingThreadsShard(bool full, long initial)
    {
        using StripedReadBuffer<int> buffer = new(1, full ? 1 : 2);
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
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
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(expected);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.DroppedFull).IsEqualTo(full ? long.MaxValue : 0);
            await Assert.That(statistics.DroppedFailed).IsEqualTo(full ? 0 : long.MaxValue);
            await Assert.That(statistics.Dropped).IsEqualTo(long.MaxValue);
            await Assert.That(statistics.Enqueued).IsEqualTo(1);
            await Assert.That(statistics.Queued).IsEqualTo(1);
            await Assert.That(statistics.DroppedShutdown).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UnsaturatedDroppedOffersCountExactlyOnTheCallingThreadsShard(bool full)
    {
        using StripedReadBuffer<int> buffer = new(1, full ? 1 : 2);
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
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
            await Assert.That(buffer.TryOffer(attempt)).IsEqualTo(expected);
        }

        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFull).IsEqualTo(full ? initial + attempts : 0);
        await Assert.That(statistics.DroppedFailed).IsEqualTo(full ? 0 : initial + attempts);
        await Assert.That(statistics.Enqueued).IsEqualTo(1);
        await Assert.That(statistics.Queued).IsEqualTo(1);
        await Assert.That(statistics.DroppedShutdown).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentFailedOffersAreExactAfterAllWritersFinish()
    {
        const int workerCount = 4;
        const int attemptsPerWorker = 4_096;
        using StripedReadBuffer<int> buffer = new(1, 2);
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
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
            await Assert.That(start.SignalAndWait(TestTimeout)).IsTrue();
            int[] unexpectedResults = await completion.WaitAsync(TestTimeout);
            await Assert.That(unexpectedResults).All(static count => count == 0);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.DroppedFailed).IsEqualTo(workerCount * attemptsPerWorker);
            await Assert.That(statistics.DroppedFull).IsEqualTo(0);
            await Assert.That(statistics.DroppedShutdown).IsEqualTo(0);
            await Assert.That(statistics.Enqueued).IsEqualTo(1);
            await Assert.That(statistics.Queued).IsEqualTo(1);
        }
        finally
        {
            await completion.WaitAsync(TestTimeout);
        }
    }
}
