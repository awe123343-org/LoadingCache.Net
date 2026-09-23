namespace LoadingCache.Tests;

public sealed class BulkTerminalRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeCompletesNormalWaitersWhileRetirementCleanupIsBlocked(
        bool backendFails
    )
    {
        var time = new BlockingDisposeTimeProvider();
        var backend = Signal<int>();
        IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .BuildAsyncLoading((_, _) => backend.Task);
        Task<int> pending = cache.GetAsync(1).AsTask();
        Task<int> joined = cache.GetAsync(1).AsTask();
        if (backendFails)
        {
            backend.TrySetException(new InvalidOperationException("Backend failure."));
        }
        else
        {
            backend.TrySetResult(42);
        }

        try
        {
            await time.DisposeEntered.Task.WaitAsync(Watchdog);
            await Assert.That(pending.IsCompleted).IsFalse();
            await cache.DisposeAsync().AsTask().WaitAsync(Watchdog);
            await Assert
                .That(pending.IsCompleted)
                .IsTrue()
                .Because("retirement cleanup cannot hide an unfinished promise from shutdown");
            await Assert.That(joined.IsCompleted).IsTrue();
            await Assert.That(() => pending).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(() => joined).ThrowsExactly<ObjectDisposedException>();
        }
        finally
        {
            time.ReleaseDispose.TrySetResult(null);
            try
            {
                foreach (Task waiter in new Task[] { pending, joined })
                {
                    try
                    {
                        await waiter.WaitAsync(Watchdog);
                    }
                    catch (InvalidOperationException)
                    {
                        // Observe either the controlled backend failure or shutdown.
                    }
                }
            }
            finally
            {
                await cache.DisposeAsync();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeCompletesTimedOutSyncWaitersAfterBackendReturns(bool useBulk)
    {
        var time = new BlockingDisposeTimeProvider(Watchdog * 3);
        var backend = new BlockingTestHook(Watchdog);
        ILoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(2)
            .MaximumBulkKeys(2)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .RecordStatistics()
            .BuildLoading(new GatedSyncLoader(backend));
        Task owner = Task.Factory.StartNew(
            static state =>
            {
                (ILoadingCache<int, int> current, bool bulk) = ((ILoadingCache<int, int>, bool))
                    state!;
                if (bulk)
                {
                    current.GetAll([1, 2]);
                }
                else
                {
                    current.Get(2);
                }
            },
            (cache, useBulk),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        Task<int> joined = Task.FromResult(0);
        Task timeout = Task.CompletedTask;
        try
        {
            await backend.Entered.WaitAsync(Watchdog);
            joined = Task.Factory.StartNew(
                static state => ((ILoadingCache<int, int>)state!).Get(2),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            WaitForSyncJoiner(cache);
            timeout = time.FireTimeoutAsync();
            await time.DisposeEntered.Task.WaitAsync(Watchdog);
            backend.Release();
            await backend.Returned.WaitAsync(Watchdog);
            WaitForSyncBackendCompletion(cache);
            await Assert.That(joined.IsCompleted).IsFalse();
            cache.Dispose();
            await Assert
                .That(() => joined.WaitAsync(Watchdog))
                .ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That(() => owner.WaitAsync(Watchdog))
                .ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That(timeout.IsCompleted)
                .IsFalse()
                .Because("the controlled timeout cleanup is blocked");
        }
        finally
        {
            backend.Release();
            time.ReleaseDispose.TrySetResult(null);
            try
            {
                await timeout.WaitAsync(Watchdog);
                foreach (Task pending in new[] { joined, owner })
                {
                    try
                    {
                        await pending.WaitAsync(Watchdog);
                    }
                    catch (Exception exception)
                        when (exception is ObjectDisposedException or TimeoutException)
                    {
                        // Preserve the original assertion when releasing the timeout gate.
                    }
                }
            }
            finally
            {
                cache.Dispose();
                await backend.DisposeAsync();
            }
        }
    }

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
            Func<CacheStatistics> readStatistics = cache.GetStatistics;
            await Assert
                .That(SpinWait.SpinUntil(() => readStatistics().InFlightLoads == 0, Watchdog))
                .IsTrue();
            await Assert.That(joined.IsCompleted).IsFalse();
            time.ReleaseDispose.TrySetResult(null);
            await timeout.WaitAsync(Watchdog);
            Func<Task> waitBulk = async () => await bulk;
            Func<Task> waitJoined = async () => await joined;
            await Assert.That(waitBulk).ThrowsExactly<TimeoutException>();
            await Assert.That(waitJoined).ThrowsExactly<TimeoutException>();
        }
        finally
        {
            loader.Release.TrySetException(new InvalidOperationException("Test cleanup."));
            time.ReleaseDispose.TrySetResult(null);
            await timeout.WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task DisposeCompletesBulkWaitersWhileTimeoutCleanupIsBlocked()
    {
        var time = new BlockingDisposeTimeProvider();
        var loader = new FaultingBulkLoader();
        IAsyncLoadingCache<int, int> cache = CacheBuilder
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
            await Assert.That(joined.IsCompleted).IsFalse();
            await cache.DisposeAsync().AsTask().WaitAsync(Watchdog);
            await Assert
                .That(joined.IsCompleted)
                .IsTrue()
                .Because("claiming timeout does not mean promises are terminal");
            await Assert.That(() => joined).ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That((Func<Task>)(() => bulk.WaitAsync(Watchdog)))
                .ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That(timeout.IsCompleted)
                .IsFalse()
                .Because("shutdown cannot wait on timeout cleanup");
        }
        finally
        {
            time.ReleaseDispose.TrySetResult(null);
            loader.Release.TrySetException(new InvalidOperationException("Late backend failure."));
            try
            {
                await timeout.WaitAsync(Watchdog);
                await loader.Returned.Task.WaitAsync(Watchdog);
                foreach (Task pending in new Task[] { joined, bulk })
                {
                    try
                    {
                        await pending.WaitAsync(Watchdog);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Preserve shutdown when timeout/backend cleanup finishes later.
                    }
                }
            }
            finally
            {
                await cache.DisposeAsync();
            }
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
        Task<IReadOnlyDictionary<int, string>> bulk = Task
            .Factory.StartNew(
                static async state =>
                    await ((IAsyncLoadingCache<int, string>)state!)
                        .GetAllAsync([1, 2])
                        .ConfigureAwait(false),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        try
        {
            await expiry.ReadEntered.Task.WaitAsync(Watchdog);
            Task<string> joined = cache.GetAsync(2).AsTask();
            await loader.BulkStarted.Task.WaitAsync(Watchdog);
            Func<Task> rejected = () => cache.GetAsync(3).AsTask();
            await Assert.That(rejected).ThrowsExactly<CacheLoadRejectedException>();
            expiry.Release.TrySetResult(null);
            Func<Task> waitBulk = async () => await bulk;
            await Assert.That(waitBulk).ThrowsExactly<InvalidOperationException>();
            await Assert.That(cache.GetStatistics().InFlightLoads).IsEqualTo(1);
            loader.Release.TrySetResult(new Dictionary<int, string> { [2] = "late" });
            await Assert.That((await joined)).IsEqualTo("late");
            await loader.Finished.Task.WaitAsync(Watchdog);
            Func<CacheStatistics> readStatistics = cache.GetStatistics;
            await Assert
                .That(SpinWait.SpinUntil(() => readStatistics().InFlightLoads == 0, Watchdog))
                .IsTrue();
        }
        finally
        {
            expiry.Release.TrySetResult(null);
            loader.Release.TrySetResult(new Dictionary<int, string> { [2] = "late" });
            try
            {
                await bulk.WaitAsync(Watchdog);
            }
            catch (InvalidOperationException exception)
                when (exception.Message == "The bulk read callback failed.")
            {
                // Observe the controlled ready-read failure before disposing the cache.
            }
            finally
            {
                if (loader.BulkStarted.Task.IsCompleted)
                {
                    await loader.Finished.Task.WaitAsync(Watchdog);
                }
            }
        }
    }

    private static void WaitForSyncJoiner(ILoadingCache<int, int> cache)
    {
        if (!(SpinWait.SpinUntil(() => cache.Statistics.CoalescedWaiters == 1, Watchdog)))
            Assert.Fail("Timed out waiting for the controlled condition.");
    }

    private static void WaitForSyncBackendCompletion(ILoadingCache<int, int> cache)
    {
        if (!(SpinWait.SpinUntil(() => cache.Statistics.InFlightLoads == 0, Watchdog)))
            Assert.Fail("Timed out waiting for the controlled condition.");
    }

    private sealed class GatedSyncLoader(BlockingTestHook backend) : IBulkSyncCacheLoader<int, int>
    {
        public int Load(int key)
        {
            backend.Invoke();
            return key;
        }

        public IReadOnlyDictionary<int, int> LoadAll(IReadOnlyCollection<int> keys)
        {
            backend.Invoke();
            return new Dictionary<int, int> { [1] = 1, [2] = 2 };
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

    private sealed class BlockingDisposeTimeProvider(TimeSpan? disposeWatchdog = null)
        : TimeProvider
    {
        private readonly TimeSpan _disposeWatchdog = disposeWatchdog ?? Watchdog;
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
                if (!owner.ReleaseDispose.Task.Wait(owner._disposeWatchdog))
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
