using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ParallelResidentPutRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task FastPutSurvivesAnOlderCommittedRefreshRollback(bool statistics)
    {
        await using var published = new BlockingTestHook(Watchdog);
        var failure = new PublicationFailure(published);
        var probe = new CommitProbe<string>();
        CacheEngine<int, string> engine = CreateEngine(
            probe,
            statistics,
            new LoadingCacheTestHooks
            {
                AfterRefreshPublished = failure.Invoke,
                BeforeResidentValuePublished = probe.Observe,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("v2")
        );
        cache.Set(1, "v1");
        cache.CleanUp();
        cache.TryGetTask(1, out Task<string>? original).Should().BeTrue();
        Task<string> refresh = StartRefresh(cache);
        Task<string>? refreshed;
        Task<string>? replacement;
        try
        {
            await published.Entered.WaitAsync(Watchdog);
            cache.TryGet(1, out string? ready).Should().BeTrue();
            ready.Should().Be("v2");
            cache.TryGetTask(1, out refreshed).Should().BeTrue();
            (await refreshed!).Should().Be("v2");

            cache.Set(1, "v3");
            probe.AssertSingleFastCommit();
            cache.TryGetTask(1, out replacement).Should().BeTrue();
            (await replacement!).Should().Be("v3");
        }
        finally
        {
            published.Release();
            await ExpectPublicationFailure(refresh);
        }

        published.TimedOut.Should().BeFalse();
        failure.Calls.Should().Be(1);
        (await original!).Should().Be("v1");
        (await refreshed).Should().Be("v2");
        cache.TryGetTask(1, out Task<string>? current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
        (await current).Should().Be("v3");
        AssertResident(engine, "v3", statistics);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task FastPutTaskViewSurvivesACommittedColdPromiseFailure(bool statistics)
    {
        await using var completion = new BlockingTestHook(Watchdog);
        var failure = new PublicationFailure(completion);
        var probe = new CommitProbe<string>();
        CacheEngine<int, string> engine = CreateEngine(
            probe,
            statistics,
            new LoadingCacheTestHooks
            {
                BeforeCompletion = failure.Invoke,
                BeforeResidentValuePublished = probe.Observe,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("v1")
        );
        Task<string> load = StartLoad(cache);
        Task<string>? original;
        Task<string>? replacement;
        try
        {
            await completion.Entered.WaitAsync(Watchdog);
            cache.TryGet(1, out string? ready).Should().BeTrue();
            ready.Should().Be("v1");
            cache.TryGetTask(1, out original).Should().BeTrue();
            original!.IsCompleted.Should().BeFalse();

            cache.Set(1, "v2");
            probe.AssertSingleFastCommit();
            cache.TryGetTask(1, out replacement).Should().BeTrue();
            replacement.Should().NotBeSameAs(original);
            (await replacement).Should().Be("v2");
        }
        finally
        {
            completion.Release();
            await ExpectPublicationFailure(load);
        }

        completion.TimedOut.Should().BeFalse();
        failure.Calls.Should().Be(1);
        await ExpectPublicationFailure(original);
        cache.TryGetTask(1, out Task<string>? current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
        (await current).Should().Be("v2");
        AssertResident(engine, "v2", statistics);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task ConditionalUpdateRejectsARevisionChangedDuringValueComparison(bool statistics)
    {
        await using var comparison = new BlockingTestHook(Watchdog);
        var probe = new CommitProbe<ComparedValue>();
        CacheEngine<int, ComparedValue> engine = CreateEngine(
            probe,
            statistics,
            new LoadingCacheTestHooks { BeforeResidentValuePublished = probe.Observe }
        );
        using var cache = new Cache<int, ComparedValue>(engine);
        var original = new ComparedValue(1, comparison);
        var replacement = new ComparedValue(3);
        cache.Put(1, original);
        cache.CleanUp();

        Task<bool> update = StartConditionalUpdate(cache.AsDictionary());
        try
        {
            await comparison.Entered.WaitAsync(Watchdog);
            cache.Put(1, replacement);
            probe.AssertSingleFastCommit();
        }
        finally
        {
            comparison.Release();
            await update.WaitAsync(Watchdog);
        }

        comparison.TimedOut.Should().BeFalse();
        original.ComparisonCalls.Should().Be(1);
        (await update).Should().BeFalse();
        cache.TryGet(1, out ComparedValue? current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.Statistics.ReplacedRemovals.Should().Be(statistics ? 1 : 0);
        engine.AssertInvariants();
    }

    private static CacheEngine<int, TValue> CreateEngine<TValue>(
        CommitProbe<TValue> probe,
        bool statistics,
        LoadingCacheTestHooks hooks
    )
        where TValue : notnull
    {
        var engine = new CacheEngine<int, TValue>(
            new CacheEngineOptions<int, TValue>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                SupportsBulkLoading = false,
                RecordStatistics = statistics,
                TestHooks = hooks,
            }
        );
        probe.Engine = engine;
        return engine;
    }

    private static Task<string> StartRefresh(AsyncLoadingCache<int, string> cache) =>
        Task
            .Factory.StartNew(
                () => cache.RefreshAsync(1).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();

    private static Task<string> StartLoad(AsyncLoadingCache<int, string> cache) =>
        Task
            .Factory.StartNew(
                () => cache.GetAsync(1).AsTask(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();

    private static Task<bool> StartConditionalUpdate(
        SyncCacheDictionary<int, ComparedValue> dictionary
    ) =>
        Task.Factory.StartNew(
            () => dictionary.TryUpdate(1, new ComparedValue(2), new ComparedValue(1)),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

    private static async Task ExpectPublicationFailure(Task<string> task) =>
        await FluentActions
            .Awaiting(() => task.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<ControlledPublicationFailure>();

    private static void AssertResident(
        CacheEngine<int, string> engine,
        string value,
        bool statistics
    )
    {
        engine.TryGet(1, out string? current).Should().BeTrue();
        current.Should().Be(value);
        engine.CleanUp();
        engine.EstimatedCount.Should().Be(1);
        engine.Policy.Eviction!.WeightedSize.Should().Be(1);
        engine.GetStatistics().InFlightLoads.Should().Be(0);
        engine.GetStatistics().ReplacedRemovals.Should().Be(statistics ? 1 : 0);
        engine.AssertInvariants();
    }

    private sealed class CommitProbe<TValue>
        where TValue : notnull
    {
        private int _calls;
        private int _coordinatedCalls;
        internal CacheEngine<int, TValue> Engine { get; set; } = null!;

        internal void Observe()
        {
            Interlocked.Increment(ref _calls);
            if (Engine.IsCoordinationLockHeldForTesting)
                Interlocked.Increment(ref _coordinatedCalls);
        }

        internal void AssertSingleFastCommit()
        {
            Volatile.Read(ref _calls).Should().Be(1);
            Volatile.Read(ref _coordinatedCalls).Should().Be(0);
        }
    }

    private sealed class PublicationFailure(BlockingTestHook gate)
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        internal void Invoke()
        {
            Interlocked.Increment(ref _calls);
            gate.Invoke();
            throw new ControlledPublicationFailure();
        }
    }

    private sealed class ComparedValue(int version, BlockingTestHook? comparison = null)
        : IEquatable<ComparedValue>
    {
        private readonly int _version = version;
        private BlockingTestHook? _comparison = comparison;
        private int _comparisonCalls;
        internal int ComparisonCalls => Volatile.Read(ref _comparisonCalls);

        public bool Equals(ComparedValue? other)
        {
            Interlocked.Increment(ref _comparisonCalls);
            Interlocked.Exchange(ref _comparison, null)?.Invoke();
            return other is not null && _version == other._version;
        }

        public override bool Equals(object? obj) => Equals(obj as ComparedValue);

        public override int GetHashCode() => _version;
    }

    private sealed class ControlledPublicationFailure : Exception;
}
