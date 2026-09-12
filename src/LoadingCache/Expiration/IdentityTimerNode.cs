namespace LoadingCache.Expiration;

/// <summary>
/// An identity-bearing timer entry owned by one <see cref="TimerWheel{T}"/>.
/// </summary>
/// <typeparam name="T">The value associated with the timer entry.</typeparam>
internal sealed class IdentityTimerNode<T>
    where T : notnull
{
    internal const int NotScheduled = -1;

    internal IdentityTimerNode(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        Value = value;
    }

    internal T Value { get; }

    internal ulong Deadline { get; private set; }

    internal bool IsRetired { get; private set; }

    internal long OwnerId { get; set; }

    internal int BucketId { get; set; } = NotScheduled;

    internal IdentityTimerNode<T>? Previous { get; set; }

    internal IdentityTimerNode<T>? Next { get; set; }

    /// <summary>
    /// Gets the insertion sequence for the current bucket link.
    /// A continuation uses this stable frontier instead of a mutable tail node.
    /// </summary>
    internal ulong LinkSequence { get; set; }

    internal bool IsScheduled => BucketId != NotScheduled;

    internal void SetDeadline(ulong deadline)
    {
        Deadline = deadline;
    }

    internal void Retire()
    {
        IsRetired = true;
    }
}
