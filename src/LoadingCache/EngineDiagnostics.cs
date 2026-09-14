using System.Diagnostics.Metrics;
using LoadingCache.Diagnostics;
using LoadingCache.Maintenance;
using LoadingCache.Notifications;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private StripedCacheCounters? _counters;
    private EngineMetrics? _metrics;
    private BoundedNotificationDispatcher<ListenerEvent>? _listenerDispatcher;
    private Action<RemovalNotification<TKey, TValue>>? _removalListener;
    private Action<RemovalNotification<TKey, TValue>>? _evictionListener;
    private readonly object _notificationGate = new();
    private readonly Queue<ListenerEvent> _pendingNotifications = [];
    private int _notificationFlushScheduled;
    private int _notificationsDisposed;
    private int _notificationCapacity;
    private long _pendingDroppedFull;
    private long _pendingDroppedSchedule;
    private long _pendingDroppedShutdown;

    [ThreadStatic]
    private static EvictionScope? _currentEvictionScope;

    private void InitializeDiagnostics(CacheEngineOptions<TKey, TValue> options)
    {
        if (_recordStatistics || options.EnableMetrics)
        {
            _counters = new StripedCacheCounters();
        }

        _removalListener = options.RemovalListener;
        _evictionListener = options.EvictionListener;
        if (_removalListener is not null || _evictionListener is not null)
        {
            if (options.NotificationCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "NotificationCapacity must be positive."
                );
            }

            _notificationCapacity = options.NotificationCapacity;
            _listenerDispatcher = new BoundedNotificationDispatcher<ListenerEvent>(
                options.NotificationCapacity,
                DispatchListenerEvent,
                options.NotificationScheduler
            );
        }

        if (options.EnableMetrics)
        {
            _metrics = new EngineMetrics(
                options.MetricsName,
                GetMetricsSnapshot,
                () => Volatile.Read(ref _runningLoads)
            );
        }
    }

    // Unlike the executing-load gauge, this includes reserved automatic refreshes which have
    // not started, and revoked flights from an older mapping or epoch. A caller must stop new
    // producers before using an empty registry as a quiescence boundary.
    internal bool HasActiveFlights
    {
        get
        {
            lock (_gate)
            {
                return _activeFlights.Count != 0;
            }
        }
    }

    private CacheStatistics GetStatisticsSnapshot(bool exposeCounters)
    {
        CacheCounterSnapshot counters = _counters?.Snapshot() ?? default;
        ReadBufferStatistics readBuffer = _policy.GetReadBufferStatistics();
        WriteBufferStatistics writeBuffer = _policy.GetWriteBufferStatistics();
        MaintenanceStatistics maintenance = _maintenanceCoordinator.GetStatistics();
        long listenerDrops = exposeCounters ? counters[CacheCounterKind.ListenerDrops] : 0;
        if (exposeCounters && _listenerDispatcher is not null)
        {
            listenerDrops = SaturatingAdd(
                listenerDrops,
                _listenerDispatcher.GetStatistics().Dropped
            );
        }

        return new CacheStatistics(
            exposeCounters ? counters[CacheCounterKind.Hits] : 0,
            exposeCounters ? counters[CacheCounterKind.Misses] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadsStarted] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadSuccesses] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadFailures] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadCancellations] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadTimeouts] : 0,
            exposeCounters ? counters[CacheCounterKind.CoalescedWaiters] : 0,
            exposeCounters ? counters[CacheCounterKind.Evictions] : 0,
            Volatile.Read(ref _runningLoads),
            exposeCounters ? counters[CacheCounterKind.TotalLoadTimeTicks] : 0,
            exposeCounters ? counters[CacheCounterKind.BulkLoads] : 0,
            exposeCounters ? counters[CacheCounterKind.RefreshAttempts] : 0,
            exposeCounters ? counters[CacheCounterKind.RefreshSuccesses] : 0,
            exposeCounters ? counters[CacheCounterKind.RefreshFailures] : 0,
            exposeCounters ? counters[CacheCounterKind.RefreshSkipped] : 0,
            exposeCounters ? counters[CacheCounterKind.RefreshBackoff] : 0,
            exposeCounters ? counters[CacheCounterKind.LoadRejections] : 0,
            exposeCounters ? counters[CacheCounterKind.EvictedWeight] : 0,
            exposeCounters ? counters[CacheCounterKind.Collected] : 0,
            exposeCounters ? counters[CacheCounterKind.ExplicitRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.ReplacedRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.ExpiredRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.ClearedRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.SizeRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.WeightRemovals] : 0,
            exposeCounters ? counters[CacheCounterKind.MemoryPressureRemovals] : 0,
            listenerDrops,
            exposeCounters ? counters[CacheCounterKind.ListenerFailures] : 0,
            SaturatingAdd(readBuffer.Queued, writeBuffer.Queued),
            exposeCounters ? readBuffer.Dropped : 0,
            exposeCounters ? maintenance.ScheduleRejections : 0,
            exposeCounters
                ? SaturatingAdd(
                    maintenance.DrainFaults,
                    counters[CacheCounterKind.TimerDisposalFailures]
                )
                : 0,
            writeBuffer.Queued,
            exposeCounters ? writeBuffer.Full : 0
        );
    }

    private CacheStatistics GetMetricsSnapshot() => GetStatisticsSnapshot(exposeCounters: true);

    internal CacheNotificationStatistics GetNotificationStatistics()
    {
        NotificationDispatchStatistics dispatcher = _listenerDispatcher?.GetStatistics() ?? default;
        int pending;
        lock (_notificationGate)
        {
            pending = _pendingNotifications.Count;
        }

        int queued =
            pending >= int.MaxValue - dispatcher.Queued
                ? int.MaxValue
                : pending + dispatcher.Queued;
        long enqueued = SaturatingAdd(dispatcher.Enqueued, pending);

        return new CacheNotificationStatistics(
            queued,
            Volatile.Read(ref _notificationsDisposed) != 0 || dispatcher.IsDisposed,
            dispatcher.HandlerRunning,
            enqueued,
            dispatcher.Invoked,
            dispatcher.Delivered,
            dispatcher.HandlerFailures,
            dispatcher.DroppedFull + Interlocked.Read(ref _pendingDroppedFull),
            dispatcher.DroppedSchedule + Interlocked.Read(ref _pendingDroppedSchedule),
            dispatcher.DroppedShutdown + Interlocked.Read(ref _pendingDroppedShutdown),
            dispatcher.ScheduleRejections
        );
    }

    private void RecordCounter(CacheCounterKind kind, long delta = 1)
    {
        _counters?.Add(kind, delta);
    }

    private void RecordFlightStarted(Flight flight)
    {
        if (_counters is null)
        {
            return;
        }

        flight.LoadStartTimestamp = _timeProvider.GetTimestamp();
        Volatile.Write(ref flight.StatisticsStarted, 1);
        RecordCounter(CacheCounterKind.LoadsStarted);
        if (flight.IsRefresh)
        {
            RecordCounter(CacheCounterKind.RefreshAttempts);
        }
    }

    private bool RecordFlightResult(Flight flight, CacheCounterKind outcome)
    {
        if (!TryRecordFlightDuration(flight))
        {
            return false;
        }

        RecordCounter(outcome);
        return true;
    }

    private bool TryRecordFlightDuration(Flight flight)
    {
        if (_counters is null)
        {
            return false;
        }

        if (Volatile.Read(ref flight.StatisticsStarted) == 0)
        {
            return false;
        }

        if (Interlocked.Exchange(ref flight.StatisticsRecorded, 1) != 0)
        {
            return false;
        }

        try
        {
            long now = _timeProvider.GetTimestamp();
            TimeSpan elapsed = _timeProvider.GetElapsedTime(flight.LoadStartTimestamp, now);
            if (elapsed >= TimeSpan.Zero)
            {
                RecordCounter(CacheCounterKind.TotalLoadTimeTicks, elapsed.Ticks);
            }
        }
        catch
        {
            // A failing diagnostic clock must not alter the flight result.
        }

        return true;
    }

    /// <summary>
    /// Creates the exact removal notification while the entry is still owned by the engine gate.
    /// A policy eviction is captured by the current operation-owned scope, or returned to the
    /// caller for lock-outside dispatch when no scope is active. A removal-listener copy is
    /// admitted independently to the bounded asynchronous dispatcher. This keeps reliable
    /// eviction delivery separate from the intentionally lossy removal notification queue.
    /// </summary>
    private bool QueueRemovalNotificationLocked(
        Entry entry,
        RemovalCause cause,
        bool eviction,
        out RemovalNotification<TKey, TValue> synchronousEviction,
        bool omitValue = false
    )
    {
        synchronousEviction = default;
        RecordRemovalCounter(cause);

        if (entry.RemovalNotified)
        {
            return false;
        }

        if (_listenerDispatcher is null)
        {
            return false;
        }

        entry.RemovalNotified = true;
        bool hasKey = entry.TryGetKey(out TKey? key);
        TValue? value = default;
        bool hasValue = !omitValue && entry.TryGetValue(out value);
        var notification = new RemovalNotification<TKey, TValue>(
            hasKey ? key : default,
            hasValue ? value : default,
            cause,
            entry.Weight
        );

        if (eviction && _evictionListener is not null)
        {
            EvictionScope? scope = _currentEvictionScope;
            if (scope is not null && ReferenceEquals(scope.Owner, this))
            {
                // The removal listener has an independent bounded delivery path.
                // Reliable eviction delivery must not depend on its queue.
                if (_removalListener is not null)
                {
                    QueueListenerEventLocked(new ListenerEvent(notification, IsEviction: false));
                }

                scope.Add(notification);
                return false;
            }

            synchronousEviction = notification;
            if (_removalListener is not null)
            {
                QueueListenerEventLocked(new ListenerEvent(notification, IsEviction: false));
            }

            return true;
        }

        QueueListenerEventLocked(new ListenerEvent(notification, eviction));
        return false;
    }

    /// <summary>
    /// Invokes one captured eviction callback after the caller has released engine/entry locks.
    /// Callback failures are observed in cache statistics and do not alter authoritative state.
    /// </summary>
    internal void DispatchSynchronousEviction(RemovalNotification<TKey, TValue> notification)
    {
        Action<RemovalNotification<TKey, TValue>>? listener = _evictionListener;
        if (listener is null)
        {
            return;
        }

        try
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                listener(notification);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    listener(notification);
                }
            }
        }
        catch
        {
            RecordCounter(CacheCounterKind.ListenerFailures);
        }
    }

    private SynchronousEvictionScope BeginSynchronousEvictionScope(bool completePolicyWrites = true)
    {
        if (_evictionListener is null)
        {
            return new SynchronousEvictionScope(completePolicyWrites ? this : null, null);
        }

        EvictionScope? previous = _currentEvictionScope;
        var scope = new EvictionScope(this, previous);
        _currentEvictionScope = scope;
        return new SynchronousEvictionScope(completePolicyWrites ? this : null, scope);
    }

    private sealed class EvictionScope(CacheEngine<TKey, TValue> owner, EvictionScope? parent)
    {
        private List<RemovalNotification<TKey, TValue>>? _notifications;

        internal CacheEngine<TKey, TValue> Owner { get; } = owner;

        internal EvictionScope? Parent { get; } = parent;

        internal void Add(RemovalNotification<TKey, TValue> notification) =>
            (_notifications ??= []).Add(notification);

        internal void Dispatch()
        {
            bool restore = ReferenceEquals(_currentEvictionScope, this);
            if (restore)
            {
                _currentEvictionScope = Parent;
            }

            if (_notifications is { Count: > 0 } notifications)
            {
                _notifications = null;
                foreach (RemovalNotification<TKey, TValue> notification in notifications)
                {
                    Owner.DispatchSynchronousEviction(notification);
                }
            }

            if (restore && ReferenceEquals(_currentEvictionScope, Parent))
            {
                _currentEvictionScope = this;
            }
        }
    }

    private readonly struct SynchronousEvictionScope(
        CacheEngine<TKey, TValue>? mutationOwner,
        EvictionScope? scope
    ) : IDisposable
    {
        internal void Dispatch()
        {
            mutationOwner?.CompletePolicyWriteBoundary();
            scope?.Dispatch();
        }

        public void Dispose()
        {
            mutationOwner?.CompletePolicyWriteBoundary();
            if (scope is null)
            {
                return;
            }

            if (ReferenceEquals(_currentEvictionScope, scope))
            {
                _currentEvictionScope = scope.Parent;
            }

            // Dispose is the final safety net for exceptional/early-return paths. The scope is
            // detached before invoking user code, so callback re-entry cannot append to a batch
            // that is currently being drained.
            scope.Dispatch();
        }
    }

    private void QueueReplacementNotificationLocked(
        Flight flight,
        RefreshPublicationSnapshot previousSnapshot
    )
    {
        if (Interlocked.Exchange(ref flight.ReplacementNotificationRecorded, 1) != 0)
        {
            return;
        }

        QueueReplacementNotificationLocked(
            flight.Key,
            previousSnapshot.Value,
            previousSnapshot.Weight
        );
    }

    private void QueueReplacementNotificationLocked(TKey key, TValue value, long weight)
    {
        RecordRemovalCounter(RemovalCause.Replaced);
        if (_listenerDispatcher is null)
        {
            return;
        }

        var notification = new RemovalNotification<TKey, TValue>(
            key,
            value,
            RemovalCause.Replaced,
            weight
        );
        QueueListenerEventLocked(new ListenerEvent(notification, IsEviction: false));
    }

    private void QueueListenerEventLocked(ListenerEvent listenerEvent)
    {
        bool schedule = false;
        lock (_notificationGate)
        {
            if (Volatile.Read(ref _notificationsDisposed) != 0)
            {
                RecordCounter(CacheCounterKind.ListenerDrops);
                AddPendingDrop(ref _pendingDroppedShutdown);
                return;
            }

            if (_pendingNotifications.Count >= _notificationCapacity)
            {
                RecordCounter(CacheCounterKind.ListenerDrops);
                AddPendingDrop(ref _pendingDroppedFull);
                return;
            }

            _pendingNotifications.Enqueue(listenerEvent);
            if (_notificationFlushScheduled == 0)
            {
                _notificationFlushScheduled = 1;
                schedule = true;
            }
        }

        if (!schedule)
        {
            return;
        }

        // This is only a lock-free handoff. It never runs user code inline,
        // which keeps eager custom notification schedulers outside _gate.
        if (
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => ((CacheEngine<TKey, TValue>)state).FlushNotificationHandoff(),
                this,
                preferLocal: true
            )
        )
        {
            return;
        }

        lock (_notificationGate)
        {
            _notificationFlushScheduled = 0;
            long dropped = _pendingNotifications.Count;
            _pendingNotifications.Clear();
            if (dropped == 0)
            {
                return;
            }

            RecordCounter(CacheCounterKind.ListenerDrops, dropped);
            AddPendingDrop(ref _pendingDroppedSchedule, dropped);
        }
    }

    private void RecordRemovalCounter(RemovalCause cause)
    {
        CacheCounterKind? counter = cause switch
        {
            RemovalCause.Explicit => CacheCounterKind.ExplicitRemovals,
            RemovalCause.Replaced => CacheCounterKind.ReplacedRemovals,
            RemovalCause.Expired => CacheCounterKind.ExpiredRemovals,
            RemovalCause.Cleared => CacheCounterKind.ClearedRemovals,
            RemovalCause.Size => CacheCounterKind.SizeRemovals,
            RemovalCause.Weight => CacheCounterKind.WeightRemovals,
            RemovalCause.MemoryPressure => CacheCounterKind.MemoryPressureRemovals,
            RemovalCause.Collected => CacheCounterKind.Collected,
            _ => null,
        };

        if (counter.HasValue)
        {
            RecordCounter(counter.Value);
        }
    }

    private void FlushNotificationHandoff()
    {
        while (true)
        {
            ListenerEvent listenerEvent;
            lock (_notificationGate)
            {
                if (_pendingNotifications.Count == 0)
                {
                    _notificationFlushScheduled = 0;
                    return;
                }

                listenerEvent = _pendingNotifications.Dequeue();
            }

            if (Volatile.Read(ref _notificationsDisposed) != 0)
            {
                RecordCounter(CacheCounterKind.ListenerDrops);
                AddPendingDrop(ref _pendingDroppedShutdown);
                continue;
            }

            // The dispatcher is authoritative for drops after handoff.  Its
            // saturating counters are folded into CacheStatistics and exposed
            // with their individual causes by the notification snapshot;
            // recording a second engine-side drop here would double-count the
            // same event.
            _ = _listenerDispatcher!.TryEnqueue(listenerEvent);
        }
    }

    private void DispatchListenerEvent(ListenerEvent listenerEvent)
    {
        Exception? firstFailure = null;
        if (listenerEvent.IsEviction)
        {
            try
            {
                _evictionListener?.Invoke(listenerEvent.Notification);
            }
            catch (Exception exception)
            {
                RecordCounter(CacheCounterKind.ListenerFailures);
                firstFailure = exception;
            }
        }

        try
        {
            _removalListener?.Invoke(listenerEvent.Notification);
        }
        catch (Exception exception)
        {
            RecordCounter(CacheCounterKind.ListenerFailures);
            firstFailure ??= exception;
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private static void AddPendingDrop(ref long counter, long delta = 1)
    {
        while (true)
        {
            long current = Volatile.Read(ref counter);
            long next = delta >= long.MaxValue - current ? long.MaxValue : current + delta;
            if (Interlocked.CompareExchange(ref counter, next, current) == current)
            {
                return;
            }
        }
    }

    private void DisposeDiagnostics()
    {
        if (Interlocked.Exchange(ref _notificationsDisposed, 1) == 0)
        {
            lock (_notificationGate)
            {
                long dropped = _pendingNotifications.Count;
                _pendingNotifications.Clear();
                if (dropped != 0)
                {
                    RecordCounter(CacheCounterKind.ListenerDrops, dropped);
                    AddPendingDrop(ref _pendingDroppedShutdown, dropped);
                }
            }

            _listenerDispatcher?.Dispose();
        }

        _metrics?.Dispose();
    }

    private readonly record struct ListenerEvent(
        RemovalNotification<TKey, TValue> Notification,
        bool IsEviction
    );

    private sealed class EngineMetrics : IDisposable
    {
        private readonly Meter _meter;
        private readonly string _cacheName;

        internal EngineMetrics(
            string? name,
            Func<CacheStatistics> snapshot,
            Func<long> inFlightLoads
        )
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(inFlightLoads);
            _cacheName = string.IsNullOrWhiteSpace(name) ? "default" : name;
            _meter = new Meter(CacheMetrics.MeterName, CacheMetrics.MeterVersion);

            try
            {
                _meter.CreateObservableCounter(
                    "loadingcache.requests",
                    () =>
                    {
                        CacheStatistics values = snapshot();
                        return new Measurement<long>[]
                        {
                            new(values.Hits, Tags("hit")),
                            new(values.Misses, Tags("miss")),
                        };
                    },
                    unit: "{request}",
                    description: "Cache requests by outcome."
                );
                _meter.CreateObservableCounter(
                    "loadingcache.loads",
                    () =>
                    {
                        CacheStatistics values = snapshot();
                        return new Measurement<long>[]
                        {
                            new(values.LoadsStarted, Tags("started")),
                            new(values.LoadSuccesses, Tags("success")),
                            new(values.LoadFailures, Tags("failure")),
                            new(values.LoadCancellations, Tags("cancelled")),
                            new(values.LoadTimeouts, Tags("timeout")),
                        };
                    },
                    unit: "{load}",
                    description: "Cache loader invocations by outcome."
                );
                _meter.CreateObservableGauge(
                    "loadingcache.inflight",
                    () =>
                        new Measurement<long>(
                            inFlightLoads(),
                            new KeyValuePair<string, object?>("cache.name", _cacheName)
                        ),
                    unit: "{load}",
                    description: "Current in-flight cache loads."
                );
                _meter.CreateObservableCounter(
                    "loadingcache.removals",
                    () =>
                    {
                        CacheStatistics values = snapshot();
                        return new Measurement<long>[]
                        {
                            new(values.ExplicitRemovals, CauseTags("explicit")),
                            new(values.ReplacedRemovals, CauseTags("replaced")),
                            new(values.ExpiredRemovals, CauseTags("expired")),
                            new(values.SizeRemovals, CauseTags("size")),
                            new(values.WeightRemovals, CauseTags("weight")),
                            new(values.ClearedRemovals, CauseTags("cleared")),
                            new(values.MemoryPressureRemovals, CauseTags("memory-pressure")),
                            new(values.Collected, CauseTags("collected")),
                        };
                    },
                    unit: "{removal}",
                    description: "Cache removals by cause."
                );
                _meter.CreateObservableCounter<double>(
                    "loadingcache.load.duration",
                    () =>
                    {
                        CacheStatistics values = snapshot();
                        return
                        [
                            new Measurement<double>(
                                values.TotalLoadTime.TotalSeconds,
                                new KeyValuePair<string, object?>("cache.name", _cacheName)
                            ),
                        ];
                    },
                    unit: "s",
                    description: "Aggregate cache loader duration."
                );
                _meter.CreateObservableGauge(
                    "loadingcache.writes.pending",
                    () =>
                        new Measurement<long>(
                            snapshot().WriteBufferBacklog,
                            new KeyValuePair<string, object?>("cache.name", _cacheName)
                        ),
                    unit: "{event}",
                    description: "Reliable policy writes awaiting maintenance."
                );
                _meter.CreateObservableCounter(
                    "loadingcache.writes.pressure",
                    () =>
                        new Measurement<long>(
                            snapshot().WriteBufferPressure,
                            new KeyValuePair<string, object?>("cache.name", _cacheName)
                        ),
                    unit: "{encounter}",
                    description: "Full write-buffer encounters requiring producer assistance."
                );
            }
            catch
            {
                _meter.Dispose();
                throw;
            }
        }

        private KeyValuePair<string, object?>[] Tags(string value) =>
            [new("cache.name", _cacheName), new("outcome", value)];

        private KeyValuePair<string, object?>[] CauseTags(string value) =>
            [new("cache.name", _cacheName), new("cause", value)];

        public void Dispose() => _meter.Dispose();
    }

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;
}
