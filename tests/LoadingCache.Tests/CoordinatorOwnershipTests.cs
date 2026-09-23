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
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
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
                await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
                await Assert
                    .That(coordinator.Request())
                    .IsEqualTo(MaintenanceRequestResult.Accepted);
            }

            schedulerReturning.Release();
            await original.WaitAsync(Watchdog);
            await Assert.That(coordinator.GetStatistics().FallbackRequired).IsFalse();
            if (replacementCompletes)
            {
                // The same Scheduled enum value now belongs to a later request, not A's re-arm.
                await Assert
                    .That(coordinator.State)
                    .IsEqualTo(MaintenanceCoordinatorState.Scheduled);
                await Assert.That(scheduler.ScheduleCalls).IsEqualTo(3);
            }
            else
            {
                await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Running);
                await Assert
                    .That(coordinator.Request())
                    .IsEqualTo(MaintenanceRequestResult.Accepted);
                await Assert
                    .That(coordinator.State)
                    .IsEqualTo(MaintenanceCoordinatorState.RunningRequired);
                await Assert.That(scheduler.ScheduleCalls).IsEqualTo(2);
                replacementDraining.Release();
                await replacement.WaitAsync(Watchdog);
            }

            scheduler.RunNext();
            await Assert.That(passes).IsEqualTo(3);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(schedulerReturning.TimedOut).IsFalse();
            await Assert.That(replacementDraining.TimedOut).IsFalse();
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
        await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
        Task older = Task.Run(scheduler.RunNext);
        Task<MaintenanceCleanupResult>? newer = null;
        try
        {
            await olderReturning.Entered.WaitAsync(Watchdog);
            newer = Task.Run(coordinator.CleanUp);
            await newerReturning.Entered.WaitAsync(Watchdog);
            olderReturning.Release();
            await older.WaitAsync(Watchdog);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Scheduled);
            await Assert.That(coordinator.GetStatistics().FallbackRequired).IsFalse();
            await Assert.That(fallbacks.Count).IsEqualTo(0);
            newerReturning.Release();
            MaintenanceCleanupResult result = await newer.WaitAsync(Watchdog);
            await Assert.That(result.FallbackRequired).IsTrue();
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(coordinator.GetStatistics().FallbackRequired).IsTrue();
            await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
            scheduler.RunNext();
            await Assert.That(passes).IsEqualTo(3);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(coordinator.GetStatistics().FallbackRequired).IsFalse();
            await Assert.That(olderReturning.TimedOut).IsFalse();
            await Assert.That(newerReturning.TimedOut).IsFalse();
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
            await Assert.That(coordinator.CleanUp().Performed).IsTrue();
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(coordinator.Request()).IsEqualTo(MaintenanceRequestResult.Accepted);
            rejecting.Release();
            await Assert
                .That((await original.WaitAsync(Watchdog)))
                .IsEqualTo(MaintenanceRequestResult.Accepted);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Scheduled);
            scheduler.RunNext();
            await Assert.That(passes).IsEqualTo(2);
            await Assert.That(coordinator.State).IsEqualTo(MaintenanceCoordinatorState.Idle);
            await Assert.That(rejecting.TimedOut).IsFalse();
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
