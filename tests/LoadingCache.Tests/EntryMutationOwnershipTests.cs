using System.Collections.Concurrent;
using FluentAssertions;

namespace LoadingCache.Tests;

public sealed class EntryMutationOwnershipTests
{
    [Test]
    [Arguments("update")]
    [Arguments("remove-value")]
    [Arguments("remove-comparison")]
    public void ConditionalMutationOwnsTheEntryThroughCommit(string operation)
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
                    dictionary.TryUpdate(1, "new", "old").Should().BeTrue();
                    dictionary[1].Should().Be("new");
                    cache.Statistics.ReplacedRemovals.Should().Be(1);
                    break;
                case "remove-value":
                    dictionary.TryRemove(1, out string? removed).Should().BeTrue();
                    removed.Should().Be("old");
                    dictionary.ContainsKey(1).Should().BeFalse();
                    cache.Statistics.ExplicitRemovals.Should().Be(1);
                    break;
                case "remove-comparison":
                    dictionary.TryRemove(1, "old").Should().BeTrue();
                    dictionary.ContainsKey(1).Should().BeFalse();
                    cache.Statistics.ExplicitRemovals.Should().Be(1);
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
    public void PresentComputeOwnsItsFinalValidationThroughCommit(CacheMutationKind kind)
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
                        current.HasValue.Should().BeTrue();
                        current.Value.Should().Be("old");
                        return kind switch
                        {
                            CacheMutationKind.Keep => CacheMutation.Keep<string>(),
                            CacheMutationKind.Set => CacheMutation.Set("new"),
                            CacheMutationKind.Remove => CacheMutation.Remove<string>(),
                            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                        };
                    }
                );
            result.Kind.Should().Be(kind);
            if (kind == CacheMutationKind.Remove)
            {
                cache.TryGet(1, out _).Should().BeFalse();
                cache.Statistics.ExplicitRemovals.Should().Be(1);
            }
            else
            {
                cache.TryGet(1, out string? value).Should().BeTrue();
                value.Should().Be(kind == CacheMutationKind.Set ? "new" : "old");
                cache
                    .Statistics.ReplacedRemovals.Should()
                    .Be(kind == CacheMutationKind.Set ? 1 : 0);
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
    public void PressureTrimOwnsItsRevisionValidationThroughRetirement()
    {
        var probe = new OwnershipProbe();
        CacheEngine<int, string> engine = CreateEngine(probe);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        MemoryPressureSnapshot snapshot = engine.CaptureMemoryPressureSnapshot(1, 1)!;
        snapshot.Candidates.Should().ContainSingle();
        probe.Start();
        try
        {
            engine.TrimForMemoryPressure(snapshot).Should().Be(1);
        }
        finally
        {
            probe.Stop();
        }

        cache.TryGet(1, out _).Should().BeFalse();
        cache.Statistics.MemoryPressureRemovals.Should().Be(1);
        cache.Statistics.Evictions.Should().Be(1);
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
                        cache.Invalidate(1).Should().BeTrue();
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
                cache.TryGet(1, out _).Should().BeFalse();
                cache.EstimatedCount.Should().Be(0);
                CacheStatistics statistics = cache.Statistics;
                statistics.ExplicitRemovals.Should().Be(operation == "invalidate" ? 1 : 0);
                statistics.ClearedRemovals.Should().Be(operation == "clear" ? 1 : 0);
                cache.AssertInvariants();
            }
            else
            {
                cache
                    .Invoking(static current => current.TryGet(1, out _))
                    .Should()
                    .ThrowExactly<ObjectDisposedException>();
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
    public void FailedComparisonDoesNotReachTheCommitSeam(bool remove)
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
            changed.Should().BeFalse();
        }
        finally
        {
            probe.Stop();
        }

        dictionary[1].Should().Be("old");
        cache.Statistics.ReplacedRemovals.Should().Be(0);
        cache.Statistics.ExplicitRemovals.Should().Be(0);
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
            completedWithinWatchdog.Should().BeTrue("the dedicated probe must complete");
            _observations.Enqueue((ownerHeld, competitorAcquired));
        }

        internal void AssertExclusive(int expectedCalls)
        {
            _observations.Should().HaveCount(expectedCalls);
            if (expectedCalls != 0)
            {
                _observations
                    .Should()
                    .OnlyContain(
                        observation => observation.OwnerHeld && !observation.CompetitorAcquired,
                        "final validation/capture and commit must share continuous entry ownership"
                    );
            }
        }
    }
}
