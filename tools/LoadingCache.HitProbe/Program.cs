using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

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
            using ICache<int, int> cache = CreateCache(options);
            int[] keys = Enumerable.Range(0, options.Residents).ToArray();

            for (int key = 0; key < options.Residents; key++)
            {
                cache.Put(key, key + 1);
            }

            cache.CleanUp();
            CacheSnapshot initial = Snapshot(cache);
            if (
                initial.ResidentCount != options.Residents
                || initial.WeightedSize != options.Residents
            )
            {
                throw new InvalidOperationException(
                    $"Initial cache bounds failed: residents={initial.ResidentCount}, "
                        + $"weightedSize={initial.WeightedSize}, expected={options.Residents}."
                );
            }

            List<ProbeSample> samples = [];
            using (ReaderCoordinator coordinator = new(cache, keys, options))
            {
                for (int sampleIndex = 0; sampleIndex < options.SampleCount; sampleIndex++)
                {
                    bool warmup = sampleIndex < options.Warmups;
                    samples.Add(coordinator.RunSample(sampleIndex, warmup));
                }
            }

            ProbeReport report = new(
                SchemaVersion: 1,
                Capacity: options.Capacity,
                Residents: options.Residents,
                Pattern: options.Pattern,
                Workers: options.Workers,
                Statistics: options.Statistics,
                Warmups: options.Warmups,
                Runs: options.Runs,
                DurationMilliseconds: options.DurationMilliseconds,
                Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OS: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
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

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"LoadingCache.HitProbe failed: {exception}");
            return 1;
        }
    }

    private static ICache<int, int> CreateCache(ProbeOptions options)
    {
        CacheBuilder<int, int> builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(options.Capacity)
            .MaxConcurrentLoads(1);
        if (options.Statistics)
        {
            builder.RecordStatistics();
        }

        return builder.Build();
    }

    private static CacheSnapshot Snapshot(ICache<int, int> cache)
    {
        IEvictionPolicy<int, int> eviction =
            cache.Policy.Eviction
            ?? throw new InvalidOperationException(
                "The size-bounded cache has no eviction policy."
            );
        return new(cache.EstimatedCount, eviction.WeightedSize);
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

    private sealed class ReaderCoordinator : IDisposable
    {
        private readonly ICache<int, int> _cache;
        private readonly int[] _keys;
        private readonly int _mask;
        private readonly int _workerCount;
        private readonly int _sampleCount;
        private readonly long _durationTicks;
        private readonly bool _statistics;
        private readonly Barrier _barrier;
        private readonly CountdownEvent _ready;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread[] _threads;
        private readonly WorkerResult[] _results;
        private readonly object _failureGate = new();
        private Exception? _failure;
        private long _deadline;
        private bool _disposed;

        internal ReaderCoordinator(ICache<int, int> cache, int[] keys, ProbeOptions options)
        {
            _cache = cache;
            _keys = keys;
            _mask = options.Pattern == "hot" ? 0 : options.Residents - 1;
            _workerCount = options.Workers;
            _sampleCount = options.SampleCount;
            _statistics = options.Statistics;
            _durationTicks = checked(
                (long)options.DurationMilliseconds * Stopwatch.Frequency / 1_000
            );
            _barrier = new Barrier(_workerCount + 1);
            _ready = new CountdownEvent(_workerCount);
            _threads = new Thread[_workerCount];
            _results = new WorkerResult[_workerCount];

            try
            {
                for (int worker = 0; worker < _workerCount; worker++)
                {
                    int workerIndex = worker;
                    _threads[worker] = new Thread(() => WorkerLoop(workerIndex))
                    {
                        IsBackground = true,
                        Name = $"LoadingCache.HitProbe.reader{workerIndex}",
                    };
                    _threads[worker].Start();
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

        internal ProbeSample RunSample(int sampleIndex, bool warmup)
        {
            ThrowIfWorkerFailed();

            CacheStatistics beforeStatistics = _cache.Statistics;
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

            CacheStatistics afterStatistics = _cache.Statistics;
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
            CacheSnapshot snapshot = Snapshot(_cache);
            bool boundsPassed =
                snapshot.ResidentCount == _keys.Length
                && snapshot.ResidentCount <= snapshot.WeightedSize
                && snapshot.WeightedSize <= snapshot.ResidentCount
                && snapshot.WeightedSize <= int.MaxValue;
            if (!boundsPassed)
            {
                throw new InvalidOperationException(
                    $"Cache bounds failed in sample {sampleIndex}: residents={snapshot.ResidentCount}, "
                        + $"weightedSize={snapshot.WeightedSize}, expected={_keys.Length}."
                );
            }

            double wallSeconds = Stopwatch.GetElapsedTime(wallStarted, wallFinished).TotalSeconds;
            double cleanupSeconds = Stopwatch
                .GetElapsedTime(cleanupStarted, cleanupFinished)
                .TotalSeconds;

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
                GcCollections: gcCollections
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
                            int key = _keys[(int)(index & _mask)];
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
            foreach (Thread thread in _threads)
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

    private sealed record ProbeOptions(
        int Capacity,
        int Residents,
        string Pattern,
        int Workers,
        bool Statistics,
        int Warmups,
        int Runs,
        int DurationMilliseconds,
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
                        "--capacity"
                        or "--residents"
                        or "--pattern"
                        or "--workers"
                        or "--statistics"
                        or "--warmups"
                        or "--runs"
                        or "--duration-ms"
                        or "--output"
                    )
                )
                {
                    throw new ArgumentException($"Unknown option '{option}'.");
                }
            }

            int capacity = ReadInt(values, "--capacity", 1_024, 1, 1 << 26);
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

            if (!IsPowerOfTwo(capacity) || !IsPowerOfTwo(residents))
            {
                throw new ArgumentException(
                    "Capacity and residents must be positive powers of two."
                );
            }

            long durationTicks = checked((long)durationMilliseconds * Stopwatch.Frequency / 1_000);
            if (durationTicks <= 0)
            {
                throw new ArgumentException("Duration must resolve to positive stopwatch ticks.");
            }

            return new(
                capacity,
                residents,
                pattern,
                workers,
                statisticsValue == "on",
                warmups,
                runs,
                durationMilliseconds,
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

    private readonly record struct CacheSnapshot(long ResidentCount, long WeightedSize);

    private readonly record struct WorkerResult(long Operations, long Checksum, long Misses);

    private sealed record ProbeReport(
        int SchemaVersion,
        int Capacity,
        int Residents,
        string Pattern,
        int Workers,
        bool Statistics,
        int Warmups,
        int Runs,
        int DurationMilliseconds,
        string Runtime,
        string OS,
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
        long WeightedSize,
        bool BoundsPassed,
        double ReadOnlyOperationsPerSecond,
        double OperationsPerSecondIncludingCleanup,
        long ManagedAllocatedBytes,
        long[] GcCollections
    );

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
