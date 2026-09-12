namespace LoadingCache.Policy;

internal static class PolicyOwnerIds
{
    internal static long Next() => Interlocked.Increment(ref _next);

    private static long _next;
}

internal enum PolicyQueue : byte
{
    None,
    Window,
    Probation,
    Protected,
}

/// <summary>
/// An identity-bearing node owned by one policy instance.
/// </summary>
/// <typeparam name="T">The value type held by the node.</typeparam>
internal sealed class PolicyNode<T>
    where T : notnull
{
    internal PolicyNode(T value, long weight, uint hash)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight),
                weight,
                "A policy weight cannot be negative."
            );
        }

        Value = value;
        Weight = weight;
        Hash = hash;
        IsAlive = true;
    }

    internal T Value { get; }

    internal long Weight { get; private set; }

    internal uint Hash { get; }

    internal bool IsAlive { get; private set; }

    internal PolicyQueue Queue { get; set; }

    internal PolicyNode<T>? Previous { get; set; }

    internal PolicyNode<T>? Next { get; set; }

    internal PolicyNode<T>? PositivePrevious { get; set; }

    internal PolicyNode<T>? PositiveNext { get; set; }

    internal PolicyNode<T>? EligiblePrevious { get; set; }

    internal PolicyNode<T>? EligibleNext { get; set; }

    internal PolicyNode<T>? EligiblePositivePrevious { get; set; }

    internal PolicyNode<T>? EligiblePositiveNext { get; set; }

    internal bool IsCandidate { get; set; }

    internal PolicyNode<T>? CandidatePrevious { get; set; }

    internal PolicyNode<T>? CandidateNext { get; set; }

    internal long OwnerId { get; private set; }

    internal void Claim(long ownerId)
    {
        if (ownerId == 0)
        {
            ArgumentOutOfRangeException.ThrowIfZero(ownerId);
        }

        if (OwnerId != 0 || !IsAlive || Queue != PolicyQueue.None)
        {
            throw new InvalidOperationException("A policy node can only be added once.");
        }

        OwnerId = ownerId;
    }

    internal void SetWeight(long weight)
    {
        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight),
                weight,
                "A policy weight cannot be negative."
            );
        }

        Weight = weight;
    }

    internal void Retire()
    {
        IsAlive = false;
        IsCandidate = false;
        Queue = PolicyQueue.None;
        Previous = null;
        Next = null;
        PositivePrevious = null;
        PositiveNext = null;
        EligiblePrevious = null;
        EligibleNext = null;
        EligiblePositivePrevious = null;
        EligiblePositiveNext = null;
        CandidatePrevious = null;
        CandidateNext = null;
    }
}
