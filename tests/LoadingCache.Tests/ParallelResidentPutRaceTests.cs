namespace LoadingCache.Tests;

public sealed class ParallelResidentPutRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
        await Assert.That(cache.TryGetTask(1, out Task<string>? original)).IsTrue();
        Assert.NotNull(original);
        Task<string> refresh = StartRefresh(cache);
        Task<string>? refreshed;
        Task<string>? replacement;
        try
        {
            await published.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.TryGet(1, out string? ready)).IsTrue();
            await Assert.That(ready).IsEqualTo("v2");
            await Assert.That(cache.TryGetTask(1, out refreshed)).IsTrue();
            await Assert.That((await refreshed!)).IsEqualTo("v2");
            cache.Set(1, "v3");
            probe.AssertSingleFastCommit();
            await Assert.That(cache.TryGetTask(1, out replacement)).IsTrue();
            Assert.NotNull(replacement);
            await Assert.That((await replacement!)).IsEqualTo("v3");
        }
        finally
        {
            published.Release();
            await ExpectPublicationFailure(refresh);
        }

        await Assert.That(published.TimedOut).IsFalse();
        await Assert.That(failure.Calls).IsEqualTo(1);
        await Assert.That((await original!)).IsEqualTo("v1");
        await Assert.That((await refreshed)).IsEqualTo("v2");
        await Assert.That(cache.TryGetTask(1, out Task<string>? current)).IsTrue();
        Assert.NotNull(current);
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
        await Assert.That((await current)).IsEqualTo("v3");
        AssertResident(engine, "v3", statistics);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(cache.TryGet(1, out string? ready)).IsTrue();
            await Assert.That(ready).IsEqualTo("v1");
            await Assert.That(cache.TryGetTask(1, out original)).IsTrue();
            await Assert.That(original!.IsCompleted).IsFalse();
            cache.Set(1, "v2");
            probe.AssertSingleFastCommit();
            await Assert.That(cache.TryGetTask(1, out replacement)).IsTrue();
            Assert.NotNull(replacement);
            await Assert.That(ReferenceEquals(replacement, original)).IsFalse();
            await Assert.That((await replacement)).IsEqualTo("v2");
        }
        finally
        {
            completion.Release();
            await ExpectPublicationFailure(load);
        }

        await Assert.That(completion.TimedOut).IsFalse();
        await Assert.That(failure.Calls).IsEqualTo(1);
        await ExpectPublicationFailure(original);
        await Assert.That(cache.TryGetTask(1, out Task<string>? current)).IsTrue();
        Assert.NotNull(current);
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
        await Assert.That((await current)).IsEqualTo("v2");
        AssertResident(engine, "v2", statistics);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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

        await Assert.That(comparison.TimedOut).IsFalse();
        await Assert.That(original.ComparisonCalls).IsEqualTo(1);
        await Assert.That((await update)).IsFalse();
        await Assert.That(cache.TryGet(1, out ComparedValue? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(statistics ? 1 : 0);
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
        await Assert
            .That((Func<Task>)(() => task.WaitAsync(Watchdog)))
            .ThrowsExactly<ControlledPublicationFailure>();

    private static void AssertResident(
        CacheEngine<int, string> engine,
        string value,
        bool statistics
    )
    {
        if (!(engine.TryGet(1, out string? current)))
            Assert.Fail("Expected engine.TryGet(1, out string? current) to be true ().");
        if ((current) != (value))
            Assert.Fail("Expected current to equal (value).");
        engine.CleanUp();
        if ((engine.EstimatedCount) != (1))
            Assert.Fail("Expected engine.EstimatedCount to equal (1).");
        if ((engine.Policy.Eviction!.WeightedSize) != (1))
            Assert.Fail("Expected engine.Policy.Eviction!.WeightedSize to equal (1).");
        if ((engine.GetStatistics().InFlightLoads) != (0))
            Assert.Fail("Expected engine.GetStatistics().InFlightLoads to equal (0).");
        if ((engine.GetStatistics().ReplacedRemovals) != (statistics ? 1 : 0))
            Assert.Fail(
                "Expected engine.GetStatistics().ReplacedRemovals to equal (statistics ? 1 : 0)."
            );
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
            if ((Volatile.Read(ref _calls)) != (1))
                Assert.Fail("Expected Volatile.Read(ref _calls) to equal (1).");
            if ((Volatile.Read(ref _coordinatedCalls)) != (0))
                Assert.Fail("Expected Volatile.Read(ref _coordinatedCalls) to equal (0).");
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
