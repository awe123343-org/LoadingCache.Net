using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;

namespace LoadingCache.HitProbe;

internal static class Program
{
    private const int LookupChunkSize = 1_024;
    private static readonly TimeSpan BarrierWatchdog = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int Main(string[] args)
    {
        try
        {
            ProbeOptions options = ProbeOptions.Parse(args);
            if (options.KeyMode == "preboxed")
            {
                Run(
                    options,
                    Enumerable.Range(0, options.Residents).Select(key => (object)key).ToArray()
                );
            }
            else
            {
                Run(options, Enumerable.Range(0, options.Residents).ToArray());
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"LoadingCache.HitProbe failed: {exception}");
            return 1;
        }
    }

    private static void Run<TKey>(ProbeOptions options, TKey[] keys)
        where TKey : notnull
    {
        if (options.Backend == "loadingcache")
        {
            using ICache<TKey, int> cache = CreateCache<TKey>(options);
            LoadingDiagnostics? diagnostics = options.Diagnostics ? new(cache) : null;
            Run<TKey, LoadingBackend<TKey>>(options, keys, new(cache), diagnostics);
        }
        else
        {
            using MemoryCache cache = new(
                new MemoryCacheOptions
                {
                    SizeLimit = options.Capacity,
                    TrackStatistics = options.Statistics,
                }
            );
            Run<TKey, MemoryBackend<TKey>>(options, keys, new(cache, options));
        }
    }

    private static void Run<TKey, TBackend>(
        ProbeOptions options,
        TKey[] keys,
        TBackend cache,
        LoadingDiagnostics? diagnostics = null
    )
        where TKey : notnull
        where TBackend : struct, IBackend<TKey>
    {
        for (int key = 0; key < options.Residents; key++)
        {
            cache.Put(keys[key], key + 1);
        }

        cache.CleanUp();
        CacheSnapshot initial = cache.Snapshot();
        if (
            initial.ResidentCount != options.Residents
            || (initial.WeightedSize is { } initialWeight && initialWeight != options.Residents)
        )
        {
            throw new InvalidOperationException(
                $"Initial cache bounds failed: residents={initial.ResidentCount}, "
                    + $"weightedSize={initial.WeightedSize}, expected={options.Residents}."
            );
        }

        List<ProbeSample> samples = [];
        using (ReaderCoordinator<TKey, TBackend> coordinator = new(cache, keys, options))
        {
            for (int sampleIndex = 0; sampleIndex < options.SampleCount; sampleIndex++)
            {
                bool warmup = sampleIndex < options.Warmups;
                samples.Add(coordinator.RunSample(sampleIndex, warmup, diagnostics));
            }
        }

        ProbeReport report = new(
            SchemaVersion: 2,
            Backend: options.Backend,
            KeyMode: options.KeyMode,
            Expiration: options.Expiration,
            BackendAssemblySha256: Sha256(
                options.Backend == "loadingcache"
                    ? typeof(CacheBuilder).Assembly.Location
                    : typeof(MemoryCache).Assembly.Location
            ),
            MemoryCacheVersion: typeof(MemoryCache).Assembly.GetName().Version!.ToString(),
            MemoryCacheInformationalVersion: typeof(MemoryCache)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion,
            Capacity: options.Capacity,
            Residents: options.Residents,
            Pattern: options.Pattern,
            Workers: options.Workers,
            Statistics: options.Statistics,
            Warmups: options.Warmups,
            Runs: options.Runs,
            DurationMilliseconds: options.DurationMilliseconds,
            Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Os: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Processors: Environment.ProcessorCount,
            StopwatchFrequency: Stopwatch.Frequency,
            ServerGc: System.Runtime.GCSettings.IsServerGC,
            GcLatencyMode: System.Runtime.GCSettings.LatencyMode.ToString(),
            RuntimeEnvironment: RuntimeEnvironmentSnapshot.Create(),
            CacheAssemblySha256: Sha256(typeof(CacheBuilder).Assembly.Location),
            HarnessAssemblySha256: Sha256(typeof(Program).Assembly.Location),
            ManagedAllocationScope: "process managed allocation delta from before start release through finish barrier",
            Samples: samples
        );

        string json = JsonSerializer.Serialize(report, JsonOptions);
        if (options.OutputPath is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            string fullPath = Path.GetFullPath(options.OutputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, json + Environment.NewLine);
        }
    }

    private static ICache<TKey, int> CreateCache<TKey>(ProbeOptions options)
        where TKey : notnull
    {
        CacheBuilder<TKey, int> builder = CacheBuilder
            .Create<TKey, int>()
            .MaximumSize(options.Capacity)
            .MaxConcurrentLoads(1);
        if (options.Statistics)
        {
            builder.RecordStatistics();
        }
        if (options.Expiration is "write" or "both")
            builder.ExpireAfterWrite(TimeSpan.FromHours(1));
        if (options.Expiration is "access" or "both")
            builder.ExpireAfterAccess(TimeSpan.FromHours(1));

        return builder.Build();
    }

    private interface IBackend<TKey>
        where TKey : notnull
    {
        bool TryGet(TKey key, out int value);
        void Put(TKey key, int value);
        void CleanUp();
        CacheSnapshot Snapshot();
        RequestStatistics Statistics { get; }
        bool HasCleanup { get; }
    }

    private readonly struct LoadingBackend<TKey>(ICache<TKey, int> cache) : IBackend<TKey>
        where TKey : notnull
    {
        public bool TryGet(TKey key, out int value) => cache.TryGet(key, out value);

        public void Put(TKey key, int value) => cache.Put(key, value);

        public void CleanUp() => cache.CleanUp();

        public bool HasCleanup => true;

        public CacheSnapshot Snapshot() =>
            new(cache.EstimatedCount, cache.Policy.Eviction!.WeightedSize);

        public RequestStatistics Statistics
        {
            get
            {
                CacheStatistics stats = cache.Statistics;
                return new(stats.Hits, stats.Misses);
            }
        }
    }

    private readonly struct MemoryBackend<TKey> : IBackend<TKey>
        where TKey : notnull
    {
        private readonly MemoryCache _cache;
        private readonly MemoryCacheEntryOptions _entryOptions;

        public MemoryBackend(MemoryCache cache, ProbeOptions options)
        {
            _cache = cache;
            _entryOptions = new MemoryCacheEntryOptions().SetSize(1);
            if (options.Expiration is "write" or "both")
                _entryOptions.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            if (options.Expiration is "access" or "both")
                _entryOptions.SlidingExpiration = TimeSpan.FromHours(1);
        }

        public bool TryGet(TKey key, out int value) => _cache.TryGetValue(key, out value);

        public void Put(TKey key, int value) => _cache.Set(key, value, _entryOptions);

        // MemoryCache has no corresponding explicit maintenance API. No compaction is substituted.
        public void CleanUp() { }

        public bool HasCleanup => false;

        public CacheSnapshot Snapshot() =>
            new(_cache.Count, _cache.GetCurrentStatistics()?.CurrentEstimatedSize);

        public RequestStatistics Statistics
        {
            get
            {
                MemoryCacheStatistics? stats = _cache.GetCurrentStatistics();
                return new(stats?.TotalHits ?? 0, stats?.TotalMisses ?? 0);
            }
        }
    }

    private static long[] CollectionCounts() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static long[] CollectionDelta(long[] before) =>
        [
            GC.CollectionCount(0) - before[0],
            GC.CollectionCount(1) - before[1],
            GC.CollectionCount(2) - before[2],
        ];

    private static string Sha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"Cannot hash assembly at '{path}'.");
        }

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static long ExpectedChecksum(int worker, long operations, int mask, int residents)
    {
        long start = (long)worker * 17;
        if (mask == 0)
        {
            return operations;
        }

        long cycleSum = checked((long)residents * (residents + 1) / 2);
        long completeCycles = operations / residents;
        long remainder = operations % residents;
        long total = checked(completeCycles * cycleSum);
        int first = (int)(start & mask);
        total = checked(total + WrappedValueSum(first, remainder, residents));
        return total;
    }

    private static long WrappedValueSum(int first, long count, int residents)
    {
        if (count == 0)
        {
            return 0;
        }

        long firstSegment = Math.Min(count, residents - first);
        long total = RangeValueSum(first, firstSegment);
        long remaining = count - firstSegment;
        if (remaining > 0)
        {
            total = checked(total + RangeValueSum(0, remaining));
        }

        return total;
    }

    private static long RangeValueSum(int first, long count)
    {
        if (count == 0)
        {
            return 0;
        }

        long last = checked(first + count - 1);
        return checked(count * (first + last) / 2 + count);
    }

    private sealed class ReaderCoordinator<TKey, TBackend> : IDisposable
        where TKey : notnull
        where TBackend : struct, IBackend<TKey>
    {
        private TBackend _cache;
        private readonly TKey[] _keys;
        private readonly int _mask;
        private readonly int _workerCount;
        private readonly int _sampleCount;
        private readonly long _durationTicks;
        private readonly bool _statistics;
        private readonly Barrier _barrier;
        private readonly CountdownEvent _ready;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread?[] _threads;
        private readonly WorkerResult[] _results;
        private readonly object _failureGate = new();
        private Exception? _failure;
        private long _deadline;
        private bool _disposed;

        internal ReaderCoordinator(TBackend cache, TKey[] keys, ProbeOptions options)
        {
            _cache = cache;
            _keys = keys;
            _mask = options.Pattern == "hot" ? 0 : options.Residents - 1;
            _workerCount = options.Workers;
            _sampleCount = options.SampleCount;
            _statistics = options.Statistics;
            _durationTicks = checked(options.DurationMilliseconds * Stopwatch.Frequency / 1_000);
            _barrier = new Barrier(_workerCount + 1);
            _ready = new CountdownEvent(_workerCount);
            _threads = new Thread[_workerCount];
            _results = new WorkerResult[_workerCount];

            try
            {
                for (int worker = 0; worker < _workerCount; worker++)
                {
                    int workerIndex = worker;
                    var thread = new Thread(() => WorkerLoop(workerIndex))
                    {
                        IsBackground = true,
                        Name = $"LoadingCache.HitProbe.reader{workerIndex}",
                    };
                    _threads[worker] = thread;
                    thread.Start();
                }

                if (!_ready.Wait(BarrierWatchdog))
                {
                    throw new TimeoutException(
                        "Hit-probe reader threads did not become ready within 30 seconds."
                    );
                }
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                TryRemoveCoordinatorParticipant();
                JoinThreads();
                _barrier.Dispose();
                _ready.Dispose();
                _stop.Dispose();
                throw;
            }
        }

        internal ProbeSample RunSample(
            int sampleIndex,
            bool warmup,
            LoadingDiagnostics? diagnostics
        )
        {
            ThrowIfWorkerFailed();

            RequestStatistics beforeStatistics = _cache.Statistics;
            ProbeDiagnosticSnapshot? diagnosticBefore = diagnostics?.Capture();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            long[] collectionsBefore = CollectionCounts();
            long wallStarted = Stopwatch.GetTimestamp();
            Volatile.Write(ref _deadline, checked(wallStarted + _durationTicks));

            try
            {
                SignalAndWait();
                ThrowIfWorkerFailed();
                SignalAndWait();
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                TryRemoveCoordinatorParticipant();
                throw;
            }

            long wallFinished = Stopwatch.GetTimestamp();
            long managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            long[] gcCollections = CollectionDelta(collectionsBefore);
            ThrowIfWorkerFailed();
            ProbeDiagnosticSnapshot? diagnosticAfter = diagnostics?.Capture();

            RequestStatistics afterStatistics = _cache.Statistics;
            long operations = 0;
            long checksum = 0;
            long misses = 0;
            long[] workersOperations = new long[_workerCount];
            for (int worker = 0; worker < _workerCount; worker++)
            {
                WorkerResult result = _results[worker];
                if (result.Operations <= 0)
                {
                    throw new InvalidOperationException(
                        $"Worker {worker} completed no operations in sample {sampleIndex}."
                    );
                }

                long expectedChecksum = ExpectedChecksum(
                    worker,
                    result.Operations,
                    _mask,
                    _keys.Length
                );
                if (result.Checksum != expectedChecksum)
                {
                    throw new InvalidOperationException(
                        $"Checksum mismatch for worker {worker} in sample {sampleIndex}: "
                            + $"expected={expectedChecksum}, actual={result.Checksum}."
                    );
                }

                workersOperations[worker] = result.Operations;
                operations = checked(operations + result.Operations);
                checksum = checked(checksum + result.Checksum);
                misses = checked(misses + result.Misses);
            }

            if (misses != 0)
            {
                throw new InvalidOperationException(
                    $"Read miss detected in sample {sampleIndex}: misses={misses}."
                );
            }

            long hitsDelta = checked(afterStatistics.Hits - beforeStatistics.Hits);
            long missesDelta = checked(afterStatistics.Misses - beforeStatistics.Misses);
            if (
                _statistics
                    ? hitsDelta != operations || missesDelta != 0
                    : hitsDelta != 0 || missesDelta != 0
            )
            {
                throw new InvalidOperationException(
                    $"Statistics delta failed in sample {sampleIndex}: "
                        + $"hits={hitsDelta}, misses={missesDelta}, operations={operations}, "
                        + $"statisticsEnabled={_statistics}."
                );
            }

            long cleanupStarted = Stopwatch.GetTimestamp();
            _cache.CleanUp();
            long cleanupFinished = Stopwatch.GetTimestamp();
            CacheSnapshot snapshot = _cache.Snapshot();
            bool boundsPassed =
                snapshot.ResidentCount == _keys.Length
                && (
                    snapshot.WeightedSize is null || snapshot.WeightedSize == snapshot.ResidentCount
                );
            if (!boundsPassed)
            {
                throw new InvalidOperationException(
                    $"Cache bounds failed in sample {sampleIndex}: residents={snapshot.ResidentCount}, "
                        + $"weightedSize={snapshot.WeightedSize}, expected={_keys.Length}."
                );
            }

            double wallSeconds = Stopwatch.GetElapsedTime(wallStarted, wallFinished).TotalSeconds;
            double cleanupSeconds = _cache.HasCleanup
                ? Stopwatch.GetElapsedTime(cleanupStarted, cleanupFinished).TotalSeconds
                : 0;

            return new ProbeSample(
                SampleIndex: sampleIndex,
                Warmup: warmup,
                WallSeconds: wallSeconds,
                CleanupSeconds: cleanupSeconds,
                Operations: operations,
                Checksum: checksum,
                Misses: misses,
                WorkersOperations: workersOperations,
                HitsDelta: hitsDelta,
                MissesDelta: missesDelta,
                ResidentCount: snapshot.ResidentCount,
                WeightedSize: snapshot.WeightedSize,
                BoundsPassed: boundsPassed,
                ReadOnlyOperationsPerSecond: operations / wallSeconds,
                OperationsPerSecondIncludingCleanup: operations / (wallSeconds + cleanupSeconds),
                ManagedAllocatedBytes: managedAllocatedBytes,
                GcCollections: gcCollections,
                Diagnostics: diagnosticBefore is null || diagnosticAfter is null
                    ? null
                    : new(
                        diagnosticBefore,
                        diagnosticAfter,
                        operations - (diagnosticAfter.Reserved - diagnosticBefore.Reserved)
                    )
            );
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Set();
            TryRemoveCoordinatorParticipant();
            JoinThreads();
            _barrier.Dispose();
            _ready.Dispose();
            _stop.Dispose();
        }

        private void WorkerLoop(int worker)
        {
            try
            {
                _ready.Signal();
                for (int sample = 0; sample < _sampleCount; sample++)
                {
                    if (_stop.IsSet)
                    {
                        return;
                    }

                    SignalAndWait();
                    ThrowIfWorkerFailed();
                    long deadline = Volatile.Read(ref _deadline);
                    long index = (long)worker * 17;
                    long checksum = 0;
                    long misses = 0;
                    long operations = 0;

                    while (true)
                    {
                        for (int offset = 0; offset < LookupChunkSize; offset++)
                        {
                            TKey key = _keys[(int)(index & _mask)];
                            if (_cache.TryGet(key, out int value))
                            {
                                checksum += value;
                            }
                            else
                            {
                                misses++;
                            }

                            index++;
                            operations++;
                        }

                        if (Stopwatch.GetTimestamp() >= deadline)
                        {
                            break;
                        }
                    }

                    _results[worker] = new WorkerResult(operations, checksum, misses);
                    SignalAndWait();
                    ThrowIfWorkerFailed();
                }
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
                TryRemoveWorkerParticipant();
            }
        }

        private void SignalAndWait()
        {
            if (!_barrier.SignalAndWait(BarrierWatchdog))
            {
                throw new TimeoutException("Hit-probe barrier did not advance within 30 seconds.");
            }
        }

        private void ThrowIfWorkerFailed()
        {
            Exception? failure;
            lock (_failureGate)
            {
                failure = _failure;
            }

            if (failure is not null)
            {
                throw new InvalidOperationException("A hit-probe worker failed.", failure);
            }
        }

        private void RecordFailure(Exception exception)
        {
            lock (_failureGate)
            {
                _failure ??= exception;
            }

            _stop.Set();
        }

        private void TryRemoveCoordinatorParticipant()
        {
            try
            {
                _barrier.RemoveParticipant();
            }
            catch (ObjectDisposedException)
            {
                // Disposal is already completing.
            }
            catch (InvalidOperationException)
            {
                // The main participant may already have been removed by an earlier timeout.
            }
        }

        private void TryRemoveWorkerParticipant()
        {
            try
            {
                _barrier.RemoveParticipant();
            }
            catch (ObjectDisposedException)
            {
                // Disposal is already completing.
            }
            catch (InvalidOperationException)
            {
                // The failed phase may already have completed after another participant left.
            }
        }

        private void JoinThreads()
        {
            foreach (Thread? thread in _threads)
            {
                if (thread is not null && thread.IsAlive && !thread.Join(BarrierWatchdog))
                {
                    throw new TimeoutException(
                        $"Reader thread '{thread.Name}' did not stop within 30 seconds."
                    );
                }
            }
        }
    }

    // Opt-in diagnostic snapshots run while all reader threads are stopped at a barrier,
    // outside both timing and allocation deltas. Reflection metadata is resolved once;
    // production counters, scheduling, and the measured worker loop remain unchanged.
    private sealed class LoadingDiagnostics
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly object _engine;
        private readonly object _buffer;
        private readonly object _consumerGate;
        private readonly MethodInfo _maintenanceStatistics;
        private readonly MethodInfo _readStatistics;
        private readonly PropertyInfo _requests;
        private readonly PropertyInfo _drainPasses;
        private readonly PropertyInfo _enqueued;
        private readonly PropertyInfo _dequeued;
        private readonly PropertyInfo _droppedFull;
        private readonly PropertyInfo _droppedFailed;
        private readonly FieldInfo _table;
        private readonly FieldInfo _readCursor;
        private readonly FieldInfo _writeCursor;
        private readonly bool _recordStatistics;
        private readonly uint _policySeed;

        internal LoadingDiagnostics(object cache)
        {
            _engine = Field(cache.GetType(), "Engine").GetValue(cache)!;
            Type engineType = _engine.GetType();
            object policy = Field(engineType, "_policy").GetValue(_engine)!;
            _buffer = Field(policy.GetType(), "_pendingAccesses").GetValue(policy)!;
            _policySeed = (uint)Field(policy.GetType(), "_policySeed").GetValue(policy)!;
            Type bufferType = _buffer.GetType();
            _consumerGate = Field(bufferType, "_consumerGate").GetValue(_buffer)!;
            _recordStatistics = (bool)Field(bufferType, "_recordStatistics").GetValue(_buffer)!;
            _table = Field(bufferType, "_table");
            Type ringType = _table.FieldType.GetElementType()!;
            _readCursor = Field(ringType, "_readCounter");
            _writeCursor = Field(ringType, "_writeCounter");
            _maintenanceStatistics = engineType.GetMethod(
                "GetMaintenanceStatistics",
                InstanceMembers
            )!;
            _readStatistics = engineType.GetMethod(
                "GetPolicyReadBufferStatistics",
                InstanceMembers
            )!;
            Type maintenanceType = _maintenanceStatistics.ReturnType;
            _requests = maintenanceType.GetProperty("Requests", InstanceMembers)!;
            _drainPasses = maintenanceType.GetProperty("DrainPasses", InstanceMembers)!;
            Type readType = _readStatistics.ReturnType;
            _enqueued = readType.GetProperty("Enqueued", InstanceMembers)!;
            _dequeued = readType.GetProperty("Dequeued", InstanceMembers)!;
            _droppedFull = readType.GetProperty("DroppedFull", InstanceMembers)!;
            _droppedFailed = readType.GetProperty("DroppedFailed", InstanceMembers)!;
        }

        internal ProbeDiagnosticSnapshot Capture()
        {
            object maintenance = _maintenanceStatistics.Invoke(_engine, null)!;
            object reads = _readStatistics.Invoke(_engine, null)!;
            long reserved = 0;
            long consumed = 0;
            int stripes = 0;
            lock (_consumerGate)
            {
                if (_table.GetValue(_buffer) is Array table)
                {
                    foreach (object? ring in table)
                    {
                        if (ring is null)
                            continue;
                        stripes++;
                        reserved = checked(reserved + (long)_writeCursor.GetValue(ring)!);
                        consumed = checked(consumed + (long)_readCursor.GetValue(ring)!);
                    }
                }
            }

            return new(
                (long)_requests.GetValue(maintenance)!,
                (long)_drainPasses.GetValue(maintenance)!,
                _recordStatistics ? (long)_enqueued.GetValue(reads)! : null,
                _recordStatistics ? (long)_dequeued.GetValue(reads)! : null,
                _recordStatistics ? (long)_droppedFull.GetValue(reads)! : null,
                _recordStatistics ? (long)_droppedFailed.GetValue(reads)! : null,
                reserved,
                consumed,
                stripes,
                _policySeed,
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount
            );
        }

        private static FieldInfo Field(Type type, string name) =>
            type.GetField(name, InstanceMembers)
            ?? throw new InvalidOperationException(
                $"Diagnostic field {type.FullName}.{name} is missing."
            );
    }

    private sealed record ProbeOptions(
        string Backend,
        string KeyMode,
        string Expiration,
        int Capacity,
        int Residents,
        string Pattern,
        int Workers,
        bool Statistics,
        int Warmups,
        int Runs,
        int DurationMilliseconds,
        bool Diagnostics,
        string? OutputPath
    )
    {
        internal int SampleCount => checked(Warmups + Runs);

        internal static ProbeOptions Parse(string[] args)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (!option.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unexpected argument '{option}'.");
                }

                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value after {option}.");
                }

                if (!values.TryAdd(option, args[++index]))
                {
                    throw new ArgumentException($"Option '{option}' was specified more than once.");
                }
            }

            foreach (string option in values.Keys)
            {
                if (
                    option
                    is not (
                        "--backend"
                        or "--key-mode"
                        or "--expiration"
                        or "--capacity"
                        or "--residents"
                        or "--pattern"
                        or "--workers"
                        or "--statistics"
                        or "--warmups"
                        or "--runs"
                        or "--duration-ms"
                        or "--diagnostics"
                        or "--output"
                    )
                )
                {
                    throw new ArgumentException($"Unknown option '{option}'.");
                }
            }

            int capacity = ReadInt(values, "--capacity", 1_024, 1, 1 << 26);
            string backend = ReadString(values, "--backend", "loadingcache");
            string keyMode = ReadString(values, "--key-mode", "native");
            string expiration = ReadString(values, "--expiration", "none");
            if (backend is not ("loadingcache" or "memorycache"))
                throw new ArgumentException("--backend must be loadingcache or memorycache.");
            if (keyMode is not ("native" or "preboxed"))
                throw new ArgumentException("--key-mode must be native or preboxed.");
            if (expiration is not ("none" or "write" or "access" or "both"))
                throw new ArgumentException("--expiration must be none, write, access or both.");
            int residents = ReadInt(values, "--residents", capacity, 1, capacity);
            int workers = ReadInt(values, "--workers", 1, 1, 256);
            int warmups = ReadInt(values, "--warmups", 3, 0, 100);
            int runs = ReadInt(values, "--runs", 5, 1, 100);
            int durationMilliseconds = ReadInt(values, "--duration-ms", 500, 1, int.MaxValue);
            string pattern = ReadString(values, "--pattern", "hot");
            if (pattern is not ("hot" or "cycle"))
            {
                throw new ArgumentException("--pattern must be hot or cycle.");
            }

            string statisticsValue = ReadString(values, "--statistics", "off");
            if (statisticsValue is not ("on" or "off"))
            {
                throw new ArgumentException("--statistics must be on or off.");
            }

            string diagnosticsValue = ReadString(values, "--diagnostics", "off");
            if (diagnosticsValue is not ("on" or "off"))
                throw new ArgumentException("--diagnostics must be on or off.");
            if (diagnosticsValue == "on" && backend != "loadingcache")
                throw new ArgumentException("--diagnostics on requires the loadingcache backend.");

            if (!IsPowerOfTwo(capacity) || !IsPowerOfTwo(residents))
            {
                throw new ArgumentException(
                    "Capacity and residents must be positive powers of two."
                );
            }

            long durationTicks = checked(durationMilliseconds * Stopwatch.Frequency / 1_000);
            if (durationTicks <= 0)
            {
                throw new ArgumentException("Duration must resolve to positive stopwatch ticks.");
            }

            return new(
                backend,
                keyMode,
                expiration,
                capacity,
                residents,
                pattern,
                workers,
                statisticsValue == "on",
                warmups,
                runs,
                durationMilliseconds,
                diagnosticsValue == "on",
                values.TryGetValue("--output", out string? output) ? output : null
            );
        }

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

        private static int ReadInt(
            IReadOnlyDictionary<string, string> values,
            string option,
            int fallback,
            int minimum,
            int maximum
        )
        {
            string raw = ReadString(
                values,
                option,
                fallback.ToString(CultureInfo.InvariantCulture)
            );
            if (
                !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum
                || value > maximum
            )
            {
                throw new ArgumentOutOfRangeException(
                    option,
                    $"Expected an integer from {minimum} to {maximum}."
                );
            }

            return value;
        }

        private static string ReadString(
            IReadOnlyDictionary<string, string> values,
            string option,
            string fallback
        ) => values.TryGetValue(option, out string? value) ? value : fallback;
    }

    private readonly record struct CacheSnapshot(long ResidentCount, long? WeightedSize);

    private readonly record struct RequestStatistics(long Hits, long Misses);

    private readonly record struct WorkerResult(long Operations, long Checksum, long Misses);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record ProbeReport(
        int SchemaVersion,
        string Backend,
        string KeyMode,
        string Expiration,
        string BackendAssemblySha256,
        string MemoryCacheVersion,
        string MemoryCacheInformationalVersion,
        int Capacity,
        int Residents,
        string Pattern,
        int Workers,
        bool Statistics,
        int Warmups,
        int Runs,
        int DurationMilliseconds,
        string Runtime,
        string Os,
        string Architecture,
        int Processors,
        long StopwatchFrequency,
        bool ServerGc,
        string GcLatencyMode,
        RuntimeEnvironmentSnapshot RuntimeEnvironment,
        string CacheAssemblySha256,
        string HarnessAssemblySha256,
        string ManagedAllocationScope,
        IReadOnlyList<ProbeSample> Samples
    );

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record ProbeSample(
        int SampleIndex,
        bool Warmup,
        double WallSeconds,
        double CleanupSeconds,
        long Operations,
        long Checksum,
        long Misses,
        long[] WorkersOperations,
        long HitsDelta,
        long MissesDelta,
        long ResidentCount,
        long? WeightedSize,
        bool BoundsPassed,
        double ReadOnlyOperationsPerSecond,
        double OperationsPerSecondIncludingCleanup,
        long ManagedAllocatedBytes,
        long[] GcCollections,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            ProbeDiagnostics? Diagnostics
    );

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record ProbeDiagnostics(
        ProbeDiagnosticSnapshot Before,
        ProbeDiagnosticSnapshot After,
        long UnrecordedReads
    );

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record ProbeDiagnosticSnapshot(
        long MaintenanceRequests,
        long MaintenanceDrainPasses,
        long? Enqueued,
        long? Dequeued,
        long? DroppedFull,
        long? DroppedFailed,
        long Reserved,
        long Consumed,
        int ActiveStripes,
        uint PolicySeed,
        int ThreadPoolThreads,
        long ThreadPoolPendingItems,
        long ThreadPoolCompletedItems
    );

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record RuntimeEnvironmentSnapshot(
        string? DotnetTieredCompilation,
        string? DotnetTieredPgo,
        string? ComPlusTieredCompilation,
        string? ComPlusTieredPgo
    )
    {
        internal static RuntimeEnvironmentSnapshot Create() =>
            new(
                Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
                Environment.GetEnvironmentVariable("COMPlus_TieredPGO")
            );
    }
}
