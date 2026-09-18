using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadingCache.Maintenance;

namespace LoadingCache.ReadProbe;

internal static class CachePathDiagnostic
{
    private const int Residents = 1_024;
    private const int ChunkSize = 64;
    private const int ChunksPerSample = 8_192;
    private const int MeasuredSamples = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static int Run(string[] args)
    {
        if (
            args
                is not [
                    "--cache-path",
                    "accepted"
                    or "full",
                    "on"
                    or "off",
                    "none"
                    or "write"
                    or "access"
                    or "both",
                    _,
                ]
            || string.IsNullOrWhiteSpace(args[4])
            || args[4].StartsWith("--", StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "Usage: --cache-path accepted|full on|off none|write|access|both output.json"
            );
        }

        string outputPath = Path.GetFullPath(args[4]);
        bool accepted = args[1] == "accepted";
        bool statistics = args[2] == "on";
        string expiration = args[3];
        ManualScheduler scheduler = new();
        CacheEngine<object, int> engine = new(
            new CacheEngineOptions<object, int>
            {
                MaximumSize = Residents,
                MaxConcurrentLoads = 1,
                RecordStatistics = statistics,
                ExpireAfterWrite = expiration is "write" or "both" ? TimeSpan.FromHours(1) : null,
                ExpireAfterAccess = expiration is "access" or "both" ? TimeSpan.FromHours(1) : null,
                TimeProvider = TimeProvider.System,
                MaintenanceScheduler = scheduler,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = ChunkSize,
            }
        );
        try
        {
            using Cache<object, int> cache = new(engine);
            object[] keys = new object[Residents];
            List<Sample> samples = [];
            for (int key = 0; key < keys.Length; key++)
            {
                keys[key] = key;
                cache.Put(keys[key], key);
            }
            Drain(cache, engine, scheduler);

            long warmupStarted = Stopwatch.GetTimestamp();
            do
            {
                samples.Add(
                    RunSample(
                        cache,
                        engine,
                        scheduler,
                        keys,
                        accepted,
                        statistics,
                        samples.Count,
                        true
                    )
                );
            } while (Stopwatch.GetElapsedTime(warmupStarted) < TimeSpan.FromSeconds(1));
            long warmupWallTicks = Stopwatch.GetTimestamp() - warmupStarted;
            int warmupSamples = samples.Count;
            for (int index = 0; index < MeasuredSamples; index++)
            {
                samples.Add(
                    RunSample(
                        cache,
                        engine,
                        scheduler,
                        keys,
                        accepted,
                        statistics,
                        samples.Count,
                        false
                    )
                );
            }

            var report = new
            {
                SchemaVersion = 1,
                Diagnostic = "controlled-cache-path",
                Path = args[1],
                Statistics = statistics,
                Expiration = expiration,
                ExpirationHours = expiration == "none" ? 0 : 1,
                TimeProvider = "System",
                ExpirationScheduler = false,
                MaintenanceScheduler = "manual; callbacks only run outside read intervals",
                Capacity = Residents,
                Residents,
                Workers = 1,
                KeyMode = "preboxed-int-as-object",
                Pattern = "cycle",
                ReadStripeCount = 1,
                ReadStripeCapacity = ChunkSize,
                ChunksPerSample,
                OperationsPerSample = (long)ChunkSize * ChunksPerSample,
                MeasuredSamples,
                WarmupSamples = warmupSamples,
                WarmupWallTicks = warmupWallTicks,
                WarmupOperations = (long)warmupSamples * ChunkSize * ChunksPerSample,
                WarmupPreparationOperations = accepted ? 0 : (long)warmupSamples * (ChunkSize + 1),
                Runtime = RuntimeInformation.FrameworkDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                OS = RuntimeInformation.OSDescription,
                LogicalProcessors = Environment.ProcessorCount,
                StopwatchFrequency = Stopwatch.Frequency,
                ServerGc = GCSettings.IsServerGC,
                GcLatencyMode = GCSettings.LatencyMode.ToString(),
                RuntimeEnvironment = new
                {
                    DotnetTieredCompilation = Environment.GetEnvironmentVariable(
                        "DOTNET_TieredCompilation"
                    ),
                    DotnetTieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                    ComPlusTieredCompilation = Environment.GetEnvironmentVariable(
                        "COMPlus_TieredCompilation"
                    ),
                    ComPlusTieredPgo = Environment.GetEnvironmentVariable("COMPlus_TieredPGO"),
                },
                CacheAssemblySha256 = Sha256(typeof(Cache<object, int>).Assembly.Location),
                HarnessAssemblySha256 = Sha256(typeof(CachePathDiagnostic).Assembly.Location),
                Arguments = args,
                ReadTimingScope = "sum of 64-hit loops; includes key indexing, miss counting and checksum",
                AllocationScope = "whole sample including preparation, cleanup and validation; excludes result serialization",
                Interpretation = "path-isolation diagnostic; not production throughput, p99, or a MemoryCache comparison",
                Samples = samples,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine
            );
            Console.Error.WriteLine($"wrote {outputPath}");
        }
        finally
        {
            scheduler.RunAll();
        }
        return 0;
    }

    private static Sample RunSample(
        Cache<object, int> cache,
        CacheEngine<object, int> engine,
        ManualScheduler scheduler,
        object[] keys,
        bool accepted,
        bool statistics,
        int sampleIndex,
        bool warmup
    )
    {
        long threadAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long processAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long wallStarted = Stopwatch.GetTimestamp();
        long cleanupTicks = Drain(cache, engine, scheduler);
        if (!accepted)
        {
            Snapshot empty = Capture(cache, engine);
            long fillChecksum = 0;
            int fillMisses = 0;
            ReadChunk(cache, keys, 0, ref fillChecksum, ref fillMisses);
            bool extraHit = cache.TryGet(keys[ChunkSize], out int extraValue);
            Snapshot full = Capture(cache, engine);
            Require(
                fillMisses == 0 && fillChecksum == ChunkSize * (ChunkSize - 1L) / 2,
                "Full preparation values"
            );
            Require(extraHit && extraValue == ChunkSize, "Full preparation extra hit");
            Require(
                full.Queued == ChunkSize && scheduler.Pending == 1,
                "Full preparation queue and schedule"
            );
            ValidateDeltas(empty, full, ChunkSize + 1, ChunkSize, 1, statistics);
        }

        Snapshot before = Capture(cache, engine);
        long checksum = 0;
        int misses = 0;
        long readTicks = 0;
        for (int chunk = 0; chunk < ChunksPerSample; chunk++)
        {
            if (accepted)
            {
                cleanupTicks += Drain(cache, engine, scheduler);
            }
            int keyOffset = chunk * ChunkSize & (Residents - 1);
            long readStarted = Stopwatch.GetTimestamp();
            ReadChunk(cache, keys, keyOffset, ref checksum, ref misses);
            readTicks += Stopwatch.GetTimestamp() - readStarted;
            Require(
                engine.GetPolicyReadBufferStatistics().Queued == ChunkSize,
                "Queue after chunk"
            );
        }

        Snapshot after = Capture(cache, engine);
        const long operations = (long)ChunkSize * ChunksPerSample;
        const long expectedChecksum = operations / Residents * (Residents * (Residents - 1L) / 2);
        Require(misses == 0 && checksum == expectedChecksum, "Read values and misses");
        ValidateDeltas(
            before,
            after,
            operations,
            accepted ? operations : 0,
            accepted ? 0 : operations,
            statistics
        );
        Require(scheduler.Pending == (accepted ? 0 : 1), "Scheduled callbacks remain controlled");
        cleanupTicks += Drain(cache, engine, scheduler);
        Snapshot final = Capture(cache, engine);
        Require(final.Queued == 0 && final.Enqueued == final.Dequeued, "Final transport balance");
        Require(
            final.Dequeued - after.Dequeued == (statistics ? ChunkSize : 0),
            "Final drain count"
        );
        long residentCount = cache.EstimatedCount;
        long weightedSize = cache.Policy.Eviction!.WeightedSize;
        Require(residentCount == Residents && weightedSize == Residents, "Final resident bounds");
        long wallTicks = Stopwatch.GetTimestamp() - wallStarted;
        long threadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - threadAllocatedBefore;
        long processAllocatedBytes =
            GC.GetTotalAllocatedBytes(precise: true) - processAllocatedBefore;
        return new Sample(
            sampleIndex,
            warmup,
            operations,
            accepted ? 0 : ChunkSize + 1,
            checksum,
            misses,
            before,
            after,
            final,
            statistics ? (after.Enqueued - before.Enqueued) / (double)operations
                : accepted ? 1
                : 0,
            statistics ? (after.DroppedFull - before.DroppedFull) / (double)operations
                : accepted ? 0
                : 1,
            statistics
                ? "exact counter deltas"
                : "controlled queue transitions; cumulative counters disabled",
            readTicks,
            readTicks * (1_000_000_000.0 / Stopwatch.Frequency) / operations,
            wallTicks,
            cleanupTicks,
            threadAllocatedBytes,
            processAllocatedBytes,
            [
                GC.CollectionCount(0) - gen0,
                GC.CollectionCount(1) - gen1,
                GC.CollectionCount(2) - gen2,
            ],
            residentCount,
            weightedSize,
            final.Queued,
            true
        );
    }

    private static void ReadChunk(
        Cache<object, int> cache,
        object[] keys,
        int keyOffset,
        ref long checksum,
        ref int misses
    )
    {
        for (int index = 0; index < ChunkSize; index++)
        {
            if (cache.TryGet(keys[keyOffset + index], out int value))
            {
                checksum += value;
            }
            else
            {
                misses++;
            }
        }
    }

    private static long Drain(
        Cache<object, int> cache,
        CacheEngine<object, int> engine,
        ManualScheduler scheduler
    )
    {
        long started = Stopwatch.GetTimestamp();
        cache.CleanUp();
        scheduler.RunAll();
        long ticks = Stopwatch.GetTimestamp() - started;
        Require(
            engine.GetPolicyReadBufferStatistics().Queued == 0 && scheduler.Pending == 0,
            "Empty queue after drain"
        );
        return ticks;
    }

    private static Snapshot Capture(Cache<object, int> cache, CacheEngine<object, int> engine)
    {
        CacheStatistics requests = cache.Statistics;
        ReadBufferStatistics buffer = engine.GetPolicyReadBufferStatistics();
        return new Snapshot(
            requests.Hits,
            requests.Misses,
            buffer.Queued,
            buffer.Enqueued,
            buffer.Dequeued,
            buffer.DroppedFull,
            buffer.DroppedFailed,
            buffer.DroppedShutdown
        );
    }

    private static void ValidateDeltas(
        Snapshot before,
        Snapshot after,
        long operations,
        long accepted,
        long full,
        bool statistics
    )
    {
        Require(
            after.Hits - before.Hits == (statistics ? operations : 0)
                && after.Misses == before.Misses,
            "Request counters"
        );
        Require(
            after.Enqueued - before.Enqueued == (statistics ? accepted : 0),
            "Accepted counter"
        );
        Require(after.DroppedFull - before.DroppedFull == (statistics ? full : 0), "Full counter");
        Require(
            after.DroppedFailed == before.DroppedFailed
                && after.DroppedShutdown == before.DroppedShutdown,
            "Unexpected transport drops"
        );
        if (!statistics)
        {
            Require(
                after
                    is {
                        Hits: 0,
                        Misses: 0,
                        Enqueued: 0,
                        Dequeued: 0,
                        DroppedFull: 0,
                        DroppedFailed: 0,
                        DroppedShutdown: 0,
                    },
                "Disabled counters"
            );
        }
    }

    private static void Require(bool condition, string invariant)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Cache-path diagnostic invariant failed: {invariant}."
            );
        }
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class ManualScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunAll()
        {
            while (_callbacks.TryDequeue(out Action? callback))
            {
                callback();
            }
        }
    }

    private readonly record struct Snapshot(
        long Hits,
        long Misses,
        long Queued,
        long Enqueued,
        long Dequeued,
        long DroppedFull,
        long DroppedFailed,
        long DroppedShutdown
    );

    private sealed record Sample(
        [property: JsonInclude] int SampleIndex,
        [property: JsonInclude] bool Warmup,
        [property: JsonInclude] long Operations,
        [property: JsonInclude] int PreparationOperations,
        [property: JsonInclude] long Checksum,
        [property: JsonInclude] int Misses,
        [property: JsonInclude] Snapshot Before,
        [property: JsonInclude] Snapshot After,
        [property: JsonInclude] Snapshot Final,
        [property: JsonInclude] double AcceptedFraction,
        [property: JsonInclude] double FullFraction,
        [property: JsonInclude] string FractionEvidence,
        [property: JsonInclude] long ReadTicks,
        [property: JsonInclude] double ReadNanosecondsPerOperation,
        [property: JsonInclude] long WholeWallTicks,
        [property: JsonInclude] long CleanupTicks,
        [property: JsonInclude] long ThreadAllocatedBytes,
        [property: JsonInclude] long ProcessAllocatedBytes,
        [property: JsonInclude] int[] GcCollections,
        [property: JsonInclude] long ResidentCount,
        [property: JsonInclude] long WeightedSize,
        [property: JsonInclude] long FinalQueued,
        [property: JsonInclude] bool Validated
    );
}
