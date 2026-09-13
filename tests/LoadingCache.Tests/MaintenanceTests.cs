using System.Collections.Concurrent;
using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
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
                    Task.Run(() =>
                    {
                        for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                        {
                            ReadEvent value = new(producerId, sequence);
                            if (buffer.TryEnqueue(value))
                            {
                                accepted.Add(value);
                            }
                        }
                    })
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
            statistics.Enqueued.Should().BeLessOrEqualTo(8L * 16L);
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
                consumer = Task.Run(() =>
                {
                    if (!start.SignalAndWait(TestTimeout))
                    {
                        throw new TimeoutException(
                            "The maintenance consumer did not meet its peer."
                        );
                    }

                    while (buffer.TryRead(out _))
                    {
                        Thread.Yield();
                    }
                });
                disposer = Task.Run(() =>
                {
                    if (!start.SignalAndWait(TestTimeout))
                    {
                        throw new TimeoutException("The buffer disposer did not meet its peer.");
                    }

                    buffer.Dispose();
                });

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
    public async Task RequestDuringRunningPassSetsRunningRequiredAndCannotLoseWakeup()
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
        Task worker = Task.Run(scheduler.RunNext);
        try
        {
            await entered.Task.WaitAsync(TestTimeout, TestContext.CurrentContext.CancellationToken);
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

    [TestCase(false)]
    [TestCase(true)]
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
        MaintenanceCoordinator coordinator = null!;
        int passes = 0;
        coordinator = new MaintenanceCoordinator(
            () =>
            {
                Interlocked.Increment(ref passes);
                coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                return true;
            },
            scheduler,
            maxPassesPerInvocation: 2
        );
        using (coordinator)
        {
            MaintenanceCleanupResult first = coordinator.CleanUp();

            first.Performed.Should().BeTrue();
            first.MoreWork.Should().BeTrue();
            first.FallbackRequired.Should().BeFalse();
            Volatile.Read(ref passes).Should().Be(2);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
            scheduler.Pending.Should().Be(1);

            scheduler.RunNext();

            Volatile.Read(ref passes).Should().Be(4);
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
    public async Task DefaultSchedulerDoesNotFlowTriggeringExecutionContext()
    {
        TaskCompletionSource<string?> observed = NewCompletionSource<string?>();
        using MaintenanceCoordinator coordinator = new(() =>
        {
            observed.TrySetResult(Context.Value);
            return false;
        });

        Context.Value = "request-context";
        try
        {
            coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        }
        finally
        {
            Context.Value = null;
        }

        (await observed.Task.WaitAsync(TestTimeout)).Should().BeNull();
        coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record ReadEvent(int Producer, int Sequence);

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
}
