namespace LoadingCache.Notifications;

/// <summary>
/// Schedules one bounded dispatcher drain operation.
/// </summary>
/// <remarks>
/// Implementations must not capture the triggering request's <see cref="ExecutionContext"/>.
/// A scheduler may reject work; the dispatcher then drops the queued batch and records the
/// rejection rather than leaving a stranded queue.
/// </remarks>
internal interface INotificationScheduler
{
    bool TrySchedule(Action callback);
}

/// <summary>
/// Uses one unsafe ThreadPool work item per drain, rather than one task per notification.
/// </summary>
internal sealed class ThreadPoolNotificationScheduler : INotificationScheduler
{
    internal static ThreadPoolNotificationScheduler Instance { get; } = new();

    private ThreadPoolNotificationScheduler() { }

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
