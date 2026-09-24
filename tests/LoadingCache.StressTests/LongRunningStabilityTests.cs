using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace LoadingCache.StressTests;

/// <summary>Continuous seeded traffic; the seed fixes inputs, not operating-system schedules.</summary>
public sealed class LongRunningStabilityTests
{
    private const int Seed = 20260913;
    private const int Workers = 8;
    private const int OperationsPerBatch = 64;
    private const int MaximumResidents = 64;
    private const int LoadLimit = 4;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    /// <summary>Runs resident, fixed-expiration, and variable-expiration caches in one process.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ContinuousMixedLifecycleMaintainsBounds(bool statistics) =>
        RunLifecycleAsync(statistics, accessOnly: false);

    /// <summary>Runs resident, access-only, and variable-expiration caches in one process.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ContinuousAccessOnlyLifecycleMaintainsBounds(bool statistics) =>
        RunLifecycleAsync(statistics, accessOnly: true);

    private static async Task RunLifecycleAsync(bool statistics, bool accessOnly)
    {
        string scenario = accessOnly ? "access-only" : "mixed";
        string? configured = Environment.GetEnvironmentVariable("LOADINGCACHE_SOAK_SECONDS");
        int seconds = configured is null ? 5 : int.Parse(configured, CultureInfo.InvariantCulture);
        seconds.Should().BeInRange(1, 86_400);
        string directory =
            Environment.GetEnvironmentVariable("LOADINGCACHE_SOAK_OUTPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "soak");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(
            directory,
            $"soak-{Environment.Version}-stats-{statistics}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl"
        );
        await using var log = new StreamWriter(output);
        log.AutoFlush = true;
        var elapsed = Stopwatch.StartNew();
        TimeSpan rotationInterval = TimeSpan.FromSeconds(Math.Min(30, seconds / 3.0));
        TimeSpan nextRotation = rotationInterval;
        TimeSpan nextProgress = TimeSpan.Zero;
        var random = Enumerable
            .Range(0, Workers)
            .Select(worker => new Random(unchecked(Seed * 397 + worker)))
            .ToArray();
        var traces = Enumerable.Range(0, Workers).Select(_ => new TraceEntry[128]).ToArray();
        long[] positions = new long[Workers];
        long[] outcomes = new long[3];
        var retired = new Queue<WeakReference>();
        SoakCache[] caches = CreateCaches(statistics, cycle: 0, accessOnly: accessOnly);
        Task[] jobs = [];
        int batch = 0;
        int cycle = 0;
        Write(
            log,
            new
            {
                Event = "start",
                Scenario = scenario,
                Seed,
                Workers,
                OperationsPerBatch,
                LoadLimit,
                statistics,
                seconds,
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription,
                Processors = Environment.ProcessorCount,
                CoreModule = typeof(CacheBuilder).Assembly.ManifestModule.ModuleVersionId,
                TestModule = typeof(LongRunningStabilityTests)
                    .Assembly
                    .ManifestModule
                    .ModuleVersionId,
                RetentionScope = "Process-wide GC samples include parallel tests; weak-target counts are observations, not a retention proof.",
            }
        );
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                SoakCache[] batchCaches = caches;
                int batchCycle = cycle;
                jobs =
                [
                    .. Enumerable
                        .Range(0, Workers)
                        .Select(worker =>
                            Task.Run(async () =>
                            {
                                for (int operation = 0; operation < OperationsPerBatch; operation++)
                                {
                                    int mode = random[worker].Next(batchCaches.Length);
                                    int key = random[worker].Next(128);
                                    int kind = random[worker].Next(1000);
                                    long position = positions[worker]++;
                                    traces[worker][position % traces[worker].Length] =
                                        new TraceEntry(position, batchCycle, mode, key, kind);
                                    try
                                    {
                                        await OperateAsync(
                                                batchCaches[mode],
                                                key,
                                                kind,
                                                random[worker]
                                            )
                                            .ConfigureAwait(false);
                                        Interlocked.Increment(ref outcomes[0]);
                                    }
                                    catch (CacheLoadRejectedException)
                                    {
                                        Interlocked.Increment(ref outcomes[1]);
                                    }
                                    catch (ControlledLoadFailure)
                                    {
                                        Interlocked.Increment(ref outcomes[2]);
                                    }
                                }
                            })
                        ),
                ];
                await Task.WhenAll(jobs).WaitAsync(Watchdog).ConfigureAwait(false);
                batch++;
                foreach (SoakCache cache in caches)
                {
                    await cache.AssertQuiescentAsync().ConfigureAwait(false);
                }

                if (batch % 16 == 0)
                {
                    foreach (SoakCache cache in caches)
                    {
                        cache.Cache.Clear();
                        await cache.AssertQuiescentAsync().ConfigureAwait(false);
                        cache.Cache.EstimatedCount.Should().Be(0);
                    }
                }

                if (elapsed.Elapsed >= nextProgress)
                {
                    WriteProgress(
                        log,
                        "progress",
                        scenario,
                        elapsed,
                        batch,
                        cycle,
                        caches,
                        outcomes,
                        retired,
                        forcedGc: false
                    );
                    nextProgress = elapsed.Elapsed + TimeSpan.FromSeconds(1);
                }

                if (elapsed.Elapsed < nextRotation)
                    continue;
                foreach (SoakCache cache in caches)
                {
                    await cache.DisposeAsync().ConfigureAwait(false);
                    retired.Enqueue(new WeakReference(cache));
                    if (retired.Count > 128)
                    {
                        retired.Dequeue();
                    }
                }

                caches = CreateCaches(statistics, ++cycle, accessOnly);
                jobs = [];
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: true,
                    compacting: false
                );
                GC.WaitForPendingFinalizers();
                WriteProgress(
                    log,
                    "recreated",
                    scenario,
                    elapsed,
                    batch,
                    cycle,
                    caches,
                    outcomes,
                    retired,
                    forcedGc: true
                );
                nextRotation = elapsed.Elapsed + rotationInterval;
            }

            WriteProgress(
                log,
                "workload-complete",
                scenario,
                elapsed,
                batch,
                cycle,
                caches,
                outcomes,
                retired,
                forcedGc: false
            );
        }
        catch (Exception error)
        {
            Write(
                log,
                new
                {
                    Event = "failed",
                    Scenario = scenario,
                    batch,
                    cycle,
                    ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    Error = error.ToString(),
                    positions,
                    traces,
                }
            );
            await TestContext
                .Current!.ErrorOutputWriter.WriteLineAsync(
                    $"scenario={scenario}; seed={Seed}; failure and final 128 operations per worker: {output}"
                )
                .ConfigureAwait(false);
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
                foreach (SoakCache cache in caches)
                {
                    await cache.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        Write(
            log,
            new
            {
                Event = "passed",
                Scenario = scenario,
                batch,
                cycle,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            }
        );
        await TestContext
            .Current!.OutputWriter.WriteLineAsync(
                $"soak scenario={scenario}; stats={statistics}; batches={batch}; cycles={cycle}; output={output}"
            )
            .ConfigureAwait(false);
    }

    private static SoakCache[] CreateCaches(bool statistics, int cycle, bool accessOnly) =>
        [
            new(0, cycle, statistics, accessOnly),
            new(1, cycle, statistics, accessOnly),
            new(2, cycle, statistics, accessOnly),
        ];

    private static async Task OperateAsync(SoakCache state, int key, int kind, Random random)
    {
        switch (kind)
        {
            case < 40:
                using (var canceled = new CancellationTokenSource())
                {
                    ValueTask<Payload> pending = state.Cache.GetAsync(key, canceled.Token);
                    // Cancellation callbacks must finish before the next deterministic test step.
                    // ReSharper disable once MethodHasAsyncOverload
                    canceled.Cancel();
                    try
                    {
                        Validate(await pending.ConfigureAwait(false), key, state.Instance);
                    }
                    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
                }

                break;
            case < 500:
                Validate(
                    await state.Cache.GetAsync(key).ConfigureAwait(false),
                    key,
                    state.Instance
                );
                break;
            case < 550:
                Validate(
                    await state.Cache.RefreshAsync(key).ConfigureAwait(false),
                    key,
                    state.Instance
                );
                break;
            case < 750:
                state.Cache.Set(
                    key,
                    new Payload(key, state.Instance, random.Next(17), random.Next())
                );
                break;
            case < 850:
                state.Cache.Invalidate(key);
                break;
            case < 940:
                if (state.Cache.TryGet(key, out Payload? value))
                {
                    Validate(value, key, state.Instance);
                }

                break;
            case < 970:
                state.Clock.Advance(TimeSpan.FromMilliseconds(random.Next(1, 8)));
                break;
            case < 985:
                state.Cache.CleanUp();
                break;
            case < 990:
                state.Cache.Clear();
                break;
            default:
                state.Cache.Policy.Eviction!.SetMaximum(
                    state.Mode == 0 ? random.Next(32, 65) : random.Next(128, 385)
                );
                break;
        }

        state.Peak.Should().BeLessThanOrEqualTo(LoadLimit);
    }

    private static void Validate(Payload value, int key, int instance)
    {
        value.Key.Should().Be(key);
        value.Instance.Should().Be(instance);
        value.Complement.Should().Be(~value.Version);
    }

    private static void WriteProgress(
        StreamWriter log,
        string eventName,
        string scenario,
        Stopwatch elapsed,
        int batch,
        int cycle,
        SoakCache[] caches,
        long[] outcomes,
        Queue<WeakReference> retired,
        bool forcedGc
    )
    {
        using var process = Process.GetCurrentProcess();
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        Write(
            log,
            new
            {
                Event = eventName,
                Scenario = scenario,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                batch,
                cycle,
                Operations = (long)batch * Workers * OperationsPerBatch,
                outcomes,
                Caches = caches
                    .Select(cache => new
                    {
                        cache.Mode,
                        cache.Instance,
                        Loads = cache.Invocations,
                        cache.Peak,
                        Residents = cache.Cache.EstimatedCount,
                        Weight = cache.Cache.Policy.Eviction!.WeightedSize,
                        Statistics = cache.Cache.GetStatistics(),
                        ClockTicks = cache.Clock.GetTimestamp(),
                    })
                    .ToArray(),
                forcedGc,
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                memory.HeapSizeBytes,
                memory.FragmentedBytes,
                WorkingSetBytes = process.WorkingSet64,
                GcCollections = new[]
                {
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2),
                },
                RetiredSampleCount = retired.Count,
                RetiredTargetsAlive = retired.Count(reference => reference.IsAlive),
            }
        );
    }

    private static void Write<T>(StreamWriter log, T item) =>
        log.WriteLine(JsonSerializer.Serialize(item));

    private readonly record struct TraceEntry(
        [property: JsonInclude] long Operation,
        [property: JsonInclude] int Cycle,
        [property: JsonInclude] int Mode,
        [property: JsonInclude] int Key,
        [property: JsonInclude] int Kind
    );

    private sealed class SoakCache : IAsyncDisposable
    {
        private readonly CacheEngine<int, Payload> _engine;
        private readonly bool _statistics;
        private int _active;
        private int _peak;
        private long _invocations;

        internal SoakCache(int mode, int cycle, bool statistics, bool accessOnly)
        {
            Mode = mode;
            Instance = cycle * 3 + mode;
            _statistics = statistics;
            _engine = new CacheEngine<int, Payload>(
                new CacheEngineOptions<int, Payload>
                {
                    MaximumSize = mode == 0 ? MaximumResidents : null,
                    MaximumWeight = mode == 0 ? null : 256,
                    MaximumResidentCount = mode == 0 ? null : MaximumResidents,
                    Weigher = mode == 0 ? null : static (_, value) => value.Weight,
                    MaxConcurrentLoads = LoadLimit, // This fixture has only a single-key loader; the resident mode also
                    // exercises entry-only Put commits while expiration modes stay coordinated.
                    SupportsBulkLoading = false,
                    RecordStatistics = statistics,
                    TimeProvider = Clock,
                    ExpireAfterWrite =
                        mode == 1 && !accessOnly ? TimeSpan.FromMilliseconds(250) : null,
                    ExpireAfterAccess = mode == 1 ? TimeSpan.FromMilliseconds(125) : null,
                    RefreshAfterWrite =
                        mode == 2 || (mode == 1 && !accessOnly)
                            ? TimeSpan.FromMilliseconds(50)
                            : null,
                    Expiry = mode == 2 ? new VariableExpiry() : null,
                }
            );
            Cache = new AsyncLoadingCache<int, Payload>(_engine, LoadAsync);
        }

        internal int Mode { get; }
        internal int Instance { get; }
        internal MonotonicClock Clock { get; } = new();
        internal AsyncLoadingCache<int, Payload> Cache { get; }
        internal int Peak => Volatile.Read(ref _peak);
        internal long Invocations => Interlocked.Read(ref _invocations);

        public ValueTask DisposeAsync() => _engine.DisposeAsync();

        private async Task<Payload> LoadAsync(int key, CancellationToken cancellationToken)
        {
            long version = Interlocked.Increment(ref _invocations);
            int active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _peak);
            } while (
                observed < active
                && Interlocked.CompareExchange(ref _peak, active, observed) != observed
            );
            try
            {
                active.Should().BeLessThanOrEqualTo(LoadLimit);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return version % 37 == 0
                    ? throw new ControlledLoadFailure()
                    : new Payload(key, Instance, key % 17, version);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        internal async Task AssertQuiescentAsync()
        {
            var watchdog = Stopwatch.StartNew();
            while (true)
            {
                // All batch producers have completed, but an automatic refresh may only be
                // reserved on the thread pool. The executing gauge alone cannot rule out a
                // future publication between the independent weight and value snapshots.
                if (!_engine.HasActiveFlights)
                {
                    Cache.CleanUp();
                    CacheStatistics statistics = Cache.GetStatistics();
                    if (
                        Volatile.Read(ref _active) == 0
                        && statistics
                            is { InFlightLoads: 0, MaintenanceBacklog: 0, WriteBufferBacklog: 0 }
                        && !_engine.HasActiveFlights
                    )
                    {
                        break;
                    }
                }

                if (watchdog.Elapsed > Watchdog)
                {
                    throw new TimeoutException(
                        $"Mode {Mode} did not quiesce: activeFlights={_engine.HasActiveFlights}; "
                            + JsonSerializer.Serialize(Cache.GetStatistics())
                    );
                }

                await Task.Yield();
            }

            _engine.AssertInvariants();
            Cache.EstimatedCount.Should().BeLessThanOrEqualTo(MaximumResidents);
            long maximum = Cache.Policy.Eviction!.Maximum;
            long weight = Cache.Policy.Eviction.WeightedSize;
            weight.Should().BeLessThanOrEqualTo(maximum);
            KeyValuePair<int, Payload>[] residents = _engine.DictionarySnapshot();
            residents.LongLength.Should().Be(Cache.EstimatedCount);
            weight
                .Should()
                .Be(Mode == 0 ? residents.Length : residents.Sum(pair => (long)pair.Value.Weight));
            foreach (var resident in residents)
            {
                Validate(resident.Value, resident.Key, Instance);
            }

            Peak.Should().BeLessThanOrEqualTo(LoadLimit);
            if (!_statistics)
            {
                Cache.GetStatistics().Hits.Should().Be(0);
                Cache.GetStatistics().LoadsStarted.Should().Be(0);
            }
        }
    }

    private sealed record Payload(int Key, int Instance, int Weight, long Version)
    {
        internal long Complement { get; } = ~Version;
    }

    private sealed class ControlledLoadFailure : Exception;

    private sealed class MonotonicClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class VariableExpiry : IExpiry<int, Payload>
    {
        public TimeSpan ExpireAfterCreate(int key, Payload value, TimeSpan currentDuration) =>
            Duration(value);

        public TimeSpan ExpireAfterUpdate(int key, Payload value, TimeSpan currentDuration) =>
            Duration(value);

        public TimeSpan ExpireAfterRead(int key, Payload value, TimeSpan currentDuration) =>
            value.Version % 11 == 0 ? TimeSpan.Zero : currentDuration;

        private static TimeSpan Duration(Payload value) =>
            TimeSpan.FromMilliseconds(40 + value.Weight * 10);
    }
}
