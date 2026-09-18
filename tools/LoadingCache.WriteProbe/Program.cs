using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        typeof(CacheStatistics).GetProperty("WriteBufferBacklog");
    private static readonly PropertyInfo? WriteBufferPressureProperty =
        typeof(CacheStatistics).GetProperty("WriteBufferPressure");

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test-cleanup"])
        {
            await PersistentWorkers.CheckRetirementAsync().ConfigureAwait(false);
            Console.WriteLine("Write-probe cleanup controls passed.");
            return 0;
        }

        ProbeOptions options = ProbeOptions.Parse(args);
        List<WorkloadSpec> workloads = [.. CreateWorkloads(options)];
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
                    await Console.Error.WriteLineAsync(
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
            Os: RuntimeInformation.OSDescription,
            LogicalProcessors: Environment.ProcessorCount,
            StopWatchFrequency: Stopwatch.Frequency,
            ServerGc: System.Runtime.GCSettings.IsServerGC,
            RuntimeEnvironment: RuntimeEnvironmentSnapshot.Create(),
            CacheAssemblySha256: Sha256(typeof(CacheBuilder).Assembly.Location),
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

            await File.WriteAllTextAsync(options.OutputPath, json + Environment.NewLine);
            await Console.Error.WriteLineAsync($"wrote {options.OutputPath}");
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

        if (options.Suite is not ("caffeine" or "all"))
            yield break;
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
        ICache<int, int> cache = BuildCache(workload);
        bool disposeCache = true;
        try
        {
            switch (workload.KeyMode)
            {
                case "replace":
                    Prefill(cache, workload.Capacity, options.Seed);
                    break;
                case "caffeine":
                    cache.Clear();
                    cache.CleanUp();
                    break;
            }

            PersistentExecution execution = await ExecutePersistentAsync(cache, workload, trace)
                .ConfigureAwait(false);
            CacheSnapshot final = Snapshot(cache);
            Validate(workload, trace, execution.Checksum, final);

            long timedOperations = checked(
                (long)workload.Batches * workload.Workers * workload.OperationsPerWorker
            );
            double timedSeconds = execution.WriteSeconds + execution.DrainSummary.Seconds;
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
                DrainSeconds: execution.DrainSummary.Seconds,
                TimedSeconds: timedSeconds,
                WriteOperationsPerSecond: timedOperations / execution.WriteSeconds,
                TimedOperationsPerSecond: timedOperations / timedSeconds,
                WorkerAllocatedBytes: execution.WorkerAllocatedBytes,
                WholeProcessAllocatedBytes: execution.AllocatedBytes,
                DrainAllocatedBytes: execution.DrainSummary.AllocatedBytes,
                TimedGcCollections: execution.GcCollections,
                DrainGcCollections: execution.DrainSummary.GcCollections,
                DrainPasses: execution.DrainSummary.Passes,
                BacklogBeforeDrain: execution.DrainSummary.BacklogBefore,
                MaximumBacklogObserved: execution.DrainSummary.MaximumBacklog,
                BacklogAfterDrain: execution.DrainSummary.BacklogAfter,
                FinalEstimatedCount: final.EstimatedCount,
                FinalWeightedSize: final.WeightedSize,
                LockContentionDelta: execution.LockContentionDelta,
                Checksum: execution.Checksum,
                PublicBoundsHold: true,
                FinalStatistics: final.Statistics
            );
        }
        catch (WorkerCleanupException)
        {
            disposeCache = false;
            throw;
        }
        finally
        {
            if (disposeCache)
                cache.Dispose();
        }
    }

    private static ICache<int, int> BuildCache(WorkloadSpec workload)
    {
        CacheBuilder<int, int> builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(workload.Capacity)
            .MaxConcurrentLoads(1);
        if (workload.Statistics)
        {
            builder.RecordStatistics();
        }

        return builder.Build();
    }

    private static void Prefill(ICache<int, int> cache, int capacity, int seed)
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

    private static Task<PersistentExecution> ExecutePersistentAsync(
        ICache<int, int> cache,
        WorkloadSpec workload,
        TraceData trace
    ) => new PersistentWorkers(cache, workload, trace).RunAsync();

    private sealed class PersistentWorkers(
        ICache<int, int> cache,
        WorkloadSpec workload,
        TraceData trace,
        CleanupControl? control = null
    ) : IDisposable
    {
        private readonly Barrier _barrier = new(workload.Workers + 1);

        private int _activeWorkers;
        private int _startedWorkers;
        private int _abort;
        private int _disposeRequested;
        private int _resourcesDisposed;
        private bool _retireCache;

        public void Dispose()
        {
            Volatile.Write(ref _disposeRequested, 1);
            TryDisposeResources();
        }

        private void TryDisposeResources()
        {
            if (
                Volatile.Read(ref _disposeRequested) == 0
                || Volatile.Read(ref _activeWorkers) != 0
                || Interlocked.Exchange(ref _resourcesDisposed, 1) != 0
            )
                return;
            _barrier.Dispose();
            if (_retireCache)
                cache.Dispose();
        }

        private void WorkerFinished()
        {
            Interlocked.Decrement(ref _activeWorkers);
            TryDisposeResources();
        }

        private void StartWorker(Thread worker)
        {
            control?.BeforeStart(_startedWorkers);
            Interlocked.Increment(ref _activeWorkers);
            try
            {
                worker.Start();
                _startedWorkers++;
                control?.Started.Add(worker);
            }
            catch
            {
                WorkerFinished();
                throw;
            }
        }

        internal static async Task CheckRetirementAsync()
        {
            foreach (bool failDuringStart in new[] { true, false })
            {
                ICache<int, int> cache = CacheBuilder
                    .Create<int, int>()
                    .MaximumSize(16)
                    .MaxConcurrentLoads(1)
                    .Build();
                WorkloadSpec workload = new("original", 16, 2, 1, 8, false, "unique", "default");
                using CleanupControl control = new(
                    failDuringStart,
                    failDuringStart ? 1 : null,
                    !failDuringStart
                );
                using var owner = new PersistentWorkers(
                    cache,
                    workload,
                    TraceData.Create(workload, 1),
                    control
                );
                Exception? observed = null;
                try
                {
                    try
                    {
                        await owner.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        observed = exception;
                    }
                    if (failDuringStart)
                    {
                        if (
                            observed is not WorkerCleanupException combined
                            || !ReferenceEquals(combined.InnerExceptions[0], control.Failure)
                        )
                            throw new InvalidOperationException(
                                "Actual startup failure was not preserved with cleanup timeout."
                            );
                        if (
                            Volatile.Read(ref owner._resourcesDisposed) != 0
                            || owner._barrier.ParticipantCount != 1
                        )
                            throw new InvalidOperationException(
                                "Startup cleanup left phantom participants or disposed live resources."
                            );
                        cache.Put(1, 1);
                    }
                    else
                    {
                        if (!ReferenceEquals(observed, control.Failure))
                            throw new InvalidOperationException(
                                "Actual drain failure was not preserved."
                            );
                        if (
                            control.BatchStarts != workload.Workers
                            || Volatile.Read(ref owner._activeWorkers) != 0
                        )
                            throw new InvalidOperationException(
                                "Workers continued into another batch after drain failure."
                            );
                    }
                }
                finally
                {
                    control.Release();
                    JoinWorkers(control.Started, TimeSpan.FromSeconds(30));
                }
                if (Volatile.Read(ref owner._resourcesDisposed) != 1)
                    throw new InvalidOperationException("Last worker did not retire resources.");
                if (failDuringStart)
                {
                    bool disposed = false;
                    try
                    {
                        cache.Put(1, 1);
                    }
                    catch (ObjectDisposedException)
                    {
                        disposed = true;
                    }
                    if (!disposed)
                        throw new InvalidOperationException("Retired cache was not disposed.");
                }
                else
                {
                    cache.Dispose();
                }
            }
            using CleanupControl faultControl = new(false, null, false);
            ICache<int, int> faultCache = CacheBuilder
                .Create<int, int>()
                .MaximumWeight(16)
                .MaximumResidentCount(16)
                .MaxConcurrentLoads(16)
                .Weigher(faultControl.FailPut)
                .Build();
            try
            {
                WorkloadSpec workload = new("original", 16, 2, 1, 8, false, "unique", "default");
                using var owner = new PersistentWorkers(
                    faultCache,
                    workload,
                    TraceData.Create(workload, 1),
                    faultControl
                );
                Exception? observed = null;
                try
                {
                    await owner.RunAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
                if (
                    observed is not InvalidOperationException
                    || !ReferenceEquals(observed.InnerException, faultControl.Failure)
                )
                    throw new InvalidOperationException(
                        "Actual Put failure was not preserved.",
                        observed
                    );
                if (
                    faultControl.BatchStarts is < 1 or > 2
                    || Volatile.Read(ref owner._activeWorkers) != 0
                    || Volatile.Read(ref owner._resourcesDisposed) != 1
                )
                    throw new InvalidOperationException(
                        "Put failure left live resources or started a later batch."
                    );
            }
            finally
            {
                faultCache.Dispose();
            }
        }

        internal async Task<PersistentExecution> RunAsync()
        {
            TaskCompletionSource<bool> ready = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TaskCompletionSource<bool> completed = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TaskCompletionSource<Exception> failure = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            WorkerExecution[] workerResults = new WorkerExecution[
                checked(workload.Workers * workload.Batches)
            ];
            Thread?[] threads = new Thread?[workload.Workers];
            int readyCount = 0;

            Exception? primaryFailure = null;
            PersistentExecution? executionResult = null;
            int alive;
            long setupStarted = Stopwatch.GetTimestamp();
            try
            {
                for (int worker = 0; worker < workload.Workers; worker++)
                {
                    int workerIndex = worker;
                    Thread thread = new(() =>
                    {
                        try
                        {
                            control?.WaitForRelease();
                            if (Interlocked.Increment(ref readyCount) == workload.Workers)
                            {
                                ready.TrySetResult(true);
                            }

                            for (int batch = 0; batch < workload.Batches; batch++)
                            {
                                if (Volatile.Read(ref _abort) != 0)
                                    break;
                                SignalAndWait(_barrier);
                                if (Volatile.Read(ref _abort) != 0)
                                    break;
                                control?.OnBatchStarting();
                                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                                long started = Stopwatch.GetTimestamp();
                                long checksum = 0;
                                int traceBatch =
                                    workload.Kind == "caffeine"
                                        ? batch % CaffeineTraceBatches
                                        : batch;
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

                                workerResults[batch * workload.Workers + workerIndex] =
                                    new WorkerExecution(
                                        Stopwatch.GetElapsedTime(started).TotalSeconds,
                                        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                                        checksum
                                    );
                                SignalAndWait(_barrier);
                            }

                            completed.TrySetResult(true);
                        }
                        catch (Exception exception)
                        {
                            failure.TrySetResult(exception);
                            Volatile.Write(ref _abort, 1);
                            ready.TrySetException(exception);
                            completed.TrySetResult(true);
                        }
                        finally
                        {
                            try
                            {
                                _barrier.RemoveParticipant();
                            }
                            finally
                            {
                                WorkerFinished();
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = $"LoadingCache.WriteProbe.worker{workerIndex}",
                    };
                    threads[worker] = thread;
                    StartWorker(thread);
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
                    SignalAndWait(_barrier);
                    SignalAndWait(_barrier);
                    writeSeconds += Stopwatch.GetElapsedTime(batchStarted).TotalSeconds;
                    if (failure.Task.IsCompletedSuccessfully)
                        throw new InvalidOperationException(
                            "A persistent worker failed.",
                            failure.Task.Result
                        );

                    double batchWorkerSeconds = 0;
                    for (int worker = 0; worker < workload.Workers; worker++)
                    {
                        WorkerExecution result = workerResults[batch * workload.Workers + worker];
                        batchWorkerSeconds = Math.Max(batchWorkerSeconds, result.Seconds);
                        workerAllocated = checked(workerAllocated + result.AllocatedBytes);
                        checksumTotal = checked(checksumTotal + result.Checksum);
                    }

                    workerWriteSeconds += batchWorkerSeconds;
                    control?.BeforeDrain();
                    drain.Add(Drain(cache, workload.Capacity));
                }

                await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Exception? workerFailure = failure.Task.IsCompletedSuccessfully
                    ? failure.Task.Result
                    : null;
                if (workerFailure is not null)
                {
                    throw new InvalidOperationException(
                        "A persistent worker failed.",
                        workerFailure
                    );
                }

                executionResult = new PersistentExecution(
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
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                try
                {
                    if (primaryFailure is not null)
                    {
                        // Release the main participant without disposing a barrier used by workers.
                        Volatile.Write(ref _abort, 1);
                        _barrier.RemoveParticipants(workload.Workers - _startedWorkers + 1);
                    }
                    alive = JoinWorkers(
                        threads,
                        control?.CleanupTimeout ?? TimeSpan.FromSeconds(5)
                    );
                    if (alive != 0)
                    {
                        _retireCache = true;
                    }
                }
                finally
                {
                    Dispose();
                }
            }
            if (alive != 0)
                throw new WorkerCleanupException(primaryFailure, alive);
            if (primaryFailure is not null)
                System
                    .Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure)
                    .Throw();
            return executionResult
                ?? throw new InvalidOperationException("Probe execution did not produce a result.");
        }
    }

    private sealed class CleanupControl(
        bool blockWorkers,
        // Intentionally configures deferred injected failure, not a constructor precondition.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        int? failBeforeStart,
        // Intentionally configures deferred injected failure, not a constructor precondition.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        bool failBeforeDrain
    ) : IDisposable
    {
        internal long FailPut(int _, int __) => throw Failure;

        private readonly ManualResetEventSlim _release = new();
        internal Exception Failure { get; } =
            new InvalidOperationException("Injected setup or drain failure.");
        internal List<Thread> Started { get; } = [];
        internal TimeSpan CleanupTimeout => blockWorkers ? TimeSpan.Zero : TimeSpan.FromSeconds(30);
        private int _batchStarts;
        internal int BatchStarts => Volatile.Read(ref _batchStarts);

        internal void OnBatchStarting() => Interlocked.Increment(ref _batchStarts);

        internal void BeforeDrain()
        {
            if (failBeforeDrain)
                throw Failure;
        }

        internal void BeforeStart(int started)
        {
            if (started == failBeforeStart)
                throw Failure;
        }

        internal void WaitForRelease()
        {
            if (blockWorkers)
                _release.Wait();
        }

        internal void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private static int JoinWorkers(IEnumerable<Thread?> workers, TimeSpan timeout)
    {
        var cleanup = Stopwatch.StartNew();
        return workers.Count(worker =>
        {
            if (worker is null || !worker.IsAlive)
                return false;
            TimeSpan remaining = timeout - cleanup.Elapsed;
            return !worker.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        });
    }

    private sealed class WorkerCleanupException(Exception? primaryFailure, int alive)
        : AggregateException(
            "Write probe cleanup timed out; live workers retain their barrier and cache until exit.",
            primaryFailure is null
                ? [new TimeoutException($"{alive} write-probe workers did not terminate.")]
                :
                [
                    primaryFailure,
                    new TimeoutException($"{alive} write-probe workers did not terminate."),
                ]
        );

    private static DrainResult Drain(ICache<int, int> cache, int capacity)
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
            || final.WeightedSize is { } weighted && weighted > workload.Capacity
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

    private static CacheSnapshot Snapshot(ICache<int, int> cache)
    {
        return new CacheSnapshot(
            cache.EstimatedCount,
            cache.Policy.Eviction?.WeightedSize,
            StatisticsSnapshot(cache)
        );
    }

    private static CacheStatisticsData StatisticsSnapshot(ICache<int, int> cache)
    {
        CacheStatistics statistics = cache.Statistics;
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

    private static long? ReadOptionalLong(PropertyInfo? property, CacheStatistics statistics)
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
        [property: JsonInclude] string Kind,
        [property: JsonInclude] int Capacity,
        [property: JsonInclude] int Workers,
        [property: JsonInclude] int OperationsPerWorker,
        [property: JsonInclude] int Batches,
        [property: JsonInclude] bool Statistics,
        [property: JsonInclude] string KeyMode,
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
            return new WorkloadSpec(
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
            return new WorkloadSpec(
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
                suite != "caffeine"
                && (
                    caffeineCapacity is not null
                    || caffeineWorkers is not null
                    || caffeineWritesPerWorker is not null
                    || caffeineStatistics is not null
                    || caffeineValueMode is not null
                )
            )
            {
                throw new ArgumentException(
                    "--capacity, --workers, --writes-per-worker, --statistics, and "
                        + "--value-mode require --suite caffeine."
                );
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
        [property: JsonInclude] int SchemaVersion,
        [property: JsonInclude] string Label,
        [property: JsonInclude] string Suite,
        [property: JsonInclude] int Seed,
        [property: JsonInclude] int Runs,
        [property: JsonInclude] int Warmups,
        [property: JsonInclude] int SteadyOperationsPerWorker,
        [property: JsonInclude] int TargetOperationsPerSample,
        [property: JsonInclude] string Runtime,
        [property: JsonInclude] string Architecture,
        [property: JsonInclude] string Os,
        [property: JsonInclude] int LogicalProcessors,
        [property: JsonInclude] long StopWatchFrequency,
        [property: JsonInclude] bool ServerGc,
        [property: JsonInclude] RuntimeEnvironmentSnapshot RuntimeEnvironment,
        [property: JsonInclude] string CacheAssemblySha256,
        [property: JsonInclude] string HarnessAssemblySha256,
        [property: JsonInclude] IReadOnlyList<ProbeSample> Samples
    );

    private sealed record ProbeSample(
        [property: JsonInclude] bool Warmup,
        [property: JsonInclude] int SampleIndex,
        [property: JsonInclude] string Name,
        [property: JsonInclude] string Kind,
        [property: JsonInclude] int Capacity,
        [property: JsonInclude] int Workers,
        [property: JsonInclude] int OperationsPerWorker,
        [property: JsonInclude] int Batches,
        [property: JsonInclude] long TotalOperations,
        [property: JsonInclude] bool Statistics,
        [property: JsonInclude] string KeyMode,
        [property: JsonInclude] string ValueMode,
        [property: JsonInclude] int PrefilledCount,
        [property: JsonInclude] double SetupSeconds,
        [property: JsonInclude] double WriteSeconds,
        [property: JsonInclude] double WorkerWriteSeconds,
        [property: JsonInclude] double DrainSeconds,
        double TimedSeconds,
        [property: JsonInclude] double WriteOperationsPerSecond,
        double TimedOperationsPerSecond,
        [property: JsonInclude] long WorkerAllocatedBytes,
        [property: JsonInclude] long WholeProcessAllocatedBytes,
        [property: JsonInclude] long DrainAllocatedBytes,
        [property: JsonInclude] long[] TimedGcCollections,
        [property: JsonInclude] long[] DrainGcCollections,
        [property: JsonInclude] int DrainPasses,
        [property: JsonInclude] long BacklogBeforeDrain,
        [property: JsonInclude] long MaximumBacklogObserved,
        [property: JsonInclude] long BacklogAfterDrain,
        [property: JsonInclude] long FinalEstimatedCount,
        [property: JsonInclude] long? FinalWeightedSize,
        [property: JsonInclude] long LockContentionDelta,
        [property: JsonInclude] long Checksum,
        [property: JsonInclude] bool PublicBoundsHold,
        [property: JsonInclude] CacheStatisticsData FinalStatistics
    );

    private sealed record RuntimeEnvironmentSnapshot(
        [property: JsonInclude] string? DotnetTieredCompilation,
        [property: JsonInclude] string? DotnetTieredPgo,
        [property: JsonInclude] string? ComPlusTieredCompilation,
        [property: JsonInclude] string? ComPlusTieredPgo
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
        [property: JsonInclude] long Hits,
        [property: JsonInclude] long Misses,
        [property: JsonInclude] long Evictions,
        long MaintenanceBacklog,
        [property: JsonInclude] long DroppedReadEvents,
        [property: JsonInclude] long MaintenanceScheduleRejections,
        [property: JsonInclude] long MaintenanceFaults,
        long? WriteBufferBacklog,
        [property: JsonInclude] long? WriteBufferPressure
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
                        switch (workload.KeyMode)
                        {
                            case "replace":
                                keys[traceIndex] = index % workload.Capacity;
                                values[traceIndex] = checked(seed + traceIndex + 1);
                                break;
                            case "caffeine":
                                int operationsPerWindow = checked(
                                    workload.Workers * workload.OperationsPerWorker
                                );
                                int sweep = batch / CaffeineTraceWindows;
                                int window = batch % CaffeineTraceWindows;
                                int windowIndex = checked(
                                    (window * workload.Workers + worker)
                                        * workload.OperationsPerWorker
                                    + index
                                );
                                int sweepLength = checked(
                                    operationsPerWindow * CaffeineTraceWindows
                                );
                                keys[traceIndex] = windowIndex;
                                values[traceIndex] =
                                    workload.ValueMode == "changed" && (sweep & 1) != 0
                                        ? checked(windowIndex + sweepLength)
                                        : windowIndex;
                                break;
                            default:
                                keys[traceIndex] = checked(seed + traceIndex + 1);
                                values[traceIndex] = checked(seed + traceIndex + 1);
                                break;
                        }
                        checksum = checked(checksum + values[traceIndex]);
                    }
                }
            }

            if (workload.Kind == "caffeine" && workload.Batches % traceBatches != 0)
                throw new InvalidOperationException(
                    "Caffeine workload batches must be a whole number of 8-window cycles."
                );
            int cycles = workload.Kind == "caffeine" ? checked(workload.Batches / traceBatches) : 1;
            return new TraceData(keys, values, checked(checksum * cycles));
        }
    }

    private sealed record WorkerExecution(double Seconds, long AllocatedBytes, long Checksum);

    private sealed record PersistentExecution(
        [property: JsonInclude] double SetupSeconds,
        [property: JsonInclude] double WriteSeconds,
        [property: JsonInclude] double WorkerWriteSeconds,
        long AllocatedBytes,
        [property: JsonInclude] long WorkerAllocatedBytes,
        long[] GcCollections,
        [property: JsonInclude] long LockContentionDelta,
        [property: JsonInclude] long Checksum,
        DrainExecution DrainSummary
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
        private readonly long[] _gcCollections = [0, 0, 0];
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
