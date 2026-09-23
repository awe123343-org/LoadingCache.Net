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
            await Assert
                .That((await cache.GetAsync(key).AsTask().WaitAsync(Watchdog)))
                .IsEqualTo(key);
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
        await Assert
            .That(() => first.WaitAsync(Watchdog))
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("Backend failure.");
        await Assert.That((await cache.GetAsync(2).AsTask().WaitAsync(Watchdog))).IsEqualTo(2);
        await Assert.That(time.DisposeCalls).IsEqualTo(2);
        await Assert.That(cache.GetStatistics().MaintenanceFaults).IsEqualTo(statistics ? 1 : 0);
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
            await Assert.That((await pending.WaitAsync(Watchdog))).IsEqualTo(42);
            int expected = replaceValue ? 99 : 42;
            await Assert.That(cache.TryGet(1, out int value)).IsTrue();
            await Assert.That(value).IsEqualTo(expected);
            await Assert.That(cache.TryGetTask(1, out Task<int>? valueTask)).IsTrue();
            Assert.NotNull(valueTask);
            await Assert.That((await valueTask!.WaitAsync(Watchdog))).IsEqualTo(expected);
            await Assert.That(cache.GetStatistics().MaintenanceFaults).IsEqualTo(1);
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

        await Assert.That(disposal.TimedOut).IsFalse();
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
            await Assert.That(time.AdvanceToTimeout).ThrowsNothing();
            await Assert.That(pending.IsCompleted).IsTrue();
            await Assert.That(() => pending).ThrowsExactly<TimeoutException>();
            backend.TrySetResult(42);
            await Assert
                .That(SpinWait.SpinUntil(() => !engine.HasActiveFlights, Watchdog))
                .IsTrue();
            await Assert.That((await cache.GetAsync(2).AsTask().WaitAsync(Watchdog))).IsEqualTo(2);
            await Assert.That(cache.GetStatistics().MaintenanceFaults).IsEqualTo(1);
            await Assert.That(cache.GetStatistics().LoadTimeouts).IsEqualTo(1);
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
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(joined.IsCompleted).IsFalse();
            await cache.DisposeAsync().AsTask().WaitAsync(Watchdog);
            await Assert
                .That(pending.IsCompleted)
                .IsTrue()
                .Because("backend completion must not hide pending timeout promises from shutdown");
            await Assert.That(joined.IsCompleted).IsTrue();
            await Assert.That(() => pending).ThrowsExactly<ObjectDisposedException>();
            await Assert.That(() => joined).ThrowsExactly<ObjectDisposedException>();
            await Assert
                .That(timeout.IsCompleted)
                .IsFalse()
                .Because("shutdown cannot wait for timeout finalization");
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

        await Assert.That(completion.TimedOut).IsFalse();
    }

    private static void WaitForBackendCompletion(AsyncLoadingCache<int, int> cache)
    {
        if (!(SpinWait.SpinUntil(() => cache.GetStatistics().InFlightLoads == 0, Watchdog)))
            Assert.Fail("Timed out waiting for the controlled condition.");
    }

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
