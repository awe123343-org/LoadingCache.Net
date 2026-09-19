namespace LoadingCache.Tests;

// Shared fixture configuration keeps race schedules independent of builder syntax.
internal sealed class LoadingTestOptions
{
    internal int MaximumSize { get; init; }
    internal int MaxConcurrentLoads { get; init; }
    internal TimeSpan? ExpireAfterWrite { get; init; }
    internal TimeSpan? ExpireAfterAccess { get; init; }
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal bool RecordStatistics { get; init; }
    internal LoadingCacheTestHooks? TestHooks { get; init; }

    internal IAsyncLoadingCache<TKey, TValue> Build<TKey, TValue>(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        IEqualityComparer<TKey>? comparer
    )
        where TKey : notnull
        where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(loader);
        var builder = CacheBuilder
            .Create<TKey, TValue>()
            .MaximumSize(MaximumSize)
            .MaxConcurrentLoads(MaxConcurrentLoads)
            .TimeProvider(TimeProvider);
        if (ExpireAfterWrite is { } write)
            builder.ExpireAfterWrite(write);
        if (ExpireAfterAccess is { } access)
            builder.ExpireAfterAccess(access);
        if (comparer is not null)
            builder.Comparer(comparer);
        if (RecordStatistics)
            builder.RecordStatistics();
        return TestHooks is null
            ? builder.BuildAsyncLoading(loader)
            : new AsyncLoadingCache<TKey, TValue>(
                builder.CreateEngine(
                    TestHooks,
                    hasFixedLoader: true,
                    isAsync: true,
                    supportsBulkLoading: false
                ),
                loader
            );
    }
}
