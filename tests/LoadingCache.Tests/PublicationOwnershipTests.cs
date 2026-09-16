using System.Collections.Concurrent;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class PublicationOwnershipTests
{
    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public void FaultedReadyPublicationRequiresPhysicalRepair(bool dictionary)
    {
        var probe = new PublicationProbe(failFinalization: true);
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        if (dictionary)
        {
            cache
                .Invoking(static current => current.AsDictionary().TryAdd(1, "failed"))
                .Should()
                .ThrowExactly<ControlledPublicationFailure>();
        }
        else
        {
            cache
                .Invoking(static current => current.Put(1, "failed"))
                .Should()
                .ThrowExactly<ControlledPublicationFailure>();
        }

        probe.Stop();
        cache.Put(1, "repaired");
        probe
            .ResidentPublicationCalls.Should()
            .Be(0, "an entry with unfinished policy publication cannot take the resident shortcut");
        cache.CleanUp();
        AssertResident(cache, 1, "repaired");
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.Statistics.ReplacedRemovals.Should().Be(1);
        AssertResidentShortcut(engine, probe, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public void ReadyEntryRemainsOwnedThroughPolicyPublication(bool dictionary)
    {
        var probe = new PublicationProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        if (dictionary)
        {
            cache.AsDictionary().TryAdd(1, "published").Should().BeTrue();
        }
        else
        {
            cache.Put(1, "published");
        }

        probe.Stop();
        AssertResident(cache, 1, "published");
        cache.Statistics.ReplacedRemovals.Should().Be(0);
        probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 0);
        AssertResidentShortcut(engine, probe, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task ColdPublicationKeepsOwnershipUntilPolicyFinalization(bool asynchronous)
    {
        var probe = new PublicationProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => Task.FromResult("published")
            );
            (await cache.GetAsync(1)).Should().Be("published");
            probe.Stop();
            cache.TryGet(1, out string? value).Should().BeTrue();
            value.Should().Be("published");
            cache.TryGetTask(1, out Task<string>? task).Should().BeTrue();
            (await task!).Should().Be("published");
            engine.AssertInvariants();
            probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, static _ => "published");
            cache.Get(1).Should().Be("published");
            probe.Stop();
            AssertResident(cache, 1, "published");
            probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Parallelizable(ParallelScope.All)]
    public async Task ClaimedColdFailureOwnsItsRevisionCheckThroughRemoval(
        bool asynchronous,
        bool failBeforeReady
    )
    {
        var probe = new PublicationProbe(failBeforeReady, failFinalization: !failBeforeReady);
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => Task.FromResult("published")
            );
            await ExpectFailure(cache.GetAsync(1).AsTask());
            probe.Stop();
            cache.TryGet(1, out _).Should().BeFalse();
            cache.EstimatedCount.Should().Be(0);
            cache.Statistics.InFlightLoads.Should().Be(0);
            (await cache.GetAsync(1)).Should().Be("published");
            engine.AssertInvariants();
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, static _ => "published");
            cache
                .Invoking(static current => current.Get(1))
                .Should()
                .ThrowExactly<ControlledPublicationFailure>();
            probe.Stop();
            cache.TryGet(1, out _).Should().BeFalse();
            cache.EstimatedCount.Should().Be(0);
            cache.Statistics.InFlightLoads.Should().Be(0);
            cache.Get(1).Should().Be("published");
            AssertResident(cache, 1, "published");
        }

        probe.AssertOwnership(expectedCalls: failBeforeReady ? 1 : 2, expectedOutsideCalls: 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task BulkOwnedAndPrefetchedPublicationsRemainOwned(bool asynchronous)
    {
        var probe = new PublicationProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => throw new InvalidOperationException("Unexpected single load."),
                bulkLoader: static (_, _) =>
                    Task.FromResult<IReadOnlyDictionary<int, string>>(BulkValues())
            );
            IReadOnlyDictionary<int, string> result = await cache.GetAllAsync([1]);
            result
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(new KeyValuePair<int, string>(1, "one"));
            probe.Stop();
            cache.TryGet(2, out string? extra).Should().BeTrue();
            extra.Should().Be("two");
            cache.EstimatedCount.Should().Be(2);
            engine.AssertInvariants();
            probe.AssertOwnership(expectedCalls: 2, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
            AssertResidentShortcut(engine, probe, 2);
        }
        else
        {
            using var cache = new LoadingCache<int, string>(
                engine,
                static _ => throw new InvalidOperationException("Unexpected single load."),
                bulkLoader: static _ => BulkValues()
            );
            IReadOnlyDictionary<int, string> result = cache.GetAll([1]);
            result
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(new KeyValuePair<int, string>(1, "one"));
            probe.Stop();
            AssertResident(cache, 2, "two");
            cache.EstimatedCount.Should().Be(2);
            probe.AssertOwnership(expectedCalls: 2, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
            AssertResidentShortcut(engine, probe, 2);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Parallelizable(ParallelScope.All)]
    public async Task FailedBulkPublicationOwnsItsRevisionCheckThroughRemoval(
        bool asynchronous,
        bool failBeforeReady
    )
    {
        var probe = new PublicationProbe(failBeforeReady, failFinalization: !failBeforeReady);
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => throw new InvalidOperationException("Unexpected single load."),
                bulkLoader: static (_, _) =>
                    Task.FromResult<IReadOnlyDictionary<int, string>>(BulkValues())
            );
            await ExpectFailure(cache.GetAllAsync([1]).AsTask());
            probe.Stop();
            cache.TryGet(1, out _).Should().BeFalse();
            cache.TryGet(2, out _).Should().BeFalse();
            cache.EstimatedCount.Should().Be(0);
            cache.Statistics.InFlightLoads.Should().Be(0);
            (await cache.GetAllAsync([1])).Should().ContainKey(1);
            engine.AssertInvariants();
        }
        else
        {
            using var cache = new LoadingCache<int, string>(
                engine,
                static _ => throw new InvalidOperationException("Unexpected single load."),
                bulkLoader: static _ => BulkValues()
            );
            cache
                .Invoking(static current => current.GetAll([1]))
                .Should()
                .ThrowExactly<ControlledPublicationFailure>();
            probe.Stop();
            cache.TryGet(1, out _).Should().BeFalse();
            cache.TryGet(2, out _).Should().BeFalse();
            cache.EstimatedCount.Should().Be(0);
            cache.Statistics.InFlightLoads.Should().Be(0);
            cache.GetAll([1]).Should().ContainKey(1);
            cache.AssertInvariants();
        }

        probe.AssertOwnership(expectedCalls: failBeforeReady ? 1 : 2, expectedOutsideCalls: 1);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Parallelizable(ParallelScope.All)]
    public async Task RefreshFinalizationAndRollbackRetainEntryOwnership(
        bool asynchronous,
        bool failAfterPublication
    )
    {
        var probe = new PublicationProbe(failAfterRefresh: failAfterPublication);
        probe.Stop();
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => Task.FromResult("new")
            );
            cache.Set(1, "old");
            cache.CleanUp();
            probe.Start();
            Task<string> refresh = cache.RefreshAsync(1).AsTask();
            if (failAfterPublication)
                await ExpectFailure(refresh);
            else
                (await refresh).Should().Be("new");
            probe.Stop();
            cache.TryGet(1, out string? current).Should().BeTrue();
            current.Should().Be(failAfterPublication ? "old" : "new");
            cache.Statistics.InFlightLoads.Should().Be(0);
            cache.Statistics.ReplacedRemovals.Should().Be(failAfterPublication ? 0 : 1);
            engine.AssertInvariants();
            probe.AssertOwnership(
                expectedCalls: failAfterPublication ? 3 : 2,
                expectedOutsideCalls: 2
            );
            AssertResidentShortcut(engine, probe, 1);
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, static _ => "new");
            cache.Put(1, "old");
            cache.CleanUp();
            probe.Start();
            Task<string> refresh = cache.RefreshAsync(1);
            if (failAfterPublication)
                await ExpectFailure(refresh);
            else
                (await refresh).Should().Be("new");
            probe.Stop();
            AssertResident(cache, 1, failAfterPublication ? "old" : "new");
            cache.Statistics.InFlightLoads.Should().Be(0);
            cache.Statistics.ReplacedRemovals.Should().Be(failAfterPublication ? 0 : 1);
            probe.AssertOwnership(
                expectedCalls: failAfterPublication ? 3 : 2,
                expectedOutsideCalls: 2
            );
            AssertResidentShortcut(engine, probe, 1);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task RefreshReservationAlreadyOwnsTheEntry(bool asynchronous)
    {
        var probe = new PublicationProbe();
        probe.Stop();
        CacheEngine<int, string> engine = CreateEngine(probe);
        if (asynchronous)
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                static (_, _) => Task.FromException<string>(new ControlledPublicationFailure())
            );
            cache.Set(1, "old");
            cache.CleanUp();
            probe.Start();
            await ExpectFailure(cache.RefreshAsync(1).AsTask());
            probe.Stop();
            cache.TryGet(1, out string? current).Should().BeTrue();
            current.Should().Be("old");
            engine.AssertInvariants();
        }
        else
        {
            using var cache = new LoadingCache<int, string>(
                engine,
                static _ => throw new ControlledPublicationFailure()
            );
            cache.Put(1, "old");
            cache.CleanUp();
            probe.Start();
            await ExpectFailure(cache.RefreshAsync(1));
            probe.Stop();
            AssertResident(cache, 1, "old");
        }

        probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 1);
    }

    private static Dictionary<int, string> BulkValues() =>
        new Dictionary<int, string> { [1] = "one", [2] = "two" };

    private static CacheEngine<int, string> CreateEngine(PublicationProbe probe) =>
        new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                MaxPendingLoadKeys = 2,
                MaximumBulkKeys = 2,
                RecordStatistics = true,
                TestHooks = new LoadingCacheTestHooks
                {
                    BeforeEntryPublicationCommit = probe.Observe,
                    BeforeResidentValuePublished = probe.ObserveResidentPublication,
                    BeforeReadyPublish = probe.BeforeReady,
                    AfterRefreshPublished = probe.AfterRefresh,
                    BeforeCompletion = probe.ObserveOutsideEntry,
                },
            }
        );

    private static void AssertResident(Cache<int, string> cache, int key, string expected)
    {
        cache.TryGet(key, out string? value).Should().BeTrue();
        value.Should().Be(expected);
        cache.AssertInvariants();
    }

    private static void AssertResidentShortcut(
        CacheEngine<int, string> engine,
        PublicationProbe probe,
        int key
    )
    {
        probe.Stop();
        int previousCalls = probe.ResidentPublicationCalls;
        engine.Put(key, "next");
        probe
            .ResidentPublicationCalls.Should()
            .Be(
                previousCalls + 1,
                "successful publication or repair must reopen the same-weight resident shortcut"
            );
        engine.TryGet(key, out string? value).Should().BeTrue();
        value.Should().Be("next");
        engine.AssertInvariants();
    }

    private static async Task ExpectFailure<T>(Task<T> operation)
    {
        await FluentActions
            .Awaiting(async () =>
            {
                await operation;
            })
            .Should()
            .ThrowExactlyAsync<ControlledPublicationFailure>();
    }

    private sealed class ControlledPublicationFailure : Exception;

    private sealed class PublicationProbe(
        bool failBeforeReady = false,
        bool failFinalization = false,
        bool failAfterRefresh = false
    )
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
        private readonly ConcurrentDictionary<object, byte> _monitors = new();
        private readonly ConcurrentQueue<(bool OwnerHeld, bool CompetitorAcquired)> _owned = new();
        private readonly ConcurrentQueue<(bool OwnerHeld, bool CompetitorAcquired)> _outside =
            new();
        private int _enabled = 1;
        private int _failBeforeReady = failBeforeReady ? 1 : 0;
        private int _failFinalization = failFinalization ? 1 : 0;
        private int _failAfterRefresh = failAfterRefresh ? 1 : 0;
        private int _outsideCalls;
        private int _residentPublicationCalls;

        internal int ResidentPublicationCalls => Volatile.Read(ref _residentPublicationCalls);

        internal void ObserveResidentPublication() =>
            Interlocked.Increment(ref _residentPublicationCalls);

        internal void Start() => Volatile.Write(ref _enabled, 1);

        internal void Stop() => Volatile.Write(ref _enabled, 0);

        internal void Observe(object sync)
        {
            _monitors.TryAdd(sync, 0);
            if (Volatile.Read(ref _enabled) == 0)
                return;
            _owned.Enqueue(CheckMonitor(sync));
            if (Interlocked.Exchange(ref _failFinalization, 0) != 0)
                throw new ControlledPublicationFailure();
        }

        internal void BeforeReady()
        {
            if (Interlocked.Exchange(ref _failBeforeReady, 0) != 0)
                throw new ControlledPublicationFailure();
        }

        internal void AfterRefresh()
        {
            ObserveOutsideEntry();
            if (Interlocked.Exchange(ref _failAfterRefresh, 0) != 0)
                throw new ControlledPublicationFailure();
        }

        internal void ObserveOutsideEntry()
        {
            if (Volatile.Read(ref _enabled) == 0)
                return;
            Interlocked.Increment(ref _outsideCalls);
            foreach (object sync in _monitors.Keys)
                _outside.Enqueue(CheckMonitor(sync));
        }

        private static (bool OwnerHeld, bool CompetitorAcquired) CheckMonitor(object sync)
        {
            bool ownerHeld = Monitor.IsEntered(sync);
            Task<bool> competitor = Task.Factory.StartNew(
                static state =>
                {
                    object entrySync = state!;
                    bool acquired = Monitor.TryEnter(entrySync, 0);
                    if (acquired)
                        Monitor.Exit(entrySync);
                    return acquired;
                },
                sync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            bool timely = competitor.Wait(Watchdog);
            bool acquiredByCompetitor = competitor.GetAwaiter().GetResult();
            timely.Should().BeTrue("the dedicated nonblocking probe must complete");
            return (ownerHeld, acquiredByCompetitor);
        }

        internal void AssertOwnership(int expectedCalls, int expectedOutsideCalls)
        {
            _owned.Should().HaveCount(expectedCalls);
            _owned.Should().OnlyContain(result => result.OwnerHeld && !result.CompetitorAcquired);
            _outsideCalls.Should().Be(expectedOutsideCalls);
            if (expectedOutsideCalls != 0)
            {
                _outside.Should().NotBeEmpty();
                _outside
                    .Should()
                    .OnlyContain(result => !result.OwnerHeld && result.CompetitorAcquired);
            }
        }
    }
}
