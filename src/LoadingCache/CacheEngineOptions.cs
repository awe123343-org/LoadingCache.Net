using LoadingCache.Maintenance;

namespace LoadingCache;

internal sealed class CacheEngineOptions<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal int? MaximumSize { get; init; }
    internal long? MaximumWeight { get; init; }
    internal int? MaximumResidentCount { get; init; }
    internal Func<TKey, TValue, long>? Weigher { get; init; }

    // Used exclusively by the manual lease-owning facade. This callback only
    // retires its internal ownership token; it never executes user code.
    internal Action<TValue>? OnValueRetired { get; init; }
    internal int MaxConcurrentLoads { get; init; }
    internal TimeSpan? ExpireAfterWrite { get; init; }
    internal TimeSpan? ExpireAfterAccess { get; init; }
    internal TimeSpan? RefreshAfterWrite { get; init; }
    internal TimeSpan? LoadTimeout { get; init; }
    internal TimeSpan RefreshFailureBackoff { get; init; } = TimeSpan.FromSeconds(1);
    internal IExpiry<TKey, TValue>? Expiry { get; init; }
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal bool RecordStatistics { get; init; }
    internal bool EnableExpirationScheduler { get; init; }
    internal TimeSpan? MemoryPressureSamplingInterval { get; init; }
    internal double MemoryPressureThreshold { get; init; } = 0.9;
    internal double MemoryPressureTrimFraction { get; init; } = 0.1;
    internal int MemoryPressureMaximumTrimCount { get; init; } = 256;
    internal IMemoryPressureSource MemoryPressureSource { get; init; } =
        GcMemoryPressureSource.Instance;
    internal IEqualityComparer<TKey>? Comparer { get; init; }
    internal LoadingCacheTestHooks? TestHooks { get; init; }
    internal ICacheEnginePolicy? Policy { get; init; }
    internal IMaintenanceScheduler? MaintenanceScheduler { get; init; }
    internal int MaintenanceMaxPasses { get; init; } = 32;
    internal int MaintenanceReadStripeCount { get; init; } = 4;
    internal int MaintenanceReadStripeCapacity { get; init; } = 256;
}
