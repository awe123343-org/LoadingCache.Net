namespace LoadingCache;

/// <summary>
/// Compatibility facade for the original fixed-loader entry point.  It routes
/// all operations through the shared <see cref="CacheEngine{TKey, TValue}"/>
/// used by the four builder personalities.
/// </summary>
internal sealed class LoadingCacheImpl<TKey, TValue> : AsyncLoadingCache<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal LoadingCacheImpl(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingCacheOptions options,
        IEqualityComparer<TKey>? comparer
    )
        : base(CreateEngine(loader, options, comparer), loader) { }

    internal new void AssertInvariants()
    {
        base.AssertInvariants();
    }

    private static CacheEngine<TKey, TValue> CreateEngine(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingCacheOptions options,
        IEqualityComparer<TKey>? comparer
    )
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(options);
        return new CacheEngine<TKey, TValue>(
            new CacheEngineOptions<TKey, TValue>
            {
                MaximumSize = options.MaximumSize,
                MaxConcurrentLoads = options.MaxConcurrentLoads,
                ExpireAfterWrite = options.ExpireAfterWrite,
                ExpireAfterAccess = options.ExpireAfterAccess,
                RefreshAfterWrite = options.RefreshAfterWrite,
                LoadTimeout = options.LoadTimeout,
                RefreshFailureBackoff = options.RefreshFailureBackoff,
                TimeProvider = options.TimeProvider,
                RecordStatistics = options.RecordStatistics,
                EnableExpirationScheduler = options.EnableExpirationScheduler,
                Comparer = comparer,
                TestHooks = options.TestHooks,
            }
        );
    }
}
