using System.Diagnostics.CodeAnalysis;
using LoadingCache.Diagnostics;
using LoadingCache.Expiration;
using LoadingCache.Maintenance;
using LoadingCache.ReferenceStorage;

namespace LoadingCache;

/// <summary>
/// The shared data plane used by every cache personality.  Policy metadata is
/// deliberately kept behind the <see cref="ICacheEnginePolicy"/>
/// seam; the mapping and flight ownership rules do not depend on a particular
/// eviction algorithm.
/// </summary>
internal sealed partial class CacheEngine<TKey, TValue> : ILoadingCacheKeyOwner, IDisposable
    where TKey : notnull
    where TValue : notnull
{
    private readonly EntryStore _entries;
    private readonly object _gate = new();
    private readonly Func<TKey, TValue, long>? _weigher;
    private readonly Action<TValue>? _onValueRetired;
    private readonly int _maxConcurrentLoads;
    private long _expireAfterWriteTicks;
    private long _expireAfterAccessTicks;
    private long _refreshAfterWriteTicks;
    private readonly long _loadTimeoutTicks;
    private readonly long _refreshFailureBackoffTicks;
    private readonly IExpiry<TKey, TValue>? _expiry;
    private readonly TimeProvider _timeProvider;
    private readonly bool _recordStatistics;
    private readonly bool _enableExpirationScheduler;
    private readonly ITimer? _expirationTimer;
    private readonly MemoryPressureController<TKey, TValue>? _memoryPressureController;
    private readonly object _expirationTimerGate = new();
    private TimerWheel<Entry>? _expirationWheel;
    private readonly Dictionary<Entry, IdentityTimerNode<Entry>>? _expirationNodes;
    private long _expirationOriginTimestamp;
    private ulong _expirationNow;
    private bool _expirationClockInitialized;
    private int _expirationTimerRunning;
    private int _expirationTimerRearmRequested;
    private long _expirationArmRevision;
    private const int ExpirationAdvanceBudget = 128;
    private const long NormalizedExpirationTickMilliseconds = 1;
    private readonly LoadingCacheTestHooks? _testHooks;
    private readonly ICacheEnginePolicy _policy;
    private readonly MaintenanceCoordinator _maintenanceCoordinator;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly HashSet<Flight> _activeFlights = [];
    private readonly TaskCompletionSource<object?> _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private long _nextEpoch;
    private long _nextGeneration;
    private long _epoch;
    private int _reservedLoads;
    private int _runningLoads;
    private int _disposed;
    private int _shutdownStarted;
    private int _shutdownCancellationCompleted;
    private int _shutdownSourceDisposed;

    private enum ExpirationKind
    {
        Access,
        Write,
        Refresh,
        Variable,
    }

    internal CacheEngine(CacheEngineOptions<TKey, TValue> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options is { WeakKeys: true, Comparer: not null })
        {
            throw new ArgumentException(
                "Weak keys use identity equality and cannot use a custom comparer.",
                nameof(options)
            );
        }

        _weakKeys = options.WeakKeys;
        _weakValues = options.WeakValues;
        Comparer = _weakKeys
            ? ReferenceIdentityComparer<TKey>.Instance
            : options.Comparer ?? EqualityComparer<TKey>.Default;
        _entries = new EntryStore(_weakKeys, Comparer);

        if (_weakKeys && typeof(TKey).IsValueType)
        {
            throw new ArgumentException("Weak keys require a reference type.", nameof(options));
        }

        if (_weakValues && typeof(TValue).IsValueType)
        {
            throw new ArgumentException("Weak values require a reference type.", nameof(options));
        }

        if (options.MaximumSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumSize must be positive.");
        }

        if (options.MaximumWeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumWeight must be positive."
            );
        }

        if (options.MaximumSize is not null && options.MaximumWeight is not null)
        {
            throw new ArgumentException(
                "MaximumSize and MaximumWeight are mutually exclusive.",
                nameof(options)
            );
        }

        if (options.MaximumWeight is not null && options.MaximumResidentCount is null)
        {
            throw new ArgumentException(
                "A weighted cache must also configure MaximumResidentCount.",
                nameof(options)
            );
        }

        if (options.MaximumResidentCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumResidentCount must be positive."
            );
        }

        if (options.MaximumWeight is not null && options.Weigher is null)
        {
            throw new ArgumentException(
                "A weighted cache must configure a weigher.",
                nameof(options)
            );
        }

        if (options.MaximumSize is null && options.MaximumWeight is null)
        {
            throw new ArgumentException(
                "A cache must configure MaximumSize or MaximumWeight.",
                nameof(options)
            );
        }

        if (options.MaxConcurrentLoads <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxConcurrentLoads must be positive."
            );
        }

        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (options.MemoryPressureSamplingInterval is { } pressureInterval)
        {
            ArgumentNullException.ThrowIfNull(options.MemoryPressureSource);
            MemoryPressureController<TKey, TValue>.ValidateOptions(
                pressureInterval,
                options.MemoryPressureThreshold,
                options.MemoryPressureTrimFraction,
                options.MemoryPressureMaximumTrimCount
            );
        }
        ValidateDuration(options.ExpireAfterWrite, nameof(options.ExpireAfterWrite));
        ValidateDuration(options.ExpireAfterAccess, nameof(options.ExpireAfterAccess));
        ValidateDuration(options.RefreshAfterWrite, nameof(options.RefreshAfterWrite));
        ValidateDuration(options.LoadTimeout, nameof(options.LoadTimeout));
        ValidateDuration(options.RefreshFailureBackoff, nameof(options.RefreshFailureBackoff));
        if (
            options.Expiry is not null
            && (options.ExpireAfterWrite.HasValue || options.ExpireAfterAccess.HasValue)
        )
        {
            throw new ArgumentException(
                "Variable expiration cannot be combined with fixed expiration.",
                nameof(options)
            );
        }

        _weigher = options.Weigher;
        _onValueRetired = options.OnValueRetired;
        _maxConcurrentLoads = options.MaxConcurrentLoads;
        ConfigureBulkLimits(options.MaxPendingLoadKeys, options.MaximumBulkKeys);
        _expireAfterWriteTicks = options.ExpireAfterWrite?.Ticks ?? -1;
        _expireAfterAccessTicks = options.ExpireAfterAccess?.Ticks ?? -1;
        _refreshAfterWriteTicks = options.RefreshAfterWrite?.Ticks ?? -1;
        _loadTimeoutTicks = options.LoadTimeout?.Ticks ?? -1;
        _refreshFailureBackoffTicks = options.RefreshFailureBackoff.Ticks;
        _expiry = options.Expiry;
        _timeProvider = options.TimeProvider;
        _recordStatistics = options.RecordStatistics;
        _enableExpirationScheduler = options.EnableExpirationScheduler;
        _testHooks = options.TestHooks;
        _policy =
            options.Policy
            ?? new WindowTinyLfuEnginePolicy(
                options.MaximumWeight ?? options.MaximumSize!.Value,
                options.MaximumResidentCount ?? options.MaximumSize,
                RemoveEvictedEntry,
                RequestPolicyMaintenance,
                options.TestHooks?.BeforePolicyMaintenance,
                options.TestHooks?.BeforeMaintenanceSignalClear,
                options.MaintenanceReadStripeCount,
                options.MaintenanceReadStripeCapacity
            );
        _maintenanceCoordinator = new MaintenanceCoordinator(
            DrainPolicyMaintenance,
            options.MaintenanceScheduler,
            options.MaintenanceMaxPasses
        );
        Policy = new CachePolicyView<TKey, TValue>(
            new EngineEvictionView(this, options.MaximumWeight.HasValue),
            QuietLookup,
            GetPublicMemoryPressureStatistics,
            GetExpireAfterAccess,
            SetExpireAfterAccess,
            key => GetExpiresAfter(key, ExpirationKind.Access),
            key => GetAgeOf(key, ExpirationKind.Access),
            GetExpireAfterWrite,
            SetExpireAfterWrite,
            key => GetExpiresAfter(key, ExpirationKind.Write),
            key => GetAgeOf(key, ExpirationKind.Write),
            GetRefreshAfterWrite,
            SetRefreshAfterWrite,
            key => GetExpiresAfter(key, ExpirationKind.Refresh),
            key => GetAgeOf(key, ExpirationKind.Refresh),
            _expiry is null
                ? null
                : new CachePolicyView<TKey, TValue>.VariableExpirationView<TKey, TValue>(
                    key => GetExpiresAfter(key, ExpirationKind.Variable),
                    SetVariableExpiresAfter,
                    key => GetAgeOf(key, ExpirationKind.Variable),
                    PutWithVariableDuration
                )
        );
        _epoch = _nextEpoch = 1;

        if (
            options.Expiry is not null
            || options.ExpireAfterWrite.HasValue
            || options.ExpireAfterAccess.HasValue
        )
        {
            _expirationOriginTimestamp = _timeProvider.GetTimestamp();
            _expirationClockInitialized = true;
            _expirationWheel = new TimerWheel<Entry>();
            _expirationNodes = new Dictionary<Entry, IdentityTimerNode<Entry>>();
            if (_enableExpirationScheduler)
            {
                _expirationTimer = CreateExpirationTimer();
            }
        }

        if (options.MemoryPressureSamplingInterval is { } samplingInterval)
        {
            try
            {
                _memoryPressureController = new MemoryPressureController<TKey, TValue>(
                    this,
                    _timeProvider,
                    samplingInterval,
                    options.MemoryPressureThreshold,
                    options.MemoryPressureTrimFraction,
                    options.MemoryPressureMaximumTrimCount,
                    options.MemoryPressureSource
                );
            }
            catch
            {
                _expirationTimer?.Dispose();
                _maintenanceCoordinator.Dispose();
                _policy.Dispose();
                DisposeDiagnostics();
                _shutdownCts.Dispose();
                throw;
            }
        }

        // Publish diagnostics only after the policy, maintenance coordinator,
        // expiration state, and optional pressure timer are fully initialized.
        // A MeterListener can observe instruments synchronously during setup.
        try
        {
            InitializeDiagnostics(options);
        }
        catch
        {
            _memoryPressureController?.Dispose();
            _expirationTimer?.Dispose();
            _maintenanceCoordinator.Dispose();
            _policy.Dispose();
            DisposeDiagnostics();
            _shutdownCts.Dispose();
            throw;
        }
    }

    internal long EstimatedCount
    {
        get
        {
            ThrowIfDisposed();
            long count = 0;
            foreach (Entry entry in _entries.Values)
            {
                if (
                    Volatile.Read(ref entry.IsReady)
                    && !Volatile.Read(ref entry.PolicyDetached)
                    && entry.TryGetKey(out _)
                    && entry.TryGetValue(out _)
                )
                {
                    count++;
                }
            }

            return count;
        }
    }

    internal ICachePolicy<TKey, TValue> Policy { get; }

    internal MaintenanceStatistics GetMaintenanceStatistics() =>
        _maintenanceCoordinator.GetStatistics();

    internal ReadBufferStatistics GetPolicyReadBufferStatistics() =>
        _policy.GetReadBufferStatistics();

    internal IEqualityComparer<TKey> Comparer { get; }

    internal void EnsureUsable() => ThrowIfDisposed();

    internal CacheStatistics GetStatistics()
    {
        ThrowIfDisposed();
        return GetStatisticsSnapshot(exposeCounters: _recordStatistics);
    }

    internal bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ThrowIfDisposed();

        if (TryReadReady(key, out value))
        {
            return true;
        }

        RecordMiss();
        value = default;
        return false;
    }

    internal ValueTask<TValue> GetAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken = default
    ) => GetAsync(key, factory, null, cancellationToken);

    internal ValueTask<TValue> GetAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        Func<TKey, TValue, CancellationToken, Task<TValue>>? reloadFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfDisposed();
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<TValue>(cancellationToken);
        }

        if (
            TryReadReady(
                key,
                out TValue readyValue,
                out _,
                out Entry? readyEntry,
                out bool refreshEligible
            )
        )
        {
            if (refreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(readyEntry!, reloadFactory);
            }

            return new ValueTask<TValue>(readyValue!);
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        AsyncFlight? flight = null;
        bool created = false;
        bool fallbackHit = false;
        bool fallbackExpired = false;
        bool fallbackCollected = false;
        object? fallbackPolicyToken = null;
        Entry? fallbackEntry = null;
        bool fallbackRefreshEligible = false;
        long fallbackVariableTimestamp = 0;
        long fallbackVariableRevision = 0;
        TimeSpan fallbackVariableDuration = TimeSpan.MaxValue;
        RemovalNotification<TKey, TValue>? pendingEviction = null;
        bool loadRejected = false;

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_entries.TryGetValue(key, out Entry? current))
            {
                if (Volatile.Read(ref current.IsReady))
                {
                    lock (current.Sync)
                    {
                        long now = _timeProvider.GetTimestamp();
                        if (
                            Volatile.Read(ref current.IsReady)
                            && !IsExpired(current, now)
                            && current.TryGetValue(out TValue? liveValue)
                        )
                        {
                            TouchWithoutLock(current, now);
                            readyValue = liveValue;
                            fallbackPolicyToken = current.PolicyToken;
                            fallbackEntry = current;
                            fallbackVariableTimestamp = current.VariableTimestamp;
                            fallbackVariableRevision = current.VariableRevision;
                            fallbackVariableDuration = _expiry is null
                                ? TimeSpan.MaxValue
                                : GetRemainingDuration(current, now, ExpirationKind.Variable);
                            fallbackRefreshEligible = IsRefreshEligibleLocked(current, now);
                            fallbackHit = true;
                        }
                        else if (Volatile.Read(ref current.IsReady))
                        {
                            fallbackCollected =
                                (_weakKeys && !current.TryGetKey(out _))
                                || (_weakValues && !current.TryGetValue(out _));
                            fallbackExpired = !fallbackCollected;
                        }
                    }

                    if (!fallbackHit)
                    {
                        if (
                            current.RefreshFlight is AsyncFlight refreshFlight
                            && IsCurrentRefreshFlightLocked(current, refreshFlight)
                        )
                        {
                            RecordMiss();
                            RecordCoalescedWaiter();
                            flight = refreshFlight;
                        }
                        else
                        {
                            pendingEviction = RemoveCurrentEntryLocked(
                                current,
                                fallbackCollected ? RemovalCause.Collected
                                    : fallbackExpired ? RemovalCause.Expired
                                    : RemovalCause.Explicit
                            );
                        }
                    }
                }
                else if (!fallbackHit)
                {
                    if (LoadChainContext.Contains(this, key))
                    {
                        throw new LoadingCacheReentrancyException(
                            "A loading delegate attempted to await a flight for an equivalent key in its own logical load chain."
                        );
                    }

                    RecordMiss();
                    RecordCoalescedWaiter();
                    flight = (AsyncFlight)current.Flight!;
                }
            }

            if (flight is null && !fallbackHit)
            {
                RecordMiss();
                if (!CanReserveFlightsLocked(1))
                {
                    RecordCounter(CacheCounterKind.LoadRejections);
                    loadRejected = true;
                }
                else
                {
                    flight = new AsyncFlight(key, _epoch, ++_nextGeneration, factory);
                    Entry entry = Entry.Loading(key, _epoch, flight.Generation, flight, _weakKeys);
                    _entries[key] = entry;
                    _activeFlights.Add(flight);
                    _reservedLoads++;
                    created = true;
                }
            }
        }

        if (pendingEviction is { } evictionNotification)
        {
            DispatchSynchronousEviction(evictionNotification);
        }
        evictionScope.Dispatch();

        if (loadRejected)
        {
            return ValueTask.FromException<TValue>(new CacheLoadRejectedException());
        }

        if (fallbackHit)
        {
            ApplyReadExpiryUpdate(
                fallbackEntry!,
                key,
                readyValue!,
                fallbackVariableTimestamp,
                fallbackVariableRevision,
                fallbackVariableDuration
            );
            RecordHit();
            _policy.OnAccess(fallbackPolicyToken);
            if (fallbackRefreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(fallbackEntry!, reloadFactory);
            }
            return new ValueTask<TValue>(readyValue);
        }

        if (created)
        {
            try
            {
                InvokeHook(_testHooks?.AfterFlightInstalled);
            }
            catch (Exception exception)
            {
                CompleteFailure(flight!, exception);
                return WaitForFlight(flight!, key, cancellationToken);
            }
        }

        // A joiner is allowed to start a flight whose installer is paused.
        StartAsyncFlight(flight!);
        return WaitForFlight(flight!, key, cancellationToken);
    }

    internal bool TryGetTask(TKey key, [NotNullWhen(true)] out Task<TValue>? valueTask)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ThrowIfDisposed();

        if (TryReadReady(key, out TValue value, out Task<TValue>? sharedTask))
        {
            valueTask = sharedTask ?? Task.FromResult(value!);
            return true;
        }

        if (_entries.TryGetValue(key, out Entry? entry) && !Volatile.Read(ref entry.IsReady))
        {
            if (entry.Flight is AsyncFlight asyncFlight)
            {
                RecordMiss();
                valueTask = GetBulkTask(asyncFlight, key);
                return true;
            }
        }

        RecordMiss();
        valueTask = null;
        return false;
    }

    internal ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken = default
    ) => GetAsync(key, factory, cancellationToken);

    internal TValue GetOrAdd(
        TKey key,
        Func<TKey, TValue> factory,
        CancellationToken cancellationToken = default
    ) => GetOrAdd(key, factory, null, cancellationToken);

    internal TValue GetOrAdd(
        TKey key,
        Func<TKey, TValue> factory,
        Func<TKey, TValue, TValue>? reloadFactory,
        CancellationToken cancellationToken = default
    )
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (
            TryReadReady(
                key,
                out TValue readyValue,
                out _,
                out Entry? readyEntry,
                out bool refreshEligible
            )
        )
        {
            if (refreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(readyEntry!, reloadFactory);
            }

            return readyValue!;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        SyncFlight? flight = null;
        bool created = false;
        bool fallbackHit = false;
        bool fallbackExpired = false;
        bool fallbackCollected = false;
        object? fallbackPolicyToken = null;
        Entry? fallbackEntry = null;
        bool fallbackRefreshEligible = false;
        long fallbackVariableTimestamp = 0;
        long fallbackVariableRevision = 0;
        TimeSpan fallbackVariableDuration = TimeSpan.MaxValue;
        RemovalNotification<TKey, TValue>? pendingEviction = null;
        bool loadRejected = false;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_entries.TryGetValue(key, out Entry? current))
            {
                if (Volatile.Read(ref current.IsReady))
                {
                    lock (current.Sync)
                    {
                        long now = _timeProvider.GetTimestamp();
                        if (
                            Volatile.Read(ref current.IsReady)
                            && !IsExpired(current, now)
                            && current.TryGetValue(out TValue? liveValue)
                        )
                        {
                            TouchWithoutLock(current, now);
                            readyValue = liveValue;
                            fallbackPolicyToken = current.PolicyToken;
                            fallbackEntry = current;
                            fallbackVariableTimestamp = current.VariableTimestamp;
                            fallbackVariableRevision = current.VariableRevision;
                            fallbackVariableDuration = _expiry is null
                                ? TimeSpan.MaxValue
                                : GetRemainingDuration(current, now, ExpirationKind.Variable);
                            fallbackRefreshEligible = IsRefreshEligibleLocked(current, now);
                            fallbackHit = true;
                        }
                        else if (Volatile.Read(ref current.IsReady))
                        {
                            fallbackCollected =
                                (_weakKeys && !current.TryGetKey(out _))
                                || (_weakValues && !current.TryGetValue(out _));
                            fallbackExpired = !fallbackCollected;
                        }
                    }

                    if (!fallbackHit)
                    {
                        if (
                            current.RefreshFlight is SyncFlight refreshFlight
                            && IsCurrentRefreshFlightLocked(current, refreshFlight)
                        )
                        {
                            RecordMiss();
                            RecordCoalescedWaiter();
                            flight = refreshFlight;
                        }
                        else
                        {
                            pendingEviction = RemoveCurrentEntryLocked(
                                current,
                                fallbackCollected ? RemovalCause.Collected
                                    : fallbackExpired ? RemovalCause.Expired
                                    : RemovalCause.Explicit
                            );
                        }
                    }
                }
                else if (!fallbackHit)
                {
                    if (LoadChainContext.Contains(this, key))
                    {
                        throw new LoadingCacheReentrancyException(
                            "A loading delegate attempted to await a synchronous flight for an equivalent key in its own logical load chain."
                        );
                    }

                    RecordMiss();
                    RecordCoalescedWaiter();
                    flight = (SyncFlight)current.Flight!;
                }
            }

            if (flight is null && !fallbackHit)
            {
                RecordMiss();
                if (!CanReserveFlightsLocked(1))
                {
                    RecordCounter(CacheCounterKind.LoadRejections);
                    loadRejected = true;
                }
                else
                {
                    flight = new SyncFlight(key, _epoch, ++_nextGeneration, factory);
                    _entries[key] = Entry.Loading(
                        key,
                        _epoch,
                        flight.Generation,
                        flight,
                        _weakKeys
                    );
                    _activeFlights.Add(flight);
                    _reservedLoads++;
                    created = true;
                }
            }
        }

        if (pendingEviction is { } evictionNotification)
        {
            DispatchSynchronousEviction(evictionNotification);
        }
        evictionScope.Dispatch();

        if (loadRejected)
        {
            throw new CacheLoadRejectedException();
        }

        if (fallbackHit)
        {
            ApplyReadExpiryUpdate(
                fallbackEntry!,
                key,
                readyValue!,
                fallbackVariableTimestamp,
                fallbackVariableRevision,
                fallbackVariableDuration
            );
            RecordHit();
            _policy.OnAccess(fallbackPolicyToken);
            if (fallbackRefreshEligible && reloadFactory is not null)
            {
                StartAutomaticRefresh(fallbackEntry!, reloadFactory);
            }
            return readyValue;
        }

        if (created)
        {
            try
            {
                InvokeHook(_testHooks?.AfterFlightInstalled);
            }
            catch (Exception exception)
            {
                CompleteFailure(flight!, exception);
            }
        }

        StartSyncFlight(flight!);
        return WaitForBulkSync(flight!, key);
    }

    internal void Put(TKey key, TValue value)
    {
        PutCore(key, value, null);
    }

    internal void Put(TKey key, TValue value, TimeSpan duration)
    {
        PutCore(key, value, duration);
    }

    private void PutCore(TKey key, TValue value, TimeSpan? explicitDuration)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        ThrowIfDisposed();
        if (explicitDuration.HasValue)
        {
            if (_expiry is null)
            {
                throw new InvalidOperationException(
                    "An explicit duration requires variable expiration to be configured."
                );
            }

            ValidateVariableDuration(explicitDuration.Value, nameof(explicitDuration));
        }

        (bool isUpdate, TimeSpan currentDuration) = CaptureVariableUpdate(key);
        long weight = ComputeWeight(key, value);
        TimeSpan variableDuration =
            explicitDuration
            ?? (
                _expiry is null ? TimeSpan.MaxValue
                : isUpdate ? ComputeUpdateDuration(key, value, currentDuration)
                : ComputeCreateDuration(key, value)
            );

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            MarkDictionaryTransformMutation(key);
            RecordBulkMutationLocked(key);
            Entry entry = Entry.Ready(
                key,
                _epoch,
                ++_nextGeneration,
                value,
                _timeProvider.GetTimestamp(),
                weight,
                variableDuration,
                _weakKeys,
                _weakValues
            );
            entry.PolicyToken = new WindowTinyLfuEnginePolicy.EngineEntryToken(
                entry,
                GetPolicyHash(key)
            );
            ReplaceCurrentLocked(key, entry);
            _policy.OnPublish(entry.PolicyToken, entry.Weight);
            if (_expirationWheel is not null)
            {
                ulong normalizedNow = GetExpirationNowLocked();
                AdvanceExpirationLocked(normalizedNow);
                ScheduleExpirationNodeLocked(entry, normalizedNow);
            }
        }

        evictionScope.Dispatch();
        RequestExpirationTimer();
    }

    internal void PutTask(TKey key, Task<TValue> valueTask)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ArgumentNullException.ThrowIfNull(valueTask);
        ThrowIfDisposed();

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        AsyncFlight flight;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            MarkDictionaryTransformMutation(key);
            RecordBulkMutationLocked(key);
            if (!CanReserveFlightsLocked(1))
            {
                RecordCounter(CacheCounterKind.LoadRejections);
                throw new CacheLoadRejectedException();
            }

            flight = new AsyncFlight(key, _epoch, ++_nextGeneration, null, valueTask);
            ReplaceCurrentLocked(
                key,
                Entry.Loading(key, _epoch, flight.Generation, flight, _weakKeys)
            );
            _activeFlights.Add(flight);
            _reservedLoads++;
        }

        evictionScope.Dispatch();
        try
        {
            InvokeHook(_testHooks?.AfterFlightInstalled);
        }
        catch (Exception exception)
        {
            CompleteFailure(flight, exception);
            return;
        }

        StartAsyncFlight(flight);
    }

    internal bool Invalidate(TKey key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ThrowIfDisposed();

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool removed;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                MarkDictionaryTransformMutation(key);
                RecordBulkMutationLocked(key);
                return false;
            }

            MarkDictionaryTransformMutation(key);
            RecordBulkMutationLocked(key);
            RemoveCurrentEntryLocked(entry, RemovalCause.Explicit);
            removed = true;
        }

        evictionScope.Dispatch();
        RequestExpirationTimer();
        return removed;
    }

    internal int Invalidate(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfDisposed();
        TKey[] snapshot = SnapshotBulkKeys(keys, BulkFallbackInputLimit);
        int count = 0;

        foreach (TKey key in snapshot)
        {
            if (Invalidate(key))
            {
                count++;
            }
        }

        return count;
    }

    internal void Clear()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            MarkAllDictionaryTransformsMutated();
            _epoch = ++_nextEpoch;
            foreach (Entry entry in _entries.Snapshot())
            {
                RemoveCurrentEntryLocked(entry, RemovalCause.Cleared);
            }
            _entries.Clear();
            _policy.Clear();
            ResetExpirationStateLocked();
        }

        RequestExpirationTimer();
    }

    internal void CleanUp()
    {
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanUpCollectedReferencesLocked();
            if (_expirationWheel is not null)
            {
                AdvanceExpirationLocked(GetExpirationNowLocked());
            }
        }

        _maintenanceCoordinator.CleanUp();
        evictionScope.Dispatch();
        RequestExpirationTimer();
    }

    private bool RequestPolicyMaintenance()
    {
        MaintenanceRequestResult result = _maintenanceCoordinator.Request();
        if (result != MaintenanceRequestResult.ScheduleRejected)
        {
            return false;
        }

        // A rejected scheduler must not turn a resident hit into an unbounded
        // drain loop. One coordinator invocation already has its own bounded
        // pass budget; a remaining lossy read batch is retried by a later hit
        // or explicit CleanUp.
        MaintenanceCleanupResult cleanup = _maintenanceCoordinator.CleanUp();
        return cleanup.FallbackRequired;
    }

    private bool DrainPolicyMaintenance()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool moreWork;
        lock (_gate)
        {
            moreWork = _disposed == 0 && _policy.CleanUp();
        }

        evictionScope.Dispatch();
        return moreWork;
    }

    internal ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return new ValueTask(_disposeCompletion.Task);
        }

        Flight[] flights;
        ITimer[] timeoutTimers;
        lock (_gate)
        {
            _epoch = ++_nextEpoch;
            foreach (Entry entry in _entries.Snapshot())
            {
                RemoveCurrentEntryLocked(entry, RemovalCause.Cleared);
            }
            _entries.Clear();
            _policy.Clear();
            ResetExpirationStateLocked();
            flights = [.. _activeFlights];
            timeoutTimers = DetachFlightTimeoutTimersLocked(flights);
        }

        _maintenanceCoordinator.Dispose();
        _policy.Dispose();
        _memoryPressureController?.Dispose();
        StopExpirationTimer();
        DisposeDiagnostics();

        foreach (Flight flight in flights)
        {
            if (!CompleteBulkDisposed(flight))
            {
                flight.SetDisposed();
            }
        }

        DisposeFlightTimeoutTimers(timeoutTimers);

        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            _ = CancelShutdownAsync();
        }

        _disposeCompletion.TrySetResult(null);
        return new ValueTask(_disposeCompletion.Task);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Flight[] flights;
        ITimer[] timeoutTimers;
        lock (_gate)
        {
            _epoch = ++_nextEpoch;
            foreach (Entry entry in _entries.Snapshot())
            {
                RemoveCurrentEntryLocked(entry, RemovalCause.Cleared);
            }
            _entries.Clear();
            _policy.Clear();
            ResetExpirationStateLocked();
            flights = [.. _activeFlights];
            timeoutTimers = DetachFlightTimeoutTimersLocked(flights);
        }

        _maintenanceCoordinator.Dispose();
        _policy.Dispose();
        _memoryPressureController?.Dispose();
        StopExpirationTimer();
        DisposeDiagnostics();

        foreach (Flight flight in flights)
        {
            if (!CompleteBulkDisposed(flight))
            {
                flight.SetDisposed();
            }
        }

        DisposeFlightTimeoutTimers(timeoutTimers);

        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            _ = CancelShutdownAsync();
        }

        _disposeCompletion.TrySetResult(null);
    }

    internal void AssertInvariants()
    {
        lock (_gate)
        {
            int reserved = 0;
            int running = 0;
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Epoch != _epoch)
                {
                    throw new InvalidOperationException(
                        "An entry from a retired epoch remained in the current state."
                    );
                }

                if (Volatile.Read(ref entry.IsReady))
                {
                    if (entry.Flight is not null)
                    {
                        throw new InvalidOperationException(
                            "A resident entry retained a flight ownership record."
                        );
                    }
                }
                else if (entry.Flight is null || !_activeFlights.Contains(entry.Flight))
                {
                    throw new InvalidOperationException(
                        "A loading entry lost its flight ownership record."
                    );
                }
            }

            foreach (Flight flight in _activeFlights)
            {
                if (flight.Retired != 0)
                {
                    throw new InvalidOperationException(
                        "A retired flight remained in the active registry."
                    );
                }

                reserved++;
                if (flight.ExecutionStarted != 0 && flight.ExecutionResourcesReleased == 0)
                {
                    running++;
                }
            }

            int pending = PendingLoadKeyCountLocked();
            if (
                reserved != _reservedLoads
                || running != _runningLoads
                || _reservedLoads > _maxConcurrentLoads
                || _bulkGroupCount < 0
                || _bulkGroupCount > _activeFlights.Count
                || _bulkPendingKeyCount < 0
                || pending < 0
                || pending > BulkPendingKeyLimit
            )
            {
                throw new InvalidOperationException(
                    "The cache load permit invariant was violated."
                );
            }
        }
    }

    bool ILoadingCacheKeyOwner.KeysEqual(object first, object second)
    {
        return Comparer.Equals((TKey)first, (TKey)second);
    }

    private void StartAsyncFlight(AsyncFlight flight, Task<TValue>? suppliedTask = null)
    {
        if (Interlocked.CompareExchange(ref flight.Started, 1, 0) != 0)
        {
            return;
        }

        bool disposed;
        lock (_gate)
        {
            disposed = _disposed != 0;
            if (!disposed)
            {
                flight.ExecutionStarted = 1;
                _runningLoads++;
            }
        }

        if (disposed)
        {
            if (!CompleteBulkDisposed(flight))
            {
                flight.SetDisposed();
            }

            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        if (!PrepareFlightExecution(flight))
        {
            return;
        }

        // PrepareFlightExecution snapshots the token before arming the timer.
        // A custom TimeProvider may synchronously fire the timeout callback and
        // dispose the linked CTS before this method resumes.
        CancellationToken loadCancellationToken = flight.LoadCancellationToken;
        if (Volatile.Read(ref flight.TerminalClaimed) != 0)
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        Task<TValue>? load = suppliedTask ?? flight.SuppliedTask;
        if (load is null)
        {
            // Count a load only once the cache is about to invoke the user
            // loader.  A synchronously firing timeout can terminalize a
            // flight while its timeout is being armed; that flight never
            // invoked the backend and must not fabricate a load start or
            // terminal loader outcome in statistics.
            RecordFlightStarted(flight);
            try
            {
                load = InvokeAsyncFactory(flight.Factory!, flight.Key, loadCancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                CompleteCancellation(flight, exception);
                return;
            }
            catch (Exception exception)
            {
                CompleteFailure(flight, exception);
                return;
            }
        }

        ScheduleObservation(flight, load);
    }

    private void StartSyncFlight(SyncFlight flight)
    {
        Interlocked.Exchange(ref flight.StartRequested, 1);
        if (Interlocked.CompareExchange(ref flight.Started, 1, 0) != 0)
        {
            return;
        }

        bool disposed;
        lock (_gate)
        {
            disposed = _disposed != 0;
            if (!disposed)
            {
                flight.ExecutionStarted = 1;
                _runningLoads++;
            }
        }

        if (disposed)
        {
            if (!CompleteBulkDisposed(flight))
            {
                flight.SetDisposed();
            }

            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        if (!PrepareFlightExecution(flight))
        {
            return;
        }

        if (Volatile.Read(ref flight.TerminalClaimed) != 0)
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        try
        {
            // Keep the invocation boundary immediately adjacent to the user
            // callback.  PrepareFlightExecution may synchronously timeout
            // and prevent this call from happening at all.
            RecordFlightStarted(flight);
            TValue value = InvokeSyncFactory(flight.Factory, flight.Key);
            if (value is null)
            {
                throw new InvalidOperationException("The loading delegate returned a null value.");
            }

            CompleteSuccess(flight, value);
        }
        catch (OperationCanceledException exception)
        {
            CompleteCancellation(flight, exception);
        }
        catch (Exception exception)
        {
            CompleteFailure(flight, exception);
        }
    }

    private Task<TValue> InvokeAsyncFactory(
        Func<TKey, CancellationToken, Task<TValue>> factory,
        TKey key,
        CancellationToken cancellationToken
    )
    {
        EnterLoadChain(key);
        try
        {
            return factory(key, cancellationToken)
                ?? throw new InvalidOperationException(
                    "The loading delegate returned a null Task."
                );
        }
        finally
        {
            ExitLoadChain();
        }
    }

    private TValue InvokeSyncFactory(Func<TKey, TValue> factory, TKey key)
    {
        EnterLoadChain(key);
        try
        {
            return factory(key);
        }
        finally
        {
            ExitLoadChain();
        }
    }

    private void EnterLoadChain(TKey key)
    {
        if (LoadChainContext.Contains(this, key))
        {
            throw new LoadingCacheReentrancyException(
                "A loading delegate attempted to start an equivalent key in its own logical load chain."
            );
        }

        LoadChainContext.Current = new LoadChainContext.Node(
            new WeakReference<object>(this),
            key,
            LoadChainContext.Current
        );
    }

    private static void ExitLoadChain()
    {
        LoadChainContext.Current = LoadChainContext.Current?.Parent;
    }

    private void ScheduleObservation(AsyncFlight flight, Task<TValue> load)
    {
        WeakReference<CacheEngine<TKey, TValue>> owner = new(this);
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = ObserveAsync(owner, flight, load);
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _ = ObserveAsync(owner, flight, load);
        }
    }

    private static async Task ObserveAsync(
        WeakReference<CacheEngine<TKey, TValue>> owner,
        AsyncFlight flight,
        Task<TValue> load
    )
    {
        try
        {
            TValue value = await load.ConfigureAwait(false);
            if (value is null)
            {
                throw new InvalidOperationException("The loading delegate returned a null value.");
            }

            if (owner.TryGetTarget(out CacheEngine<TKey, TValue>? cache))
            {
                cache.CompleteSuccess(flight, value);
            }
            else
            {
                flight.Completion.TrySetResult(value);
            }
        }
        catch (OperationCanceledException exception)
        {
            if (owner.TryGetTarget(out CacheEngine<TKey, TValue>? cache))
            {
                cache.CompleteCancellation(flight, exception);
            }
            else
            {
                flight.Completion.TrySetCanceled(GetCancellationToken(exception));
            }
        }
        catch (Exception exception)
        {
            if (owner.TryGetTarget(out CacheEngine<TKey, TValue>? cache))
            {
                cache.CompleteFailure(flight, exception);
            }
            else
            {
                flight.TrySetException(exception);
            }
        }
    }

    private void CompleteSuccess(Flight flight, TValue value)
    {
        if (flight.IsRefresh)
        {
            CompleteRefreshSuccess(flight, value);
            return;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed = false;
        Entry? publishedEntry = null;
        long publishedRevision = 0;
        try
        {
            InvokeHook(_testHooks?.BeforePublish);
            if (TryCompleteBulkSuccess(flight, value))
            {
                evictionScope.Dispatch();
                return;
            }

            long weight = ComputeWeight(flight.Key, value);
            TimeSpan variableDuration = ComputeCreateDuration(flight.Key, value);

            lock (_gate)
            {
                claimed = Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) == 0;
                if (
                    claimed
                    && _disposed == 0
                    && flight.Epoch == _epoch
                    && _entries.TryGetValue(flight.Key, out Entry? entry)
                    && !Volatile.Read(ref entry.IsReady)
                    && ReferenceEquals(entry.Flight, flight)
                    && entry.Generation == flight.Generation
                )
                {
                    // Capture the exact node before any field is changed.  A
                    // hook may throw after Flight is cleared but before
                    // IsReady is published, so a key-only loading check would
                    // fail to remove this candidate.
                    publishedEntry = entry;
                    long timestamp = _timeProvider.GetTimestamp();
                    lock (entry.Sync)
                    {
                        entry.SetValue(value, _weakValues);
                        entry.Weight = weight;
                        entry.WriteTimestamp = timestamp;
                        entry.AccessTimestamp = timestamp;
                        entry.VariableTimestamp = timestamp;
                        entry.VariableDuration = variableDuration;
                        entry.VariableRevision++;
                        entry.PublicationRevision++;
                        publishedRevision = entry.PublicationRevision;
                        entry.Flight = null;
                        entry.SharedTask = _weakValues
                            ? null
                            : (flight as AsyncFlight)?.Completion.Task;
                        entry.PolicyToken = new WindowTinyLfuEnginePolicy.EngineEntryToken(
                            entry,
                            GetPolicyHash(flight.Key)
                        );
                        InvokeHook(_testHooks?.BeforeReadyPublish);
                        // Publish the complete snapshot last. Readers use an
                        // acquire read, so they cannot observe IsReady before
                        // PolicyToken/SharedTask and timestamps are initialized.
                        Volatile.Write(ref entry.IsReady, true);
                    }

                    publishedEntry = entry;

                    _policy.OnPublish(entry.PolicyToken, entry.Weight);
                    if (_expirationWheel is not null)
                    {
                        ulong normalizedNow = GetExpirationNowLocked();
                        AdvanceExpirationLocked(normalizedNow);
                        ScheduleExpirationNodeLocked(entry, normalizedNow);
                    }
                }

                if (claimed)
                {
                    RecordFlightResult(flight, CacheCounterKind.LoadSuccesses);
                }
            }

            if (!claimed)
            {
                evictionScope.Dispatch();
                RetireFlight(flight, underlyingCompleted: true);
                return;
            }

            RequestExpirationTimer();
        }
        catch (Exception exception)
        {
            evictionScope.Dispatch();
            if (claimed)
            {
                CompleteClaimedFailure(flight, exception, publishedEntry, publishedRevision);
            }
            else
            {
                CompleteFailure(flight, exception);
            }
            return;
        }

        evictionScope.Dispatch();
        CompletePromise(
            flight,
            static (asyncFlight, value) => asyncFlight.Completion.TrySetResult(value),
            value
        );
    }

    private void CompleteCancellation(Flight flight, OperationCanceledException exception)
    {
        if (flight.IsRefresh)
        {
            CompleteRefreshFailure(flight, exception);
            return;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed;
        lock (_gate)
        {
            claimed = Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) == 0;
            if (claimed)
            {
                RemoveCurrentEntryLocked(flight);
                RecordFlightResult(flight, CacheCounterKind.LoadCancellations);
            }
        }

        evictionScope.Dispatch();
        if (!claimed)
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        FailBulkFlight(flight, exception);
        CompletePromise(
            flight,
            static (asyncFlight, error) =>
                asyncFlight.Completion.TrySetCanceled(GetCancellationToken(error)),
            exception
        );
    }

    private void CompleteFailure(Flight flight, Exception exception)
    {
        if (flight.IsRefresh)
        {
            CompleteRefreshFailure(flight, exception);
            return;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool claimed;
        lock (_gate)
        {
            claimed = Interlocked.CompareExchange(ref flight.TerminalClaimed, 1, 0) == 0;
            if (claimed)
            {
                RemoveCurrentEntryLocked(flight);
                RecordFlightResult(flight, CacheCounterKind.LoadFailures);
            }
        }

        evictionScope.Dispatch();
        if (!claimed)
        {
            RetireFlight(flight, underlyingCompleted: true);
            return;
        }

        FailBulkFlight(flight, exception);
        CompletePromise(
            flight,
            static (current, error) => current.TrySetException(error),
            exception
        );
    }

    private void CompleteClaimedFailure(
        Flight flight,
        Exception exception,
        Entry? publishedEntry = null,
        long publishedRevision = 0
    )
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        // CompleteSuccess claims terminal ownership before policy and
        // expiration maintenance.  If one of those operations fails, the
        // generic failure path cannot claim the flight a second time and
        // would otherwise leave every waiter pending.  Remove only the exact
        // publication candidate.  A Set, refresh, expiry update, or a
        // replacement entry that committed after this candidate has a newer
        // identity/revision and must survive this late failure.
        lock (_gate)
        {
            if (
                publishedEntry is not null
                && publishedEntry.Epoch == _epoch
                && publishedEntry.Generation == flight.Generation
                && publishedEntry.PublicationRevision == publishedRevision
                && _entries.TryGetValue(flight.Key, out Entry? current)
                && ReferenceEquals(current, publishedEntry)
            )
            {
                RemoveCurrentEntryLocked(publishedEntry);
            }
            else
            {
                if (
                    publishedEntry is not null
                    && flight is AsyncFlight asyncFlight
                    && publishedEntry.Epoch == _epoch
                    && _entries.TryGetValue(flight.Key, out Entry? candidate)
                    && ReferenceEquals(candidate, publishedEntry)
                )
                {
                    // A newer refresh may have fenced this cold flight from
                    // rollback while the entry still points at the cold
                    // completion task.  The ready value is authoritative at
                    // this point, so replace only that exact failed promise
                    // with a completed snapshot task.  Normal successful
                    // publication and the stable Task identity contract are
                    // unchanged.
                    lock (publishedEntry.Sync)
                    {
                        if (
                            Volatile.Read(ref publishedEntry.IsReady)
                            && publishedEntry.Flight is null
                            && ReferenceEquals(
                                publishedEntry.SharedTask,
                                asyncFlight.Completion.Task
                            )
                        )
                        {
                            if (publishedEntry.TryGetValue(out TValue? liveValue))
                            {
                                publishedEntry.SharedTask = Task.FromResult(liveValue);
                            }
                        }
                    }
                }

                RemoveCurrentEntryLocked(flight);
            }
        }

        evictionScope.Dispatch();
        CompletePromise(
            flight,
            static (current, error) => current.TrySetException(error),
            exception
        );
    }

    private void CompletePromise<TError>(
        Flight flight,
        Action<AsyncFlight, TError> completion,
        TError error
    )
    {
        try
        {
            InvokeHook(_testHooks?.BeforeCompletion);
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    // Release the execution/reservation bookkeeping before making
                    // the shared promise observable.  A continuation may enqueue
                    // another load immediately after completion; it must not see a
                    // stale permit held by the old flight.  RetireFlight is still
                    // kept after the hook so Dispose can fence this promise while a
                    // controlled BeforeCompletion hook is paused.
                    RetireFlight(flight, underlyingCompleted: true);
                    completion(asyncFlight, error);
                    break;
                case SyncFlight syncFlight when error is Exception exception:
                    RetireFlight(flight, underlyingCompleted: true);
                    syncFlight.Set(exception);
                    break;
            }
        }
        catch (Exception hookException)
        {
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    asyncFlight.TrySetException(hookException);
                    break;
                case SyncFlight syncFlight:
                    syncFlight.Set(hookException);
                    break;
            }
        }
        finally
        {
            RetireFlight(flight, underlyingCompleted: true);
        }
    }

    private void CompletePromise(
        Flight flight,
        Action<AsyncFlight, TValue> completion,
        TValue value
    )
    {
        try
        {
            InvokeHook(_testHooks?.BeforeCompletion);
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    RetireFlight(flight, underlyingCompleted: true);
                    completion(asyncFlight, value);
                    break;
                case SyncFlight syncFlight:
                    RetireFlight(flight, underlyingCompleted: true);
                    syncFlight.Set(value);
                    break;
            }
        }
        catch (Exception hookException)
        {
            switch (flight)
            {
                case AsyncFlight asyncFlight:
                    asyncFlight.TrySetException(hookException);
                    break;
                case SyncFlight syncFlight:
                    syncFlight.Set(hookException);
                    break;
            }
        }
        finally
        {
            RetireFlight(flight, underlyingCompleted: true);
        }
    }

    private void RetireFlight(Flight flight, bool underlyingCompleted = false)
    {
        bool disposeSource = false;
        ITimer? timeoutTimer;
        CancellationTokenSource? workCancellation = null;
        lock (_gate)
        {
            if (flight.Retired != 0)
            {
                return;
            }

            if (underlyingCompleted)
            {
                Volatile.Write(ref flight.UnderlyingCompleted, 1);
            }

            if (
                flight.ExecutionStarted != 0
                && Volatile.Read(ref flight.UnderlyingCompleted) != 0
                && flight.ExecutionResourcesReleased == 0
            )
            {
                flight.ExecutionResourcesReleased = 1;
                _runningLoads--;
            }

            // A timeout may have completed the shared promise while its
            // cancellation callbacks are still running.  Keep the active
            // flight and reservation until both the user loader and the
            // cache-owned cancellation cleanup have reached a terminal state.
            if (
                Volatile.Read(ref flight.UnderlyingCompleted) == 0
                || (
                    Volatile.Read(ref flight.TimeoutCancellationStarted) != 0
                    && Volatile.Read(ref flight.CancellationCleanupCompleted) == 0
                )
            )
            {
                return;
            }

            flight.Retired = 1;
            RetireBulkFlight(flight);
            _activeFlights.Remove(flight);

            if (flight.ResourcesReleased == 0)
            {
                flight.ResourcesReleased = 1;
            }

            _reservedLoads--;
            timeoutTimer = flight.TimeoutTimer;
            flight.TimeoutTimer = null;
            if (Volatile.Read(ref flight.TimeoutCancellationStarted) == 0)
            {
                workCancellation = flight.WorkCancellation;
                flight.WorkCancellation = null;
            }
            flight.RefreshEntry = null;
            if (
                _disposed != 0
                && _shutdownCancellationCompleted != 0
                && _activeFlights.Count == 0
                && _shutdownSourceDisposed == 0
            )
            {
                _shutdownSourceDisposed = 1;
                disposeSource = true;
            }
        }

        if (disposeSource)
        {
            _shutdownCts.Dispose();
        }

        timeoutTimer?.Dispose();
        workCancellation?.Dispose();
    }

    private void ReplaceCurrentLocked(TKey key, Entry replacement)
    {
        if (_entries.TryGetValue(key, out Entry? existing))
        {
            RemoveCurrentEntryLocked(existing, RemovalCause.Replaced);
        }

        _entries[key] = replacement;
    }

    private void RemoveCurrentEntryLocked(Entry entry)
    {
        RemoveCurrentEntryLocked(entry, RemovalCause.Explicit, collected: false);
    }

    private RemovalNotification<TKey, TValue>? RemoveCurrentEntryLocked(Entry entry, bool collected)
    {
        return RemoveCurrentEntryLocked(
            entry,
            collected ? RemovalCause.Collected : RemovalCause.Explicit,
            collected
        );
    }

    private RemovalNotification<TKey, TValue>? RemoveCurrentEntryLocked(
        Entry entry,
        RemovalCause cause,
        bool collected = false
    )
    {
        if (!_entries.IsCurrent(entry))
        {
            return null;
        }

        RetireExpirationNodeLocked(entry);
        _entries.TryRemoveExact(entry);
        entry.Retired = true;
        Flight? refreshFlight = entry.RefreshFlight;
        if (refreshFlight is not null)
        {
            Volatile.Write(ref refreshFlight.PublishRevoked, 1);
            entry.RefreshFlight = null;
        }
        if (!Volatile.Read(ref entry.IsReady))
        {
            return null;
        }

        bool hasSynchronousEviction = QueueRemovalNotificationLocked(
            entry,
            cause,
            IsEvictionCause(cause),
            out RemovalNotification<TKey, TValue> synchronousEviction,
            collected
        );

        // Trusted ownership bookkeeping only. It may enqueue bounded work but
        // must never invoke a user disposer on this thread or throw.
        if (entry.TryGetValue(out TValue? retiredValue))
        {
            _onValueRetired?.Invoke(retiredValue);
        }

        if (entry.PolicyDetached)
        {
            entry.PolicyDetached = false;
        }
        else
        {
            _policy.OnRemove(entry.PolicyToken);
        }

        return hasSynchronousEviction ? synchronousEviction : null;
    }

    private void RemoveCurrentEntryLocked(Flight flight)
    {
        if (
            flight.Epoch == _epoch
            && _entries.TryGetValue(flight.Key, out Entry? current)
            && !Volatile.Read(ref current.IsReady)
            && ReferenceEquals(current.Flight, flight)
            && current.Generation == flight.Generation
        )
        {
            RemoveCurrentEntryLocked(current, RemovalCause.Explicit, collected: false);
        }
    }

    private RemovalNotification<TKey, TValue>? RemoveExpiredEntryLocked(Entry entry)
    {
        Flight? refreshFlight = entry.RefreshFlight;
        if (
            !_entries.IsCurrent(entry)
            || !Volatile.Read(ref entry.IsReady)
            || refreshFlight is null
            || !IsCurrentRefreshFlightLocked(entry, refreshFlight)
        )
        {
            return RemoveCurrentEntryLocked(entry, RemovalCause.Expired);
        }

        RetireExpirationNodeLocked(entry);
        if (entry.PolicyDetached)
        {
            return null;
        }

        _policy.OnRemove(entry.PolicyToken);
        entry.PolicyDetached = true;
        return null;
    }

    private bool TryReadReady(TKey key, out TValue value) =>
        TryReadReady(key, out value, out _, out _, out _);

    private bool TryReadReady(TKey key, out TValue value, out Task<TValue>? sharedTask) =>
        TryReadReady(key, out value, out sharedTask, out _, out _);

    private bool TryReadReady(
        TKey key,
        out TValue value,
        out Task<TValue>? sharedTask,
        out Entry? readyEntry,
        out bool refreshEligible
    )
    {
        sharedTask = null;
        readyEntry = null;
        refreshEligible = false;
        if (!_entries.TryGetValue(key, out Entry? entry) || !Volatile.Read(ref entry.IsReady))
        {
            value = default!;
            return false;
        }

        object? policyToken = null;
        long variableTimestamp = 0;
        long variableRevision = 0;
        TimeSpan variableDuration = TimeSpan.MaxValue;
        bool preserveForRefresh = false;
        bool valueCollected = false;
        lock (entry.Sync)
        {
            // Sample the monotonic clock while holding the entry snapshot lock.
            // This keeps access/write timestamps and the freshness decision from
            // observing a torn value during replacement.
            long now = _timeProvider.GetTimestamp();
            if (!Volatile.Read(ref entry.IsReady) || IsExpired(entry, now))
            {
                value = default!;
                preserveForRefresh =
                    entry.RefreshFlight is not null
                    && IsCurrentRefreshFlightSnapshot(entry, entry.RefreshFlight);
            }
            else
            {
                TouchWithoutLock(entry, now);
                if (!entry.TryGetValue(out TValue? liveValue))
                {
                    preserveForRefresh = false;
                    valueCollected = true;
                    value = default!;
                }
                else
                {
                    value = liveValue!;
                    policyToken = entry.PolicyToken;
                    sharedTask = entry.SharedTask;
                    variableTimestamp = entry.VariableTimestamp;
                    variableRevision = entry.VariableRevision;
                    variableDuration = _expiry is null
                        ? TimeSpan.MaxValue
                        : GetRemainingDuration(entry, now, ExpirationKind.Variable);
                    readyEntry = entry;
                    refreshEligible = IsRefreshEligibleLocked(entry, now);
                }
            }
        }

        if (policyToken is null)
        {
            if (preserveForRefresh)
            {
                return false;
            }

            // A nested lookup owns its expiration notification. It must not enqueue
            // into a surrounding loader's scope and defer delivery until that loader returns.
            using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
            RemovalNotification<TKey, TValue>? evictionNotification;
            lock (_gate)
            {
                evictionNotification = valueCollected
                    ? RemoveCurrentEntryLocked(entry, collected: true)
                    : RemoveExpiredEntryLocked(entry);
            }
            if (evictionNotification is { } notification)
            {
                DispatchSynchronousEviction(notification);
            }
            return false;
        }

        ApplyReadExpiryUpdate(
            entry,
            key,
            value,
            variableTimestamp,
            variableRevision,
            variableDuration
        );
        RecordHit();
        // Access recording is a bounded, lossy policy event.  It intentionally
        // runs after releasing the entry lock and never takes the engine gate.
        _policy.OnAccess(policyToken);
        return true;
    }

    private void ApplyReadExpiryUpdate(
        Entry entry,
        TKey key,
        TValue value,
        long variableTimestamp,
        long variableRevision,
        TimeSpan currentDuration
    )
    {
        if (_expiry is null)
        {
            return;
        }

        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();

        // The callback is user code and is deliberately invoked without either
        // the engine gate or the entry snapshot lock.  It may re-enter the
        // cache, block, or throw without corrupting the data plane.
        TimeSpan nextDuration = NormalizeVariableDuration(
            _expiry.ExpireAfterRead(key, value, currentDuration)
        );

        bool changed = false;
        bool reschedule = false;
        lock (_gate)
        {
            if (
                _disposed == 0
                && _entries.TryGetValue(key, out Entry? current)
                && ReferenceEquals(current, entry)
            )
            {
                lock (entry.Sync)
                {
                    if (
                        Volatile.Read(ref entry.IsReady)
                        && entry.VariableTimestamp == variableTimestamp
                        && entry.VariableRevision == variableRevision
                        && !entry.Retired
                    )
                    {
                        long commitTimestamp = _timeProvider.GetTimestamp();
                        if (IsExpired(entry, commitTimestamp))
                        {
                            RemoveExpiredEntryLocked(entry);
                        }
                        else
                        {
                            // Use the completion timestamp so a slow callback
                            // cannot extend a value from an old read instant.
                            entry.VariableTimestamp = commitTimestamp;
                            entry.VariableDuration = nextDuration;
                            entry.VariableRevision++;
                            if (nextDuration <= TimeSpan.Zero)
                            {
                                RemoveExpiredEntryLocked(entry);
                            }
                            else
                            {
                                reschedule = true;
                            }
                        }

                        changed = true;
                    }
                }

                if (reschedule && _expirationWheel is not null)
                {
                    ulong normalizedNow = GetExpirationNowLocked();
                    AdvanceExpirationLocked(normalizedNow);
                    ScheduleExpirationNodeLocked(entry, normalizedNow);
                }
            }
        }

        evictionScope.Dispatch();

        if (!changed)
        {
            return;
        }

        // The variable revision is visible before this hook.  Keeping the
        // seam outside the engine/entry locks lets race tests release a
        // competing read without introducing a production lock edge.
        InvokeHook(_testHooks?.AfterReadExpiryUpdate);
        RequestExpirationTimer();
    }

    private void RemoveEvictedEntry(object entryToken)
    {
        if (entryToken is not Entry entry)
        {
            return;
        }

        RemovalCause cause = _weigher is null ? RemovalCause.Size : RemovalCause.Weight;
        RemoveCurrentEntryLocked(entry, cause);
        RecordCounter(CacheCounterKind.Evictions);
        RecordCounter(CacheCounterKind.EvictedWeight, entry.Weight);
    }

    private static bool IsEvictionCause(RemovalCause cause) =>
        cause
            is RemovalCause.Size
                or RemovalCause.Weight
                or RemovalCause.Expired
                or RemovalCause.Collected
                or RemovalCause.MemoryPressure;

    private long ComputeWeight(TKey key, TValue value)
    {
        if (_weigher is null)
        {
            return 1;
        }

        long weight = _weigher(key, value);
        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A value weight cannot be negative."
            );
        }

        return weight;
    }

    private uint GetPolicyHash(TKey key)
    {
        return unchecked((uint)Comparer.GetHashCode(key));
    }

    private static Task<TValue> WaitForSyncFlight(
        SyncFlight flight,
        CancellationToken cancellationToken
    )
    {
        return cancellationToken.CanBeCanceled
            ? flight.Completion.Task.WaitAsync(cancellationToken)
            : flight.Completion.Task;
    }

    private async Task CancelShutdownAsync()
    {
        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cancellation callbacks are user code.  Observe their failures and
            // leave cache state intact; DisposeAsync never waits on them.
        }
        finally
        {
            bool disposeSource = false;
            lock (_gate)
            {
                _shutdownCancellationCompleted = 1;
                if (_activeFlights.Count == 0 && _shutdownSourceDisposed == 0)
                {
                    _shutdownSourceDisposed = 1;
                    disposeSource = true;
                }
            }

            if (disposeSource)
            {
                _shutdownCts.Dispose();
            }
        }
    }

    private static ITimer[] DetachFlightTimeoutTimersLocked(Flight[] flights)
    {
        if (flights.Length == 0)
        {
            return [];
        }

        var timers = new List<ITimer>(flights.Length);
        foreach (Flight flight in flights)
        {
            ITimer? timer = flight.TimeoutTimer;
            flight.TimeoutTimer = null;
            if (timer is not null)
            {
                timers.Add(timer);
            }
        }

        return [.. timers];
    }

    private static void DisposeFlightTimeoutTimers(ITimer[] timers)
    {
        foreach (ITimer timer in timers)
        {
            timer.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(LoadingCache));
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(LoadingCache));
    }

    private static void InvokeHook(Action? hook)
    {
        hook?.Invoke();
    }

    private void RecordHit()
    {
        RecordCounter(CacheCounterKind.Hits);
    }

    private void RecordMiss()
    {
        RecordCounter(CacheCounterKind.Misses);
    }

    private void RecordCoalescedWaiter()
    {
        RecordCounter(CacheCounterKind.CoalescedWaiters);
    }

    private static CancellationToken GetCancellationToken(OperationCanceledException exception)
    {
        return exception.CancellationToken.IsCancellationRequested
            ? exception.CancellationToken
            : new CancellationToken(true);
    }
}
