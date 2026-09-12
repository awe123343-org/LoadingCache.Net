using FluentAssertions;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class TimeoutEngineTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public async Task TimeoutRevokesColdPublicationAndRetainsExecutionPermitUntilLateCompletion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var releaseLate = NewSignal<string>();
        int loads = 0;
        var loader = new TestLoader(
            (_, _) =>
            {
                int call = Interlocked.Increment(ref loads);
                if (call != 1)
                {
                    return Task.FromResult("fresh");
                }

                entered.TrySetResult(true);
                return releaseLate.Task;
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .RecordStatistics()
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        Task<string> first = cache.GetAsync(1).AsTask();
        await entered.Task.WaitAsync(Watchdog);
        clock.Advance(TimeSpan.FromSeconds(1));

        await FluentActions.Awaiting(() => first).Should().ThrowExactlyAsync<TimeoutException>();
        cache.GetStatistics().InFlightLoads.Should().Be(1);
        cache.GetStatistics().LoadTimeouts.Should().Be(1);

        Exception? rejection = null;
        try
        {
            await cache.GetAsync(2);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        rejection.Should().BeOfType<CacheLoadRejectedException>();

        releaseLate.SetResult("late");
        await Eventually(() => cache.GetStatistics().InFlightLoads == 0);
        cache.TryGet(1, out _).Should().BeFalse();

        (await cache.GetAsync(1)).Should().Be("fresh");
        Volatile.Read(ref loads).Should().Be(2);
    }

    [Test]
    public async Task TimeoutClearsRefreshOwnershipAndExplicitRefreshCanRetryAfterLateCompletion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseLate = NewSignal<string>();
        int reloads = 0;
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                int call = Interlocked.Increment(ref reloads);
                if (call != 1)
                {
                    return Task.FromResult("new");
                }

                reloadEntered.TrySetResult(true);
                return releaseLate.Task;
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        (await cache.GetAsync(1)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("old");
        await reloadEntered.Task.WaitAsync(Watchdog);

        clock.Advance(TimeSpan.FromSeconds(1));
        cache.TryGet(1, out string? oldValue).Should().BeTrue();
        oldValue.Should().Be("old");

        Exception? rejection = null;
        try
        {
            await cache.RefreshAsync(1);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        rejection.Should().BeOfType<CacheLoadRejectedException>();

        releaseLate.SetResult("late");
        await Eventually(() => cache.GetStatistics().InFlightLoads == 0);
        (await cache.RefreshAsync(1).AsTask().WaitAsync(Watchdog)).Should().Be("new");
        Volatile.Read(ref reloads).Should().Be(2);
    }

    [Test]
    public async Task CallerCancellationDoesNotCancelSharedLoadWithTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = NewSignal();
        var release = NewSignal<string>();
        var loader = new TestLoader(
            (_, _) =>
            {
                entered.TrySetResult(true);
                return release.Task;
            }
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .LoadTimeout(TimeSpan.FromSeconds(10))
            .TimeProvider(clock)
            .BuildAsyncLoading(loader);

        using var canceled = new CancellationTokenSource();
        Task<string> first = cache.GetAsync(1, canceled.Token).AsTask();
        await entered.Task.WaitAsync(Watchdog, CancellationToken.None);
        Task<string> second = cache.GetAsync(1, CancellationToken.None).AsTask();
        await canceled.CancelAsync();

        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult("shared");
        (await second.WaitAsync(Watchdog, CancellationToken.None)).Should().Be("shared");
    }

    [Test]
    public async Task ClaimedRefreshMaintenanceFailureRollsBackAndCanRetry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        int reloads = 0;
        var policy = new ThrowingPublishPolicy();
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 2,
                RefreshAfterWrite = TimeSpan.FromSeconds(1),
                TimeProvider = clock,
                Policy = policy,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref reloads) != 1)
                {
                    return Task.FromResult("retry");
                }

                reloadEntered.TrySetResult(true);
                return releaseReload.Task;
            }
        );

        (await cache.GetAsync(1).AsTask().WaitAsync(Watchdog)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));

        Task<string> failedRefresh = cache.RefreshAsync(1).AsTask();
        await reloadEntered.Task.WaitAsync(Watchdog);
        policy.ThrowNextPublish();
        releaseReload.SetResult("new");

        await FluentActions
            .Awaiting(() => failedRefresh)
            .Should()
            .ThrowExactlyAsync<ControlledRefreshFailureException>();
        cache.TryGet(1, out string? oldValue).Should().BeTrue();
        oldValue.Should().Be("old");

        Task<string> retry = cache.RefreshAsync(1).AsTask();
        (await retry.WaitAsync(Watchdog)).Should().Be("retry");
        cache.TryGet(1, out string? retriedValue).Should().BeTrue();
        retriedValue.Should().Be("retry");
        Volatile.Read(ref reloads).Should().Be(2);
    }

    [Test]
    public async Task RefreshSuccessRearmsPromptTimerForShorterVariableDuration()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        int reloads = 0;
        await using var cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .ExpireAfter(
                new FixedRefreshExpiry
                {
                    CreateDuration = TimeSpan.FromSeconds(10),
                    UpdateDuration = TimeSpan.FromSeconds(1),
                }
            )
            .EnableExpirationScheduler()
            .BuildAsyncLoading(
                new TestLoader(
                    (_, _) => Task.FromResult("old"),
                    (_, _, _) =>
                    {
                        if (Interlocked.Increment(ref reloads) != 1)
                        {
                            return Task.FromResult("retry");
                        }

                        reloadEntered.TrySetResult(true);
                        return releaseReload.Task;
                    }
                )
            );

        (await cache.GetAsync(1).AsTask().WaitAsync(Watchdog)).Should().Be("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<string> refresh = cache.RefreshAsync(1).AsTask();
        await reloadEntered.Task.WaitAsync(Watchdog);
        releaseReload.SetResult("new");
        (await refresh.WaitAsync(Watchdog)).Should().Be("new");

        clock.Advance(TimeSpan.FromSeconds(1));
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public async Task ColdClaimFailureDoesNotLeaveReadyEntryWithFaultedColdSharedTask()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coldResult = NewSignal<string>();
        var coldTimerArmEntered = NewSignal();
        var reloadEntered = NewSignal();
        ManualResetEventSlim releaseColdTimerArm = new(false);
        int timerArms = 0;
        var hooks = new LoadingCacheTestHooks
        {
            BeforeExpirationTimerArm = () =>
            {
                int arm = Interlocked.Increment(ref timerArms);
                if (arm != 1)
                {
                    throw new ControlledRefreshFailureException();
                }

                coldTimerArmEntered.TrySetResult(true);
                if (!releaseColdTimerArm.Wait(Watchdog))
                {
                    throw new TimeoutException("The cold timer arm was not released.");
                }
                throw new ControlledRefreshFailureException();
            },
        };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 2,
                ExpireAfterWrite = TimeSpan.FromMinutes(1),
                EnableExpirationScheduler = true,
                TimeProvider = clock,
                TestHooks = hooks,
            }
        );
        try
        {
            await using var cache = new AsyncLoadingCache<int, string>(
                engine,
                (_, _) => coldResult.Task,
                (_, _, _) =>
                {
                    reloadEntered.TrySetResult(true);
                    return Task.FromResult("new");
                }
            );
            Task<string> cold = Task.Run(() => cache.GetAsync(1).AsTask());
            Task<string>? refresh = null;
            try
            {
                coldResult.SetResult("old");
                await coldTimerArmEntered.Task.WaitAsync(Watchdog);

                refresh = Task.Run(() => cache.RefreshAsync(1).AsTask());
                await reloadEntered.Task.WaitAsync(Watchdog);

                releaseColdTimerArm.Set();
                await FluentActions
                    .Awaiting(() => cold)
                    .Should()
                    .ThrowExactlyAsync<ControlledRefreshFailureException>();
                await FluentActions
                    .Awaiting(() => refresh)
                    .Should()
                    .ThrowExactlyAsync<ControlledRefreshFailureException>();

                cache.TryGet(1, out string? value).Should().BeTrue();
                value.Should().Be("old");
                cache.TryGetTask(1, out Task<string>? currentTask).Should().BeTrue();
                currentTask.Should().NotBeSameAs(cold);
                (await currentTask.WaitAsync(Watchdog)).Should().Be("old");
            }
            finally
            {
                releaseColdTimerArm.Set();
                try
                {
                    await cold.WaitAsync(Watchdog);
                }
                catch (Exception) when (cold.IsCompleted) { }

                if (refresh is not null)
                {
                    try
                    {
                        await refresh.WaitAsync(Watchdog);
                    }
                    catch (Exception) when (refresh.IsCompleted) { }
                }
            }
        }
        finally
        {
            releaseColdTimerArm.Set();
            releaseColdTimerArm.Dispose();
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Watchdog;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionException("The controlled timeout condition was not reached.");
            }

            await Task.Yield();
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledRefreshFailureException : Exception;

    private sealed class ThrowingPublishPolicy : ICacheEnginePolicy
    {
        private int _throwNext;

        public long Maximum => 4;

        public long WeightedSize => 0;

        internal void ThrowNextPublish() => Volatile.Write(ref _throwNext, 1);

        public void OnAccess(object? entryToken) { }

        public void OnPublish(object? entryToken, long weight)
        {
            if (Interlocked.Exchange(ref _throwNext, 0) != 0)
            {
                throw new ControlledRefreshFailureException();
            }
        }

        public void OnRemove(object? entryToken) { }

        public void Clear() { }

        public bool CleanUp() => false;

        public ReadBufferStatistics GetReadBufferStatistics() => default;

        public void Dispose() { }
    }

    private sealed class FixedRefreshExpiry : IExpiry<int, string>
    {
        internal TimeSpan CreateDuration { get; init; }

        internal TimeSpan UpdateDuration { get; init; }

        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            CreateDuration;

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            UpdateDuration;

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration) =>
            CreateDuration;
    }

    private sealed class TestLoader : IAsyncCacheLoader<int, string>
    {
        private readonly Func<int, CancellationToken, Task<string>> _load;
        private readonly Func<int, string, CancellationToken, Task<string>>? _reload;

        internal TestLoader(
            Func<int, CancellationToken, Task<string>> load,
            Func<int, string, CancellationToken, Task<string>>? reload = null
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
        ) =>
            _reload is null
                ? _load(key, cancellationToken)
                : _reload(key, oldValue, cancellationToken);
    }
}
