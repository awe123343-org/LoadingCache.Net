using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class TimeoutFinalizationTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task CompletedAsyncLoadImmediatelyAllowsTheNextDistinctKey()
    {
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromMinutes(1))
            .BuildAsyncLoading(
                static async (key, _) =>
                {
                    await Task.Yield();
                    return key;
                }
            );
        for (int key = 0; key < 256; key++)
        {
            (await cache.GetAsync(key).AsTask().WaitAsync(Watchdog)).Should().Be(key);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingRetirementCleanupEndsWaitersAndReleasesReservation(bool statistics)
    {
        var time = new ThrowingDisposeTimeProvider();
        var builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromMinutes(1))
            .TimeProvider(time);
        if (statistics)
        {
            builder.RecordStatistics();
        }

        await using IAsyncLoadingCache<int, int> cache = builder.BuildAsyncLoading(
            static (key, _) =>
                key == 1
                    ? Task.FromException<int>(new InvalidOperationException("Backend failure."))
                    : Task.FromResult(key)
        );
        Task<int> first = cache.GetAsync(1).AsTask();
        await FluentActions
            .Awaiting(() => first.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("Backend failure.");
        (await cache.GetAsync(2).AsTask().WaitAsync(Watchdog)).Should().Be(2);
        time.DisposeCalls.Should().Be(2);
        cache.GetStatistics().MaintenanceFaults.Should().Be(statistics ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimerCleanupFailurePreservesSuccessfulValueAndTask(bool replaceValue)
    {
        var disposal = new BlockingTestHook(Watchdog);
        var time = new ThrowingDisposeTimeProvider(disposal);
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromMinutes(1))
            .TimeProvider(time)
            .RecordStatistics()
            .BuildAsyncLoading((_, _) => backend.Task);
        Task<int> pending = cache.GetAsync(1).AsTask();
        backend.TrySetResult(42);
        try
        {
            await disposal.Entered.WaitAsync(Watchdog);
            if (replaceValue)
            {
                cache.Set(1, 99);
            }

            disposal.Release();
            (await pending.WaitAsync(Watchdog)).Should().Be(42);
            int expected = replaceValue ? 99 : 42;
            cache.TryGet(1, out int value).Should().BeTrue();
            value.Should().Be(expected);
            cache.TryGetTask(1, out Task<int>? valueTask).Should().BeTrue();
            (await valueTask!.WaitAsync(Watchdog)).Should().Be(expected);
            cache.GetStatistics().MaintenanceFaults.Should().Be(1);
        }
        finally
        {
            disposal.Release();
            try
            {
                await pending.WaitAsync(Watchdog);
            }
            catch (RetirementCleanupException)
            {
                // The red implementation replaces the successful promise with cleanup failure.
            }
            finally
            {
                await cache.DisposeAsync();
                await disposal.DisposeAsync();
            }
        }

        disposal.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task ThrowingTimeoutCleanupStillCompletesTimeoutAndRetiresLateBackend()
    {
        var time = new ThrowingDisposeTimeProvider();
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .RecordStatistics();
        CacheEngine<int, int> engine = builder.CreateEngine(hasFixedLoader: true);
        var cache = new AsyncLoadingCache<int, int>(
            engine,
            (key, _) => key == 1 ? backend.Task : Task.FromResult(key)
        );
        Task<int> pending = cache.GetAsync(1).AsTask();
        try
        {
            FluentActions.Invoking(time.AdvanceToTimeout).Should().NotThrow();
            pending.IsCompleted.Should().BeTrue();
            await FluentActions
                .Awaiting(() => pending)
                .Should()
                .ThrowExactlyAsync<TimeoutException>();
            backend.TrySetResult(42);
            SpinWait.SpinUntil(() => !engine.HasActiveFlights, Watchdog).Should().BeTrue();
            (await cache.GetAsync(2).AsTask().WaitAsync(Watchdog)).Should().Be(2);
            cache.GetStatistics().MaintenanceFaults.Should().Be(1);
            cache.GetStatistics().LoadTimeouts.Should().Be(1);
        }
        finally
        {
            backend.TrySetResult(42);
            await cache.DisposeAsync();
            try
            {
                await pending.WaitAsync(Watchdog);
            }
            catch (Exception exception)
                when (exception is TimeoutException or ObjectDisposedException)
            {
                // Observe timeout, or shutdown when cleanup stranded the red implementation.
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeCompletesTimedOutWaitersAfterLateBackendCompletion(bool backendFails)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var completion = new BlockingTestHook(Watchdog);
        var backend = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .LoadTimeout(TimeSpan.FromSeconds(1))
            .TimeProvider(time);
        CacheEngine<int, int> engine = builder.CreateEngine(
            new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke },
            hasFixedLoader: true
        );
        var cache = new AsyncLoadingCache<int, int>(engine, (_, _) => backend.Task);
        Task<int> pending = cache.GetAsync(1).AsTask();
        Task<int> joined = cache.GetAsync(1).AsTask();
        Task timeout = Task.Run(() => time.Advance(TimeSpan.FromSeconds(1)));
        try
        {
            await completion.Entered.WaitAsync(Watchdog);
            if (backendFails)
            {
                backend.TrySetException(new InvalidOperationException("Late backend failure."));
            }
            else
            {
                backend.TrySetResult(42);
            }

            WaitForBackendCompletion(cache);
            pending.IsCompleted.Should().BeFalse();
            joined.IsCompleted.Should().BeFalse();
            await cache.DisposeAsync().AsTask().WaitAsync(Watchdog);
            pending
                .IsCompleted.Should()
                .BeTrue("backend completion must not hide pending timeout promises from shutdown");
            joined.IsCompleted.Should().BeTrue();
            await FluentActions
                .Awaiting(() => pending)
                .Should()
                .ThrowExactlyAsync<ObjectDisposedException>();
            await FluentActions
                .Awaiting(() => joined)
                .Should()
                .ThrowExactlyAsync<ObjectDisposedException>();
            timeout.IsCompleted.Should().BeFalse("shutdown cannot wait for timeout finalization");
        }
        finally
        {
            completion.Release();
            backend.TrySetResult(42);
            try
            {
                await timeout.WaitAsync(Watchdog);
                foreach (Task waiter in new Task[] { pending, joined })
                {
                    try
                    {
                        await waiter.WaitAsync(Watchdog);
                    }
                    catch (Exception exception)
                        when (exception is TimeoutException or ObjectDisposedException)
                    {
                        // Cleanup observes the timeout on the broken implementation and
                        // shutdown after the pending flight remains discoverable by disposal.
                    }
                }
            }
            finally
            {
                await cache.DisposeAsync();
                await completion.DisposeAsync();
            }
        }

        completion.TimedOut.Should().BeFalse();
    }

    private static void WaitForBackendCompletion(AsyncLoadingCache<int, int> cache) =>
        SpinWait
            .SpinUntil(() => cache.GetStatistics().InFlightLoads == 0, Watchdog)
            .Should()
            .BeTrue();

    private sealed class RetirementCleanupException : Exception;

    private sealed class ThrowingDisposeTimeProvider(BlockingTestHook? disposal = null)
        : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(DateTimeOffset.UnixEpoch);
        private readonly BlockingTestHook? _disposal = disposal;
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal void AdvanceToTimeout() => _inner.Advance(TimeSpan.FromSeconds(1));

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        ) => new ThrowingTimer(this, _inner.CreateTimer(callback, state, dueTime, period));

        private sealed class ThrowingTimer(ThrowingDisposeTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                inner.Dispose();
                if (Interlocked.Increment(ref owner._disposeCalls) != 1)
                {
                    return;
                }

                owner._disposal?.Invoke();
                throw new RetirementCleanupException();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
