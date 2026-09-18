using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class BulkLoadingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public void SyncBulkLoaderUsesOneBackendCallAndAdmitsPrefetchedValues()
    {
        var loader = new SyncLoader();
        using ILoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .BuildLoading(loader);

        IReadOnlyDictionary<int, int> result = cache.GetAll([1, 2, 1]);

        result.Should().Equal(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        loader.BulkCalls.Should().Be(1);
        loader.SingleCalls.Should().Be(0);
        cache.TryGet(99, out int prefetched).Should().BeTrue();
        prefetched.Should().Be(990);
    }

    [Test]
    public void SyncBulkLoaderDeduplicatesComparerEquivalentKeys()
    {
        var loader = new StringSyncLoader();
        using ILoadingCache<string, string> cache = CacheBuilder
            .Create<string, string>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .BuildLoading(loader);

        IReadOnlyDictionary<string, string> result = cache.GetAll(["alpha", "ALPHA"]);

        result.Should().HaveCount(1);
        result.Values.Single().Should().Be("ALPHA");
        loader.Keys.Should().Equal("alpha");
    }

    [Test]
    public void InvalidSyncBulkResultDoesNotPublishAndCanRetry()
    {
        var loader = new SyncLoader { ReturnMissingKeyOnce = true };
        using ILoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .BuildLoading(loader);

        Action first = cache.Invoking(static current =>
        {
            current.GetAll([1, 2]);
        });
        first.Should().Throw<InvalidOperationException>();
        cache.TryGet(1, out _).Should().BeFalse();

        cache.GetAll([1, 2]).Should().HaveCount(2);
        loader.BulkCalls.Should().Be(2);
    }

    [Test]
    public void FallbackGetAllHonorsAnExplicitMaximumBulkKeysBound()
    {
        using ILoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaximumBulkKeys(2)
            .BuildLoading(static key => key * 10);

        Action operation = cache.Invoking(static current =>
        {
            current.GetAll([1, 2, 3]);
        });

        operation.Should().ThrowExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FallbackGetAllPreservesRequestTriggeredRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var loader = new ReloadingSyncLoader();
        using ILoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildLoading(loader);

        cache.GetAll([1]).Should().Equal(new Dictionary<int, int> { [1] = 10 });
        clock.Advance(TimeSpan.FromSeconds(2));

        cache.GetAll([1]).Should().Equal(new Dictionary<int, int> { [1] = 10 });
        await loader.ReloadStarted.Task.WaitAsync(TestTimeout);
        loader.Release.TrySetResult(11);
        await loader.ReloadCompleted.Task.WaitAsync(TestTimeout);

        loader.ReloadCalls.Should().Be(1);
    }

    [Test]
    public async Task AsyncBulkLoaderSharesPerKeyPromiseWithNormalGet()
    {
        var loader = new AsyncLoader();
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> all = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        Task<int> keyTwo = cache.GetAsync(2).AsTask();

        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );

        (await all).Should().Equal(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        (await keyTwo).Should().Be(20);
        loader.BulkCalls.Should().Be(1);
        cache.TryGet(99, out int prefetched).Should().BeTrue();
        prefetched.Should().Be(990);
    }

    [Test]
    public async Task AsyncBulkCallerCancellationOnlyCancelsItsWait()
    {
        var loader = new AsyncLoader();
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .BuildAsyncLoading(loader);

        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyDictionary<int, int>> canceled = cache
            .GetAllAsync([1, 2], cancellation.Token)
            .AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout, CancellationToken.None);
        Task<int> surviving = cache.GetAsync(2, CancellationToken.None).AsTask();

        await cancellation.CancelAsync();
        Func<Task> waitCanceled = async () => await canceled;
        await waitCanceled.Should().ThrowAsync<OperationCanceledException>();

        loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        (await surviving).Should().Be(20);
        loader.BulkCalls.Should().Be(1);
    }

    [Test]
    public async Task BulkPendingKeyLimitIncludesExistingSingleFlight()
    {
        var loader = new AsyncLoader();
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(16)
            .MaxConcurrentLoads(2)
            .MaxPendingLoadKeys(3)
            .MaximumBulkKeys(3)
            .BuildAsyncLoading(loader);

        Task<int> single = cache.GetAsync(99).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);

        Func<Task> bulk = cache.Awaiting(static current => current.GetAllAsync([1, 2, 3]).AsTask());
        await bulk.Should().ThrowAsync<CacheLoadRejectedException>();

        loader.Release.TrySetResult(new Dictionary<int, int> { [99] = 990 });
        (await single).Should().Be(990);
    }

    private sealed class SyncLoader : IBulkSyncCacheLoader<int, int>
    {
        private int _returnMissingKeyOnce;

        internal int BulkCalls;
        internal int SingleCalls;
        internal bool ReturnMissingKeyOnce
        {
            set => _returnMissingKeyOnce = value ? 1 : 0;
        }

        public int Load(int key)
        {
            Interlocked.Increment(ref SingleCalls);
            return key * 10;
        }

        public IReadOnlyDictionary<int, int> LoadAll(IReadOnlyCollection<int> keys)
        {
            Interlocked.Increment(ref BulkCalls);
            var result = keys.ToDictionary(key => key, key => key * 10);
            result[99] = 990;
            if (Interlocked.Exchange(ref _returnMissingKeyOnce, 0) != 0)
            {
                result.Remove(keys.First());
            }

            return result;
        }
    }

    private sealed class StringSyncLoader : IBulkSyncCacheLoader<string, string>
    {
        internal List<string> Keys { get; } = [];

        public string Load(string key) => key.ToUpperInvariant();

        public IReadOnlyDictionary<string, string> LoadAll(IReadOnlyCollection<string> keys)
        {
            Keys.AddRange(keys);
            return keys.ToDictionary(key => key, key => key.ToUpperInvariant());
        }
    }

    private sealed class ReloadingSyncLoader : ISyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> ReloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReloadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReloadCalls;

        public int Load(int key) => key * 10;

        public int Reload(int key, int oldValue)
        {
            Interlocked.Increment(ref ReloadCalls);
            ReloadStarted.TrySetResult(true);
            try
            {
                return Release.Task.GetAwaiter().GetResult();
            }
            finally
            {
                ReloadCompleted.TrySetResult(true);
            }
        }
    }

    private sealed class AsyncLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int BulkCalls;

        public async Task<int> LoadAsync(int key, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            IReadOnlyDictionary<int, int> values = await Release
                .Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return values[key];
        }

        public async Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref BulkCalls);
            Started.TrySetResult(true);
            return await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
