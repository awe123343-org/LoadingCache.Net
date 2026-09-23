using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class EngineRefreshTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RefreshRequiresAFixedLoaderPersonality()
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
        await Assert.That(sync).ThrowsExactly<InvalidOperationException>();
        await Assert.That(async).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task AutomaticRefreshReturnsFreshOldValueAndCoalesces()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        var loads = new System.Runtime.CompilerServices.StrongBox<int>();
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var loader = new TestLoader(
            (key, _) =>
            {
                Interlocked.Increment(ref loads.Value);
                return Task.FromResult($"v{key}");
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads.Value);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("v1");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("v1");
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("v1");
        await reloadEntered.Task.WaitAsync(Watchdog);
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(1);
        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        releaseReload.SetResult("v2");
        await Assert.That((await joinedRefresh.WaitAsync(Watchdog))).IsEqualTo("v2");
        await Assert.That(Volatile.Read(ref loads.Value)).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetDoesNotTriggerAutomaticRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads.Value);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("old");
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(0);
    }

    [Test]
    public async Task HardExpiredValueJoinsAnOngoingRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        var loads = new System.Runtime.CompilerServices.StrongBox<int>();
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var loader = new TestLoader(
            (_, _) =>
            {
                Interlocked.Increment(ref loads.Value);
                return Task.FromResult("old");
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref reloads.Value);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await reloadEntered.Task.WaitAsync(Watchdog);
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<string> joined = cache.GetAsync(1).AsTask();
        await Assert.That(joined.IsCompleted).IsFalse();
        releaseReload.SetResult("new");
        await Assert.That((await joined.WaitAsync(Watchdog))).IsEqualTo("new");
        await Assert.That(Volatile.Read(ref loads.Value)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(1);
    }

    [Test]
    public async Task FailedAutomaticRefreshKeepsValueAndHonorsBackoff()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var firstReloadEntered = NewSignal();
        var secondReloadEntered = NewSignal();
        var firstFailure = NewSignal<string>();
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                int call = Interlocked.Increment(ref reloads.Value);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await firstReloadEntered.Task.WaitAsync(Watchdog);
        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(1);
        firstFailure.SetException(new InvalidOperationException("reload"));
        await Assert
            .That((Func<Task>)(() => joinedRefresh))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await reloadEntered.Task.WaitAsync(Watchdog);
        Task<string> joinedRefresh = cache.RefreshAsync(1).AsTask();
        cache.Set(1, "set");
        releaseReload.SetResult("late");
        await reloadCompleted.Task.WaitAsync(Watchdog);
        await joinedRefresh.WaitAsync(Watchdog);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("set");
    }

    [Test]
    public async Task ExplicitRefreshSameKeyFailsFastInsideItsLoadChain()
    {
        var loader = new SameKeyRefreshLoader();
        await using var cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildAsyncLoading(loader.LoadAsync);
        loader.Cache = cache;
        await Assert
            .That((Func<Task>)(() => cache.GetAsync(1).AsTask()))
            .ThrowsExactly<LoadingCacheReentrancyException>();
    }

    [Test]
    public async Task ExplicitRefreshKToJToKCycleFailsFast()
    {
        var loader = new CyclicRefreshLoader();
        await using var cache = CacheBuilder
            .Create<string, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .BuildAsyncLoading(loader.LoadAsync);
        loader.Cache = cache;
        await Assert
            .That(() => cache.GetAsync("K").AsTask())
            .ThrowsExactly<LoadingCacheReentrancyException>();
    }

    [Test]
    public async Task SynchronousRefreshSameKeyFailsFastInsideItsLoadChain()
    {
        var loader = new SyncReentrantRefreshLoader();
        using var cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildLoading(loader.Load);
        loader.Cache = cache;
        await Assert.That(() => cache.Get(1)).ThrowsExactly<LoadingCacheReentrancyException>();
    }

    private sealed class SameKeyRefreshLoader
    {
        internal IAsyncLoadingCache<int, string> Cache { private get; set; } = null!;

        internal async Task<string> LoadAsync(int key, CancellationToken cancellationToken) =>
            await Cache.RefreshAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private sealed class CyclicRefreshLoader
    {
        internal IAsyncLoadingCache<string, int> Cache { private get; set; } = null!;

        internal async Task<int> LoadAsync(string key, CancellationToken cancellationToken) =>
            await Cache
                .RefreshAsync(key == "K" ? "J" : "K", CancellationToken.None)
                .ConfigureAwait(false);
    }

    private sealed class SyncReentrantRefreshLoader
    {
        internal ILoadingCache<int, string> Cache { private get; set; } = null!;

        internal string Load(int key) => Cache.RefreshAsync(key).GetAwaiter().GetResult();
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
        await Assert.That(cache.Get(1)).IsEqualTo("old");
        Task<string> refresh = cache.RefreshAsync(1);
        await entered.Task.WaitAsync(Watchdog);
        await Assert.That(refresh.IsCompleted).IsFalse();
        release.TrySetResult(true);
        await Assert.That((await refresh.WaitAsync(Watchdog))).IsEqualTo("new");
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
