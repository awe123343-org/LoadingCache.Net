using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class TimeoutAdversarialTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public async Task TimeoutDoesNotReleaseReservationBeforeCancellationCallbackCompletes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var loaderEntered = Signal();
        var cancellationEntered = Signal();
        var cancellationExited = Signal();
        var cancellationRelease = Signal();
        var backend = Signal<string>();
        var callbackCount = new System.Runtime.CompilerServices.StrongBox<int>();
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(Loader);
        Task<string> first = cache.GetAsync(1).AsTask();
        try
        {
            await loaderEntered.Task.WaitAsync(Watchdog, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitForCompletion(first);
            await Assert.That((Func<Task>)(() => first)).ThrowsExactly<TimeoutException>();
            await cancellationEntered.Task.WaitAsync(Watchdog);
            backend.TrySetResult("late");
            Func<CacheStatistics> readStatistics = cache.GetStatistics;
            await Assert
                .That(SpinWait.SpinUntil(() => readStatistics().InFlightLoads == 0, Watchdog))
                .IsTrue();
            Task<string> second = cache.GetAsync(2).AsTask();
            await WaitForCompletion(second);
            await Assert
                .That((Func<Task>)(() => second))
                .ThrowsExactly<CacheLoadRejectedException>();
            await Assert.That(Volatile.Read(ref callbackCount.Value)).IsEqualTo(1);
        }
        finally
        {
            cancellationRelease.TrySetResult(true);
            backend.TrySetResult("late");
            await cancellationExited.Task.WaitAsync(Watchdog, CancellationToken.None);
            await backend.Task.WaitAsync(Watchdog, CancellationToken.None);
        }

        return;
        Task<string> Loader(int key, CancellationToken cancellationToken)
        {
            if (key != 1)
            {
                return Task.FromResult("second");
            }

            cancellationToken.Register(() =>
            {
                Interlocked.Increment(ref callbackCount.Value);
                cancellationEntered.TrySetResult(true);
                cancellationRelease.Task.GetAwaiter().GetResult();
                cancellationExited.TrySetResult(true);
            });
            loaderEntered.TrySetResult(true);
            return backend.Task;
        }
    }

    [Test]
    public async Task SynchronouslyFiringTimeoutTimerDoesNotReadDisposedCancellationSource()
    {
        var clock = new ImmediateFireTimeProvider();
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading((_, _) => Task.FromResult("value"));
        Task<string> first = cache.GetAsync(1).AsTask();
        await WaitForCompletion(first);
        await Assert.That((Func<Task>)(() => first)).ThrowsExactly<TimeoutException>();
        await Assert.That(cache.GetStatistics().InFlightLoads).IsEqualTo(0);
        await Assert
            .That((await cache.GetAsync(2).AsTask().WaitAsync(Watchdog)))
            .IsEqualTo("value");
    }

    [Test]
    public async Task PostClaimClockFailureCompletesWaiterAndAllowsRetry()
    {
        var clock = new ThrowNextTimestampTimeProvider();
        int hookCalls = 0;
        var loads = new System.Runtime.CompilerServices.StrongBox<int>();
        var hooks = new LoadingCacheTestHooks
        {
            BeforeReadyPublish = () =>
            {
                if (Interlocked.Exchange(ref hookCalls, 1) == 0)
                {
                    clock.ThrowNextTimestamp();
                }
            },
        };
        var builder = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromMinutes(1))
            .TimeProvider(clock);
        var engine = builder.CreateEngine(hooks, hasFixedLoader: true);
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) =>
                Task.FromResult(Interlocked.Increment(ref loads.Value) == 1 ? "first" : "retry")
        );
        Task<string> first = cache.GetAsync(1).AsTask();
        await WaitForCompletion(first);
        await Assert.That((Func<Task>)(() => first)).ThrowsExactly<ControlledTimestampException>();
        Task<string> retry = cache.GetAsync(1).AsTask();
        await WaitForCompletion(retry);
        await Assert.That((await retry)).IsEqualTo("retry");
        await Assert.That(Volatile.Read(ref loads.Value)).IsEqualTo(2);
    }

    [Test]
    public async Task DisposeReleasesActiveLoadTimeoutTimerWithoutWaitingForLoader()
    {
        var timeProvider = new TrackingTimeProvider();
        var loaderEntered = Signal();
        var releaseLoader = Signal<string>();
        IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromHours(1))
            .TimeProvider(timeProvider)
            .BuildAsyncLoading(
                (_, _) =>
                {
                    loaderEntered.TrySetResult(true);
                    return releaseLoader.Task;
                }
            );
        Task<string> pending = cache.GetAsync(1).AsTask();
        try
        {
            await loaderEntered.Task.WaitAsync(Watchdog, CancellationToken.None);
            await cache.DisposeAsync().AsTask().WaitAsync(Watchdog, CancellationToken.None);
            await WaitForCompletion(pending);
            await Assert.That((Func<Task>)(() => pending)).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(timeProvider.CreatedTimers).HasSingleItem();
            await Assert.That(timeProvider.CreatedTimers[0].DisposeCount).IsEqualTo(1);
            await Assert.That(timeProvider.CreatedTimers[0].CallbackCount).IsEqualTo(0);
        }
        finally
        {
            releaseLoader.TrySetResult("late");
            await releaseLoader.Task.WaitAsync(Watchdog, CancellationToken.None);
            await cache.DisposeAsync();
        }
    }

    private static async Task WaitForCompletion(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Watchdog));
        await Assert
            .That(ReferenceEquals(completed, task))
            .IsTrue()
            .Because("the adversarial operation must complete within the watchdog");
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ImmediateFireTimeProvider : TimeProvider
    {
        private long _timestamp;
        private int _fireNext = 1;
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
            var timer = new ImmediateTimer();
            if (dueTime == Timeout.InfiniteTimeSpan || Interlocked.Exchange(ref _fireNext, 0) != 1)
            {
                return timer;
            }

            Interlocked.Add(ref _timestamp, dueTime.Ticks);
            callback(state);
            return timer;
        }

        private sealed class ImmediateTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowNextTimestampTimeProvider : TimeProvider
    {
        private int _throwNext;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Exchange(ref _throwNext, 0) == 0
                ? 0
                : throw new ControlledTimestampException();
        }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        internal void ThrowNextTimestamp() => Volatile.Write(ref _throwNext, 1);
    }

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<TrackingTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => 0;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        internal TrackingTimer[] CreatedTimers
        {
            get
            {
                lock (_sync)
                {
                    return [.. _timers];
                }
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = new TrackingTimer(callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        internal sealed class TrackingTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _disposed;
            private int _disposeCount;
            private int _callbackCount;

            internal TrackingTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            internal int DisposeCount => Volatile.Read(ref _disposeCount);
            internal int CallbackCount => Volatile.Read(ref _callbackCount);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return Volatile.Read(ref _disposed) == 0;
                }

                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                Interlocked.Increment(ref _callbackCount);
                _callback(_state);
                return true;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Increment(ref _disposeCount);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ControlledTimestampException : InvalidOperationException
    {
        internal ControlledTimestampException()
            : base("Controlled timestamp failure.") { }
    }
}
