using System.Collections.Concurrent;
using FluentAssertions;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class OwnedCacheTests
{
    [Test]
    public void WeigherFailureAfterTransferDisposesNewValueAndPreservesOldValue()
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
        FluentActions
            .Invoking(() => cache.Put(1, rejected))
            .Should()
            .ThrowExactly<InvalidOperationException>();
        WaitForDisposal(rejected);
        cache.TryGet(1, out var lease).Should().BeTrue();
        using (lease)
        {
            lease!.Value.Should().BeSameAs(oldValue);
            oldValue.DisposeCount.Should().Be(0);
        }
    }

    [Test]
    public async Task DisposerDoesNotCaptureRequestContextAndCanReenterEngine()
    {
        var context = new AsyncLocal<string?>();
        var observed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        OwnedCache<int, DisposableValue>? cache = null;
        cache = OwnedCache.CreateAsync(
            CreateOptions(2, 4),
            async item =>
            {
                // A second thread's mutation must finish before the disposer does.
                await Task.Run(cache!.Clear).WaitAsync(TimeSpan.FromSeconds(5));
                item.Dispose();
                observed.TrySetResult(context.Value);
            }
        );
        await using (cache)
        {
            context.Value = "request";
            cache.Put(1, new DisposableValue());
            cache.Invalidate(1);
            (await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
        }
    }

    [Test]
    public void CompletedValuesAreNotRootedByOwnershipHistory()
    {
        using var cache = OwnedCache.Create(CreateOptions(64, 128), static item => item.Dispose());
        WeakReference[] values = PopulateOwnedValues(cache);
        cache.Clear();
        WaitUntil(
            () => cache.GetDisposalStatistics().ActiveValueCount == 0,
            TimeSpan.FromSeconds(5)
        );
        for (int attempt = 0; attempt < 8 && values.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        values.Should().OnlyContain(static item => !item.IsAlive);
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
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        CacheLease<DisposableValue> lease = cache.PutAndLease(1, value);

        cache.Invalidate(1).Should().BeTrue();
        disposed.Task.IsCompleted.Should().BeFalse();
        lease.Value.Should().BeSameAs(value);

        lease.Dispose();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        value.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task PutTransfersOwnershipAndDisposesAfterRemoval()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );

        cache.Put(1, value);
        cache.Invalidate(1).Should().BeTrue();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        value.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task TryGetReturnsLeaseThatPinsValue()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );
        cache.Put(1, value);

        cache.TryGet(1, out CacheLease<DisposableValue>? lease).Should().BeTrue();
        lease.Should().NotBeNull();
        cache.Invalidate(1).Should().BeTrue();
        disposed.Task.IsCompleted.Should().BeFalse();

        lease!.Dispose();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        value.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task AliasedKeysDisposeOneObjectOnlyOnce()
    {
        var value = new DisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 2, maximumActiveValues: 3),
            item =>
            {
                item.Dispose();
                disposed.TrySetResult(null);
            }
        );

        cache.Put(1, value);
        cache.Put(2, value);
        cache.Invalidate(1).Should().BeTrue();
        disposed.Task.IsCompleted.Should().BeFalse();
        cache.Invalidate(2).Should().BeTrue();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        value.DisposeCount.Should().Be(1);
    }

    [Test]
    public void SizeEvictionRetiresValuesAfterMaintenance()
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
            maintenance.Pending.Should().BePositive();
            disposals.Should().BeEmpty();
            first.DisposeCount.Should().Be(0);
            second.DisposeCount.Should().Be(0);

            cache.CleanUp();
            maintenance.RunAll();
            cache.EstimatedCount.Should().Be(1);
            disposals.Should().ContainSingle();
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(1);
            first.DisposeCount.Should().Be(0);
            second.DisposeCount.Should().Be(0);

            disposals.TryDequeue(out Action? disposeEvicted).Should().BeTrue();
            disposeEvicted!();
            (first.DisposeCount + second.DisposeCount).Should().Be(1);
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(0);
            cache.GetDisposalStatistics().ActiveValueCount.Should().Be(1);
            disposals.Should().BeEmpty();

            cache.Dispose();
            maintenance.RunAll();
            disposals.Should().ContainSingle();
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(1);
            (first.DisposeCount + second.DisposeCount).Should().Be(1);

            disposals.TryDequeue(out Action? disposeRemaining).Should().BeTrue();
            disposeRemaining!();
            first.DisposeCount.Should().Be(1);
            second.DisposeCount.Should().Be(1);
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(0);
            cache.GetDisposalStatistics().ActiveValueCount.Should().Be(0);
            disposals.Should().BeEmpty();
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
    public void ExpirationRetiresValueWithoutWallClockSleep()
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
        cache.TryGet(1, out CacheLease<DisposableValue>? lease).Should().BeFalse();
        lease.Should().BeNull();
        WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
    }

    [Test]
    public void ClearRetiresValueButHonoursLease()
    {
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item => item.Dispose()
        );
        cache.Put(1, value);
        cache.TryGet(1, out CacheLease<DisposableValue>? lease).Should().BeTrue();

        cache.Clear();
        cache.EstimatedCount.Should().Be(0);
        value.DisposeCount.Should().Be(0);
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
        using var cache = OwnedCache.Create(
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

        Task put = Task.Run(() => cache.Put(1, value));
        await weigherEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.Dispose();
        value.DisposeCount.Should().Be(0);
        releaseWeigher.SetResult(null);
        Func<Task> awaitPut = async () => await put;
        await awaitPut.Should().ThrowAsync<ObjectDisposedException>();
        WaitUntil(() => value.DisposeCount == 1, TimeSpan.FromSeconds(2));
    }

    [Test]
    public void OverweightPutAndLeaseKeepsRejectedValueUsable()
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

        using CacheLease<DisposableValue> lease = cache.PutAndLease(1, value);
        cache.CleanUp();
        lease.Value.Should().BeSameAs(value);
        value.DisposeCount.Should().Be(0);
        disposals.Should().BeEmpty();
        lease.Dispose();
        disposals.Should().ContainSingle();
        cache.GetDisposalStatistics().PendingDisposals.Should().Be(1);
        value.DisposeCount.Should().Be(0);

        disposals.TryDequeue(out Action? dispose).Should().BeTrue();
        dispose!();

        value.DisposeCount.Should().Be(1);
        cache.GetDisposalStatistics().PendingDisposals.Should().Be(0);
        cache.GetDisposalStatistics().ActiveValueCount.Should().Be(0);
        disposals.Should().BeEmpty();
    }

    [Test]
    public async Task ActiveValueBoundIncludesLiveLeases()
    {
        var first = new DisposableValue();
        var second = new DisposableValue();
        var firstDisposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 1),
            item =>
            {
                item.Dispose();
                firstDisposed.TrySetResult(null);
            }
        );
        CacheLease<DisposableValue> lease = cache.PutAndLease(1, first);
        cache.Invalidate(1).Should().BeTrue();

        Action putWhileLeased = () => cache.Put(2, second);
        putWhileLeased.Should().Throw<InvalidOperationException>();
        second.DisposeCount.Should().Be(0);

        lease.Dispose();
        await firstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        WaitUntil(
            () => cache.GetDisposalStatistics().ActiveValueCount == 0,
            TimeSpan.FromSeconds(2)
        );
        cache.Put(2, second);
        cache.Invalidate(2).Should().BeTrue();
        WaitForDisposal(second);
    }

    [Test]
    public void DisposedValueCannotBePublishedAgain()
    {
        var value = new DisposableValue();
        using var cache = OwnedCache.Create(
            CreateOptions(maximumSize: 1, maximumActiveValues: 2),
            item => item.Dispose()
        );
        cache.Put(1, value);
        cache.Invalidate(1).Should().BeTrue();
        WaitForDisposal(value);
        WaitUntil(
            () => cache.GetDisposalStatistics().ActiveValueCount == 0,
            TimeSpan.FromSeconds(2)
        );

        Action republish = () => cache.Put(2, value);
        republish.Should().Throw<InvalidOperationException>();
        value.DisposeCount.Should().Be(1);
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
        cache.Invalidate(1).Should().BeTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cache.DisposeAsync();
        completed.Task.IsCompleted.Should().BeFalse();
        release.SetResult(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        value.DisposeCount.Should().Be(1);
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
            disposals.Should().BeEmpty();
            cache.Invalidate(1).Should().BeTrue();
            disposals.Should().ContainSingle();
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(1);
            first.DisposeCount.Should().Be(0);
            started.Task.IsCompleted.Should().BeFalse();
            disposals.TryDequeue(out Action? startDisposal).Should().BeTrue();
            startDisposal!();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Action putWhileDisposing = () => cache.Put(2, second);
            putWhileDisposing.Should().Throw<InvalidOperationException>();
            second.DisposeCount.Should().Be(0);

            release.SetResult(null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            WaitUntil(
                () => cache.GetDisposalStatistics().PendingDisposals == 0,
                TimeSpan.FromSeconds(2)
            );
            cache.Put(2, second);
            cache.Invalidate(2).Should().BeTrue();
            disposals.Should().ContainSingle();
            second.DisposeCount.Should().Be(0);
            disposals.TryDequeue(out Action? disposeSecond).Should().BeTrue();
            disposeSecond!();
            second.DisposeCount.Should().Be(1);
            cache.GetDisposalStatistics().PendingDisposals.Should().Be(0);
            disposals.Should().BeEmpty();
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
    public void DisposerFailureIsVisibleInDiagnostics()
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
        cache.Invalidate(1).Should().BeTrue();

        WaitUntil(
            () => cache.GetDisposalStatistics().PendingDisposals == 0,
            TimeSpan.FromSeconds(2)
        );
        cache
            .GetDisposalStatistics()
            .LastDisposalError.Should()
            .BeOfType<InvalidOperationException>();
        value.DisposeCount.Should().Be(1);
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
        SpinWait.SpinUntil(condition, timeout).Should().BeTrue();
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

    private sealed class DisposableValue
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
