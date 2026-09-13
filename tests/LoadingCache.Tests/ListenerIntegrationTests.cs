using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.Notifications;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ListenerIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RemovalListenerReportsReplacementAndExplicitCauses()
    {
        var observed = new ConcurrentQueue<RemovalNotification<int, string>>();
        var completed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .RemovalListener(notification =>
            {
                observed.Enqueue(notification);
                if (observed.Count >= 2)
                {
                    completed.TrySetResult(null);
                }
            })
            .RecordStatistics()
            .Build();

        cache.Put(1, "first");
        cache.Put(1, "second");
        cache.Invalidate(1).Should().BeTrue();

        await completed.Task.WaitAsync(TestTimeout);

        observed
            .Select(notification => notification.Cause)
            .Should()
            .ContainInOrder(RemovalCause.Replaced, RemovalCause.Explicit);
        cache.Statistics.ReplacedRemovals.Should().Be(1);
        cache.Statistics.ExplicitRemovals.Should().Be(1);
    }

    [Test]
    public async Task EvictionListenerReportsCapacityCauseAndDoesNotBlockCacheMutation()
    {
        var observed = new TaskCompletionSource<RemovalNotification<int, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(1)
            .MaxConcurrentLoads(4)
            .EvictionListener(notification => observed.TrySetResult(notification))
            .RecordStatistics()
            .Build();

        cache.Put(1, "one");
        cache.Put(2, "two");
        cache.CleanUp();

        RemovalNotification<int, string> notification = await observed.Task.WaitAsync(TestTimeout);
        notification.Cause.Should().Be(RemovalCause.Size);
        notification.Value.Should().Be("one");
        cache.TryGet(2, out string? current).Should().BeTrue();
        current.Should().Be("two");
        cache.Statistics.SizeRemovals.Should().Be(1);
    }

    [Test]
    public async Task ListenerFailureIsObservedAndDoesNotCorruptTheCache()
    {
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .RemovalListener(_ => throw new InvalidOperationException("listener failure"))
            .RecordStatistics()
            .Build();

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();

        await Eventually(() => cache.Statistics.ListenerFailures == 1);

        cache.Put(2, "two");
        cache.TryGet(2, out string? value).Should().BeTrue();
        value.Should().Be("two");
    }

    [Test]
    public async Task NotificationStatisticsExposeBoundedDispatcherProgress()
    {
        var observed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(2)
            .MaxConcurrentLoads(4)
            .RemovalListener(_ => observed.TrySetResult(null))
            .Build();

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();
        await observed.Task.WaitAsync(TestTimeout);
        await Eventually(() => cache.GetNotificationStatistics().Delivered >= 1);

        CacheNotificationStatistics statistics = cache.GetNotificationStatistics();
        statistics.IsDisposed.Should().BeFalse();
        statistics.Enqueued.Should().BeGreaterThanOrEqualTo(1);
        statistics.Delivered.Should().BeGreaterThanOrEqualTo(1);
        statistics.HandlerFailures.Should().Be(0);
    }

    [Test]
    public async Task EagerSchedulerAllowsReentrantListenerMutation()
    {
        var entered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Cache<int, string>? current = null;
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ =>
            {
                current!.Put(2, "reentrant");
                entered.TrySetResult(null);
            },
            scheduler: new EagerNotificationScheduler()
        );
        current = cache;

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();

        await entered.Task.WaitAsync(TestTimeout);
        cache.TryGet(2, out string? value).Should().BeTrue();
        value.Should().Be("reentrant");
    }

    [Test]
    public async Task SchedulerRejectionReportsOneDroppedBatch()
    {
        var scheduler = new ManualNotificationScheduler { Reject = true };
        int callbacks = 0;
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ => Interlocked.Increment(ref callbacks),
            scheduler: scheduler,
            notificationCapacity: 2
        );

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();

        await Eventually(() => cache.GetNotificationStatistics().ScheduleRejections == 1);

        CacheNotificationStatistics notificationStats = cache.GetNotificationStatistics();
        notificationStats.DroppedSchedule.Should().Be(1);
        notificationStats.Dropped.Should().Be(1);
        callbacks.Should().Be(0);
        cache.Statistics.ListenerDrops.Should().Be(1);
    }

    [Test]
    public async Task FullDispatcherReportsDroppedNewestWithoutLosingAcceptedEvent()
    {
        var scheduler = new ManualNotificationScheduler();
        var delivered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ => delivered.TrySetResult(null),
            scheduler: scheduler,
            notificationCapacity: 1
        );

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();
        await Eventually(() => scheduler.Pending == 1);

        cache.Put(2, "two");
        cache.Invalidate(2).Should().BeTrue();
        await Eventually(() => cache.GetNotificationStatistics().DroppedFull == 1);

        cache.GetNotificationStatistics().Dropped.Should().Be(1);
        delivered.Task.IsCompleted.Should().BeFalse();
        scheduler.RunNext();
        await delivered.Task.WaitAsync(TestTimeout);
        cache.GetNotificationStatistics().Delivered.Should().Be(1);
        cache.Statistics.ListenerDrops.Should().Be(1);
    }

    [Test]
    public async Task DisposeDoesNotWaitForSlowListenerAndReportsShutdownDrop()
    {
        var scheduler = new ManualNotificationScheduler();
        var entered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ =>
            {
                entered.TrySetResult(null);
                release.Task.GetAwaiter().GetResult();
            },
            scheduler: scheduler,
            notificationCapacity: 2
        );

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();
        await Eventually(() => scheduler.Pending == 1);

        Task drain = Task.Run(scheduler.RunNext);
        await entered.Task.WaitAsync(TestTimeout);
        cache.Dispose();
        drain.IsCompleted.Should().BeFalse();

        CacheNotificationStatistics afterDispose = cache.GetNotificationStatistics();
        afterDispose.IsDisposed.Should().BeTrue();
        afterDispose.DroppedShutdown.Should().Be(0);

        release.TrySetResult(null);
        await drain.WaitAsync(TestTimeout);
    }

    [Test]
    public async Task DisposeDropsPendingHandoffAndKeepsShutdownDiagnosticsReadable()
    {
        var scheduler = new ManualNotificationScheduler();
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ => { },
            scheduler: scheduler,
            notificationCapacity: 2
        );

        cache.Put(1, "one");
        cache.Invalidate(1).Should().BeTrue();
        await Eventually(() => scheduler.Pending == 1);

        cache.Dispose();

        CacheNotificationStatistics afterDispose = cache.GetNotificationStatistics();
        afterDispose.IsDisposed.Should().BeTrue();
        afterDispose.DroppedShutdown.Should().Be(1);
        afterDispose.Dropped.Should().Be(1);
    }

    [Test]
    public async Task ExpiredGetReportsExpiredEvictionWithoutCountingCapacityEviction()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var eviction = new TaskCompletionSource<RemovalNotification<int, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var removal = new TaskCompletionSource<RemovalNotification<int, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        using ILoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .EvictionListener(notification => eviction.TrySetResult(notification))
            .RemovalListener(notification => removal.TrySetResult(notification))
            .RecordStatistics()
            .BuildLoading(_ => $"value-{Interlocked.Increment(ref calls)}");

        cache.Get(1).Should().Be("value-1");
        time.Advance(TimeSpan.FromSeconds(1));
        cache.Get(1).Should().Be("value-2");

        RemovalNotification<int, string> evictionNotification = await eviction.Task.WaitAsync(
            TestTimeout
        );
        RemovalNotification<int, string> removalNotification = await removal.Task.WaitAsync(
            TestTimeout
        );
        evictionNotification.Cause.Should().Be(RemovalCause.Expired);
        removalNotification.Cause.Should().Be(RemovalCause.Expired);
        cache.Statistics.ExpiredRemovals.Should().Be(1);
        cache.Statistics.Evictions.Should().Be(0);
    }

    [Test]
    public async Task CollectedGetReportsCollectedEvictionWithMissingValue()
    {
        var eviction = new TaskCompletionSource<RemovalNotification<int, CollectedValue>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var removal = new TaskCompletionSource<RemovalNotification<int, CollectedValue>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using ICache<int, CollectedValue> cache = CacheBuilder
            .Create<int, CollectedValue>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .WeakValues()
            .EvictionListener(notification => eviction.TrySetResult(notification))
            .RemovalListener(notification => removal.TrySetResult(notification))
            .RecordStatistics()
            .Build();

        WeakReference reference = StoreWeakValue(cache);
        ForceCollection(reference);
        reference.IsAlive.Should().BeFalse();
        cache.TryGet(1, out _).Should().BeFalse();

        RemovalNotification<int, CollectedValue> evictionNotification =
            await eviction.Task.WaitAsync(TestTimeout);
        RemovalNotification<int, CollectedValue> removalNotification = await removal.Task.WaitAsync(
            TestTimeout
        );
        evictionNotification.Cause.Should().Be(RemovalCause.Collected);
        removalNotification.Cause.Should().Be(RemovalCause.Collected);
        removalNotification.Key.Should().Be(1);
        removalNotification.Value.Should().BeNull();
        cache.Statistics.Collected.Should().Be(1);
    }

    [Test]
    public async Task FailedRefreshPreservesOldValueWithoutRemovalNotification()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var notifications = new ConcurrentQueue<RemovalNotification<int, string>>();
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .RemovalListener(notifications.Enqueue)
            .RecordStatistics()
            .BuildAsyncLoading(
                (_, _) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    return call == 1
                        ? Task.FromResult("old")
                        : Task.FromException<string>(new InvalidOperationException("refresh"));
                }
            );

        (await cache.GetAsync(1)).Should().Be("old");
        time.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("old");
        await Eventually(() => cache.Statistics.RefreshFailures == 1);

        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("old");
        notifications.Should().BeEmpty();
        cache.Statistics.ReplacedRemovals.Should().Be(0);
    }

    private static Cache<int, string> CreateManualCache(
        Action<RemovalNotification<int, string>>? removalListener,
        INotificationScheduler scheduler,
        int notificationCapacity = 4,
        Action<RemovalNotification<int, string>>? evictionListener = null
    ) =>
        new(
            new CacheEngine<int, string>(
                new CacheEngineOptions<int, string>
                {
                    MaximumSize = 4,
                    MaxConcurrentLoads = 4,
                    RecordStatistics = true,
                    RemovalListener = removalListener,
                    EvictionListener = evictionListener,
                    NotificationCapacity = notificationCapacity,
                    NotificationScheduler = scheduler,
                }
            )
        );

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StoreWeakValue(ICache<int, CollectedValue> cache)
    {
        var value = new CollectedValue();
        cache.Put(1, value);
        return new WeakReference(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Yield();
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionException("The condition did not become true in time.");
            }

            await Task.Yield();
        }
    }

    private sealed class CollectedValue { }

    private sealed class EagerNotificationScheduler : INotificationScheduler
    {
        public bool TrySchedule(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class ManualNotificationScheduler : INotificationScheduler
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _callbacks = new();

        internal bool Reject { get; init; }

        internal int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _callbacks.Count;
                }
            }
        }

        public bool TrySchedule(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                if (Reject)
                {
                    return false;
                }

                _callbacks.Enqueue(callback);
                return true;
            }
        }

        internal void RunNext()
        {
            Action callback;
            lock (_gate)
            {
                callback = _callbacks.Dequeue();
            }

            callback();
        }
    }
}
