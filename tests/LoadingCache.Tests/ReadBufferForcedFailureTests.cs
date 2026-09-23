using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferForcedFailureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BeforeReserveCanReplacePublicationHookForTheSameOffer(bool recordStatistics)
    {
        using ProducerHookState state = new(recordStatistics);
        StripedReadBuffer<int> buffer = state.Buffer;
        List<string> trace = state.Trace;
        buffer.SetHooksForTesting(
            beforeReserve: state.ReplacePublicationHook,
            beforePublish: state.RecordOriginalPublication
        );
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(trace).IsEmpty();
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert
            .That(trace)
            .IsEquivalentTo(
                ["reserve", "replacement", "replacement"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(3);
        await Assert
            .That(observed)
            .IsEquivalentTo([0, 1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClearingHooksDoesNotDisablePendingForcedFailures(bool recordStatistics)
    {
        using ProducerHookState state = new(recordStatistics);
        StripedReadBuffer<int> buffer = state.Buffer;
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.SetHooksForTesting(
            beforeReserve: state.ClearHooks,
            beforePublish: state.RecordPublication
        );
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Failed);
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(state.Reservations).IsEqualTo(1);
        await Assert.That(state.Publications).IsEqualTo(0);
        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(2);
        await Assert
            .That(observed)
            .IsEquivalentTo([0, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFailed).IsEqualTo(recordStatistics ? 1 : 0);
        await Assert.That(statistics.Enqueued).IsEqualTo(recordStatistics ? 2 : 0);
        await Assert.That(statistics.Dequeued).IsEqualTo(recordStatistics ? 2 : 0);
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task BeforeReserveCanClearForcedFailuresForTheSameOffer()
    {
        using ProducerHookState state = new();
        StripedReadBuffer<int> buffer = state.Buffer;
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.SetHooksForTesting(beforeReserve: state.ClearForcedFailures, beforePublish: null);
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(state.Reservations).IsEqualTo(1);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFailed).IsEqualTo(0);
        await Assert.That(statistics.Enqueued).IsEqualTo(2);
        await Assert.That(statistics.Queued).IsEqualTo(2);
        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(2);
        await Assert
            .That(observed)
            .IsEquivalentTo([0, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(buffer.GetStatistics().Dequeued).IsEqualTo(2);
        await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ExhaustedForcedFailuresAllowLaterOffersWithoutReset()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Failed);
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Success);
        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, 4)).IsEqualTo(3);
        await Assert
            .That(observed)
            .IsEquivalentTo([0, 1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFailed).IsEqualTo(1);
        await Assert.That(statistics.DroppedFull).IsEqualTo(0);
        await Assert.That(statistics.DroppedShutdown).IsEqualTo(0);
        await Assert.That(statistics.Enqueued).IsEqualTo(3);
        await Assert.That(statistics.Dequeued).IsEqualTo(3);
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentFiniteFailuresPreserveOfferAndDrainAccounting()
    {
        const int workerCount = 4;
        const int attemptsPerWorker = 256;
        const int totalAttempts = workerCount * attemptsPerWorker;
        const int capacity = 2_048;
        using StripedReadBuffer<int> buffer = new(1, capacity);
        await Assert.That(buffer.TryOffer(-1)).IsEqualTo(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(128);
        using Barrier start = new(workerCount + 1);
        Task<WorkerResult>[] workers = new Task<WorkerResult>[workerCount];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Factory.StartNew(
                static state =>
                {
                    var work = ((StripedReadBuffer<int> Buffer, Barrier Start, int Worker))state!;
                    if (!work.Start.SignalAndWait(TestTimeout))
                    {
                        throw new TimeoutException(
                            "Read-buffer forced-failure writers did not reach the start gate."
                        );
                    }

                    List<int> accepted = [];
                    int failed = 0;
                    int unexpected = 0;
                    for (int attempt = 0; attempt < attemptsPerWorker; attempt++)
                    {
                        int value = work.Worker * attemptsPerWorker + attempt;
                        switch (work.Buffer.TryOffer(value))
                        {
                            case ReadBufferOfferResult.Success:
                                accepted.Add(value);
                                break;
                            case ReadBufferOfferResult.Failed:
                                failed++;
                                break;
                            case ReadBufferOfferResult.Full:
                            case ReadBufferOfferResult.Shutdown:
                            default:
                                unexpected++;
                                break;
                        }
                    }

                    return new WorkerResult(accepted, failed, unexpected);
                },
                (buffer, start, worker),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        Task<WorkerResult[]> completion = Task.WhenAll(workers);
        try
        {
            await Assert.That(start.SignalAndWait(TestTimeout)).IsTrue();
            WorkerResult[] results = await completion.WaitAsync(TestTimeout);
            int failed = results.Sum(static result => result.Failed);
            List<int> accepted = [.. results.SelectMany(static result => result.Accepted)];
            await Assert.That(results.Sum(static result => result.Unexpected)).IsEqualTo(0);
            await Assert.That(failed).IsGreaterThan(0);
            await Assert.That(accepted).IsNotEmpty();
            await Assert.That((accepted.Count + failed)).IsEqualTo(totalAttempts);
            // The finite failure budget is exhausted. No setter resets it before this offer.
            await Assert
                .That(buffer.TryOffer(totalAttempts))
                .IsEqualTo(ReadBufferOfferResult.Success);
            accepted.Add(-1);
            accepted.Add(totalAttempts);
            ReadBufferStatistics beforeDrain = buffer.GetStatistics();
            await Assert.That(beforeDrain.DroppedFailed).IsEqualTo(failed);
            await Assert.That(beforeDrain.DroppedFull).IsEqualTo(0);
            await Assert.That(beforeDrain.DroppedShutdown).IsEqualTo(0);
            await Assert.That(beforeDrain.Enqueued).IsEqualTo(accepted.Count);
            await Assert.That(beforeDrain.Queued).IsEqualTo(accepted.Count);
            List<int> observed = [];
            await Assert.That(buffer.DrainTo(observed.Add, capacity)).IsEqualTo(accepted.Count);
            await Assert.That(observed).HasDistinctItems();
            await Assert.That(observed).IsEquivalentTo(accepted);
            ReadBufferStatistics afterDrain = buffer.GetStatistics();
            await Assert.That(afterDrain.Dequeued).IsEqualTo(accepted.Count);
            await Assert.That(afterDrain.Queued).IsEqualTo(0);
            await Assert.That(afterDrain.DroppedFailed).IsEqualTo(failed);
        }
        finally
        {
            await completion.WaitAsync(TestTimeout);
        }
    }

    private sealed class ProducerHookState(bool recordStatistics = true) : IDisposable
    {
        internal StripedReadBuffer<int> Buffer { get; } = new(1, 4, recordStatistics);
        internal List<string> Trace { get; } = [];
        internal int Reservations { get; private set; }
        internal int Publications { get; private set; }

        internal void ReplacePublicationHook()
        {
            Trace.Add("reserve");
            Buffer.SetHooksForTesting(null, RecordReplacementPublication);
        }

        internal void RecordOriginalPublication() => Trace.Add("original");

        private void RecordReplacementPublication() => Trace.Add("replacement");

        internal void ClearHooks()
        {
            Reservations++;
            Buffer.SetHooksForTesting(null, null);
        }

        internal void RecordPublication() => Publications++;

        internal void ClearForcedFailures()
        {
            Reservations++;
            Buffer.SetForcedCasFailuresForTesting(0);
        }

        public void Dispose()
        {
            Buffer.SetHooksForTesting(null, null);
            Buffer.Dispose();
        }
    }

    private sealed record WorkerResult(List<int> Accepted, int Failed, int Unexpected);
}
