using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using LoadingCache.Maintenance;

namespace LoadingCache.ReadProbe;

internal static class Program
{
    private const int DefaultOperationsPerProducer = 16_384;
    private const int DefaultTargetOperations = 1_048_576;
    private const int DefaultRuns = 5;
    private const int DefaultWarmups = 1;
    private const int SingleChunkSize = 16;
    private const int BatchDrainBudget = 1_024;
    private const int MaxJoinSeconds = 30;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int Main(string[] args)
    {
        if (Array.IndexOf(args, "--cache-path") >= 0)
        {
            return CachePathDiagnostic.Run(args);
        }

        if (args is ["--self-test-cleanup"])
        {
            ConcurrentExecution.CheckRetirement();
            Console.WriteLine("Read-probe cleanup controls passed.");
            return 0;
        }

        ProbeOptions options = ProbeOptions.Parse(args);
        List<ProbeSample> samples = [];

        foreach (Scenario scenario in CreateScenarios(options))
        {
            ReferenceEvents events = ReferenceEvents.Create(scenario, options.Seed);
            int sampleCount = options.Warmups + options.Runs;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                bool warmup = sampleIndex < options.Warmups;
                ProbeSample sample = RunSample(options, scenario, events, sampleIndex, warmup);
                samples.Add(sample);

                if (!options.NoProgress)
                {
                    Console.Error.WriteLine(
                        $"{scenario.Name} sample={sampleIndex + 1}/{sampleCount} "
                            + $"elapsed={sample.ElapsedSeconds:F6}s "
                            + $"accepted={sample.Accepted}/{sample.Attempts} "
                            + $"drained={sample.Drained}"
                    );
                }
            }
        }

        ProbeReport report = new(
            SchemaVersion: 1,
            Label: options.Label,
            Transport: options.Transport,
            Mode: options.Mode,
            DrainMode: options.DrainMode,
            Seed: options.Seed,
            StripeCount: options.StripeCount,
            StripeCapacity: options.StripeCapacity,
            OperationsPerProducer: options.OperationsPerProducer,
            TargetOperations: options.TargetOperations,
            ProducerCounts: options.ProducerCounts,
            Runs: options.Runs,
            Warmups: options.Warmups,
            Runtime: RuntimeInformation.FrameworkDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            Os: RuntimeInformation.OSDescription,
            LogicalProcessors: Environment.ProcessorCount,
            ProcessorIdentifier: Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ServerGc: GCSettings.IsServerGC,
            GcLatencyMode: GCSettings.LatencyMode.ToString(),
            RuntimeEnvironment: RuntimeEnvironmentSnapshot.Create(),
            CacheAssemblySha256: Sha256(typeof(StripedReadBuffer<ReadEvent>).Assembly.Location),
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

    private static IEnumerable<Scenario> CreateScenarios(ProbeOptions options)
    {
        if (options.Mode is "single" or "all")
        {
            yield return CreateScenario("single", 1, options);
        }

        if (options.Mode is not ("concurrent" or "all"))
            yield break;
        foreach (int producerCount in options.ProducerCounts)
        {
            yield return CreateScenario("concurrent", producerCount, options);
        }
    }

    private static Scenario CreateScenario(string mode, int producers, ProbeOptions options)
    {
        long operationsPerRound = checked((long)producers * options.OperationsPerProducer);
        long rounds = Math.Max(
            1,
            (options.TargetOperations + operationsPerRound - 1) / operationsPerRound
        );
        return new Scenario(mode, producers, options.OperationsPerProducer, checked((int)rounds));
    }

    private static ProbeSample RunSample(
        ProbeOptions options,
        Scenario scenario,
        ReferenceEvents events,
        int sampleIndex,
        bool warmup
    )
    {
        IReadTransport transport = new StripedTransport(
            options.StripeCount,
            options.StripeCapacity
        );
        Execution execution;
        bool disposeTransport = true;
        try
        {
            if (options.DrainMode == "batch" && !transport.SupportsBatchDrain)
            {
                throw new InvalidOperationException(
                    "--drain-mode batch requires a StripedReadBuffer implementation exposing "
                        + "the optional DrainTo(Action<TEvent>, int) method."
                );
            }

            execution = Execute(transport, options.DrainMode, scenario, events);
            TransportStatistics beforeDispose = transport.GetStatistics();
            if (beforeDispose.Queued != 0)
            {
                throw new InvalidOperationException(
                    $"{scenario.Name} left {beforeDispose.Queued} queued events before shutdown."
                );
            }
        }
        catch (WorkerCleanupException)
        {
            // The still-running workers now own final transport disposal.
            disposeTransport = false;
            throw;
        }
        finally
        {
            if (disposeTransport)
                transport.Dispose();
        }
        bool acceptedAfterDispose = transport.TryEnqueue(events.Values[0][0]);
        TransportStatistics finalStatistics = transport.GetStatistics();
        long expectedAttempts = scenario.Attempts;
        long policyDropped = checked(finalStatistics.DroppedFull + finalStatistics.DroppedFailed);
        long accountedAttempts = checked(execution.Accepted + policyDropped);
        long unaccountedRejected = expectedAttempts - accountedAttempts;
        bool bounded =
            !acceptedAfterDispose
            && finalStatistics.Queued == 0
            && finalStatistics.Enqueued == execution.Accepted
            && finalStatistics.Dequeued == execution.Drained
            && policyDropped <= expectedAttempts - execution.Accepted
            && unaccountedRejected >= 0
            && (!finalStatistics.HasDroppedFailed || unaccountedRejected == 0)
            && finalStatistics.DroppedShutdown >= 1
            && execution.Drained == execution.Accepted
            && execution.AcceptedIdentitySum == execution.DrainedIdentitySum
            && execution.AcceptedIdentityXor == execution.DrainedIdentityXor;

        if (!bounded)
        {
            throw new InvalidOperationException(
                $"{scenario.Name} final transport invariant failed: "
                    + $"accepted={execution.Accepted}, drained={execution.Drained}, "
                    + $"queued={finalStatistics.Queued}, enqueued={finalStatistics.Enqueued}, "
                    + $"dequeued={finalStatistics.Dequeued}, "
                    + $"droppedFull={finalStatistics.DroppedFull}, "
                    + $"droppedFailed={finalStatistics.DroppedFailed}, "
                    + $"droppedShutdown={finalStatistics.DroppedShutdown}, "
                    + $"unaccountedRejected={unaccountedRejected}."
            );
        }

        return new ProbeSample(
            Warmup: warmup,
            SampleIndex: sampleIndex,
            Name: scenario.Name,
            Mode: scenario.Mode,
            DrainMode: options.DrainMode,
            Producers: scenario.Producers,
            Rounds: scenario.Rounds,
            StripeCount: options.StripeCount,
            StripeCapacity: options.StripeCapacity,
            OperationsPerProducer: scenario.OperationsPerProducer,
            Attempts: expectedAttempts,
            Accepted: execution.Accepted,
            Drained: execution.Drained,
            Dropped: checked(policyDropped + finalStatistics.DroppedShutdown),
            DroppedPolicy: policyDropped,
            DroppedFull: finalStatistics.DroppedFull,
            DroppedFailed: finalStatistics.DroppedFailed,
            DroppedShutdown: finalStatistics.DroppedShutdown,
            UnaccountedRejected: unaccountedRejected,
            ShutdownAttempts: 1,
            ShutdownRejected: acceptedAfterDispose ? 0 : 1,
            AcceptanceFraction: execution.Accepted / (double)expectedAttempts,
            ElapsedSeconds: execution.ElapsedSeconds,
            ProducerWallSeconds: execution.ProducerWallSeconds,
            ConsumerWallSeconds: execution.ConsumerWallSeconds,
            CleanupDrainSeconds: execution.CleanupDrainSeconds,
            WriterAllocatedBytes: execution.WriterAllocatedBytes,
            ProcessAllocatedBytes: execution.ProcessAllocatedBytes,
            TimedGcCollections: execution.GcCollections,
            FinalQueued: finalStatistics.Queued,
            FinalEnqueued: finalStatistics.Enqueued,
            FinalDequeued: finalStatistics.Dequeued,
            FinalDisposed: finalStatistics.IsDisposed,
            AcceptedIdentitySum: execution.AcceptedIdentitySum,
            DrainedIdentitySum: execution.DrainedIdentitySum,
            AcceptedIdentityXor: execution.AcceptedIdentityXor,
            DrainedIdentityXor: execution.DrainedIdentityXor,
            IdentityMatch: execution.AcceptedIdentitySum == execution.DrainedIdentitySum
                && execution.AcceptedIdentityXor == execution.DrainedIdentityXor,
            BoundedFinalCountsHold: bounded
        );
    }

    private static Execution ExecuteSingle(
        IReadTransport transport,
        string drainMode,
        Scenario scenario,
        ReferenceEvents events
    )
    {
        ConsumerState consumerState = new();
        Action<ReadEvent> consume = consumerState.Consume;
        long processAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        long[] collectionCountsBefore = CollectionCounts();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        long accepted = 0;
        long acceptedIdentitySum = 0;
        long acceptedIdentityXor = 0;
        ReadEvent[] values = events.Values[0];

        for (int round = 0; round < scenario.Rounds; round++)
        {
            for (int offset = 0; offset < values.Length; offset += SingleChunkSize)
            {
                int end = Math.Min(offset + SingleChunkSize, values.Length);
                for (int index = offset; index < end; index++)
                {
                    ReadEvent value = values[index];
                    if (!transport.TryEnqueue(value))
                        continue;
                    accepted++;
                    acceptedIdentitySum = checked(acceptedIdentitySum + value.Identity);
                    acceptedIdentityXor ^= value.Identity;
                }

                _ = DrainAvailable(transport, drainMode, consume);
            }
        }

        long finished = Stopwatch.GetTimestamp();
        if (consumerState.Drained != accepted)
        {
            throw new InvalidOperationException(
                $"single inline drain mismatch: accepted={accepted}, drained={consumerState.Drained}."
            );
        }

        return new Execution(
            accepted,
            consumerState.Drained,
            acceptedIdentitySum,
            consumerState.IdentitySum,
            acceptedIdentityXor,
            consumerState.IdentityXor,
            Stopwatch.GetElapsedTime(started, finished).TotalSeconds,
            Stopwatch.GetElapsedTime(started, finished).TotalSeconds,
            Stopwatch.GetElapsedTime(started, finished).TotalSeconds,
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            GC.GetTotalAllocatedBytes(precise: false) - processAllocatedBefore,
            CollectionDelta(collectionCountsBefore)
        );
    }

    private static Execution Execute(
        IReadTransport transport,
        string drainMode,
        Scenario scenario,
        ReferenceEvents events,
        CleanupControl? control = null
    )
    {
        if (scenario.Mode == "single")
            return ExecuteSingle(transport, drainMode, scenario, events);
        using var execution = new ConcurrentExecution(
            transport,
            drainMode,
            scenario,
            events,
            control
        );
        return execution.Run();
    }

    private sealed class ConcurrentExecution(
        IReadTransport transport,
        string drainMode,
        Scenario scenario,
        ReferenceEvents events,
        CleanupControl? control = null
    ) : IDisposable
    {
        private readonly ManualResetEventSlim _allReady = new(false);
        private readonly ManualResetEventSlim _launch = new(false);
        private readonly ManualResetEventSlim _producersDone = new(false);
        private readonly ManualResetEventSlim _consumerDone = new(false);
        private int _readyCount;
        private int _remainingProducers = scenario.Producers;
        private int _failed;
        private long _producersCompletedTimestamp;
        private Exception? _failure;
        private readonly object _failureGate = new();
        private ConsumerExecution _consumerResult;
        private int _activeWorkers;
        private int _startedWorkers;
        private int _disposeRequested;
        private int _resourcesDisposed;
        private bool _retireTransport;

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
            _allReady.Dispose();
            _launch.Dispose();
            _producersDone.Dispose();
            _consumerDone.Dispose();
            if (_retireTransport)
                transport.Dispose();
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

        internal static void CheckRetirement()
        {
            foreach (bool failDuringStart in new[] { true, false })
            {
                var transport = new StripedTransport(1, 16);
                Scenario scenario = new("concurrent", 1, 1, 1);
                using CleanupControl control = new(true, failDuringStart ? 1 : null);
                WorkerCleanupException? observed = null;
                try
                {
                    try
                    {
                        Execute(
                            transport,
                            "single",
                            scenario,
                            ReferenceEvents.Create(scenario, 1),
                            control
                        );
                    }
                    catch (WorkerCleanupException exception)
                    {
                        observed = exception;
                    }
                    if (observed is null || transport.GetStatistics().IsDisposed)
                        throw new InvalidOperationException(
                            "Actual failure path did not retire live resources."
                        );
                    bool preserved = failDuringStart
                        ? ReferenceEquals(observed.InnerExceptions[0], control.Failure)
                        : observed.InnerExceptions[0] is TimeoutException;
                    if (!preserved)
                        throw new InvalidOperationException(
                            "Startup failure or ready timeout was not preserved."
                        );
                }
                finally
                {
                    control.Release();
                    JoinWorkers(control.Started, TimeSpan.FromSeconds(30));
                }
                if (!transport.GetStatistics().IsDisposed)
                    throw new InvalidOperationException(
                        "Last worker did not retire the transport."
                    );
            }
        }

        private void MarkReady()
        {
            if (Interlocked.Increment(ref _readyCount) == scenario.Producers + 1)
            {
                _allReady.Set();
            }
        }

        private void RecordFailure(Exception exception)
        {
            Interlocked.Exchange(ref _failed, 1);
            lock (_failureGate)
            {
                _failure ??= exception;
            }

            _allReady.Set();
            _launch.Set();
            _producersDone.Set();
            _consumerDone.Set();
        }

        internal Execution Run()
        {
            ProducerExecution[] producerResults = new ProducerExecution[scenario.Producers];
            Thread?[] producerThreads = new Thread?[scenario.Producers];
            ConsumerState consumerState = new();
            Action<ReadEvent> consume = consumerState.Consume;

            Thread consumer = new(() =>
            {
                MarkReady();
                try
                {
                    control?.WaitForRelease();
                    _launch.Wait();
                    long started = Stopwatch.GetTimestamp();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

                    while (Volatile.Read(ref _failed) == 0)
                    {
                        if (DrainAvailable(transport, drainMode, consume) != 0)
                        {
                            continue;
                        }

                        if (Volatile.Read(ref _remainingProducers) != 0)
                        {
                            Thread.Yield();
                            continue;
                        }

                        // Producers publish before decrementing _remainingProducers. Once zero is
                        // observed, perform two more empty drains to close the final-read race.
                        if (DrainAvailable(transport, drainMode, consume) != 0)
                        {
                            continue;
                        }

                        if (
                            DrainAvailable(transport, drainMode, consume) == 0
                            && Volatile.Read(ref _remainingProducers) == 0
                        )
                        {
                            break;
                        }
                    }

                    _consumerResult = new ConsumerExecution(
                        consumerState.Drained,
                        consumerState.IdentitySum,
                        consumerState.IdentityXor,
                        Stopwatch.GetElapsedTime(started).TotalSeconds,
                        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore
                    );
                }
                catch (Exception exception)
                {
                    RecordFailure(exception);
                }
                finally
                {
                    _consumerDone.Set();
                    WorkerFinished();
                }
            })
            {
                IsBackground = true,
                Name = "LoadingCache.ReadProbe.consumer",
            };

            Exception? primaryFailure = null;
            Execution? executionResult = null;
            int alive;
            try
            {
                StartWorker(consumer);
                for (int producer = 0; producer < scenario.Producers; producer++)
                {
                    int producerIndex = producer;
                    Thread producerThread = new(() =>
                    {
                        MarkReady();
                        try
                        {
                            control?.WaitForRelease();
                            _launch.Wait();
                            long started = Stopwatch.GetTimestamp();
                            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                            long accepted = 0;
                            long identitySum = 0;
                            long identityXor = 0;
                            ReadEvent[] values = events.Values[producerIndex];

                            for (int round = 0; round < scenario.Rounds; round++)
                            {
                                foreach (ReadEvent value in values)
                                {
                                    if (!transport.TryEnqueue(value))
                                        continue;
                                    accepted++;
                                    identitySum = checked(identitySum + value.Identity);
                                    identityXor ^= value.Identity;
                                }
                            }

                            producerResults[producerIndex] = new ProducerExecution(
                                accepted,
                                identitySum,
                                identityXor,
                                Stopwatch.GetElapsedTime(started).TotalSeconds,
                                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore
                            );
                        }
                        catch (Exception exception)
                        {
                            RecordFailure(exception);
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref _remainingProducers) == 0)
                            {
                                Volatile.Write(
                                    ref _producersCompletedTimestamp,
                                    Stopwatch.GetTimestamp()
                                );
                                _producersDone.Set();
                            }
                            WorkerFinished();
                        }
                    })
                    {
                        IsBackground = true,
                        Name = $"LoadingCache.ReadProbe.producer{producerIndex}",
                    };
                    producerThreads[producer] = producerThread;
                    StartWorker(producerThread);
                }

                WaitOrThrow(
                    _allReady,
                    "read-probe workers did not become ready",
                    control?.WaitTimeout
                );
                long processAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                long[] collectionCountsBefore = CollectionCounts();
                long timedStarted = Stopwatch.GetTimestamp();
                _launch.Set();
                WaitOrThrow(
                    _producersDone,
                    "read-probe producers did not finish",
                    control?.WaitTimeout
                );
                WaitOrThrow(
                    _consumerDone,
                    "read-probe consumer did not drain",
                    control?.WaitTimeout
                );
                long timedFinished = Stopwatch.GetTimestamp();

                lock (_failureGate)
                {
                    if (_failure is not null)
                    {
                        throw new InvalidOperationException("Read probe worker failed.", _failure);
                    }
                }

                long accepted = 0;
                long acceptedIdentitySum = 0;
                long acceptedIdentityXor = 0;
                double producerWallSeconds = 0;
                long writerAllocatedBytes = 0;
                foreach (ProducerExecution result in producerResults)
                {
                    accepted = checked(accepted + result.Accepted);
                    acceptedIdentitySum = checked(acceptedIdentitySum + result.IdentitySum);
                    acceptedIdentityXor ^= result.IdentityXor;
                    producerWallSeconds = Math.Max(producerWallSeconds, result.Seconds);
                    writerAllocatedBytes = checked(writerAllocatedBytes + result.AllocatedBytes);
                }

                long producerFinished = Volatile.Read(ref _producersCompletedTimestamp);
                executionResult = new Execution(
                    accepted,
                    _consumerResult.Drained,
                    acceptedIdentitySum,
                    _consumerResult.IdentitySum,
                    acceptedIdentityXor,
                    _consumerResult.IdentityXor,
                    Stopwatch.GetElapsedTime(timedStarted, timedFinished).TotalSeconds,
                    producerWallSeconds,
                    _consumerResult.Seconds,
                    Stopwatch.GetElapsedTime(producerFinished, timedFinished).TotalSeconds,
                    writerAllocatedBytes,
                    GC.GetTotalAllocatedBytes(precise: false) - processAllocatedBefore,
                    CollectionDelta(collectionCountsBefore)
                );
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
            }
            finally
            {
                Interlocked.Exchange(ref _failed, 1);
                _launch.Set();
                alive = JoinWorkers(
                    [consumer, .. producerThreads],
                    control?.CleanupTimeout ?? TimeSpan.FromSeconds(30)
                );
                if (alive != 0)
                {
                    _retireTransport = true;
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
        int? failBeforeStart
    ) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        internal Exception Failure { get; } =
            new InvalidOperationException("Injected setup or drain failure.");
        internal List<Thread> Started { get; } = [];
        internal TimeSpan CleanupTimeout => blockWorkers ? TimeSpan.Zero : TimeSpan.FromSeconds(30);
        internal TimeSpan WaitTimeout => blockWorkers ? TimeSpan.Zero : TimeSpan.FromSeconds(30);

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
            "Read probe cleanup timed out; live workers retain their gates and transport until exit.",
            primaryFailure is null
                ? [new TimeoutException($"{alive} read-probe workers did not terminate.")]
                :
                [
                    primaryFailure,
                    new TimeoutException($"{alive} read-probe workers did not terminate."),
                ]
        );

    private static int DrainAvailable(
        IReadTransport transport,
        string drainMode,
        Action<ReadEvent> consumer
    )
    {
        if (drainMode == "batch")
        {
            int drained = 0;
            while (true)
            {
                int batch = transport.DrainBatch(consumer, BatchDrainBudget);
                if (batch == 0)
                {
                    return drained;
                }

                drained = checked(drained + batch);
            }
        }

        int count = 0;
        while (transport.TryRead(out ReadEvent? value))
        {
            consumer(value!);
            count++;
        }

        return count;
    }

    private static void WaitOrThrow(
        ManualResetEventSlim gate,
        string message,
        TimeSpan? timeout = null
    )
    {
        if (!gate.Wait(timeout ?? TimeSpan.FromSeconds(MaxJoinSeconds)))
        {
            throw new TimeoutException(message);
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

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

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

    private static string ReadString(string[] args, string option, string fallback) =>
        ReadOptionalString(args, option) ?? fallback;

    private static int ReadInt(string[] args, string option, int fallback, int minimum, int maximum)
    {
        string raw = ReadString(args, option, fallback.ToString(CultureInfo.InvariantCulture));
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

    private sealed record ProbeOptions(
        [property: JsonInclude] string Label,
        [property: JsonInclude] string Transport,
        [property: JsonInclude] string Mode,
        [property: JsonInclude] string DrainMode,
        [property: JsonInclude] int Seed,
        [property: JsonInclude] int StripeCount,
        [property: JsonInclude] int StripeCapacity,
        [property: JsonInclude] int OperationsPerProducer,
        [property: JsonInclude] int TargetOperations,
        int[] ProducerCounts,
        [property: JsonInclude] int Runs,
        [property: JsonInclude] int Warmups,
        string? OutputPath,
        bool NoProgress
    )
    {
        public static ProbeOptions Parse(string[] args)
        {
            string mode = ReadString(args, "--mode", "all");
            if (mode is not ("single" or "concurrent" or "all"))
            {
                throw new ArgumentException("--mode must be single, concurrent, or all.");
            }

            string drainMode = ReadString(args, "--drain-mode", "try-read");
            if (drainMode is not ("try-read" or "batch"))
            {
                throw new ArgumentException("--drain-mode must be try-read or batch.");
            }

            int stripeCount = ReadInt(args, "--stripe-count", 4, 1, 64);
            if ((stripeCount & (stripeCount - 1)) != 0)
            {
                throw new ArgumentException("--stripe-count must be a power of two.");
            }

            int[] producers = ParseProducerCounts(ReadString(args, "--producers", "1,4,10,20"));
            return new ProbeOptions(
                Label: ReadString(args, "--label", "unlabelled"),
                Transport: ReadString(args, "--transport", "current"),
                Mode: mode,
                DrainMode: drainMode,
                Seed: ReadInt(args, "--seed", 419, 0, int.MaxValue),
                StripeCount: stripeCount,
                StripeCapacity: ReadInt(args, "--stripe-capacity", 256, 1, 1_000_000),
                OperationsPerProducer: ReadInt(
                    args,
                    "--operations-per-producer",
                    DefaultOperationsPerProducer,
                    1,
                    2_000_000
                ),
                TargetOperations: ReadInt(
                    args,
                    "--target-operations",
                    DefaultTargetOperations,
                    1,
                    100_000_000
                ),
                ProducerCounts: producers,
                Runs: ReadInt(args, "--runs", DefaultRuns, 1, 100),
                Warmups: ReadInt(args, "--warmups", DefaultWarmups, 0, 100),
                OutputPath: ReadOptionalString(args, "--output"),
                NoProgress: args.Contains("--no-progress", StringComparer.Ordinal)
            );
        }

        private static int[] ParseProducerCounts(string raw)
        {
            string[] tokens = raw.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (tokens.Length == 0)
            {
                throw new ArgumentException("--producers must contain at least one count.");
            }

            int[] values = new int[tokens.Length];
            for (int index = 0; index < tokens.Length; index++)
            {
                if (
                    !int.TryParse(tokens[index], CultureInfo.InvariantCulture, out int value)
                    || value < 1
                    || value > 64
                )
                {
                    throw new ArgumentException(
                        "--producers values must be integers from 1 to 64."
                    );
                }

                if (Array.IndexOf(values, value, 0, index) >= 0)
                {
                    throw new ArgumentException($"--producers contains duplicate count {value}.");
                }

                values[index] = value;
            }

            return values;
        }
    }

    private sealed record Scenario(
        [property: JsonInclude] string Mode,
        [property: JsonInclude] int Producers,
        [property: JsonInclude] int OperationsPerProducer,
        int Rounds
    )
    {
        public string Name => $"{Mode}-p{Producers}-ops{OperationsPerProducer}";

        public long Attempts => checked((long)Producers * OperationsPerProducer * Rounds);
    }

    private sealed record ReferenceEvents(ReadEvent[][] Values)
    {
        public static ReferenceEvents Create(Scenario scenario, int seed)
        {
            ReadEvent[][] values = new ReadEvent[scenario.Producers][];
            for (int producer = 0; producer < scenario.Producers; producer++)
            {
                ReadEvent[] producerValues = new ReadEvent[scenario.OperationsPerProducer];
                for (int index = 0; index < producerValues.Length; index++)
                {
                    int identity = checked(
                        seed + producer * scenario.OperationsPerProducer + index + 1
                    );
                    producerValues[index] = new ReadEvent(producer, index, identity);
                }

                values[producer] = producerValues;
            }

            return new ReferenceEvents(values);
        }
    }

    private sealed class ReadEvent(int producer, int sequence, int identity)
    {
        // Retain the event payload layout used by the transport benchmark.
        [UsedImplicitly]
        public int Producer { get; } = producer;

        [UsedImplicitly]
        public int Sequence { get; } = sequence;

        public int Identity { get; } = identity;
    }

    private sealed class ConsumerState
    {
        public long Drained { get; private set; }

        public long IdentitySum { get; private set; }

        public long IdentityXor { get; private set; }

        public void Consume(ReadEvent value)
        {
            Drained++;
            IdentitySum = checked(IdentitySum + value.Identity);
            IdentityXor ^= value.Identity;
        }
    }

    private interface IReadTransport : IDisposable
    {
        bool SupportsBatchDrain { get; }

        bool TryEnqueue(ReadEvent value);

        bool TryRead(out ReadEvent? value);

        int DrainBatch(Action<ReadEvent> consumer, int budget);

        TransportStatistics GetStatistics();
    }

    private sealed class StripedTransport : IReadTransport
    {
        private static readonly PropertyInfo? DroppedFailedProperty =
            typeof(ReadBufferStatistics).GetProperty(
                "DroppedFailed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

        private readonly StripedReadBuffer<ReadEvent> _buffer;
        private readonly Func<Action<ReadEvent>, int, int>? _drainTo;

        public StripedTransport(int stripeCount, int stripeCapacity)
        {
            _buffer = new StripedReadBuffer<ReadEvent>(stripeCount, stripeCapacity);
            MethodInfo? method = typeof(StripedReadBuffer<ReadEvent>).GetMethod(
                "DrainTo",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(Action<ReadEvent>), typeof(int)],
                modifiers: null
            );
            _drainTo = (Func<Action<ReadEvent>, int, int>?)
                method?.CreateDelegate(typeof(Func<Action<ReadEvent>, int, int>), _buffer);
        }

        public bool SupportsBatchDrain => _drainTo is not null;

        public bool TryEnqueue(ReadEvent value) => _buffer.TryEnqueue(value);

        public bool TryRead(out ReadEvent? value)
        {
            bool result = _buffer.TryRead(out ReadEvent candidate);
            value = candidate;
            return result;
        }

        public int DrainBatch(Action<ReadEvent> consumer, int budget) =>
            _drainTo?.Invoke(consumer, budget)
            ?? throw new InvalidOperationException("This transport does not expose DrainTo.");

        public TransportStatistics GetStatistics()
        {
            ReadBufferStatistics statistics = _buffer.GetStatistics();
            long droppedFailed = DroppedFailedProperty?.GetValue(statistics) is long value
                ? value
                : 0;
            return new TransportStatistics(
                statistics.IsDisposed,
                statistics.Queued,
                statistics.Enqueued,
                statistics.Dequeued,
                statistics.DroppedFull,
                droppedFailed,
                statistics.DroppedShutdown,
                DroppedFailedProperty is not null
            );
        }

        public void Dispose() => _buffer.Dispose();
    }

    private sealed record TransportStatistics(
        bool IsDisposed,
        long Queued,
        long Enqueued,
        long Dequeued,
        [property: JsonInclude] long DroppedFull,
        [property: JsonInclude] long DroppedFailed,
        [property: JsonInclude] long DroppedShutdown,
        bool HasDroppedFailed
    );

    private sealed record ProducerExecution(
        long Accepted,
        long IdentitySum,
        long IdentityXor,
        double Seconds,
        long AllocatedBytes
    );

    private readonly record struct ConsumerExecution(
        long Drained,
        long IdentitySum,
        long IdentityXor,
        double Seconds,
        // Preserve the collected worker result and its measured value-type layout.
        [property: UsedImplicitly] long AllocatedBytes
    );

    private sealed record Execution(
        long Accepted,
        long Drained,
        [property: JsonInclude] long AcceptedIdentitySum,
        [property: JsonInclude] long DrainedIdentitySum,
        [property: JsonInclude] long AcceptedIdentityXor,
        [property: JsonInclude] long DrainedIdentityXor,
        double ElapsedSeconds,
        [property: JsonInclude] double ProducerWallSeconds,
        [property: JsonInclude] double ConsumerWallSeconds,
        [property: JsonInclude] double CleanupDrainSeconds,
        [property: JsonInclude] long WriterAllocatedBytes,
        [property: JsonInclude] long ProcessAllocatedBytes,
        long[] GcCollections
    );

    private sealed record ProbeReport(
        [property: JsonInclude] int SchemaVersion,
        [property: JsonInclude] string Label,
        [property: JsonInclude] string Transport,
        [property: JsonInclude] string Mode,
        [property: JsonInclude] string DrainMode,
        [property: JsonInclude] int Seed,
        [property: JsonInclude] int StripeCount,
        [property: JsonInclude] int StripeCapacity,
        [property: JsonInclude] int OperationsPerProducer,
        [property: JsonInclude] int TargetOperations,
        [property: JsonInclude] IReadOnlyList<int> ProducerCounts,
        [property: JsonInclude] int Runs,
        [property: JsonInclude] int Warmups,
        [property: JsonInclude] string Runtime,
        [property: JsonInclude] string Architecture,
        [property: JsonInclude] string Os,
        [property: JsonInclude] int LogicalProcessors,
        [property: JsonInclude] string? ProcessorIdentifier,
        [property: JsonInclude] bool ServerGc,
        [property: JsonInclude] string GcLatencyMode,
        [property: JsonInclude] RuntimeEnvironmentSnapshot RuntimeEnvironment,
        [property: JsonInclude] string CacheAssemblySha256,
        [property: JsonInclude] string HarnessAssemblySha256,
        [property: JsonInclude] IReadOnlyList<ProbeSample> Samples
    );

    private sealed record ProbeSample(
        [property: JsonInclude] bool Warmup,
        [property: JsonInclude] int SampleIndex,
        [property: JsonInclude] string Name,
        [property: JsonInclude] string Mode,
        [property: JsonInclude] string DrainMode,
        [property: JsonInclude] int Producers,
        [property: JsonInclude] int Rounds,
        [property: JsonInclude] int StripeCount,
        [property: JsonInclude] int StripeCapacity,
        [property: JsonInclude] int OperationsPerProducer,
        long Attempts,
        long Accepted,
        long Drained,
        [property: JsonInclude] long Dropped,
        [property: JsonInclude] long DroppedPolicy,
        [property: JsonInclude] long DroppedFull,
        [property: JsonInclude] long DroppedFailed,
        [property: JsonInclude] long DroppedShutdown,
        [property: JsonInclude] long UnaccountedRejected,
        [property: JsonInclude] long ShutdownAttempts,
        [property: JsonInclude] long ShutdownRejected,
        [property: JsonInclude] double AcceptanceFraction,
        double ElapsedSeconds,
        [property: JsonInclude] double ProducerWallSeconds,
        [property: JsonInclude] double ConsumerWallSeconds,
        [property: JsonInclude] double CleanupDrainSeconds,
        [property: JsonInclude] long WriterAllocatedBytes,
        [property: JsonInclude] long ProcessAllocatedBytes,
        [property: JsonInclude] long[] TimedGcCollections,
        [property: JsonInclude] long FinalQueued,
        [property: JsonInclude] long FinalEnqueued,
        [property: JsonInclude] long FinalDequeued,
        [property: JsonInclude] bool FinalDisposed,
        [property: JsonInclude] long AcceptedIdentitySum,
        [property: JsonInclude] long DrainedIdentitySum,
        [property: JsonInclude] long AcceptedIdentityXor,
        [property: JsonInclude] long DrainedIdentityXor,
        [property: JsonInclude] bool IdentityMatch,
        [property: JsonInclude] bool BoundedFinalCountsHold
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
}
