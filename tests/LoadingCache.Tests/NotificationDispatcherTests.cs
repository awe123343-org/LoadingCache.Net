using FluentAssertions;
using LoadingCache.Notifications;

namespace LoadingCache.Tests;

public sealed class NotificationDispatcherTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> Context = new();

    [Test]
    public void CapacityMustBePositive()
    {
        Action action = () =>
        {
            using BoundedNotificationDispatcher<int> dispatcher = new(0, _ => { });
        };
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void AcceptedNotificationsAreDrainedInFifoOrderByOneScheduledWorkItem()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(4, observed.Add, scheduler);
        dispatcher.TryEnqueue(1).Should().BeTrue();
        dispatcher.TryEnqueue(2).Should().BeTrue();
        dispatcher.TryEnqueue(3).Should().BeTrue();
        scheduler.ScheduleCalls.Should().Be(1);
        scheduler.Pending.Should().Be(1);
        scheduler.RunNext();
        observed.Should().Equal(1, 2, 3);
        NotificationDispatchStatistics statistics = dispatcher.GetStatistics();
        statistics.Enqueued.Should().Be(3);
        statistics.Invoked.Should().Be(3);
        statistics.Delivered.Should().Be(3);
        statistics.HandlerFailures.Should().Be(0);
        statistics.Queued.Should().Be(0);
        statistics.HandlerRunning.Should().BeFalse();
    }

    [Test]
    public void FullQueueDropsNewestNotification()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(2, observed.Add, scheduler);
        dispatcher.TryEnqueue(1).Should().BeTrue();
        dispatcher.TryEnqueue(2).Should().BeTrue();
        dispatcher.TryEnqueue(3).Should().BeFalse();
        NotificationDispatchStatistics beforeDrain = dispatcher.GetStatistics();
        beforeDrain.Queued.Should().Be(2);
        beforeDrain.DroppedFull.Should().Be(1);
        scheduler.RunNext();
        observed.Should().Equal(1, 2);
        dispatcher.GetStatistics().Dropped.Should().Be(1);
    }

    [Test]
    public void HandlerFailureIsCountedAndDoesNotStrandFollowingNotifications()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(
            4,
            value =>
            {
                observed.Add(value);
                if (value == 1)
                {
                    throw new InvalidOperationException("test handler failure");
                }
            },
            scheduler
        );
        dispatcher.TryEnqueue(1).Should().BeTrue();
        dispatcher.TryEnqueue(2).Should().BeTrue();
        Action action = scheduler.RunNext;
        action.Should().NotThrow();
        observed.Should().Equal(1, 2);
        NotificationDispatchStatistics statistics = dispatcher.GetStatistics();
        statistics.Invoked.Should().Be(2);
        statistics.Delivered.Should().Be(1);
        statistics.HandlerFailures.Should().Be(1);
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public void ReentrantHandlerCanEnqueueWithoutDeadlockAndKeepsFifoOrder()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        var handler = new ReentrantHandler(observed);
        var dispatcher = new BoundedNotificationDispatcher<int>(4, handler.Invoke, scheduler);
        handler.Dispatcher = dispatcher;
        using (dispatcher)
        {
            dispatcher.TryEnqueue(1).Should().BeTrue();
            dispatcher.TryEnqueue(2).Should().BeTrue();
            scheduler.RunNext();
            observed.Should().Equal(1, 2, 3);
            scheduler.ScheduleCalls.Should().Be(1);
        }
    }

    [Test]
    public void ScheduleRejectionDropsTheBatchAndDoesNotStrandTheNextBatch()
    {
        ManualNotificationScheduler scheduler = new() { Reject = true };
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(4, observed.Add, scheduler);
        dispatcher.TryEnqueue(1).Should().BeFalse();
        NotificationDispatchStatistics rejected = dispatcher.GetStatistics();
        rejected.ScheduleRejections.Should().Be(1);
        rejected.DroppedSchedule.Should().Be(1);
        rejected.Queued.Should().Be(0);
        scheduler.Reject = false;
        dispatcher.TryEnqueue(2).Should().BeTrue();
        scheduler.RunNext();
        observed.Should().Equal(2);
        dispatcher.GetStatistics().HandlerFailures.Should().Be(0);
    }

    [Test]
    public void SlowHandlerRunsOutsideDispatcherLockAndDisposeDoesNotWaitForIt()
    {
        ManualNotificationScheduler scheduler = new();
        TaskCompletionSource<bool> entered = NewCompletionSource<bool>();
        TaskCompletionSource<bool> release = NewCompletionSource<bool>();
        using BoundedNotificationDispatcher<int> dispatcher = new(
            4,
            _ =>
            {
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
            },
            scheduler
        );
        dispatcher.TryEnqueue(1).Should().BeTrue();
        Task drain = Task.Run(scheduler.RunNext);
        try
        {
            entered.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            dispatcher.TryEnqueue(2).Should().BeTrue();
            Task dispose = Task.Run(dispatcher.Dispose);
            dispose.Wait(TestTimeout).Should().BeTrue();
            NotificationDispatchStatistics disposed = dispatcher.GetStatistics();
            disposed.IsDisposed.Should().BeTrue();
            disposed.DroppedShutdown.Should().Be(1);
            dispatcher.TryEnqueue(3).Should().BeFalse();
        }
        finally
        {
            release.TrySetResult(true);
        }

        drain.Wait(TestTimeout).Should().BeTrue();
        dispatcher.GetStatistics().DroppedShutdown.Should().Be(2);
    }

    [Test]
    public void DuplicateSchedulerCallbacksStillExecuteOnlyOneHandlerAtATime()
    {
        ManualNotificationScheduler scheduler = new();
        TaskCompletionSource<bool> entered = NewCompletionSource<bool>();
        TaskCompletionSource<bool> secondStarted = NewCompletionSource<bool>();
        TaskCompletionSource<bool> release = NewCompletionSource<bool>();
        int active = 0;
        int maximumActive = 0;
        using BoundedNotificationDispatcher<int> dispatcher = new(
            2,
            _ =>
            {
                int current = Interlocked.Increment(ref active);
                SetMaximum(ref maximumActive, current);
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
                Interlocked.Decrement(ref active);
            },
            scheduler
        );
        dispatcher.TryEnqueue(1).Should().BeTrue();
        dispatcher.TryEnqueue(2).Should().BeTrue();
        Action callback = scheduler.Peek();
        Task first = Task.Run(callback);
        Task? second;
        try
        {
            entered.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            second = Task.Run(() =>
            {
                secondStarted.SetResult(true);
                callback();
            });
            secondStarted.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        }
        finally
        {
            release.TrySetResult(true);
        }

        Task.WaitAll([first, second], TestTimeout).Should().BeTrue();
        maximumActive.Should().Be(1);
    }

    [Test]
    public void QueueEmptyHandoffDoesNotStrandAConcurrentlyAdmittedBatch()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        TaskCompletionSource<bool> firstHandoff = NewCompletionSource<bool>();
        TaskCompletionSource<bool> releaseFirstHandoff = NewCompletionSource<bool>();
        int handoffCount = 0;
        using BoundedNotificationDispatcher<int> dispatcher = new(
            4,
            observed.Add,
            scheduler,
            () =>
            {
                if (Interlocked.Increment(ref handoffCount) != 1)
                {
                    return;
                }

                firstHandoff.SetResult(true);
                releaseFirstHandoff.Task.GetAwaiter().GetResult();
            }
        );
        dispatcher.TryEnqueue(1).Should().BeTrue();
        Task firstDrain = Task.Run(scheduler.RunNext);
        bool firstDrainCompleted;
        try
        {
            firstHandoff.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            dispatcher.TryEnqueue(2).Should().BeTrue();
            scheduler.Pending.Should().Be(1);
            scheduler.RunNext();
        }
        finally
        {
            releaseFirstHandoff.TrySetResult(true);
            firstDrainCompleted = firstDrain.Wait(TestTimeout);
        }

        firstDrainCompleted.Should().BeTrue();
        observed.Should().Equal(1, 2);
        dispatcher.GetStatistics().Queued.Should().Be(0);
    }

    [Test]
    public async Task DefaultSchedulerDoesNotFlowTriggeringExecutionContext()
    {
        TaskCompletionSource<string?> observed = NewCompletionSource<string?>();
        using BoundedNotificationDispatcher<int> dispatcher = new(
            1,
            _ => observed.TrySetResult(Context.Value)
        );
        Context.Value = "request-context";
        try
        {
            dispatcher.TryEnqueue(1).Should().BeTrue();
        }
        finally
        {
            Context.Value = null;
        }

        (await observed.Task.WaitAsync(TestTimeout)).Should().BeNull();
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void SetMaximum(ref int target, int value)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (
                current >= value
                || Interlocked.CompareExchange(ref target, value, current) == current
            )
            {
                return;
            }
        }
    }

    private sealed class ManualNotificationScheduler : INotificationScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        internal bool Reject { get; set; }
        internal int ScheduleCalls { get; private set; }
        internal int Pending => _callbacks.Count;

        internal Action Peek() => _callbacks.Peek();

        public bool TrySchedule(Action callback)
        {
            ScheduleCalls++;
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

    private sealed class ReentrantHandler(List<int> observed)
    {
        internal BoundedNotificationDispatcher<int> Dispatcher { private get; set; } = null!;

        internal void Invoke(int value)
        {
            observed.Add(value);
            if (value == 1)
            {
                Dispatcher.TryEnqueue(3).Should().BeTrue();
            }
        }
    }
}
