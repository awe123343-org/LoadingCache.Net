using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ResidentPutHistoryTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Parallelizable(ParallelScope.All)]
    public async Task MissingComputeRetriesAfterPublicationAndAutomaticRemoval(
        bool replace,
        bool rollover
    )
    {
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .CreateEngine(supportsBulkLoading: false);
        using var cache = new Cache<int, string>(engine);
        if (rollover)
        {
            engine.SetDictionaryMutationSequenceForTesting(long.MaxValue);
        }
        var transform = new MissingTransform();
        Task<CacheMutation<string>> compute = StartCompute(cache, transform);
        try
        {
            await transform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.GetOrAdd(1, static _ => "loaded").Should().Be("loaded");
            if (replace)
            {
                cache.Put(1, "replacement");
            }
            cache.CleanUp();
            MemoryPressureSnapshot snapshot = engine.CaptureMemoryPressureSnapshot(1, 1)!;
            snapshot.Candidates.Should().ContainSingle();
            engine.TrimForMemoryPressure(snapshot).Should().Be(1);
            cache.TryGet(1, out _).Should().BeFalse();
        }
        finally
        {
            transform.Release.TrySetResult();
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        transform.Calls.Should().Be(2);
        cache.AsDictionary()[1].Should().Be("retried");
        cache.AssertInvariants();
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task MissingSnapshotHonorsInvalidationWithoutDependingOnUnrelatedResidentWrites(
        bool invalidate
    )
    {
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Build();
        cache.Put(2, "old");
        cache.CleanUp();
        var transform = new MissingTransform();
        Task<CacheMutation<string>> compute = StartCompute(cache, transform);
        try
        {
            await transform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (invalidate)
                cache.Invalidate(1).Should().BeFalse();
            else
                cache.Put(2, "new");
        }
        finally
        {
            transform.Release.TrySetResult();
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }
        transform.Calls.Should().Be(invalidate ? 2 : 1);
        cache.AsDictionary()[1].Should().Be(invalidate ? "retried" : "stale");
    }

    private static Task<CacheMutation<string>> StartCompute(
        ICache<int, string> cache,
        MissingTransform transform
    ) =>
        Task.Factory.StartNew(
            static state =>
            {
                var (dictionary, currentTransform) = ((
                    SyncCacheDictionary<int, string>,
                    MissingTransform
                ))
                    state!;
                return dictionary.Compute(1, currentTransform.Invoke);
            },
            (cache.AsDictionary(), transform),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );

    [Test]
    [Parallelizable]
    public void ComputeDoesNotRetryBecauseItsOwnSnapshotRemovedAnExpiredEntry()
    {
        var clock = new FakeTimeProvider();
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .Build();
        cache.Put(1, "expired");
        clock.Advance(TimeSpan.FromSeconds(1));
        int calls = 0;

        cache
            .AsDictionary()
            .Compute(
                1,
                (_, current) =>
                {
                    current.HasValue.Should().BeFalse();
                    calls++;
                    return CacheMutation.Set("new");
                }
            );

        calls.Should().Be(1);
        cache.AsDictionary()[1].Should().Be("new");
    }

    private sealed class MissingTransform
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }

        internal CacheMutation<string> Invoke(int key, CacheValue<string> current)
        {
            key.Should().Be(1);
            current.HasValue.Should().BeFalse();
            Calls++;
            if (Calls != 1)
            {
                return CacheMutation.Set(Calls == 1 ? "stale" : "retried");
            }

            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return CacheMutation.Set(Calls == 1 ? "stale" : "retried");
        }
    }
}
