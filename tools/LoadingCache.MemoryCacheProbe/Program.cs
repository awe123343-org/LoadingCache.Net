using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace LoadingCache.MemoryCacheProbe;

internal static class Program
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(60);
    private static readonly string[] BuildMetadataFiles =
    [
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static async Task Main(string[] args)
    {
        var options = Options.Parse(args);
        var samples = new List<object>();
        int measured = 0;
        long warmupStarted = Stopwatch.GetTimestamp();
        for (int index = 0; measured < options.Runs; index++)
        {
            bool warmup =
                index < options.Warmups
                || (
                    measured == 0
                    && Stopwatch.GetElapsedTime(warmupStarted).TotalMilliseconds
                        < options.MinimumWarmupMilliseconds
                );
            object result = options.Scenario switch
            {
                "replacement" => Replacement(options),
                "trace" => Trace(options),
                "fanin" => await FanInAsync(options).ConfigureAwait(false),
                _ => throw new ArgumentException("--scenario replacement|trace|fanin"),
            };
            samples.Add(
                new
                {
                    index,
                    warmup,
                    result,
                }
            );
            if (!warmup)
                measured++;
            Console.Error.WriteLine(
                $"{options.Backend}/{options.Scenario} sample={index} validated"
            );
        }
        var report = new
        {
            schemaVersion = 1,
            options,
            metadata = Metadata(),
            traceFileSha256 = options.TraceFile is null ? null : Hash(options.TraceFile),
            samples,
        };
        string json = JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine;
        if (options.Output is null)
            Console.Write(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
            File.WriteAllText(options.Output, json);
        }
    }

    private static CacheBuilder<Key, Payload> Builder(Options options)
    {
        var builder = CacheBuilder
            .Create<Key, Payload>()
            .MaximumSize(options.Capacity)
            .MaxConcurrentLoads(options.Workers);
        if (options.Statistics)
            builder.RecordStatistics();
        return builder;
    }

    private static MemoryCache Memory(Options options) =>
        new(
            new MemoryCacheOptions
            {
                SizeLimit = options.Capacity,
                TrackStatistics = options.Statistics,
            }
        );

    private static object Replacement(Options options)
    {
        int resident = options.Capacity / 2;
        Check(resident >= options.Workers, "capacity / 2 must cover every worker");
        var keys = Enumerable.Range(0, resident).Select(id => new Key(id)).ToArray();
        var values = keys.Select(key => new Payload(key.Id, 0)).ToArray();
        var changed = keys.Select(key => new Payload(key.Id, 1)).ToArray();
        var expected = values.ToArray();
        var workerKeys = new Key[options.Workers][];
        var workerValues = new Payload[options.Workers][];
        int perWorker = checked(options.Operations * options.Batches);
        for (int worker = 0; worker < options.Workers; worker++)
        {
            int partition = (resident - 1 - worker) / options.Workers + 1;
            workerKeys[worker] = new Key[perWorker];
            workerValues[worker] = new Payload[perWorker];
            for (int index = 0; index < perWorker; index++)
            {
                int id = worker + index % partition * options.Workers;
                var value =
                    options.ValueMode == "changed" && (index / partition & 1) == 0
                        ? changed[id]
                        : values[id];
                workerKeys[worker][index] = keys[id];
                workerValues[worker][index] = value;
                expected[id] = value;
            }
        }
        using var loading = options.Backend == "loadingcache" ? Builder(options).Build() : null;
        using var memory = options.Backend == "memorycache" ? Memory(options) : null;
        var entryOptions = new MemoryCacheEntryOptions().SetSize(1);
        for (int index = 0; index < resident; index++)
        {
            if (loading is not null)
                loading.Put(keys[index], values[index]);
            else
                memory!.Set(keys[index], values[index], entryOptions);
        }
        if (loading is not null)
            Drain(loading);
        Check((loading?.EstimatedCount ?? memory!.Count) == resident, "prefill count");

        using var workers = new ReplacementWorkers(
            options,
            loading,
            memory,
            entryOptions,
            workerKeys,
            workerValues
        );
        var counters = StartCounters();
        var utcStarted = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        workers.RunBatches();
        long ended = Stopwatch.GetTimestamp();
        var utcEnded = DateTimeOffset.UtcNow;
        var deltas = EndCounters(counters);
        workers.EnsureCompleted();
        long cleanupStarted = Stopwatch.GetTimestamp();
        int cleanupPasses = loading is null ? 0 : Drain(loading);
        double loadingCacheCleanupSeconds = loading is null
            ? 0
            : Stopwatch.GetElapsedTime(cleanupStarted).TotalSeconds;
        object? statistics = loading is null ? memory!.GetCurrentStatistics() : loading.Statistics;
        long finalCount = loading?.EstimatedCount ?? memory!.Count;
        Check(finalCount == resident, "resident replacement changed count");
        long checksum = 0;
        for (int index = 0; index < resident; index++)
        {
            Payload? value;
            bool found = loading is null
                ? memory!.TryGetValue(keys[index], out value)
                : loading.Policy.TryGetQuietly(keys[index], out value);
            Check(found && ReferenceEquals(value, expected[index]), "replacement final value");
            checksum += value!.KeyId * 2L + value.Version;
        }
        return new
        {
            operations = (long)perWorker * options.Workers,
            resident,
            finalCount,
            checksum,
            utcStarted,
            utcEnded,
            startedTimestamp = started,
            endedTimestamp = ended,
            seconds = (double)(ended - started) / Stopwatch.Frequency,
            deltas,
            workerAllocatedBytes = workers.AllocatedBytes,
            loadingCacheCleanupSeconds,
            cleanupPasses,
            statisticsBeforeValidation = statistics,
            contention = "disjoint worker keys; shared cache/policy structures",
            validated = true,
        };
    }

    private static object Trace(Options options)
    {
        int[] original = CreateTrace(options);
        var mapping = new Dictionary<int, int>();
        var trace = new int[original.Length];
        for (int index = 0; index < original.Length; index++)
        {
            if (!mapping.TryGetValue(original[index], out int id))
            {
                id = mapping.Count;
                mapping.Add(original[index], id);
            }
            trace[index] = id;
        }
        var keys = Enumerable.Range(0, mapping.Count).Select(id => new Key(id)).ToArray();
        var values = keys.Select(key => new Payload(key.Id, 0)).ToArray();
        using var loading = options.Backend == "loadingcache" ? Builder(options).Build() : null;
        using var memory = options.Backend == "memorycache" ? Memory(options) : null;
        var entryOptions = new MemoryCacheEntryOptions().SetSize(1);
        long hits = 0,
            misses = 0,
            checksum = 0;
        var counters = StartCounters();
        var utcStarted = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        if (loading is not null)
        {
            foreach (int id in trace)
            {
                if (loading.TryGet(keys[id], out var value))
                {
                    Check(ReferenceEquals(value, values[id]), "trace value");
                    hits++;
                }
                else
                {
                    misses++;
                    loading.Put(keys[id], values[id]);
                }
                checksum += id;
            }
        }
        else
        {
            foreach (int id in trace)
            {
                if (memory!.TryGetValue(keys[id], out Payload? value))
                {
                    Check(ReferenceEquals(value, values[id]), "trace value");
                    hits++;
                }
                else
                {
                    misses++;
                    memory!.Set(keys[id], values[id], entryOptions);
                }
                checksum += id;
            }
        }
        long ended = Stopwatch.GetTimestamp();
        var utcEnded = DateTimeOffset.UtcNow;
        var deltas = EndCounters(counters);
        long countBeforeCleanup = loading?.EstimatedCount ?? memory!.Count;
        int cleanupPasses = loading is null ? 0 : Drain(loading);
        long finalCount = loading?.EstimatedCount ?? memory!.Count;
        object? statistics = loading is null ? memory!.GetCurrentStatistics() : loading.Statistics;
        Check(
            hits + misses == trace.Length && finalCount <= options.Capacity,
            "trace accounting/capacity"
        );
        return new
        {
            requests = trace.Length,
            hits,
            misses,
            backendCalls = misses,
            hitRatio = (double)hits / trace.Length,
            checksum,
            distinctKeys = keys.Length,
            canonicalTraceSha256 = Convert
                .ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", original) + "\n"))
                )
                .ToLowerInvariant(),
            utcStarted,
            utcEnded,
            startedTimestamp = started,
            endedTimestamp = ended,
            seconds = (double)(ended - started) / Stopwatch.Frequency,
            deltas,
            countBeforeCleanup,
            finalCount,
            cleanupPasses,
            statisticsBeforeValidation = statistics,
            interpretation = "engine-level observed hit ratio; native asynchronous compaction/admission; no policy-oracle or timing equivalence",
            validated = true,
        };
    }

    private static async Task<object> FanInAsync(Options options)
    {
        var key = new Key(0);
        var value = new Payload(0, 0);
        var gate = new TaskCompletionSource<Payload>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var invocations = new InvocationCounter();
        Task<Payload> Load(Key _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocations.Value);
            return gate.Task;
        }
        using var memory = options.Backend == "memorycache" ? Memory(options) : null;
        await using var loading =
            options.Backend == "loadingcache" ? Builder(options).BuildAsyncLoading(Load) : null;
        var calls = new Task<Payload?>[options.Workers];
        var counters = StartCounters();
        var utcStarted = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int index = 0; index < calls.Length; index++)
                calls[index] = loading is null
                    ? memory!.GetOrCreateAsync<Payload>(
                        key,
                        entry =>
                        {
                            entry.Size = 1;
                            Interlocked.Increment(ref invocations.Value);
                            return gate.Task;
                        }
                    )
                    : NullableTask(loading.GetAsync(key).AsTask());
            int expectedCalls = loading is null ? options.Workers : 1;
            while (Volatile.Read(ref invocations.Value) < expectedCalls)
            {
                Check(Stopwatch.GetElapsedTime(started) < Watchdog, "loader start watchdog");
                await Task.Yield();
            }
            int pendingBeforeRelease = calls.Count(call => !call.IsCompleted);
            Check(
                pendingBeforeRelease == options.Workers,
                "caller completed before loader gate release"
            );
            Check(invocations.Value == expectedCalls, "native fan-in backend work contract");
            gate.SetResult(value);
            var results = await Task.WhenAll(calls).WaitAsync(Watchdog).ConfigureAwait(false);
            long ended = Stopwatch.GetTimestamp();
            var utcEnded = DateTimeOffset.UtcNow;
            var deltas = EndCounters(counters);
            Check(results.All(result => ReferenceEquals(result, value)), "fanin caller value");
            return new
            {
                callers = calls.Length,
                pendingBeforeRelease,
                backendCalls = invocations.Value,
                successfulCallers = results.Length,
                failedCallers = 0,
                canceledCallers = 0,
                utcStarted,
                utcEnded,
                startedTimestamp = started,
                endedTimestamp = ended,
                seconds = (double)(ended - started) / Stopwatch.Frequency,
                deltas,
                interpretation = "gated overlapping native async calls; backend-work suppression contract, not a latency/speedup benchmark",
                validated = true,
            };
        }
        finally
        {
            gate.TrySetResult(value);
        }
    }

    private static async Task<Payload?> NullableTask(Task<Payload> task) =>
        await task.ConfigureAwait(false);

    private static int[] CreateTrace(Options options)
    {
        if (options.TraceFile is not null)
        {
            string raw = File.ReadAllText(options.TraceFile);
            int[] loaded = raw.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<int[]>(raw)!
                : raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => int.Parse(line, CultureInfo.InvariantCulture))
                    .ToArray();
            Check(loaded.Length > 0, "empty trace");
            return loaded;
        }
        var trace = new int[options.Operations];
        int capacity = options.Capacity;
        int universe = checked(capacity * 4);
        uint state = (uint)options.Seed;
        double[] cdf = new double[universe];
        double sum = 0;
        for (int i = 0; i < universe; i++)
            cdf[i] = sum += 1 / Math.Pow(i + 1, 1.1);
        for (int i = 0; i < trace.Length; i++)
        {
            state = unchecked(state * 1664525 + 1013904223);
            double uniform = (state + 0.5) / ((double)uint.MaxValue + 1);
            int zipf = Array.BinarySearch(cdf, uniform * sum);
            if (zipf < 0)
                zipf = ~zipf;
            trace[i] = options.TraceKind switch
            {
                "scan" => i,
                "uniform" => (int)((ulong)state * (uint)universe >> 32),
                "zipf" => zipf,
                "hotset-scan" => i % (capacity * 8) < capacity * 6
                    ? (int)((ulong)state * (uint)(capacity / 2) >> 32)
                    : capacity + i,
                "phase" => (int)((ulong)state * (uint)(capacity / 2) >> 32)
                    + (i < trace.Length / 2 ? 0 : capacity * 2),
                "cycle" => i % (capacity + capacity / 8),
                _ => throw new ArgumentException(
                    "--trace-kind scan|uniform|zipf|hotset-scan|phase|cycle"
                ),
            };
        }
        return trace;
    }

    private static int Drain(ICache<Key, Payload> cache)
    {
        for (int pass = 1; pass <= 256; pass++)
        {
            cache.CleanUp();
            if (
                cache.Statistics.MaintenanceBacklog == 0
                && cache.Policy.Eviction!.WeightedSize <= cache.Policy.Eviction.Maximum
            )
                return pass;
        }
        throw new InvalidOperationException("LoadingCache cleanup did not converge");
    }

    private static void Signal(Barrier barrier) =>
        Check(barrier.SignalAndWait(Watchdog), "worker barrier timeout");

    private static Counters StartCounters() =>
        new(
            GC.GetTotalAllocatedBytes(true),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            Monitor.LockContentionCount
        );

    private static Counters EndCounters(Counters start) =>
        new(
            GC.GetTotalAllocatedBytes(true) - start.ProcessAllocatedBytes,
            GC.CollectionCount(0) - start.Gen0,
            GC.CollectionCount(1) - start.Gen1,
            GC.CollectionCount(2) - start.Gen2,
            Monitor.LockContentionCount - start.LockContentions
        );

    private static object Metadata()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            root is not null
            && !File.Exists(Path.Combine(root.FullName, "Directory.Packages.props"))
        )
            root = root.Parent;
        Check(root is not null, "repository root for source metadata");
        string[] directories = ["src/LoadingCache", "tools/LoadingCache.MemoryCacheProbe"];
        var sources = directories
            .SelectMany(directory =>
                Directory.EnumerateFiles(
                    Path.Combine(root!.FullName, directory),
                    "*",
                    SearchOption.AllDirectories
                )
            )
            .Where(path =>
                !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal)
            )
            .Where(path =>
                path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal)
            )
            .Concat(BuildMetadataFiles.Select(path => Path.Combine(root!.FullName, path)))
            .Order(StringComparer.Ordinal)
            .Select(path => new
            {
                path = Path.GetRelativePath(root!.FullName, path),
                sha256 = Hash(path),
            })
            .ToArray();
        Assembly[] assemblies =
        [
            typeof(Program).Assembly,
            typeof(CacheBuilder).Assembly,
            typeof(MemoryCache).Assembly,
            typeof(MemoryCacheEntryOptions).Assembly,
            typeof(object).Assembly,
        ];
        return new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription,
            logicalProcessors = Environment.ProcessorCount,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
            stopwatchFrequency = Stopwatch.Frequency,
            processPath = Environment.ProcessPath,
            processSha256 = Environment.ProcessPath is null ? null : Hash(Environment.ProcessPath),
            assemblies = assemblies.Select(assembly => new
            {
                name = assembly.GetName().Name,
                version = assembly.GetName().Version?.ToString(),
                informationalVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion,
                path = assembly.Location,
                sha256 = Hash(assembly.Location),
            }),
            sourceManifest = sources,
            sourceManifestSha256 = Convert
                .ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(sources)))
                .ToLowerInvariant(),
            environment = Environment
                .GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(entry =>
                    ((string)entry.Key).StartsWith("DOTNET_", StringComparison.Ordinal)
                    || ((string)entry.Key).StartsWith("COMPlus_", StringComparison.Ordinal)
                )
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value),
        };
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // Borrows the two cache references. The caller declares this scope after the cache
    // owners, so every worker is joined before either cache can be disposed.
    private sealed class ReplacementWorkers : IDisposable
    {
        private readonly Options _options;
        private readonly ICache<Key, Payload>? _loading;
        private readonly MemoryCache? _memory;
        private readonly MemoryCacheEntryOptions _entryOptions;
        private readonly Key[][] _keys;
        private readonly Payload[][] _values;
        private readonly Barrier _barrier;
        private readonly CountdownEvent _ready;
        private readonly Thread?[] _threads;
        private readonly long[] _allocated;
        private readonly ConcurrentQueue<Exception> _failures = new();
        private bool _stopping;

        public ReplacementWorkers(
            Options options,
            ICache<Key, Payload>? loading,
            MemoryCache? memory,
            MemoryCacheEntryOptions entryOptions,
            Key[][] keys,
            Payload[][] values
        )
        {
            _options = options;
            _loading = loading;
            _memory = memory;
            _entryOptions = entryOptions;
            _keys = keys;
            _values = values;
            _barrier = new Barrier(options.Workers + 1);
            _ready = new CountdownEvent(options.Workers);
            _threads = new Thread?[options.Workers];
            _allocated = new long[options.Workers];
            int started = 0;
            try
            {
                for (int worker = 0; worker < options.Workers; worker++)
                {
                    int id = worker;
                    var thread = new Thread(() => RunWorker(id))
                    {
                        IsBackground = true,
                        Name = $"MemoryCacheProbe.{worker}",
                    };
                    _threads[worker] = thread;
                    thread.Start();
                    started++;
                }
                Check(_ready.Wait(Watchdog), "worker readiness timeout");
            }
            catch
            {
                if (started < options.Workers)
                    _barrier.RemoveParticipants(options.Workers - started);
                Dispose();
                throw;
            }
        }

        public long AllocatedBytes => _allocated.Sum();

        public void RunBatches()
        {
            for (int batch = 0; batch < _options.Batches; batch++)
            {
                ThrowIfFailed();
                Signal(_barrier);
                ThrowIfFailed();
                Signal(_barrier);
            }
            ThrowIfFailed();
        }

        public void EnsureCompleted()
        {
            Join();
            ThrowIfFailed();
        }

        private void RunWorker(int id)
        {
            try
            {
                Key[] keys = _keys[id];
                Payload[] values = _values[id];
                _ready.Signal();
                for (int batch = 0; batch < _options.Batches; batch++)
                {
                    Signal(_barrier);
                    if (Volatile.Read(ref _stopping))
                        return;
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    int end = (batch + 1) * _options.Operations;
                    if (_loading is not null)
                    {
                        for (int index = batch * _options.Operations; index < end; index++)
                            _loading.Put(keys[index], values[index]);
                    }
                    else
                    {
                        for (int index = batch * _options.Operations; index < end; index++)
                            _memory!.Set(keys[index], values[index], _entryOptions);
                    }
                    _allocated[id] += GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Signal(_barrier);
                }
            }
            catch (Exception exception)
            {
                _failures.Enqueue(exception);
                Volatile.Write(ref _stopping, true);
            }
            finally
            {
                _barrier.RemoveParticipant();
            }
        }

        private void ThrowIfFailed()
        {
            if (!_failures.IsEmpty)
                throw new AggregateException(_failures);
        }

        private void Join()
        {
            foreach (Thread? thread in _threads)
                if (thread is { IsAlive: true })
                    Check(thread.Join(Watchdog), "worker join timeout");
        }

        public void Dispose()
        {
            Volatile.Write(ref _stopping, true);
            _barrier.RemoveParticipant();
            Join();
            _barrier.Dispose();
            _ready.Dispose();
        }
    }

    private sealed class InvocationCounter
    {
        public int Value;
    }

    private sealed class Key(int id) : IEquatable<Key>
    {
        public int Id { get; } = id;

        public bool Equals(Key? other) => other?.Id == Id;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => Id;
    }

    private sealed record Payload(int KeyId, int Version);

    private sealed record Counters(
        long ProcessAllocatedBytes,
        int Gen0,
        int Gen1,
        int Gen2,
        long LockContentions
    );

    private sealed record Options(
        string Backend,
        string Scenario,
        int Capacity,
        int Workers,
        int Operations,
        int Batches,
        string ValueMode,
        bool Statistics,
        string TraceKind,
        string? TraceFile,
        int Seed,
        int Warmups,
        int Runs,
        int MinimumWarmupMilliseconds,
        string? Output
    )
    {
        public static Options Parse(string[] args)
        {
            Check(args.Length % 2 == 0, "options require name/value pairs");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
                Check(map.TryAdd(args[i], args[i + 1]), "duplicate option");
            string[] allowed =
            [
                "--backend",
                "--scenario",
                "--capacity",
                "--workers",
                "--operations",
                "--batches",
                "--value-mode",
                "--statistics",
                "--trace-kind",
                "--trace-file",
                "--seed",
                "--warmups",
                "--runs",
                "--minimum-warmup-ms",
                "--output",
            ];
            Check(
                map.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal)),
                "unknown option"
            );
            int Number(string key, int fallback, int min, int max)
            {
                int value = map.TryGetValue(key, out var raw)
                    ? int.Parse(raw, CultureInfo.InvariantCulture)
                    : fallback;
                Check(value >= min && value <= max, $"{key} out of range");
                return value;
            }
            string backend = map.GetValueOrDefault("--backend", "loadingcache");
            string mode = map.GetValueOrDefault("--value-mode", "changed");
            string statistics = map.GetValueOrDefault("--statistics", "off");
            Check(backend is "loadingcache" or "memorycache", "--backend loadingcache|memorycache");
            Check(mode is "same" or "changed", "--value-mode same|changed");
            Check(statistics is "on" or "off", "--statistics on|off");
            var options = new Options(
                backend,
                map.GetValueOrDefault("--scenario", "replacement"),
                Number("--capacity", 1024, 16, 1000000),
                Number("--workers", 4, 1, 128),
                Number("--operations", 65536, 1, 10000000),
                Number("--batches", 4, 1, 1024),
                mode,
                statistics == "on",
                map.GetValueOrDefault("--trace-kind", "zipf"),
                map.GetValueOrDefault("--trace-file"),
                Number("--seed", 419, 0, int.MaxValue),
                Number("--warmups", 2, 0, 100),
                Number("--runs", 3, 1, 100),
                Number("--minimum-warmup-ms", 0, 0, 60_000),
                map.GetValueOrDefault("--output")
            );
            Check(
                options.Scenario != "replacement"
                    || (long)options.Operations * options.Batches * options.Workers <= 50000000,
                "replacement preallocation exceeds 50 million operations"
            );
            Check(
                options.TraceFile is null || options.Scenario == "trace",
                "--trace-file requires trace scenario"
            );
            return options;
        }
    }
}
