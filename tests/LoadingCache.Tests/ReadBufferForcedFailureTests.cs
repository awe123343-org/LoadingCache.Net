using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ReadBufferForcedFailureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    public void BeforeReserveCanReplacePublicationHookForTheSameOffer(bool recordStatistics)
    {
        using ProducerHookState state = new(recordStatistics);
        StripedReadBuffer<int> buffer = state.Buffer;
        List<string> trace = state.Trace;
        buffer.SetHooksForTesting(
            beforeReserve: state.ReplacePublicationHook,
            beforePublish: state.RecordOriginalPublication
        );

        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        trace.Should().BeEmpty();
        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
        buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Success);

        trace.Should().Equal("reserve", "replacement", "replacement");
        List<int> observed = [];
        buffer.DrainTo(observed.Add, 4).Should().Be(3);
        observed.Should().Equal(0, 1, 2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ClearingHooksDoesNotDisablePendingForcedFailures(bool recordStatistics)
    {
        using ProducerHookState state = new(recordStatistics);
        StripedReadBuffer<int> buffer = state.Buffer;
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.SetHooksForTesting(
            beforeReserve: state.ClearHooks,
            beforePublish: state.RecordPublication
        );

        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Failed);
        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);

        state.Reservations.Should().Be(1);
        state.Publications.Should().Be(0);
        List<int> observed = [];
        buffer.DrainTo(observed.Add, 4).Should().Be(2);
        observed.Should().Equal(0, 1);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedFailed.Should().Be(recordStatistics ? 1 : 0);
        statistics.Enqueued.Should().Be(recordStatistics ? 2 : 0);
        statistics.Dequeued.Should().Be(recordStatistics ? 2 : 0);
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public void BeforeReserveCanClearForcedFailuresForTheSameOffer()
    {
        using ProducerHookState state = new();
        StripedReadBuffer<int> buffer = state.Buffer;
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.SetHooksForTesting(beforeReserve: state.ClearForcedFailures, beforePublish: null);

        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
        state.Reservations.Should().Be(1);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedFailed.Should().Be(0);
        statistics.Enqueued.Should().Be(2);
        statistics.Queued.Should().Be(2);
        List<int> observed = [];
        buffer.DrainTo(observed.Add, 4).Should().Be(2);
        observed.Should().Equal(0, 1);
        buffer.GetStatistics().Dequeued.Should().Be(2);
        buffer.GetStatistics().Queued.Should().Be(0);
    }

    [Test]
    public void ExhaustedForcedFailuresAllowLaterOffersWithoutReset()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        buffer.SetForcedCasFailuresForTesting(3);

        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Failed);
        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
        buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Success);

        List<int> observed = [];
        buffer.DrainTo(observed.Add, 4).Should().Be(3);
        observed.Should().Equal(0, 1, 2);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedFailed.Should().Be(1);
        statistics.DroppedFull.Should().Be(0);
        statistics.DroppedShutdown.Should().Be(0);
        statistics.Enqueued.Should().Be(3);
        statistics.Dequeued.Should().Be(3);
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public async Task ConcurrentFiniteFailuresPreserveOfferAndDrainAccounting()
    {
        const int workerCount = 4;
        const int attemptsPerWorker = 256;
        const int totalAttempts = workerCount * attemptsPerWorker;
        const int capacity = 2_048;
        using StripedReadBuffer<int> buffer = new(1, capacity);
        buffer.TryOffer(-1).Should().Be(ReadBufferOfferResult.Success);
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
            start.SignalAndWait(TestTimeout).Should().BeTrue();
            WorkerResult[] results = await completion.WaitAsync(TestTimeout);
            int failed = results.Sum(static result => result.Failed);
            List<int> accepted = results.SelectMany(static result => result.Accepted).ToList();
            results.Sum(static result => result.Unexpected).Should().Be(0);
            failed.Should().BeGreaterThan(0);
            accepted.Should().NotBeEmpty();
            (accepted.Count + failed).Should().Be(totalAttempts);

            // The finite failure budget is exhausted. No setter resets it before this offer.
            buffer.TryOffer(totalAttempts).Should().Be(ReadBufferOfferResult.Success);
            accepted.Add(-1);
            accepted.Add(totalAttempts);
            ReadBufferStatistics beforeDrain = buffer.GetStatistics();
            beforeDrain.DroppedFailed.Should().Be(failed);
            beforeDrain.DroppedFull.Should().Be(0);
            beforeDrain.DroppedShutdown.Should().Be(0);
            beforeDrain.Enqueued.Should().Be(accepted.Count);
            beforeDrain.Queued.Should().Be(accepted.Count);

            List<int> observed = [];
            buffer.DrainTo(observed.Add, capacity).Should().Be(accepted.Count);
            observed.Should().OnlyHaveUniqueItems();
            observed.Should().BeEquivalentTo(accepted);
            ReadBufferStatistics afterDrain = buffer.GetStatistics();
            afterDrain.Dequeued.Should().Be(accepted.Count);
            afterDrain.Queued.Should().Be(0);
            afterDrain.DroppedFailed.Should().Be(failed);
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
