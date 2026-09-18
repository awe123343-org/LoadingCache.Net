using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>Common operations for a synchronous cache.</summary>
[PublicAPI]
public interface ICache<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Attempts to read a resident value.</summary>
    bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value);

    /// <summary>Returns the existing value or computes and stores one.</summary>
    TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory);

    /// <summary>Returns all currently resident values for the supplied keys.</summary>
    IReadOnlyDictionary<TKey, TValue> GetAllPresent(IEnumerable<TKey> keys);

    /// <summary>Stores a value for a key.</summary>
    void Put(TKey key, TValue value);

    /// <summary>Stores a value with an explicit variable expiration duration.</summary>
    void Put(TKey key, TValue value, TimeSpan duration);

    /// <summary>Stores a snapshot of key/value pairs.</summary>
    void PutAll(IEnumerable<KeyValuePair<TKey, TValue>> values);

    /// <summary>Removes the current value for a key.</summary>
    bool Invalidate(TKey key);

    /// <summary>Removes the current values for a snapshot of keys.</summary>
    int Invalidate(IEnumerable<TKey> keys);

    /// <summary>Removes all current values and advances the epoch.</summary>
    void Clear();

    /// <summary>Performs available expiration and policy maintenance.</summary>
    void CleanUp();

    /// <summary>Gets the approximate resident value count.</summary>
    long EstimatedCount { get; }

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    CacheStatistics Statistics { get; }

    /// <summary>
    /// Gets a point-in-time snapshot of the bounded listener dispatcher. This remains available
    /// after disposal so shutdown drops can be observed.
    /// </summary>
    CacheNotificationStatistics GetNotificationStatistics();

    /// <summary>Gets the read-only policy view.</summary>
    ICachePolicy<TKey, TValue> Policy { get; }

    /// <summary>
    /// Gets a mutable cache-aware dictionary view. Reads expose only ready,
    /// non-expired values and never invoke a loader.
    /// </summary>
    SyncCacheDictionary<TKey, TValue> AsDictionary();
}

/// <summary>Common operations for a synchronous loading cache.</summary>
[PublicAPI]
public interface ILoadingCache<TKey, TValue> : ICache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Gets a value, computing it with the fixed loader when absent.</summary>
#pragma warning disable CA1716 // The LoadingCache API intentionally uses Get as its primary operation.
    TValue Get(TKey key);
#pragma warning restore CA1716

    /// <summary>Gets all requested values with the fixed loader.</summary>
    IReadOnlyDictionary<TKey, TValue> GetAll(IEnumerable<TKey> keys);

    /// <summary>Reloads or loads a key and observes the shared refresh result.</summary>
    Task<TValue> RefreshAsync(TKey key, CancellationToken cancellationToken = default);
}

/// <summary>Common operations for a manually populated asynchronous cache.</summary>
[PublicAPI]
public interface IAsyncCache<TKey, TValue> : IAsyncDisposable
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>Attempts to obtain a resident value or shared flight task.</summary>
    bool TryGetTask(TKey key, [NotNullWhen(true)] out Task<TValue>? valueTask);

    /// <summary>Gets or computes a value using a shared asynchronous flight.</summary>
    ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> valueFactory,
        CancellationToken cancellationToken = default
    );

    /// <summary>Publishes a task result when it completes.</summary>
    void Put(TKey key, Task<TValue> valueTask);

    /// <summary>Publishes a value with an explicit variable expiration duration.</summary>
    void Put(TKey key, TValue value, TimeSpan duration);

    /// <summary>Removes the current value or flight for a key.</summary>
    bool Invalidate(TKey key);

    /// <summary>Removes all values and advances the epoch.</summary>
    void Clear();

    /// <summary>Performs available expiration and policy maintenance.</summary>
    void CleanUp();

    /// <summary>Gets the approximate resident value count.</summary>
    long EstimatedCount { get; }

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    CacheStatistics Statistics { get; }

    /// <summary>
    /// Gets a point-in-time snapshot of the bounded listener dispatcher. This remains available
    /// after disposal so shutdown drops can be observed.
    /// </summary>
    CacheNotificationStatistics GetNotificationStatistics();

    /// <summary>Gets the read-only policy view.</summary>
    ICachePolicy<TKey, TValue> Policy { get; }

    /// <summary>
    /// Gets a value-materialized mutable dictionary view. Reads never block on
    /// asynchronous flights; pending values are absent from the view.
    /// </summary>
    AsyncCacheDictionary<TKey, TValue> AsDictionary();
}

/// <summary>A manually populated synchronous cache.</summary>
public class Cache<TKey, TValue> : ICache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private protected readonly CacheEngine<TKey, TValue> Engine;

    internal Cache(CacheEngine<TKey, TValue> engine)
    {
        Engine = engine;
    }

    /// <summary>Attempts to read a resident value.</summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        Engine.TryGet(key, out value);

    /// <summary>Returns the existing value or computes and stores one.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory) =>
        Engine.GetOrAdd(key, valueFactory);

    /// <summary>Returns all currently resident values for the supplied keys.</summary>
    public IReadOnlyDictionary<TKey, TValue> GetAllPresent(IEnumerable<TKey> keys) =>
        Engine.GetAllPresent(keys);

    /// <summary>Stores a value for a key.</summary>
    public void Put(TKey key, TValue value) => Engine.Put(key, value);

    /// <summary>Stores a value with an explicit variable expiration duration.</summary>
    [PublicAPI]
    public void Put(TKey key, TValue value, TimeSpan duration) => Engine.Put(key, value, duration);

    /// <summary>Stores a snapshot of key/value pairs.</summary>
    public void PutAll(IEnumerable<KeyValuePair<TKey, TValue>> values) => Engine.PutAll(values);

    /// <summary>Removes the current value for a key.</summary>
    public bool Invalidate(TKey key) => Engine.Invalidate(key);

    /// <summary>Removes the current values for a snapshot of keys.</summary>
    public int Invalidate(IEnumerable<TKey> keys) => Engine.Invalidate(keys);

    /// <summary>Removes all current values and advances the epoch.</summary>
    public void Clear() => Engine.Clear();

    /// <summary>Performs available expiration and policy maintenance.</summary>
    public void CleanUp() => Engine.CleanUp();

    /// <summary>Gets the approximate resident value count.</summary>
    public long EstimatedCount => Engine.EstimatedCount;

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    public CacheStatistics Statistics => Engine.GetStatistics();

    /// <summary>Gets a point-in-time snapshot of the bounded listener dispatcher.</summary>
    public CacheNotificationStatistics GetNotificationStatistics() =>
        Engine.GetNotificationStatistics();

    /// <summary>Gets the read-only policy view.</summary>
    public ICachePolicy<TKey, TValue> Policy => Engine.Policy;

    /// <summary>Gets a mutable cache-aware dictionary view.</summary>
    public SyncCacheDictionary<TKey, TValue> AsDictionary() => new(Engine);

    /// <summary>Releases cache-owned resources without waiting for user code.</summary>
    public void Dispose()
    {
        Engine.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void AssertInvariants() => Engine.AssertInvariants();
}

/// <summary>A synchronous cache populated by a fixed value factory.</summary>
public sealed class LoadingCache<TKey, TValue> : Cache<TKey, TValue>, ILoadingCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Func<TKey, TValue> _loader;
    private readonly Func<TKey, TValue, TValue> _reload;
    private readonly Func<
        IReadOnlyCollection<TKey>,
        IReadOnlyDictionary<TKey, TValue>
    >? _bulkLoader;

    internal LoadingCache(
        CacheEngine<TKey, TValue> engine,
        Func<TKey, TValue> loader,
        Func<TKey, TValue, TValue>? reload = null,
        Func<IReadOnlyCollection<TKey>, IReadOnlyDictionary<TKey, TValue>>? bulkLoader = null
    )
        : base(engine)
    {
        _loader = loader;
        _reload = reload ?? Reload;
        _bulkLoader = bulkLoader;
    }

    private TValue Reload(TKey key, TValue _) => _loader(key);

    /// <summary>Gets a value, invoking the fixed synchronous loader when absent.</summary>
    public TValue Get(TKey key) => Engine.GetOrAdd(key, _loader, _reload);

    /// <summary>Gets all requested values with the fixed loader.</summary>
    public IReadOnlyDictionary<TKey, TValue> GetAll(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Engine.GetAll(keys, _loader, _reload, _bulkLoader);
    }

    /// <summary>Reloads or loads a key without converting an async loader to sync code.</summary>
    public Task<TValue> RefreshAsync(TKey key, CancellationToken cancellationToken = default) =>
        Engine.RefreshSyncAsync(key, _loader, _reload, cancellationToken);
}

/// <summary>A manually populated asynchronous cache.</summary>
public class AsyncCache<TKey, TValue> : IAsyncCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly CacheEngine<TKey, TValue> _engine;

    internal AsyncCache(CacheEngine<TKey, TValue> engine)
    {
        _engine = engine;
    }

    /// <summary>Attempts to obtain a resident value or shared flight task.</summary>
    public bool TryGetTask(TKey key, [NotNullWhen(true)] out Task<TValue>? valueTask) =>
        _engine.TryGetTask(key, out valueTask);

    /// <summary>Gets or computes a value using a shared asynchronous flight.</summary>
    public ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> valueFactory,
        CancellationToken cancellationToken = default
    ) => _engine.GetOrAddAsync(key, valueFactory, cancellationToken);

    /// <summary>Publishes a task result when it completes.</summary>
    public void Put(TKey key, Task<TValue> valueTask) => _engine.PutTask(key, valueTask);

    /// <summary>Publishes a value with an explicit variable expiration duration.</summary>
    [PublicAPI]
    public void Put(TKey key, TValue value, TimeSpan duration) => _engine.Put(key, value, duration);

    /// <summary>Removes the current value or flight for a key.</summary>
    public bool Invalidate(TKey key) => _engine.Invalidate(key);

    /// <summary>Removes all values and advances the epoch.</summary>
    public void Clear() => _engine.Clear();

    /// <summary>Performs available expiration and policy maintenance.</summary>
    public void CleanUp() => _engine.CleanUp();

    /// <summary>Gets the approximate resident value count.</summary>
    public long EstimatedCount => _engine.EstimatedCount;

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    public CacheStatistics Statistics => _engine.GetStatistics();

    /// <summary>Gets a point-in-time snapshot of the bounded listener dispatcher.</summary>
    public CacheNotificationStatistics GetNotificationStatistics() =>
        _engine.GetNotificationStatistics();

    /// <summary>Gets the read-only policy view.</summary>
    public ICachePolicy<TKey, TValue> Policy => _engine.Policy;

    /// <summary>Gets a value-materialized mutable dictionary view.</summary>
    public AsyncCacheDictionary<TKey, TValue> AsDictionary() => new(_engine);

    /// <summary>Releases cache-owned resources without waiting for user code.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return _engine.DisposeAsync();
    }

    internal void AssertInvariants() => _engine.AssertInvariants();
}

/// <summary>An asynchronous cache populated by a fixed loader.</summary>
public class AsyncLoadingCache<TKey, TValue>
    : AsyncCache<TKey, TValue>,
        IAsyncLoadingCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly CacheEngine<TKey, TValue> _engine;
    private readonly Func<TKey, CancellationToken, Task<TValue>> _loader;
    private readonly Func<TKey, TValue, CancellationToken, Task<TValue>> _reload;
    private readonly Func<
        IReadOnlyCollection<TKey>,
        CancellationToken,
        Task<IReadOnlyDictionary<TKey, TValue>>
    >? _bulkLoader;

    internal AsyncLoadingCache(
        CacheEngine<TKey, TValue> engine,
        Func<TKey, CancellationToken, Task<TValue>> loader,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reload = null,
        Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        >? bulkLoader = null
    )
        : base(engine)
    {
        _engine = engine;
        _loader = loader;
        _reload = reload ?? ReloadAsync;
        _bulkLoader = bulkLoader;
    }

    private Task<TValue> ReloadAsync(TKey key, TValue _, CancellationToken cancellationToken) =>
        _loader(key, cancellationToken);

    /// <summary>Gets a value, invoking the fixed asynchronous loader when absent.</summary>
    public ValueTask<TValue> GetAsync(TKey key, CancellationToken cancellationToken = default) =>
        _engine.GetAsync(key, _loader, _reload, cancellationToken);

    /// <summary>Reloads or loads a key and observes the shared refresh result.</summary>
    public ValueTask<TValue> RefreshAsync(
        TKey key,
        CancellationToken cancellationToken = default
    ) => _engine.RefreshAsync(key, _loader, _reload, cancellationToken);

    /// <summary>Attempts to read a resident value without loading.</summary>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        _engine.TryGet(key, out value);

    /// <summary>Replaces the current value for a key.</summary>
    public void Set(TKey key, TValue value) => _engine.Put(key, value);

    /// <summary>Replaces the current value with an explicit expiration duration.</summary>
    [PublicAPI]
    public void Set(TKey key, TValue value, TimeSpan duration) => _engine.Put(key, value, duration);

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    public CacheStatistics GetStatistics() => Statistics;

    /// <summary>Gets and loads a sequence of keys.</summary>
    public ValueTask<IReadOnlyDictionary<TKey, TValue>> GetAllAsync(
        IEnumerable<TKey> keys,
        CancellationToken cancellationToken = default
    ) => _engine.GetAllAsync(_loader, _reload, _bulkLoader, keys, cancellationToken);
}
