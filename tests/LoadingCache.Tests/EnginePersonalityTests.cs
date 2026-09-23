using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class EnginePersonalityTests
{
    [Test]
    public async Task BuilderCreatesManualSynchronousCache()
    {
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .Build();
        Assert.NotNull(cache.Policy.Eviction);
        await Assert.That(cache.Policy.Eviction!.Maximum).IsEqualTo(8);
        cache.Put(1, "one");
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("one");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
    }

    [Test]
    public async Task SynchronousLoadingCacheUsesARealSynchronousFactory()
    {
        int calls = 0;
        using ILoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildLoading(key =>
            {
                Interlocked.Increment(ref calls);
                return key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        await Assert.That(cache.Get(7)).IsEqualTo("7");
        await Assert.That(cache.Get(7)).IsEqualTo("7");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ManualAsyncFactoryIsSharedPerKey()
    {
        int calls = 0;
        var entered = NewSignal();
        var release = NewSignal();
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        ValueTask<string> first = cache.GetOrAddAsync(1, Load);
        ValueTask<string> second = cache.GetOrAddAsync(1, Load);
        await entered.Task.WaitAsync(TestTimeout, CancellationToken.None);
        release.TrySetResult(true);
        await Assert.That((await first)).IsEqualTo("1");
        await Assert.That((await second)).IsEqualTo("1");
        await Assert.That(calls).IsEqualTo(1);
        return;
        Task<string> Load(int key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            return CompleteAfterRelease(
                key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                release.Task,
                cancellationToken
            );
        }
    }

    [Test]
    public async Task AsyncLoadingCacheUsesSharedEngine()
    {
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsyncLoading(
                async (key, _) =>
                {
                    Interlocked.Increment(ref calls);
                    await Task.Yield();
                    return key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            );
        await Assert.That((await cache.GetAsync(3))).IsEqualTo("3");
        await Assert.That((await cache.GetAsync(3))).IsEqualTo("3");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AsyncCallerCancellationOnlyCancelsThatWaiter()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await using IAsyncCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        using var canceled = new CancellationTokenSource();
        ValueTask<int> first = cache.GetOrAddAsync(1, Load, canceled.Token);
        await entered.Task.WaitAsync(TestTimeout, CancellationToken.None);
        ValueTask<int> second = cache.GetOrAddAsync(1, Load, CancellationToken.None);
        await canceled.CancelAsync();
        Func<Task<int>> waitFirst = first.AsTask;
        await Assert.That(waitFirst).Throws<OperationCanceledException>();
        release.TrySetResult(true);
        await Assert.That((await second)).IsEqualTo(42);
        return;
        Task<int> Load(int _, CancellationToken cancellationToken)
        {
            entered.TrySetResult(true);
            return CompleteAfterRelease(42, release.Task, cancellationToken);
        }
    }

    [Test]
    public async Task WeightedBuilderRequiresAnExplicitResidentCount()
    {
        Action action = () =>
            CacheBuilder
                .Create<int, string>()
                .MaximumWeight(100)
                .Weigher((_, value) => value.Length)
                .Build();
        await Assert.That(action).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task MaximumSizeAndMaximumWeightAreMutuallyExclusive()
    {
        Action action = () =>
            CacheBuilder
                .Create<int, string>()
                .MaximumSize(10)
                .MaximumWeight(10)
                .MaximumResidentCount(10)
                .Weigher((_, _) => 1)
                .Build();
        await Assert.That(action).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task WeigherRunsOutsideMutationGateAndClearKeepsCurrentEpochCommit()
    {
        var weigherEntered = NewSignal();
        using var releaseWeigher = new ManualResetEventSlim(false);
        Func<TimeSpan, bool> waitForWeigherRelease = releaseWeigher.Wait;
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumWeight(100)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(8)
            .Weigher(
                (_, item) =>
                {
                    weigherEntered.TrySetResult(true);
                    return waitForWeigherRelease(TestTimeout)
                        ? item.Length
                        : throw new TimeoutException("The controlled weigher was not released.");
                }
            )
            .Build();
        Task put = Task.Factory.StartNew(
            static state => ((ICache<int, string>)state!).Put(1, "value"),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await weigherEntered.Task.WaitAsync(TestTimeout, CancellationToken.None);
            cache.Clear();
            releaseWeigher.Set();
            await put.WaitAsync(TestTimeout, CancellationToken.None);
        }
        finally
        {
            releaseWeigher.Set();
            await put.WaitAsync(TestTimeout, CancellationToken.None);
        }

        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("value");
    }

    [Test]
    public async Task WeightedPolicyUsesZeroWeightEntriesWithinResidentCountBound()
    {
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumWeight(1)
            .MaximumResidentCount(2)
            .MaxConcurrentLoads(8)
            .Weigher((_, _) => 0)
            .BuildAsyncLoading(
                (key, _) =>
                    Task.FromResult(key.ToString(System.Globalization.CultureInfo.InvariantCulture))
            );
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("1");
        await Assert.That((await cache.GetAsync(2))).IsEqualTo("2");
        await Assert.That((await cache.GetAsync(3))).IsEqualTo("3");
        // Capacity is a quiescent bound; ready publication precedes policy replay.
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task SameKeyAsyncLoadingSurvivesSetFence()
    {
        var load = NewSignal<string>();
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsyncLoading((_, _) => load.Task);
        Task<string> pending = cache.GetAsync(1).AsTask();
        cache.Set(1, "new");
        load.TrySetResult("old");
        await Assert.That((await pending)).IsEqualTo("old");
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("new");
    }

    [Test]
    public async Task SameKeyAsyncLoadingSurvivesClearFence()
    {
        var oldLoad = NewSignal<string>();
        var newLoad = NewSignal<string>();
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsyncLoading(
                (_, _) => Interlocked.Increment(ref calls) == 1 ? oldLoad.Task : newLoad.Task
            );
        Task<string> old = cache.GetAsync(1).AsTask();
        cache.Clear();
        Task<string> current = cache.GetAsync(1).AsTask();
        newLoad.TrySetResult("new");
        oldLoad.TrySetResult("old");
        await Assert.That((await current)).IsEqualTo("new");
        await Assert.That((await old)).IsEqualTo("old");
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("new");
    }

    [Test]
    public async Task DifferentKeysCanRunAsyncFactoriesConcurrently()
    {
        int active = 0;
        int maximum = 0;
        var release = NewSignal();
        await using IAsyncCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .BuildAsync();
        Task<int> first = cache.GetOrAddAsync(1, Load).AsTask();
        Task<int> second = cache.GetOrAddAsync(2, Load).AsTask();
        try
        {
            await Eventually(() => Volatile.Read(ref maximum) == 2);
            release.TrySetResult(true);
            await Assert.That((await first)).IsEqualTo(1);
            await Assert.That((await second)).IsEqualTo(2);
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TestTimeout, CancellationToken.None);
        }

        return;
        async Task<int> Load(int key, CancellationToken _)
        {
            int now = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, now);
            try
            {
                await release.Task;
                return key;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    [Test]
    public async Task ReadyPublicationDoesNotExposeAnIncompleteEntrySnapshot()
    {
        await using BlockingTestHook publication = new(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforeReadyPublish = publication.Invoke };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TestHooks = hooks,
            }
        );
        var source = NewSignal<string>();
        await using var cache = new AsyncLoadingCache<int, string>(engine, (_, _) => source.Task);
        Task<string> load = cache.GetAsync(1).AsTask();
        Task<bool>? probe = null;
        try
        {
            source.SetResult("1");
            await publication.Entered.WaitAsync(TestTimeout, CancellationToken.None);
            probe = Task.Factory.StartNew(
                static state => ((AsyncLoadingCache<int, string>)state!).TryGet(1, out _),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await Assert
                .That((await probe.WaitAsync(TestTimeout, CancellationToken.None)))
                .IsFalse();
            publication.Release();
            await Assert
                .That((await load.WaitAsync(TestTimeout, CancellationToken.None)))
                .IsEqualTo("1");
            await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
            await Assert.That(value).IsEqualTo("1");
            await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(load, probe ?? Task.CompletedTask)
                .WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    [Test]
    public async Task PendingTaskPutCanBeStartedByAJoinerWhileInstallerIsPaused()
    {
        await using BlockingTestHook installation = new(TestTimeout);
        var hooks = new LoadingCacheTestHooks { AfterFlightInstalled = installation.Invoke };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TestHooks = hooks,
            }
        );
        await using var cache = new AsyncCache<int, string>(engine);
        var source = NewSignal<string>();
        Task installer = Task.Factory.StartNew(
            static state =>
            {
                (AsyncCache<int, string> current, Task<string> pending) = ((
                    AsyncCache<int, string>,
                    Task<string>
                ))
                    state!;
                current.Put(1, pending);
            },
            (cache, source.Task),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await installation.Entered.WaitAsync(TestTimeout, CancellationToken.None);
            ValueTask<string> joined = cache.GetOrAddAsync(
                1,
                (_, _) => throw new AssertionException("The stored task was not joined.")
            );
            source.SetResult("stored");
            installation.Release();
            await installer.WaitAsync(TestTimeout, CancellationToken.None);
            await Assert
                .That((await joined.AsTask().WaitAsync(TestTimeout, CancellationToken.None)))
                .IsEqualTo("stored");
        }
        finally
        {
            installation.Release();
            await installer.WaitAsync(TestTimeout, CancellationToken.None);
        }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T> CompleteAfterRelease<T>(
        T value,
        Task release,
        CancellationToken cancellationToken = default
    )
    {
        await release.WaitAsync(cancellationToken);
        return value;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionException(
                    "The controlled concurrency condition was not reached."
                );
            }

            await Task.Yield();
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
