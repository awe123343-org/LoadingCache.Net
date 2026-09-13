using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>Loads and reloads values for a synchronous loading cache.</summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface ISyncCacheLoader<in TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Loads a value for a missing key.</summary>
    TValue Load(TKey key);

    /// <summary>Reloads a current value. The default delegates to <see cref="Load"/>.</summary>
    TValue Reload(TKey key, TValue oldValue) => Load(key);
}

/// <summary>
/// Adds an optional true bulk operation to a synchronous cache loader.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface IBulkSyncCacheLoader<TKey, TValue> : ISyncCacheLoader<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Loads the requested keys in one backend operation. The result may also
    /// contain prefetched keys; the cache validates and admits those keys
    /// independently of the requested result.
    /// </summary>
    /// <param name="keys">A comparer-unique, bounded key snapshot.</param>
    /// <returns>The loaded and optionally prefetched values.</returns>
    IReadOnlyDictionary<TKey, TValue> LoadAll(IReadOnlyCollection<TKey> keys);
}

/// <summary>Loads and reloads values for an asynchronous loading cache.</summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface IAsyncCacheLoader<in TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Loads a value for a missing key.</summary>
    Task<TValue> LoadAsync(TKey key, CancellationToken cancellationToken);

    /// <summary>Reloads a current value. The default delegates to <see cref="LoadAsync"/>.</summary>
    Task<TValue> ReloadAsync(TKey key, TValue oldValue, CancellationToken cancellationToken) =>
        LoadAsync(key, cancellationToken);
}

/// <summary>
/// Adds an optional true bulk operation to an asynchronous cache loader.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public interface IBulkAsyncCacheLoader<TKey, TValue> : IAsyncCacheLoader<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Loads the requested keys in one backend operation. The result may also
    /// contain prefetched keys; the cache validates and admits those keys
    /// independently of the requested result.
    /// </summary>
    /// <param name="keys">A comparer-unique, bounded key snapshot.</param>
    /// <param name="cancellationToken">The cache-owned loader token.</param>
    /// <returns>The loaded and optionally prefetched values.</returns>
    Task<IReadOnlyDictionary<TKey, TValue>> LoadAllAsync(
        IReadOnlyCollection<TKey> keys,
        CancellationToken cancellationToken
    );
}

internal sealed class DelegateSyncCacheLoader<TKey, TValue> : ISyncCacheLoader<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Func<TKey, TValue> _load;
    private readonly Func<TKey, TValue, TValue>? _reload;

    internal DelegateSyncCacheLoader(
        Func<TKey, TValue> load,
        Func<TKey, TValue, TValue>? reload = null
    )
    {
        _load = load;
        _reload = reload;
    }

    public TValue Load(TKey key) => _load(key);

    public TValue Reload(TKey key, TValue oldValue) =>
        _reload is null ? _load(key) : _reload(key, oldValue);
}

internal sealed class DelegateAsyncCacheLoader<TKey, TValue> : IAsyncCacheLoader<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Func<TKey, CancellationToken, Task<TValue>> _load;
    private readonly Func<TKey, TValue, CancellationToken, Task<TValue>>? _reload;

    internal DelegateAsyncCacheLoader(
        Func<TKey, CancellationToken, Task<TValue>> load,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reload = null
    )
    {
        _load = load;
        _reload = reload;
    }

    public Task<TValue> LoadAsync(TKey key, CancellationToken cancellationToken) =>
        _load(key, cancellationToken);

    public Task<TValue> ReloadAsync(
        TKey key,
        TValue oldValue,
        CancellationToken cancellationToken
    ) =>
        _reload is null ? _load(key, cancellationToken) : _reload(key, oldValue, cancellationToken);
}
