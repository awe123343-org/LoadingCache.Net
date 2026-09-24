using FluentAssertions;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class CoordinatorOwnershipTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InlineRearmCannotReleaseAReplacementOwner(bool replacementCompletes)
    {
        await using BlockingTestHook schedulerReturning = new(Watchdog);
        await using BlockingTestHook replacementDraining = new(Watchdog);
        Action pauseReplacement = replacementDraining.Invoke;
        InlineRearmScheduler scheduler = new(schedulerReturning);
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                int pass = Interlocked.Increment(ref passes);
                if (pass == 2)
                {
                    pauseReplacement();
                }

                return pass == 1;
            },
            scheduler,
            maxPassesPerInvocation: 1
        );
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        Task original = Task.Run(scheduler.RunNext);
        Task? replacement = null;
        try
        {
            await schedulerReturning.Entered.WaitAsync(Watchdog);
            replacement = Task.Run(coordinator.CleanUp);
            await replacementDraining.Entered.WaitAsync(Watchdog);
            if (replacementCompletes)
            {
                replacementDraining.Release();
                await replacement.WaitAsync(Watchdog);
                coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
                coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
            }

            schedulerReturning.Release();
            await original.WaitAsync(Watchdog);
            coordinator.GetStatistics().FallbackRequired.Should().BeFalse();
            if (replacementCompletes)
            {
                // The same Scheduled enum value now belongs to a later request, not A's re-arm.
                coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
                scheduler.ScheduleCalls.Should().Be(3);
            }
            else
            {
                coordinator.State.Should().Be(MaintenanceCoordinatorState.Running);
                coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
                coordinator.State.Should().Be(MaintenanceCoordinatorState.RunningRequired);
                scheduler.ScheduleCalls.Should().Be(2);
                replacementDraining.Release();
                await replacement.WaitAsync(Watchdog);
            }

            scheduler.RunNext();
            passes.Should().Be(3);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
            schedulerReturning.TimedOut.Should().BeFalse();
            replacementDraining.TimedOut.Should().BeFalse();
        }
        finally
        {
            schedulerReturning.Release();
            replacementDraining.Release();
            await Task.WhenAll(original, replacement ?? Task.CompletedTask).WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task OlderRearmCannotConsumeANewerOwnersInlineFallback()
    {
        await using BlockingTestHook olderReturning = new(Watchdog);
        await using BlockingTestHook newerReturning = new(Watchdog);
        InlineRearmScheduler scheduler = new(olderReturning, newerReturning);
        int passes = 0;
        CallbackCounter fallbacks = new();
        using MaintenanceCoordinator coordinator = new(
            () => Interlocked.Increment(ref passes) < 3,
            scheduler,
            maxPassesPerInvocation: 1,
            rejectedRemainderFallback: fallbacks.Increment
        );
        coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
        Task older = Task.Run(scheduler.RunNext);
        Task<MaintenanceCleanupResult>? newer = null;
        try
        {
            await olderReturning.Entered.WaitAsync(Watchdog);
            newer = Task.Run(coordinator.CleanUp);
            await newerReturning.Entered.WaitAsync(Watchdog);
            olderReturning.Release();
            await older.WaitAsync(Watchdog);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
            coordinator.GetStatistics().FallbackRequired.Should().BeFalse();
            fallbacks.Count.Should().Be(0);
            newerReturning.Release();
            MaintenanceCleanupResult result = await newer.WaitAsync(Watchdog);
            result.FallbackRequired.Should().BeTrue();
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
            coordinator.GetStatistics().FallbackRequired.Should().BeTrue();
            coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
            scheduler.RunNext();
            passes.Should().Be(3);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
            coordinator.GetStatistics().FallbackRequired.Should().BeFalse();
            olderReturning.TimedOut.Should().BeFalse();
            newerReturning.TimedOut.Should().BeFalse();
        }
        finally
        {
            olderReturning.Release();
            newerReturning.Release();
            await Task.WhenAll(older, newer ?? Task.CompletedTask).WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task OldInitialRejectionCannotCancelANewerScheduledRequest()
    {
        await using BlockingTestHook rejecting = new(Watchdog);
        RejectInitialScheduler scheduler = new(rejecting);
        int passes = 0;
        using MaintenanceCoordinator coordinator = new(
            () =>
            {
                Interlocked.Increment(ref passes);
                return false;
            },
            scheduler
        );
        Task<MaintenanceRequestResult> original = Task.Run(coordinator.Request);
        try
        {
            await rejecting.Entered.WaitAsync(Watchdog);
            coordinator.CleanUp().Performed.Should().BeTrue();
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
            coordinator.Request().Should().Be(MaintenanceRequestResult.Accepted);
            rejecting.Release();
            (await original.WaitAsync(Watchdog)).Should().Be(MaintenanceRequestResult.Accepted);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Scheduled);
            scheduler.RunNext();
            passes.Should().Be(2);
            coordinator.State.Should().Be(MaintenanceCoordinatorState.Idle);
            rejecting.TimedOut.Should().BeFalse();
        }
        finally
        {
            rejecting.Release();
            await original.WaitAsync(Watchdog);
        }
    }

    private sealed class CallbackCounter
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        internal void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class RejectInitialScheduler(BlockingTestHook rejecting) : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        private int _calls;

        public bool TrySchedule(Action callback)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                rejecting.Invoke();
                return false;
            }

            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunNext() => _callbacks.Dequeue()();
    }

    private sealed class InlineRearmScheduler(
        BlockingTestHook returning,
        BlockingTestHook? newerReturning = null
    ) : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        internal int ScheduleCalls { get; private set; }

        public bool TrySchedule(Action callback)
        {
            if (++ScheduleCalls == 2)
            {
                callback();
                returning.Invoke();
            }
            else if (ScheduleCalls == 3 && newerReturning is not null)
            {
                callback();
                newerReturning.Invoke();
            }
            else
            {
                _callbacks.Enqueue(callback);
            }

            return true;
        }

        internal void RunNext() => _callbacks.Dequeue()();
    }
}
