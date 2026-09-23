using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class ResidentPutHistoryTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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
            await Assert.That(cache.GetOrAdd(1, static _ => "loaded")).IsEqualTo("loaded");
            if (replace)
            {
                cache.Put(1, "replacement");
            }

            cache.CleanUp();
            MemoryPressureSnapshot snapshot = engine.CaptureMemoryPressureSnapshot(1, 1)!;
            await Assert.That(snapshot.Candidates).HasSingleItem();
            await Assert.That(engine.TrimForMemoryPressure(snapshot)).IsEqualTo(1);
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
        }
        finally
        {
            transform.Release.TrySetResult();
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(transform.Calls).IsEqualTo(2);
        await Assert.That(cache.AsDictionary()[1]).IsEqualTo("retried");
        cache.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
                await Assert.That(cache.Invalidate(1)).IsFalse();
            else
                cache.Put(2, "new");
        }
        finally
        {
            transform.Release.TrySetResult();
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(transform.Calls).IsEqualTo(invalidate ? 2 : 1);
        await Assert.That(cache.AsDictionary()[1]).IsEqualTo(invalidate ? "retried" : "stale");
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
    public async Task ComputeDoesNotRetryBecauseItsOwnSnapshotRemovedAnExpiredEntry()
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
                    if (current.HasValue)
                        Assert.Fail("Expected current.HasValue to be false ().");
                    calls++;
                    return CacheMutation.Set("new");
                }
            );
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(cache.AsDictionary()[1]).IsEqualTo("new");
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
            if ((key) != (1))
                Assert.Fail("Expected key to equal (1).");
            if (current.HasValue)
                Assert.Fail("Expected current.HasValue to be false ().");
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
