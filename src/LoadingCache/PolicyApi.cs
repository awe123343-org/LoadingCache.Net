using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>
/// A read-only view over the cache policies enabled by a builder.
/// </summary>
[PublicAPI]
public interface ICachePolicy<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Reads a fresh resident value without access, statistics, expiry callbacks, or refresh.</summary>
    bool TryGetQuietly(TKey key, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Gets monitor statistics when memory-pressure eviction is enabled; otherwise null.</summary>
    MemoryPressureStatistics? MemoryPressureStatistics { get; }

    /// <summary>Gets the eviction policy view, when eviction is enabled.</summary>
    IEvictionPolicy<TKey, TValue>? Eviction { get; }

    /// <summary>Gets the fixed expire-after-access view, when enabled.</summary>
    /// <remarks>Lock-free hits may coalesce accesses within a duration-dependent tolerance capped at one millisecond.</remarks>
    IFixedExpirationPolicy<TKey, TValue>? ExpireAfterAccess { get; }

    /// <summary>Gets the fixed expire-after-write view, when enabled.</summary>
    IFixedExpirationPolicy<TKey, TValue>? ExpireAfterWrite { get; }

    /// <summary>Gets the request-triggered refresh-after-write view, when enabled.</summary>
    IFixedExpirationPolicy<TKey, TValue>? RefreshAfterWrite { get; }

    /// <summary>Gets the variable expiration view, when enabled.</summary>
    IVariableExpirationPolicy<TKey, TValue>? VariableExpiration { get; }
}

/// <summary>Describes the configured eviction policy.</summary>
[PublicAPI]
public interface IEvictionPolicy<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Gets the configured maximum weight.</summary>
    long Maximum { get; }

    /// <summary>Gets the current policy weighted size.</summary>
    long WeightedSize { get; }

    /// <summary>Changes the positive maximum and evicts excess residents before returning.</summary>
    /// <remarks>Size limits must fit in an Int32. A weighted cache retains its separate resident count limit.</remarks>
    void SetMaximum(long maximum);

    /// <summary>Copies up to the requested number of residents in approximate coldest-first policy order.</summary>
    /// <remarks>Order follows policy segments and their recency, not a globally sorted frequency ranking.</remarks>
    IReadOnlyList<KeyValuePair<TKey, TValue>> Coldest(int limit);

    /// <summary>Copies up to the requested number of residents in approximate hottest-first policy order.</summary>
    /// <remarks>Copying costs O(limit) time and space; concurrent reads and lossy access records make order approximate.</remarks>
    IReadOnlyList<KeyValuePair<TKey, TValue>> Hottest(int limit);
}

/// <summary>Describes a fixed expiration policy.</summary>
/// <remarks>
/// Fixed expire-after-access lock-free hits may coalesce nearby accesses. The tolerance is
/// at most the configured duration divided by 2^20, capped at one millisecond. The actual
/// threshold uses the guarded freshness bound and rounds down to provider timestamp units.
/// An entry can expire this much early; its recorded age can be this much greater, and its
/// remaining duration this much smaller, than a timestamp for every successful access.
/// Other read paths keep exact access timestamps. Expire-after-write and refresh ages are unchanged.
/// </remarks>
[PublicAPI]
public interface IFixedExpirationPolicy<in TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Gets the configured expiration duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Gets the remaining duration for a resident key.</summary>
    /// <remarks>Expire-after-access uses the recorded, potentially coalesced access timestamp.</remarks>
    TimeSpan? GetExpiresAfter(TKey key);

    /// <summary>Changes the configured duration for subsequent freshness checks.</summary>
    /// <remarks>
    /// Does not recover previously coalesced accesses. After shrinking expire-after-access,
    /// the old tolerance's error can exceed the new tolerance, but remains at most one millisecond,
    /// until an actual touch or value replacement. An entry may therefore expire immediately.
    /// </remarks>
    void SetDuration(TimeSpan duration);

    /// <summary>Alias for <see cref="SetDuration"/>.</summary>
    /// <remarks>Retains the same previously recorded access age as <see cref="SetDuration"/>.</remarks>
    void SetExpiresAfter(TimeSpan duration);

    /// <summary>Gets the age of a resident key.</summary>
    /// <remarks>Expire-after-access reports time since the recorded, potentially coalesced access.</remarks>
    TimeSpan? AgeOf(TKey key);
}
