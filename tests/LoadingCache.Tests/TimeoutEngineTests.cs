using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Exceptions;

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
        var loads = new System.Runtime.CompilerServices.StrongBox<int>();
        var loader = new TestLoader(
            (_, _) =>
            {
                int call = Interlocked.Increment(ref loads.Value);
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
        await Assert.That((Func<Task>)(() => first)).ThrowsExactly<TimeoutException>();
        await Assert.That(cache.GetStatistics().InFlightLoads).IsEqualTo(1);
        await Assert.That(cache.GetStatistics().LoadTimeouts).IsEqualTo(1);
        Exception? rejection = null;
        try
        {
            await cache.GetAsync(2);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        await Assert.That<object>(rejection!).IsTypeOf<CacheLoadRejectedException>();
        releaseLate.SetResult("late");
        await Eventually(() => cache.GetStatistics().InFlightLoads == 0);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("fresh");
        await Assert.That(Volatile.Read(ref loads.Value)).IsEqualTo(2);
    }

    [Test]
    public async Task TimeoutClearsRefreshOwnershipAndExplicitRefreshCanRetryAfterLateCompletion()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseLate = NewSignal<string>();
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var loader = new TestLoader(
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                int call = Interlocked.Increment(ref reloads.Value);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await reloadEntered.Task.WaitAsync(Watchdog);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.TryGet(1, out string? oldValue)).IsTrue();
        await Assert.That(oldValue).IsEqualTo("old");
        Exception? rejection = null;
        try
        {
            await cache.RefreshAsync(1);
        }
        catch (Exception exception)
        {
            rejection = exception;
        }

        await Assert.That<object>(rejection!).IsTypeOf<CacheLoadRejectedException>();
        releaseLate.SetResult("late");
        await Eventually(() => cache.GetStatistics().InFlightLoads == 0);
        await Assert
            .That((await cache.RefreshAsync(1).AsTask().WaitAsync(Watchdog)))
            .IsEqualTo("new");
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(2);
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
        await Assert.That((Func<Task>)(() => first)).Throws<OperationCanceledException>();
        release.SetResult("shared");
        await Assert
            .That((await second.WaitAsync(Watchdog, CancellationToken.None)))
            .IsEqualTo("shared");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClaimedRefreshMaintenanceFailureRollsBackAndCanRetry(bool automaticRefresh)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var reloadEntered = NewSignal();
        var releaseReload = NewSignal<string>();
        var reloads = new System.Runtime.CompilerServices.StrongBox<int>();
        var policy = new ThrowingPublishPolicy();
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 4,
                MaxConcurrentLoads = 2,
                RefreshAfterWrite = automaticRefresh ? TimeSpan.FromSeconds(1) : null,
                TimeProvider = clock,
                Policy = policy,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) => Task.FromResult("old"),
            (_, _, _) =>
            {
                if (Interlocked.Increment(ref reloads.Value) != 1)
                {
                    return Task.FromResult("retry");
                }

                reloadEntered.TrySetResult(true);
                return releaseReload.Task;
            }
        );
        await Assert.That((await cache.GetAsync(1).AsTask().WaitAsync(Watchdog))).IsEqualTo("old");
        await Assert.That(cache.TryGetTask(1, out Task<string>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<string> failedRefresh = cache.RefreshAsync(1).AsTask();
        await reloadEntered.Task.WaitAsync(Watchdog);
        policy.ThrowNextPublish();
        releaseReload.SetResult("new");
        await Assert
            .That((Func<Task>)(() => failedRefresh))
            .ThrowsExactly<ControlledRefreshFailureException>();
        await Assert.That(cache.TryGet(1, out string? oldValue)).IsTrue();
        await Assert.That(oldValue).IsEqualTo("old");
        await Assert.That(cache.TryGetTask(1, out Task<string>? rolledBackTask)).IsTrue();
        Assert.NotNull(rolledBackTask);
        await Assert.That(ReferenceEquals(rolledBackTask, oldTask)).IsTrue();
        await Assert.That((await rolledBackTask)).IsEqualTo("old");
        Task<string> retry = cache.RefreshAsync(1).AsTask();
        await Assert.That((await retry.WaitAsync(Watchdog))).IsEqualTo("retry");
        await Assert.That(cache.TryGet(1, out string? retriedValue)).IsTrue();
        await Assert.That(retriedValue).IsEqualTo("retry");
        await Assert.That(cache.TryGetTask(1, out Task<string>? retriedTask)).IsTrue();
        Assert.NotNull(retriedTask);
        await Assert.That(ReferenceEquals(retriedTask, oldTask)).IsFalse();
        await Assert.That((await retriedTask)).IsEqualTo("retry");
        await Assert.That(Volatile.Read(ref reloads.Value)).IsEqualTo(2);
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
        await Assert.That((await cache.GetAsync(1).AsTask().WaitAsync(Watchdog))).IsEqualTo("old");
        clock.Advance(TimeSpan.FromSeconds(1));
        Task<string> refresh = cache.RefreshAsync(1).AsTask();
        await reloadEntered.Task.WaitAsync(Watchdog);
        releaseReload.SetResult("new");
        await Assert.That((await refresh.WaitAsync(Watchdog))).IsEqualTo("new");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ColdClaimFailureDoesNotLeaveReadyEntryWithFaultedColdSharedTask()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coldResult = NewSignal<string>();
        var coldTimerArmEntered = NewSignal();
        var reloadEntered = NewSignal();
        ManualResetEventSlim releaseColdTimerArm = new(false);
        Func<TimeSpan, bool> waitForColdTimerArmRelease = releaseColdTimerArm.Wait;
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
                if (!waitForColdTimerArmRelease(Watchdog))
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
            Task<string> cold = Task
                .Factory.StartNew(
                    static state => ((IAsyncLoadingCache<int, string>)state!).GetAsync(1).AsTask(),
                    cache,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default
                )
                .Unwrap();
            Task<string>? refresh = null;
            try
            {
                coldResult.SetResult("old");
                await coldTimerArmEntered.Task.WaitAsync(Watchdog);
                refresh = Task
                    .Factory.StartNew(
                        static state =>
                            ((IAsyncLoadingCache<int, string>)state!).RefreshAsync(1).AsTask(),
                        cache,
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default
                    )
                    .Unwrap();
                await reloadEntered.Task.WaitAsync(Watchdog);
                releaseColdTimerArm.Set();
                await Assert
                    .That((Func<Task>)(() => cold))
                    .ThrowsExactly<ControlledRefreshFailureException>();
                await Assert
                    .That((Func<Task>)(() => refresh))
                    .ThrowsExactly<ControlledRefreshFailureException>();
                await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
                await Assert.That(value).IsEqualTo("old");
                await Assert.That(cache.TryGetTask(1, out Task<string>? currentTask)).IsTrue();
                Assert.NotNull(currentTask);
                await Assert.That(ReferenceEquals(currentTask, cold)).IsFalse();
                await Assert.That((await currentTask.WaitAsync(Watchdog))).IsEqualTo("old");
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
        public long Maximum { get; private set; } = 4;
        public long WeightedSize => 0;
        public int ResidentCount => 0;

        public void SetMaximum(long maximum, bool weighted) => Maximum = maximum;

        public IReadOnlyList<object> Snapshot(bool hottest, int limit) => [];

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
