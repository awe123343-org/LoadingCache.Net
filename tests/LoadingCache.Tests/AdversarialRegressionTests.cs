using System.Runtime.CompilerServices;

namespace LoadingCache.Tests;

public sealed class AdversarialRegressionTests
{
    [Test]
    public async Task DisposeAtBeforeCompletionFaultsSharedWaiterWhileHookIsHeld(
        CancellationToken cancellationToken
    )
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
            var pending = Get(cache, 1, cancellationToken).AsTask();
            try
            {
                load.TrySetResult(123);
                await AwaitWithTestTimeout(completion.Entered, cancellationToken);
                try
                {
                    await AwaitWithTestTimeout(cache.DisposeAsync().AsTask(), cancellationToken);
                    await Assert
                        .That(
                            ((Func<Task>)(() => AwaitWithTestTimeout(pending, cancellationToken)))
                        )
                        .ThrowsExactly<ObjectDisposedException>();
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

        await Assert.That(completion.TimedOut).IsFalse();
    }

    [Test]
    public async Task DisposedCacheRejectsOperationsAndGetters(CancellationToken cancellationToken)
    {
        var cache = Create<int, int>((_, _) => Task.FromResult(1));
        await cache.DisposeAsync();
        await Assert
            .That(() => Get(cache, 1, cancellationToken).AsTask())
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => cache.TryGet(1, out _)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => cache.Set(1, 1)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => cache.Invalidate(1)).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(cache.Clear).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(cache.CleanUp).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(cache.GetStatistics).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(() => _ = cache.EstimatedCount).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task StatisticsSeparateMissesFromLoaderStartsAndCoalescedWaiters(
        CancellationToken cancellationToken
    )
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
                go.Task,
                cancellationToken
            );
        }

        try
        {
            await AwaitWithTestTimeout(
                Task.WhenAll(ready.Select(signal => signal.Task)),
                cancellationToken
            );
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            await AwaitWithTestTimeout(
                Task.WhenAll(requested.Select(signal => signal.Task)),
                cancellationToken
            );
            releaseLoader.TrySetResult(true);
            int[] values = await AwaitWithTestTimeout(Task.WhenAll(waiters), cancellationToken);
            foreach (int value in values)
            {
                await Assert.That(value).IsEqualTo(130);
            }

            var statistics = cache.GetStatistics();
            await Assert.That(statistics.Misses).IsEqualTo(callerCount);
            await Assert.That(statistics.LoadsStarted).IsEqualTo(1);
            await Assert.That(statistics.CoalescedWaiters).IsEqualTo(callerCount - 1);
            await Assert.That(statistics.LoadSuccesses).IsEqualTo(1);
            await Assert.That(statistics.Hits).IsEqualTo(0);
            await Assert.That(statistics.InFlightLoads).IsEqualTo(0);
            await Assert.That(calls).IsEqualTo(1);
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
    public async Task DisabledStatisticsKeepCountersZeroButExposeRunningGauge(
        CancellationToken cancellationToken
    )
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
        var pending = Get(cache, 1, cancellationToken).AsTask();
        try
        {
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            var running = cache.GetStatistics();
            await Assert.That(running.Hits).IsEqualTo(0);
            await Assert.That(running.Misses).IsEqualTo(0);
            await Assert.That(running.LoadsStarted).IsEqualTo(0);
            await Assert.That(running.InFlightLoads).IsEqualTo(1);
            releaseLoader.TrySetResult(true);
            await Assert
                .That((await AwaitWithTestTimeout(pending, cancellationToken)))
                .IsEqualTo(131);
        }
        finally
        {
            releaseLoader.TrySetResult(true);
            await ObserveForCleanup(pending);
        }

        var idle = cache.GetStatistics();
        await Assert.That(idle.Hits).IsEqualTo(0);
        await Assert.That(idle.Misses).IsEqualTo(0);
        await Assert.That(idle.LoadsStarted).IsEqualTo(0);
        await Assert.That(idle.InFlightLoads).IsEqualTo(0);
    }

    [Test]
    public async Task SameFlightFaultIsObservedByAllWaitersAndFailureCanRetry(
        CancellationToken cancellationToken
    )
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
                go.Task,
                cancellationToken
            );
        }

        try
        {
            await AwaitWithTestTimeout(
                Task.WhenAll(ready.Select(signal => signal.Task)),
                cancellationToken
            );
            go.TrySetResult(true);
            await AwaitWithTestTimeout(loaderEntered.Task, cancellationToken);
            await AwaitWithTestTimeout(
                Task.WhenAll(requested.Select(signal => signal.Task)),
                cancellationToken
            );
            var failure = new InvalidOperationException("gated failure");
            releaseFailure.TrySetResult(failure);
            var exceptions = await AwaitWithTestTimeout(Task.WhenAll(waiters), cancellationToken);
            foreach (var exception in exceptions)
            {
                await Assert.That<object>(exception!).IsTypeOf<InvalidOperationException>();
            }

            await Assert.That(calls).IsEqualTo(1);
            await Assert.That((await Get(cache, 1, cancellationToken))).IsEqualTo(132);
            await Assert.That(calls).IsEqualTo(2);
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
    public async Task TimestampWrapDoesNotResurrectOrPrematurelyExpireValues(
        CancellationToken cancellationToken
    )
    {
        var clock = new WrappingTimeProvider(long.MaxValue - 2);
        await using var cache = Create<int, int>(
            (_, _) => Task.FromResult(133),
            Options(expireAfterAccess: TimeSpan.FromSeconds(3), timeProvider: clock)
        );
        await Get(cache, 1, cancellationToken);
        clock.Advance(2);
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(1);
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(1);
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(1);
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        clock.Advance(3);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task ComparerEqualReentrancyIsDetected(CancellationToken cancellationToken)
    {
        var loader = new DependentLoader();
        await using var cache = Create<string, int>(
            loader.LoadComparerEqualAsync,
            comparer: StringComparer.OrdinalIgnoreCase
        );
        loader.Cache = cache;
        await Assert
            .That(() =>
                AwaitWithTestTimeout(
                    Get(cache, "KEY", cancellationToken).AsTask(),
                    cancellationToken
                )
            )
            .ThrowsExactly<LoadingCacheReentrancyException>();
        await Assert.That(loader.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task DifferentKeyDependencyIsAllowedAndScopeIsRestored(
        CancellationToken cancellationToken
    )
    {
        var loader = new DependentLoader();
        await using var cache = Create<string, int>(loader.LoadDifferentKeyAsync);
        loader.Cache = cache;
        await Assert.That((await Get(cache, "K", cancellationToken))).IsEqualTo(141);
        await Assert.That((await Get(cache, "J", cancellationToken))).IsEqualTo(140);
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
    public async Task LoadPermitRemainsReservedUntilBeforeCompletionReturns(
        CancellationToken cancellationToken
    )
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
        Task<int> first = Task.Run(() => Get(cache, 1, cancellationToken).AsTask());
        try
        {
            await AwaitWithTestTimeout(completion.Entered, cancellationToken);
            await Assert
                .That(
                    (
                        (Func<Task>)(
                            () =>
                                AwaitWithTestTimeout(
                                    Get(cache, 2, cancellationToken).AsTask(),
                                    cancellationToken
                                )
                        )
                    )
                )
                .ThrowsExactly<CacheLoadRejectedException>();
            completion.Release();
            await Assert
                .That((await AwaitWithTestTimeout(first, cancellationToken)))
                .IsEqualTo(150);
            await Assert.That((await Get(cache, 2, cancellationToken))).IsEqualTo(151);
            await Assert.That(completion.TimedOut).IsFalse();
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
            await Assert
                .That(Collects(weakPayload))
                .IsTrue()
                .Because("The cache-owned observer retained the caller ExecutionContext payload.");
        }
        finally
        {
            load.TrySetResult(151);
            await AwaitForCleanup(cache.DisposeAsync().AsTask());
        }
    }

    private static IAsyncLoadingCache<TKey, TValue> Create<TKey, TValue>(
        Func<TKey, CancellationToken, Task<TValue>> loader,
        LoadingTestOptions? options = null,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull => (options ?? Options()).Build(loader, comparer);

    private static ValueTask<TValue> Get<TKey, TValue>(
        IAsyncLoadingCache<TKey, TValue> cache,
        TKey key,
        CancellationToken cancellationToken
    )
        where TKey : notnull
        where TValue : notnull => cache.GetAsync(key, cancellationToken);

    private static LoadingTestOptions Options(
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
        Task gate,
        CancellationToken cancellationToken
    )
    {
        ready.TrySetResult(true);
        await gate.ConfigureAwait(false);
        var flight = Get(cache, key, cancellationToken).AsTask();
        requested.TrySetResult(true);
        return await flight.ConfigureAwait(false);
    }

    private static async Task<Exception?> CaptureExceptionAfterGateAsync(
        IAsyncLoadingCache<int, int> cache,
        int key,
        TaskCompletionSource<bool> ready,
        TaskCompletionSource<bool> requested,
        Task gate,
        CancellationToken cancellationToken
    )
    {
        ready.TrySetResult(true);
        await gate.ConfigureAwait(false);
        var flight = Get(cache, key, cancellationToken).AsTask();
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
        await Assert
            .That(() => waiter.WaitAsync(TestTimeout, CancellationToken.None))
            .Throws<OperationCanceledException>();
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

    private static async Task AwaitWithTestTimeout(Task task, CancellationToken cancellationToken)
    {
        await task.WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> AwaitWithTestTimeout<T>(
        Task<T> task,
        CancellationToken cancellationToken
    )
    {
        return await task.WaitAsync(TestTimeout, cancellationToken).ConfigureAwait(false);
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
