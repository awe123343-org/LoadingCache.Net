using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using BitFaster.Caching.Lfu;
using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;

namespace LoadingCache.Benchmarks;

/// <summary>Resident lookup cost only; no claim of loading or eviction equivalence.</summary>
[MemoryDiagnoser]
public class LookupBenchmarks : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, int> _dictionary = new();
    private readonly LockedLru _lru = new();
    private MemoryCache _memory = null!;
    private BitFaster.Caching.ICache<int, int> _bitFaster = null!;
    private ICache<int, int> _manual = null!;
    private IAsyncLoadingCache<int, int> _loading = null!;
    private CancellationTokenSource _waiter = null!;

    /// <summary>Gets or sets the resident capacity.</summary>
    [UsedImplicitly]
    [Params(64, 10_000)]
    public int Capacity { get; set; }

    /// <summary>Gets or sets whether LoadingCache and MemoryCache statistics are enabled.</summary>
    [UsedImplicitly]
    [Params(false, true)]
    public bool Statistics { get; set; }

    /// <summary>Initializes resident entries outside timed iterations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _memory = new MemoryCache(
            new MemoryCacheOptions { SizeLimit = Capacity, TrackStatistics = Statistics }
        );
        _bitFaster = new ConcurrentLfuBuilder<int, int>().WithCapacity(Capacity).Build();
        var builder = CacheBuilder.Create<int, int>().MaximumSize(Capacity).MaxConcurrentLoads(16);
        if (Statistics)
        {
            builder.RecordStatistics();
        }

        _manual = builder.Build();
        _loading = builder.BuildAsyncLoading(static (key, _) => Task.FromResult(key));
        _waiter = new CancellationTokenSource();
        _lru.Capacity = Capacity;
        for (int key = 0; key < Capacity; key++)
        {
            _dictionary[key] = key;
            _lru.Put(key, key);
            _memory.Set(key, key, new MemoryCacheEntryOptions { Size = 1 });
            _bitFaster.AddOrUpdate(key, key);
            _manual.Put(key, key);
            _loading.Set(key, key);
        }

        _manual.CleanUp();
        _loading.CleanUp();
        if (!_manual.TryGet(0, out _) || !_loading.TryGet(0, out _))
        {
            throw new InvalidOperationException(
                "The benchmark key must be resident before timing."
            );
        }
    }

    /// <summary>Releases owned objects after a benchmark case.</summary>
    [GlobalCleanup]
    public Task Cleanup() => DisposeAsync().AsTask();

    /// <summary>Releases benchmark resources, including asynchronous cache lifetime state.</summary>
    public async ValueTask DisposeAsync()
    {
        await _loading.DisposeAsync().ConfigureAwait(false);
        _manual.Dispose();
        _memory.Dispose();
        _waiter.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Measures the unbounded dictionary lower-bound reference.</summary>
    [Benchmark(Baseline = true)]
    public int Dictionary() => _dictionary[0];

    /// <summary>Measures a locked LRU reference.</summary>
    [Benchmark]
    public int LockedLru() => _lru.Read(0);

    /// <summary>Measures an IMemoryCache resident read.</summary>
    [Benchmark]
    public int MemoryCache() => _memory.Get<int>(0);

    /// <summary>Measures a BitFaster resident read.</summary>
    [Benchmark]
    public int BitFaster() => _bitFaster.TryGet(0, out int value) ? value : -1;

    /// <summary>Measures this engine's synchronous resident read.</summary>
    [Benchmark]
    public int Manual() => _manual.TryGet(0, out int value) ? value : -1;

    /// <summary>Measures the async loading facade's ready fast path.</summary>
    [Benchmark]
    public ValueTask<int> AsyncLoading() => _loading.GetAsync(0);

    /// <summary>Measures an uncanceled but cancelable waiter token on a hit.</summary>
    [Benchmark]
    public ValueTask<int> AsyncLoadingCancelable() => _loading.GetAsync(0, _waiter.Token);
}

internal sealed class LockedLru
{
    private readonly object _gate = new();
    private readonly Dictionary<int, LinkedListNode<KeyValuePair<int, int>>> _map = new();
    private readonly LinkedList<KeyValuePair<int, int>> _order = new();

    internal int Capacity { get; set; }

    internal void Put(int key, int value)
    {
        lock (_gate)
        {
            if (_map.Remove(key, out var old))
            {
                _order.Remove(old);
            }

            _map[key] = _order.AddLast(new KeyValuePair<int, int>(key, value));
            if (_map.Count <= Capacity)
            {
                return;
            }

            var victim = _order.First!;
            _order.RemoveFirst();
            _map.Remove(victim.Value.Key);
        }
    }

    internal int Read(int key)
    {
        lock (_gate)
        {
            var node = _map[key];
            _order.Remove(node);
            _order.AddLast(node);
            return node.Value.Value;
        }
    }
}
