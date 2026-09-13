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
[PublicAPI]
public interface IFixedExpirationPolicy<in TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Gets the configured expiration duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Gets the remaining duration for a resident key.</summary>
    TimeSpan? GetExpiresAfter(TKey key);

    /// <summary>Changes the configured duration for subsequent freshness checks.</summary>
    void SetDuration(TimeSpan duration);

    /// <summary>Alias for <see cref="SetDuration"/>.</summary>
    void SetExpiresAfter(TimeSpan duration);

    /// <summary>Gets the age of a resident key.</summary>
    TimeSpan? AgeOf(TKey key);
}
