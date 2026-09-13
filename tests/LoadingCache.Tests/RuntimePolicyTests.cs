using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

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
        await Task.WhenAll(
                Enumerable
                    .Range(0, 4)
                    .Select(worker =>
                        Task.Run(
                            () =>
                            {
                                var random = new Random(811 + worker);
                                for (int operation = 0; operation < 1_000; operation++)
                                {
                                    int key = random.Next(128);
                                    switch (operation % 5)
                                    {
                                        case 0:
                                            eviction.SetMaximum(random.Next(1, 64));
                                            break;
                                        case 1:
                                            cache.Put(key, operation);
                                            break;
                                        case 2:
                                            cache.TryGet(key, out _);
                                            break;
                                        case 3:
                                            cache.Invalidate(key);
                                            break;
                                        default:
                                            eviction.Hottest(8).Count.Should().BeLessOrEqualTo(8);
                                            break;
                                    }
                                }
                            },
                            CancellationToken.None
                        )
                    )
            )
            .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        eviction.SetMaximum(4);
        cache.CleanUp();
        cache.EstimatedCount.Should().BeLessOrEqualTo(4);
        eviction.WeightedSize.Should().Be(cache.EstimatedCount);
        ((Cache<int, int>)cache).AssertInvariants();
    }

    [Test]
    public void SizeMaximumCanShrinkAndGrowWithoutKeepingTheOriginalCountCap()
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
        cache.EstimatedCount.Should().Be(1);
        eviction.Maximum.Should().Be(1);
        eviction.SetMaximum(8);
        cache.Clear();
        for (int key = 0; key < 8; key++)
            cache.Put(key, key);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(8);
        eviction.WeightedSize.Should().Be(8);
        ((Cache<int, int>)cache).AssertInvariants();
    }

    [Test]
    public void WeightedMaximumPreservesTheIndependentZeroWeightCountBound()
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
        cache.Policy.Eviction.WeightedSize.Should().BeLessOrEqualTo(5);
        cache.Policy.Eviction.SetMaximum(long.MaxValue);
        cache.Clear();
        for (int key = 0; key < 10; key++)
            cache.Put(key, 0);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(3);
        cache.Policy.Eviction.WeightedSize.Should().Be(0);
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
        (await first).Should().Be(42);
        (await joined).Should().Be(42);
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public void QuietLookupAndSnapshotsDoNotExtendAccessExpirationOrCountHits()
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
        cache.Policy.TryGetQuietly(1, out int value).Should().BeTrue();
        value.Should().Be(0);
        var snapshot = cache.Policy.Eviction!.Hottest(4);
        snapshot.Should().ContainSingle();
        cache.Statistics.Hits.Should().Be(0);
        time.Advance(TimeSpan.FromSeconds(1));
        cache.Policy.TryGetQuietly(1, out _).Should().BeFalse();
        cache.Policy.Eviction.Coldest(4).Should().BeEmpty();
        snapshot.Should().ContainSingle();
        cache.Statistics.Misses.Should().Be(0);
    }

    [Test]
    public void OrderedSnapshotsAreBoundedAndDoNotExposeMutableCacheState()
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
        hot.Select(pair => pair.Key).Should().Equal(cold.Reverse().Select(pair => pair.Key));
        cache.Policy.Eviction.Hottest(2).Should().HaveCount(2);
        cache.Policy.Eviction.Coldest(0).Should().BeEmpty();
        cache.Clear();
        cold.Should().HaveCount(8);
        cache.Policy.Eviction.Hottest(int.MaxValue).Should().BeEmpty();
    }

    [Test]
    public void InvalidLimitsAndSavedViewsAfterDisposalFail()
    {
        var cache = CacheBuilder.Create<int, int>().MaximumSize(4).MaxConcurrentLoads(1).Build();
        var policy = cache.Policy;
        var eviction = policy.Eviction!;
        Action zero = () => eviction.SetMaximum(0);
        Action tooLarge = () => eviction.SetMaximum((long)int.MaxValue + 1);
        Action negative = () => eviction.Coldest(-1);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        tooLarge.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
        cache.Dispose();
        Action resize = () => eviction.SetMaximum(1);
        Action snapshot = () => eviction.Hottest(1);
        Action quiet = () => policy.TryGetQuietly(1, out _);
        resize.Should().Throw<ObjectDisposedException>();
        snapshot.Should().Throw<ObjectDisposedException>();
        quiet.Should().Throw<ObjectDisposedException>();
    }
}
