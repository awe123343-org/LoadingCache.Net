namespace LoadingCache;

/// <summary>A non-transactional snapshot of the optional memory-pressure monitor.</summary>
/// <param name="Samples">The number of sampling attempts.</param>
/// <param name="PressureSamples">The number of valid samples at or above the threshold.</param>
/// <param name="EvictedEntries">The number of resident versions evicted by pressure.</param>
/// <param name="SamplingErrors">The number of observed sampling or trim failures.</param>
/// <param name="LastSamplingError">The last observed failure, or null.</param>
public readonly record struct MemoryPressureStatistics(
    long Samples,
    long PressureSamples,
    long EvictedEntries,
    long SamplingErrors,
    Exception? LastSamplingError
);

/// <summary>Represents a normalized snapshot of the current GC memory load.</summary>
public readonly record struct MemoryPressureSample
{
    /// <summary>Initializes a memory pressure sample.</summary>
    /// <param name="loadRatio">The memory load divided by the GC high-load threshold.</param>
    public MemoryPressureSample(double loadRatio)
    {
        if (!double.IsFinite(loadRatio) || loadRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(loadRatio));
        }

        LoadRatio = loadRatio;
    }

    /// <summary>
    /// Gets the normalized memory load. A value of <c>1.0</c> represents the
    /// GC high-memory-load threshold.
    /// </summary>
    public double LoadRatio { get; }
}

/// <summary>Supplies memory pressure samples to an opt-in cache policy.</summary>
public interface IMemoryPressureSource
{
    /// <summary>Gets a snapshot without forcing a collection.</summary>
    MemoryPressureSample GetSample();
}

/// <summary>
/// Uses the most recent CLR GC memory information as a pressure signal.
/// </summary>
/// <remarks>
/// The CLR reports information from the most recent collection. This source
/// does not call <see cref="GC.Collect()"/> and does not represent process RSS
/// or a guarantee that an out-of-memory condition will be avoided.
/// </remarks>
public sealed class GcMemoryPressureSource : IMemoryPressureSource
{
    /// <summary>Gets the stateless shared source instance.</summary>
    public static GcMemoryPressureSource Instance { get; } = new();

    /// <inheritdoc />
    public MemoryPressureSample GetSample()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.MemoryLoadBytes <= 0 || info.HighMemoryLoadThresholdBytes <= 0)
        {
            return new MemoryPressureSample(0);
        }

        return new MemoryPressureSample(
            (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes
        );
    }
}
