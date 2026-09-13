using LoadingCache.Diagnostics;

namespace LoadingCache;

internal readonly record struct MemoryPressureDiagnostics(
    long Samples,
    long PressureSamples,
    long EvictedEntries,
    long SamplingErrors,
    Exception? LastSamplingError
);

internal readonly record struct MemoryPressureCandidate(object Entry, long PublicationRevision);

internal sealed class MemoryPressureSnapshot(
    long epoch,
    IReadOnlyList<MemoryPressureCandidate> candidates
)
{
    internal long Epoch { get; } = epoch;

    internal IReadOnlyList<MemoryPressureCandidate> Candidates { get; } = candidates;
}

internal sealed class MemoryPressureController<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : notnull
{
    private readonly CacheEngine<TKey, TValue> _owner;
    private readonly double _pressureThreshold;
    private readonly double _trimFraction;
    private readonly int _maximumTrimCount;
    private readonly IMemoryPressureSource _source;
    private readonly ITimer _timer;
    private int _sampleRunning;
    private int _disposed;
    private long _samples;
    private long _pressureSamples;
    private long _evictedEntries;
    private long _samplingErrors;
    private Exception? _lastSamplingError;

    internal MemoryPressureController(
        CacheEngine<TKey, TValue> owner,
        TimeProvider timeProvider,
        TimeSpan samplingInterval,
        double pressureThreshold,
        double trimFraction,
        int maximumTrimCount,
        IMemoryPressureSource source
    )
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(source);
        ValidateOptions(samplingInterval, pressureThreshold, trimFraction, maximumTrimCount);
        _owner = owner;
        _pressureThreshold = pressureThreshold;
        _trimFraction = trimFraction;
        _maximumTrimCount = maximumTrimCount;
        _source = source;

        if (ExecutionContext.IsFlowSuppressed())
        {
            _timer = timeProvider.CreateTimer(
                static state => ((MemoryPressureController<TKey, TValue>)state!).Sample(),
                this,
                samplingInterval,
                samplingInterval
            );
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _timer = timeProvider.CreateTimer(
                    static state => ((MemoryPressureController<TKey, TValue>)state!).Sample(),
                    this,
                    samplingInterval,
                    samplingInterval
                );
            }
        }
    }

    internal MemoryPressureDiagnostics GetDiagnostics() =>
        new(
            Interlocked.Read(ref _samples),
            Interlocked.Read(ref _pressureSamples),
            Interlocked.Read(ref _evictedEntries),
            Interlocked.Read(ref _samplingErrors),
            Volatile.Read(ref _lastSamplingError)
        );

    internal void SampleForTesting() => Sample();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // ITimer.Dispose() does not wait for a callback that is already in the
        // source. The callback checks both controller and engine state before
        // publishing any eviction, so shutdown never waits for user code.
        _timer.Dispose();
    }

    private void Sample()
    {
        if (
            Volatile.Read(ref _disposed) != 0
            || !_owner.IsMemoryPressureEpochCurrent(_owner.GetMemoryPressureEpoch())
            || Interlocked.CompareExchange(ref _sampleRunning, 1, 0) != 0
        )
        {
            return;
        }

        try
        {
            Interlocked.Increment(ref _samples);
            MemoryPressureSnapshot? snapshot = _owner.CaptureMemoryPressureSnapshot(
                _trimFraction,
                _maximumTrimCount
            );
            if (snapshot is null)
            {
                return;
            }

            MemoryPressureSample sample = _source.GetSample();

            // The provider is deliberately outside the engine lock. A source
            // may call unrelated cache APIs, and Clear/Set may advance the
            // epoch while a slow provider is producing its sample.
            if (
                Volatile.Read(ref _disposed) != 0
                || !_owner.IsMemoryPressureEpochCurrent(snapshot.Epoch)
                || sample.LoadRatio < _pressureThreshold
            )
            {
                return;
            }

            Interlocked.Increment(ref _pressureSamples);
            int evicted = _owner.TrimForMemoryPressure(snapshot);
            if (evicted != 0)
            {
                Interlocked.Add(ref _evictedEntries, evicted);
            }
        }
        catch (Exception exception)
        {
            // A sampling provider is an observation hook. Its failure must be
            // visible through diagnostics without damaging cache state or
            // escaping from an ITimer callback.
            Interlocked.Increment(ref _samplingErrors);
            Interlocked.Exchange(ref _lastSamplingError, exception);
        }
        finally
        {
            Volatile.Write(ref _sampleRunning, 0);
        }
    }

    internal static void ValidateOptions(
        TimeSpan samplingInterval,
        double pressureThreshold,
        double trimFraction,
        int maximumTrimCount
    )
    {
        if (
            samplingInterval <= TimeSpan.Zero
            || samplingInterval == Timeout.InfiniteTimeSpan
            || samplingInterval > TimeSpan.FromMilliseconds(uint.MaxValue - 1d)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(samplingInterval));
        }

        if (!double.IsFinite(pressureThreshold) || pressureThreshold <= 0 || pressureThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pressureThreshold));
        }

        if (!double.IsFinite(trimFraction) || trimFraction <= 0 || trimFraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(trimFraction));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTrimCount);
    }
}

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal long GetMemoryPressureEpoch() => Volatile.Read(ref _epoch);

    internal bool IsMemoryPressureEpochCurrent(long sampledEpoch) =>
        Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _epoch) == sampledEpoch;

    internal MemoryPressureDiagnostics GetMemoryPressureDiagnostics() =>
        _memoryPressureController?.GetDiagnostics() ?? default;

    private MemoryPressureStatistics? GetPublicMemoryPressureStatistics()
    {
        ThrowIfDisposed();
        if (_memoryPressureController is null)
            return null;
        MemoryPressureDiagnostics snapshot = _memoryPressureController.GetDiagnostics();
        return new MemoryPressureStatistics(
            snapshot.Samples,
            snapshot.PressureSamples,
            snapshot.EvictedEntries,
            snapshot.SamplingErrors,
            snapshot.LastSamplingError
        );
    }

    internal void SampleMemoryPressureForTesting() => _memoryPressureController?.SampleForTesting();

    internal MemoryPressureSnapshot? CaptureMemoryPressureSnapshot(
        double trimFraction,
        int maximumTrimCount
    )
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return null;
            }

            var policy = _policy;
            int residentCount = policy.ResidentCount;
            int trimCount = Math.Min(
                (int)Math.Ceiling(residentCount * trimFraction),
                maximumTrimCount
            );
            var selected = new List<MemoryPressureCandidate>(trimCount);
            foreach (var candidate in policy.Snapshot(hottest: false, trimCount))
            {
                if (
                    candidate is not Entry entry
                    || entry.Epoch != _epoch
                    || !_entries.IsCurrent(entry)
                )
                    continue;
                lock (entry.Sync)
                {
                    if (
                        Volatile.Read(ref entry.IsReady)
                        && !entry.PolicyDetached
                        && entry.TryGetKey(out _)
                        && entry.TryGetValue(out _)
                    )
                        selected.Add(new MemoryPressureCandidate(entry, entry.PublicationRevision));
                }
            }
            return new MemoryPressureSnapshot(_epoch, selected);
        }
    }

    internal int TrimForMemoryPressure(MemoryPressureSnapshot snapshot)
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        int removed = 0;
        lock (_gate)
        {
            if (_disposed != 0 || _epoch != snapshot.Epoch)
            {
                return 0;
            }

            IReadOnlyList<MemoryPressureCandidate> candidates = snapshot.Candidates;
            if (candidates.Count == 0)
            {
                return 0;
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                MemoryPressureCandidate candidate = candidates[index];
                if (candidate.Entry is not Entry candidateEntry)
                {
                    continue;
                }

                Entry current = candidateEntry;

                if (
                    _disposed != 0
                    || _epoch != snapshot.Epoch
                    || !_entries.IsCurrent(candidateEntry)
                    || current.Epoch != snapshot.Epoch
                    || !Volatile.Read(ref current.IsReady)
                    || current.PublicationRevision != candidate.PublicationRevision
                )
                {
                    continue;
                }

                RemoveCurrentEntryLocked(current, RemovalCause.MemoryPressure);
                RecordCounter(CacheCounterKind.Evictions);
                RecordCounter(CacheCounterKind.EvictedWeight, current.Weight);

                removed++;
            }
        }

        evictionScope.Dispatch();
        return removed;
    }
}
