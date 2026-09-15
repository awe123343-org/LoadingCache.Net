using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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
            OS: RuntimeInformation.OSDescription,
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

        if (options.Mode is "concurrent" or "all")
        {
            foreach (int producerCount in options.ProducerCounts)
            {
                yield return CreateScenario("concurrent", producerCount, options);
            }
        }
    }

    private static Scenario CreateScenario(string mode, int producers, ProbeOptions options)
    {
        long operationsPerRound = checked((long)producers * options.OperationsPerProducer);
        long rounds = Math.Max(
            1,
            (options.TargetOperations + operationsPerRound - 1) / operationsPerRound
        );
        return new(mode, producers, options.OperationsPerProducer, checked((int)rounds));
    }

    private static ProbeSample RunSample(
        ProbeOptions options,
        Scenario scenario,
        ReferenceEvents events,
        int sampleIndex,
        bool warmup
    )
    {
        using IReadTransport transport = new StripedTransport(
            options.StripeCount,
            options.StripeCapacity
        );
        if (options.DrainMode == "batch" && !transport.SupportsBatchDrain)
        {
            throw new InvalidOperationException(
                "--drain-mode batch requires a StripedReadBuffer implementation exposing "
                    + "the optional DrainTo(Action<TEvent>, int) method."
            );
        }

        Execution execution = Execute(transport, options.DrainMode, scenario, events);
        TransportStatistics beforeDispose = transport.GetStatistics();
        if (beforeDispose.Queued != 0)
        {
            throw new InvalidOperationException(
                $"{scenario.Name} left {beforeDispose.Queued} queued events before shutdown."
            );
        }

        transport.Dispose();
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
                    if (transport.TryEnqueue(value))
                    {
                        accepted++;
                        acceptedIdentitySum = checked(acceptedIdentitySum + value.Identity);
                        acceptedIdentityXor ^= value.Identity;
                    }
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
        ReferenceEvents events
    )
    {
        if (scenario.Mode == "single")
        {
            return ExecuteSingle(transport, drainMode, scenario, events);
        }

        using ManualResetEventSlim allReady = new(false);
        using ManualResetEventSlim launch = new(false);
        using ManualResetEventSlim producersDone = new(false);
        using ManualResetEventSlim consumerDone = new(false);

        ProducerExecution[] producerResults = new ProducerExecution[scenario.Producers];
        Thread[] producerThreads = new Thread[scenario.Producers];
        int readyCount = 0;
        int remainingProducers = scenario.Producers;
        int failed = 0;
        long producersCompletedTimestamp = 0;
        Exception? failure = null;
        object failureGate = new();
        ConsumerExecution consumerResult = default;
        ConsumerState consumerState = new();
        Action<ReadEvent> consume = consumerState.Consume;

        void MarkReady()
        {
            if (Interlocked.Increment(ref readyCount) == scenario.Producers + 1)
            {
                allReady.Set();
            }
        }

        void RecordFailure(Exception exception)
        {
            Interlocked.Exchange(ref failed, 1);
            lock (failureGate)
            {
                failure ??= exception;
            }

            allReady.Set();
            launch.Set();
            producersDone.Set();
            consumerDone.Set();
        }

        Thread consumer = new(() =>
        {
            MarkReady();
            try
            {
                launch.Wait();
                long started = Stopwatch.GetTimestamp();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

                while (Volatile.Read(ref failed) == 0)
                {
                    if (DrainAvailable(transport, drainMode, consume) != 0)
                    {
                        continue;
                    }

                    if (Volatile.Read(ref remainingProducers) != 0)
                    {
                        Thread.Yield();
                        continue;
                    }

                    // Producers publish before decrementing remainingProducers. Once zero is
                    // observed, perform two more empty drains to close the final-read race.
                    if (DrainAvailable(transport, drainMode, consume) != 0)
                    {
                        continue;
                    }

                    if (
                        DrainAvailable(transport, drainMode, consume) == 0
                        && Volatile.Read(ref remainingProducers) == 0
                    )
                    {
                        break;
                    }
                }

                consumerResult = new ConsumerExecution(
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
                consumerDone.Set();
            }
        })
        {
            IsBackground = true,
            Name = "LoadingCache.ReadProbe.consumer",
        };

        consumer.Start();
        for (int producer = 0; producer < scenario.Producers; producer++)
        {
            int producerIndex = producer;
            producerThreads[producer] = new Thread(() =>
            {
                MarkReady();
                try
                {
                    launch.Wait();
                    long started = Stopwatch.GetTimestamp();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    long accepted = 0;
                    long identitySum = 0;
                    long identityXor = 0;
                    ReadEvent[] values = events.Values[producerIndex];

                    for (int round = 0; round < scenario.Rounds; round++)
                    {
                        for (int index = 0; index < values.Length; index++)
                        {
                            ReadEvent value = values[index];
                            if (transport.TryEnqueue(value))
                            {
                                accepted++;
                                identitySum = checked(identitySum + value.Identity);
                                identityXor ^= value.Identity;
                            }
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
                    if (Interlocked.Decrement(ref remainingProducers) == 0)
                    {
                        Volatile.Write(ref producersCompletedTimestamp, Stopwatch.GetTimestamp());
                        producersDone.Set();
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"LoadingCache.ReadProbe.producer{producerIndex}",
            };
            producerThreads[producer].Start();
        }

        long timedStarted = 0;
        long timedFinished = 0;
        bool normalCompletion = false;
        long processAllocatedBefore = 0;
        long[] collectionCountsBefore = [];
        try
        {
            WaitOrThrow(allReady, "read-probe workers did not become ready");
            processAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            collectionCountsBefore = CollectionCounts();
            timedStarted = Stopwatch.GetTimestamp();
            launch.Set();
            WaitOrThrow(producersDone, "read-probe producers did not finish");
            WaitOrThrow(consumerDone, "read-probe consumer did not drain");
            timedFinished = Stopwatch.GetTimestamp();

            lock (failureGate)
            {
                if (failure is not null)
                {
                    throw new InvalidOperationException("Read probe worker failed.", failure);
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

            long producerFinished = Volatile.Read(ref producersCompletedTimestamp);
            normalCompletion = true;
            return new Execution(
                accepted,
                consumerResult.Drained,
                acceptedIdentitySum,
                consumerResult.IdentitySum,
                acceptedIdentityXor,
                consumerResult.IdentityXor,
                Stopwatch.GetElapsedTime(timedStarted, timedFinished).TotalSeconds,
                producerWallSeconds,
                consumerResult.Seconds,
                Stopwatch.GetElapsedTime(producerFinished, timedFinished).TotalSeconds,
                writerAllocatedBytes,
                GC.GetTotalAllocatedBytes(precise: false) - processAllocatedBefore,
                CollectionDelta(collectionCountsBefore)
            );
        }
        finally
        {
            if (!normalCompletion)
            {
                launch.Set();
                producersDone.Set();
                consumerDone.Set();
            }

            JoinOrThrow(consumer, "consumer");
            foreach (Thread producer in producerThreads)
            {
                JoinOrThrow(producer, "producer");
            }
        }
    }

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

    private static void WaitOrThrow(ManualResetEventSlim gate, string message)
    {
        if (!gate.Wait(TimeSpan.FromSeconds(MaxJoinSeconds)))
        {
            throw new TimeoutException(message);
        }
    }

    private static void JoinOrThrow(Thread thread, string role)
    {
        if (!thread.Join(TimeSpan.FromSeconds(MaxJoinSeconds)))
        {
            throw new TimeoutException($"read-probe {role} did not exit");
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
        string Label,
        string Transport,
        string Mode,
        string DrainMode,
        int Seed,
        int StripeCount,
        int StripeCapacity,
        int OperationsPerProducer,
        int TargetOperations,
        int[] ProducerCounts,
        int Runs,
        int Warmups,
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
            return new(
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
        string Mode,
        int Producers,
        int OperationsPerProducer,
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
        public int Producer { get; } = producer;

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
            _drainTo = method is null
                ? null
                : (Func<Action<ReadEvent>, int, int>)
                    method.CreateDelegate(typeof(Func<Action<ReadEvent>, int, int>), _buffer);
        }

        public bool SupportsBatchDrain => _drainTo is not null;

        public bool TryEnqueue(ReadEvent value) => _buffer.TryEnqueue(value);

        public bool TryRead(out ReadEvent? value)
        {
            ReadEvent candidate = null!;
            bool result = _buffer.TryRead(out candidate);
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
            return new(
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
        long DroppedFull,
        long DroppedFailed,
        long DroppedShutdown,
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
        long AllocatedBytes
    );

    private sealed record Execution(
        long Accepted,
        long Drained,
        long AcceptedIdentitySum,
        long DrainedIdentitySum,
        long AcceptedIdentityXor,
        long DrainedIdentityXor,
        double ElapsedSeconds,
        double ProducerWallSeconds,
        double ConsumerWallSeconds,
        double CleanupDrainSeconds,
        long WriterAllocatedBytes,
        long ProcessAllocatedBytes,
        long[] GcCollections
    );

    private sealed record ProbeReport(
        int SchemaVersion,
        string Label,
        string Transport,
        string Mode,
        string DrainMode,
        int Seed,
        int StripeCount,
        int StripeCapacity,
        int OperationsPerProducer,
        int TargetOperations,
        IReadOnlyList<int> ProducerCounts,
        int Runs,
        int Warmups,
        string Runtime,
        string Architecture,
        string OS,
        int LogicalProcessors,
        string? ProcessorIdentifier,
        bool ServerGc,
        string GcLatencyMode,
        RuntimeEnvironmentSnapshot RuntimeEnvironment,
        string CacheAssemblySha256,
        string HarnessAssemblySha256,
        IReadOnlyList<ProbeSample> Samples
    );

    private sealed record ProbeSample(
        bool Warmup,
        int SampleIndex,
        string Name,
        string Mode,
        string DrainMode,
        int Producers,
        int Rounds,
        int StripeCount,
        int StripeCapacity,
        int OperationsPerProducer,
        long Attempts,
        long Accepted,
        long Drained,
        long Dropped,
        long DroppedPolicy,
        long DroppedFull,
        long DroppedFailed,
        long DroppedShutdown,
        long UnaccountedRejected,
        long ShutdownAttempts,
        long ShutdownRejected,
        double AcceptanceFraction,
        double ElapsedSeconds,
        double ProducerWallSeconds,
        double ConsumerWallSeconds,
        double CleanupDrainSeconds,
        long WriterAllocatedBytes,
        long ProcessAllocatedBytes,
        long[] TimedGcCollections,
        long FinalQueued,
        long FinalEnqueued,
        long FinalDequeued,
        bool FinalDisposed,
        long AcceptedIdentitySum,
        long DrainedIdentitySum,
        long AcceptedIdentityXor,
        long DrainedIdentityXor,
        bool IdentityMatch,
        bool BoundedFinalCountsHold
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
}
