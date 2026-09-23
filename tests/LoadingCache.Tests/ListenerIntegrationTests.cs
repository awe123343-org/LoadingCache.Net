using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LoadingCache.Notifications;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

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
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await completed.Task.WaitAsync(TestTimeout);
        await Assert
            .That(observed.Select(notification => notification.Cause))
            .IsEquivalentTo(
                [RemovalCause.Replaced, RemovalCause.Explicit],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(1);
        await Assert.That(cache.Statistics.ExplicitRemovals).IsEqualTo(1);
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
        await Assert.That(notification.Cause).IsEqualTo(RemovalCause.Size);
        await Assert.That(notification.Value).IsEqualTo("one");
        await Assert.That(cache.TryGet(2, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("two");
        await Assert.That(cache.Statistics.SizeRemovals).IsEqualTo(1);
    }

    [Test]
    public async Task EvictionListenerRunsBeforeMutationReturnsWhenRemovalSchedulerRejects()
    {
        var scheduler = new ManualNotificationScheduler { Reject = true };
        RemovalNotification<int, string>? observed = null;
        using Cache<int, string> cache = CreateManualCache(
            removalListener: _ => { },
            scheduler: scheduler,
            notificationCapacity: 1,
            evictionListener: notification => observed = notification,
            maximumSize: 1
        );
        cache.Put(1, "one");
        cache.Put(2, "two");
        Assert.NotNull(observed);
        await Assert.That(observed!.Value.Cause).IsEqualTo(RemovalCause.Size);
        await Assert.That(observed.Value.Key).IsEqualTo(1);
        await Eventually(() => cache.GetNotificationStatistics().DroppedSchedule >= 1);
        await Assert.That(cache.TryGet(2, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("two");
    }

    [Test]
    public async Task EvictionListenerFailureIsObservedWithoutBreakingTheMutation()
    {
        using Cache<int, string> cache = CreateManualCache(
            removalListener: null,
            scheduler: new ManualNotificationScheduler(),
            evictionListener: _ => throw new InvalidOperationException("eviction failure"),
            maximumSize: 1
        );
        cache.Put(1, "one");
        cache.Put(2, "two");
        await Assert.That(cache.Statistics.ListenerFailures).IsEqualTo(1);
        await Assert.That(cache.TryGet(2, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("two");
    }

    [Test]
    public async Task EvictionListenerMayReenterAfterTheEntryLockIsReleased()
    {
        var listener = new ReentrantListener();
        using Cache<int, string> cache = CreateManualCache(
            removalListener: null,
            scheduler: new ManualNotificationScheduler(),
            evictionListener: listener.OnEviction,
            maximumSize: 1
        );
        listener.Cache = cache;
        cache.Put(1, "one");
        cache.Put(2, "two");
        await Assert.That(cache.TryGet(3, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("reentrant");
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
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Eventually(() => cache.Statistics.ListenerFailures == 1);
        cache.Put(2, "two");
        await Assert.That(cache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("two");
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
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await observed.Task.WaitAsync(TestTimeout);
        await Eventually(() => cache.GetNotificationStatistics().Delivered >= 1);
        CacheNotificationStatistics statistics = cache.GetNotificationStatistics();
        await Assert.That(statistics.IsDisposed).IsFalse();
        await Assert.That(statistics.Enqueued).IsGreaterThanOrEqualTo(1);
        await Assert.That(statistics.Delivered).IsGreaterThanOrEqualTo(1);
        await Assert.That(statistics.HandlerFailures).IsEqualTo(0);
    }

    [Test]
    public async Task EagerSchedulerAllowsReentrantListenerMutation()
    {
        var listener = new ReentrantListener();
        using Cache<int, string> cache = CreateManualCache(
            removalListener: listener.OnRemoval,
            scheduler: new EagerNotificationScheduler()
        );
        listener.Cache = cache;
        cache.Put(1, "one");
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await listener.Entered.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.TryGet(2, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("reentrant");
    }

    private sealed class ReentrantListener
    {
        private int _reentries;
        internal Cache<int, string> Cache { private get; set; } = null!;
        internal TaskCompletionSource<object?> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void OnEviction(RemovalNotification<int, string> notification)
        {
            if (notification.Key == 1 && Interlocked.Exchange(ref _reentries, 1) == 0)
            {
                Cache.Put(3, "reentrant");
            }
        }

        internal void OnRemoval(RemovalNotification<int, string> notification)
        {
            Cache.Put(2, "reentrant");
            Entered.TrySetResult(null);
        }
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
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Eventually(() => cache.GetNotificationStatistics().ScheduleRejections == 1);
        CacheNotificationStatistics notificationStats = cache.GetNotificationStatistics();
        await Assert.That(notificationStats.DroppedSchedule).IsEqualTo(1);
        await Assert.That(notificationStats.Dropped).IsEqualTo(1);
        await Assert.That(callbacks).IsEqualTo(0);
        await Assert.That(cache.Statistics.ListenerDrops).IsEqualTo(1);
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
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await Eventually(() => scheduler.Pending == 1);
        cache.Put(2, "two");
        await Assert.That(cache.Invalidate(2)).IsTrue();
        await Eventually(() => cache.GetNotificationStatistics().DroppedFull == 1);
        await Assert.That(cache.GetNotificationStatistics().Dropped).IsEqualTo(1);
        await Assert.That(delivered.Task.IsCompleted).IsFalse();
        scheduler.RunNext();
        await delivered.Task.WaitAsync(TestTimeout);
        await Assert.That(cache.GetNotificationStatistics().Delivered).IsEqualTo(1);
        await Assert.That(cache.Statistics.ListenerDrops).IsEqualTo(1);
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
        Cache<int, string> cache = CreateManualCache(
            removalListener: _ =>
            {
                entered.TrySetResult(null);
                release.Task.GetAwaiter().GetResult();
            },
            scheduler: scheduler,
            notificationCapacity: 2
        );
        try
        {
            cache.Put(1, "one");
            await Assert.That(cache.Invalidate(1)).IsTrue();
            await Eventually(() => scheduler.Pending == 1);
            Task drain = Task.Run(scheduler.RunNext);
            await entered.Task.WaitAsync(TestTimeout);
            cache.Dispose();
            await Assert.That(drain.IsCompleted).IsFalse();
            CacheNotificationStatistics afterDispose = cache.GetNotificationStatistics();
            await Assert.That(afterDispose.IsDisposed).IsTrue();
            await Assert.That(afterDispose.DroppedShutdown).IsEqualTo(0);
            release.TrySetResult(null);
            await drain.WaitAsync(TestTimeout);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Test]
    public async Task DisposeDropsPendingHandoffAndKeepsShutdownDiagnosticsReadable()
    {
        var scheduler = new ManualNotificationScheduler();
        Cache<int, string> cache = CreateManualCache(
            removalListener: _ => { },
            scheduler: scheduler,
            notificationCapacity: 2
        );
        try
        {
            cache.Put(1, "one");
            await Assert.That(cache.Invalidate(1)).IsTrue();
            await Eventually(() => scheduler.Pending == 1);
            cache.Dispose();
            CacheNotificationStatistics afterDispose = cache.GetNotificationStatistics();
            await Assert.That(afterDispose.IsDisposed).IsTrue();
            await Assert.That(afterDispose.DroppedShutdown).IsEqualTo(1);
            await Assert.That(afterDispose.Dropped).IsEqualTo(1);
        }
        finally
        {
            cache.Dispose();
        }
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
        await Assert.That(cache.Get(1)).IsEqualTo("value-1");
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(cache.Get(1)).IsEqualTo("value-2");
        RemovalNotification<int, string> evictionNotification = await eviction.Task.WaitAsync(
            TestTimeout
        );
        RemovalNotification<int, string> removalNotification = await removal.Task.WaitAsync(
            TestTimeout
        );
        await Assert.That(evictionNotification.Cause).IsEqualTo(RemovalCause.Expired);
        await Assert.That(removalNotification.Cause).IsEqualTo(RemovalCause.Expired);
        await Assert.That(cache.Statistics.ExpiredRemovals).IsEqualTo(1);
        await Assert.That(cache.Statistics.Evictions).IsEqualTo(0);
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
        await Assert.That(reference.IsAlive).IsFalse();
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        RemovalNotification<int, CollectedValue> evictionNotification =
            await eviction.Task.WaitAsync(TestTimeout);
        RemovalNotification<int, CollectedValue> removalNotification = await removal.Task.WaitAsync(
            TestTimeout
        );
        await Assert.That(evictionNotification.Cause).IsEqualTo(RemovalCause.Collected);
        await Assert.That(removalNotification.Cause).IsEqualTo(RemovalCause.Collected);
        await Assert.That(removalNotification.Key).IsEqualTo(1);
        await Assert.That((removalNotification.Value) is null).IsTrue();
        await Assert.That(cache.Statistics.Collected).IsEqualTo(1);
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("old");
        await Eventually(() => cache.Statistics.RefreshFailures == 1);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("old");
        await Assert.That(notifications).IsEmpty();
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(0);
    }

    private static Cache<int, string> CreateManualCache(
        Action<RemovalNotification<int, string>>? removalListener,
        INotificationScheduler scheduler,
        int notificationCapacity = 4,
        Action<RemovalNotification<int, string>>? evictionListener = null,
        int maximumSize = 4
    ) =>
        new(
            new CacheEngine<int, string>(
                new CacheEngineOptions<int, string>
                {
                    MaximumSize = maximumSize,
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

    private sealed class CollectedValue;

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
