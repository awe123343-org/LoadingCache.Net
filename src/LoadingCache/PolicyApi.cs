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
