using System.Collections.Concurrent;

namespace LoadingCache.Tests;

public sealed class PublicationOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FaultedReadyPublicationRequiresPhysicalRepair(bool dictionary)
    {
        var probe = new PublicationProbe(failFinalization: true);
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        if (dictionary)
        {
            await Assert
                .That(() => cache.AsDictionary().TryAdd(1, "failed"))
                .ThrowsExactly<ControlledPublicationFailure>();
        }
        else
        {
            await Assert
                .That(() => cache.Put(1, "failed"))
                .ThrowsExactly<ControlledPublicationFailure>();
        }

        probe.Stop();
        cache.Put(1, "repaired");
        await Assert
            .That(probe.ResidentPublicationCalls)
            .IsEqualTo(0)
            .Because(
                "an entry with unfinished policy publication cannot take the resident shortcut"
            );
        cache.CleanUp();
        AssertResident(cache, 1, "repaired");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(1);
        AssertResidentShortcut(engine, probe, 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadyEntryRemainsOwnedThroughPolicyPublication(bool dictionary)
    {
        var probe = new PublicationProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        if (dictionary)
        {
            await Assert.That(cache.AsDictionary().TryAdd(1, "published")).IsTrue();
        }
        else
        {
            cache.Put(1, "published");
        }

        probe.Stop();
        AssertResident(cache, 1, "published");
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(0);
        probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 0);
        AssertResidentShortcut(engine, probe, 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That((await cache.GetAsync(1))).IsEqualTo("published");
            probe.Stop();
            await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
            await Assert.That(value).IsEqualTo("published");
            await Assert.That(cache.TryGetTask(1, out Task<string>? task)).IsTrue();
            Assert.NotNull(task);
            await Assert.That((await task!)).IsEqualTo("published");
            engine.AssertInvariants();
            probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, static _ => "published");
            await Assert.That(cache.Get(1)).IsEqualTo("published");
            probe.Stop();
            AssertResident(cache, 1, "published");
            probe.AssertOwnership(expectedCalls: 1, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(cache.EstimatedCount).IsEqualTo(0);
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert.That((await cache.GetAsync(1))).IsEqualTo("published");
            engine.AssertInvariants();
        }
        else
        {
            using var cache = new LoadingCache<int, string>(engine, static _ => "published");
            await Assert.That(() => cache.Get(1)).ThrowsExactly<ControlledPublicationFailure>();
            probe.Stop();
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(cache.EstimatedCount).IsEqualTo(0);
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert.That(cache.Get(1)).IsEqualTo("published");
            AssertResident(cache, 1, "published");
        }

        probe.AssertOwnership(expectedCalls: failBeforeReady ? 1 : 2, expectedOutsideCalls: 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(result).HasSingleItem();
            await Assert.That(result[1]).IsEqualTo("one");
            probe.Stop();
            await Assert.That(cache.TryGet(2, out string? extra)).IsTrue();
            await Assert.That(extra).IsEqualTo("two");
            await Assert.That(cache.EstimatedCount).IsEqualTo(2);
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
            await Assert.That(result).HasSingleItem();
            await Assert.That(result[1]).IsEqualTo("one");
            probe.Stop();
            AssertResident(cache, 2, "two");
            await Assert.That(cache.EstimatedCount).IsEqualTo(2);
            probe.AssertOwnership(expectedCalls: 2, expectedOutsideCalls: 1);
            AssertResidentShortcut(engine, probe, 1);
            AssertResidentShortcut(engine, probe, 2);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(cache.TryGet(2, out _)).IsFalse();
            await Assert.That(cache.EstimatedCount).IsEqualTo(0);
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert.That((await cache.GetAllAsync([1]))).ContainsKey(1);
            engine.AssertInvariants();
        }
        else
        {
            using var cache = new LoadingCache<int, string>(
                engine,
                static _ => throw new InvalidOperationException("Unexpected single load."),
                bulkLoader: static _ => BulkValues()
            );
            await Assert
                .That(() => cache.GetAll([1]))
                .ThrowsExactly<ControlledPublicationFailure>();
            probe.Stop();
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(cache.TryGet(2, out _)).IsFalse();
            await Assert.That(cache.EstimatedCount).IsEqualTo(0);
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert.That(cache.GetAll([1])).ContainsKey(1);
            cache.AssertInvariants();
        }

        probe.AssertOwnership(expectedCalls: failBeforeReady ? 1 : 2, expectedOutsideCalls: 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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
                await Assert.That((await refresh)).IsEqualTo("new");
            probe.Stop();
            await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
            await Assert.That(current).IsEqualTo(failAfterPublication ? "old" : "new");
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert
                .That(cache.Statistics.ReplacedRemovals)
                .IsEqualTo(failAfterPublication ? 0 : 1);
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
                await Assert.That((await refresh)).IsEqualTo("new");
            probe.Stop();
            AssertResident(cache, 1, failAfterPublication ? "old" : "new");
            await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
            await Assert
                .That(cache.Statistics.ReplacedRemovals)
                .IsEqualTo(failAfterPublication ? 0 : 1);
            probe.AssertOwnership(
                expectedCalls: failAfterPublication ? 3 : 2,
                expectedOutsideCalls: 2
            );
            AssertResidentShortcut(engine, probe, 1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
            await Assert.That(current).IsEqualTo("old");
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

    private static Dictionary<int, string> BulkValues() => new() { [1] = "one", [2] = "two" };

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
        if (!(cache.TryGet(key, out string? value)))
            Assert.Fail("Expected cache.TryGet(key, out string? value) to be true ().");
        if ((value) != (expected))
            Assert.Fail("Expected value to equal (expected).");
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
        if ((probe.ResidentPublicationCalls) != (previousCalls + 1))
            Assert.Fail(
                "Expected probe .ResidentPublicationCalls to equal ( previousCalls + 1, \"successful publication or repair must reopen the same-weight resident shortcut\" )."
            );
        if (!(engine.TryGet(key, out string? value)))
            Assert.Fail("Expected engine.TryGet(key, out string? value) to be true ().");
        if ((value) != ("next"))
            Assert.Fail("Expected value to equal (\"next\").");
        engine.AssertInvariants();
    }

    private static async Task ExpectFailure<T>(Task<T> operation)
    {
        await Assert
            .That(async () =>
            {
                await operation;
            })
            .ThrowsExactly<ControlledPublicationFailure>();
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
            if (!(timely))
                Assert.Fail(
                    "Expected timely to be true (\"the dedicated nonblocking probe must complete\")."
                );
            return (ownerHeld, acquiredByCompetitor);
        }

        internal void AssertOwnership(int expectedCalls, int expectedOutsideCalls)
        {
            if (!(_owned.Count == expectedCalls))
                Assert.Fail("Expected _owned to have count (expectedCalls).");
            if (!_owned.All(result => result.OwnerHeld && !result.CompetitorAcquired))
                Assert.Fail("Entry ownership was not continuous.");
            if ((_outsideCalls) != (expectedOutsideCalls))
                Assert.Fail("Expected _outsideCalls to equal (expectedOutsideCalls).");
            if (expectedOutsideCalls == 0)
            {
                return;
            }

            if (_outside.IsEmpty)
                Assert.Fail("No outside-lock observation was recorded.");
            if (!_outside.All(result => !result.OwnerHeld && result.CompetitorAcquired))
                Assert.Fail("Callback ran with entry ownership.");
        }
    }
}
