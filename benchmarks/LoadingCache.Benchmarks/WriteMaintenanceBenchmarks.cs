using BenchmarkDotNet.Attributes;
using JetBrains.Annotations;

namespace LoadingCache.Benchmarks;

/// <summary>
/// Measures cache writes together with a bounded quiescent maintenance drain.
///
/// The benchmark intentionally includes the cleanup loop after the write batch. A write-buffer
/// implementation must therefore account for both admission and policy work; enqueue-only speed
/// is not sufficient evidence of an improvement.
/// </summary>
[MemoryDiagnoser]
public class WriteMaintenanceBenchmarks : IDisposable
{
    private const int Capacity = 1024;
    private const int QuiescencePassLimit = 128;
    private const int TraceWindows = 4;

    private ICache<int, int> _cache = null!;
    private int[] _keys = [];
    private ParallelOptions _parallelOptions = null!;
    private int _nextWindow;

    /// <summary>Gets or sets the number of workers writing the pre-generated trace.</summary>
    [UsedImplicitly]
    [Params(1, 4)]
    public int Workers { get; set; }

    /// <summary>Gets or sets the number of writes performed by each worker.</summary>
    [UsedImplicitly]
    [Params(512, 4096)]
    public int WritesPerWorker { get; set; }

    /// <summary>Creates a bounded cache and a repeatable trace outside timed iterations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(Capacity)
            .MaxConcurrentLoads(1)
            .RecordStatistics()
            .Build();
        _keys = CreateTrace(Workers * WritesPerWorker * TraceWindows);
        _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Workers };
        _nextWindow = 0;
    }

    /// <summary>Starts each invocation from an empty, quiescent cache.</summary>
    [IterationSetup]
    public void Reset()
    {
        _cache.Clear();
        _cache.CleanUp();
        _nextWindow = 0;
    }

    /// <summary>
    /// Measures concurrent writes and the maintenance required to reach a quiescent bounded state.
    /// </summary>
    [Benchmark]
    public int PutBatchAndQuiesce()
    {
        int window = Interlocked.Increment(ref _nextWindow) - 1;
        int start = window % TraceWindows * Workers * WritesPerWorker;

        if (Workers == 1)
        {
            for (int index = 0; index < WritesPerWorker; index++)
            {
                int key = _keys[start + index];
                _cache.Put(key, key);
            }
        }
        else
        {
            Parallel.For(
                0,
                Workers,
                _parallelOptions,
                worker =>
                {
                    int workerStart = start + worker * WritesPerWorker;
                    for (int index = 0; index < WritesPerWorker; index++)
                    {
                        int key = _keys[workerStart + index];
                        _cache.Put(key, key);
                    }
                }
            );
        }

        Quiesce();
        return checked((int)_cache.EstimatedCount);
    }

    /// <summary>Releases the cache after the benchmark case.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Quiesce()
    {
        long previousWeightedSize = -1;
        long previousBacklog = -1;

        for (int pass = 0; pass < QuiescencePassLimit; pass++)
        {
            _cache.CleanUp();
            CacheStatistics statistics = _cache.Statistics;
            long weightedSize = _cache.Policy.Eviction?.WeightedSize ?? 0;

            if (
                statistics.MaintenanceBacklog == 0
                && weightedSize <= Capacity
                && _cache.EstimatedCount <= Capacity
                && weightedSize == previousWeightedSize
                && statistics.MaintenanceBacklog == previousBacklog
            )
            {
                return;
            }

            previousWeightedSize = weightedSize;
            previousBacklog = statistics.MaintenanceBacklog;
        }

        throw new InvalidOperationException(
            $"Cache did not become quiescent after {QuiescencePassLimit} maintenance passes."
        );
    }

    private static int[] CreateTrace(int length)
    {
        int[] trace = new int[length];
        for (int index = 0; index < trace.Length; index++)
        {
            trace[index] = index;
        }

        return trace;
    }
}

/// <summary>Measures the resident-hit path independently from write maintenance.</summary>
[MemoryDiagnoser]
public class WriteMaintenanceHitBenchmarks : IDisposable
{
    private ICache<int, int> _cache = null!;
    private int _residentKey;

    /// <summary>Gets or sets the number of resident entries before the timed lookup.</summary>
    [UsedImplicitly]
    [Params(1, 1024)]
    public int ResidentEntries { get; set; }

    /// <summary>Initializes resident cache entries outside timed iterations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _cache = CacheBuilder.Create<int, int>().MaximumSize(1024).MaxConcurrentLoads(1).Build();
        for (int key = 0; key < ResidentEntries; key++)
        {
            _cache.Put(key, 42);
        }

        _residentKey = ResidentEntries - 1;
        _cache.CleanUp();
    }

    /// <summary>Measures one resident lookup without writes or cleanup.</summary>
    [Benchmark]
    public int ResidentHit() => _cache.TryGet(_residentKey, out int value) ? value : -1;

    /// <summary>Releases the cache after the benchmark case.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }
}
