using System.Collections.Concurrent;
using JetBrains.Annotations;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class MaintenanceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> Context = new();

    [Test]
    public async Task ReadBufferRequiresPositivePowerOfTwoStripesAndPositiveCapacity()
    {
        Action zeroStripes = () =>
        {
            using StripedReadBuffer<int> _ = new(0, 1);
        };
        Action nonPowerOfTwoStripes = () =>
        {
            using StripedReadBuffer<int> _ = new(3, 1);
        };
        Action zeroCapacity = () =>
        {
            using StripedReadBuffer<int> _ = new(2, 0);
        };
        await Assert.That(zeroStripes).Throws<ArgumentOutOfRangeException>();
        await Assert.That(nonPowerOfTwoStripes).Throws<ArgumentException>();
        await Assert.That(zeroCapacity).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReadBufferPreservesExactIdentityAndFifoWithinOneStripe()
    {
        using StripedReadBuffer<ReadEvent> buffer = new(1, 4);
        ReadEvent first = new(1, 1);
        ReadEvent second = new(1, 2);
        ReadEvent third = new(1, 3);
        await Assert.That(buffer.TryEnqueue(first)).IsTrue();
        await Assert.That(buffer.TryEnqueue(second)).IsTrue();
        await Assert.That(buffer.TryEnqueue(third)).IsTrue();
        await Assert.That(buffer.TryRead(out ReadEvent observedFirst)).IsTrue();
        await Assert.That(buffer.TryRead(out ReadEvent observedSecond)).IsTrue();
        await Assert.That(buffer.TryRead(out ReadEvent observedThird)).IsTrue();
        await Assert.That(buffer.TryRead(out _)).IsFalse();
        await Assert.That(ReferenceEquals(observedFirst, first)).IsTrue();
        await Assert.That(ReferenceEquals(observedSecond, second)).IsTrue();
        await Assert.That(ReferenceEquals(observedThird, third)).IsTrue();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.Enqueued).IsEqualTo(3);
        await Assert.That(statistics.Dequeued).IsEqualTo(3);
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.Dropped).IsEqualTo(0);
    }

    [Test]
    public async Task ReadBufferSupportsBatchDrainAndPublishedProbe()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.HasPublished).IsFalse();
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsTrue();
        await Assert.That(buffer.TryEnqueue(3)).IsTrue();
        await Assert.That(buffer.HasPublished).IsTrue();
        List<int> observed = [];
        await Assert.That(buffer.DrainTo(observed.Add, budget: 2)).IsEqualTo(2);
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(buffer.HasPublished).IsTrue();
        await Assert.That(buffer.DrainTo(observed.Add, budget: 2)).IsEqualTo(1);
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(buffer.HasPublished).IsFalse();
    }

    [Test]
    public async Task ReadBufferUsesPublicationSequenceForNullValues()
    {
        using StripedReadBuffer<string?> buffer = new(1, 2);
        await Assert.That(buffer.TryEnqueue(null)).IsTrue();
        await Assert.That(buffer.TryRead(out string? observed)).IsTrue();
        await Assert.That((observed) is null).IsTrue();
        await Assert.That(buffer.TryRead(out _)).IsFalse();
        await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ReadBufferStopsAtAnUnpublishedReservationAndResumesInFifoOrder()
    {
        StripedReadBuffer<int> buffer = new(1, 4);
        PublishGate gate = new();
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetHooksForTesting(beforeReserve: null, beforePublish: gate.BeforePublish);
        Task<bool> producer = StartEnqueue(buffer, 1);
        Task<bool>? laterProducer = null;
        try
        {
            await Assert.That(gate.Entered.Wait(TestTimeout)).IsTrue();
            await Assert.That(buffer.TryRead(out int first)).IsTrue();
            await Assert.That(first).IsEqualTo(0);
            laterProducer = StartEnqueue(buffer, 2);
            await Assert.That((await laterProducer.WaitAsync(TestTimeout))).IsTrue();
            await Assert.That(buffer.TryRead(out _)).IsFalse();
            await Assert.That(buffer.HasPublished).IsFalse();
            gate.Release.Set();
            await Assert.That((await producer.WaitAsync(TestTimeout))).IsTrue();
            await Assert.That(buffer.TryRead(out int second)).IsTrue();
            await Assert.That(second).IsEqualTo(1);
            await Assert.That(buffer.TryRead(out int third)).IsTrue();
            await Assert.That(third).IsEqualTo(2);
        }
        finally
        {
            gate.Release.Set();
            try
            {
                await producer.WaitAsync(TestTimeout);
                if (laterProducer is not null)
                {
                    await laterProducer.WaitAsync(TestTimeout);
                }
            }
            finally
            {
                buffer.SetHooksForTesting(null, null);
                gate.Dispose();
                buffer.Dispose();
            }
        }
    }

    [Test]
    public async Task ReadBufferRetriesAContendedCasWithoutCountingARejectedEvent()
    {
        StripedReadBuffer<int> buffer = new(1, 8);
        ContendedReserveGate gate = new(2);
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetHooksForTesting(beforeReserve: gate.BeforeReserve, beforePublish: null);
        Task<bool> first = StartEnqueue(buffer, 1);
        Task<bool> second = StartEnqueue(buffer, 2);
        try
        {
            await Assert
                .That((await Task.WhenAll(first, second).WaitAsync(TestTimeout)))
                .IsEquivalentTo([true, true], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.DroppedFailed).IsEqualTo(0);
            await Assert.That(statistics.Dropped).IsEqualTo(0);
        }
        finally
        {
            try
            {
                await Task.WhenAll(first, second).WaitAsync(TestTimeout);
            }
            catch (Exception) when (first.IsCompleted && second.IsCompleted) { }
            finally
            {
                buffer.SetHooksForTesting(null, null);
                gate.Dispose();
                buffer.Dispose();
            }
        }
    }

    [Test]
    public async Task ReadBufferReportsOneFinalFailedOfferAfterBoundedRetries()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetForcedCasFailuresForTesting(3);
        await Assert.That(buffer.TryEnqueue(1)).IsFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFailed).IsEqualTo(1);
        await Assert.That(statistics.Dropped).IsEqualTo(1);
        buffer.SetForcedCasFailuresForTesting(0);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
    }

    [Test]
    public async Task ReadBufferExpandsOnlyToTheConfiguredStripeLimit()
    {
        using StripedReadBuffer<int> buffer = new(2, 4);
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetForcedCasFailuresForTesting(3);
        await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Failed);
        await Assert.That(buffer.StripeCountForTesting).IsEqualTo(2);
        buffer.SetForcedCasFailuresForTesting(0);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.StripeCountForTesting).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task ReadBufferRejectsNonPowerOfTwoCapacity()
    {
        Action create = () =>
        {
            using StripedReadBuffer<int> buffer = new(1, 3);
            _ = buffer.GetStatistics();
        };
        await Assert.That(create).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(long.MaxValue - 2)]
    [Arguments(-2L)]
    public async Task ReadBufferHandlesCounterWrapWithPowerOfTwoCapacity(long counter)
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.TryEnqueue(0)).IsTrue();
        buffer.SetCounterForTesting(counter);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            int firstValue = cycle * 4 + 1;
            for (int offset = 0; offset < 4; offset++)
            {
                await Assert.That(buffer.TryEnqueue(firstValue + offset)).IsTrue();
            }

            await Assert.That(buffer.TryEnqueue(firstValue + 4)).IsFalse();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(4);
            for (int offset = 0; offset < 4; offset++)
            {
                await Assert.That(buffer.TryRead(out int observed)).IsTrue();
                await Assert.That(observed).IsEqualTo(firstValue + offset);
            }

            await Assert.That(buffer.TryRead(out _)).IsFalse();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ReadBufferContinuesAfterAConsumerCallbackThrows()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsTrue();
        await Assert
            .That(() =>
                buffer.DrainTo(
                    static _ => throw new InvalidOperationException("test callback failure"),
                    budget: 4
                )
            )
            .Throws<InvalidOperationException>();
        await Assert.That(buffer.TryRead(out int observed)).IsTrue();
        await Assert.That(observed).IsEqualTo(2);
    }

    [Test]
    public async Task ReadBufferDisposalDoesNotRetainAValuePublishedByAPausedProducer()
    {
        StripedReadBuffer<TrackedValue> buffer = new(1, 2);
        PublishGate gate = new();
        TrackedValue initial = new();
        await Assert.That(buffer.TryEnqueue(initial)).IsTrue();
        await Assert.That(buffer.TryRead(out _)).IsTrue();
        buffer.SetHooksForTesting(beforeReserve: null, beforePublish: gate.BeforePublish);
        Task<(bool Accepted, WeakReference<TrackedValue> Weak)> producer = StartTrackedEnqueue(
            buffer
        );
        try
        {
            await Assert.That(gate.Entered.Wait(TestTimeout)).IsTrue();
            buffer.Dispose();
            gate.Release.Set();
            (bool accepted, WeakReference<TrackedValue> weak) = await producer.WaitAsync(
                TestTimeout
            );
            await Assert.That(accepted).IsFalse();
            buffer.SetHooksForTesting(null, null);
            await Assert.That(EventuallyCollected(weak)).IsTrue();
        }
        finally
        {
            gate.Release.Set();
            try
            {
                await producer.WaitAsync(TestTimeout);
            }
            finally
            {
                buffer.SetHooksForTesting(null, null);
                gate.Dispose();
                buffer.Dispose();
            }
        }
    }

    [Test]
    public async Task ReadBufferFullTryWriteDropsNewestItemInsteadOfReportingFalseSuccess()
    {
        using StripedReadBuffer<int> buffer = new(1, 1);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsFalse();
        await Assert.That(buffer.TryRead(out int observed)).IsTrue();
        await Assert.That(observed).IsEqualTo(1);
        await Assert.That(buffer.TryRead(out _)).IsFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.Enqueued).IsEqualTo(1);
        await Assert.That(statistics.DroppedFull).IsEqualTo(1);
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ReadBufferDiagnosticCountersSaturateWithoutCorruptingQueuedCount()
    {
        using StripedReadBuffer<int> buffer = new(2, 1);
        buffer.AddStatisticsForTesting(
            0,
            enqueued: long.MaxValue - 1,
            dequeued: long.MaxValue - 1,
            droppedFull: long.MaxValue - 1,
            droppedShutdown: long.MaxValue - 1
        );
        buffer.AddStatisticsForTesting(
            1,
            enqueued: 2,
            dequeued: 2,
            droppedFull: 2,
            droppedShutdown: 2
        );
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.Enqueued).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.Dequeued).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.DroppedFull).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.DroppedShutdown).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.Dropped).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task ConcurrentProducersPreserveAcceptedIdentityWithinBoundedCapacity()
    {
        const int producerCount = 8;
        const int eventsPerProducer = 64;
        StripedReadBuffer<ReadEvent> buffer = new(8, 16);
        ConcurrentBag<ReadEvent> accepted = [];
        List<Task> producers = [];
        try
        {
            for (int producer = 0; producer < producerCount; producer++)
            {
                int producerId = producer;
                producers.Add(
                    Task.Factory.StartNew(
                        static state =>
                        {
                            (
                                StripedReadBuffer<ReadEvent> current,
                                ConcurrentBag<ReadEvent> results,
                                int workerId
                            ) = ((StripedReadBuffer<ReadEvent>, ConcurrentBag<ReadEvent>, int))
                                state!;
                            for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                            {
                                ReadEvent value = new(workerId, sequence);
                                if (current.TryEnqueue(value))
                                {
                                    results.Add(value);
                                }
                            }
                        },
                        (buffer, accepted, producerId),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default
                    )
                );
            }

            await Task.WhenAll(producers).WaitAsync(TestTimeout, CancellationToken.None);
            List<ReadEvent> observed = [];
            while (buffer.TryRead(out ReadEvent value))
            {
                observed.Add(value);
            }

            await Assert.That(observed.Count).IsEqualTo(accepted.Count);
            await Assert.That(observed).HasDistinctItems();
            foreach (ReadEvent value in observed)
            {
                await Assert.That(accepted).Contains(value);
            }

            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.Queued).IsEqualTo(0);
            await Assert.That(statistics.Enqueued).IsEqualTo(observed.Count);
            await Assert.That(statistics.Enqueued).IsLessThanOrEqualTo(8L * 16L);
        }
        finally
        {
            try
            {
                if (producers.Count != 0)
                {
                    try
                    {
                        await Task.WhenAll(producers)
                            .WaitAsync(TestTimeout, CancellationToken.None);
                    }
                    catch (Exception) when (producers.All(task => task.IsCompleted)) { }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    [Test]
    public async Task ReadBufferConcurrentDisposeRejectsLateProducersAndClearsQueues()
    {
        const int producerCount = 8;
        const int eventsPerProducer = 128;
        StripedReadBuffer<int> buffer = new(8, 16);
        // Blocking peers must have their own threads: ThreadPool injection is not a test gate.
        using Barrier start = new(producerCount + 1);
        List<Task> workers = [];
        try
        {
            for (int producer = 0; producer < producerCount; producer++)
            {
                int producerId = producer;
                workers.Add(
                    Task.Factory.StartNew(
                        static state =>
                        {
                            (
                                StripedReadBuffer<int> buffer,
                                Barrier start,
                                int producerId,
                                int eventsPerProducer,
                                TimeSpan timeout
                            ) = ((
                                StripedReadBuffer<int> Buffer,
                                Barrier Start,
                                int ProducerId,
                                int EventsPerProducer,
                                TimeSpan Timeout
                            ))
                                state!;
                            if (!start.SignalAndWait(timeout))
                            {
                                throw new TimeoutException("The producer did not meet its peers.");
                            }

                            for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                            {
                                buffer.TryEnqueue(producerId * eventsPerProducer + sequence);
                            }
                        },
                        (buffer, start, producerId, eventsPerProducer, TestTimeout),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default
                    )
                );
            }

            workers.Add(
                Task.Factory.StartNew(
                    static state =>
                    {
                        (StripedReadBuffer<int> buffer, Barrier start, TimeSpan timeout) = ((
                            StripedReadBuffer<int> Buffer,
                            Barrier Start,
                            TimeSpan Timeout
                        ))
                            state!;
                        if (!start.SignalAndWait(timeout))
                        {
                            throw new TimeoutException("The disposer did not meet its peers.");
                        }

                        buffer.Dispose();
                    },
                    (buffer, start, TestTimeout),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default
                )
            );
            await Task.WhenAll(workers).WaitAsync(TestTimeout, CancellationToken.None);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.IsDisposed).IsTrue();
            await Assert.That(statistics.Queued).IsEqualTo(0);
            await Assert
                .That(statistics.DroppedShutdown)
                .IsGreaterThanOrEqualTo(statistics.Enqueued);
            await Assert.That(buffer.TryEnqueue(-1)).IsFalse();
            await Assert.That(buffer.TryRead(out _)).IsFalse();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
        }
        finally
        {
            try
            {
                await Task.WhenAll(workers).WaitAsync(TestTimeout, CancellationToken.None);
            }
            catch (Exception) when (workers.All(static worker => worker.IsCompleted)) { }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    [Test]
    public async Task ReadBufferDisposeDropsQueuedItemsAndRejectsLaterPublication()
    {
        StripedReadBuffer<int> buffer = new(2, 2);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsTrue();
        buffer.Dispose();
        await Assert.That(buffer.TryEnqueue(3)).IsFalse();
        await Assert.That(buffer.TryRead(out _)).IsFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.IsDisposed).IsTrue();
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.DroppedShutdown).IsEqualTo(3);
        buffer.Dispose();
        await Assert.That(buffer.GetStatistics().DroppedShutdown).IsEqualTo(3);
    }

    [Test]
    public async Task ReadBufferDisposeMayDrainWhileTheMaintenanceConsumerReads()
    {
        StripedReadBuffer<int> buffer = new(4, 64);
        try
        {
            for (int value = 0; value < 64; value++)
            {
                await Assert.That(buffer.TryEnqueue(value)).IsTrue();
            }

            Barrier start = new(2);
            Task? consumer = null;
            Task? disposer = null;
            try
            {
                consumer = Task.Factory.StartNew(
                    static state =>
                    {
                        (StripedReadBuffer<int> current, Barrier gate) = ((
                            StripedReadBuffer<int>,
                            Barrier
                        ))
                            state!;
                        if (!gate.SignalAndWait(TestTimeout))
                        {
                            throw new TimeoutException(
                                "The maintenance consumer did not meet its peer."
                            );
                        }

                        while (current.TryRead(out _))
                        {
                            Thread.Yield();
                        }
                    },
                    (buffer, start),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default
                );
                disposer = Task.Factory.StartNew(
                    static state =>
                    {
                        (StripedReadBuffer<int> current, Barrier gate) = ((
                            StripedReadBuffer<int>,
                            Barrier
                        ))
                            state!;
                        if (!gate.SignalAndWait(TestTimeout))
                        {
                            throw new TimeoutException(
                                "The buffer disposer did not meet its peer."
                            );
                        }

                        current.Dispose();
                    },
                    (buffer, start),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default
                );
                await Task.WhenAll(consumer, disposer)
                    .WaitAsync(TestTimeout, CancellationToken.None);
                ReadBufferStatistics statistics = buffer.GetStatistics();
                await Assert.That(statistics.IsDisposed).IsTrue();
                await Assert.That(statistics.Queued).IsEqualTo(0);
                await Assert
                    .That(statistics.Enqueued)
                    .IsEqualTo(statistics.Dequeued + statistics.DroppedShutdown);
            }
            finally
            {
                Task[] workers = [consumer ?? Task.CompletedTask, disposer ?? Task.CompletedTask];
                try
                {
                    await Task.WhenAll(workers).WaitAsync(TestTimeout, CancellationToken.None);
                }
                catch (Exception) when (workers.All(task => task.IsCompleted)) { }
                finally
                {
                    start.Dispose();
                }
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public async Task CoordinatorCoalescesRequestsAndRepeatsBoundedPassesWithoutRecursion()
    {
        ManualMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () => Interlocked.Increment(ref passes) < 3,
            scheduler
        );
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(1);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Scheduled);
        scheduler.RunNext();
        await Assert.That(passes).IsEqualTo(3);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        MaintenanceStatistics statistics = coordinator.GetStatistics();
        await Assert.That(statistics.Requests).IsEqualTo(2);
        await Assert.That(statistics.CoalescedRequests).IsEqualTo(1);
        await Assert.That(statistics.DrainPasses).IsEqualTo(3);
        await Assert.That(statistics.MoreWorkPasses).IsEqualTo(2);
    }

    [Test]
    public async Task RequestDuringRunningPassSetsRunningRequiredAndCannotLoseWakeup(
        CancellationToken cancellationToken
    )
    {
        ManualMaintenanceScheduler scheduler = new();
        TaskCompletionSource<bool> entered = NewCompletionSource<bool>();
        TaskCompletionSource<bool> release = NewCompletionSource<bool>();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                if (Interlocked.Increment(ref passes) != 1)
                {
                    return false;
                }

                entered.SetResult(true);
                release
                    .Task.WaitAsync(TestTimeout, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return false;
            },
            scheduler
        );
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        Task worker = Task.Factory.StartNew(
            scheduler.RunNext,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await entered.Task.WaitAsync(TestTimeout, cancellationToken);
            await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
            await Assert
                .That(coordinator.State)
                .IsEqualTo(MaintenanceCoordinatorState.RunningRequired);
        }
        finally
        {
            release.TrySetResult(true);
            await worker.WaitAsync(TestTimeout, CancellationToken.None);
        }

        await Assert.That(passes).IsEqualTo(2);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        await Assert.That(coordinator.GetStatistics().CoalescedRequests).IsEqualTo(1);
    }

    [Test]
    public async Task CleanUpClaimsScheduledWorkAndLeavesStaleCallbackHarmless()
    {
        ManualMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                passes++;
                return false;
            },
            scheduler
        );
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        await Assert.That(cleanup.Performed).IsTrue();
        await Assert.That(cleanup.MoreWork).IsFalse();
        await Assert.That(cleanup.FallbackRequired).IsFalse();
        await Assert.That(passes).IsEqualTo(1);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        scheduler.RunNext();
        await Assert.That(passes).IsEqualTo(1);
        await Assert.That(coordinator.GetStatistics().SynchronousCleanUps).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedOrThrowingSchedulerReportsSynchronousFallback(bool throws)
    {
        ManualMaintenanceScheduler scheduler = new() { Reject = !throws, Throw = throws };
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                passes++;
                return false;
            },
            scheduler
        );
        await Assert
            .That(coordinator.Request())
            .IsEqualTo(MaintenanceRequestResult.ScheduleRejected);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        await Assert.That(coordinator.GetStatistics().ScheduleRejections).IsEqualTo(1);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        await Assert.That(cleanup.Performed).IsTrue();
        await Assert.That(cleanup.MoreWork).IsFalse();
        await Assert.That(cleanup.FallbackRequired).IsFalse();
        await Assert.That(passes).IsEqualTo(1);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public async Task CleanUpReportsFallbackWhenRearmIsRejectedWithRemainingWork()
    {
        ManualMaintenanceScheduler scheduler = new() { Reject = true };
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                passes++;
                return true;
            },
            scheduler,
            maxPassesPerInvocation: 1
        );
        await Assert
            .That(coordinator.Request())
            .IsEqualTo(MaintenanceRequestResult.ScheduleRejected);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        await Assert.That(cleanup.Performed).IsTrue();
        await Assert.That(cleanup.MoreWork).IsTrue();
        await Assert.That(cleanup.FallbackRequired).IsTrue();
        await Assert.That(passes).IsEqualTo(1);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        await Assert.That(coordinator.GetStatistics().FallbackRequired).IsTrue();
        await Assert.That(coordinator.GetStatistics().ScheduleRejections).IsEqualTo(2);
    }

    [Test]
    public async Task InlineSchedulerCannotBypassThePerInvocationPassBudget()
    {
        InlineMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () => Interlocked.Increment(ref passes) < 128,
            scheduler,
            maxPassesPerInvocation: 8
        );
        await Assert
            .That(coordinator.Request())
            .IsEqualTo(MaintenanceRequestResult.ScheduleRejected);
        await Assert.That(passes).IsEqualTo(8);
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(2);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        await Assert.That(coordinator.GetStatistics().FallbackRequired).IsTrue();
    }

    [Test]
    public async Task CleanUpReturnsMoreWorkAfterBudgetAndRequestDuringDrainCannotExtendIt()
    {
        ManualMaintenanceScheduler scheduler = new();
        var drain = new ReentrantDrain();
        var coordinator = new MaintenanceCoordinator(
            drain.Invoke,
            scheduler,
            maxPassesPerInvocation: 2
        );
        drain.Coordinator = coordinator;
        using (coordinator)
        {
            MaintenanceCleanupResult first = coordinator.CleanUp();
            await Assert.That(first.Performed).IsTrue();
            await Assert.That(first.MoreWork).IsTrue();
            await Assert.That(first.FallbackRequired).IsFalse();
            await Assert.That(drain.Passes).IsEqualTo(2);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Scheduled);
            await Assert.That(scheduler.Pending).IsEqualTo(1);
            scheduler.RunNext();
            await Assert.That(drain.Passes).IsEqualTo(4);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Scheduled);
            await Assert.That(coordinator.GetStatistics().BudgetExhaustions).IsEqualTo(2);
        }
    }

    [Test]
    public async Task DrainFaultIsObservedAndDoesNotStrandTheCoordinator()
    {
        ManualMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                passes++;
                return passes == 1 ? throw new InvalidOperationException("test fault") : false;
            },
            scheduler
        );
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        scheduler.RunNext();
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
        await Assert.That(coordinator.GetStatistics().DrainFaults).IsEqualTo(1);
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        scheduler.RunNext();
        await Assert.That(passes).IsEqualTo(2);
        await Assert.That(coordinator.GetStatistics().DrainFaults).IsEqualTo(1);
    }

    [Test]
    public async Task CoordinatorDiagnosticCountersSaturateAtLongMaxValue()
    {
        using MaintenanceCoordinator coordinator = new(() => false);
        coordinator.AddStatisticsForTesting(
            scheduleRejections: long.MaxValue - 1,
            drainFaults: long.MaxValue - 1
        );
        coordinator.AddStatisticsForTesting(scheduleRejections: 2, drainFaults: 2);
        MaintenanceStatistics statistics = coordinator.GetStatistics();
        await Assert.That(statistics.ScheduleRejections).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.DrainFaults).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task DisposeRejectsNewRequestsAndStaleScheduledCallbackDoesNoWork()
    {
        ManualMaintenanceScheduler scheduler = new();
        int passes = 0;
        MaintenanceCoordinator coordinator = new(
            () =>
            {
                passes++;
                return false;
            },
            scheduler
        );
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        coordinator.Dispose();
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Disposed);
        scheduler.RunNext();
        await Assert.That(passes).IsEqualTo(0);
        await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Disposed);
        coordinator.Dispose();
    }

    [Test]
    public async Task DefaultSchedulerRearmsWithoutFlowingContextOrOverlappingDrains()
    {
        TaskCompletionSource<bool>[] entered =
        [
            NewCompletionSource<bool>(),
            NewCompletionSource<bool>(),
        ];
        TaskCompletionSource<bool>[] release =
        [
            NewCompletionSource<bool>(),
            NewCompletionSource<bool>(),
        ];
        string?[] observedContexts = new string?[4];
        int passes = 0;
        var activeDrains = new System.Runtime.CompilerServices.StrongBox<int>();
        int overlappingDrains = 0;
        MaintenanceCoordinator coordinator = new(
            () =>
            {
                if (Interlocked.Increment(ref activeDrains.Value) != 1)
                {
                    Interlocked.Increment(ref overlappingDrains);
                }

                try
                {
                    int pass = Interlocked.Increment(ref passes) - 1;
                    observedContexts[pass] = Context.Value;
                    Context.Value = "drain-context";
                    if (pass % 2 != 0)
                    {
                        return false;
                    }

                    entered[pass / 2].TrySetResult(true);
                    release[pass / 2]
                        .Task.WaitAsync(TestTimeout, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref activeDrains.Value);
                }
            },
            maxPassesPerInvocation: 1
        );
        try
        {
            try
            {
                for (int round = 0; round < entered.Length; round++)
                {
                    Context.Value = "request-context";
                    try
                    {
                        await Assert
                            .That(coordinator.Request())
                            .IsEqualTo(MaintenanceRequestResult.Accepted);
                    }
                    finally
                    {
                        Context.Value = null;
                    }

                    await entered[round].Task.WaitAsync(TestTimeout);
                    await Assert
                        .That(coordinator.Request())
                        .IsEqualTo(MaintenanceRequestResult.Accepted);
                    await Assert.That(coordinator.CleanUp().Performed).IsFalse();
                    await Assert.That(Volatile.Read(ref activeDrains.Value)).IsEqualTo(1);
                    release[round].TrySetResult(true);
                    await WaitForCompletedDrainAsync(coordinator, (round + 1) * 2);
                    await Assert
                        .That(coordinator.State)
                        .IsEqualTo(MaintenanceCoordinatorState.Idle);
                }

                await Assert.That(passes).IsEqualTo(4);
                await Assert.That(overlappingDrains).IsEqualTo(0);
                await Assert.That(observedContexts).All(context => context == null);
                MaintenanceStatistics statistics = coordinator.GetStatistics();
                await Assert.That(statistics.DrainPasses).IsEqualTo(4);
                await Assert.That(statistics.BudgetExhaustions).IsEqualTo(2);
                await Assert.That(statistics.SynchronousCleanUps).IsEqualTo(0);
                await Assert.That(statistics.ScheduleRejections).IsEqualTo(0);
                await Assert.That(statistics.DrainFaults).IsEqualTo(0);
            }
            finally
            {
                coordinator.Dispose();
                foreach (TaskCompletionSource<bool> gate in release)
                {
                    gate.TrySetResult(true);
                }
            }
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    [Test]
    public async Task DefaultSchedulerDoesNotRearmDisposedRunningDrain()
    {
        TaskCompletionSource<bool> entered = NewCompletionSource<bool>();
        TaskCompletionSource<bool> release = NewCompletionSource<bool>();
        int passes = 0;
        MaintenanceCoordinator coordinator = new(
            () =>
            {
                Interlocked.Increment(ref passes);
                entered.TrySetResult(true);
                release
                    .Task.WaitAsync(TestTimeout, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return true;
            },
            maxPassesPerInvocation: 1
        );
        try
        {
            try
            {
                await Assert
                    .That(coordinator.Request())
                    .IsEqualTo(MaintenanceRequestResult.Accepted);
                await entered.Task.WaitAsync(TestTimeout);
                await Assert
                    .That(coordinator.Request())
                    .IsEqualTo(MaintenanceRequestResult.Accepted);
                coordinator.Dispose();
                await Assert
                    .That(coordinator.Request())
                    .IsEqualTo(MaintenanceRequestResult.Disposed);
                await Assert.That(coordinator.CleanUp().Performed).IsFalse();
            }
            finally
            {
                coordinator.Dispose();
                release.TrySetResult(true);
            }

            await WaitForCompletedDrainAsync(coordinator, 1);
            await Assert.That(passes).IsEqualTo(1);
            MaintenanceStatistics statistics = coordinator.GetStatistics();
            await Assert.That(statistics.State).IsEqualTo(MaintenanceCoordinatorState.Disposed);
            await Assert.That(statistics.DrainPasses).IsEqualTo(1);
            await Assert.That(statistics.BudgetExhaustions).IsEqualTo(0);
            await Assert.That(statistics.DrainFaults).IsEqualTo(0);
        }
        finally
        {
            coordinator.Dispose();
        }
    }

    private static async Task WaitForCompletedDrainAsync(
        MaintenanceCoordinator coordinator,
        long expectedPasses
    )
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        while (true)
        {
            MaintenanceStatistics statistics = coordinator.GetStatistics();
            if (
                statistics.DrainPasses >= expectedPasses
                && statistics.State
                    is MaintenanceCoordinatorState.Idle
                        or MaintenanceCoordinatorState.Disposed
            )
            {
                return;
            }

            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Publication/reservation hooks can block until another worker or the test releases them.
    private static Task<bool> StartEnqueue<T>(StripedReadBuffer<T> buffer, T value)
    {
        return Task.Factory.StartNew(
            static state =>
            {
                (StripedReadBuffer<T> buffer, T value) = ((StripedReadBuffer<T> Buffer, T Value))
                    state!;
                return buffer.TryEnqueue(value);
            },
            (buffer, value),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
    }

    private static Task<(bool Accepted, WeakReference<TrackedValue> Weak)> StartTrackedEnqueue(
        StripedReadBuffer<TrackedValue> buffer
    )
    {
        return Task.Factory.StartNew(
            static state =>
            {
                StripedReadBuffer<TrackedValue> buffer = (StripedReadBuffer<TrackedValue>)state!;
                TrackedValue value = new();
                WeakReference<TrackedValue> weak = new(value);
                return (buffer.TryEnqueue(value), weak);
            },
            buffer,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
    }

    private static bool EventuallyCollected<T>(WeakReference<T> weakReference)
        where T : class
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakReference.TryGetTarget(out _))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private sealed class PublishGate : IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        private int _pause;

        internal void BeforePublish()
        {
            if (Interlocked.Exchange(ref _pause, 1) != 0)
            {
                return;
            }

            Entered.Set();
            if (!Release.Wait(TestTimeout))
            {
                throw new TimeoutException("The producer did not resume.");
            }
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class ContendedReserveGate : IDisposable
    {
        private readonly Barrier _barrier;
        private int _hookCalls;

        internal ContendedReserveGate(int participantCount)
        {
            _barrier = new Barrier(participantCount);
        }

        internal void BeforeReserve()
        {
            if (Interlocked.Increment(ref _hookCalls) <= 2 && !_barrier.SignalAndWait(TestTimeout))
            {
                throw new TimeoutException("The competing producers did not meet.");
            }
        }

        public void Dispose() => _barrier.Dispose();
    }

    private sealed record ReadEvent(
        [property: UsedImplicitly] int Producer,
        [property: UsedImplicitly] int Sequence
    );

    private sealed class TrackedValue;

    private sealed class ManualMaintenanceScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        internal bool Reject { get; init; }
        internal bool Throw { get; init; }
        internal int ScheduleCalls { get; private set; }
        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            ScheduleCalls++;
            if (Throw)
            {
                throw new InvalidOperationException("test scheduler failure");
            }

            if (Reject)
            {
                return false;
            }

            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunNext()
        {
            _callbacks.Dequeue()();
        }
    }

    private sealed class InlineMaintenanceScheduler : IMaintenanceScheduler
    {
        internal int ScheduleCalls { get; private set; }

        public bool TrySchedule(Action callback)
        {
            ScheduleCalls++;
            callback();
            return true;
        }
    }

    private sealed class ReentrantDrain
    {
        private int _passes;
        internal int Passes => Volatile.Read(ref _passes);
        internal MaintenanceCoordinator Coordinator { private get; set; } = null!;

        internal bool Invoke()
        {
            Interlocked.Increment(ref _passes);
            if ((Coordinator.Request()) != (MaintenanceRequestResult.Accepted))
                Assert.Fail(
                    "Expected Coordinator.Request() to equal (MaintenanceRequestResult.Accepted)."
                );
            return true;
        }
    }
}
