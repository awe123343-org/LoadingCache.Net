using System.Collections.Concurrent;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class OwnedCacheTests
{
    [Test]
    public async Task WeigherFailureAfterTransferDisposesNewValueAndPreservesOldValue()
    {
        var oldValue = new DisposableValue();
        var rejected = new DisposableValue();
        using var cache = OwnedCache.Create(
            new OwnedCacheOptions<int, DisposableValue>
            {
                MaximumWeight = 4,
                MaximumResidentCount = 4,
                MaximumActiveValues = 8,
                Weigher = (_, value) =>
                    ReferenceEquals(value, rejected)
                        ? throw new InvalidOperationException("controlled weight failure")
                        : 1,
            },
            static value => value.Dispose()
        );
        cache.Put(1, oldValue);
        await Assert.That(() => cache.Put(1, rejected)).ThrowsExactly<InvalidOperationException>();
        WaitForDisposal(rejected);
        await Assert.That(cache.TryGet(1, out var lease)).IsTrue();
        using (lease)
        {
            await Assert.That(ReferenceEquals(lease!.Value, oldValue)).IsTrue();
            await Assert.That(oldValue.DisposeCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task DisposerDoesNotCaptureRequestContextAndCanReenterEngine()
    {
        var context = new AsyncLocal<string?>();
        var observed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var disposer = new ReentrantDisposer(context, observed);
        var cache = OwnedCache.CreateAsync(CreateOptions(2, 4), disposer.DisposeAsync);
        disposer.Cache = cache;
        await using (cache)
        {
            context.Value = "request";
            cache.Put(1, new DisposableValue());
            cache.Invalidate(1);
            await Assert
                .That(((await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)))) is null)
                .IsTrue();
        }
    }

    [Test]
    public async Task CompletedValuesAreNotRootedByOwnershipHistory()
    {
        using var cache = OwnedCache.Create(CreateOptions(64, 128), static item => item.Dispose());
        WeakReference[] values = PopulateOwnedValues(cache);
        cache.Clear();
        Func<OwnedCacheDisposalStatistics> readDisposalStatistics = cache.GetDisposalStatistics;
        WaitUntil(() => readDisposalStatistics().ActiveValueCount == 0, TimeSpan.FromSeconds(5));
        for (int attempt = 0; attempt < 8 && values.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        await Assert.That(values).All(static item => !item.IsAlive);
        GC.KeepAlive(cache);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static WeakReference[] PopulateOwnedValues(OwnedCache<int, DisposableValue> cache)
    {
        var references = new WeakReference[64];
        for (int key = 0; key < references.Length; key++)
        {
            var value = new DisposableValue();
            references[key] = new WeakReference(value);
            cache.Put(key, value);
        }

        return references;
    }

    [Test]
    public async Task PutAndLeaseKeepsValueAliveAfterInvalidation()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        CacheLease<DisposableValue> lease = cache.PutAndLease(1, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Assert.That(disposed.Task.IsCompleted).IsFalse();
        await Assert.That(ReferenceEquals(lease.Value, value)).IsTrue();
        // ReSharper disable once MethodHasAsyncOverload -- Verify synchronous lease release queues disposal without waiting.
        lease.Dispose();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task PutTransfersOwnershipAndDisposesAfterRemoval()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        cache.Put(1, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetReturnsLeaseThatPinsValue()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        cache.Put(1, value);
        await Assert.That(cache.TryGet(1, out CacheLease<DisposableValue>? lease)).IsTrue();
        Assert.NotNull(lease);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Assert.That(disposed.Task.IsCompleted).IsFalse();
        // ReSharper disable once MethodHasAsyncOverload -- Verify synchronous lease release queues disposal without waiting.
        lease.Dispose();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task AliasedKeysDisposeOneObjectOnlyOnce()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 2, maximumActiveValues: 3),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        cache.Put(1, value);
        cache.Put(2, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Assert.That(disposed.Task.IsCompleted).IsFalse();
        await Assert.That(cache.Invalidate(2)).IsTrue();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task SizeEvictionRetiresValuesAfterMaintenance()
    {
        var first = new DisposableValue();
        var second = new DisposableValue();
        ConcurrentQueue<Action> disposals = new();
        var maintenance = new ManualMaintenanceScheduler();
        var cache = new OwnedCache<int, DisposableValue>(
            CreateOptions(maximumSize: 1, maximumActiveValues: 3),
            static item => item.Dispose(),
            disposeValueAsync: null,
            scheduleDisposal: work =>
            {
                disposals.Enqueue(work);
                return true;
            },
            maintenanceScheduler: maintenance
        );
        try
        {
            cache.Put(1, first);
            cache.Put(2, second);
            await Assert.That(maintenance.Pending).IsPositive();
            await Assert.That(disposals).IsEmpty();
            await Assert.That(first.DisposeCount).IsEqualTo(0);
            await Assert.That(second.DisposeCount).IsEqualTo(0);
            cache.CleanUp();
            maintenance.RunAll();
            await Assert.That(cache.EstimatedCount).IsEqualTo(1);
            await Assert.That(disposals).HasSingleItem();
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(1);
            await Assert.That(first.DisposeCount).IsEqualTo(0);
            await Assert.That(second.DisposeCount).IsEqualTo(0);
            await Assert.That(disposals.TryDequeue(out Action? disposeEvicted)).IsTrue();
            disposeEvicted!();
            await Assert.That((first.DisposeCount + second.DisposeCount)).IsEqualTo(1);
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(0);
            await Assert.That(cache.GetDisposalStatistics().ActiveValueCount).IsEqualTo(1);
            await Assert.That(disposals).IsEmpty();
            cache.Dispose();
            maintenance.RunAll();
            await Assert.That(disposals).HasSingleItem();
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(1);
            await Assert.That((first.DisposeCount + second.DisposeCount)).IsEqualTo(1);
            await Assert.That(disposals.TryDequeue(out Action? disposeRemaining)).IsTrue();
            disposeRemaining!();
            await Assert.That(first.DisposeCount).IsEqualTo(1);
            await Assert.That(second.DisposeCount).IsEqualTo(1);
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(0);
            await Assert.That(cache.GetDisposalStatistics().ActiveValueCount).IsEqualTo(0);
            await Assert.That(disposals).IsEmpty();
        }
        finally
        {
            cache.Dispose();
            maintenance.RunAll();
            while (disposals.TryDequeue(out Action? pendingDisposal))
            {
                pendingDisposal();
            }
        }
    }

    [Test]
    public async Task ExpirationRetiresValueWithoutWallClockSleep()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            new OwnedCacheOptions<int, DisposableValue>
            {
                MaximumSize = 1,
                MaximumActiveValues = 2,
                ExpireAfterWrite = TimeSpan.FromSeconds(1),
                TimeProvider = clock,
            },
            item => item.Dispose()
        );
        cache.Put(1, value);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.TryGet(1, out CacheLease<DisposableValue>? lease)).IsFalse();
        await Assert.That((lease) is null).IsTrue();
        WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ClearRetiresValueButHonoursLease()
    {
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item => item.Dispose()
        );
        cache.Put(1, value);
        await Assert.That(cache.TryGet(1, out CacheLease<DisposableValue>? lease)).IsTrue();
        cache.Clear();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        lease!.Dispose();
        WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ConcurrentDisposeCannotDisposeValueDuringWeighing()
    {
        var value = new DisposableValue();
        var weigherEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseWeigher = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = OwnedCache.Create(
            new OwnedCacheOptions<int, DisposableValue>
            {
                MaximumWeight = 10,
                MaximumResidentCount = 1,
                MaximumActiveValues = 2,
                Weigher = (_, item) =>
                {
                    weigherEntered.SetResult(null);
                    releaseWeigher.Task.GetAwaiter().GetResult();
                    return item.DisposeCount == 0 ? 1 : 2;
                },
            },
            item => item.Dispose()
        );
        try
        {
            Task put = Task.Factory.StartNew(
                static state =>
                {
                    (OwnedCache<int, DisposableValue> current, DisposableValue item) = ((
                        OwnedCache<int, DisposableValue>,
                        DisposableValue
                    ))
                        state!;
                    current.Put(1, item);
                },
                (cache, value),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            try
            {
                await weigherEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                // ReSharper disable once MethodHasAsyncOverload -- Exercise synchronous disposal while the weigher is blocked; cleanup uses the same contract.
                cache.Dispose();
                await Assert.That(value.DisposeCount).IsEqualTo(0);
                releaseWeigher.SetResult(null);
                Func<Task> awaitPut = async () => await put;
                await Assert.That(awaitPut).Throws<ObjectDisposedException>();
                WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
            }
            finally
            {
                releaseWeigher.TrySetResult(null);
                try
                {
                    await put.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown deliberately wins the pending publication in this test.
                }
            }
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- Exercise synchronous disposal while the weigher is blocked; cleanup uses the same contract.
            cache.Dispose();
        }
    }

    [Test]
    public async Task OverweightPutAndLeaseKeepsRejectedValueUsable()
    {
        var value = new DisposableValue();
        ConcurrentQueue<Action> disposals = new();
        using var cache = new OwnedCache<int, DisposableValue>(
            new OwnedCacheOptions<int, DisposableValue>
            {
                MaximumWeight = 1,
                MaximumResidentCount = 1,
                MaximumActiveValues = 2,
                Weigher = (_, _) => 2,
            },
            static item => item.Dispose(),
            disposeValueAsync: null,
            scheduleDisposal: work =>
            {
                disposals.Enqueue(work);
                return true;
            }
        );
        CacheLease<DisposableValue> lease = cache.PutAndLease(1, value);
        try
        {
            cache.CleanUp();
            await Assert.That(ReferenceEquals(lease.Value, value)).IsTrue();
            await Assert.That(value.DisposeCount).IsEqualTo(0);
            await Assert.That(disposals).IsEmpty();
            lease.Dispose();
            await Assert.That(disposals).HasSingleItem();
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(1);
            await Assert.That(value.DisposeCount).IsEqualTo(0);
            await Assert.That(disposals.TryDequeue(out Action? dispose)).IsTrue();
            dispose!();
            await Assert.That(value.DisposeCount).IsEqualTo(1);
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(0);
            await Assert.That(cache.GetDisposalStatistics().ActiveValueCount).IsEqualTo(0);
            await Assert.That(disposals).IsEmpty();
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Test]
    public async Task ActiveValueBoundIncludesLiveLeases()
    {
        var first = new DisposableValue();
        var second = new DisposableValue();
        var firstDisposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 1),
            item =>
            {
                item.Dispose();
                firstDisposed.TrySetResult(null);
            }
        );
        CacheLease<DisposableValue> lease = cache.PutAndLease(1, first);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        Action putWhileLeased = () => cache.Put(2, second);
        await Assert.That(putWhileLeased).Throws<InvalidOperationException>();
        await Assert.That(second.DisposeCount).IsEqualTo(0);
        // ReSharper disable once MethodHasAsyncOverload -- Verify synchronous lease release queues disposal without waiting.
        lease.Dispose();
        await firstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Func<OwnedCacheDisposalStatistics> readDisposalStatistics = cache.GetDisposalStatistics;
        WaitUntil(() => readDisposalStatistics().ActiveValueCount == 0, TimeSpan.FromSeconds(2));
        cache.Put(2, second);
        await Assert.That(cache.Invalidate(2)).IsTrue();
        WaitForDisposal(second);
    }

    [Test]
    public async Task DisposedValueCannotBePublishedAgain()
    {
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item => item.Dispose()
        );
        cache.Put(1, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        WaitForDisposal(value);
        Func<OwnedCacheDisposalStatistics> readDisposalStatistics = cache.GetDisposalStatistics;
        WaitUntil(() => readDisposalStatistics().ActiveValueCount == 0, TimeSpan.FromSeconds(2));
        Action republish = () => cache.Put(2, value);
        await Assert.That(republish).Throws<InvalidOperationException>();
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task AsyncDisposerDoesNotBlockCacheShutdown()
    {
        var value = new DisposableValue();
        var started = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var completed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = OwnedCache.CreateAsync(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            async item =>
            {
                started.SetResult(null);
                await release.Task.ConfigureAwait(false);
                item.Dispose();
                completed.SetResult(null);
            }
        );
        cache.Put(1, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cache.DisposeAsync();
        await Assert.That(completed.Task.IsCompleted).IsFalse();
        release.SetResult(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    [Test]
    public async Task PendingAsyncDisposerConsumesActiveValueBound()
    {
        var first = new DisposableValue();
        var second = new DisposableValue();
        ConcurrentQueue<Action> disposals = new();
        var started = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var completed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cache = new OwnedCache<int, DisposableValue>(
            CreateOptions(maximumSize: 1, maximumActiveValues: 1),
            disposeValue: null,
            disposeValueAsync: async item =>
            {
                started.TrySetResult(null);
                await release.Task.ConfigureAwait(false);
                item.Dispose();
                completed.TrySetResult(null);
            },
            scheduleDisposal: work =>
            {
                disposals.Enqueue(work);
                return true;
            }
        );
        try
        {
            cache.Put(1, first);
            await Assert.That(disposals).IsEmpty();
            await Assert.That(cache.Invalidate(1)).IsTrue();
            await Assert.That(disposals).HasSingleItem();
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(1);
            await Assert.That(first.DisposeCount).IsEqualTo(0);
            await Assert.That(started.Task.IsCompleted).IsFalse();
            await Assert.That(disposals.TryDequeue(out Action? startDisposal)).IsTrue();
            startDisposal!();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Action putWhileDisposing = () => cache.Put(2, second);
            await Assert.That(putWhileDisposing).Throws<InvalidOperationException>();
            await Assert.That(second.DisposeCount).IsEqualTo(0);
            release.SetResult(null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            WaitUntil(
                () => cache.GetDisposalStatistics().PendingDisposals == 0,
                TimeSpan.FromSeconds(2)
            );
            cache.Put(2, second);
            await Assert.That(cache.Invalidate(2)).IsTrue();
            await Assert.That(disposals).HasSingleItem();
            await Assert.That(second.DisposeCount).IsEqualTo(0);
            await Assert.That(disposals.TryDequeue(out Action? disposeSecond)).IsTrue();
            disposeSecond!();
            await Assert.That(second.DisposeCount).IsEqualTo(1);
            await Assert.That(cache.GetDisposalStatistics().PendingDisposals).IsEqualTo(0);
            await Assert.That(disposals).IsEmpty();
        }
        finally
        {
            release.TrySetResult(null);
            await cache.DisposeAsync();
            while (disposals.TryDequeue(out Action? pendingDisposal))
            {
                pendingDisposal();
            }
        }
    }

    [Test]
    public async Task DisposerFailureIsVisibleInDiagnostics()
    {
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                throw new InvalidOperationException("dispose failed");
            }
        );
        cache.Put(1, value);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        Func<OwnedCacheDisposalStatistics> readDisposalStatistics = cache.GetDisposalStatistics;
        WaitUntil(() => readDisposalStatistics().PendingDisposals == 0, TimeSpan.FromSeconds(2));
        await Assert
            .That<object>(cache.GetDisposalStatistics().LastDisposalError!)
            .IsTypeOf<InvalidOperationException>();
        await Assert.That(value.DisposeCount).IsEqualTo(1);
    }

    private static OwnedCacheOptions<int, DisposableValue> CreateOptions(
        int maximumSize,
        int maximumActiveValues
    ) => new() { MaximumSize = maximumSize, MaximumActiveValues = maximumActiveValues };

    private static void WaitForDisposal(DisposableValue value)
    {
        WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (!(SpinWait.SpinUntil(condition, timeout)))
            Assert.Fail("Expected SpinWait.SpinUntil(condition, timeout) to be true ().");
    }

    private sealed class ManualMaintenanceScheduler : IMaintenanceScheduler
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunAll()
        {
            int budget = 128;
            while (_callbacks.TryDequeue(out Action? callback))
            {
                if (--budget == 0)
                {
                    throw new InvalidOperationException("Maintenance did not quiesce.");
                }

                callback();
            }
        }
    }

    private sealed class ReentrantDisposer(
        AsyncLocal<string?> context,
        TaskCompletionSource<string?> observed
    )
    {
        internal OwnedCache<int, DisposableValue> Cache { private get; set; } = null!;

        internal async ValueTask DisposeAsync(DisposableValue item)
        {
            // The competing mutation must finish before this disposer can return.
            await Task.Run(Cache.Clear).WaitAsync(TimeSpan.FromSeconds(5));
            item.Dispose();
            observed.TrySetResult(context.Value);
        }
    }

    private sealed class DisposableValue
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
