using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ReadBufferReuseTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [TestCase(false, 1, 0L)]
    [TestCase(true, 1, 0L)]
    [TestCase(false, 16, 0L)]
    [TestCase(true, 16, 0L)]
    [TestCase(false, 1, long.MaxValue - 2)]
    [TestCase(true, 1, long.MaxValue - 2)]
    [TestCase(false, 16, long.MaxValue - 2)]
    [TestCase(true, 16, long.MaxValue - 2)]
    [TestCase(false, 1, -2L)]
    [TestCase(true, 1, -2L)]
    [TestCase(false, 16, -2L)]
    [TestCase(true, 16, -2L)]
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
        buffer.TryOffer(default).Should().Be(ReadBufferOfferResult.Success);
        buffer.TryRead(out _).Should().BeTrue();
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
        int remaining = producerCount;
        Task[] workers = new Task[producerCount + 1];
        for (int producer = 0; producer < producerCount; producer++)
        {
            int producerId = producer;
            workers[producer] = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        MeetPeers(start);
                        for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                        {
                            int id = producerId * eventsPerProducer + sequence;
                            while (true)
                            {
                                stop.Token.ThrowIfCancellationRequested();
                                ReadBufferOfferResult result = buffer.TryOffer(expected[id]);
                                if (result == ReadBufferOfferResult.Success)
                                {
                                    accepted[id] = true;
                                    break;
                                }

                                if (result == ReadBufferOfferResult.Full)
                                {
                                    full[producerId]++;
                                }
                                else if (result == ReadBufferOfferResult.Failed)
                                {
                                    failed[producerId]++;
                                }
                                else
                                {
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
                        Interlocked.Decrement(ref remaining);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        workers[producerCount] = Task.Factory.StartNew(
            () =>
            {
                MeetPeers(start);
                while (Volatile.Read(ref remaining) != 0 || buffer.HasPublished)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (
                        buffer.DrainTo(
                            payload =>
                            {
                                if (
                                    (uint)payload.Id >= (uint)expected.Length
                                    || payload != expected[payload.Id]
                                    || ++observed[payload.Id] != 1
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
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

        try
        {
            await Task.WhenAll(workers).WaitAsync(TestTimeout);
            accepted.Should().OnlyContain(static value => value);
            observed.Should().OnlyContain(static count => count == 1);
            buffer.HasPublished.Should().BeFalse();
            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.Queued.Should().Be(0);
            statistics.Enqueued.Should().Be(recordStatistics ? eventCount : 0);
            statistics.Dequeued.Should().Be(recordStatistics ? eventCount : 0);
            statistics.DroppedFull.Should().Be(recordStatistics ? full.Sum() : 0);
            statistics.DroppedFailed.Should().Be(recordStatistics ? failed.Sum() : 0);
            statistics.DroppedShutdown.Should().Be(0);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(workers).WaitAsync(TestTimeout);
            }
            catch (Exception) when (workers.All(static worker => worker.IsCompleted)) { }
        }
    }

    private static void MeetPeers(Barrier start)
    {
        if (!start.SignalAndWait(TestTimeout))
        {
            throw new TimeoutException("The read-buffer workers did not meet at the start gate.");
        }
    }

    private readonly record struct ReadPayload(int Id, int Complement, long Stamp, object Identity);
}
