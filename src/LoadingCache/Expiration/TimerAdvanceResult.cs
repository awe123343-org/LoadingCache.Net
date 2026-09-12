namespace LoadingCache.Expiration;

/// <summary>
/// The bounded result of advancing a timer wheel.
/// </summary>
/// <typeparam name="T">The value associated with timer entries.</typeparam>
internal sealed class TimerAdvanceResult<T>
    where T : notnull
{
    internal TimerAdvanceResult(IReadOnlyList<IdentityTimerNode<T>> dueNodes, bool hasPending)
    {
        DueNodes = dueNodes;
        HasPending = hasPending;
    }

    internal IReadOnlyList<IdentityTimerNode<T>> DueNodes { get; }

    internal bool HasPending { get; }
}
