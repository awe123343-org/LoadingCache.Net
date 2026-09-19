using FluentAssertions;
using NUnit.Framework;

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

        manual.GetOrAdd(1, static key => key).Should().Be(1);
        loading.Get(2).Should().Be(2);
        (await asyncManual.GetOrAddAsync(3, static (key, _) => Task.FromResult(key)))
            .Should()
            .Be(3);
        (await asyncLoading.GetAsync(4)).Should().Be(4);
    }

    [TestCase(0)]
    [TestCase(1)]
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
            loader.Calls.Should().Be(distinctKeys);
            pending.Should().OnlyContain(static operation => !operation.IsCompleted);

            await cancellation.CancelAsync();
            Func<Task> waitCanceled = () =>
                canceledJoin.WaitAsync(TestTimeout, CancellationToken.None);
            await waitCanceled.Should().ThrowAsync<OperationCanceledException>();
            loader.Cancellation.IsCancellationRequested.Should().BeFalse();
            pending.Should().OnlyContain(static operation => !operation.IsCompleted);

            loader.Release.TrySetResult(42);
            int[] results = await Task.WhenAll(pending)
                .WaitAsync(TestTimeout, CancellationToken.None);
            results.Should().OnlyContain(static value => value == 42);
            loader.Calls.Should().Be(distinctKeys);
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

    [TestCase(false)]
    [TestCase(true)]
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
            (await cache.GetAsync(99)).Should().Be(99);
            rejectedOperation = cache.GetAsync(2).AsTask();
            Func<Task> rejected = () => rejectedOperation.WaitAsync(TestTimeout);
            await rejected.Should().ThrowExactlyAsync<CacheLoadRejectedException>();
            loader.Calls.Should().Be(1);

            loader.Release.TrySetResult(42);
            int[] results = await Task.WhenAll(pending).WaitAsync(TestTimeout);
            results.Should().Equal(42, 42);
            (await cache.GetAsync(2)).Should().Be(42);
            loader.Calls.Should().Be(2);
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

    [TestCase(0)]
    [TestCase(-1)]
    public void ExplicitNonPositiveBuilderConcurrencyLimitRemainsInvalid(int maximum)
    {
        Action configure = () => CacheBuilder.Create<int, int>().MaxConcurrentLoads(maximum);

        configure.Should().ThrowExactly<ArgumentOutOfRangeException>();
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
