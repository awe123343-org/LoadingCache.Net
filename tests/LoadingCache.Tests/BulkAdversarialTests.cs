using Microsoft.Extensions.Time.Testing;

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
        await Assert
            .That(result[1])
            .IsEqualTo(10)
            .Because("the existing waiter may observe its original flight");
        await Assert.That(cache.TryGet(1, out int current)).IsTrue();
        await Assert.That(current).IsEqualTo(101);
        await Assert.That(cache.TryGet(2, out int loaded)).IsTrue();
        await Assert.That(loaded).IsEqualTo(20);
    }

    [Test]
    public async Task InvalidateDuringPendingBulkDoesNotResurrectTheInvalidatedKey()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        loader.Release.TrySetResult(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        IReadOnlyDictionary<int, int> result = await pending.WaitAsync(TestTimeout);
        await Assert
            .That(result[1])
            .IsEqualTo(10)
            .Because("the original waiter already joined the old flight");
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(2, out int loaded)).IsTrue();
        await Assert.That(loaded).IsEqualTo(20);
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
        await Assert
            .That(result[1])
            .IsEqualTo(10)
            .Because("the original waiter may observe its old epoch");
        await Assert.That(cache.TryGet(1, out int current)).IsTrue();
        await Assert.That(current).IsEqualTo(101);
        await Assert.That(cache.TryGet(2, out _)).IsFalse();
    }

    [Test]
    public async Task PrefetchIsFencedByInvalidateOfAnAbsentKey()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.Invalidate(99)).IsFalse();
        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );
        await pending.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet(99, out _)).IsFalse();
    }

    [Test]
    public async Task PrefetchSurvivesAnUnrelatedPut()
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
        await Assert.That(cache.TryGet(77, out int unrelated)).IsTrue();
        await Assert.That(unrelated).IsEqualTo(770);
        await Assert.That(cache.TryGet(99, out int prefetched)).IsTrue();
        await Assert.That(prefetched).IsEqualTo(990);
    }

    [Test]
    public async Task PrefetchSurvivesAnUnrelatedInvalidate()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.Invalidate(77)).IsFalse();
        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );
        await pending.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet(99, out int prefetched)).IsTrue();
        await Assert.That(prefetched).IsEqualTo(990);
    }

    [Test]
    public async Task PrefetchIsFencedWhenItsKeyIsSetThenInvalidated()
    {
        var loader = new GatedBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        cache.Set(99, 991);
        await Assert.That(cache.Invalidate(99)).IsTrue();
        loader.Release.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );
        await pending.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet(99, out _)).IsFalse();
    }

    [Test]
    public async Task BulkMutationLedgerRotatesAfterOverflowForNewActiveBulk()
    {
        var loader = new MultiPhaseBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(2048)
            .MaxConcurrentLoads(2)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<int, int>> first = cache.GetAllAsync([1, 2]).AsTask();
        await loader.FirstStarted.Task.WaitAsync(TestTimeout);
        for (int index = 0; index <= 1024; index++)
        {
            cache.Set(1_000 + index, index);
        }

        Task<IReadOnlyDictionary<int, int>> second = cache.GetAllAsync([3, 4]).AsTask();
        await loader.SecondStarted.Task.WaitAsync(TestTimeout);
        loader.SecondRelease.TrySetResult(
            new Dictionary<int, int>
            {
                [3] = 30,
                [4] = 40,
                [99] = 990,
            }
        );
        await second.WaitAsync(TestTimeout);
        await Assert
            .That(cache.TryGet(99, out int newPrefetched))
            .IsTrue()
            .Because("a new bulk may use the rotated ledger while the old bulk remains active");
        await Assert.That(newPrefetched).IsEqualTo(990);
        loader.FirstRelease.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [98] = 980,
            }
        );
        await first.WaitAsync(TestTimeout);
        await Assert
            .That(cache.TryGet(98, out _))
            .IsFalse()
            .Because("the pre-rotation bulk must fail closed");
        Func<CacheStatistics> readStatistics = cache.GetStatistics;
        await Assert
            .That(SpinWait.SpinUntil(() => readStatistics().InFlightLoads == 0, TestTimeout))
            .IsTrue()
            .Because("the final active bulk must retire before the ledger is reused");
        Task<IReadOnlyDictionary<int, int>> third = cache.GetAllAsync([5, 6]).AsTask();
        await loader.ThirdStarted.Task.WaitAsync(TestTimeout);
        loader.ThirdRelease.TrySetResult(
            new Dictionary<int, int>
            {
                [5] = 50,
                [6] = 60,
                [97] = 970,
            }
        );
        await third.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet(97, out int postRetirementPrefetch)).IsTrue();
        await Assert.That(postRetirementPrefetch).IsEqualTo(970);
    }

    [Test]
    public async Task LongMaxBulkMutationSequenceRotatesWithoutWrapping()
    {
        var loader = new MultiPhaseBulkLoader();
        CacheBuilder<int, int> builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(32)
            .MaxConcurrentLoads(2)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4);
        CacheEngine<int, int> engine = builder.CreateEngine(hasFixedLoader: true, isAsync: true);
        await using var cache = new AsyncLoadingCache<int, int>(
            engine,
            loader.LoadAsync,
            bulkLoader: loader.LoadAllAsync
        );
        Task<IReadOnlyDictionary<int, int>> pending = cache.GetAllAsync([1, 2]).AsTask();
        await loader.FirstStarted.Task.WaitAsync(TestTimeout);
        engine.SetBulkMutationSequenceForTesting(long.MaxValue);
        cache.Set(77, 770);
        loader.FirstRelease.TrySetResult(
            new Dictionary<int, int>
            {
                [1] = 10,
                [2] = 20,
                [99] = 990,
            }
        );
        await pending.WaitAsync(TestTimeout);
        await Assert
            .That(cache.TryGet(99, out _))
            .IsFalse()
            .Because("long.MaxValue must rotate, never wrap to a publishable sequence");
    }

    [Test]
    public async Task BulkMutationLedgerUsesTheConfiguredComparer()
    {
        var loader = new StringGatedBulkLoader();
        await using IAsyncLoadingCache<string, int> cache = CacheBuilder
            .Create<string, int>()
            .MaximumSize(32)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .BuildAsyncLoading(loader);
        Task<IReadOnlyDictionary<string, int>> pending = cache.GetAllAsync(["a", "b"]).AsTask();
        await loader.Started.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.Invalidate("C")).IsFalse();
        loader.Release.TrySetResult(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = 10,
                ["b"] = 20,
                ["c"] = 30,
            }
        );
        await pending.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet("c", out _)).IsFalse();
    }

    [Test]
    public async Task WeakKeyBulkMutationLedgerDoesNotRetainAnAbsentMutationKey()
    {
        var loader = new WeakKeyGatedBulkLoader();
        await using IAsyncLoadingCache<WeakBulkKey, int> cache = CacheBuilder
            .Create<WeakBulkKey, int>()
            .MaximumSize(32)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .WeakKeys()
            .BuildAsyncLoading(loader);
        (
            Task<IReadOnlyDictionary<WeakBulkKey, int>> pending,
            WeakBulkKey first,
            WeakBulkKey second
        ) = StartWeakBulk(cache, loader);
        await loader.Started.Task.WaitAsync(TestTimeout);
        WeakReference mutationKey = CreateAndInvalidateAbsentWeakKey(cache);
        ForceCollection(mutationKey);
        await Assert
            .That(mutationKey.IsAlive)
            .IsFalse()
            .Because("the active bulk ledger stores only a weak wrapper");
        loader.Release.TrySetResult(
            new Dictionary<WeakBulkKey, int> { [first] = 10, [second] = 20 }
        );
        await pending.WaitAsync(TestTimeout);
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
        try
        {
            await loader.Started.Task.WaitAsync(TestTimeout);
            clock.Advance(TimeSpan.FromSeconds(1));
            Func<Task> waitFirst = async () => await first.WaitAsync(TestTimeout);
            await Assert.That(waitFirst).ThrowsExactly<TimeoutException>();
            await Assert.That(cache.GetStatistics().InFlightLoads).IsEqualTo(1);
            Func<Task> rejected = () => cache.GetAllAsync([3, 4]).AsTask();
            await Assert.That(rejected).ThrowsExactly<CacheLoadRejectedException>();
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
            Func<CacheStatistics> readStatistics = cache.GetStatistics;
            await Assert
                .That(SpinWait.SpinUntil(() => readStatistics().InFlightLoads == 0, TestTimeout))
                .IsTrue();
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            await Assert.That(cache.TryGet(2, out _)).IsFalse();
            IReadOnlyDictionary<int, int> second = await cache.GetAllAsync([3, 4]);
            await Assert
                .That(second)
                .IsEquivalentTo(new Dictionary<int, int> { [3] = 30, [4] = 40 });
            await Assert.That(loader.BulkCalls).IsEqualTo(2);
        }
        finally
        {
            // This backend deliberately ignores cancellation, so shutdown cannot release it.
            loader.Release.TrySetResult(
                new Dictionary<int, int>
                {
                    [1] = 10,
                    [2] = 20,
                    [3] = 30,
                    [4] = 40,
                }
            );
            try
            {
                await Task.WhenAll(loader.Finished.Task, first).WaitAsync(TestTimeout);
            }
            catch (TimeoutException) when (first.IsCompleted && loader.Finished.Task.IsCompleted)
            {
                // Observe the load timeout while preserving a cleanup watchdog failure.
            }
        }
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
            await Assert.That(waitBulk).ThrowsExactly<ObjectDisposedException>();
            Func<Task> waitJoined = async () => await joined.WaitAsync(TestTimeout);
            await Assert.That(waitJoined).ThrowsExactly<ObjectDisposedException>();
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
            await Assert.That(wait).ThrowsExactly<LoadingCacheReentrancyException>();
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
        Func<Task> operation = () => cache.GetAllAsync(input).AsTask();
        await Assert.That(operation).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(input.MoveNextCalls).IsEqualTo(5);
        await Assert.That(loader.BulkCalls).IsEqualTo(0);
    }

    [Test]
    public async Task MissingBulkOutputDoesNotPublishAnyRequestedValue()
    {
        var loader = new ResultBulkLoader<int>(_ => new Dictionary<int, int> { [1] = 10 });
        await using IAsyncLoadingCache<int, int> cache = CreateBuilder().BuildAsyncLoading(loader);
        Func<Task> operation = () => cache.GetAllAsync([1, 2]).AsTask();
        await Assert.That(operation).ThrowsExactly<InvalidOperationException>();
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(2, out _)).IsFalse();
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
        Func<Task> operation = () => cache.GetAllAsync([1, 2]).AsTask();
        await Assert.That(operation).ThrowsExactly<ArgumentNullException>();
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(2, out _)).IsFalse();
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
        await Assert.That(result).IsEquivalentTo(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        await Assert.That(cache.TryGet(1, out int first)).IsTrue();
        await Assert.That(first).IsEqualTo(10);
        await Assert.That(cache.TryGet(99, out int prefetched)).IsTrue();
        await Assert.That(prefetched).IsEqualTo(990);
        await Assert.That(cache.TryGet(100, out _)).IsFalse();
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

    private sealed class MultiPhaseBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> FirstStarted { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> FirstRelease { get; } =
            Signal<IReadOnlyDictionary<int, int>>();
        internal TaskCompletionSource<bool> SecondStarted { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> SecondRelease { get; } =
            Signal<IReadOnlyDictionary<int, int>>();
        internal TaskCompletionSource<bool> ThirdStarted { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> ThirdRelease { get; } =
            Signal<IReadOnlyDictionary<int, int>>();

        private int _calls;

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public async Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            int call = Interlocked.Increment(ref _calls);
            switch (call)
            {
                case 1:
                    FirstStarted.TrySetResult(true);
                    return await FirstRelease
                        .Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                case 2:
                    SecondStarted.TrySetResult(true);
                    return await SecondRelease
                        .Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                default:
                    ThirdStarted.TrySetResult(true);
                    return await ThirdRelease
                        .Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
        }
    }

    private sealed class StringGatedBulkLoader : IBulkAsyncCacheLoader<string, int>
    {
        internal TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<string, int>> Release { get; } =
            Signal<IReadOnlyDictionary<string, int>>();

        public Task<int> LoadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public async Task<IReadOnlyDictionary<string, int>> LoadAllAsync(
            IReadOnlyCollection<string> keys,
            CancellationToken cancellationToken
        )
        {
            Started.TrySetResult(true);
            return await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WeakKeyGatedBulkLoader : IBulkAsyncCacheLoader<WeakBulkKey, int>
    {
        internal TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<WeakBulkKey, int>> Release { get; } =
            Signal<IReadOnlyDictionary<WeakBulkKey, int>>();

        public Task<int> LoadAsync(WeakBulkKey key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException("The bulk test must use LoadAllAsync.")
            );

        public async Task<IReadOnlyDictionary<WeakBulkKey, int>> LoadAllAsync(
            IReadOnlyCollection<WeakBulkKey> keys,
            CancellationToken cancellationToken
        )
        {
            Started.TrySetResult(true);
            return await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed class WeakBulkKey;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static (
        Task<IReadOnlyDictionary<WeakBulkKey, int>> Pending,
        WeakBulkKey First,
        WeakBulkKey Second
    ) StartWeakBulk(IAsyncLoadingCache<WeakBulkKey, int> cache, WeakKeyGatedBulkLoader loader)
    {
        WeakBulkKey first = new();
        WeakBulkKey second = new();
        Task<IReadOnlyDictionary<WeakBulkKey, int>> pending = cache
            .GetAllAsync([first, second])
            .AsTask();
        GC.KeepAlive(loader);
        return (pending, first, second);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static WeakReference CreateAndInvalidateAbsentWeakKey(
        IAsyncLoadingCache<WeakBulkKey, int> cache
    )
    {
        WeakBulkKey key = new();
        if (cache.Invalidate(key))
            Assert.Fail("Expected cache.Invalidate(key) to be false ().");
        return new WeakReference(key);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Yield();
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
