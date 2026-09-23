using LoadingCache.Notifications;

namespace LoadingCache.Tests;

public sealed class NotificationDispatcherTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<string?> Context = new();

    [Test]
    public async Task CapacityMustBePositive()
    {
        Action action = () =>
        {
            using BoundedNotificationDispatcher<int> dispatcher = new(0, _ => { });
        };
        await Assert.That(action).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AcceptedNotificationsAreDrainedInFifoOrderByOneScheduledWorkItem()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(4, observed.Add, scheduler);
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(3)).IsTrue();
        await Assert.That(scheduler.ScheduleCalls).IsEqualTo(1);
        await Assert.That(scheduler.Pending).IsEqualTo(1);
        scheduler.RunNext();
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        NotificationDispatchStatistics statistics = dispatcher.GetStatistics();
        await Assert.That(statistics.Enqueued).IsEqualTo(3);
        await Assert.That(statistics.Invoked).IsEqualTo(3);
        await Assert.That(statistics.Delivered).IsEqualTo(3);
        await Assert.That(statistics.HandlerFailures).IsEqualTo(0);
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.HandlerRunning).IsFalse();
    }

    [Test]
    public async Task FullQueueDropsNewestNotification()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(2, observed.Add, scheduler);
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(3)).IsFalse();
        NotificationDispatchStatistics beforeDrain = dispatcher.GetStatistics();
        await Assert.That(beforeDrain.Queued).IsEqualTo(2);
        await Assert.That(beforeDrain.DroppedFull).IsEqualTo(1);
        scheduler.RunNext();
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dispatcher.GetStatistics().Dropped).IsEqualTo(1);
    }

    [Test]
    public async Task HandlerFailureIsCountedAndDoesNotStrandFollowingNotifications()
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
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
        Action action = scheduler.RunNext;
        await Assert.That(action).ThrowsNothing();
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        NotificationDispatchStatistics statistics = dispatcher.GetStatistics();
        await Assert.That(statistics.Invoked).IsEqualTo(2);
        await Assert.That(statistics.Delivered).IsEqualTo(1);
        await Assert.That(statistics.HandlerFailures).IsEqualTo(1);
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ReentrantHandlerCanEnqueueWithoutDeadlockAndKeepsFifoOrder()
    {
        ManualNotificationScheduler scheduler = new();
        List<int> observed = [];
        var handler = new ReentrantHandler(observed);
        var dispatcher = new BoundedNotificationDispatcher<int>(4, handler.Invoke, scheduler);
        handler.Dispatcher = dispatcher;
        using (dispatcher)
        {
            await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
            await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
            scheduler.RunNext();
            await Assert
                .That(observed)
                .IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(scheduler.ScheduleCalls).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ScheduleRejectionDropsTheBatchAndDoesNotStrandTheNextBatch()
    {
        ManualNotificationScheduler scheduler = new() { Reject = true };
        List<int> observed = [];
        using BoundedNotificationDispatcher<int> dispatcher = new(4, observed.Add, scheduler);
        await Assert.That(dispatcher.TryEnqueue(1)).IsFalse();
        NotificationDispatchStatistics rejected = dispatcher.GetStatistics();
        await Assert.That(rejected.ScheduleRejections).IsEqualTo(1);
        await Assert.That(rejected.DroppedSchedule).IsEqualTo(1);
        await Assert.That(rejected.Queued).IsEqualTo(0);
        scheduler.Reject = false;
        await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
        scheduler.RunNext();
        await Assert
            .That(observed)
            .IsEquivalentTo([2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dispatcher.GetStatistics().HandlerFailures).IsEqualTo(0);
    }

    [Test]
    public async Task SlowHandlerRunsOutsideDispatcherLockAndDisposeDoesNotWaitForIt()
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
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        Task drain = Task.Run(scheduler.RunNext);
        try
        {
            entered.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
            Task dispose = Task.Run(dispatcher.Dispose);
            await Assert.That(dispose.Wait(TestTimeout)).IsTrue();
            NotificationDispatchStatistics disposed = dispatcher.GetStatistics();
            await Assert.That(disposed.IsDisposed).IsTrue();
            await Assert.That(disposed.DroppedShutdown).IsEqualTo(1);
            await Assert.That(dispatcher.TryEnqueue(3)).IsFalse();
        }
        finally
        {
            release.TrySetResult(true);
        }

        await Assert.That(drain.Wait(TestTimeout)).IsTrue();
        await Assert.That(dispatcher.GetStatistics().DroppedShutdown).IsEqualTo(2);
    }

    [Test]
    public async Task DuplicateSchedulerCallbacksStillExecuteOnlyOneHandlerAtATime()
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
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
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

        await Assert.That(Task.WaitAll([first, second], TestTimeout)).IsTrue();
        await Assert.That(maximumActive).IsEqualTo(1);
    }

    [Test]
    public async Task QueueEmptyHandoffDoesNotStrandAConcurrentlyAdmittedBatch()
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
        await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        Task firstDrain = Task.Run(scheduler.RunNext);
        bool firstDrainCompleted;
        try
        {
            firstHandoff.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            await Assert.That(dispatcher.TryEnqueue(2)).IsTrue();
            await Assert.That(scheduler.Pending).IsEqualTo(1);
            scheduler.RunNext();
        }
        finally
        {
            releaseFirstHandoff.TrySetResult(true);
            firstDrainCompleted = firstDrain.Wait(TestTimeout);
        }

        await Assert.That(firstDrainCompleted).IsTrue();
        await Assert
            .That(observed)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dispatcher.GetStatistics().Queued).IsEqualTo(0);
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
            await Assert.That(dispatcher.TryEnqueue(1)).IsTrue();
        }
        finally
        {
            Context.Value = null;
        }

        await Assert.That(((await observed.Task.WaitAsync(TestTimeout))) is null).IsTrue();
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
                if (!(Dispatcher.TryEnqueue(3)))
                    Assert.Fail("Expected Dispatcher.TryEnqueue(3) to be true ().");
            }
        }
    }
}
