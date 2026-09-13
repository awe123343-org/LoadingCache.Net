using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class BulkTerminalRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task BulkTimeoutOwnsEveryPromiseWhenBackendFaultsDuringTimeoutCleanup()
    {
        var time = new BlockingDisposeTimeProvider();
        var loader = new FaultingBulkLoader();
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .BuildAsyncLoading(loader);

        Task<IReadOnlyDictionary<int, int>> bulk = cache.GetAllAsync([1, 2]).AsTask();
        await loader.Started.Task.WaitAsync(Watchdog);
        Task<int> joined = cache.GetAsync(2).AsTask();

        Task timeout = time.FireTimeoutAsync();
        try
        {
            await time.DisposeEntered.Task.WaitAsync(Watchdog);
            loader.Release.TrySetException(new InvalidOperationException("backend failed"));
            await loader.Returned.Task.WaitAsync(Watchdog);

            SpinWait
                .SpinUntil(() => cache.GetStatistics().InFlightLoads == 0, Watchdog)
                .Should()
                .BeTrue();
            joined.IsCompleted.Should().BeFalse();

            time.ReleaseDispose.TrySetResult(null);
            await timeout.WaitAsync(Watchdog);

            Func<Task> waitBulk = async () => await bulk;
            Func<Task> waitJoined = async () => await joined;
            await waitBulk.Should().ThrowExactlyAsync<TimeoutException>();
            await waitJoined.Should().ThrowExactlyAsync<TimeoutException>();
        }
        finally
        {
            loader.Release.TrySetException(new InvalidOperationException("Test cleanup."));
            time.ReleaseDispose.TrySetResult(null);
            await timeout.WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task BulkReadyReadFailureDoesNotReleaseBackendPermitEarly()
    {
        var expiry = new BlockingReadExpiry();
        var loader = new ControlledBulkLoader();
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .ExpireAfter(expiry)
            .BuildAsyncLoading(loader);

        cache.Set(1, "ready");
        Task<IReadOnlyDictionary<int, string>> bulk = Task.Run(async () =>
            await cache.GetAllAsync([1, 2]).ConfigureAwait(false)
        );
        try
        {
            await expiry.ReadEntered.Task.WaitAsync(Watchdog);

            Task<string> joined = cache.GetAsync(2).AsTask();
            await loader.BulkStarted.Task.WaitAsync(Watchdog);

            Func<Task> rejected = async () => await cache.GetAsync(3);
            await rejected.Should().ThrowExactlyAsync<CacheLoadRejectedException>();

            expiry.Release.TrySetResult(null);

            Func<Task> waitBulk = async () => await bulk;
            await waitBulk.Should().ThrowExactlyAsync<InvalidOperationException>();

            cache.GetStatistics().InFlightLoads.Should().Be(1);

            loader.Release.TrySetResult(new Dictionary<int, string> { [2] = "late" });
            (await joined).Should().Be("late");
            await loader.Finished.Task.WaitAsync(Watchdog);
            SpinWait
                .SpinUntil(() => cache.GetStatistics().InFlightLoads == 0, Watchdog)
                .Should()
                .BeTrue();
        }
        finally
        {
            expiry.Release.TrySetResult(null);
            loader.Release.TrySetResult(new Dictionary<int, string> { [2] = "late" });
            if (loader.BulkStarted.Task.IsCompleted)
            {
                await loader.Finished.Task.WaitAsync(Watchdog);
            }
        }
    }

    private sealed class FaultingBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, int>> Release { get; } =
            Signal<IReadOnlyDictionary<int, int>>();
        internal TaskCompletionSource<bool> Returned { get; } = Signal<bool>();

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromException<int>(
                new InvalidOperationException($"Unexpected single load {key}.")
            );

        public async Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Started.TrySetResult(true);
            try
            {
                return await Release.Task.ConfigureAwait(false);
            }
            finally
            {
                Returned.TrySetResult(true);
            }
        }
    }

    private sealed class ControlledBulkLoader : IBulkAsyncCacheLoader<int, string>
    {
        internal TaskCompletionSource<bool> BulkStarted { get; } = Signal<bool>();
        internal TaskCompletionSource<IReadOnlyDictionary<int, string>> Release { get; } =
            Signal<IReadOnlyDictionary<int, string>>();
        internal TaskCompletionSource<bool> Finished { get; } = Signal<bool>();

        public Task<string> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromResult($"single-{key}");

        public async Task<IReadOnlyDictionary<int, string>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            BulkStarted.TrySetResult(true);
            try
            {
                return await Release.Task.ConfigureAwait(false);
            }
            finally
            {
                Finished.TrySetResult(true);
            }
        }
    }

    private sealed class BlockingReadExpiry : IExpiry<int, string>
    {
        internal TaskCompletionSource<bool> ReadEntered { get; } = Signal<bool>();
        internal TaskCompletionSource<object?> Release { get; } = Signal<object?>();

        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromHours(1);

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromHours(1);

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration)
        {
            ReadEntered.TrySetResult(true);
            if (!Release.Task.Wait(Watchdog))
            {
                throw new TimeoutException("The controlled expiry callback was not released.");
            }

            throw new InvalidOperationException("The bulk read callback failed.");
        }
    }

    private sealed class BlockingDisposeTimeProvider : TimeProvider
    {
        private long _timestamp;
        private BlockingTimer? _timer;

        internal TaskCompletionSource<bool> DisposeEntered { get; } = Signal<bool>();
        internal TaskCompletionSource<object?> ReleaseDispose { get; } = Signal<object?>();

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = new BlockingTimer(this, callback, state, dueTime);
            Interlocked.CompareExchange(ref _timer, timer, null);
            return timer;
        }

        internal Task FireTimeoutAsync()
        {
            BlockingTimer timer =
                _timer ?? throw new InvalidOperationException("The timeout timer was not created.");
            return Task.Run(timer.Fire);
        }

        private sealed class BlockingTimer(
            BlockingDisposeTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime
        ) : ITimer
        {
            private int _disposed;

            internal void Fire()
            {
                Interlocked.Exchange(ref owner._timestamp, dueTime.Ticks);
                callback(state);
            }

            public bool Change(TimeSpan newDueTime, TimeSpan period) =>
                Volatile.Read(ref _disposed) == 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                owner.DisposeEntered.TrySetResult(true);
                if (!owner.ReleaseDispose.Task.Wait(Watchdog))
                {
                    throw new TimeoutException("The controlled timer was not released.");
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
