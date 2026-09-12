namespace LoadingCache.Maintenance;

/// <summary>
/// A point-in-time snapshot of maintenance coordination counters.
/// </summary>
internal readonly struct MaintenanceStatistics
{
    internal MaintenanceStatistics(
        MaintenanceCoordinatorState state,
        long requests,
        long coalescedRequests,
        long drainPasses,
        long moreWorkPasses,
        long scheduleRejections,
        long drainFaults,
        long synchronousCleanUps,
        long budgetExhaustions,
        bool fallbackRequired
    )
    {
        State = state;
        Requests = requests;
        CoalescedRequests = coalescedRequests;
        DrainPasses = drainPasses;
        MoreWorkPasses = moreWorkPasses;
        ScheduleRejections = scheduleRejections;
        DrainFaults = drainFaults;
        SynchronousCleanUps = synchronousCleanUps;
        BudgetExhaustions = budgetExhaustions;
        FallbackRequired = fallbackRequired;
    }

    internal MaintenanceCoordinatorState State { get; }

    internal long Requests { get; }

    internal long CoalescedRequests { get; }

    internal long DrainPasses { get; }

    internal long MoreWorkPasses { get; }

    internal long ScheduleRejections { get; }

    internal long DrainFaults { get; }

    internal long SynchronousCleanUps { get; }

    internal long BudgetExhaustions { get; }

    internal bool FallbackRequired { get; }
}
