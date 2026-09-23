using System.Globalization;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class MemoryPressureTests
{
    [Test]
    public async Task PolicyIsDisabledByDefault()
    {
        var source = new TestMemoryPressureSource(1);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .MemoryPressureSource(source)
            .Build();
        cache.Put(1, "one");
        await Assert.That(source.Calls).IsEqualTo(0);
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That((cache.Policy.MemoryPressureStatistics) is null).IsTrue();
    }

    [Test]
    public async Task HighPressureTrimsBoundedPolicyColdestEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new TestMemoryPressureSource(1);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(
                TimeSpan.FromSeconds(1),
                pressureThreshold: 0.8,
                trimFraction: 0.5,
                maximumTrimCount: 1
            )
            .Build();
        cache.Put(1, "one");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.Put(2, "two");
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction!.Coldest(1).Select(pair => pair.Key)).Contains(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("two");
        await Assert.That(source.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task PressureTrimHonorsTheConfiguredMaximumTrimCount()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new TestMemoryPressureSource(1);
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(64)
            .MaxConcurrentLoads(1)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), trimFraction: 1, maximumTrimCount: 2)
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 16; key++)
        {
            cache.Put(key, key.ToString(CultureInfo.InvariantCulture));
        }

        cache.CleanUp();
        engine.SampleMemoryPressureForTesting();
        await Assert.That(cache.EstimatedCount).IsEqualTo(14);
        MemoryPressureStatistics? diagnostics = cache.Policy.MemoryPressureStatistics;
        Assert.NotNull(diagnostics);
        await Assert.That(diagnostics.Value.Samples).IsEqualTo(1);
        await Assert.That(diagnostics.Value.PressureSamples).IsEqualTo(1);
        await Assert.That(diagnostics.Value.EvictedEntries).IsEqualTo(2);
    }

    [Test]
    public async Task PressureBelowThresholdDoesNotTrim()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new TestMemoryPressureSource(0.5);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), pressureThreshold: 0.8)
            .Build();
        cache.Put(1, "one");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("one");
    }

    [Test]
    public async Task MemoryPressureOptionsValidateAtBuilderBoundary()
    {
        Action invalidInterval = () =>
            CacheBuilder.Create<int, string>().MemoryPressureEviction(TimeSpan.Zero);
        Action invalidThreshold = () =>
            CacheBuilder
                .Create<int, string>()
                .MemoryPressureEviction(TimeSpan.FromSeconds(1), pressureThreshold: double.NaN);
        Action invalidFraction = () =>
            CacheBuilder
                .Create<int, string>()
                .MemoryPressureEviction(TimeSpan.FromSeconds(1), trimFraction: 0);
        Action invalidCount = () =>
            CacheBuilder
                .Create<int, string>()
                .MemoryPressureEviction(TimeSpan.FromSeconds(1), maximumTrimCount: 0);
        await Assert.That(invalidInterval).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(invalidThreshold).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(invalidFraction).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(invalidCount).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FailedPressureTimerConstructionDisposesAnAlreadyCreatedExpirationTimer()
    {
        using var timeProvider = new ThrowingSecondTimerProvider();
        Action build = () =>
        {
            CacheBuilder
                .Create<int, string>()
                .MaximumSize(8)
                .MaxConcurrentLoads(1)
                .TimeProvider(timeProvider)
                .ExpireAfterWrite(TimeSpan.FromMinutes(1))
                .EnableExpirationScheduler()
                .MemoryPressureEviction(TimeSpan.FromSeconds(1))
                .Build();
        };
        await Assert.That(build).ThrowsExactly<InvalidOperationException>();
        await Assert.That(timeProvider.DisposedTimerCount).IsEqualTo(1);
    }

    [Test]
    public async Task SamplingErrorsAreObservedWithoutDamagingCacheState()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new TestMemoryPressureSource(new InvalidOperationException("sample failed"));
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "one");
        engine.SampleMemoryPressureForTesting();
        MemoryPressureStatistics? diagnostics = cache.Policy.MemoryPressureStatistics;
        Assert.NotNull(diagnostics);
        await Assert.That(diagnostics.Value.SamplingErrors).IsEqualTo(1);
        await Assert
            .That<object>(diagnostics.Value.LastSamplingError!)
            .IsTypeOf<InvalidOperationException>();
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("one");
    }

    [Test]
    public async Task ClearDuringProviderCallFencesThePressureSample()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var release = NewSignal();
        var source = new TestMemoryPressureSource(() =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return new MemoryPressureSample(1);
        });
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(0.01), maximumTrimCount: 8)
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        Task sample = Task.Run(engine.SampleMemoryPressureForTesting);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Clear();
        cache.Put(2, "new");
        release.TrySetResult(true);
        await sample.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("new");
        await Assert.That(cache.Policy.MemoryPressureStatistics!.Value.EvictedEntries).IsEqualTo(0);
    }

    [Test]
    public async Task SetDuringProviderCallFencesTheCapturedValueVersion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var release = NewSignal();
        var source = new TestMemoryPressureSource(() =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return new MemoryPressureSample(1);
        });
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), maximumTrimCount: 8)
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        Task sample = Task.Run(engine.SampleMemoryPressureForTesting);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Put(1, "new");
        release.TrySetResult(true);
        await sample.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("new");
        await Assert.That(cache.Policy.MemoryPressureStatistics!.Value.EvictedEntries).IsEqualTo(0);
    }

    [Test]
    public async Task RefreshPublicationFencesTheCapturedValueVersion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var refreshEntered = NewSignal();
        var releaseRefresh = NewSignal();
        int calls = 0;
        var sourceEntered = NewSignal();
        var releaseSource = NewSignal();
        var source = new TestMemoryPressureSource(() =>
        {
            sourceEntered.TrySetResult(true);
            releaseSource.Task.GetAwaiter().GetResult();
            return new MemoryPressureSample(1);
        });
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), maximumTrimCount: 8)
            .CreateEngine();
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    return "v1";
                }

                refreshEntered.TrySetResult(true);
                await releaseRefresh.Task.ConfigureAwait(false);
                return "v2";
            }
        );
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("v1");
        Task<string> refresh = cache.RefreshAsync(1).AsTask();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task sample = Task.Run(engine.SampleMemoryPressureForTesting);
        await sourceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseRefresh.TrySetResult(true);
        await Assert.That((await refresh.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo("v2");
        releaseSource.TrySetResult(true);
        await sample.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("v2");
        await Assert.That(cache.Policy.MemoryPressureStatistics!.Value.EvictedEntries).IsEqualTo(0);
    }

    [Test]
    public async Task ProviderRunsOutsideCacheGateAndMayReenter()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new ReentrantMemoryPressureSource();
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .CreateEngine();
        using var typedCache = new Cache<int, string>(engine);
        source.Cache = typedCache;
        await Task.Run(engine.SampleMemoryPressureForTesting).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(typedCache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("reentrant");
    }

    [Test]
    public async Task OverlappingSamplesDoNotOverlapProviderCalls()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var release = NewSignal();
        var source = new TestMemoryPressureSource(() =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return new MemoryPressureSample(0);
        });
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "one");
        Task first = Task.Run(engine.SampleMemoryPressureForTesting);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = Task.Run(engine.SampleMemoryPressureForTesting);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(source.Calls).IsEqualTo(1);
        release.TrySetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Policy.MemoryPressureStatistics!.Value.Samples).IsEqualTo(1);
    }

    [Test]
    public async Task SamplingTimerDoesNotFlowExecutionContext()
    {
        var context = new AsyncLocal<string?>();
        var observed = NewSignal();
        string? observedValue = null;
        var source = new TestMemoryPressureSource(() =>
        {
            observedValue = context.Value;
            observed.TrySetResult(true);
            return new MemoryPressureSample(0);
        });
        using var timeProvider = new ContextCapturingTimeProvider();
        context.Value = "builder-context";
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .TimeProvider(timeProvider)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .Build();
        cache.Put(1, "one");
        context.Value = "caller-context";
        await timeProvider.FireTimerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That((observedValue) is null).IsTrue();
    }

    [Test]
    public async Task PendingLoadIsNotEvictedOrCountedAsResidentTrim()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var release = NewSignal();
        var source = new TestMemoryPressureSource(1);
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .CreateEngine();
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            async (_, _) =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return "loaded";
            }
        );
        Task<string> load = cache.GetAsync(1).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(1);
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        release.TrySetResult(true);
        await Assert.That((await load.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo("loaded");
    }

    [Test]
    public async Task WeightedZeroEntriesRemainSubjectToPressureTrim()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumWeight(8)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(1)
            .TimeProvider(clock)
            .Weigher((_, _) => 0)
            .MemoryPressureSource(new TestMemoryPressureSource(1))
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), trimFraction: 0.5)
            .CreateEngine();
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "one");
        cache.Put(2, "two");
        engine.SampleMemoryPressureForTesting();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(0);
    }

    [Test]
    public async Task DisposingCacheStopsFutureSamples()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var source = new TestMemoryPressureSource(1);
        ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .MemoryPressureSource(source)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1))
            .Build();
        cache.Put(1, "one");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(source.Calls).IsEqualTo(1);
        cache.Dispose();
        clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.That(source.Calls).IsEqualTo(1);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ReentrantMemoryPressureSource : IMemoryPressureSource
    {
        internal Cache<int, string> Cache { private get; set; } = null!;

        public MemoryPressureSample GetSample()
        {
            Cache.Put(2, "reentrant");
            return new MemoryPressureSample(0);
        }
    }

    private sealed class TestMemoryPressureSource : IMemoryPressureSource
    {
        private readonly Func<MemoryPressureSample> _sample;
        private int _calls;

        internal TestMemoryPressureSource(double loadRatio)
            : this(() => new MemoryPressureSample(loadRatio)) { }

        internal TestMemoryPressureSource(Exception exception)
            : this(() => throw exception) { }

        internal TestMemoryPressureSource(Func<MemoryPressureSample> sample)
        {
            _sample = sample;
        }

        internal int Calls => Volatile.Read(ref _calls);

        public MemoryPressureSample GetSample()
        {
            Interlocked.Increment(ref _calls);
            return _sample();
        }
    }

    private sealed class ContextCapturingTimeProvider : TimeProvider, IDisposable
    {
        private readonly TimeProvider _system = System;
        private ContextCapturingTimer? _timer;

        public override DateTimeOffset GetUtcNow() => _system.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => _system.LocalTimeZone;
        public override long TimestampFrequency => _system.TimestampFrequency;

        public override long GetTimestamp() => _system.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _timer = new ContextCapturingTimer(callback, state);
            return _timer;
        }

        internal Task<bool> FireTimerAsync() =>
            _timer?.FireAsync()
            ?? Task.FromException<bool>(new InvalidOperationException("Timer was not created."));

        public void Dispose() => _timer?.Dispose();

        private sealed class ContextCapturingTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private readonly ExecutionContext? _context;

            internal ContextCapturingTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
                _context = ExecutionContext.Capture();
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            internal Task<bool> FireAsync()
            {
                var completion = NewSignal();
                var invocation = new Invocation(this, completion);
                ThreadPool.UnsafeQueueUserWorkItem(
                    static value => value.Run(),
                    invocation,
                    preferLocal: false
                );
                return completion.Task;
            }

            private sealed class Invocation
            {
                private readonly ContextCapturingTimer _timer;
                private readonly TaskCompletionSource<bool> _completion;

                internal Invocation(
                    ContextCapturingTimer timer,
                    TaskCompletionSource<bool> completion
                )
                {
                    _timer = timer;
                    _completion = completion;
                }

                internal void Run()
                {
                    try
                    {
                        if (_timer._context is null)
                        {
                            _timer._callback(_timer._state);
                        }
                        else
                        {
                            ExecutionContext.Run(
                                _timer._context,
                                static state =>
                                {
                                    var invocation = (Invocation)state!;
                                    invocation._timer._callback(invocation._timer._state);
                                },
                                this
                            );
                        }

                        _completion.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        _completion.TrySetException(exception);
                    }
                }
            }
        }
    }

    private sealed class ThrowingSecondTimerProvider : TimeProvider, IDisposable
    {
        private readonly TimeProvider _system = System;
        private int _createCount;
        private int _disposedTimerCount;
        internal int DisposedTimerCount => Volatile.Read(ref _disposedTimerCount);

        public override DateTimeOffset GetUtcNow() => _system.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => _system.LocalTimeZone;
        public override long TimestampFrequency => _system.TimestampFrequency;

        public override long GetTimestamp() => _system.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            return Interlocked.Increment(ref _createCount) == 2
                ? throw new InvalidOperationException("The second timer is rejected.")
                : new TrackingTimer(() => Interlocked.Increment(ref _disposedTimerCount));
        }

        public void Dispose() { }

        private sealed class TrackingTimer(Action onDispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => onDispose();

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
