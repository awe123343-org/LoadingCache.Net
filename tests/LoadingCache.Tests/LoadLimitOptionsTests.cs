namespace LoadingCache.Tests;

public sealed class LoadLimitOptionsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task AllBuilderPersonalitiesLoadWithoutAConcurrencyLimit()
    {
        using ICache<int, int> manual = CacheBuilder.Create<int, int>().MaximumSize(1).Build();
        using ILoadingCache<int, int> loading = CacheBuilder
            .Create<int, int>()
            .MaximumSize(1)
            .BuildLoading(static key => key);
        await using IAsyncCache<int, int> asyncManual = CacheBuilder
            .Create<int, int>()
            .MaximumSize(1)
            .BuildAsync();
        await using IAsyncLoadingCache<int, int> asyncLoading = CacheBuilder
            .Create<int, int>()
            .MaximumSize(1)
            .BuildAsyncLoading(static (key, _) => Task.FromResult(key));
        await Assert.That(manual.GetOrAdd(1, static key => key)).IsEqualTo(1);
        await Assert.That(loading.Get(2)).IsEqualTo(2);
        await Assert
            .That((await asyncManual.GetOrAddAsync(3, static (key, _) => Task.FromResult(key))))
            .IsEqualTo(3);
        await Assert.That((await asyncLoading.GetAsync(4))).IsEqualTo(4);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task UnsetLimitAllowsDistinctFlightsAndPreservesJoinsAndCallerCancellation(
        int configuration
    )
    {
        const int distinctKeys = 16;
        var loader = new GatedLoader();
        await using IAsyncCache<int, int> cache = configuration switch
        {
            0 => CacheBuilder.Create<int, int>().MaximumSize(1).BuildAsync(),
            _ => CacheBuilder.Create<int, int>().MaximumSize(1).BuildAsyncLoading(loader.LoadAsync),
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new List<Task<int>>();
        Task<int>? canceledJoin = null;
        try
        {
            for (int key = 0; key < distinctKeys; key++)
            {
                pending.Add(Get(cache, loader, key, CancellationToken.None).AsTask());
            }

            pending.Add(Get(cache, loader, 0, CancellationToken.None).AsTask());
            canceledJoin = Get(cache, loader, 0, cancellation.Token).AsTask();
            await Assert.That(loader.Calls).IsEqualTo(distinctKeys);
            await Assert.That(pending).All(static operation => !operation.IsCompleted);
            await cancellation.CancelAsync();
            Func<Task> waitCanceled = () =>
                canceledJoin.WaitAsync(TestTimeout, CancellationToken.None);
            await Assert.That(waitCanceled).Throws<OperationCanceledException>();
            await Assert.That(loader.Cancellation.IsCancellationRequested).IsFalse();
            await Assert.That(pending).All(static operation => !operation.IsCompleted);
            loader.Release.TrySetResult(42);
            int[] results = await Task.WhenAll(pending)
                .WaitAsync(TestTimeout, CancellationToken.None);
            await Assert.That(results).All(static value => value == 42);
            await Assert.That(loader.Calls).IsEqualTo(distinctKeys);
        }
        finally
        {
            await cancellation.CancelAsync();
            loader.Release.TrySetResult(42);
            await Task.WhenAll(pending).WaitAsync(TestTimeout, CancellationToken.None);
            if (canceledJoin is not null)
            {
                try
                {
                    await canceledJoin.WaitAsync(TestTimeout, CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // This caller owns only its canceled wait, not the shared load.
                }
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitConcurrencyOrPendingLimitRejectsOnlyNewFlights(bool pendingOnly)
    {
        var loader = new GatedLoader();
        CacheBuilder<int, int> builder = CacheBuilder.Create<int, int>().MaximumSize(4);
        if (pendingOnly)
        {
            builder.MaxPendingLoadKeys(1);
        }
        else
        {
            builder.MaxConcurrentLoads(1);
        }

        await using IAsyncLoadingCache<int, int> cache = builder.BuildAsyncLoading(
            loader.LoadAsync
        );
        cache.Set(99, 99);
        var pending = new List<Task<int>>();
        Task<int>? rejectedOperation = null;
        try
        {
            pending.Add(cache.GetAsync(1).AsTask());
            pending.Add(cache.GetAsync(1).AsTask());
            await Assert.That((await cache.GetAsync(99))).IsEqualTo(99);
            rejectedOperation = cache.GetAsync(2).AsTask();
            Func<Task> rejected = () => rejectedOperation.WaitAsync(TestTimeout);
            await Assert.That(rejected).ThrowsExactly<CacheLoadRejectedException>();
            await Assert.That(loader.Calls).IsEqualTo(1);
            loader.Release.TrySetResult(42);
            int[] results = await Task.WhenAll(pending).WaitAsync(TestTimeout);
            await Assert
                .That(results)
                .IsEquivalentTo([42, 42], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That((await cache.GetAsync(2))).IsEqualTo(42);
            await Assert.That(loader.Calls).IsEqualTo(2);
        }
        finally
        {
            loader.Release.TrySetResult(42);
            await Task.WhenAll(pending).WaitAsync(TestTimeout);
            if (rejectedOperation is not null)
            {
                try
                {
                    await rejectedOperation.WaitAsync(TestTimeout);
                }
                catch (CacheLoadRejectedException)
                {
                    // The opt-in limit rejects this distinct flight immediately.
                }
            }
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task ExplicitNonPositiveBuilderConcurrencyLimitRemainsInvalid(int maximum)
    {
        Action configure = () => CacheBuilder.Create<int, int>().MaxConcurrentLoads(maximum);
        await Assert.That(configure).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    private static ValueTask<int> Get(
        IAsyncCache<int, int> cache,
        GatedLoader loader,
        int key,
        CancellationToken cancellationToken = default
    ) =>
        cache is IAsyncLoadingCache<int, int> loading
            ? loading.GetAsync(key, cancellationToken)
            : cache.GetOrAddAsync(key, loader.LoadAsync, cancellationToken);

    private sealed class GatedLoader
    {
        internal TaskCompletionSource<int> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls;
        internal CancellationToken Cancellation;

        internal Task<int> LoadAsync(int _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                Cancellation = cancellationToken;
            }

            return Release.Task;
        }
    }
}
