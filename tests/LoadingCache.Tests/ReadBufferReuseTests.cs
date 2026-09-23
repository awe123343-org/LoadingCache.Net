using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferReuseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Test]
    [Arguments(false, 1, 0L)]
    [Arguments(true, 1, 0L)]
    [Arguments(false, 16, 0L)]
    [Arguments(true, 16, 0L)]
    [Arguments(false, 1, long.MaxValue - 2)]
    [Arguments(true, 1, long.MaxValue - 2)]
    [Arguments(false, 16, long.MaxValue - 2)]
    [Arguments(true, 16, long.MaxValue - 2)]
    [Arguments(false, 1, -2L)]
    [Arguments(true, 1, -2L)]
    [Arguments(false, 16, -2L)]
    [Arguments(true, 16, -2L)]
    public async Task ConcurrentProducersAndBatchConsumerReuseEverySlotWithoutLosingOrTearingEvents(
        bool recordStatistics,
        int capacity,
        long initialCounter
    )
    {
        const int producerCount = 4;
        const int eventsPerProducer = 2048;
        const int eventCount = producerCount * eventsPerProducer;
        using StripedReadBuffer<ReadPayload> buffer = new(1, capacity, recordStatistics);
        await Assert.That(buffer.TryOffer(default)).IsEqualTo(ReadBufferOfferResult.Success);
        await Assert.That(buffer.TryRead(out _)).IsTrue();
        buffer.SetCounterForTesting(initialCounter);
        ReadPayload[] expected = new ReadPayload[eventCount];
        for (int id = 0; id < expected.Length; id++)
        {
            expected[id] = new ReadPayload(id, ~id, ((long)id << 32) | (uint)~id, new object());
        }

        bool[] accepted = new bool[eventCount];
        int[] observed = new int[eventCount];
        long[] full = new long[producerCount];
        long[] failed = new long[producerCount];
        using Barrier start = new(producerCount + 1);
        using CancellationTokenSource stop = new();
        var remaining = new System.Runtime.CompilerServices.StrongBox<int>(producerCount);
        var workload = new ReuseState(
            buffer,
            start,
            expected,
            accepted,
            observed,
            full,
            failed,
            remaining,
            stop.Token
        );
        Task[] workers = new Task[producerCount + 1];
        for (int producer = 0; producer < producerCount; producer++)
        {
            int producerId = producer;
            workers[producer] = Task.Factory.StartNew(
                static state =>
                {
                    (ReuseState work, int producerIndex) = ((ReuseState, int))state!;
                    try
                    {
                        MeetPeers(work.Start);
                        for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                        {
                            int id = producerIndex * eventsPerProducer + sequence;
                            while (true)
                            {
                                work.Cancellation.ThrowIfCancellationRequested();
                                ReadBufferOfferResult result = work.Buffer.TryOffer(
                                    work.Expected[id]
                                );
                                if (result == ReadBufferOfferResult.Success)
                                {
                                    work.Accepted[id] = true;
                                    break;
                                }

                                switch (result)
                                {
                                    case ReadBufferOfferResult.Full:
                                        work.Full[producerIndex]++;
                                        break;
                                    case ReadBufferOfferResult.Failed:
                                        work.Failed[producerIndex]++;
                                        break;
                                    case ReadBufferOfferResult.Success:
                                    case ReadBufferOfferResult.Shutdown:
                                    default:
                                        throw new InvalidOperationException(
                                            "The live buffer shut down."
                                        );
                                }

                                Thread.Yield();
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref work.Remaining.Value);
                    }
                },
                (workload, producerId),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        workers[producerCount] = Task.Factory.StartNew(
            static state =>
            {
                ReuseState work = (ReuseState)state!;
                MeetPeers(work.Start);
                while (Volatile.Read(ref work.Remaining.Value) != 0 || work.Buffer.HasPublished)
                {
                    work.Cancellation.ThrowIfCancellationRequested();
                    if (
                        work.Buffer.DrainTo(
                            payload =>
                            {
                                if (
                                    (uint)payload.Id >= (uint)work.Expected.Length
                                    || payload != work.Expected[payload.Id]
                                    || ++work.Observed[payload.Id] != 1
                                )
                                {
                                    throw new InvalidOperationException(
                                        "A read event was torn, fabricated, or delivered twice."
                                    );
                                }
                            },
                            budget: 7
                        ) == 0
                    )
                    {
                        Thread.Yield();
                    }
                }
            },
            workload,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        try
        {
            await Task.WhenAll(workers).WaitAsync(TestTimeout, CancellationToken.None);
            await Assert.That(accepted).All(static value => value);
            await Assert.That(observed).All(static count => count == 1);
            await Assert.That(buffer.HasPublished).IsFalse();
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.Queued).IsEqualTo(0);
            await Assert.That(statistics.Enqueued).IsEqualTo(recordStatistics ? eventCount : 0);
            await Assert.That(statistics.Dequeued).IsEqualTo(recordStatistics ? eventCount : 0);
            await Assert.That(statistics.DroppedFull).IsEqualTo(recordStatistics ? full.Sum() : 0);
            await Assert
                .That(statistics.DroppedFailed)
                .IsEqualTo(recordStatistics ? failed.Sum() : 0);
            await Assert.That(statistics.DroppedShutdown).IsEqualTo(0);
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await Task.WhenAll(workers).WaitAsync(TestTimeout, CancellationToken.None);
            }
            catch (Exception) when (workers.All(static worker => worker.IsCompleted)) { }
        }
    }

    private sealed record ReuseState(
        StripedReadBuffer<ReadPayload> Buffer,
        Barrier Start,
        ReadPayload[] Expected,
        bool[] Accepted,
        int[] Observed,
        long[] Full,
        long[] Failed,
        System.Runtime.CompilerServices.StrongBox<int> Remaining,
        CancellationToken Cancellation
    );

    private static void MeetPeers(Barrier start)
    {
        if (!start.SignalAndWait(TestTimeout))
        {
            throw new TimeoutException("The read-buffer workers did not meet at the start gate.");
        }
    }

    private readonly record struct ReadPayload(int Id, int Complement, long Stamp, object Identity);
}
