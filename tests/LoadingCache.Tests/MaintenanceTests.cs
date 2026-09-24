using System.Collections.Concurrent;
using FluentAssertions;
using JetBrains.Annotations;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class MaintenanceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> Context = new();

    [Test]
    public void ReadBufferRequiresPositivePowerOfTwoStripesAndPositiveCapacity()
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
        zeroStripes.Should().Throw<ArgumentOutOfRangeException>();
        nonPowerOfTwoStripes.Should().Throw<ArgumentException>();
        zeroCapacity.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void ReadBufferPreservesExactIdentityAndFifoWithinOneStripe()
    {
        using StripedReadBuffer<ReadEvent> buffer = new(1, 4);
        ReadEvent first = new(1, 1);
        ReadEvent second = new(1, 2);
        ReadEvent third = new(1, 3);
        buffer.TryEnqueue(first).Should().BeTrue();
        buffer.TryEnqueue(second).Should().BeTrue();
        buffer.TryEnqueue(third).Should().BeTrue();
        buffer.TryRead(out ReadEvent observedFirst).Should().BeTrue();
        buffer.TryRead(out ReadEvent observedSecond).Should().BeTrue();
        buffer.TryRead(out ReadEvent observedThird).Should().BeTrue();
        buffer.TryRead(out _).Should().BeFalse();
        observedFirst.Should().BeSameAs(first);
        observedSecond.Should().BeSameAs(second);
        observedThird.Should().BeSameAs(third);
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.Enqueued.Should().Be(3);
        statistics.Dequeued.Should().Be(3);
        statistics.Queued.Should().Be(0);
        statistics.Dropped.Should().Be(0);
    }

    [Test]
    public void ReadBufferSupportsBatchDrainAndPublishedProbe()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.HasPublished.Should().BeFalse();
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeTrue();
        buffer.TryEnqueue(3).Should().BeTrue();
        buffer.HasPublished.Should().BeTrue();
        List<int> observed = [];
        buffer.DrainTo(observed.Add, budget: 2).Should().Be(2);
        observed.Should().Equal(1, 2);
        buffer.HasPublished.Should().BeTrue();
        buffer.DrainTo(observed.Add, budget: 2).Should().Be(1);
        observed.Should().Equal(1, 2, 3);
        buffer.HasPublished.Should().BeFalse();
    }

    [Test]
    public void ReadBufferUsesPublicationSequenceForNullValues()
    {
        using StripedReadBuffer<string?> buffer = new(1, 2);
        buffer.TryEnqueue(null).Should().BeTrue();
        buffer.TryRead(out string? observed).Should().BeTrue();
        observed.Should().BeNull();
        buffer.TryRead(out _).Should().BeFalse();
        buffer.GetStatistics().Queued.Should().Be(0);
    }

    [Test]
    public async Task ReadBufferStopsAtAnUnpublishedReservationAndResumesInFifoOrder()
    {
        StripedReadBuffer<int> buffer = new(1, 4);
        PublishGate gate = new();
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetHooksForTesting(beforeReserve: null, beforePublish: gate.BeforePublish);
        Task<bool> producer = StartEnqueue(buffer, 1);
        Task<bool>? laterProducer = null;
        try
        {
            gate.Entered.Wait(TestTimeout).Should().BeTrue();
            buffer.TryRead(out int first).Should().BeTrue();
            first.Should().Be(0);
            laterProducer = StartEnqueue(buffer, 2);
            (await laterProducer.WaitAsync(TestTimeout)).Should().BeTrue();
            buffer.TryRead(out _).Should().BeFalse();
            buffer.HasPublished.Should().BeFalse();
            gate.Release.Set();
            (await producer.WaitAsync(TestTimeout)).Should().BeTrue();
            buffer.TryRead(out int second).Should().BeTrue();
            second.Should().Be(1);
            buffer.TryRead(out int third).Should().BeTrue();
            third.Should().Be(2);
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
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetHooksForTesting(beforeReserve: gate.BeforeReserve, beforePublish: null);
        Task<bool> first = StartEnqueue(buffer, 1);
        Task<bool> second = StartEnqueue(buffer, 2);
        try
        {
            (await Task.WhenAll(first, second).WaitAsync(TestTimeout)).Should().Equal(true, true);
            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.DroppedFailed.Should().Be(0);
            statistics.Dropped.Should().Be(0);
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
    public void ReadBufferReportsOneFinalFailedOfferAfterBoundedRetries()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.TryEnqueue(1).Should().BeFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedFailed.Should().Be(1);
        statistics.Dropped.Should().Be(1);
        buffer.SetForcedCasFailuresForTesting(0);
        buffer.TryEnqueue(1).Should().BeTrue();
    }

    [Test]
    public void ReadBufferExpandsOnlyToTheConfiguredStripeLimit()
    {
        using StripedReadBuffer<int> buffer = new(2, 4);
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetForcedCasFailuresForTesting(3);
        buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Failed);
        buffer.StripeCountForTesting.Should().Be(2);
        buffer.SetForcedCasFailuresForTesting(0);
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.StripeCountForTesting.Should().BeLessThanOrEqualTo(2);
    }

    [Test]
    public void ReadBufferRejectsNonPowerOfTwoCapacity()
    {
        Action create = () =>
        {
            using StripedReadBuffer<int> buffer = new(1, 3);
            _ = buffer.GetStatistics();
        };
        create.Should().Throw<ArgumentException>();
    }

    [Test]
    [Arguments(long.MaxValue - 2)]
    [Arguments(-2L)]
    public void ReadBufferHandlesCounterWrapWithPowerOfTwoCapacity(long counter)
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryEnqueue(0).Should().BeTrue();
        buffer.SetCounterForTesting(counter);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            int firstValue = cycle * 4 + 1;
            for (int offset = 0; offset < 4; offset++)
            {
                buffer.TryEnqueue(firstValue + offset).Should().BeTrue();
            }

            buffer.TryEnqueue(firstValue + 4).Should().BeFalse();
            buffer.GetStatistics().Queued.Should().Be(4);
            for (int offset = 0; offset < 4; offset++)
            {
                buffer.TryRead(out int observed).Should().BeTrue();
                observed.Should().Be(firstValue + offset);
            }

            buffer.TryRead(out _).Should().BeFalse();
            buffer.GetStatistics().Queued.Should().Be(0);
        }
    }

    [Test]
    public void ReadBufferContinuesAfterAConsumerCallbackThrows()
    {
        using StripedReadBuffer<int> buffer = new(1, 4);
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeTrue();
        buffer
            .Invoking(static target =>
                target.DrainTo(
                    static _ => throw new InvalidOperationException("test callback failure"),
                    budget: 4
                )
            )
            .Should()
            .Throw<InvalidOperationException>();
        buffer.TryRead(out int observed).Should().BeTrue();
        observed.Should().Be(2);
    }

    [Test]
    public async Task ReadBufferDisposalDoesNotRetainAValuePublishedByAPausedProducer()
    {
        StripedReadBuffer<TrackedValue> buffer = new(1, 2);
        PublishGate gate = new();
        TrackedValue initial = new();
        buffer.TryEnqueue(initial).Should().BeTrue();
        buffer.TryRead(out _).Should().BeTrue();
        buffer.SetHooksForTesting(beforeReserve: null, beforePublish: gate.BeforePublish);
        Task<(bool Accepted, WeakReference<TrackedValue> Weak)> producer = StartTrackedEnqueue(
            buffer
        );
        try
        {
            gate.Entered.Wait(TestTimeout).Should().BeTrue();
            buffer.Dispose();
            gate.Release.Set();
            (bool accepted, WeakReference<TrackedValue> weak) = await producer.WaitAsync(
                TestTimeout
            );
            accepted.Should().BeFalse();
            buffer.SetHooksForTesting(null, null);
            EventuallyCollected(weak).Should().BeTrue();
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
    public void ReadBufferFullTryWriteDropsNewestItemInsteadOfReportingFalseSuccess()
    {
        using StripedReadBuffer<int> buffer = new(1, 1);
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeFalse();
        buffer.TryRead(out int observed).Should().BeTrue();
        observed.Should().Be(1);
        buffer.TryRead(out _).Should().BeFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.Enqueued.Should().Be(1);
        statistics.DroppedFull.Should().Be(1);
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public void ReadBufferDiagnosticCountersSaturateWithoutCorruptingQueuedCount()
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
        statistics.Queued.Should().Be(0);
        statistics.Enqueued.Should().Be(long.MaxValue);
        statistics.Dequeued.Should().Be(long.MaxValue);
        statistics.DroppedFull.Should().Be(long.MaxValue);
        statistics.DroppedShutdown.Should().Be(long.MaxValue);
        statistics.Dropped.Should().Be(long.MaxValue);
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

            observed.Count.Should().Be(accepted.Count);
            observed.Should().OnlyHaveUniqueItems();
            foreach (ReadEvent value in observed)
            {
                accepted.Should().Contain(value);
            }

            ReadBufferStatistics statistics = buffer.GetStatistics();
            statistics.Queued.Should().Be(0);
            statistics.Enqueued.Should().Be(observed.Count);
            statistics.Enqueued.Should().BeLessThanOrEqualTo(8L * 16L);
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
            statistics.IsDisposed.Should().BeTrue();
            statistics.Queued.Should().Be(0);
            statistics.DroppedShutdown.Should().BeGreaterThanOrEqualTo(statistics.Enqueued);
            buffer.TryEnqueue(-1).Should().BeFalse();
            buffer.TryRead(out _).Should().BeFalse();
            buffer.GetStatistics().Queued.Should().Be(0);
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
    public void ReadBufferDisposeDropsQueuedItemsAndRejectsLaterPublication()
    {
        StripedReadBuffer<int> buffer = new(2, 2);
        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeTrue();
        buffer.Dispose();
        buffer.TryEnqueue(3).Should().BeFalse();
        buffer.TryRead(out _).Should().BeFalse();
        ReadBufferStatistics statistics = buffer.GetStatistics();
        statistics.IsDisposed.Should().BeTrue();
        statistics.Queued.Should().Be(0);
        statistics.DroppedShutdown.Should().Be(3);
        buffer.Dispose();
        buffer.GetStatistics().DroppedShutdown.Should().Be(3);
    }

    [Test]
    public async Task ReadBufferDisposeMayDrainWhileTheMaintenanceConsumerReads()
    {
        StripedReadBuffer<int> buffer = new(4, 64);
        try
        {
            for (int value = 0; value < 64; value++)
            {
                buffer.TryEnqueue(value).Should().BeTrue();
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
                statistics.IsDisposed.Should().BeTrue();
                statistics.Queued.Should().Be(0);
                statistics.Enqueued.Should().Be(statistics.Dequeued + statistics.DroppedShutdown);
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
    public void CoordinatorCoalescesRequestsAndRepeatsBoundedPassesWithoutRecursion()
    {
        ManualMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () => Interlocked.Increment(ref passes) < 3,
            scheduler
        );
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        scheduler.ScheduleCalls.Should().Be(1);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
        scheduler.RunNext();
        passes.Should().Be(3);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        MaintenanceStatistics statistics = coordinator.GetStatistics();
        statistics.Requests.Should().Be(2);
        statistics.CoalescedRequests.Should().Be(1);
        statistics.DrainPasses.Should().Be(3);
        statistics.MoreWorkPasses.Should().Be(2);
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        Task worker = Task.Factory.StartNew(
            scheduler.RunNext,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await entered.Task.WaitAsync(TestTimeout, cancellationToken);
            coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.RunningRequired);
        }
        finally
        {
            release.TrySetResult(true);
            await worker.WaitAsync(TestTimeout, CancellationToken.None);
        }

        passes.Should().Be(2);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        coordinator.GetStatistics().CoalescedRequests.Should().Be(1);
    }

    [Test]
    public void CleanUpClaimsScheduledWorkAndLeavesStaleCallbackHarmless()
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        cleanup.Performed.Should().BeTrue();
        cleanup.MoreWork.Should().BeFalse();
        cleanup.FallbackRequired.Should().BeFalse();
        passes.Should().Be(1);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        scheduler.RunNext();
        passes.Should().Be(1);
        coordinator.GetStatistics().SynchronousCleanUps.Should().Be(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RejectedOrThrowingSchedulerReportsSynchronousFallback(bool throws)
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.ScheduleRejected);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        coordinator.GetStatistics().ScheduleRejections.Should().Be(1);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        cleanup.Performed.Should().BeTrue();
        cleanup.MoreWork.Should().BeFalse();
        cleanup.FallbackRequired.Should().BeFalse();
        passes.Should().Be(1);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    [Test]
    public void CleanUpReportsFallbackWhenRearmIsRejectedWithRemainingWork()
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.ScheduleRejected);
        MaintenanceCleanupResult cleanup = coordinator.CleanUp();
        cleanup.Performed.Should().BeTrue();
        cleanup.MoreWork.Should().BeTrue();
        cleanup.FallbackRequired.Should().BeTrue();
        passes.Should().Be(1);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        coordinator.GetStatistics().FallbackRequired.Should().BeTrue();
        coordinator.GetStatistics().ScheduleRejections.Should().Be(2);
    }

    [Test]
    public void InlineSchedulerCannotBypassThePerInvocationPassBudget()
    {
        InlineMaintenanceScheduler scheduler = new();
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () => Interlocked.Increment(ref passes) < 128,
            scheduler,
            maxPassesPerInvocation: 8
        );
        coordinator.Request().Should().Be(MaintenanceRequestResult.ScheduleRejected);
        passes.Should().Be(8);
        scheduler.ScheduleCalls.Should().Be(2);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        coordinator.GetStatistics().FallbackRequired.Should().BeTrue();
    }

    [Test]
    public void CleanUpReturnsMoreWorkAfterBudgetAndRequestDuringDrainCannotExtendIt()
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
            first.Performed.Should().BeTrue();
            first.MoreWork.Should().BeTrue();
            first.FallbackRequired.Should().BeFalse();
            drain.Passes.Should().Be(2);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
            scheduler.Pending.Should().Be(1);
            scheduler.RunNext();
            drain.Passes.Should().Be(4);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
            coordinator.GetStatistics().BudgetExhaustions.Should().Be(2);
        }
    }

    [Test]
    public void DrainFaultIsObservedAndDoesNotStrandTheCoordinator()
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        scheduler.RunNext();
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
        coordinator.GetStatistics().DrainFaults.Should().Be(1);
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        scheduler.RunNext();
        passes.Should().Be(2);
        coordinator.GetStatistics().DrainFaults.Should().Be(1);
    }

    [Test]
    public void CoordinatorDiagnosticCountersSaturateAtLongMaxValue()
    {
        using MaintenanceCoordinator coordinator = new(() => false);
        coordinator.AddStatisticsForTesting(
            scheduleRejections: long.MaxValue - 1,
            drainFaults: long.MaxValue - 1
        );
        coordinator.AddStatisticsForTesting(scheduleRejections: 2, drainFaults: 2);
        MaintenanceStatistics statistics = coordinator.GetStatistics();
        statistics.ScheduleRejections.Should().Be(long.MaxValue);
        statistics.DrainFaults.Should().Be(long.MaxValue);
    }

    [Test]
    public void DisposeRejectsNewRequestsAndStaleScheduledCallbackDoesNoWork()
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
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        coordinator.Dispose();
        coordinator.Request().Should().Be(MaintenanceRequestResult.Disposed);
        scheduler.RunNext();
        passes.Should().Be(0);
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Disposed);
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
                        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                    }
                    finally
                    {
                        Context.Value = null;
                    }

                    await entered[round].Task.WaitAsync(TestTimeout);
                    coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                    coordinator.CleanUp().Performed.Should().BeFalse();
                    Volatile.Read(ref activeDrains.Value).Should().Be(1);
                    release[round].TrySetResult(true);
                    await WaitForCompletedDrainAsync(coordinator, (round + 1) * 2);
                    coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
                }

                passes.Should().Be(4);
                overlappingDrains.Should().Be(0);
                observedContexts.Should().OnlyContain(context => context == null);
                MaintenanceStatistics statistics = coordinator.GetStatistics();
                statistics.DrainPasses.Should().Be(4);
                statistics.BudgetExhaustions.Should().Be(2);
                statistics.SynchronousCleanUps.Should().Be(0);
                statistics.ScheduleRejections.Should().Be(0);
                statistics.DrainFaults.Should().Be(0);
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
                coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                await entered.Task.WaitAsync(TestTimeout);
                coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                coordinator.Dispose();
                coordinator.Request().Should().Be(MaintenanceRequestResult.Disposed);
                coordinator.CleanUp().Performed.Should().BeFalse();
            }
            finally
            {
                coordinator.Dispose();
                release.TrySetResult(true);
            }

            await WaitForCompletedDrainAsync(coordinator, 1);
            passes.Should().Be(1);
            MaintenanceStatistics statistics = coordinator.GetStatistics();
            statistics.State.Should().Be(MaintenanceCoordinatorState.Disposed);
            statistics.DrainPasses.Should().Be(1);
            statistics.BudgetExhaustions.Should().Be(0);
            statistics.DrainFaults.Should().Be(0);
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
            Coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
            return true;
        }
    }
}
