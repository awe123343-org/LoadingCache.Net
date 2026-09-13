using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class BulkAdversarialTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SetDuringPendingBulkPreservesTheNewerValue()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        cache.Set(1, 101);
        loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });

        IReadOnlyDictionary<int, int> result = await pending.WaitAsync(TestTimeout);
        result[1].Should().Be(10, "the existing waiter may observe its original flight");
        cache.TryGet(1, out int current).Should().BeTrue();
        current.Should().Be(101);
        cache.TryGet(2, out int loaded).Should().BeTrue();
        loaded.Should().Be(20);
    }

    [Test]
    public async Task InvalidateDuringPendingBulkDoesNotResurrectTheInvalidatedKey()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        cache.Invalidate(1).Should().BeTrue();
        loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });

        IReadOnlyDictionary<int, int> result = await pending.WaitAsync(TestTimeout);
        result[1].Should().Be(10, "the original waiter already joined the old flight");
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGet(2, out int loaded).Should().BeTrue();
        loaded.Should().Be(20);
    }

    [Test]
    public async Task ClearDuringPendingBulkFencesTheOldEpoch()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        cache.Clear();
        cache.Set(1, 101);
        loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });

        IReadOnlyDictionary<int, int> result = await pending.WaitAsync(TestTimeout);
        result[1].Should().Be(10, "the original waiter may observe its old epoch");
        cache.TryGet(1, out int current).Should().BeTrue();
        current.Should().Be(101);
        cache.TryGet(2, out _).Should().BeFalse();
    }

    [Test]
    public async Task PrefetchIsFencedByInvalidateOfAnAbsentKey()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        cache.Invalidate(99).Should().BeFalse();
        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );

        await pending.WaitAsync(TestTimeout);
        cache.TryGet(99, out _).Should().BeFalse();
    }

    [Test]
    public async Task PrefetchIsFencedByAnUnrelatedPut()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        cache.Set(77, 770);
        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );

        await pending.WaitAsync(TestTimeout);
        cache.TryGet(77, out int unrelated).Should().BeTrue();
        unrelated.Should().Be(770);
        cache.TryGet(99, out _).Should().BeFalse();
    }

    [Test]
    public async Task TimedOutBulkKeepsItsExecutionReservationUntilIgnoredBackendReturns()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var loader = new GatedBulkLoader { IgnoreCancellation = true };
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder()
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> first = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        clock.Advance(TimeSpan.FromSeconds(1));
        Func<Task> waitFirst = async () => await first.WaitAsync(TestTimeout);
        await waitFirst.Should().ThrowExactlyAsync<TimeoutException>();
        cache.GetStatistics().InFlightLoads.Should().Be(1);

        Func<Task> rejected = async () => await cache.GetAllAsync([3, 4]).AsTask();
        await rejected.Should().ThrowExactlyAsync<CacheLoadRejectedException>();

        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [3] = 30,
                [4] = 40,
            }
        );
        await loader.Finished.Task.WaitAsync(TestTimeout);
        SpinWait
            .SpinUntil(() => cache.GetStatistics().InFlightLoads == 0, TestTimeout)
            .Should()
            .BeTrue();

        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGet(2, out _).Should().BeFalse();

        IReadOnlyDictionary<int, int> second = await cache.GetAllAsync([3, 4]);
        second.Should().BeEquivalentTo(new Dictionary<int, int> { [3] = 30, [4] = 40 });
        loader.BulkCalls.Should().Be(2);
    }

    [Test]
    public async Task DisposeCompletesEveryBulkKeyPromise()
    {
        var loader = new GatedBulkLoader();
        IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> bulk = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        Task<int> joined = cache.GetAsync(2).AsTask();

        try
        {
            await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);

            Func<Task> waitBulk = async () => await bulk.WaitAsync(TestTimeout);
            await waitBulk.Should().ThrowExactlyAsync<ObjectDisposedException>();
            Func<Task> waitJoined = async () => await joined.WaitAsync(TestTimeout);
            await waitJoined.Should().ThrowExactlyAsync<ObjectDisposedException>();
        }
        finally
        {
            loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });
            await loader.Finished.Task.WaitAsync(TestTimeout);
            await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Test]
    public async Task BulkLoaderSelfAwaitOnNonLeaderKeyFailsFast()
    {
        var loader = new SelfAwaitBulkLoader();
        IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        loader.Cache = cache;

        try
        {
            Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
            await loader.Started.Task.WaitAsync(TestTimeout);
            Func<Task> wait = async () => await pending.WaitAsync(TestTimeout);
            await wait.Should().ThrowExactlyAsync<LoadingCacheReentrancyException>();
        }
        finally
        {
            await cache.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }

    [Test]
    public async Task InfiniteDuplicateInputStopsAtTheConfiguredBound()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder()
            .MaximumBulkKeys(4)
            .MaxPendingLoadKeys(4)
            .BuildAsyncLoading(loader);
        var input = new GuardedInfiniteDuplicateInput();

        Func<Task> operation = async () => await cache.GetAllAsync(input).AsTask();
        await operation.Should().ThrowExactlyAsync<ArgumentOutOfRangeException>();
        input.MoveNextCalls.Should().Be(5);
        loader.BulkCalls.Should().Be(0);
    }

    [Test]
    public async Task MissingBulkOutputDoesNotPublishAnyRequestedValue()
    {
        var loader = new ResultBulkLoader<int>(_ => new Dictionary<int, int> { [1] = 10 });
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        Func<Task> operation = async () => await cache.GetAllAsync([1, 2]).AsTask();
        await operation.Should().ThrowExactlyAsync<InvalidOperationException>();
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGet(2, out _).Should().BeFalse();
    }

    [Test]
    public async Task NullBulkOutputDoesNotPublishAnyRequestedValue()
    {
        var loader = new ResultBulkLoader<string>(_ => new Dictionary<int, string>
        {
            [1] = "one",
            [2] = null!,
        });
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(32)
            .MaxConcurrentLoads(4)
            .MaxPendingLoadKeys(8)
            .MaximumBulkKeys(4)
            .BuildAsyncLoading(loader);

        Func<Task> operation = async () => await cache.GetAllAsync([1, 2]).AsTask();
        await operation.Should().ThrowExactlyAsync<ArgumentNullException>();
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGet(2, out _).Should().BeFalse();
    }

    [Test]
    public async Task BulkOutputAcceptsPrefetchButCopiesTheReturnedMap()
    {
        var source = new Dictionary<int, int>
        {
            [1] = 10,
            [2] = 20,
            [99] = 990,
        };
        var loader = new ResultBulkLoader<int>(_ => source);
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);

        IReadOnlyDictionary<int, int> result = await cache.GetAllAsync([1, 2]);
        source[1] = 1001;
        source[99] = 9999;
        source[100] = 1000;

        result.Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        cache.TryGet(1, out int first).Should().BeTrue();
        first.Should().Be(10);
        cache.TryGet(99, out int prefetched).Should().BeTrue();
        prefetched.Should().Be(990);
        cache.TryGet(100, out _).Should().BeFalse();
    }

    private static CacheBuilder<int, int> CreateBuilder() =>
        CacheBuilder
            .Create<int, int>()
            .MaximumSize(32)
            .MaxConcurrentLoads(4)
            .MaxPendingLoadKeys(8)
            .MaximumBulkKeys(4);

    private sealed class GatedBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> Release { get; } =
            Signal<IReadOnlyDictionary<int, int>>();
        internal TaskCompletionSource<bool> Finished { get; } = Signal<bool>();
        internal bool IgnoreCancellation { get; init; }
        internal int BulkCalls;

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public async Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref BulkCalls);
            Started.TrySetResult(true);
            try
            {
                return IgnoreCancellation
                    ? await Release.Task.ConfigureAwait(false)
                    : await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Finished.TrySetResult(true);
            }
        }
    }

    private sealed class SelfAwaitBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        internal IAsyncLoadingCache<int, int>? Cache { get; set; }

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public async Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Started.TrySetResult(true);
            _ = await Cache!.GetAsync(2, cancellationToken).ConfigureAwait(false);
            return new Dictionary<int, int> { [1] = 10, [2] = 20 };
        }
    }

    private sealed class ResultBulkLoader<TValue> : IBulkAsyncCacheLoader<int, TValue>
        where TValue : notnull
    {
        private readonly Func<IReadOnlyCollection<int>, IReadOnlyDictionary<int, TValue>> _factory;

        internal ResultBulkLoader(
            Func<IReadOnlyCollection<int>, IReadOnlyDictionary<int, TValue>> factory
        )
        {
            _factory = factory;
        }

        public Task<TValue> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<TValue>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public Task<IReadOnlyDictionary<int, TValue>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        ) => Task.FromResult(_factory(keys));
    }

    private sealed class GuardedInfiniteDuplicateInput : IEnumerable<int>
    {
        internal int MoveNextCalls;

        public IEnumerator<int> GetEnumerator()
        {
            while (true)
            {
                int count = Interlocked.Increment(ref MoveNextCalls);
                if (count > 100)
                {
                    throw new InvalidOperationException("The bulk input was not bounded.");
                }

                yield return 1;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
