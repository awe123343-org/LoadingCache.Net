using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.StressTests;

/// <summary>Exercises true bulk loading, weak references, and listeners under concurrent traffic.</summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class FeatureCombinationStabilityTests
{
    private const int Seed = 20260914;
    private const int Workers = 8;
    private const int OperationsPerBatch = 32;
    private const int Maximum = 64;
    private const int LoadLimit = 4;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);
    private static readonly string[] Modes =
    [
        "async-bulk-listeners",
        "async-bulk-weak-keys-listeners",
        "sync-bulk-weak-values-listeners",
    ];

    /// <summary>Rotates three feature combinations while checking ownership and resource bounds.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task BulkWeakReferencesAndListenersRemainConsistent(bool statistics)
    {
        string? configured = Environment.GetEnvironmentVariable(
            "LOADINGCACHE_FEATURE_SOAK_SECONDS"
        );
        int seconds = configured is null ? 5 : int.Parse(configured, CultureInfo.InvariantCulture);
        seconds.Should().BeInRange(1, 86_400);
        string directory =
            Environment.GetEnvironmentVariable("LOADINGCACHE_SOAK_OUTPUT")
            ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "soak");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(
            directory,
            $"features-{Environment.Version}-stats-{statistics}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl"
        );
        await using var log = new StreamWriter(output);
        log.AutoFlush = true;
        var elapsed = Stopwatch.StartNew();
        var random = Enumerable
            .Range(0, Workers)
            .Select(worker => new Random(unchecked(Seed * 397 + worker)))
            .ToArray();
        var traces = Enumerable.Range(0, Workers).Select(_ => new TraceEntry[128]).ToArray();
        long[] positions = new long[Workers];
        long[] outcomes = new long[4];
        int batch = 0;
        int cycle = 0;
        TimeSpan interval = TimeSpan.FromSeconds(Math.Min(30, seconds / 3.0));
        TimeSpan nextRotation = interval;
        TimeSpan nextProgress = TimeSpan.Zero;
        FeatureCache[] caches = CreateCaches(statistics, cycle);
        Task[] jobs = [];
        Write(
            log,
            new
            {
                Event = "start",
                Seed,
                Workers,
                OperationsPerBatch,
                Maximum,
                LoadLimit,
                statistics,
                seconds,
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                CoreModule = typeof(CacheBuilder).Assembly.ManifestModule.ModuleVersionId,
                TestModule = typeof(FeatureCombinationStabilityTests)
                    .Assembly
                    .ManifestModule
                    .ModuleVersionId,
                Modes,
                RetentionScope = "GC measurements are process-wide observations; this test is not a heap retention proof.",
            }
        );
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                FeatureCache[] current = caches;
                int currentCycle = cycle;
                jobs =
                [
                    .. Enumerable
                        .Range(0, Workers)
                        .Select(worker =>
                            Task.Run(async () =>
                            {
                                for (int index = 0; index < OperationsPerBatch; index++)
                                {
                                    int mode = random[worker].Next(current.Length);
                                    int key = random[worker].Next(128);
                                    int kind = random[worker].Next(100);
                                    long position = positions[worker]++;
                                    traces[worker][position % 128] = new TraceEntry(
                                        position,
                                        currentCycle,
                                        mode,
                                        key,
                                        kind
                                    );
                                    try
                                    {
                                        await OperateAsync(current[mode], key, kind)
                                            .ConfigureAwait(false);
                                        Interlocked.Increment(ref outcomes[0]);
                                    }
                                    catch (CacheLoadRejectedException)
                                    {
                                        Interlocked.Increment(ref outcomes[1]);
                                    }
                                    catch (ControlledLoaderFailure)
                                    {
                                        Interlocked.Increment(ref outcomes[2]);
                                    }
                                    catch (OperationCanceledException) when (kind < 5)
                                    {
                                        Interlocked.Increment(ref outcomes[3]);
                                    }
                                }
                            })
                        ),
                ];
                await Task.WhenAll(jobs).WaitAsync(Watchdog).ConfigureAwait(false);
                batch++;
                foreach (FeatureCache cache in caches)
                {
                    await cache.AssertQuiescentAsync().ConfigureAwait(false);
                }
                if (batch % 16 == 0)
                {
                    foreach (FeatureCache cache in caches)
                        cache.RotateKeys();
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Forced,
                        blocking: true,
                        compacting: false
                    );
                    GC.WaitForPendingFinalizers();
                    foreach (FeatureCache cache in caches)
                    {
                        cache.Engine.CleanUp();
                        await cache.AssertQuiescentAsync().ConfigureAwait(false);
                    }
                }
                if (elapsed.Elapsed >= nextProgress)
                {
                    Progress(log, "progress", elapsed, batch, cycle, caches, outcomes);
                    nextProgress = elapsed.Elapsed + TimeSpan.FromSeconds(1);
                }
                if (elapsed.Elapsed < nextRotation)
                    continue;
                foreach (FeatureCache cache in caches)
                    await cache.DisposeAsync().ConfigureAwait(false);
                Progress(log, "retired", elapsed, batch, cycle, caches, outcomes);
                caches = CreateCaches(statistics, ++cycle);
                nextRotation = elapsed.Elapsed + interval;
            }
            Progress(log, "workload-complete", elapsed, batch, cycle, caches, outcomes);
        }
        catch (Exception exception)
        {
            Write(
                log,
                new
                {
                    Event = "failed",
                    batch,
                    cycle,
                    Error = exception.ToString(),
                    positions,
                    traces = traces.Select(worker =>
                        worker.Select(entry => new
                        {
                            entry.Position,
                            entry.Cycle,
                            entry.Mode,
                            Key = entry.KeyId,
                            entry.Kind,
                        })
                    ),
                }
            );
            throw;
        }
        finally
        {
            try
            {
                await Task.WhenAll(jobs).WaitAsync(Watchdog).ConfigureAwait(false);
            }
            finally
            {
                foreach (FeatureCache cache in caches)
                    await cache.DisposeAsync().ConfigureAwait(false);
            }
        }
        Write(
            log,
            new
            {
                Event = "passed",
                batch,
                cycle,
                Operations = (long)batch * Workers * OperationsPerBatch,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            }
        );
        await TestContext
            .Progress.WriteLineAsync(
                $"feature soak stats={statistics}; batches={batch}; cycles={cycle}; output={output}"
            )
            .ConfigureAwait(false);
    }

    private static FeatureCache[] CreateCaches(bool statistics, int cycle) =>
        [new(0, cycle, statistics), new(1, cycle, statistics), new(2, cycle, statistics)];

    private static async Task OperateAsync(FeatureCache cache, int id, int kind)
    {
        Key key = cache.Keys[id];
        switch (kind)
        {
            case < 5 when cache.Mode != 2:
                using (var cancellation = new CancellationTokenSource())
                {
                    var pending = cache.GetAllAsync(
                        [key, cache.Keys[(id + 1) % 128], key],
                        cancellation.Token
                    );
                    // Cancellation callbacks must finish before the next deterministic test step.
                    // ReSharper disable once MethodHasAsyncOverload
                    cancellation.Cancel();
                    cache.Validate(await pending.ConfigureAwait(false));
                }
                break;
            case < 45:
                cache.Validate(
                    await cache
                        .GetAllAsync([key, cache.Keys[(id + 1) % 128], key])
                        .ConfigureAwait(false)
                );
                break;
            case < 60:
                Validate(await cache.GetAsync(key).ConfigureAwait(false), key.Id, cache.Instance);
                break;
            case < 77:
                cache.Engine.Put(key, cache.NewPayload(key));
                break;
            case < 87:
                cache.Engine.Invalidate(key);
                break;
            case < 93:
                if (cache.Engine.TryGet(key, out Payload? value))
                    Validate(value, key.Id, cache.Instance);
                break;
            case < 96:
                cache.Engine.Put(new Key(128 + id), cache.NewPayload(new Key(128 + id)));
                break;
            case < 98:
                cache.Engine.Clear();
                break;
            default:
                cache.Engine.CleanUp();
                break;
        }
        cache.Error.Should().BeNull();
    }

    private static void Validate(Payload value, int id, int instance)
    {
        value.KeyId.Should().Be(id);
        value.Instance.Should().Be(instance);
        value.Complement.Should().Be(~value.Version);
    }

    private static void Progress(
        StreamWriter log,
        string name,
        Stopwatch elapsed,
        int batch,
        int cycle,
        FeatureCache[] caches,
        long[] outcomes
    ) =>
        Write(
            log,
            new
            {
                Event = name,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                batch,
                cycle,
                Operations = (long)batch * Workers * OperationsPerBatch,
                outcomes,
                Caches = caches.Select(cache => cache.Snapshot()).ToArray(),
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                GcCollections = new[]
                {
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2),
                },
            }
        );

    private static void Write<T>(StreamWriter log, T item) =>
        log.WriteLine(JsonSerializer.Serialize(item));

    private readonly record struct TraceEntry(
        long Position,
        int Cycle,
        int Mode,
        int KeyId,
        int Kind
    );

    private sealed class FeatureCache : IAsyncDisposable
    {
        private readonly AsyncLoadingCache<Key, Payload>? _async;
        private readonly LoadingCache<Key, Payload>? _sync;
        private readonly bool _statistics;
        private readonly IEqualityComparer<Key> _comparer;
        private long _version;
        private long _bulkCalls;
        private long _singleCalls;
        private long _listenerCalls;
        private long _listenerThrows;
        private long _evictionCalls;
        private long _collected;
        private int _active;
        private int _peak;
        private Exception? _error;
        private bool _disposed;

        internal FeatureCache(int mode, int cycle, bool statistics)
        {
            Mode = mode;
            Instance = cycle * 3 + mode;
            _statistics = statistics;
            _comparer =
                mode == 1 ? ReferenceEqualityComparer.Instance : EqualityComparer<Key>.Default;
            Engine = new CacheEngine<Key, Payload>(
                new CacheEngineOptions<Key, Payload>
                {
                    MaximumSize = Maximum,
                    MaxConcurrentLoads = LoadLimit,
                    MaxPendingLoadKeys = 16,
                    MaximumBulkKeys = 4,
                    WeakKeys = mode == 1,
                    WeakValues = mode == 2,
                    RecordStatistics = statistics,
                    NotificationCapacity = 16,
                    RemovalListener = Removed,
                    EvictionListener = Evicted,
                }
            );
            if (mode == 2)
                _sync = new LoadingCache<Key, Payload>(Engine, Load, bulkLoader: LoadAll);
            else
                _async = new AsyncLoadingCache<Key, Payload>(
                    Engine,
                    LoadAsync,
                    bulkLoader: LoadAllAsync
                );
        }

        internal int Mode { get; }
        internal int Instance { get; }
        internal Key[] Keys { get; private set; } =
        [.. Enumerable.Range(0, 128).Select(id => new Key(id))];
        internal CacheEngine<Key, Payload> Engine { get; }
        internal Exception? Error => Volatile.Read(ref _error);

        internal Payload NewPayload(Key key) =>
            new(key.Id, Instance, Interlocked.Increment(ref _version));

        internal void RotateKeys() =>
            Keys = [.. Enumerable.Range(0, 128).Select(id => new Key(id))];

        internal ValueTask<Payload> GetAsync(Key key) =>
            _async?.GetAsync(key) ?? ValueTask.FromResult(_sync!.Get(key));

        internal ValueTask<IReadOnlyDictionary<Key, Payload>> GetAllAsync(
            Key[] keys,
            CancellationToken token = default
        ) => _async?.GetAllAsync(keys, token) ?? ValueTask.FromResult(_sync!.GetAll(keys));

        internal void Validate(IReadOnlyDictionary<Key, Payload> values)
        {
            values.Count.Should().Be(2);
            foreach (var pair in values)
                FeatureCombinationStabilityTests.Validate(pair.Value, pair.Key.Id, Instance);
        }

        private void Enter()
        {
            int active = Interlocked.Increment(ref _active);
            int prior;
            do
            {
                prior = Volatile.Read(ref _peak);
            } while (
                prior < active && Interlocked.CompareExchange(ref _peak, active, prior) != prior
            );
            active.Should().BeLessThanOrEqualTo(LoadLimit);
        }

        private Payload Load(Key key)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref _singleCalls);
                return NewPayload(key);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private async Task<Payload> LoadAsync(Key key, CancellationToken token)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref _singleCalls);
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return NewPayload(key);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private Dictionary<Key, Payload> Result(IReadOnlyCollection<Key> keys, long call)
        {
            if (call % 37 == 0)
                throw new ControlledLoaderFailure();
            Dictionary<Key, Payload> values = keys.ToDictionary(
                static key => key,
                NewPayload,
                _comparer
            );
            Key extra = new(10_000 + (int)(call % 8));
            values.Add(extra, NewPayload(extra));
            return values;
        }

        private Dictionary<Key, Payload> LoadAll(IReadOnlyCollection<Key> keys)
        {
            Enter();
            try
            {
                return Result(keys, Interlocked.Increment(ref _bulkCalls));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private async Task<IReadOnlyDictionary<Key, Payload>> LoadAllAsync(
            IReadOnlyCollection<Key> keys,
            CancellationToken token
        )
        {
            Enter();
            try
            {
                long call = Interlocked.Increment(ref _bulkCalls);
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return Result(keys, call);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void CheckNotification(
            RemovalNotification<Key, Payload> notification,
            bool eviction
        )
        {
            try
            {
                if (notification.Value is { } value)
                {
                    FeatureCombinationStabilityTests.Validate(
                        value,
                        notification.Key?.Id ?? value.KeyId,
                        Instance
                    );
                    int count = eviction
                        ? Interlocked.Increment(ref value.Evictions)
                        : Interlocked.Increment(ref value.Removals);
                    count
                        .Should()
                        .Be(
                            1,
                            "each payload is published only once and must not be notified twice"
                        );
                }
                if (notification.Cause == RemovalCause.Collected)
                    Interlocked.Increment(ref _collected);
                if (eviction)
                    notification.Cause.Should().BeOneOf(RemovalCause.Size, RemovalCause.Collected);
                if (notification.Key is not { } key)
                    return;
                try
                {
                    Engine.TryGet(key, out _);
                }
                catch (ObjectDisposedException) { }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _error, exception, null);
            }
        }

        private void Removed(RemovalNotification<Key, Payload> notification)
        {
            CheckNotification(notification, eviction: false);
            long call = Interlocked.Increment(ref _listenerCalls);
            if (call % 31 != 0)
                return;
            Interlocked.Increment(ref _listenerThrows);
            throw new ControlledListenerFailure();
        }

        private void Evicted(RemovalNotification<Key, Payload> notification)
        {
            CheckNotification(notification, eviction: true);
            Interlocked.Increment(ref _evictionCalls);
        }

        internal async Task AssertQuiescentAsync()
        {
            var watch = Stopwatch.StartNew();
            while (Engine.HasActiveFlights)
            {
                if (watch.Elapsed > Watchdog)
                    throw new TimeoutException("Feature cache loads did not quiesce.");
                await Task.Yield();
            }
            Engine.CleanUp();
            Engine.AssertInvariants();
            Engine.EstimatedCount.Should().BeLessThanOrEqualTo(Maximum);
            Engine.GetStatistics().InFlightLoads.Should().Be(0);
            Volatile.Read(ref _active).Should().Be(0);
            Volatile.Read(ref _peak).Should().BeLessThanOrEqualTo(LoadLimit);
            Engine.GetNotificationStatistics().Queued.Should().BeLessThanOrEqualTo(32);
            foreach (var pair in Engine.DictionarySnapshot())
                FeatureCombinationStabilityTests.Validate(pair.Value, pair.Key.Id, Instance);
            Error.Should().BeNull();
            if (!_statistics)
                Engine.GetStatistics().BulkLoads.Should().Be(0);
        }

        internal object Snapshot() =>
            new
            {
                Mode,
                Instance,
                BulkCalls = Interlocked.Read(ref _bulkCalls),
                SingleCalls = Interlocked.Read(ref _singleCalls),
                ListenerCalls = Interlocked.Read(ref _listenerCalls),
                ListenerThrows = Interlocked.Read(ref _listenerThrows),
                EvictionCalls = Interlocked.Read(ref _evictionCalls),
                CollectedCallbacks = Interlocked.Read(ref _collected),
                Peak = Volatile.Read(ref _peak),
                Notifications = Engine.GetNotificationStatistics(),
                Disposed = _disposed,
            };

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            await Engine.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
            var watch = Stopwatch.StartNew();
            while (Engine.GetNotificationStatistics().HandlerRunning)
            {
                if (watch.Elapsed > Watchdog)
                    throw new TimeoutException("Feature listener did not finish after disposal.");
                await Task.Yield();
            }
            Error.Should().BeNull();
            Engine.GetNotificationStatistics().Queued.Should().Be(0);
        }
    }

    private sealed record Key(int Id);

    private sealed class Payload(int keyId, int instance, long version)
    {
        internal int KeyId { get; } = keyId;
        internal int Instance { get; } = instance;
        internal long Version { get; } = version;
        internal long Complement { get; } = ~version;
        internal int Removals;
        internal int Evictions;
    }

    private sealed class ControlledLoaderFailure : Exception;

    private sealed class ControlledListenerFailure : Exception;
}
