using BenchmarkDotNet.Attributes;
using JetBrains.Annotations;

namespace LoadingCache.Benchmarks;

/// <summary>Compares strong and weak resident reads with caller-owned live targets.</summary>
[MemoryDiagnoser]
public class ReferenceLookupBenchmarks : IDisposable
{
    private readonly object _key = new();
    private readonly byte[] _value = [42];
    private ICache<object, byte[]> _cache = null!;

    /// <summary>Gets or sets whether keys are held weakly.</summary>
    [UsedImplicitly]
    [Params(false, true)]
    public bool WeakKeys { get; set; }

    /// <summary>Gets or sets whether values are held weakly.</summary>
    [UsedImplicitly]
    [Params(false, true)]
    public bool WeakValues { get; set; }

    /// <summary>Gets or sets whether striped operation counters are enabled.</summary>
    [UsedImplicitly]
    [Params(false, true)]
    public bool Statistics { get; set; }

    /// <summary>Creates the cache and live caller roots outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var builder = CacheBuilder.Create<object, byte[]>().MaximumSize(64).MaxConcurrentLoads(4);
        if (WeakKeys)
            builder.WeakKeys();
        if (WeakValues)
            builder.WeakValues();
        if (Statistics)
            builder.RecordStatistics();
        _cache = builder.Build();
        _cache.Put(_key, _value);
        _cache.CleanUp();
    }

    /// <summary>Consumes one live value; it excludes GC-driven collection and cold loads.</summary>
    [Benchmark]
    public byte ResidentHit() => _cache.TryGet(_key, out byte[]? value) ? value[0] : (byte)0;

    /// <summary>Closes the cache after measurement.</summary>
    [GlobalCleanup]
    public void Dispose()
    {
        _cache.Dispose();
        GC.KeepAlive(_key);
        GC.KeepAlive(_value);
        GC.SuppressFinalize(this);
    }
}
