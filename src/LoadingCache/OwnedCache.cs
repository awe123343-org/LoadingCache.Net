using System.Diagnostics.CodeAnalysis;
using LoadingCache.Maintenance;
using LoadingCache.Ownership;

namespace LoadingCache;

/// <summary>
/// Options for an opt-in cache that owns reference values and disposes them
/// after eviction and after all caller leases have been released.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The reference value type.</typeparam>
public sealed class OwnedCacheOptions<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    /// <summary>Gets or sets the maximum resident entry count.</summary>
    public int? MaximumSize { get; init; }

    /// <summary>Gets or sets the maximum resident weight.</summary>
    public long? MaximumWeight { get; init; }

    /// <summary>
    /// Gets or sets the resident entry bound used together with
    /// <see cref="MaximumWeight"/>.
    /// </summary>
    public int? MaximumResidentCount { get; init; }

    /// <summary>Gets or sets the value weight function.</summary>
    public Func<TKey, TValue, long>? Weigher { get; init; }

    /// <summary>
    /// Gets or sets the bound for distinct active value identities. This must
    /// be positive and should include headroom above the resident bound for
    /// replacement values and live leases.
    /// </summary>
    /// <remarks>
    /// The bound includes resident values, retired values with live leases,
    /// and values whose disposer is still running.
    /// </remarks>
    public int MaximumActiveValues { get; init; }

    /// <summary>Gets or sets the expire-after-write duration.</summary>
    public TimeSpan? ExpireAfterWrite { get; init; }

    /// <summary>Gets or sets the expire-after-access duration.</summary>
    public TimeSpan? ExpireAfterAccess { get; init; }

    /// <summary>Gets or sets the time provider used for expiration.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets or sets whether one cache-owned expiration timer is enabled.</summary>
    public bool EnableExpirationScheduler { get; init; }

    /// <summary>Gets or sets whether cache statistics are recorded.</summary>
    public bool RecordStatistics { get; init; }

    /// <summary>Gets or sets the optional memory pressure sampling interval.</summary>
    public TimeSpan? MemoryPressureSamplingInterval { get; init; }

    /// <summary>Gets or sets the memory pressure trim threshold.</summary>
    public double MemoryPressureThreshold { get; init; } = 0.9;

    /// <summary>Gets or sets the fraction trimmed by one pressure sample.</summary>
    public double MemoryPressureTrimFraction { get; init; } = 0.1;

    /// <summary>Gets or sets the maximum entries trimmed by one pressure sample.</summary>
    public int MemoryPressureMaximumTrimCount { get; init; } = 256;

    /// <summary>Gets or sets the source used for memory pressure samples.</summary>
    public IMemoryPressureSource MemoryPressureSource { get; init; } =
        GcMemoryPressureSource.Instance;

    /// <summary>Gets or sets the comparer used for all key operations.</summary>
    public IEqualityComparer<TKey>? Comparer { get; init; }
}

/// <summary>Describes asynchronous value-disposal activity.</summary>
public readonly record struct OwnedCacheDisposalStatistics
{
    /// <summary>Creates a disposal activity snapshot.</summary>
    public OwnedCacheDisposalStatistics(
        int activeValueCount,
        long pendingDisposals,
        Exception? lastDisposalError
    )
    {
        ActiveValueCount = activeValueCount;
        PendingDisposals = pendingDisposals;
        LastDisposalError = lastDisposalError;
    }

    /// <summary>Gets the number of resident, leased, or disposing identities.</summary>
    public int ActiveValueCount { get; }

    /// <summary>Gets the number of disposers that have not completed.</summary>
    public long PendingDisposals { get; }

    /// <summary>Gets the most recent disposer exception, if any.</summary>
    public Exception? LastDisposalError { get; }
}

/// <summary>Factory methods for the opt-in lease-owning cache.</summary>
public static class OwnedCache
{
    /// <summary>Creates a cache with a synchronous value disposer.</summary>
    public static OwnedCache<TKey, TValue> Create<TKey, TValue>(
        OwnedCacheOptions<TKey, TValue> options,
        Action<TValue> disposeValue
    )
        where TKey : notnull
        where TValue : class => new(options, disposeValue, null);

    /// <summary>Creates a cache with an asynchronous value disposer.</summary>
    public static OwnedCache<TKey, TValue> CreateAsync<TKey, TValue>(
        OwnedCacheOptions<TKey, TValue> options,
        Func<TValue, ValueTask> disposeValueAsync
    )
        where TKey : notnull
        where TValue : class => new(options, null, disposeValueAsync);
}

/// <summary>
/// A manual cache with explicit leases for reference values owned by the
/// cache. It is the safe opt-in path for automatic disposal.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The reference value type.</typeparam>
/// <remarks>
/// <para>
/// <see cref="Put(TKey, TValue)"/> transfers ownership to the cache. The
/// caller must not use or dispose the value after that call. Use
/// <see cref="PutAndLease(TKey, TValue)"/> when the caller must continue to
/// use the value, and dispose the returned lease when it is finished.
/// </para>
/// <para>
/// This API is intentionally manual. A loading cache returns raw values to
/// existing waiters, so an automatic disposer cannot know when those aliases
/// are no longer in use without changing the loading API's ownership contract.
/// </para>
/// </remarks>
public sealed class OwnedCache<TKey, TValue> : IDisposable, IAsyncDisposable
    where TKey : notnull
    where TValue : class
{
    private readonly ValueOwnership<TValue> _ownership;
    private readonly CacheEngine<TKey, OwnedEntry> _engine;

    internal OwnedCache(
        OwnedCacheOptions<TKey, TValue> options,
        Action<TValue>? disposeValue,
        Func<TValue, ValueTask>? disposeValueAsync,
        Func<Action, bool>? scheduleDisposal = null,
        IMaintenanceScheduler? maintenanceScheduler = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        if (disposeValue is null == disposeValueAsync is null)
        {
            throw new ArgumentException(
                "Configure exactly one synchronous or asynchronous value disposer.",
                nameof(disposeValue)
            );
        }

        int maximumActiveValues = ResolveMaximumActiveValues(options);
        Func<TKey, TValue, long>? configuredWeigher = options.Weigher;
        _ownership = disposeValue is not null
            ? new ValueOwnership<TValue>(
                maximumActiveValues,
                disposeValue,
                scheduleDisposal: scheduleDisposal
            )
            : new ValueOwnership<TValue>(
                maximumActiveValues,
                disposeValueAsync!,
                scheduleDisposal: scheduleDisposal
            );

        try
        {
            _engine = new CacheEngine<TKey, OwnedEntry>(
                new CacheEngineOptions<TKey, OwnedEntry>
                {
                    MaximumSize = options.MaximumSize,
                    MaximumWeight = options.MaximumWeight,
                    MaximumResidentCount = options.MaximumResidentCount,
                    Weigher = configuredWeigher is null
                        ? null
                        : (key, entry) => configuredWeigher!(key, entry.Value),
                    OnValueRetired = entry => _ownership.Retire(entry.Token),
                    MaxConcurrentLoads = 1,
                    ExpireAfterWrite = options.ExpireAfterWrite,
                    ExpireAfterAccess = options.ExpireAfterAccess,
                    TimeProvider = options.TimeProvider,
                    EnableExpirationScheduler = options.EnableExpirationScheduler,
                    RecordStatistics = options.RecordStatistics,
                    MaintenanceScheduler = maintenanceScheduler,
                    MemoryPressureSamplingInterval = options.MemoryPressureSamplingInterval,
                    MemoryPressureThreshold = options.MemoryPressureThreshold,
                    MemoryPressureTrimFraction = options.MemoryPressureTrimFraction,
                    MemoryPressureMaximumTrimCount = options.MemoryPressureMaximumTrimCount,
                    MemoryPressureSource = options.MemoryPressureSource,
                    Comparer = options.Comparer,
                }
            );
        }
        catch
        {
            _ownership.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Attempts to acquire a lease for a fresh resident value.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="lease">The caller lease when found.</param>
    /// <returns><see langword="true"/> when a lease was acquired.</returns>
    public bool TryGet(TKey key, [NotNullWhen(true)] out CacheLease<TValue>? lease)
    {
        CacheLease<TValue>? candidate = null;
        try
        {
            bool found = _engine.TryGetOwned(
                key,
                entry =>
                {
                    try
                    {
                        candidate = _ownership.Acquire(entry.Token);
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                },
                out _
            );

            if (!found)
            {
                candidate?.Dispose();
                lease = null;
                return false;
            }

            lease = candidate!;
            return true;
        }
        catch
        {
            candidate?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stores a value and transfers its ownership to the cache.
    /// </summary>
    /// <remarks>
    /// Use <see cref="PutAndLease(TKey, TValue)"/> if the caller needs to use
    /// the value after this method returns. Admission failure before ownership
    /// registration leaves the value with the caller. Once registration
    /// succeeds, ownership has transferred and a later engine failure retires
    /// the value through the configured disposer.
    /// </remarks>
    public void Put(TKey key, TValue value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        (ValueOwnership<TValue>.Token token, CacheLease<TValue> operationLease) =
            _ownership.PublishAndAcquire(value);
        try
        {
            _engine.Put(key, new OwnedEntry(value, token));
        }
        catch
        {
            _ownership.Retire(token);
            throw;
        }
        finally
        {
            operationLease.Dispose();
        }
    }

    /// <summary>
    /// Stores a value and returns a lease that keeps it usable after eviction.
    /// </summary>
    /// <param name="key">The key to store.</param>
    /// <param name="value">The value whose ownership is transferred.</param>
    /// <returns>A lease for the stored value.</returns>
    /// <remarks>
    /// If active-value admission fails before registration, the caller still
    /// owns <paramref name="value"/>. After registration succeeds, a later
    /// engine failure retires it through the configured disposer.
    /// </remarks>
    public CacheLease<TValue> PutAndLease(TKey key, TValue value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        (ValueOwnership<TValue>.Token token, CacheLease<TValue> lease) =
            _ownership.PublishAndAcquire(value);
        try
        {
            _engine.Put(key, new OwnedEntry(value, token));
            return lease;
        }
        catch
        {
            lease?.Dispose();
            _ownership.Retire(token);
            throw;
        }
    }

    /// <summary>Invalidates the current value for a key.</summary>
    public bool Invalidate(TKey key) => _engine.Invalidate(key);

    /// <summary>Invalidates the current values for a sequence of keys.</summary>
    public int Invalidate(IEnumerable<TKey> keys) => _engine.Invalidate(keys);

    /// <summary>Removes every resident value.</summary>
    public void Clear() => _engine.Clear();

    /// <summary>Runs currently available expiration and policy maintenance.</summary>
    public void CleanUp()
    {
        _engine.CleanUp();
        _ownership.RetryPendingDisposals();
    }

    /// <summary>
    /// Retries disposers rejected by infrastructure scheduling and returns the
    /// number accepted. Running disposers are never started again. Available
    /// after cache shutdown; this does not wait for user disposal code.
    /// </summary>
    public int RetryPendingDisposals() => _ownership.RetryPendingDisposals();

    /// <summary>Gets an approximate resident entry count.</summary>
    public long EstimatedCount => _engine.EstimatedCount;

    /// <summary>Gets a point-in-time statistics snapshot.</summary>
    public CacheStatistics GetStatistics() => _engine.GetStatistics();

    /// <summary>Gets a point-in-time value-disposal activity snapshot.</summary>
    public OwnedCacheDisposalStatistics GetDisposalStatistics() => _ownership.GetStatistics();

    /// <summary>
    /// Stops the cache and schedules disposal of retired values. It does not
    /// wait for non-cooperative user disposers.
    /// </summary>
    /// <remarks>Concurrent synchronous calls may return while the first caller
    /// finishes infrastructure teardown. New cache operations are already fenced.
    /// Use DisposeAsync to observe shared engine shutdown completion.</remarks>
    public void Dispose()
    {
        try
        {
            _engine.Dispose();
        }
        finally
        {
            _ownership.Dispose();
        }
    }

    /// <summary>
    /// Stops cache-owned infrastructure and schedules value disposal. The
    /// returned task does not wait for user disposal code to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _ownership.Dispose();
        }
    }

    private static int ResolveMaximumActiveValues(OwnedCacheOptions<TKey, TValue> options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumActiveValues);
        return options.MaximumActiveValues;
    }

    private static void ValidateKey(TKey key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
    }

    private sealed class OwnedEntry
    {
        internal OwnedEntry(TValue value, ValueOwnership<TValue>.Token token)
        {
            Value = value;
            Token = token;
        }

        internal TValue Value { get; }

        internal ValueOwnership<TValue>.Token Token { get; }
    }
}
