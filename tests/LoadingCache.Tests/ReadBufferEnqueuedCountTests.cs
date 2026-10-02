using FluentAssertions;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferEnqueuedCountTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(1, false, false)]
    [Arguments(1, false, true)]
    [Arguments(1, true, false)]
    [Arguments(1, true, true)]
    [Arguments(4, false, false)]
    [Arguments(4, false, true)]
    [Arguments(4, true, false)]
    [Arguments(4, true, true)]
    public async Task DisposalCountsALatePublicationOnlyAfterItPublishes(
        int capacity,
        bool pauseBeforeReservation,
        bool recordStatistics
    )
    {
        using StripedReadBuffer<int> buffer = new(1, capacity, recordStatistics);
        await using BlockingTestHook pause = new(TestTimeout);
        buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
        int consumed = capacity == 1 ? buffer.DrainTo(static _ => { }, 1) : 0;
        buffer.SetHooksForTesting(
            pauseBeforeReservation ? pause.Invoke : null,
            pauseBeforeReservation ? null : pause.Invoke
        );
        Task<ReadBufferOfferResult> producer = Task.Factory.StartNew(
            static state => ((StripedReadBuffer<int>)state!).TryOffer(1),
            buffer,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        try
        {
            await pause.Entered.WaitAsync(TestTimeout);
            buffer.GetStatistics().Enqueued.Should().Be(recordStatistics ? 1 : 0);
            buffer.Dispose();
            ReadBufferStatistics paused = buffer.GetStatistics();
            paused.Enqueued.Should().Be(recordStatistics ? 1 : 0);
            paused.Dequeued.Should().Be(recordStatistics ? consumed : 0);
            paused.DroppedShutdown.Should().Be(recordStatistics ? 1 - consumed : 0);
            paused.Queued.Should().Be(0);

            // A rejected offer never contributes to accepted publications.
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Shutdown);
            buffer.GetStatistics().Enqueued.Should().Be(recordStatistics ? 1 : 0);
            pause.Release();
            (await producer.WaitAsync(TestTimeout)).Should().Be(ReadBufferOfferResult.Shutdown);
            ReadBufferStatistics finished = buffer.GetStatistics();
            finished.Enqueued.Should().Be(recordStatistics ? 2 : 0);
            finished.Dequeued.Should().Be(recordStatistics ? consumed : 0);
            finished.DroppedShutdown.Should().Be(recordStatistics ? 3 - consumed : 0);
            finished.Queued.Should().Be(0);
            pause.TimedOut.Should().BeFalse();
        }
        finally
        {
            pause.Release();
            await producer.WaitAsync(TestTimeout);
            buffer.SetHooksForTesting(null, null);
        }
    }

    [Test]
    public void EnqueuedIncludesTheConsumedPrefixBeforeABatchReleasesCapacity()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        for (int value = 0; value < 4; value++)
        {
            buffer.TryEnqueue(value).Should().BeTrue();
        }

        buffer.DrainTo(_ => buffer.GetStatistics().Enqueued.Should().Be(4), 4).Should().Be(4);
        buffer.GetStatistics().Enqueued.Should().Be(4);
        buffer.GetStatistics().Dequeued.Should().Be(4);
    }

    [Test]
    public async Task ACapturedRingKeepsItsObservedTotalDuringShutdownHandoff()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await using BlockingTestHook capture = new(TestTimeout);
        await using BlockingTestHook shutdown = new(TestTimeout);
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.GetStatistics().Enqueued.Should().Be(1);
        buffer.SetShutdownHookForTesting(_ => shutdown.Invoke());
        Task<ReadBufferStatistics> snapshot = Task.Run(() => buffer.GetStatistics(capture.Invoke));
        Task? disposer = null;
        try
        {
            await capture.Entered.WaitAsync(TestTimeout);
            disposer = Task.Run(buffer.Dispose);
            await shutdown.Entered.WaitAsync(TestTimeout);
            capture.Release();
            (await snapshot.WaitAsync(TestTimeout)).Enqueued.Should().Be(1);
            shutdown.Release();
            await disposer.WaitAsync(TestTimeout);
            buffer.GetStatistics().Enqueued.Should().Be(1);
            buffer.GetStatistics().DroppedShutdown.Should().Be(1);
            capture.TimedOut.Should().BeFalse();
            shutdown.TimedOut.Should().BeFalse();
        }
        finally
        {
            capture.Release();
            shutdown.Release();
            await Task.WhenAll(snapshot, disposer ?? Task.CompletedTask).WaitAsync(TestTimeout);
            buffer.SetShutdownHookForTesting(null);
        }
    }

    [Test]
    public async Task ConcurrentSnapshotsDoNotMoveAcceptedTotalsBackwards()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        const int publications = 4000;
        int finished = 0;
        using Barrier start = new(4);
        Task[] readers = Enumerable
            .Range(0, 3)
            .Select(_ =>
                Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait(TestTimeout).Should().BeTrue();
                        long previous = 0;
                        do
                        {
                            long current = buffer.GetStatistics().Enqueued;
                            current.Should().BeGreaterThanOrEqualTo(previous);
                            current.Should().BeLessThanOrEqualTo(publications);
                            previous = current;
                        } while (Volatile.Read(ref finished) == 0);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
            )
            .ToArray();
        try
        {
            start.SignalAndWait(TestTimeout).Should().BeTrue();
            for (int value = 0; value < publications; value++)
            {
                buffer.TryEnqueue(value).Should().BeTrue();
                buffer.DrainTo(static _ => { }, 1).Should().Be(1);
            }
        }
        finally
        {
            Volatile.Write(ref finished, 1);
            await Task.WhenAll(readers).WaitAsync(TestTimeout);
        }

        buffer.GetStatistics().Enqueued.Should().Be(publications);
        buffer.GetStatistics().Dequeued.Should().Be(publications);
    }
}
