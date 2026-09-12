using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class EngineRefreshTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public void RefreshRequiresAFixedLoaderPersonality()
    {
        Action sync = () =>
            CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(2)
                .RefreshAfterWrite(TimeSpan.FromSeconds(1))
                .Build();
        Action async = () =>
            CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(2)
                .RefreshAfterWrite(TimeSpan.FromSeconds(1))
                .BuildAsync();

        sync.Should().ThrowExactly<InvalidOperationException>();
        async.Should().ThrowExactly<InvalidOperationException>();
    }

    [Test]
    public async Task AutomaticRefreshReturnsFreshOldValueAndCoalesces()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        int loads = 0;
        int reloads = 0;
        var loader = new TestLoader(
            (key, _) =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult($"v{key}");
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads);
                reloadEntered.TrySetResult(true);
                return releaseReload.Task;
            }
        );

        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("v1");
        clock.Advance(TimeSpan.FromSeconds(1));

        (await cache.GetAsync(1)).Should().Be("v1");
        (await cache.GetAsync(1)).Should().Be("v1");
        await reloadEntered.Task.WaitAsync(Watchdog);
        Volatile.Read(ref reloads).Should().Be(1);

        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        releaseReload.SetResult("v2");
        (await joinedRefresh.WaitAsync(Watchdog)).Should().Be("v2");
        Volatile.Read(ref loads).Should().Be(1);
    }

    [Test]
    public async Task TryGetDoesNotTriggerAutomaticRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int reloads = 0;
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads);
                return Task.FromResult("new");
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("old");
        Volatile.Read(ref reloads).Should().Be(0);
    }

    [Test]
    public async Task HardExpiredValueJoinsAnOngoingRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        int loads = 0;
        int reloads = 0;
        var loader = new TestLoader(
            (_, _) =>
            {
                Interlocked.Increment(ref loads);
                return Task.FromResult("old");
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads);
                reloadEntered.TrySetResult(true);
                return releaseReload.Task;
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromSeconds(2))
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("old");
        await reloadEntered.Task.WaitAsync(Watchdog);

        clock.Advance(TimeSpan.FromSeconds(1));
        Task<string> joined = cache.GetAsync(1).AsTask();
        joined.IsCompleted.Should().BeFalse();
        releaseReload.SetResult("new");
        (await joined.WaitAsync(Watchdog)).Should().Be("new");
        Volatile.Read(ref loads).Should().Be(1);
        Volatile.Read(ref reloads).Should().Be(1);
    }

    [Test]
    public async Task FailedAutomaticRefreshKeepsValueAndHonorsBackoff()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int reloads = 0;
        var firstReloadEntered = NewSignal();
        var secondReloadEntered = NewSignal();
        var firstFailure = NewSignal<string>();
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                int call = Interlocked.Increment(ref reloads);
                (call == 1 ? firstReloadEntered : secondReloadEntered).TrySetResult(true);
                return call == 1
                    ? firstFailure.Task
                    : Task.FromException<string>(new InvalidOperationException("reload"));
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .RefreshFailureBackoff(TimeSpan.FromSeconds(5))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("old");
        await firstReloadEntered.Task.WaitAsync(Watchdog);
        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        Volatile.Read(ref reloads).Should().Be(1);
        firstFailure.SetException(new InvalidOperationException("reload"));
        await FluentActions
            .Awaiting(() => joinedRefresh)
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();

        (await cache.GetAsync(1)).Should().Be("old");
        Volatile.Read(ref reloads).Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(5));
        (await cache.GetAsync(1)).Should().Be("old");
        await secondReloadEntered.Task.WaitAsync(Watchdog);
    }

    [Test]
    public async Task SetFencesARefreshingCompletion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        var reloadCompleted = NewSignal();
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                reloadEntered.TrySetResult(true);
                return CompleteAndSignal(releaseReload.Task, reloadCompleted);
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("old");
        await reloadEntered.Task.WaitAsync(Watchdog);

        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        cache.Set(1, "set");
        releaseReload.SetResult("late");
        await reloadCompleted.Task.WaitAsync(Watchdog);
        await joinedRefresh.WaitAsync(Watchdog);
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("set");
    }

    [Test]
    public async Task ExplicitRefreshSameKeyFailsFastInsideItsLoadChain()
    {
        IAsyncLoadingCache<int, string>? cache = null;
        cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildAsyncLoading(
                async (key, cancellationToken) =>
                    await cache!.RefreshAsync(key, cancellationToken).ConfigureAwait(false)
            );

        await using (cache)
        {
            await FluentActions
                .Awaiting(() => cache.GetAsync(1).AsTask())
                .Should()
                .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        }
    }

    [Test]
    public async Task ExplicitRefreshKToJToKCycleFailsFast()
    {
        IAsyncLoadingCache<string, int>? cache = null;
        cache = CacheBuilder
            .Create<string, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .BuildAsyncLoading(
                async (key, _) =>
                {
                    string dependency = key == "K" ? "J" : "K";
                    return await cache!
                        .RefreshAsync(dependency, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            );

        await using (cache)
        {
            await FluentActions
                .Awaiting(() => cache.GetAsync("K").AsTask())
                .Should()
                .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        }
    }

    [Test]
    public void SynchronousRefreshSameKeyFailsFastInsideItsLoadChain()
    {
        ILoadingCache<int, string>? cache = null;
        cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildLoading(key => cache!.RefreshAsync(key).GetAwaiter().GetResult());

        using (cache)
        {
            FluentActions
                .Invoking(() => cache.Get(1))
                .Should()
                .ThrowExactly<LoadingCacheReentrancyException>();
        }
    }

    [Test]
    public async Task SynchronousRefreshUsesAnAsyncMirrorForTheSharedSyncFlight()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var loader = new SyncTestLoader(
            () => "old",
            () =>
            {
                entered.TrySetResult(true);
                release.Task.GetAwaiter().GetResult();
                return "new";
            }
        );
        using ILoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildLoading(loader);

        cache.Get(1).Should().Be("old");
        Task<string> refresh = cache.RefreshAsync(1);
        await entered.Task.WaitAsync(Watchdog);
        refresh.IsCompleted.Should().BeFalse();
        release.TrySetResult(true);
        (await refresh.WaitAsync(Watchdog)).Should().Be("new");
    }

    private static async Task<string> CompleteAndSignal(
        Task<string> source,
        TaskCompletionSource<bool> completed
    )
    {
        string value = await source.ConfigureAwait(false);
        completed.TrySetResult(true);
        return value;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TestLoader : IAsyncCacheLoader<int, string>
    {
        private readonly Func<int, CancellationToken, Task<string>> _load;
        private readonly Func<int, string, CancellationToken, Task<string>> _reload;

        internal TestLoader(
            Func<int, CancellationToken, Task<string>> load,
            Func<int, string, CancellationToken, Task<string>> reload
        )
        {
            _load = load;
            _reload = reload;
        }

        public Task<string> LoadAsync(int key, CancellationToken cancellationToken) =>
            _load(key, cancellationToken);

        public Task<string> ReloadAsync(
            int key,
            string oldValue,
            CancellationToken cancellationToken
        ) => _reload(key, oldValue, cancellationToken);
    }

    private sealed class SyncTestLoader : ISyncCacheLoader<int, string>
    {
        private readonly Func<string> _load;
        private readonly Func<string> _reload;

        internal SyncTestLoader(Func<string> load, Func<string> reload)
        {
            _load = load;
            _reload = reload;
        }

        public string Load(int key) => _load();

        public string Reload(int key, string oldValue) => _reload();
    }
}
