namespace LoadingCache.Maintenance;

/// <summary>
/// Schedules one maintenance worker without flowing the producer's execution context.
/// </summary>
/// <remarks>
/// Implementations may reject a callback. The coordinator reports that rejection to the caller,
/// which must use <see cref="MaintenanceCoordinator.CleanUp" /> or an equivalent synchronous
/// fallback before returning a correctness-relevant mutation to its caller.
/// </remarks>
internal interface IMaintenanceScheduler
{
    bool TrySchedule(Action callback);
}

/// <summary>
/// Schedules maintenance on the ThreadPool without capturing <see cref="ExecutionContext" />.
/// </summary>
internal sealed class ThreadPoolMaintenanceScheduler : IMaintenanceScheduler
{
    internal static ThreadPoolMaintenanceScheduler Instance { get; } = new();

    private ThreadPoolMaintenanceScheduler() { }

    public bool TrySchedule(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ThreadPool.UnsafeQueueUserWorkItem(
            static state => state(),
            callback,
            preferLocal: true
        );
    }
}
