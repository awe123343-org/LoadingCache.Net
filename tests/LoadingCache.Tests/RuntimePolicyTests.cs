using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class RuntimePolicyTests
{
    [Test]
    public async Task ConcurrentResizeWritesAndSnapshotsConverge()
    {
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(32)
            .MaxConcurrentLoads(4)
            .Build();
        var eviction = cache.Policy.Eviction!;
        var workers = new Task[4];
        for (int worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Factory.StartNew(
                static state =>
                {
                    (ICache<int, int> current, int workerId) = ((ICache<int, int>, int))state!;
                    var policy = current.Policy.Eviction!;
                    var random = new Random(811 + workerId);
                    for (int operation = 0; operation < 1_000; operation++)
                    {
                        int key = random.Next(128);
                        switch (operation % 5)
                        {
                            case 0:
                                policy.SetMaximum(random.Next(1, 64));
                                break;
                            case 1:
                                current.Put(key, operation);
                                break;
                            case 2:
                                current.TryGet(key, out _);
                                break;
                            case 3:
                                current.Invalidate(key);
                                break;
                            default:
                                if ((policy.Hottest(8).Count) > (8))
                                    Assert.Fail(
                                        "Expected policy.Hottest(8).Count to be at most (8)."
                                    );
                                break;
                        }
                    }
                },
                (cache, worker),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
        }

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        eviction.SetMaximum(4);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(4);
        await Assert.That(eviction.WeightedSize).IsEqualTo(cache.EstimatedCount);
        ((Cache<int, int>)cache).AssertInvariants();
    }

    [Test]
    public async Task SizeMaximumCanShrinkAndGrowWithoutKeepingTheOriginalCountCap()
    {
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .Build();
        for (int key = 0; key < 4; key++)
            cache.Put(key, key);
        var eviction = cache.Policy.Eviction!;
        eviction.SetMaximum(1);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(eviction.Maximum).IsEqualTo(1);
        eviction.SetMaximum(8);
        cache.Clear();
        for (int key = 0; key < 8; key++)
            cache.Put(key, key);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(8);
        await Assert.That(eviction.WeightedSize).IsEqualTo(8);
        ((Cache<int, int>)cache).AssertInvariants();
    }

    [Test]
    public async Task WeightedMaximumPreservesTheIndependentZeroWeightCountBound()
    {
        using var cache = CacheBuilder
            .Create<int, long>()
            .MaximumWeight(20)
            .MaximumResidentCount(3)
            .Weigher((_, value) => value)
            .MaxConcurrentLoads(2)
            .Build();
        cache.Put(1, 10);
        cache.Put(2, 10);
        cache.Policy.Eviction!.SetMaximum(5);
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsLessThanOrEqualTo(5);
        cache.Policy.Eviction.SetMaximum(long.MaxValue);
        cache.Clear();
        for (int key = 0; key < 10; key++)
            cache.Put(key, 0);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(3);
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsEqualTo(0);
    }

    [Test]
    public async Task ResizingDoesNotRemoveColdFlightReservations()
    {
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .BuildAsyncLoading((_, _) => completion.Task);
        Task<int> first = cache.GetAsync(1).AsTask();
        cache.Policy.Eviction!.SetMaximum(1);
        Task<int> joined = cache.GetAsync(1).AsTask();
        completion.SetResult(42);
        await Assert.That((await first)).IsEqualTo(42);
        await Assert.That((await joined)).IsEqualTo(42);
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
    }

    [Test]
    public async Task QuietLookupAndSnapshotsDoNotExtendAccessExpirationOrCountHits()
    {
        var time = new FakeTimeProvider();
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .TimeProvider(time)
            .ExpireAfterAccess(TimeSpan.FromSeconds(10))
            .RecordStatistics()
            .Build();
        cache.Put(1, 0);
        time.Advance(TimeSpan.FromSeconds(9));
        await Assert.That(cache.Policy.TryGetQuietly(1, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(0);
        var snapshot = cache.Policy.Eviction!.Hottest(4);
        await Assert.That(snapshot).HasSingleItem();
        await Assert.That(cache.Statistics.Hits).IsEqualTo(0);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.Policy.TryGetQuietly(1, out _)).IsFalse();
        await Assert.That(cache.Policy.Eviction.Coldest(4)).IsEmpty();
        await Assert.That(snapshot).HasSingleItem();
        await Assert.That(cache.Statistics.Misses).IsEqualTo(0);
    }

    [Test]
    public async Task OrderedSnapshotsAreBoundedAndDoNotExposeMutableCacheState()
    {
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .Build();
        for (int key = 0; key < 8; key++)
            cache.Put(key, key);
        var cold = cache.Policy.Eviction!.Coldest(8);
        var hot = cache.Policy.Eviction.Hottest(8);
        await Assert
            .That(hot.Select(pair => pair.Key))
            .IsEquivalentTo(
                cold.Reverse().Select(pair => pair.Key),
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(cache.Policy.Eviction.Hottest(2).Count).IsEqualTo(2);
        await Assert.That(cache.Policy.Eviction.Coldest(0)).IsEmpty();
        cache.Clear();
        await Assert.That(cold.Count).IsEqualTo(8);
        await Assert.That(cache.Policy.Eviction.Hottest(int.MaxValue)).IsEmpty();
    }

    [Test]
    public async Task InvalidLimitsAndSavedViewsAfterDisposalFail()
    {
        var cache = CacheBuilder.Create<int, int>().MaximumSize(4).MaxConcurrentLoads(1).Build();
        var policy = cache.Policy;
        var eviction = policy.Eviction!;
        Action zero = () => eviction.SetMaximum(0);
        Action tooLarge = () => eviction.SetMaximum((long)int.MaxValue + 1);
        Action negative = () => eviction.Coldest(-1);
        await Assert.That(zero).Throws<ArgumentOutOfRangeException>();
        await Assert.That(tooLarge).Throws<ArgumentOutOfRangeException>();
        await Assert.That(negative).Throws<ArgumentOutOfRangeException>();
        cache.Dispose();
        Action resize = () => eviction.SetMaximum(1);
        Action snapshot = () => eviction.Hottest(1);
        Action quiet = () => policy.TryGetQuietly(1, out _);
        await Assert.That(resize).Throws<ObjectDisposedException>();
        await Assert.That(snapshot).Throws<ObjectDisposedException>();
        await Assert.That(quiet).Throws<ObjectDisposedException>();
    }
}
