using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>
/// An asynchronous, bounded cache whose values are populated by a fixed loader.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[PublicAPI]
public interface IAsyncLoadingCache<TKey, TValue> : IAsyncCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// Gets a value, loading it when the current entry is absent or expired.
    /// </summary>
    /// <param name="key">The key to load.</param>
    /// <param name="cancellationToken">The token for this caller's wait only.</param>
    /// <returns>The cached or loaded value.</returns>
    ValueTask<TValue> GetAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>Reloads or loads a key and observes the shared refresh result.</summary>
    ValueTask<TValue> RefreshAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets and loads a sequence of keys.</summary>
    ValueTask<IReadOnlyDictionary<TKey, TValue>> GetAllAsync(
        IEnumerable<TKey> keys,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Attempts to get a currently resident, non-expired value without loading.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="value">The value when the lookup succeeds.</param>
    /// <returns><see langword="true"/> when a resident value was found.</returns>
    bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value);

    /// <summary>
    /// Replaces the current value for a key.
    /// </summary>
    /// <param name="key">The key to update.</param>
    /// <param name="value">The value to store.</param>
#pragma warning disable CA1716 // The public cache contract intentionally exposes the Set operation.
    void Set(TKey key, TValue value);
#pragma warning restore CA1716

    /// <summary>
    /// Gets a point-in-time statistics snapshot.
    /// </summary>
    /// <returns>A statistics snapshot.</returns>
    CacheStatistics GetStatistics();
}

/// <summary>
/// Configuration for an asynchronous loading cache.
/// </summary>
public sealed class LoadingCacheOptions
{
    /// <summary>
    /// Gets or sets the maximum number of resident values. This must be positive.
    /// </summary>
    [PublicAPI]
    public int MaximumSize { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of load flights, including retired epochs.
    /// This must be positive.
    /// </summary>
    [PublicAPI]
    public int MaxConcurrentLoads { get; init; }

    /// <summary>
    /// Gets or sets the duration from a successful publication until the value expires.
    /// </summary>
    [PublicAPI]
    public TimeSpan? ExpireAfterWrite { get; init; }

    /// <summary>
    /// Gets or sets the duration from the last successful access until the value expires.
    /// </summary>
    [PublicAPI]
    public TimeSpan? ExpireAfterAccess { get; init; }

    /// <summary>
    /// Gets or sets the duration from a successful publication until the value becomes
    /// eligible for request-triggered refresh.
    /// </summary>
    [PublicAPI]
    public TimeSpan? RefreshAfterWrite { get; init; }

    /// <summary>
    /// Gets or sets the cache-owned deadline for a load or refresh flight.
    /// </summary>
    [PublicAPI]
    public TimeSpan? LoadTimeout { get; init; }

    /// <summary>Gets or sets the retry backoff after an automatic refresh failure.</summary>
    [PublicAPI]
    public TimeSpan RefreshFailureBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets whether one cache-owned expiration timer is enabled.</summary>
    [PublicAPI]
    public bool EnableExpirationScheduler { get; init; }

    /// <summary>
    /// Gets or sets the time provider used for expiration checks.
    /// </summary>
    [PublicAPI]
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets whether cache operation counters are recorded.
    /// </summary>
    [PublicAPI]
    public bool RecordStatistics { get; init; }

    internal LoadingCacheTestHooks? TestHooks { get; init; }
}

/// <summary>
/// A point-in-time cache statistics snapshot.
/// </summary>
[PublicAPI]
public readonly struct CacheStatistics
{
    internal CacheStatistics(
        long hits,
        long misses,
        long loadsStarted,
        long loadSuccesses,
        long loadFailures,
        long loadCancellations,
        long loadTimeouts,
        long coalescedWaiters,
        long evictions,
        long inFlightLoads,
        long totalLoadTimeTicks,
        long bulkLoads,
        long refreshAttempts,
        long refreshSuccesses,
        long refreshFailures,
        long refreshSkipped,
        long refreshBackoff,
        long loadRejections,
        long evictedWeight,
        long collected,
        long explicitRemovals,
        long replacedRemovals,
        long expiredRemovals,
        long clearedRemovals,
        long sizeRemovals,
        long weightRemovals,
        long memoryPressureRemovals,
        long listenerDrops,
        long listenerFailures,
        long maintenanceBacklog,
        long droppedReadEvents,
        long maintenanceScheduleRejections,
        long maintenanceFaults,
        long writeBufferBacklog,
        long writeBufferPressure
    )
    {
        Hits = hits;
        Misses = misses;
        LoadsStarted = loadsStarted;
        LoadSuccesses = loadSuccesses;
        LoadFailures = loadFailures;
        LoadCancellations = loadCancellations;
        LoadTimeouts = loadTimeouts;
        CoalescedWaiters = coalescedWaiters;
        Evictions = evictions;
        InFlightLoads = inFlightLoads;
        TotalLoadTimeTicks = totalLoadTimeTicks;
        BulkLoads = bulkLoads;
        RefreshAttempts = refreshAttempts;
        RefreshSuccesses = refreshSuccesses;
        RefreshFailures = refreshFailures;
        RefreshSkipped = refreshSkipped;
        RefreshBackoff = refreshBackoff;
        LoadRejections = loadRejections;
        EvictedWeight = evictedWeight;
        Collected = collected;
        ExplicitRemovals = explicitRemovals;
        ReplacedRemovals = replacedRemovals;
        ExpiredRemovals = expiredRemovals;
        ClearedRemovals = clearedRemovals;
        SizeRemovals = sizeRemovals;
        WeightRemovals = weightRemovals;
        MemoryPressureRemovals = memoryPressureRemovals;
        ListenerDrops = listenerDrops;
        ListenerFailures = listenerFailures;
        MaintenanceBacklog = maintenanceBacklog;
        DroppedReadEvents = droppedReadEvents;
        MaintenanceScheduleRejections = maintenanceScheduleRejections;
        MaintenanceFaults = maintenanceFaults;
        WriteBufferBacklog = writeBufferBacklog;
        WriteBufferPressure = writeBufferPressure;
    }

    /// <summary>Gets the number of resident hits.</summary>
    public long Hits { get; }

    /// <summary>Gets the number of cache misses, including joined flights.</summary>
    public long Misses { get; }

    /// <summary>Gets the number of loader invocations started.</summary>
    public long LoadsStarted { get; }

    /// <summary>Gets the number of loader invocations that completed successfully.</summary>
    public long LoadSuccesses { get; }

    /// <summary>Gets the number of loader invocations that failed.</summary>
    public long LoadFailures { get; }

    /// <summary>
    /// Gets the number of loader invocations that reached a cancellation terminal outcome.
    /// Cache-owned timeouts are counted only by <see cref="LoadTimeouts" />, even when the
    /// cooperative cancellation signal is accepted by the loader.
    /// </summary>
    public long LoadCancellations { get; }

    /// <summary>
    /// Gets the number of load flights terminalized by their cache-owned deadline. This outcome is
    /// mutually exclusive with load success, failure, and cancellation counters; a
    /// non-cooperative loader may still be executing after this counter is recorded.
    /// </summary>
    public long LoadTimeouts { get; }

    /// <summary>Gets the number of callers that joined an existing flight.</summary>
    public long CoalescedWaiters { get; }

    /// <summary>Gets the number of Size, Weight, and MemoryPressure removals.</summary>
    public long Evictions { get; }

    /// <summary>Gets the number of loader invocations still executing.</summary>
    public long InFlightLoads { get; }

    /// <summary>Gets the aggregate loader duration in <see cref="TimeSpan.Ticks"/>.</summary>
    public long TotalLoadTimeTicks { get; }

    /// <summary>Gets the aggregate loader duration.</summary>
    public TimeSpan TotalLoadTime => TimeSpan.FromTicks(TotalLoadTimeTicks);

    /// <summary>Gets the number of bulk loader invocations.</summary>
    public long BulkLoads { get; }

    /// <summary>Gets the number of refresh flights started.</summary>
    public long RefreshAttempts { get; }

    /// <summary>Gets the number of refresh flights that published successfully.</summary>
    public long RefreshSuccesses { get; }

    /// <summary>Gets the number of refresh flights that failed.</summary>
    public long RefreshFailures { get; }

    /// <summary>Gets the number of refresh attempts skipped by admission or backoff.</summary>
    public long RefreshSkipped { get; }

    /// <summary>Gets the number of refresh attempts suppressed by failure backoff.</summary>
    public long RefreshBackoff { get; }

    /// <summary>Gets the number of distinct-key load attempts rejected by capacity.</summary>
    public long LoadRejections { get; }

    /// <summary>Gets the cumulative weight removed by eviction policy.</summary>
    public long EvictedWeight { get; }

    /// <summary>Gets the number of entries removed after weak-reference collection.</summary>
    public long Collected { get; }

    /// <summary>Gets the number of explicitly invalidated resident values.</summary>
    public long ExplicitRemovals { get; }

    /// <summary>Gets the number of resident values replaced by a newer value.</summary>
    public long ReplacedRemovals { get; }

    /// <summary>Gets the number of resident values removed by expiration.</summary>
    public long ExpiredRemovals { get; }

    /// <summary>Gets the number of resident values removed by Clear.</summary>
    public long ClearedRemovals { get; }

    /// <summary>Gets the number of resident values removed by the size bound.</summary>
    public long SizeRemovals { get; }

    /// <summary>Gets the number of resident values removed by the weight bound.</summary>
    public long WeightRemovals { get; }

    /// <summary>Gets the number of resident values removed by memory pressure.</summary>
    public long MemoryPressureRemovals { get; }

    /// <summary>Gets the number of notifications dropped by bounded dispatch.</summary>
    public long ListenerDrops { get; }

    /// <summary>Gets the number of listener callback or scheduler failures.</summary>
    public long ListenerFailures { get; }

    /// <summary>Gets the current number of policy read and write events awaiting maintenance.</summary>
    public long MaintenanceBacklog { get; }

    /// <summary>Gets the number of policy read events dropped by the bounded transport.</summary>
    public long DroppedReadEvents { get; }

    /// <summary>Gets the number of rejected policy maintenance schedules.</summary>
    public long MaintenanceScheduleRejections { get; }

    /// <summary>Gets the number of policy maintenance drain and load-timer disposal faults.</summary>
    public long MaintenanceFaults { get; }

    /// <summary>Gets the current number of deferred, reliable policy write events.</summary>
    public long WriteBufferBacklog { get; }

    /// <summary>
    /// Gets the number of full write-buffer encounters that required producer assistance.
    /// This saturating counter is zero when statistics recording is disabled.
    /// </summary>
    public long WriteBufferPressure { get; }
}

/// <summary>
/// Indicates that a new distinct-key load was rejected by the configured load bound.
/// </summary>
public sealed class CacheLoadRejectedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new exception for a saturated loading cache.
    /// </summary>
    public CacheLoadRejectedException()
        : base("The cache has reached its maximum number of concurrent loads.") { }
}

/// <summary>
/// Indicates that a loader attempted to await a flight in its own logical load chain.
/// </summary>
public sealed class LoadingCacheReentrancyException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new exception for a cyclic logical load chain.
    /// </summary>
    /// <param name="message">The diagnostic message.</param>
    public LoadingCacheReentrancyException(string message)
        : base(message) { }
}

/// <summary>
/// Entry point for creating loading caches.
/// </summary>
public static class LoadingCache
{
    /// <summary>
    /// Creates an asynchronous loading cache.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <param name="loader">The fixed loader invoked for a distinct flight.</param>
    /// <param name="options">The cache options.</param>
    /// <param name="comparer">The comparer used for all key operations.</param>
    /// <returns>A configured cache.</returns>
    public static IAsyncLoadingCache<TKey, TValue> Create<TKey, TValue>(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingCacheOptions options,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(options);
        return new LoadingCacheImpl<TKey, TValue>(loader, options, comparer);
    }
}
