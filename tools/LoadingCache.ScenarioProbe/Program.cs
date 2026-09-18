using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadingCache.ScenarioProbe;

internal static class Program
{
    private static readonly string[] Names =
    [
        "size-churn",
        "weight-churn",
        "weight-replace",
        "mixed",
        "ttl",
        "tti",
        "variable",
        "variable-update",
        "ttl-cleanup",
        "tti-cleanup",
        "variable-cleanup",
        "runtime-maximum",
        "runtime-expiry",
        "runtime-access",
        "runtime-refresh",
        "runtime-variable",
        "variable-put",
        "eviction-listener",
        "removal-listener",
        "strong-lookup",
        "quiet-lookup",
        "weak-key-lookup",
        "weak-value-lookup",
        "weak-key-cleanup",
        "weak-value-cleanup",
        "sync-miss",
        "manual-sync-miss",
        "sync-fanin-contract",
        "manual-sync-fanin-contract",
        "async-completed-miss",
        "manual-async-completed-miss",
        "async-gated-miss",
        "manual-async-gated-miss",
        "async-gated-fanin",
        "manual-async-gated-fanin",
        "refresh-explicit",
        "refresh-auto",
        "bulk-sync",
        "bulk-async",
        "prefetch-sync",
        "prefetch-async",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(10);

    private static async Task Main(string[] args)
    {
        var options = Options.Parse(args);
        string[] selected = options.Scenario == "all" ? Names : [options.Scenario];
        if (
            selected.Any(name =>
                name != "trace-policy" && !Names.Contains(name, StringComparer.Ordinal)
            )
        )
            throw new ArgumentException("Unknown scenario.");
        List<Sample> samples = [];
        foreach (string name in selected)
            for (int index = 0; index < options.Warmups + options.Runs; index++)
            {
                var sample = await RunAsync(name, options, index).ConfigureAwait(false);
                samples.Add(sample);
                await Console.Error.WriteLineAsync(
                    $"{name} sample={index} operations={sample.Operations} backend={sample.BackendCalls} seconds={sample.Seconds:F6}"
                );
            }
        var report = new
        {
            schemaVersion = 1,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            stopwatchFrequency = Stopwatch.Frequency,
            serverGc = System.Runtime.GCSettings.IsServerGC,
            cacheSha256 = Hash(typeof(CacheBuilder).Assembly.Location),
            harnessSha256 = Hash(typeof(Program).Assembly.Location),
            argv = args,
            traceSha256 = options.TraceFile is null ? null : Hash(options.TraceFile),
            options,
            allocationScope = "whole process allocated bytes during timed scenario including driver, validation, maintenance, loader and callback workers; not directly comparable to Java driver-thread bytes",
            timingScope = "closed-loop wall time including scenario actions, inline validation and final maintenance; excludes builder, trace and object setup, final snapshot validation, disposal and JSON; no per-request latency percentiles",
            samples,
        };
        string json = JsonSerializer.Serialize(report, JsonOptions);
        if (options.Output is null)
            Console.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
            await File.WriteAllTextAsync(options.Output, json + Environment.NewLine)
                .ConfigureAwait(false);
        }
    }

    private static async Task<Sample> RunAsync(string name, Options options, int index)
    {
        switch (name)
        {
            case "sync-fanin-contract" or "manual-sync-fanin-contract":
                return SyncFanIn(name, options, index);
            case "weak-key-cleanup" or "weak-value-cleanup":
                return ReferenceCleanup(name, options, index);
        }
        var clock = new ManualClock();
        int[] trace = options.TraceFile is null
            ? CreateTrace(options.Cycles, options.Seed, options.Capacity * 4)
            : JsonSerializer.Deserialize<int[]>(
                await File.ReadAllTextAsync(options.TraceFile).ConfigureAwait(false)
            ) ?? throw new ArgumentException("Empty trace.");
        Check(
            trace.Length == options.Cycles && trace.All(key => key is >= 0 and <= 1_000_000),
            "trace length/key bounds"
        );
        var keys = Enumerable
            .Range(0, Math.Max(options.Capacity * 4 + 32, trace.Max() + 1))
            .Select(id => new Key(id))
            .ToArray();
        var values = keys.Select(key => new Value(key.Id + 1, key.Id % 4 + 1)).ToArray();
        var alternate = keys.Select(key => new Value(key.Id + 10001, key.Id % 2 + 1)).ToArray();
        Value[] weightReplacementTrace =
            name == "weight-replace"
                ? CreateWeightReplacementTrace(trace, options.Capacity / 8, values)
                : [];
        Value?[] previousWeightValues =
            name == "weight-replace" ? new Value?[options.Capacity / 8] : [];
        int expectedWeightChanges =
            name == "weight-replace"
                ? options.Cycles
                    - trace.Select(key => key % (options.Capacity / 8)).Distinct().Count()
                : 0;
        CallbackCounter callbacks = new();
        var builder = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(options.Capacity)
            .MaxConcurrentLoads(32)
            .MaxPendingLoadKeys(64)
            .MaximumBulkKeys(32)
            .TimeProvider(clock);
        if (options.Statistics)
            builder.RecordStatistics();
        if (name.StartsWith("weight", StringComparison.Ordinal))
            builder = CacheBuilder
                .Create<Key, Value>()
                .MaximumWeight(options.Capacity)
                .MaximumResidentCount(options.Capacity * 4)
                .Weigher(static (_, value) => value.Weight)
                .MaxConcurrentLoads(32)
                .TimeProvider(clock);
        if (options.Statistics && name.StartsWith("weight", StringComparison.Ordinal))
            builder.RecordStatistics();
        switch (name)
        {
            case "ttl" or "ttl-cleanup" or "runtime-expiry":
                builder.ExpireAfterWrite(Duration);
                break;
            case "tti" or "tti-cleanup" or "runtime-access":
                builder.ExpireAfterAccess(Duration);
                break;
            case "variable"
            or "variable-update"
            or "variable-cleanup"
            or "runtime-variable"
            or "variable-put":
                builder.ExpireAfter(new Expiry());
                break;
            case "refresh-auto" or "runtime-refresh":
                builder.RefreshAfterWrite(Duration);
                break;
            case "weak-key-lookup":
                builder.WeakKeys();
                break;
            case "weak-value-lookup":
                builder.WeakValues();
                break;
            case "eviction-listener":
                builder.EvictionListener(callbacks.Record);
                break;
        }
        if (name == "removal-listener")
            builder.NotificationCapacity(options.Cycles + 16).RemovalListener(callbacks.Record);
        bool asyncMode = name.Contains("async", StringComparison.Ordinal);
        var loader = new Loader(
            values,
            name.StartsWith("prefetch", StringComparison.Ordinal),
            name.Contains("gated", StringComparison.Ordinal)
        );
        ICache<Key, Value>? sync = null;
        IAsyncCache<Key, Value>? asyncCache = null;
        if (asyncMode)
            asyncCache = name.StartsWith("manual", StringComparison.Ordinal)
                ? builder.BuildAsync()
                : builder.BuildAsyncLoading(loader);
        else
            sync = name
                is "sync-miss"
                    or "refresh-explicit"
                    or "refresh-auto"
                    or "runtime-refresh"
                    or "bulk-sync"
                    or "prefetch-sync"
                ? builder.BuildLoading(loader)
                : builder.Build();
        try
        {
            if (name.EndsWith("lookup", StringComparison.Ordinal))
            {
                for (int key = 0; key < options.Capacity / 2; key++)
                    sync!.Put(keys[key], values[key]);
                Drain(sync!);
                Check(
                    sync!.TryGet(new Key(0), out _) == (name != "weak-key-lookup"),
                    "reference key equality"
                );
            }
            var model = new Dictionary<int, Value>();
            long operations = 0,
                checksum = 0,
                hits = 0,
                misses = 0;
            int drains = 0;
            int weightChanges = 0;
            var utcStarted = DateTimeOffset.UtcNow;
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            long started = Stopwatch.GetTimestamp();
            for (int cycle = 0; cycle < options.Cycles; cycle++)
            {
                int id = trace[cycle];
                switch (name)
                {
                    case "size-churn":
                    case "weight-churn":
                    case "eviction-listener":
                        sync!.Put(keys[id], values[id]);
                        operations++;
                        break;
                    case "trace-policy":
                        if (sync!.TryGet(keys[id], out var traced))
                        {
                            Check(ReferenceEquals(traced, values[id]), "trace value");
                            hits++;
                            checksum += traced.Id;
                        }
                        else
                        {
                            misses++;
                            sync.Put(keys[id], values[id]);
                            operations++;
                        }
                        operations++;
                        drains += Drain(sync);
                        break;
                    case "weight-replace":
                        id %= options.Capacity / 8;
                        var replacement = weightReplacementTrace[cycle];
                        if (previousWeightValues[id] is { } previousWeightValue)
                        {
                            Check(
                                !ReferenceEquals(previousWeightValue, replacement)
                                    && previousWeightValue.Weight != replacement.Weight,
                                "replacement must change value identity and weight for the same key"
                            );
                            weightChanges++;
                        }
                        sync!.Put(keys[id], replacement);
                        operations++;
                        Check(
                            sync.TryGet(keys[id], out var replaced)
                                && ReferenceEquals(replaced, replacement),
                            "replacement lost"
                        );
                        operations++;
                        checksum += replaced!.Id;
                        previousWeightValues[id] = replaced;
                        hits++;
                        break;
                    case "mixed":
                        id %= options.Capacity / 2;
                        switch (cycle % 10)
                        {
                            case < 2:
                                sync!.Put(keys[id], values[id]);
                                model[id] = values[id];
                                break;
                            case 2:
                                sync!.Invalidate(keys[id]);
                                model.Remove(id);
                                break;
                            default:
                                bool found = sync!.TryGet(keys[id], out var mixed);
                                Check(found == model.ContainsKey(id), "mixed presence");
                                if (found)
                                {
                                    Check(ReferenceEquals(mixed, model[id]), "mixed value");
                                    checksum += mixed!.Id;
                                    hits++;
                                }
                                else
                                    misses++;
                                break;
                        }
                        operations++;
                        break;
                    case "ttl":
                    case "tti":
                    case "variable":
                        sync!.Put(keys[0], values[0]);
                        operations++;
                        clock.Advance(6);
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            true,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        clock.Advance(6);
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            name != "ttl",
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        clock.Advance(11);
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            false,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "variable-update":
                        sync!.Put(keys[0], values[0]);
                        operations++;
                        clock.Advance(6);
                        sync.Put(keys[0], alternate[0]);
                        operations++;
                        clock.Advance(6);
                        Read(
                            sync,
                            keys[0],
                            alternate[0],
                            true,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        clock.Advance(11);
                        Read(
                            sync,
                            keys[0],
                            alternate[0],
                            false,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "ttl-cleanup":
                    case "tti-cleanup":
                    case "variable-cleanup":
                        for (int k = 0; k < 16; k++)
                        {
                            sync!.Put(keys[k], values[k]);
                            operations++;
                        }
                        drains += Drain(sync!);
                        operations++;
                        Check(sync!.EstimatedCount == 16, "expiry prefill not fully registered");
                        clock.Advance(2000);
                        drains += Drain(sync);
                        operations++;
                        Check(sync.EstimatedCount == 0, "expired entries survived cleanup");
                        break;
                    case "runtime-maximum":
                        sync!.Policy.Eviction!.SetMaximum(
                            (cycle & 1) == 0 ? options.Capacity : options.Capacity / 2
                        );
                        operations++;
                        for (int k = 0; k < 16; k++)
                        {
                            sync.Put(keys[(id + k) % keys.Length], values[(id + k) % keys.Length]);
                            operations++;
                        }
                        break;
                    case "runtime-expiry":
                    case "runtime-access":
                        var fixedPolicy =
                            name == "runtime-access"
                                ? sync!.Policy.ExpireAfterAccess!
                                : sync!.Policy.ExpireAfterWrite!;
                        fixedPolicy.SetDuration(Duration);
                        operations++;
                        sync.Put(keys[0], values[0]);
                        operations++;
                        clock.Advance(6);
                        fixedPolicy.SetDuration(TimeSpan.FromMilliseconds(5));
                        operations++;
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            false,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "runtime-variable":
                        sync!.Put(keys[0], values[0]);
                        operations++;
                        Check(
                            sync.Policy.VariableExpiration!.SetExpiresAfter(
                                keys[0],
                                TimeSpan.FromMilliseconds(5)
                            ),
                            "variable mutation absent"
                        );
                        operations++;
                        clock.Advance(6);
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            false,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "variable-put":
                        sync!.Put(keys[0], values[0], TimeSpan.FromMilliseconds(5));
                        operations++;
                        clock.Advance(6);
                        Read(
                            sync,
                            keys[0],
                            values[0],
                            false,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "quiet-lookup":
                        id %= options.Capacity / 2;
                        Check(
                            sync!.Policy.TryGetQuietly(keys[id], out var quiet)
                                && ReferenceEquals(quiet, values[id]),
                            "quiet lookup value"
                        );
                        operations++;
                        hits++;
                        checksum += quiet!.Id;
                        break;
                    case "removal-listener":
                        sync!.Put(keys[0], (cycle & 1) == 0 ? values[0] : alternate[0]);
                        operations++;
                        break;
                    case "strong-lookup":
                    case "weak-key-lookup":
                    case "weak-value-lookup":
                        id %= options.Capacity / 2;
                        Read(
                            sync!,
                            keys[id],
                            values[id],
                            true,
                            ref operations,
                            ref checksum,
                            ref hits,
                            ref misses
                        );
                        break;
                    case "sync-miss":
                    case "manual-sync-miss":
                        sync!.Invalidate(keys[0]);
                        operations++;
                        var loaded =
                            name == "sync-miss"
                                ? ((ILoadingCache<Key, Value>)sync).Get(keys[0])
                                : sync.GetOrAdd(keys[0], loader.Load);
                        Check(ReferenceEquals(loaded, values[0]), "sync load");
                        checksum += loaded.Id;
                        operations++;
                        break;
                    case "async-completed-miss":
                    case "manual-async-completed-miss":
                    case "async-gated-miss":
                    case "manual-async-gated-miss":
                    case "async-gated-fanin":
                    case "manual-async-gated-fanin":
                        asyncCache!.Invalidate(keys[0]);
                        operations++;
                        bool gated = name.Contains("gated", StringComparison.Ordinal);
                        if (gated)
                            loader.Gate = new TaskCompletionSource<Value>();
                        int count =
                            gated && name.Contains("fanin", StringComparison.Ordinal)
                                ? options.FanIn
                                : 1;
                        var requests = new Task<Value>[count];
                        for (int waiter = 0; waiter < count; waiter++)
                        {
                            requests[waiter] = asyncCache is IAsyncLoadingCache<Key, Value> loading
                                ? loading.GetAsync(keys[0]).AsTask()
                                : asyncCache.GetOrAddAsync(keys[0], loader.LoadAsync).AsTask();
                            operations++;
                        }
                        if (gated)
                        {
                            Check(
                                requests.All(task => !task.IsCompleted),
                                "gated requests completed before release"
                            );
                            loader.Gate!.SetResult(values[0]);
                        }
                        foreach (var request in requests)
                        {
                            var result = await request
                                .WaitAsync(TimeSpan.FromSeconds(30))
                                .ConfigureAwait(false);
                            Check(ReferenceEquals(result, values[0]), "async value");
                            checksum += result.Id;
                        }
                        break;
                    case "refresh-explicit":
                    case "refresh-auto":
                    case "runtime-refresh":
                    {
                        if (name == "runtime-refresh")
                        {
                            sync!.Policy.RefreshAfterWrite!.SetDuration(Duration);
                            operations++;
                        }
                        sync!.Put(keys[0], alternate[0]);
                        operations++;
                        var refreshing = (ILoadingCache<Key, Value>)sync;
                        if (name != "refresh-explicit")
                        {
                            clock.Advance(name == "runtime-refresh" ? 6 : 11);
                            if (name == "runtime-refresh")
                            {
                                sync.Policy.RefreshAfterWrite!.SetDuration(
                                    TimeSpan.FromMilliseconds(5)
                                );
                                operations++;
                            }
                            var stale = refreshing.Get(keys[0]);
                            operations++;
                            Check(
                                ReferenceEquals(stale, alternate[0])
                                    || ReferenceEquals(stale, values[0]),
                                "refresh read"
                            );
                            await UntilAsync(() =>
                                    refreshing.Policy.TryGetQuietly(keys[0], out var current)
                                    && ReferenceEquals(current, values[0])
                                )
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            Check(
                                ReferenceEquals(
                                    await refreshing
                                        .RefreshAsync(keys[0])
                                        .WaitAsync(TimeSpan.FromSeconds(30))
                                        .ConfigureAwait(false),
                                    values[0]
                                ),
                                "refresh result"
                            );
                            operations++;
                        }
                        checksum += values[0].Id;
                        break;
                    }
                    case "bulk-sync":
                    case "prefetch-sync":
                    case "bulk-async":
                    case "prefetch-async":
                        var batch = keys.AsSpan(0, 16).ToArray();
                        if (asyncMode)
                        {
                            asyncCache!.Clear();
                            operations++;
                            var result = await ((IAsyncLoadingCache<Key, Value>)asyncCache)
                                .GetAllAsync(batch)
                                .ConfigureAwait(false);
                            ValidateBulk(result, values, ref checksum);
                        }
                        else
                        {
                            sync!.Clear();
                            operations++;
                            var result = ((ILoadingCache<Key, Value>)sync).GetAll(batch);
                            ValidateBulk(result, values, ref checksum);
                        }
                        operations++;
                        if (name.StartsWith("prefetch", StringComparison.Ordinal))
                        {
                            bool present = asyncMode
                                ? asyncCache!.Policy.TryGetQuietly(
                                    loader.PrefetchKey,
                                    out var prefetched
                                )
                                : sync!.Policy.TryGetQuietly(loader.PrefetchKey, out prefetched);
                            Check(
                                present && ReferenceEquals(prefetched, values[16]),
                                "prefetch not published"
                            );
                            operations++;
                        }
                        break;
                    default:
                        throw new InvalidOperationException(name);
                }
            }
            if (sync is not null)
                drains += Drain(sync);
            else
            {
                for (int pass = 0; pass < 4; pass++)
                    asyncCache!.CleanUp();
                drains = 4;
            }
            if (name == "removal-listener")
                await UntilAsync(() => callbacks.Count == options.Cycles - 1).ConfigureAwait(false);
            long ended = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
            var utcEnded = DateTimeOffset.UtcNow;
            if (name == "weight-replace")
                Check(
                    weightChanges == expectedWeightChanges,
                    "weighted replacement transition count"
                );
            var policy = sync?.Policy ?? asyncCache!.Policy;
            long finalCount = sync?.EstimatedCount ?? asyncCache!.EstimatedCount;
            var stats = sync?.Statistics ?? asyncCache!.Statistics;
            if (name == "weight-replace")
                Check(
                    finalCount == options.Cycles - expectedWeightChanges
                        && policy.Eviction!.WeightedSize
                            == previousWeightValues.Sum(value => (long)(value?.Weight ?? 0)),
                    "weighted replacement final residents/weight metadata"
                );
            if (!options.Statistics)
                Check(stats is { Hits: 0, Misses: 0 }, "statistics disabled");
            else if (name.EndsWith("lookup", StringComparison.Ordinal) && name != "quiet-lookup")
                Check(stats.Hits >= options.Cycles, "statistics hit recording");
            if (options.Statistics && name == "quiet-lookup")
                Check(stats is { Hits: 1, Misses: 0 }, "quiet lookup altered statistics");
            Check(
                finalCount <= options.Capacity
                    && policy.Eviction!.WeightedSize <= policy.Eviction.Maximum,
                "capacity bound"
            );
            Check(stats.MaintenanceBacklog == 0, "maintenance backlog");
            long expectedBackend =
                name.Contains("miss", StringComparison.Ordinal)
                || name.Contains("fanin", StringComparison.Ordinal)
                || name.StartsWith("refresh", StringComparison.Ordinal)
                || name == "runtime-refresh"
                    ? options.Cycles
                    : 0;
            Check(loader.Calls == expectedBackend, "backend invocation count");
            long expectedBulk =
                name.StartsWith("bulk", StringComparison.Ordinal)
                || name.StartsWith("prefetch", StringComparison.Ordinal)
                    ? options.Cycles
                    : 0;
            Check(loader.BulkCalls == expectedBulk, "true bulk count");
            if (name is "size-churn" or "weight-churn" or "eviction-listener" or "runtime-maximum")
            {
                foreach (var pair in policy.Eviction!.Coldest(options.Capacity * 4))
                {
                    Check(
                        ReferenceEquals(pair.Value, values[pair.Key.Id]),
                        "resident value corrupt"
                    );
                    checksum += pair.Value.Id;
                }
            }
            GC.KeepAlive(keys);
            GC.KeepAlive(values);
            GC.KeepAlive(alternate);
            return new Sample(
                name,
                index,
                index < options.Warmups,
                operations,
                checksum,
                hits,
                misses,
                loader.Calls,
                loader.BulkCalls,
                callbacks.Count,
                finalCount,
                policy.Eviction!.WeightedSize,
                policy.Eviction.Maximum,
                stats.MaintenanceBacklog,
                drains,
                utcStarted,
                utcEnded,
                started,
                ended,
                (double)(ended - started) / Stopwatch.Frequency,
                allocated,
                true
            );
        }
        finally
        {
            sync?.Dispose();
            if (asyncCache is not null)
                await asyncCache.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Read(
        ICache<Key, Value> cache,
        Key key,
        Value expected,
        bool present,
        ref long operations,
        ref long checksum,
        ref long hits,
        ref long misses
    )
    {
        bool found = cache.TryGet(key, out var value);
        operations++;
        Check(found == present, "expiry/lookup presence");
        if (found)
        {
            Check(ReferenceEquals(value, expected), "lookup value");
            checksum += value!.Id;
            hits++;
        }
        else
            misses++;
    }

    private static Sample ReferenceCleanup(string name, Options options, int index)
    {
        var builder = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(options.Capacity)
            .MaxConcurrentLoads(1);
        if (name == "weak-key-cleanup")
            builder.WeakKeys();
        else
            builder.WeakValues();
        if (options.Statistics)
            builder.RecordStatistics();
        using var cache = builder.Build();
        (WeakReference weak, object retained) = PopulateUnrooted(cache, name == "weak-key-cleanup");
        var utcStart = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        int passes = 0;
        do
        {
            GC.Collect(2, GCCollectionMode.Forced, true, false);
            GC.WaitForPendingFinalizers();
            cache.CleanUp();
            passes++;
        } while ((weak.IsAlive || cache.EstimatedCount != 0) && passes < 32);
        Check(
            !weak.IsAlive && cache.EstimatedCount == 0,
            "weak target not collected/cleaned within bounded forced GC passes"
        );
        GC.KeepAlive(retained);
        long ended = Stopwatch.GetTimestamp();
        return new Sample(
            name,
            index,
            index < options.Warmups,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            options.Capacity,
            cache.Statistics.MaintenanceBacklog,
            passes,
            utcStart,
            DateTimeOffset.UtcNow,
            started,
            ended,
            (double)(ended - started) / Stopwatch.Frequency,
            -1,
            true
        );
    }

    private static Sample SyncFanIn(string name, Options options, int index)
    {
        using var run = new SyncFanInRun(name, options);
        return run.Execute(index);
    }

    private sealed class CallbackCounter
    {
        private long _count;
        internal long Count => Interlocked.Read(ref _count);

        internal void Record(RemovalNotification<Key, Value> _) =>
            Interlocked.Increment(ref _count);
    }

    private sealed class SyncFanInRun : IDisposable
    {
        private readonly string _name;
        private readonly Options _options;
        private readonly CountdownEvent _invoked;
        private readonly CountdownEvent _completed;
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Key _key = new(0);
        private readonly Value _expected = new(1, 1);
        private readonly ICache<Key, Value> _cache;
        private readonly Value?[] _values;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _failures = new();
        private readonly Thread?[] _threads;
        private long _calls;

        internal SyncFanInRun(string name, Options options)
        {
            _name = name;
            _options = options;
            _invoked = new CountdownEvent(_options.FanIn);
            _completed = new CountdownEvent(_options.FanIn);
            _values = new Value?[_options.FanIn];
            _threads = new Thread?[_options.FanIn];
            try
            {
                var builder = CacheBuilder
                    .Create<Key, Value>()
                    .MaximumSize(_options.Capacity)
                    .MaxConcurrentLoads(32);
                if (_options.Statistics)
                    builder.RecordStatistics();
                _cache =
                    _name == "sync-fanin-contract" ? builder.BuildLoading(Load) : builder.Build();
            }
            catch
            {
                DisposeGates();
                throw;
            }
        }

        private Value Load(Key _)
        {
            Interlocked.Increment(ref _calls);
            _entered.Set();
            Check(_release.Wait(TimeSpan.FromSeconds(30)), "loader gate timeout");
            return _expected;
        }

        internal Sample Execute(int index)
        {
            var utcStart = DateTimeOffset.UtcNow;
            long start = Stopwatch.GetTimestamp();
            try
            {
                for (int i = 0; i < _threads.Length; i++)
                {
                    int worker = i;
                    Thread thread = new(() =>
                    {
                        _invoked.Signal();
                        try
                        {
                            _values[worker] = _cache is ILoadingCache<Key, Value> loading
                                ? loading.Get(_key)
                                : _cache.GetOrAdd(_key, Load);
                        }
                        catch (Exception exception)
                        {
                            _failures.Enqueue(exception);
                        }
                        finally
                        {
                            _completed.Signal();
                        }
                    })
                    {
                        IsBackground = true,
                    };
                    _threads[i] = thread;
                    thread.Start();
                }
                Check(
                    _invoked.Wait(TimeSpan.FromSeconds(30))
                        && _entered.Wait(TimeSpan.FromSeconds(30)),
                    "callers did not enter"
                );
                Check(
                    _completed.CurrentCount == _options.FanIn,
                    "a gated call _completed before _release"
                );
                _release.Set();
                Check(_completed.Wait(TimeSpan.FromSeconds(30)), "callers failed to finish");
                Check(
                    _failures.IsEmpty
                        && _calls == 1
                        && _values.All(value => ReferenceEquals(value, _expected)),
                    "sync fan-in result/backend"
                );
                int drains = Drain(_cache);
                long end = Stopwatch.GetTimestamp();
                return new Sample(
                    _name,
                    index,
                    index < _options.Warmups,
                    0,
                    _options.FanIn,
                    0,
                    0,
                    _calls,
                    0,
                    0,
                    _cache.EstimatedCount,
                    _cache.Policy.Eviction!.WeightedSize,
                    _options.Capacity,
                    _cache.Statistics.MaintenanceBacklog,
                    drains,
                    utcStart,
                    DateTimeOffset.UtcNow,
                    start,
                    end,
                    (double)(end - start) / Stopwatch.Frequency,
                    -1,
                    true
                );
            }
            finally
            {
                _release.Set();
                foreach (Thread? thread in _threads)
                    if (thread is not null)
                        Check(
                            thread.Join(TimeSpan.FromSeconds(30)),
                            "sync caller did not terminate"
                        );
            }
        }

        private void DisposeGates()
        {
            using (_invoked)
            using (_completed)
            using (_entered)
            using (_release) { }
        }

        public void Dispose()
        {
            try
            {
                _cache.Dispose();
            }
            finally
            {
                DisposeGates();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Weak, object Retained) PopulateUnrooted(
        ICache<Key, Value> cache,
        bool weakKey
    )
    {
        var key = new Key(1);
        var value = new Value(2, 1);
        cache.Put(key, value);
        Drain(cache);
        return (new WeakReference(weakKey ? key : value), weakKey ? value : key);
    }

    private static void ValidateBulk(
        IReadOnlyDictionary<Key, Value> result,
        Value[] values,
        ref long checksum
    )
    {
        Check(result.Count == 16, "bulk count");
        foreach (var pair in result)
        {
            Check(
                pair.Key.Id < 16 && ReferenceEquals(pair.Value, values[pair.Key.Id]),
                "bulk value"
            );
            checksum += pair.Value.Id;
        }
    }

    private static int Drain(ICache<Key, Value> cache)
    {
        long started = Stopwatch.GetTimestamp();
        var wait = new SpinWait();
        for (int pass = 1; ; pass++)
        {
            cache.CleanUp();
            if (
                cache.Statistics.MaintenanceBacklog == 0
                && cache.Policy.Eviction!.WeightedSize <= cache.Policy.Eviction.Maximum
            )
                return pass;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                throw new InvalidOperationException(
                    "maintenance failed to converge within 30 seconds"
                );
            wait.SpinOnce();
        }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        long start = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(30))
                throw new TimeoutException("callback/refresh failed to finish");
            await Task.Yield();
        }
    }

    private static int[] CreateTrace(int count, int seed, int bound)
    {
        int[] trace = new int[count];
        uint state = (uint)seed;
        for (int i = 0; i < count; i++)
        {
            state = unchecked(state * 1664525 + 1013904223);
            trace[i] = (int)(state % (uint)bound);
        }
        return trace;
    }

    private static Value[] CreateWeightReplacementTrace(int[] trace, int keyCount, Value[] values)
    {
        int[] versions = new int[keyCount];
        var changed = values
            .Select(value => new Value(value.Id + 10000, value.Weight % 4 + 1))
            .ToArray();
        var replacements = new Value[trace.Length];
        for (int i = 0; i < trace.Length; i++)
        {
            int key = trace[i] % keyCount;
            replacements[i] = (versions[key]++ & 1) == 0 ? values[key] : changed[key];
        }
        return replacements;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Key(int id) : IEquatable<Key>
    {
        public int Id { get; } = id;

        public bool Equals(Key? other) => other?.Id == Id;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode() => Id;
    }

    private sealed record Value(int Id, int Weight);

    private sealed class ManualClock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => 1_000_000_000;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public void Advance(int milliseconds) =>
            Interlocked.Add(ref _now, milliseconds * 1_000_000L);
    }

    private sealed class Expiry : IExpiry<Key, Value>
    {
        public TimeSpan ExpireAfterCreate(Key key, Value value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterUpdate(Key key, Value value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterRead(Key key, Value value, TimeSpan currentDuration) => Duration;
    }

    private sealed class Loader(Value[] values, bool prefetch, bool gated)
        : IBulkSyncCacheLoader<Key, Value>,
            IBulkAsyncCacheLoader<Key, Value>
    {
        private long _calls,
            _bulkCalls;
        public long Calls => Interlocked.Read(ref _calls);
        public long BulkCalls => Interlocked.Read(ref _bulkCalls);
        public Key PrefetchKey { get; } = new(16);
        public TaskCompletionSource<Value>? Gate { get; set; }

        public Value Load(Key key)
        {
            Interlocked.Increment(ref _calls);
            return values[key.Id];
        }

        public Task<Value> LoadAsync(Key key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return gated ? Gate!.Task : Task.FromResult(values[key.Id]);
        }

        public IReadOnlyDictionary<Key, Value> LoadAll(IReadOnlyCollection<Key> keys)
        {
            Interlocked.Increment(ref _bulkCalls);
            var result = keys.ToDictionary(key => key, key => values[key.Id]);
            if (prefetch)
                result.Add(PrefetchKey, values[16]);
            return result;
        }

        public Task<IReadOnlyDictionary<Key, Value>> LoadAllAsync(
            IReadOnlyCollection<Key> keys,
            CancellationToken cancellationToken
        ) => Task.FromResult(LoadAll(keys));
    }

    private sealed record Sample(
        [property: JsonInclude] string Name,
        [property: JsonInclude] int Index,
        [property: JsonInclude] bool Warmup,
        long Operations,
        [property: JsonInclude] long Checksum,
        [property: JsonInclude] long Hits,
        [property: JsonInclude] long Misses,
        long BackendCalls,
        [property: JsonInclude] long BulkCalls,
        [property: JsonInclude] long Callbacks,
        [property: JsonInclude] long FinalCount,
        [property: JsonInclude] long FinalWeight,
        [property: JsonInclude] long Maximum,
        [property: JsonInclude] long MaintenanceBacklog,
        [property: JsonInclude] int DrainPasses,
        [property: JsonInclude] DateTimeOffset UtcStarted,
        [property: JsonInclude] DateTimeOffset UtcEnded,
        [property: JsonInclude] long StartedTimestamp,
        [property: JsonInclude] long EndedTimestamp,
        double Seconds,
        [property: JsonInclude] long AllocatedBytes,
        [property: JsonInclude] bool Validated
    );

    private sealed record Options(
        string Scenario,
        int Cycles,
        int Capacity,
        int FanIn,
        int Seed,
        int Warmups,
        int Runs,
        bool Statistics,
        string? Output,
        string? TraceFile
    )
    {
        public static Options Parse(string[] args)
        {
            if (args.Length % 2 != 0)
                throw new ArgumentException("Options require name/value pairs.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i += 2)
                if (!map.TryAdd(args[i], args[i + 1]))
                    throw new ArgumentException("Duplicate option.");
            string[] allowed =
            [
                "--scenario",
                "--cycles",
                "--capacity",
                "--fan-in",
                "--seed",
                "--warmups",
                "--runs",
                "--statistics",
                "--output",
                "--trace-file",
            ];
            if (map.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
                throw new ArgumentException("Unknown option.");
            string statistics = map.GetValueOrDefault("--statistics", "off");
            Check(
                map.GetValueOrDefault("--scenario")
                    == "trace-policy"
                    == map.ContainsKey("--trace-file"),
                "trace-policy requires --trace-file exclusively"
            );
            if (statistics is not ("on" or "off"))
                throw new ArgumentException("--statistics on|off");
            return new Options(
                map.GetValueOrDefault("--scenario", "all"),
                Number("--cycles", 1024, 2, 1000000),
                Number("--capacity", 1024, 64, 1000000),
                Number("--fan-in", 16, 2, 1024),
                Number("--seed", 419, 0, int.MaxValue),
                Number("--warmups", 2, 0, 100),
                Number("--runs", 3, 1, 100),
                statistics == "on",
                map.GetValueOrDefault("--output"),
                map.GetValueOrDefault("--trace-file")
            );

            int Number(string key, int fallback, int min, int max)
            {
                int value = map.TryGetValue(key, out string? raw)
                    ? int.Parse(raw, CultureInfo.InvariantCulture)
                    : fallback;
                if (value < min || value > max)
                    throw new ArgumentOutOfRangeException(key);
                return value;
            }
        }
    }
}
