using System.Collections.Concurrent;

namespace LoadingCache.Tests;

public sealed class EntryMutationOwnershipTests
{
    [Test]
    [Arguments("update")]
    [Arguments("remove-value")]
    [Arguments("remove-comparison")]
    public async Task ConditionalMutationOwnsTheEntryThroughCommit(string operation)
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        probe.Start();
        try
        {
            switch (operation)
            {
                case "update":
                    await Assert.That(dictionary.TryUpdate(1, "new", "old")).IsTrue();
                    await Assert.That(dictionary[1]).IsEqualTo("new");
                    await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(1);
                    break;
                case "remove-value":
                    await Assert.That(dictionary.TryRemove(1, out string? removed)).IsTrue();
                    await Assert.That(removed).IsEqualTo("old");
                    await Assert.That(dictionary.ContainsKey(1)).IsFalse();
                    await Assert.That(cache.Statistics.ExplicitRemovals).IsEqualTo(1);
                    break;
                case "remove-comparison":
                    await Assert.That(dictionary.TryRemove(1, "old")).IsTrue();
                    await Assert.That(dictionary.ContainsKey(1)).IsFalse();
                    await Assert.That(cache.Statistics.ExplicitRemovals).IsEqualTo(1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
        finally
        {
            probe.Stop();
        }

        cache.AssertInvariants();
        // One caller commit and one retirement of the exact old mapping.
        probe.AssertExclusive(expectedCalls: 2);
    }

    [Test]
    [Arguments(CacheMutationKind.Keep)]
    [Arguments(CacheMutationKind.Set)]
    [Arguments(CacheMutationKind.Remove)]
    public async Task PresentComputeOwnsItsFinalValidationThroughCommit(CacheMutationKind kind)
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        probe.Start();
        try
        {
            CacheMutation<string> result = cache
                .AsDictionary()
                .Compute(
                    1,
                    (_, current) =>
                    {
                        if (!(current.HasValue))
                            Assert.Fail("Expected current.HasValue to be true ().");
                        if ((current.Value) != ("old"))
                            Assert.Fail("Expected current.Value to equal (\"old\").");
                        return kind switch
                        {
                            CacheMutationKind.Keep => CacheMutation.Keep<string>(),
                            CacheMutationKind.Set => CacheMutation.Set("new"),
                            CacheMutationKind.Remove => CacheMutation.Remove<string>(),
                            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                        };
                    }
                );
            await Assert.That(result.Kind).IsEqualTo(kind);
            if (kind == CacheMutationKind.Remove)
            {
                await Assert.That(cache.TryGet(1, out _)).IsFalse();
                await Assert.That(cache.Statistics.ExplicitRemovals).IsEqualTo(1);
            }
            else
            {
                await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
                await Assert.That(value).IsEqualTo(kind == CacheMutationKind.Set ? "new" : "old");
                await Assert
                    .That(cache.Statistics.ReplacedRemovals)
                    .IsEqualTo(kind == CacheMutationKind.Set ? 1 : 0);
            }
        }
        finally
        {
            probe.Stop();
        }

        cache.AssertInvariants();
        probe.AssertExclusive(expectedCalls: kind == CacheMutationKind.Keep ? 1 : 2);
    }

    [Test]
    public async Task PressureTrimOwnsItsRevisionValidationThroughRetirement()
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        MemoryPressureSnapshot snapshot = engine.CaptureMemoryPressureSnapshot(1, 1)!;
        await Assert.That(snapshot.Candidates).HasSingleItem();
        probe.Start();
        try
        {
            await Assert.That(engine.TrimForMemoryPressure(snapshot)).IsEqualTo(1);
        }
        finally
        {
            probe.Stop();
        }

        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.Statistics.MemoryPressureRemovals).IsEqualTo(1);
        await Assert.That(cache.Statistics.Evictions).IsEqualTo(1);
        cache.AssertInvariants();
        probe.AssertExclusive(expectedCalls: 2);
    }

    [Test]
    [Arguments("invalidate")]
    [Arguments("clear")]
    [Arguments("dispose")]
    [Arguments("dispose-async")]
    public async Task CommonRetirementOwnsTheExactEntry(string operation)
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        var cache = new Cache<int, string>(engine);
        try
        {
            cache.Put(1, "old");
            cache.CleanUp();
            probe.Start();
            try
            {
                switch (operation)
                {
                    case "invalidate":
                        await Assert.That(cache.Invalidate(1)).IsTrue();
                        break;
                    case "clear":
                        cache.Clear();
                        break;
                    case "dispose":
                        engine.Dispose();
                        break;
                    case "dispose-async":
                        await engine.DisposeAsync();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }
            finally
            {
                probe.Stop();
            }

            if (operation is "invalidate" or "clear")
            {
                await Assert.That(cache.TryGet(1, out _)).IsFalse();
                await Assert.That(cache.EstimatedCount).IsEqualTo(0);
                CacheStatistics statistics = cache.Statistics;
                await Assert
                    .That(statistics.ExplicitRemovals)
                    .IsEqualTo(operation == "invalidate" ? 1 : 0);
                await Assert
                    .That(statistics.ClearedRemovals)
                    .IsEqualTo(operation == "clear" ? 1 : 0);
                cache.AssertInvariants();
            }
            else
            {
                await Assert
                    .That(() => cache.TryGet(1, out _))
                    .ThrowsExactly<ObjectDisposedException>();
            }

            probe.AssertExclusive(expectedCalls: 1);
        }
        finally
        {
            probe.Stop();
            engine.Dispose();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedComparisonDoesNotReachTheCommitSeam(bool remove)
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        probe.Start();
        try
        {
            bool changed = remove
                ? dictionary.TryRemove(1, "mismatch")
                : dictionary.TryUpdate(1, "new", "mismatch");
            await Assert.That(changed).IsFalse();
        }
        finally
        {
            probe.Stop();
        }

        await Assert.That(dictionary[1]).IsEqualTo("old");
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(0);
        await Assert.That(cache.Statistics.ExplicitRemovals).IsEqualTo(0);
        probe.AssertExclusive(expectedCalls: 0);
    }

    private static CacheEngine<int, string> CreateEngine(OwnershipProbe probe) =>
        CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .RecordStatistics()
            .CreateEngine(new LoadingCacheTestHooks { BeforeEntryMutationCommit = probe.Observe });

    private sealed class OwnershipProbe
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
        private readonly ConcurrentQueue<(bool OwnerHeld, bool CompetitorAcquired)> _observations =
            new();
        private int _enabled;

        internal void Start() => Volatile.Write(ref _enabled, 1);

        internal void Stop() => Volatile.Write(ref _enabled, 0);

        internal void Observe(object sync)
        {
            if (Volatile.Read(ref _enabled) == 0)
            {
                return;
            }

            bool ownerHeld = Monitor.IsEntered(sync);
            Task<bool> competitor = Task.Factory.StartNew(
                static state =>
                {
                    object entrySync = state!;
                    bool acquired = Monitor.TryEnter(entrySync, 0);
                    if (acquired)
                    {
                        Monitor.Exit(entrySync);
                    }

                    return acquired;
                },
                sync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            // The zero-wait acquisition result is the oracle; the five-second limit only
            // detects broken test infrastructure. Always join the nonblocking competitor.
            bool completedWithinWatchdog = competitor.Wait(Watchdog);
            bool competitorAcquired = competitor.GetAwaiter().GetResult();
            if (!(completedWithinWatchdog))
                Assert.Fail(
                    "Expected completedWithinWatchdog to be true (\"the dedicated probe must complete\")."
                );
            _observations.Enqueue((ownerHeld, competitorAcquired));
        }

        internal void AssertExclusive(int expectedCalls)
        {
            if (!(_observations.Count == expectedCalls))
                Assert.Fail("Expected _observations to have count (expectedCalls).");
            if (expectedCalls != 0)
            {
                if (
                    !_observations.All(observation =>
                        observation.OwnerHeld && !observation.CompetitorAcquired
                    )
                )
                    Assert.Fail(
                        "Final validation/capture and commit must share continuous entry ownership."
                    );
            }
        }
    }
}
