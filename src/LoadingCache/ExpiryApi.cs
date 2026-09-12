using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>
/// Calculates the next expiration duration for a variable-expiration cache.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface IExpiry<in TKey, in TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Calculates the duration after a value is created.
    /// </summary>
    TimeSpan ExpireAfterCreate(TKey key, TValue value, TimeSpan currentDuration);

    /// <summary>
    /// Calculates the duration after a value is replaced.
    /// </summary>
    TimeSpan ExpireAfterUpdate(TKey key, TValue value, TimeSpan currentDuration);

    /// <summary>
    /// Calculates the duration after a successful read.
    /// </summary>
    TimeSpan ExpireAfterRead(TKey key, TValue value, TimeSpan currentDuration);
}

/// <summary>
/// Inspects and mutates the variable expiration of resident entries.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface IVariableExpirationPolicy<in TKey, in TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Gets the remaining duration for a current resident entry.
    /// </summary>
    TimeSpan? GetExpiresAfter(TKey key);

    /// <summary>
    /// Replaces the remaining duration for a current resident entry.
    /// </summary>
    bool SetExpiresAfter(TKey key, TimeSpan duration);

    /// <summary>
    /// Gets the age of a current resident entry.
    /// </summary>
    TimeSpan? AgeOf(TKey key);

    /// <summary>
    /// Publishes a value with an explicit variable expiration duration.
    /// </summary>
    void Put(TKey key, TValue value, TimeSpan duration);
}
