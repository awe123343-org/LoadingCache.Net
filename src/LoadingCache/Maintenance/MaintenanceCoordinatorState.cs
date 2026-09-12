namespace LoadingCache.Maintenance;

/// <summary>
/// The coalesced maintenance worker state.
/// </summary>
internal enum MaintenanceCoordinatorState
{
    Idle,
    Scheduled,
    Running,
    RunningRequired,
    Disposed,
}

/// <summary>
/// Reports the result of a synchronous maintenance attempt.
/// </summary>
internal readonly struct MaintenanceCleanupResult
{
    internal MaintenanceCleanupResult(bool performed, bool moreWork, bool fallbackRequired)
    {
        Performed = performed;
        MoreWork = moreWork;
        FallbackRequired = fallbackRequired;
    }

    internal bool Performed { get; }

    internal bool MoreWork { get; }

    internal bool FallbackRequired { get; }
}
