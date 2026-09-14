using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CacheApi = global::LoadingCache;

namespace LoadingCache.WriteProbe;

internal static class Program
{
    private static readonly TimeSpan DrainWatchdog = TimeSpan.FromSeconds(30);
    private const int DefaultTargetOperationsPerSample = 131_072;
    private const int CaffeineTraceWindows = 4;
    private const int CaffeineTraceBatches = CaffeineTraceWindows * 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly PropertyInfo? WriteBufferBacklogProperty =
        typeof(CacheApi.CacheStatistics).GetProperty("WriteBufferBacklog");
    private static readonly PropertyInfo? WriteBufferPressureProperty =
        typeof(CacheApi.CacheStatistics).GetProperty("WriteBufferPressure");

    private static async Task<int> Main(string[] args)
    {
        ProbeOptions options = ProbeOptions.Parse(args);
        List<WorkloadSpec> workloads = CreateWorkloads(options).ToList();
        if (workloads.Count == 0)
        {
            throw new ArgumentException(
                "No workloads matched the selected caffeine filters. "
                    + "Check --capacity, --workers, --writes-per-worker, --statistics, and --value-mode."
            );
        }

        List<ProbeSample> samples = [];

        foreach (WorkloadSpec workload in workloads)
        {
            int totalSamples = options.Warmups + options.Runs;
            for (int sampleIndex = 0; sampleIndex < totalSamples; sampleIndex++)
            {
                bool warmup = sampleIndex < options.Warmups;
                ProbeSample sample = await RunSampleAsync(workload, options, sampleIndex, warmup)
                    .ConfigureAwait(false);
                samples.Add(sample);

                if (!options.NoProgress)
                {
                    Console.Error.WriteLine(
                        $"{workload.Name} sample={sampleIndex + 1}/{totalSamples} "
                            + $"warmup={warmup} timed={sample.TimedSeconds:F6}s "
                            + $"ops/s={sample.TimedOperationsPerSecond:F0}"
                    );
                }
            }
        }

        ProbeReport report = new(
            SchemaVersion: 5,
            Label: options.Label,
            Suite: options.Suite,
            Seed: options.Seed,
            Runs: options.Runs,
            Warmups: options.Warmups,
            SteadyOperationsPerWorker: options.SteadyOperationsPerWorker,
            TargetOperationsPerSample: options.TargetOperationsPerSample,
            Runtime: RuntimeInformation.FrameworkDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            OS: RuntimeInformation.OSDescription,
            LogicalProcessors: Environment.ProcessorCount,
            StopWatchFrequency: Stopwatch.Frequency,
            ServerGc: System.Runtime.GCSettings.IsServerGC,
            RuntimeEnvironment: RuntimeEnvironmentSnapshot.Create(),
            CacheAssemblySha256: Sha256(typeof(CacheApi.CacheBuilder).Assembly.Location),
            HarnessAssemblySha256: Sha256(typeof(Program).Assembly.Location),
            Samples: samples
        );
        string json = JsonSerializer.Serialize(report, JsonOptions);

        if (options.OutputPath is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(options.OutputPath, json + Environment.NewLine);
            Console.Error.WriteLine($"wrote {options.OutputPath}");
        }

        return 0;
    }

    private static IEnumerable<WorkloadSpec> CreateWorkloads(ProbeOptions options)
    {
        if (options.Suite is "repro" or "original" or "all")
        {
            foreach (int workers in new[] { 1, 4 })
            {
                foreach (int operations in new[] { 512, 4096 })
                {
                    yield return WorkloadSpec.Create(
                        "original",
                        1024,
                        workers,
                        operations,
                        statistics: true,
                        keyMode: "distinct",
                        targetOperations: options.TargetOperationsPerSample
                    );
                }
            }
        }

        if (options.Suite is "steady" or "all")
        {
            foreach (int capacity in new[] { 1024, 16384 })
            {
                foreach (int workers in new[] { 1, 4, 10 })
                {
                    foreach (bool statistics in new[] { false, true })
                    {
                        foreach (string keyMode in new[] { "distinct", "replace" })
                        {
                            yield return WorkloadSpec.Create(
                                "steady",
                                capacity,
                                workers,
                                options.SteadyOperationsPerWorker,
                                statistics,
                                keyMode,
                                targetOperations: options.TargetOperationsPerSample
                            );
                        }
                    }
                }
            }
        }

        if (options.Suite is "caffeine" or "all")
        {
            foreach (int capacity in new[] { 1024, 16384 })
            {
                foreach (int workers in new[] { 1, 4, 10 })
                {
                    foreach (int operations in new[] { 512, 4096 })
                    {
                        foreach (bool statistics in new[] { false, true })
                        {
                            foreach (string valueMode in new[] { "same", "changed" })
                            {
                                if (
                                    !options.MatchesCaffeine(
                                        capacity,
                                        workers,
                                        operations,
                                        statistics,
                                        valueMode
                                    )
                                )
                                {
                                    continue;
                                }

                                yield return WorkloadSpec.CreateCaffeine(
                                    capacity,
                                    workers,
                                    operations,
                                    statistics,
                                    valueMode,
                                    options.TargetOperationsPerSample
                                );
                            }
                        }
                    }
                }
            }
        }
    }

    private static async Task<ProbeSample> RunSampleAsync(
        WorkloadSpec workload,
        ProbeOptions options,
        int sampleIndex,
        bool warmup
    )
    {
        if (!warmup)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
        }

        TraceData trace = TraceData.Create(workload, options.Seed);
        using CacheApi.ICache<int, int> cache = BuildCache(workload);
        if (workload.KeyMode == "replace")
        {
            Prefill(cache, workload.Capacity, options.Seed);
        }
        else if (workload.KeyMode == "caffeine")
        {
            cache.Clear();
            cache.CleanUp();
        }

        PersistentExecution execution = await ExecutePersistentAsync(cache, workload, trace)
            .ConfigureAwait(false);
        CacheSnapshot final = Snapshot(cache);
        Validate(workload, trace, execution.Checksum, final);

        long timedOperations = checked(
            (long)workload.Batches * workload.Workers * workload.OperationsPerWorker
        );
        double timedSeconds = execution.WriteSeconds + execution.Drain.Seconds;
        return new ProbeSample(
            Warmup: warmup,
            SampleIndex: sampleIndex,
            Name: workload.Name,
            Kind: workload.Kind,
            Capacity: workload.Capacity,
            Workers: workload.Workers,
            OperationsPerWorker: workload.OperationsPerWorker,
            Batches: workload.Batches,
            TotalOperations: timedOperations,
            Statistics: workload.Statistics,
            KeyMode: workload.KeyMode,
            ValueMode: workload.ValueMode,
            PrefilledCount: workload.KeyMode == "replace" ? workload.Capacity : 0,
            SetupSeconds: execution.SetupSeconds,
            WriteSeconds: execution.WriteSeconds,
            WorkerWriteSeconds: execution.WorkerWriteSeconds,
            DrainSeconds: execution.Drain.Seconds,
            TimedSeconds: timedSeconds,
            WriteOperationsPerSecond: timedOperations / execution.WriteSeconds,
            TimedOperationsPerSecond: timedOperations / timedSeconds,
            WorkerAllocatedBytes: execution.WorkerAllocatedBytes,
            WholeProcessAllocatedBytes: execution.AllocatedBytes,
            DrainAllocatedBytes: execution.Drain.AllocatedBytes,
            TimedGcCollections: execution.GcCollections,
            DrainGcCollections: execution.Drain.GcCollections,
            DrainPasses: execution.Drain.Passes,
            BacklogBeforeDrain: execution.Drain.BacklogBefore,
            MaximumBacklogObserved: execution.Drain.MaximumBacklog,
            BacklogAfterDrain: execution.Drain.BacklogAfter,
            FinalEstimatedCount: final.EstimatedCount,
            FinalWeightedSize: final.WeightedSize,
            LockContentionDelta: execution.LockContentionDelta,
            Checksum: execution.Checksum,
            PublicBoundsHold: true,
            FinalStatistics: final.Statistics
        );
    }

    private static CacheApi.ICache<int, int> BuildCache(WorkloadSpec workload)
    {
        CacheApi.CacheBuilder<int, int> builder = CacheApi
            .CacheBuilder.Create<int, int>()
            .MaximumSize(workload.Capacity)
            .MaxConcurrentLoads(1);
        if (workload.Statistics)
        {
            builder.RecordStatistics();
        }

        return builder.Build();
    }

    private static void Prefill(CacheApi.ICache<int, int> cache, int capacity, int seed)
    {
        for (int key = 0; key < capacity; key++)
        {
            cache.Put(key, checked(seed + key + 1));
        }

        cache.CleanUp();
        if (cache.EstimatedCount != capacity)
        {
            throw new InvalidOperationException(
                $"Replacement prefill expected {capacity} residents, observed {cache.EstimatedCount}."
            );
        }
    }

    private static async Task<PersistentExecution> ExecutePersistentAsync(
        CacheApi.ICache<int, int> cache,
        WorkloadSpec workload,
        TraceData trace
    )
    {
        Barrier barrier = new(workload.Workers + 1);
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<Exception> failure = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        WorkerExecution[] workerResults = new WorkerExecution[
            checked(workload.Workers * workload.Batches)
        ];
        Thread[] threads = new Thread[workload.Workers];
        int readyCount = 0;

        long setupStarted = Stopwatch.GetTimestamp();
        try
        {
            for (int worker = 0; worker < workload.Workers; worker++)
            {
                int workerIndex = worker;
                threads[worker] = new Thread(() =>
                {
                    try
                    {
                        if (Interlocked.Increment(ref readyCount) == workload.Workers)
                        {
                            ready.TrySetResult(true);
                        }

                        for (int batch = 0; batch < workload.Batches; batch++)
                        {
                            SignalAndWait(barrier);
                            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                            long started = Stopwatch.GetTimestamp();
                            long checksum = 0;
                            int traceBatch =
                                workload.Kind == "caffeine" ? batch % CaffeineTraceBatches : batch;
                            int traceOffset = checked(
                                (traceBatch * workload.Workers + workerIndex)
                                * workload.OperationsPerWorker
                            );
                            for (int index = 0; index < workload.OperationsPerWorker; index++)
                            {
                                int traceIndex = traceOffset + index;
                                cache.Put(trace.Keys[traceIndex], trace.Values[traceIndex]);
                                checksum += trace.Values[traceIndex];
                            }

                            workerResults[batch * workload.Workers + workerIndex] = new(
                                Stopwatch.GetElapsedTime(started).TotalSeconds,
                                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                                checksum
                            );
                            SignalAndWait(barrier);
                        }

                        completed.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        failure.TrySetResult(exception);
                        ready.TrySetException(exception);
                        completed.TrySetException(exception);
                        try
                        {
                            barrier.RemoveParticipant();
                        }
                        catch (InvalidOperationException)
                        {
                            // The main participant will surface the original worker failure.
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = $"LoadingCache.WriteProbe.worker{workerIndex}",
                };
                threads[worker].Start();
            }

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            double setupSeconds = Stopwatch.GetElapsedTime(setupStarted).TotalSeconds;
            long[] writeCollectionsBefore = CollectionCounts();
            long allocatedBeforeProcess = GC.GetTotalAllocatedBytes(precise: false);
            long lockContentionBefore = Monitor.LockContentionCount;
            double writeSeconds = 0;
            double workerWriteSeconds = 0;
            long checksumTotal = 0;
            long workerAllocated = 0;
            DrainAggregate drain = new();

            for (int batch = 0; batch < workload.Batches; batch++)
            {
                long batchStarted = Stopwatch.GetTimestamp();
                SignalAndWait(barrier);
                SignalAndWait(barrier);
                writeSeconds += Stopwatch.GetElapsedTime(batchStarted).TotalSeconds;

                double batchWorkerSeconds = 0;
                for (int worker = 0; worker < workload.Workers; worker++)
                {
                    WorkerExecution result = workerResults[batch * workload.Workers + worker];
                    batchWorkerSeconds = Math.Max(batchWorkerSeconds, result.Seconds);
                    workerAllocated = checked(workerAllocated + result.AllocatedBytes);
                    checksumTotal = checked(checksumTotal + result.Checksum);
                }

                workerWriteSeconds += batchWorkerSeconds;
                drain.Add(Drain(cache, workload.Capacity));
            }

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Exception? workerFailure = failure.Task.IsCompletedSuccessfully
                ? failure.Task.Result
                : null;
            if (workerFailure is not null)
            {
                throw new InvalidOperationException("A persistent worker failed.", workerFailure);
            }

            return new PersistentExecution(
                setupSeconds,
                writeSeconds,
                workerWriteSeconds,
                GC.GetTotalAllocatedBytes(precise: false) - allocatedBeforeProcess,
                workerAllocated,
                CollectionDelta(writeCollectionsBefore),
                Monitor.LockContentionCount - lockContentionBefore,
                checksumTotal,
                drain.ToResult()
            );
        }
        finally
        {
            barrier.Dispose();
            foreach (Thread thread in threads)
            {
                if (thread is not null && thread.IsAlive)
                {
                    thread.Join(TimeSpan.FromSeconds(5));
                }
            }
        }
    }

    private static DrainResult Drain(CacheApi.ICache<int, int> cache, int capacity)
    {
        long started = Stopwatch.GetTimestamp();
        long[] collectionsBefore = CollectionCounts();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        CacheStatisticsData before = StatisticsSnapshot(cache);
        long maximumBacklog = before.MaintenanceBacklog;
        long previousCount = -1;
        long previousWeightedSize = -1;

        var wait = new SpinWait();
        for (int pass = 0; ; pass++)
        {
            cache.CleanUp();
            CacheSnapshot snapshot = Snapshot(cache);
            maximumBacklog = Math.Max(maximumBacklog, snapshot.Statistics.MaintenanceBacklog);
            bool stable =
                snapshot.Statistics.MaintenanceBacklog == 0
                && snapshot.EstimatedCount <= capacity
                && (snapshot.WeightedSize is null || snapshot.WeightedSize.Value <= capacity)
                && snapshot.EstimatedCount == previousCount
                && snapshot.WeightedSize.GetValueOrDefault() == previousWeightedSize;
            previousCount = snapshot.EstimatedCount;
            previousWeightedSize = snapshot.WeightedSize.GetValueOrDefault(-1);
            if (stable)
            {
                return new DrainResult(
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
                    CollectionDelta(collectionsBefore),
                    pass + 1,
                    before.MaintenanceBacklog,
                    maximumBacklog,
                    snapshot.Statistics.MaintenanceBacklog
                );
            }
            if (Stopwatch.GetElapsedTime(started) >= DrainWatchdog)
                break;
            wait.SpinOnce();
        }

        CacheSnapshot last = Snapshot(cache);
        throw new InvalidOperationException(
            $"Drain did not quiesce within {DrainWatchdog}: "
                + $"backlog={last.Statistics.MaintenanceBacklog}, "
                + $"count={last.EstimatedCount}, weighted={last.WeightedSize}."
        );
    }

    private static void SignalAndWait(Barrier barrier)
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Write-probe barrier did not advance within 30 seconds.");
        }
    }

    private static void Validate(
        WorkloadSpec workload,
        TraceData trace,
        long checksum,
        CacheSnapshot final
    )
    {
        if (trace.ExpectedChecksum == 0 || checksum != trace.ExpectedChecksum)
        {
            throw new InvalidOperationException(
                $"Trace checksum mismatch: expected={trace.ExpectedChecksum}, actual={checksum}."
            );
        }

        if (
            final.EstimatedCount > workload.Capacity
            || final.WeightedSize is long weighted && weighted > workload.Capacity
            || final.Statistics.MaintenanceBacklog != 0
            || final.Statistics.WriteBufferBacklog.GetValueOrDefault() > 0
        )
        {
            throw new InvalidOperationException(
                $"Public-bound invariant failure for {workload.Name}: "
                    + $"count={final.EstimatedCount}, weighted={final.WeightedSize}, "
                    + $"backlog={final.Statistics.MaintenanceBacklog}, "
                    + $"writeBacklog={final.Statistics.WriteBufferBacklog}."
            );
        }
    }

    private static CacheSnapshot Snapshot(CacheApi.ICache<int, int> cache)
    {
        return new CacheSnapshot(
            cache.EstimatedCount,
            cache.Policy.Eviction?.WeightedSize,
            StatisticsSnapshot(cache)
        );
    }

    private static CacheStatisticsData StatisticsSnapshot(CacheApi.ICache<int, int> cache)
    {
        CacheApi.CacheStatistics statistics = cache.Statistics;
        return new CacheStatisticsData(
            statistics.Hits,
            statistics.Misses,
            statistics.Evictions,
            statistics.MaintenanceBacklog,
            statistics.DroppedReadEvents,
            statistics.MaintenanceScheduleRejections,
            statistics.MaintenanceFaults,
            ReadOptionalLong(WriteBufferBacklogProperty, statistics),
            ReadOptionalLong(WriteBufferPressureProperty, statistics)
        );
    }

    private static long? ReadOptionalLong(
        PropertyInfo? property,
        CacheApi.CacheStatistics statistics
    )
    {
        object? value = property?.GetValue(statistics);
        return value is long longValue ? longValue : null;
    }

    private static long[] CollectionCounts() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static long[] CollectionDelta(long[] before) =>
        [
            GC.CollectionCount(0) - before[0],
            GC.CollectionCount(1) - before[1],
            GC.CollectionCount(2) - before[2],
        ];

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private readonly record struct WorkloadSpec(
        string Kind,
        int Capacity,
        int Workers,
        int OperationsPerWorker,
        int Batches,
        bool Statistics,
        string KeyMode,
        string ValueMode
    )
    {
        public string Name =>
            $"{Kind}-cap{Capacity}-w{Workers}-ops{OperationsPerWorker}-batches{Batches}"
            + $"-stats{(Statistics ? "on" : "off")}-{KeyMode}"
            + (Kind == "caffeine" ? $"-values-{ValueMode}" : string.Empty);

        public static WorkloadSpec Create(
            string kind,
            int capacity,
            int workers,
            int operationsPerWorker,
            bool statistics,
            string keyMode,
            int targetOperations
        )
        {
            long operationsPerBatch = checked((long)workers * operationsPerWorker);
            int batches = checked(
                (int)
                    Math.Max(
                        kind == "original" ? 8 : 4,
                        (targetOperations + operationsPerBatch - 1) / operationsPerBatch
                    )
            );
            return new(
                kind,
                capacity,
                workers,
                operationsPerWorker,
                batches,
                statistics,
                keyMode,
                "default"
            );
        }

        public static WorkloadSpec CreateCaffeine(
            int capacity,
            int workers,
            int operationsPerWorker,
            bool statistics,
            string valueMode,
            int targetOperations
        )
        {
            long operationsPerCycle = checked(
                (long)workers * operationsPerWorker * CaffeineTraceWindows * 2
            );
            int cycles = checked(
                (int)Math.Max(1, (targetOperations + operationsPerCycle - 1) / operationsPerCycle)
            );
            int batches = checked(cycles * CaffeineTraceWindows * 2);
            return new(
                "caffeine",
                capacity,
                workers,
                operationsPerWorker,
                batches,
                statistics,
                "caffeine",
                valueMode
            );
        }
    }

    private sealed class ProbeOptions
    {
        public required string Suite { get; init; }
        public required string Label { get; init; }
        public required int Seed { get; init; }
        public required int Runs { get; init; }
        public required int Warmups { get; init; }
        public required int TargetOperationsPerSample { get; init; }
        public required int SteadyOperationsPerWorker { get; init; }
        public required int? CaffeineCapacity { get; init; }
        public required int? CaffeineWorkers { get; init; }
        public required int? CaffeineWritesPerWorker { get; init; }
        public required bool? CaffeineStatistics { get; init; }
        public required string? CaffeineValueMode { get; init; }
        public required string? OutputPath { get; init; }
        public required bool NoProgress { get; init; }

        public bool MatchesCaffeine(
            int capacity,
            int workers,
            int writesPerWorker,
            bool statistics,
            string valueMode
        ) =>
            (CaffeineCapacity is null || CaffeineCapacity == capacity)
            && (CaffeineWorkers is null || CaffeineWorkers == workers)
            && (CaffeineWritesPerWorker is null || CaffeineWritesPerWorker == writesPerWorker)
            && (CaffeineStatistics is null || CaffeineStatistics == statistics)
            && (CaffeineValueMode is null || CaffeineValueMode == valueMode);

        public static ProbeOptions Parse(string[] args)
        {
            string suite = ReadString(args, "--suite", "repro");
            if (suite is not ("repro" or "original" or "steady" or "caffeine" or "all"))
            {
                throw new ArgumentException(
                    "--suite must be repro, original, steady, caffeine, or all."
                );
            }

            int? caffeineCapacity = ReadOptionalInt(args, "--capacity", 1, 2_000_000);
            int? caffeineWorkers = ReadOptionalInt(args, "--workers", 1, 100);
            int? caffeineWritesPerWorker = ReadOptionalInt(
                args,
                "--writes-per-worker",
                1,
                2_000_000
            );
            bool? caffeineStatistics = ReadOptionalStatistics(args);
            string? caffeineValueMode = ReadOptionalString(args, "--value-mode");
            if (caffeineValueMode is not (null or "same" or "changed"))
            {
                throw new ArgumentException("--value-mode must be same or changed.");
            }

            if (
                caffeineCapacity is not null
                || caffeineWorkers is not null
                || caffeineWritesPerWorker is not null
                || caffeineStatistics is not null
                || caffeineValueMode is not null
            )
            {
                if (suite != "caffeine")
                {
                    throw new ArgumentException(
                        "--capacity, --workers, --writes-per-worker, --statistics, and "
                            + "--value-mode require --suite caffeine."
                    );
                }
            }

            return new ProbeOptions
            {
                Suite = suite,
                Label = ReadString(args, "--label", "unlabelled"),
                Seed = ReadInt(args, "--seed", 419, 0, int.MaxValue),
                Runs = ReadInt(args, "--runs", 5, 1, 100),
                Warmups = ReadInt(args, "--warmups", 1, 0, 100),
                TargetOperationsPerSample = ReadInt(
                    args,
                    "--target-operations",
                    DefaultTargetOperationsPerSample,
                    1,
                    2_000_000
                ),
                SteadyOperationsPerWorker = ReadInt(
                    args,
                    "--steady-operations",
                    16_384,
                    1,
                    2_000_000
                ),
                CaffeineCapacity = caffeineCapacity,
                CaffeineWorkers = caffeineWorkers,
                CaffeineWritesPerWorker = caffeineWritesPerWorker,
                CaffeineStatistics = caffeineStatistics,
                CaffeineValueMode = caffeineValueMode,
                OutputPath = ReadOptionalString(args, "--output"),
                NoProgress = args.Contains("--no-progress", StringComparer.Ordinal),
            };
        }
    }

    private sealed record ProbeReport(
        int SchemaVersion,
        string Label,
        string Suite,
        int Seed,
        int Runs,
        int Warmups,
        int SteadyOperationsPerWorker,
        int TargetOperationsPerSample,
        string Runtime,
        string Architecture,
        string OS,
        int LogicalProcessors,
        long StopWatchFrequency,
        bool ServerGc,
        RuntimeEnvironmentSnapshot RuntimeEnvironment,
        string CacheAssemblySha256,
        string HarnessAssemblySha256,
        IReadOnlyList<ProbeSample> Samples
    );

    private sealed record ProbeSample(
        bool Warmup,
        int SampleIndex,
        string Name,
        string Kind,
        int Capacity,
        int Workers,
        int OperationsPerWorker,
        int Batches,
        long TotalOperations,
        bool Statistics,
        string KeyMode,
        string ValueMode,
        int PrefilledCount,
        double SetupSeconds,
        double WriteSeconds,
        double WorkerWriteSeconds,
        double DrainSeconds,
        double TimedSeconds,
        double WriteOperationsPerSecond,
        double TimedOperationsPerSecond,
        long WorkerAllocatedBytes,
        long WholeProcessAllocatedBytes,
        long DrainAllocatedBytes,
        long[] TimedGcCollections,
        long[] DrainGcCollections,
        int DrainPasses,
        long BacklogBeforeDrain,
        long MaximumBacklogObserved,
        long BacklogAfterDrain,
        long FinalEstimatedCount,
        long? FinalWeightedSize,
        long LockContentionDelta,
        long Checksum,
        bool PublicBoundsHold,
        CacheStatisticsData FinalStatistics
    );

    private sealed record RuntimeEnvironmentSnapshot(
        string? DotnetTieredCompilation,
        string? DotnetTieredPgo,
        string? ComPlusTieredCompilation,
        string? ComPlusTieredPgo
    )
    {
        public static RuntimeEnvironmentSnapshot Create() =>
            new(
                Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                Environment.GetEnvironmentVariable("COMPlus_TieredCompilation"),
                Environment.GetEnvironmentVariable("COMPlus_TieredPGO")
            );
    }

    private sealed record CacheStatisticsData(
        long Hits,
        long Misses,
        long Evictions,
        long MaintenanceBacklog,
        long DroppedReadEvents,
        long MaintenanceScheduleRejections,
        long MaintenanceFaults,
        long? WriteBufferBacklog,
        long? WriteBufferPressure
    );

    private sealed record TraceData(int[] Keys, int[] Values, long ExpectedChecksum)
    {
        public static TraceData Create(WorkloadSpec workload, int seed)
        {
            int traceBatches =
                workload.Kind == "caffeine" ? CaffeineTraceBatches : workload.Batches;
            int length = checked(workload.Workers * workload.OperationsPerWorker * traceBatches);
            int[] keys = new int[length];
            int[] values = new int[length];
            long checksum = 0;
            for (int batch = 0; batch < traceBatches; batch++)
            {
                for (int worker = 0; worker < workload.Workers; worker++)
                {
                    int offset = checked(
                        (batch * workload.Workers + worker) * workload.OperationsPerWorker
                    );
                    for (int index = 0; index < workload.OperationsPerWorker; index++)
                    {
                        int traceIndex = offset + index;
                        if (workload.KeyMode == "replace")
                        {
                            keys[traceIndex] = index % workload.Capacity;
                            values[traceIndex] = checked(seed + traceIndex + 1);
                        }
                        else if (workload.KeyMode == "caffeine")
                        {
                            int operationsPerWindow = checked(
                                workload.Workers * workload.OperationsPerWorker
                            );
                            int sweep = batch / CaffeineTraceWindows;
                            int window = batch % CaffeineTraceWindows;
                            int windowIndex = checked(
                                (window * workload.Workers + worker) * workload.OperationsPerWorker
                                + index
                            );
                            int sweepLength = checked(operationsPerWindow * CaffeineTraceWindows);
                            keys[traceIndex] = windowIndex;
                            values[traceIndex] =
                                workload.ValueMode == "changed" && (sweep & 1) != 0
                                    ? checked(windowIndex + sweepLength)
                                    : windowIndex;
                        }
                        else
                        {
                            keys[traceIndex] = checked(seed + traceIndex + 1);
                            values[traceIndex] = checked(seed + traceIndex + 1);
                        }
                        checksum = checked(checksum + values[traceIndex]);
                    }
                }
            }

            int cycles = 1;
            if (workload.Kind == "caffeine")
            {
                if (workload.Batches % traceBatches != 0)
                {
                    throw new InvalidOperationException(
                        "Caffeine workload batches must be a whole number of 8-window cycles."
                    );
                }

                cycles = checked(workload.Batches / traceBatches);
            }
            return new TraceData(keys, values, checked(checksum * cycles));
        }
    }

    private sealed record WorkerExecution(double Seconds, long AllocatedBytes, long Checksum);

    private sealed record PersistentExecution(
        double SetupSeconds,
        double WriteSeconds,
        double WorkerWriteSeconds,
        long AllocatedBytes,
        long WorkerAllocatedBytes,
        long[] GcCollections,
        long LockContentionDelta,
        long Checksum,
        DrainExecution Drain
    );

    private sealed record DrainExecution(
        double Seconds,
        long AllocatedBytes,
        long[] GcCollections,
        int Passes,
        long BacklogBefore,
        long MaximumBacklog,
        long BacklogAfter
    );

    private sealed class DrainAggregate
    {
        private double _seconds;
        private long _allocatedBytes;
        private long[] _gcCollections = [0, 0, 0];
        private int _passes;
        private long _backlogBefore;
        private long _maximumBacklog;
        private long _backlogAfter;

        public void Add(DrainResult result)
        {
            _seconds += result.Seconds;
            _allocatedBytes = checked(_allocatedBytes + result.AllocatedBytes);
            for (int generation = 0; generation < _gcCollections.Length; generation++)
            {
                _gcCollections[generation] = checked(
                    _gcCollections[generation] + result.GcCollections[generation]
                );
            }

            _passes = checked(_passes + result.Passes);
            _backlogBefore = Math.Max(_backlogBefore, result.BacklogBefore);
            _maximumBacklog = Math.Max(_maximumBacklog, result.MaximumBacklog);
            _backlogAfter = result.BacklogAfter;
        }

        public DrainExecution ToResult() =>
            new(
                _seconds,
                _allocatedBytes,
                _gcCollections,
                _passes,
                _backlogBefore,
                _maximumBacklog,
                _backlogAfter
            );
    }

    private sealed record DrainResult(
        double Seconds,
        long AllocatedBytes,
        long[] GcCollections,
        int Passes,
        long BacklogBefore,
        long MaximumBacklog,
        long BacklogAfter
    );

    private sealed record CacheSnapshot(
        long EstimatedCount,
        long? WeightedSize,
        CacheStatisticsData Statistics
    );

    private static string ReadString(string[] args, string option, string fallback) =>
        ReadOptionalString(args, option) ?? fallback;

    private static string? ReadOptionalString(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value after {option}.");
        }

        return args[index + 1];
    }

    private static int ReadInt(string[] args, string option, int fallback, int minimum, int maximum)
    {
        string raw = ReadString(args, option, fallback.ToString(CultureInfo.InvariantCulture));
        return ParseInt(option, raw, minimum, maximum);
    }

    private static int? ReadOptionalInt(string[] args, string option, int minimum, int maximum)
    {
        string? raw = ReadOptionalString(args, option);
        return raw is null ? null : ParseInt(option, raw, minimum, maximum);
    }

    private static int ParseInt(string option, string raw, int minimum, int maximum)
    {
        if (
            !int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
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

    private static bool? ReadOptionalStatistics(string[] args)
    {
        string? raw = ReadOptionalString(args, "--statistics");
        return raw switch
        {
            null => null,
            "on" or "true" => true,
            "off" or "false" => false,
            _ => throw new ArgumentException("--statistics must be on or off."),
        };
    }
}
