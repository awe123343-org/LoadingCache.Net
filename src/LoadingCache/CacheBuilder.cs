using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>Entry point for the typed cache builder.</summary>
public static class CacheBuilder
{
    /// <summary>Creates a builder for the specified key and value types.</summary>
    public static CacheBuilder<TKey, TValue> Create<TKey, TValue>()
        where TKey : notnull
        where TValue : notnull => new();
}

/// <summary>
/// Fluent configuration shared by the synchronous and asynchronous cache
/// personalities.
/// </summary>
public sealed class CacheBuilder<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private int? _maximumSize;
    private long? _maximumWeight;
    private int? _maximumResidentCount;
    private Func<TKey, TValue, long>? _weigher;
    private int _maxConcurrentLoads;
    private int? _maxPendingLoadKeys;
    private int? _maximumBulkKeys;
    private bool _weakKeys;
    private bool _weakValues;
    private bool _hasCustomComparer;
    private Action<RemovalNotification<TKey, TValue>>? _removalListener;
    private Action<RemovalNotification<TKey, TValue>>? _evictionListener;
    private int _notificationCapacity = 1024;
    private bool _enableMetrics;
    private string? _metricsName;
    private TimeSpan? _expireAfterWrite;
    private TimeSpan? _expireAfterAccess;
    private TimeSpan? _refreshAfterWrite;
    private TimeSpan? _loadTimeout;
    private TimeSpan _refreshFailureBackoff = TimeSpan.FromSeconds(1);
    private IExpiry<TKey, TValue>? _expiry;
    private TimeProvider _timeProvider = System.TimeProvider.System;
    private bool _recordStatistics;
    private bool _enableExpirationScheduler;
    private TimeSpan? _memoryPressureSamplingInterval;
    private double _memoryPressureThreshold = 0.9;
    private double _memoryPressureTrimFraction = 0.1;
    private int _memoryPressureMaximumTrimCount = 256;
    private IMemoryPressureSource _memoryPressureSource = GcMemoryPressureSource.Instance;
    private IEqualityComparer<TKey> _comparer = EqualityComparer<TKey>.Default;

    /// <summary>Sets the maximum resident entry count.</summary>
    public CacheBuilder<TKey, TValue> MaximumSize(int maximumSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSize);
        _maximumSize = maximumSize;
        return this;
    }

    /// <summary>Sets the maximum weighted capacity.</summary>
    public CacheBuilder<TKey, TValue> MaximumWeight(long maximumWeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWeight);
        _maximumWeight = maximumWeight;
        return this;
    }

    /// <summary>
    /// Sets a separate resident count bound for a weighted cache.  This bound
    /// prevents zero-weight values from making metadata unbounded.
    /// </summary>
    public CacheBuilder<TKey, TValue> MaximumResidentCount(int maximumResidentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResidentCount);
        _maximumResidentCount = maximumResidentCount;
        return this;
    }

    /// <summary>Sets the user callback used to calculate a value weight.</summary>
    public CacheBuilder<TKey, TValue> Weigher(Func<TKey, TValue, long> weigher)
    {
        ArgumentNullException.ThrowIfNull(weigher);
        _weigher = weigher;
        return this;
    }

    /// <summary>Sets the maximum number of distinct load flights.</summary>
    public CacheBuilder<TKey, TValue> MaxConcurrentLoads(int maximumConcurrentLoads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentLoads);
        _maxConcurrentLoads = maximumConcurrentLoads;
        return this;
    }

    /// <summary>Bounds pending key generations, including retired loads still running.</summary>
    public CacheBuilder<TKey, TValue> MaxPendingLoadKeys(int maximumPendingLoadKeys)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingLoadKeys);
        _maxPendingLoadKeys = maximumPendingLoadKeys;
        return this;
    }

    /// <summary>Bounds enumerated input and output entries in a bulk operation.</summary>
    public CacheBuilder<TKey, TValue> MaximumBulkKeys(int maximumBulkKeys)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBulkKeys);
        _maximumBulkKeys = maximumBulkKeys;
        return this;
    }

    /// <summary>Stores reference-type keys weakly and compares keys by object identity.</summary>
    /// <remarks>Cannot be combined with an explicitly configured key comparer.</remarks>
    public CacheBuilder<TKey, TValue> WeakKeys()
    {
        if (typeof(TKey).IsValueType)
        {
            throw new InvalidOperationException("Weak keys require a reference key type.");
        }
        _weakKeys = true;
        return this;
    }

    /// <summary>Stores reference-type values weakly in a synchronous cache.</summary>
    /// <remarks>Asynchronous caches retain shared tasks and cannot use weak values.</remarks>
    public CacheBuilder<TKey, TValue> WeakValues()
    {
        if (typeof(TValue).IsValueType)
        {
            throw new InvalidOperationException("Weak values require a reference value type.");
        }
        _weakValues = true;
        return this;
    }

    /// <summary>Enables expire-after-write.</summary>
    public CacheBuilder<TKey, TValue> ExpireAfterWrite(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        _expireAfterWrite = duration;
        return this;
    }

    /// <summary>Enables expire-after-access.</summary>
    public CacheBuilder<TKey, TValue> ExpireAfterAccess(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        _expireAfterAccess = duration;
        return this;
    }

    /// <summary>Enables request-triggered refresh after a successful publication.</summary>
    public CacheBuilder<TKey, TValue> RefreshAfterWrite(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        _refreshAfterWrite = duration;
        return this;
    }

    /// <summary>Sets the cache-owned load and refresh flight deadline.</summary>
    public CacheBuilder<TKey, TValue> LoadTimeout(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        _loadTimeout = duration;
        return this;
    }

    /// <summary>Sets the automatic refresh failure retry backoff.</summary>
    public CacheBuilder<TKey, TValue> RefreshFailureBackoff(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        _refreshFailureBackoff = duration;
        return this;
    }

    /// <summary>Enables variable expiration callbacks.</summary>
    public CacheBuilder<TKey, TValue> ExpireAfter(IExpiry<TKey, TValue> expiry)
    {
        ArgumentNullException.ThrowIfNull(expiry);
        _expiry = expiry;
        return this;
    }

    /// <summary>
    /// Enables one cache-owned prompt expiration timer backed by the configured
    /// <see cref="System.TimeProvider"/>.
    /// </summary>
    public CacheBuilder<TKey, TValue> EnableExpirationScheduler()
    {
        _enableExpirationScheduler = true;
        return this;
    }

    /// <summary>
    /// Enables opt-in memory-pressure eviction driven by a cache-owned timer.
    /// The trim is a bounded count-based eviction in approximate cold policy
    /// order; it is not a JVM <c>SoftReference</c> view.
    /// </summary>
    /// <param name="samplingInterval">The positive interval between samples.</param>
    /// <param name="pressureThreshold">
    /// The normalized load ratio at which trimming starts, from greater than
    /// zero through one.
    /// </param>
    /// <param name="trimFraction">The resident fraction to trim per sample.</param>
    /// <param name="maximumTrimCount">The maximum entries removed by one sample.</param>
    public CacheBuilder<TKey, TValue> MemoryPressureEviction(
        TimeSpan samplingInterval,
        double pressureThreshold = 0.9,
        double trimFraction = 0.1,
        int maximumTrimCount = 256
    )
    {
        ValidateMemoryPressureOptions(
            samplingInterval,
            pressureThreshold,
            trimFraction,
            maximumTrimCount
        );
        _memoryPressureSamplingInterval = samplingInterval;
        _memoryPressureThreshold = pressureThreshold;
        _memoryPressureTrimFraction = trimFraction;
        _memoryPressureMaximumTrimCount = maximumTrimCount;
        return this;
    }

    /// <summary>Sets the source used by the memory-pressure policy.</summary>
    /// <remarks>
    /// The source is called outside cache locks and must not force a collection.
    /// </remarks>
    public CacheBuilder<TKey, TValue> MemoryPressureSource(IMemoryPressureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _memoryPressureSource = source;
        return this;
    }

    /// <summary>Sets the time provider used by expiration checks.</summary>
    public CacheBuilder<TKey, TValue> TimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        return this;
    }

    /// <summary>Sets the comparer used for every key operation.</summary>
    public CacheBuilder<TKey, TValue> Comparer(IEqualityComparer<TKey> comparer)
    {
        ArgumentNullException.ThrowIfNull(comparer);
        _comparer = comparer;
        _hasCustomComparer = true;
        return this;
    }

    /// <summary>Enables cache statistics.</summary>
    public CacheBuilder<TKey, TValue> RecordStatistics()
    {
        _recordStatistics = true;
        return this;
    }

    /// <summary>Registers a bounded asynchronous notification for every removal cause.</summary>
    /// <remarks>Runs outside cache locks. Notifications may be dropped under pressure or shutdown.</remarks>
    public CacheBuilder<TKey, TValue> RemovalListener(
        Action<RemovalNotification<TKey, TValue>> listener
    )
    {
        ArgumentNullException.ThrowIfNull(listener);
        _removalListener = listener;
        return this;
    }

    /// <summary>Registers a reliable callback for policy-driven removals.</summary>
    /// <remarks>
    /// The callback runs synchronously after the cache has detached the entry and released its
    /// internal locks, but before the operation that caused the eviction returns. Callback
    /// exceptions are observed in cache statistics and do not corrupt cache state. This callback
    /// is a diagnostic notification, not a value-disposal guarantee.
    /// </remarks>
    public CacheBuilder<TKey, TValue> EvictionListener(
        Action<RemovalNotification<TKey, TValue>> listener
    )
    {
        ArgumentNullException.ThrowIfNull(listener);
        _evictionListener = listener;
        return this;
    }

    /// <summary>Sets the listener queue capacity. The default is 1024 notifications.</summary>
    public CacheBuilder<TKey, TValue> NotificationCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _notificationCapacity = capacity;
        return this;
    }

    /// <summary>Enables optional System.Diagnostics.Metrics instruments for this cache.</summary>
    /// <param name="cacheName">A stable, low-cardinality name. Never use request, user or tenant identifiers.</param>
    public CacheBuilder<TKey, TValue> EnableMetrics(string cacheName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        _enableMetrics = true;
        _metricsName = cacheName;
        return this;
    }

    /// <summary>Builds a manual synchronous cache.</summary>
    public ICache<TKey, TValue> Build() =>
        new Cache<TKey, TValue>(CreateEngine(supportsBulkLoading: false));

    /// <summary>Builds a synchronous loading cache.</summary>
    public ILoadingCache<TKey, TValue> BuildLoading(Func<TKey, TValue> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        return BuildLoading(new DelegateSyncCacheLoader<TKey, TValue>(loader));
    }

    /// <summary>Builds a synchronous loading cache from a load/reload contract.</summary>
    public ILoadingCache<TKey, TValue> BuildLoading(ISyncCacheLoader<TKey, TValue> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        var bulkLoader = loader as IBulkSyncCacheLoader<TKey, TValue>;
        ValidateBulkCapability(bulkLoader is not null);
        return new LoadingCache<TKey, TValue>(
            CreateEngine(hasFixedLoader: true, supportsBulkLoading: bulkLoader is not null),
            loader.Load,
            loader.Reload,
            bulkLoader is null ? null : bulkLoader.LoadAll
        );
    }

    /// <summary>Builds a manual asynchronous cache.</summary>
    public IAsyncCache<TKey, TValue> BuildAsync() =>
        new AsyncCache<TKey, TValue>(CreateEngine(isAsync: true, supportsBulkLoading: false));

    /// <summary>Builds an asynchronous loading cache.</summary>
    public IAsyncLoadingCache<TKey, TValue> BuildAsyncLoading(
        Func<TKey, CancellationToken, Task<TValue>> loader
    )
    {
        ArgumentNullException.ThrowIfNull(loader);
        return BuildAsyncLoading(new DelegateAsyncCacheLoader<TKey, TValue>(loader));
    }

    /// <summary>Builds an asynchronous loading cache from a load/reload contract.</summary>
    public IAsyncLoadingCache<TKey, TValue> BuildAsyncLoading(
        IAsyncCacheLoader<TKey, TValue> loader
    )
    {
        ArgumentNullException.ThrowIfNull(loader);
        var bulkLoader = loader as IBulkAsyncCacheLoader<TKey, TValue>;
        ValidateBulkCapability(bulkLoader is not null);
        return new AsyncLoadingCache<TKey, TValue>(
            CreateEngine(
                hasFixedLoader: true,
                isAsync: true,
                supportsBulkLoading: bulkLoader is not null
            ),
            loader.LoadAsync,
            loader.ReloadAsync,
            bulkLoader is null ? null : bulkLoader.LoadAllAsync
        );
    }

    internal CacheEngine<TKey, TValue> CreateEngine(
        LoadingCacheTestHooks? testHooks = null,
        bool hasFixedLoader = false,
        bool isAsync = false,
        bool supportsBulkLoading = true
    )
    {
        ValidateBuild(hasFixedLoader);
        if (_weakValues && isAsync)
        {
            throw new InvalidOperationException(
                "Weak values cannot be used with asynchronous caches."
            );
        }
        return new CacheEngine<TKey, TValue>(
            new CacheEngineOptions<TKey, TValue>
            {
                MaximumSize = _maximumSize,
                MaximumWeight = _maximumWeight,
                MaximumResidentCount = _maximumResidentCount,
                Weigher = _weigher,
                MaxConcurrentLoads = _maxConcurrentLoads,
                MaxPendingLoadKeys = _maxPendingLoadKeys,
                MaximumBulkKeys = _maximumBulkKeys,
                SupportsBulkLoading = supportsBulkLoading,
                WeakKeys = _weakKeys,
                WeakValues = _weakValues,
                ExpireAfterWrite = _expireAfterWrite,
                ExpireAfterAccess = _expireAfterAccess,
                RefreshAfterWrite = _refreshAfterWrite,
                LoadTimeout = _loadTimeout,
                RefreshFailureBackoff = _refreshFailureBackoff,
                Expiry = _expiry,
                TimeProvider = _timeProvider,
                RecordStatistics = _recordStatistics,
                RemovalListener = _removalListener,
                EvictionListener = _evictionListener,
                NotificationCapacity = _notificationCapacity,
                EnableMetrics = _enableMetrics,
                MetricsName = _metricsName,
                EnableExpirationScheduler = _enableExpirationScheduler,
                MemoryPressureSamplingInterval = _memoryPressureSamplingInterval,
                MemoryPressureThreshold = _memoryPressureThreshold,
                MemoryPressureTrimFraction = _memoryPressureTrimFraction,
                MemoryPressureMaximumTrimCount = _memoryPressureMaximumTrimCount,
                MemoryPressureSource = _memoryPressureSource,
                Comparer = _weakKeys ? null : _comparer,
                TestHooks = testHooks,
            }
        );
    }

    private void ValidateBuild(bool hasFixedLoader)
    {
        if (_weakKeys && _hasCustomComparer)
        {
            throw new InvalidOperationException(
                "Weak keys use identity equality and cannot use a custom comparer."
            );
        }

        if (
            _maximumBulkKeys.HasValue
            && _maxPendingLoadKeys.HasValue
            && _maximumBulkKeys > _maxPendingLoadKeys
        )
        {
            throw new InvalidOperationException(
                "MaximumBulkKeys cannot exceed MaxPendingLoadKeys."
            );
        }

        if (_maximumSize.HasValue == _maximumWeight.HasValue)
        {
            throw new InvalidOperationException(
                "Configure exactly one of MaximumSize or MaximumWeight before Build."
            );
        }

        if (_maximumWeight.HasValue && _maximumResidentCount is null)
        {
            throw new InvalidOperationException(
                "Weighted caches require MaximumResidentCount to bound zero-weight entries."
            );
        }

        if (_maximumWeight.HasValue && _weigher is null)
        {
            throw new InvalidOperationException("Weighted caches require a Weigher callback.");
        }

        if (_maximumSize.HasValue && _weigher is not null)
        {
            throw new InvalidOperationException("Weigher is only valid with MaximumWeight.");
        }

        if (_expiry is not null && (_expireAfterWrite.HasValue || _expireAfterAccess.HasValue))
        {
            throw new InvalidOperationException(
                "Variable expiration cannot be combined with fixed expiration."
            );
        }

        if (_maxConcurrentLoads <= 0)
        {
            throw new InvalidOperationException(
                "Configure MaxConcurrentLoads with a positive value before Build."
            );
        }

        if (_refreshAfterWrite.HasValue && !hasFixedLoader)
        {
            throw new InvalidOperationException(
                "RefreshAfterWrite requires a fixed loading cache loader."
            );
        }
    }

    private void ValidateBulkCapability(bool hasBulkLoader)
    {
        if (hasBulkLoader && (!_maxPendingLoadKeys.HasValue || !_maximumBulkKeys.HasValue))
        {
            throw new InvalidOperationException(
                "Bulk loaders require explicit MaxPendingLoadKeys and MaximumBulkKeys limits."
            );
        }
    }

    private static void ValidateDuration(TimeSpan? duration, string parameterName)
    {
        if (
            duration.HasValue
            && (duration.Value <= TimeSpan.Zero || duration.Value == Timeout.InfiniteTimeSpan)
        )
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Expiration duration must be finite and positive."
            );
        }
    }

    [AssertionMethod]
    private static void ValidateMemoryPressureOptions(
        TimeSpan samplingInterval,
        double pressureThreshold,
        double trimFraction,
        int maximumTrimCount
    )
    {
        if (
            samplingInterval <= TimeSpan.Zero
            || samplingInterval == Timeout.InfiniteTimeSpan
            || samplingInterval > TimeSpan.FromMilliseconds(uint.MaxValue - 1d)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingInterval),
                "The memory-pressure sampling interval must be positive and timer-compatible."
            );
        }

        if (!double.IsFinite(pressureThreshold) || pressureThreshold <= 0 || pressureThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pressureThreshold),
                "The memory-pressure threshold must be finite and in the (0, 1] range."
            );
        }

        if (!double.IsFinite(trimFraction) || trimFraction <= 0 || trimFraction > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trimFraction),
                "The memory-pressure trim fraction must be finite and in the (0, 1] range."
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTrimCount);
    }
}
