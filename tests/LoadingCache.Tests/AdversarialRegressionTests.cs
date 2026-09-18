using System.Runtime.CompilerServices;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class AdversarialRegressionTests
{
    [Test]
    public async Task DisposeAtBeforeCompletionFaultsSharedWaiterWhileHookIsHeld()
    {
        await using var completion = new BlockingTestHook(TestTimeout);
        var hooks = new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke };

        var load = Signal<int>();
        IAsyncLoadingCache<int, int> cache = Create<int, int>(
            (_, _) => load.Task,
            Options(testHooks: hooks)
        );
        try
        {
            var pending = Get(cache, 1).AsTask();
            try
            {
                load.TrySetResult(123);
                await AwaitWithTestTimeout(completion.Entered);

                try
                {
                    await AwaitWithTestTimeout(cache.DisposeAsync().AsTask());
                    await ((Func<Task>)(() => AwaitWithTestTimeout(pending)))
                        .Should()
                        .ThrowExactlyAsync<ObjectDisposedException>();
                }
                finally
                {
                    completion.Release();
                }
            }
            finally
            {
                completion.Release();
                await ObserveForCleanup(pending);
            }
        }
        finally
        {
            completion.Release();
            try
            {
                if (completion.Entered.IsCompleted)
                {
                    await AwaitForCleanup(completion.Returned);
                }
            }
            finally
            {
                await AwaitForCleanup(cache.DisposeAsync().AsTask());
            }
        }

        completion.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task DisposedCacheRejectsOperationsAndGetters()
    {
        var cache = Create<int, int>((_, _) => Task.FromResult(1));
        await cache.DisposeAsync();

        await FluentActions
            .Awaiting(() => Get(cache, 1).AsTask())
            .Should()
            .ThrowExactlyAsync<ObjectDisposedException>();
        FluentActions
            .Invoking(() => cache.TryGet(1, out _))
            .Should()
            .ThrowExactly<ObjectDisposedException>();
        FluentActions
            .Invoking(() => cache.Set(1, 1))
            .Should()
            .ThrowExactly<ObjectDisposedException>();
        FluentActions
            .Invoking(() => cache.Invalidate(1))
            .Should()
            .ThrowExactly<ObjectDisposedException>();
        FluentActions.Invoking(cache.Clear).Should().ThrowExactly<ObjectDisposedException>();
        FluentActions.Invoking(cache.CleanUp).Should().ThrowExactly<ObjectDisposedException>();
        FluentActions
            .Invoking(cache.GetStatistics)
            .Should()
            .ThrowExactly<ObjectDisposedException>();
        FluentActions
            .Invoking(() => _ = cache.EstimatedCount)
            .Should()
            .ThrowExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task StatisticsSeparateMissesFromLoaderStartsAndCoalescedWaiters()
    {
        const int callerCount = 128;
        var loaderEntered = Signal();
        var releaseLoader = Signal();
        int calls = 0;
        IAsyncLoadingCache<int, int> cache = Create<int, int>(
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                loaderEntered.TrySetResult(true);
                await releaseLoader.Task.ConfigureAwait(false);
                return 130;
            },
            Options(recordStatistics: true)
        );

        var ready = Enumerable.Range(0, callerCount).Select(_ => Signal()).ToArray();
        var requested = Enumerable.Range(0, callerCount).Select(_ => Signal()).ToArray();
        var go = Signal();
        var waiters = new Task<int>[callerCount];
        for (int index = 0; index < callerCount; index++)
        {
            waiters[index] = RequestAfterGateAsync(
                cache,
                1,
                ready[index],
                requested[index],
                go.Task
            );
        }
        try
        {
            await AwaitWithTestTimeout(Task.WhenAll(ready.Select(signal => signal.Task)));
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task);
            await AwaitWithTestTimeout(Task.WhenAll(requested.Select(signal => signal.Task)));
            releaseLoader.TrySetResult(true);
            int[] values = await AwaitWithTestTimeout(Task.WhenAll(waiters));

            foreach (int value in values)
            {
                value.Should().Be(130);
            }
            var statistics = cache.GetStatistics();
            statistics.Misses.Should().Be(callerCount);
            statistics.LoadsStarted.Should().Be(1);
            statistics.CoalescedWaiters.Should().Be(callerCount - 1);
            statistics.LoadSuccesses.Should().Be(1);
            statistics.Hits.Should().Be(0);
            statistics.InFlightLoads.Should().Be(0);
            calls.Should().Be(1);
        }
        finally
        {
            go.TrySetResult(true);
            releaseLoader.TrySetResult(true);
            try
            {
                await ObserveForCleanup(Task.WhenAll(waiters));
            }
            finally
            {
                await AwaitForCleanup(cache.DisposeAsync().AsTask());
            }
        }
    }

    [Test]
    public async Task DisabledStatisticsKeepCountersZeroButExposeRunningGauge()
    {
        var loaderEntered = Signal();
        var releaseLoader = Signal();
        await using var cache = Create<int, int>(
            async (_, _) =>
            {
                loaderEntered.TrySetResult(true);
                await releaseLoader.Task.ConfigureAwait(false);
                return 131;
            },
            Options(recordStatistics: false)
        );

        var pending = Get(cache, 1).AsTask();
        try
        {
            await AwaitWithTestTimeout(loaderEntered.Task);
            var running = cache.GetStatistics();
            running.Hits.Should().Be(0);
            running.Misses.Should().Be(0);
            running.LoadsStarted.Should().Be(0);
            running.InFlightLoads.Should().Be(1);
            releaseLoader.TrySetResult(true);
            (await AwaitWithTestTimeout(pending)).Should().Be(131);
        }
        finally
        {
            releaseLoader.TrySetResult(true);
            await ObserveForCleanup(pending);
        }

        var idle = cache.GetStatistics();
        idle.Hits.Should().Be(0);
        idle.Misses.Should().Be(0);
        idle.LoadsStarted.Should().Be(0);
        idle.InFlightLoads.Should().Be(0);
    }

    [Test]
    public async Task SameFlightFaultIsObservedByAllWaitersAndFailureCanRetry()
    {
        const int callerCount = 128;
        var loaderEntered = Signal();
        var releaseFailure = Signal<Exception>();
        int calls = 0;
        IAsyncLoadingCache<int, int> cache = Create<int, int>(
            async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) != 1)
                {
                    return 132;
                }

                loaderEntered.TrySetResult(true);
                Exception failure = await releaseFailure.Task.ConfigureAwait(false);
                throw failure;
            }
        );

        var ready = Enumerable.Range(0, callerCount).Select(_ => Signal()).ToArray();
        var requested = Enumerable.Range(0, callerCount).Select(_ => Signal()).ToArray();
        var go = Signal();
        var waiters = new Task<Exception?>[callerCount];
        for (int index = 0; index < callerCount; index++)
        {
            waiters[index] = CaptureExceptionAfterGateAsync(
                cache,
                1,
                ready[index],
                requested[index],
                go.Task
            );
        }
        try
        {
            await AwaitWithTestTimeout(Task.WhenAll(ready.Select(signal => signal.Task)));
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task);
            await AwaitWithTestTimeout(Task.WhenAll(requested.Select(signal => signal.Task)));
            var failure = new InvalidOperationException("gated failure");
            releaseFailure.TrySetResult(failure);
            var exceptions = await AwaitWithTestTimeout(Task.WhenAll(waiters));
            foreach (var exception in exceptions)
            {
                exception.Should().BeOfType<InvalidOperationException>();
            }
            calls.Should().Be(1);
            (await Get(cache, 1)).Should().Be(132);
            calls.Should().Be(2);
        }
        finally
        {
            go.TrySetResult(true);
            releaseFailure.TrySetResult(new InvalidOperationException("cleanup failure"));
            try
            {
                await ObserveForCleanup(Task.WhenAll(waiters));
            }
            finally
            {
                await AwaitForCleanup(cache.DisposeAsync().AsTask());
            }
        }
    }

    [Test]
    public async Task TimestampWrapDoesNotResurrectOrPrematurelyExpireValues()
    {
        var clock = new WrappingTimeProvider(long.MaxValue - 2);
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(133),
            Options(expireAfterAccess: TimeSpan.FromSeconds(3), timeProvider: clock)
        );

        await Get(cache, 1);
        clock.Advance(2);
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(1);
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(1);
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(1);
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(3);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Test]
    public async Task ComparerEqualReentrancyIsDetected()
    {
        var loader = new DependentLoader();
        await using var cache = Create<string, int>(
            loader.LoadComparerEqualAsync,
            comparer: StringComparer.OrdinalIgnoreCase
        );
        loader.Cache = cache;

        await cache
            .Awaiting(static current => AwaitWithTestTimeout(Get(current, "KEY").AsTask()))
            .Should()
            .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        loader.Calls.Should().Be(1);
    }

    [Test]
    public async Task DifferentKeyDependencyIsAllowedAndScopeIsRestored()
    {
        var loader = new DependentLoader();
        await using var cache = Create<string, int>(loader.LoadDifferentKeyAsync);
        loader.Cache = cache;
        (await Get(cache, "K")).Should().Be(141);
        (await Get(cache, "J")).Should().Be(140);
    }

    private sealed class DependentLoader
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal IAsyncLoadingCache<string, int> Cache { private get; set; } = null!;

        internal async Task<int> LoadComparerEqualAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _calls);
            return await Get(Cache, key.ToLowerInvariant(), CancellationToken.None);
        }

        internal async Task<int> LoadDifferentKeyAsync(
            string key,
            CancellationToken cancellationToken
        )
        {
            if (key == "K")
            {
                return await Get(Cache, "J", CancellationToken.None) + 1;
            }

            return 140;
        }
    }

    [Test]
    public async Task LoadPermitRemainsReservedUntilBeforeCompletionReturns()
    {
        await using var completion = new BlockingTestHook(TestTimeout);
        int calls = 0;
        var hooks = new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke };

        IAsyncLoadingCache<int, int> cache = Create<int, int>(
            (_, _) =>
            {
                int call = Interlocked.Increment(ref calls);
                return Task.FromResult(call == 1 ? 150 : 151);
            },
            Options(maxConcurrentLoads: 1, testHooks: hooks)
        );

        Task<int> first = Task.Run(() => Get(cache, 1).AsTask());
        try
        {
            await AwaitWithTestTimeout(completion.Entered);
            await ((Func<Task>)(() => AwaitWithTestTimeout(Get(cache, 2).AsTask())))
                .Should()
                .ThrowExactlyAsync<CacheLoadRejectedException>();
            completion.Release();
            (await AwaitWithTestTimeout(first)).Should().Be(150);
            (await Get(cache, 2)).Should().Be(151);
            completion.TimedOut.Should().BeFalse();
        }
        finally
        {
            completion.Release();
            try
            {
                try
                {
                    await AwaitForCleanup(first);
                }
                finally
                {
                    if (completion.Entered.IsCompleted)
                    {
                        await AwaitForCleanup(completion.Returned);
                    }
                }
            }
            finally
            {
                await AwaitForCleanup(cache.DisposeAsync().AsTask());
            }
        }
    }

    [Test]
    public async Task LoadObserverDoesNotRetainAmbientExecutionContextPayload()
    {
        var load = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        IAsyncLoadingCache<int, int> cache = Create<int, int>((_, _) => load.Task);
        try
        {
            var weakPayload = await StartLoadWithAmbientPayloadAsync(cache);
            Collects(weakPayload)
                .Should()
                .BeTrue("The cache-owned observer retained the caller ExecutionContext payload.");
        }
        finally
        {
            load.TrySetResult(151);
            await AwaitForCleanup(cache.DisposeAsync().AsTask());
        }
    }

    private static IAsyncLoadingCache<TKey, TValue> Create<TKey, TValue>(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingCacheOptions? options = null,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull => LoadingCache.Create(loader, options ?? Options(), comparer);

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, TestContext.CurrentContext.CancellationToken);

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key,
        CancellationToken cancellationToken
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, cancellationToken);

    private static LoadingCacheOptions Options(
        int maximumSize = 128,
        int maxConcurrentLoads = 128,
        TimeSpan? expireAfterWrite = null,
        TimeSpan? expireAfterAccess = null,
        TimeProvider? timeProvider = null,
        bool recordStatistics = false,
        LoadingCacheTestHooks? testHooks = null
    ) =>
        new()
        {
            MaximumSize = maximumSize,
            MaxConcurrentLoads = maxConcurrentLoads,
            ExpireAfterWrite = expireAfterWrite,
            ExpireAfterAccess = expireAfterAccess,
            TimeProvider = timeProvider ?? TimeProvider.System,
            RecordStatistics = recordStatistics,
            TestHooks = testHooks,
        };

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<int> RequestAfterGateAsync(
        IAsyncLoadingCache<int, int> cache,
        int key,
        TaskCompletionSource<bool> ready,
        TaskCompletionSource<bool> requested,
        Task gate
    )
    {
        ready.TrySetResult(true);
        await gate.ConfigureAwait(false);
        var flight = Get(cache, key).AsTask();
        requested.TrySetResult(true);
        return await flight.ConfigureAwait(false);
    }

    private static async Task<Exception?> CaptureExceptionAfterGateAsync(
        IAsyncLoadingCache<int, int> cache,
        int key,
        TaskCompletionSource<bool> ready,
        TaskCompletionSource<bool> requested,
        Task gate
    )
    {
        ready.TrySetResult(true);
        await gate.ConfigureAwait(false);
        var flight = Get(cache, key).AsTask();
        requested.TrySetResult(true);
        return await CaptureExceptionAsync(() => flight).ConfigureAwait(false);
    }

    private static readonly AsyncLocal<object?> AmbientPayload = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StartLoadWithAmbientPayloadAsync(
        IAsyncLoadingCache<int, int> cache
    )
    {
        using CancellationTokenSource cancellation = new();
        object payload = new byte[1024 * 1024];
        AmbientPayload.Value = payload;
        Task<int> waiter;
        try
        {
            waiter = Get(cache, 1, cancellation.Token).AsTask();
        }
        finally
        {
            AmbientPayload.Value = null;
        }

        var weakPayload = new WeakReference(payload);
        await cancellation.CancelAsync();
        await FluentActions
            .Awaiting(() => waiter.WaitAsync(TestTimeout, CancellationToken.None))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        // Return only the weak reference after observing the canceled waiter.
        // The GC assertion must not depend on this helper's strong local roots.
        return weakPayload;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Collects(WeakReference weakReference)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakReference.IsAlive)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task AwaitWithTestTimeout(Task task)
    {
        await task.WaitAsync(TestTimeout, TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AwaitForCleanup(Task task)
    {
        await task.WaitAsync(TestTimeout, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ObserveForCleanup(Task task)
    {
        try
        {
            await AwaitForCleanup(task).ConfigureAwait(false);
        }
        catch (Exception) when (task.IsCompleted) { }
    }

    private static async Task<T> AwaitWithTestTimeout<T>(Task<T> task)
    {
        return await task.WaitAsync(TestTimeout, TestContext.CurrentContext.CancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private sealed class WrappingTimeProvider(long initialTimestamp) : TimeProvider
    {
        private long _timestamp = initialTimestamp;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public void Advance(long ticks)
        {
            Interlocked.Add(ref _timestamp, ticks);
        }
    }
}
