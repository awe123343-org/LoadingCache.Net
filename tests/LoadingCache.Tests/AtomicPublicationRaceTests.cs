using FluentAssertions;
using JetBrains.Annotations;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class AtomicPublicationRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task TaskLookupDuringResidentPutKeepsValueAndTaskPaired(bool materializeOldTask)
    {
        await using var publication = new BlockingTestHook(Watchdog);
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .CreateEngine(
                new LoadingCacheTestHooks { BeforeResidentValuePublished = publication.Invoke },
                hasFixedLoader: true,
                isAsync: true,
                supportsBulkLoading: false
            );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("unexpected")
        );
        await VerifyTaskLookupDuringResidentPut(cache, publication, materializeOldTask);
    }

    private static async Task VerifyTaskLookupDuringResidentPut(
        AsyncLoadingCache<int, string> cache,
        BlockingTestHook publication,
        bool materializeOldTask
    )
    {
        cache.Set(1, "old");
        Task<string>? oldTask = null;
        if (materializeOldTask)
        {
            cache.TryGetTask(1, out oldTask).Should().BeTrue();
        }

        var readerEntered = NewSignal();
        Task<TaskObservation<string>>? reader = null;
        Task writer = Task.Run(() => cache.Set(1, "new"));
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            cache.TryGet(1, out string? oldValue).Should().BeTrue();
            oldValue.Should().Be("old");
            reader = Task.Run(() =>
            {
                readerEntered.TrySetResult();
                cache.TryGetTask(1, out Task<string>? task).Should().BeTrue();
                return new TaskObservation<string>(task!);
            });
            await readerEntered.Task.WaitAsync(Watchdog);
            reader.IsCompleted.Should().BeFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(writer, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
        }

        publication.TimedOut.Should().BeFalse();
        Task<string> observed = (await reader).Task;
        (await observed).Should().Be("new");
        cache.TryGetTask(1, out Task<string>? current).Should().BeTrue();
        current.Should().BeSameAs(observed);
        if (oldTask is not null)
        {
            observed.Should().NotBeSameAs(oldTask);
            (await oldTask).Should().Be("old");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public Task ExplicitReferenceRefreshPublicationSurvivesLaterSet(bool fixedExpiration) =>
        VerifyRefreshPublication(new Payload(), new Payload(), new Payload(), fixedExpiration);

    [TestCase(false)]
    [TestCase(true)]
    public Task ExplicitInt64RefreshPublicationSurvivesLaterSet(bool fixedExpiration) =>
        VerifyRefreshPublication(
            0x12345678abcdef01L,
            0x23456789abcdef12L,
            0x3456789abcdef123L,
            fixedExpiration
        );

    [Test]
    public Task FixedExpirationLargeStructRefreshPublicationSurvivesLaterSet() =>
        VerifyRefreshPublication(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            new LargeValue(9, 10, 11, 12),
            fixedExpiration: true
        );

    private static async Task VerifyRefreshPublication<TValue>(
        TValue oldValue,
        TValue refreshedValue,
        TValue replacement,
        bool fixedExpiration
    )
        where TValue : notnull
    {
        await using var completion = new BlockingTestHook(Watchdog);
        var engine = CreateEngine<TValue>(
            new LoadingCacheTestHooks { BeforeCompletion = completion.Invoke },
            fixedExpiration
        );
        await using var cache = new AsyncLoadingCache<int, TValue>(
            engine,
            (_, _) => Task.FromResult(refreshedValue)
        );
        cache.Set(1, oldValue);
        cache.TryGetTask(1, out Task<TValue>? oldTask).Should().BeTrue();
        Task<TValue> refresh = Task
            .Factory.StartNew(
                static async state =>
                    await ((AsyncLoadingCache<int, TValue>)state!).RefreshAsync(1),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        try
        {
            await completion.Entered.WaitAsync(Watchdog);
            refresh.IsCompleted.Should().BeFalse();
            cache.TryGet(1, out TValue? published).Should().BeTrue();
            published.Should().Be(refreshedValue);
            cache.TryGetTask(1, out Task<TValue>? publishedTask).Should().BeTrue();
            (await publishedTask!).Should().Be(refreshedValue);
            cache.Set(1, replacement);
            cache.TryGetTask(1, out Task<TValue>? replacementTask).Should().BeTrue();
            (await replacementTask!).Should().Be(replacement);
            (await publishedTask).Should().Be(refreshedValue);
            (await oldTask!).Should().Be(oldValue);
        }
        finally
        {
            completion.Release();
            await refresh.WaitAsync(Watchdog);
        }

        completion.TimedOut.Should().BeFalse();
        (await refresh).Should().Be(refreshedValue);
        cache.TryGet(1, out TValue? final).Should().BeTrue();
        final.Should().Be(replacement);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public Task ExplicitReferenceRefreshFailureFencesLaterSet(
        bool replaceBeforeFailure,
        bool fixedExpiration
    ) =>
        VerifyRefreshRollback(
            new Payload(),
            new Payload(),
            new Payload(),
            replaceBeforeFailure,
            fixedExpiration
        );

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public Task ExplicitInt64RefreshFailureFencesLaterSet(
        bool replaceBeforeFailure,
        bool fixedExpiration
    ) =>
        VerifyRefreshRollback(
            0x12345678abcdef01L,
            0x23456789abcdef12L,
            0x3456789abcdef123L,
            replaceBeforeFailure,
            fixedExpiration
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task FixedExpirationLargeStructRefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            new LargeValue(9, 10, 11, 12),
            replaceBeforeFailure,
            fixedExpiration: true
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task AccessExpirationReferenceRefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            new Payload(),
            new Payload(),
            new Payload(),
            replaceBeforeFailure,
            fixedExpiration: false,
            accessExpiration: true
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task AccessExpirationInt64RefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            0x12345678abcdef01L,
            0x23456789abcdef12L,
            0x3456789abcdef123L,
            replaceBeforeFailure,
            fixedExpiration: false,
            accessExpiration: true
        );

    [TestCase(false)]
    [TestCase(true)]
    public Task AccessExpirationLargeStructRefreshFailureFencesLaterSet(
        bool replaceBeforeFailure
    ) =>
        VerifyRefreshRollback(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            new LargeValue(9, 10, 11, 12),
            replaceBeforeFailure,
            fixedExpiration: false,
            accessExpiration: true
        );

    private static async Task VerifyRefreshRollback<TValue>(
        TValue oldValue,
        TValue refreshedValue,
        TValue replacement,
        bool replaceBeforeFailure,
        bool fixedExpiration,
        bool accessExpiration = false
    )
        where TValue : notnull
    {
        await using var publication = new BlockingTestHook(Watchdog);
        var engine = CreateEngine<TValue>(
            FailureHooks(publication),
            fixedExpiration,
            accessExpiration
        );
        await using var cache = new AsyncLoadingCache<int, TValue>(
            engine,
            (_, _) => Task.FromResult(refreshedValue)
        );
        cache.Set(1, oldValue);
        cache.TryGetTask(1, out Task<TValue>? oldTask).Should().BeTrue();
        Task<TValue> refresh = Task
            .Factory.StartNew(
                static async state =>
                    await ((AsyncLoadingCache<int, TValue>)state!).RefreshAsync(1),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<TValue>? publishedTask;
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            cache.TryGet(1, out TValue? published).Should().BeTrue();
            published.Should().Be(refreshedValue);
            cache.TryGetTask(1, out publishedTask).Should().BeTrue();
            (await publishedTask!).Should().Be(refreshedValue);
            if (replaceBeforeFailure)
            {
                cache.Set(1, replacement);
            }
        }
        finally
        {
            publication.Release();
            await ObserveControlledFailure(refresh);
        }

        publication.TimedOut.Should().BeFalse();
        cache.TryGet(1, out TValue? restored).Should().BeTrue();
        restored.Should().Be(replaceBeforeFailure ? replacement : oldValue);
        cache.TryGetTask(1, out Task<TValue>? current).Should().BeTrue();
        (await current!).Should().Be(restored);
        if (!replaceBeforeFailure)
        {
            current.Should().BeSameAs(oldTask);
            cache.Set(1, replacement);
        }

        (await oldTask!).Should().Be(oldValue);
        (await publishedTask).Should().Be(refreshedValue);
        cache.TryGet(1, out TValue? final).Should().BeTrue();
        final.Should().Be(replacement);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
    }

    [Test]
    public async Task PressureSnapshotFromFailedRefreshCannotRemoveLaterResidentPut()
    {
        await using var publication = new BlockingTestHook(Watchdog);
        var engine = CreateEngine<string>(FailureHooks(publication));
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("refresh")
        );
        cache.Set(1, "old");
        Task<string> refresh = Task
            .Factory.StartNew(
                static async state =>
                    await ((AsyncLoadingCache<int, string>)state!).RefreshAsync(1),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        MemoryPressureSnapshot? stale;
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            stale = engine.CaptureMemoryPressureSnapshot(1, 1);
            stale.Should().NotBeNull();
            stale.Candidates.Should().ContainSingle();
            cache.TryGet(1, out string? value).Should().BeTrue();
            value.Should().Be("refresh");
        }
        finally
        {
            publication.Release();
            await ObserveControlledFailure(refresh);
        }

        publication.TimedOut.Should().BeFalse();
        cache.TryGet(1, out string? restored).Should().BeTrue();
        restored.Should().Be("old");
        cache.Set(1, "new");
        engine.TrimForMemoryPressure(stale).Should().Be(0);
        cache.TryGet(1, out string? current).Should().BeTrue();
        current.Should().Be("new");
    }

    [Test]
    public async Task DictionarySnapshotFromFailedRefreshRetriesAfterLaterResidentPut()
    {
        await using var publication = new BlockingTestHook(Watchdog);
        await using var transform = new BlockingTestHook(Watchdog);
        Action pauseTransform = transform.Invoke;
        var engine = CreateEngine<string>(FailureHooks(publication));
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("refresh")
        );
        using var manual = new Cache<int, string>(engine);
        SyncCacheDictionary<int, string> dictionary = manual.AsDictionary();
        cache.Set(1, "old");
        Task<string> refresh = Task
            .Factory.StartNew(
                static async state =>
                    await ((AsyncLoadingCache<int, string>)state!).RefreshAsync(1),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<CacheMutation<string>>? compute = null;
        int callbacks = 0;
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            compute = Task.Run(() =>
                dictionary.Compute(
                    1,
                    (_, current) =>
                    {
                        if (Interlocked.Increment(ref callbacks) == 1)
                        {
                            current.Value.Should().Be("refresh");
                            pauseTransform();
                            return CacheMutation.Set("stale transform");
                        }

                        current.Value.Should().Be("new");
                        return CacheMutation.Keep<string>();
                    }
                )
            );
            await transform.Entered.WaitAsync(Watchdog);
            publication.Release();
            await ObserveControlledFailure(refresh);
            cache.Set(1, "new");
            transform.Release();
            (await compute.WaitAsync(Watchdog)).Kind.Should().Be(CacheMutationKind.Keep);
            callbacks.Should().Be(2);
            dictionary[1].Should().Be("new");
        }
        finally
        {
            publication.Release();
            transform.Release();
            await Task.WhenAll(ObserveControlledFailure(refresh), compute ?? Task.CompletedTask)
                .WaitAsync(Watchdog);
        }

        publication.TimedOut.Should().BeFalse();
        transform.TimedOut.Should().BeFalse();
    }

    [Test]
    public async Task VariableReadFromFailedRefreshCannotExpireRetriedRefreshAtTheSameTimestamp()
    {
        await using var publication = new BlockingTestHook(Watchdog);
        await using var readExpiry = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        Action pausePublication = publication.Invoke;
        int publications = 0;
        int reloads = 0;
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                Expiry = new BlockingReadExpiry(readExpiry),
                SupportsBulkLoading = false,
                TestHooks = new LoadingCacheTestHooks
                {
                    AfterRefreshPublished = () =>
                    {
                        if (Interlocked.Increment(ref publications) != 1)
                        {
                            return;
                        }

                        pausePublication();
                        throw new ControlledPublicationFailure();
                    },
                },
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) =>
                Task.FromResult(
                    Interlocked.Increment(ref reloads) == 1 ? "failed refresh" : "retry"
                )
        );
        cache.Set(1, "old");
        long timestamp = clock.GetTimestamp();
        Task<string> refresh = Task
            .Factory.StartNew(
                static async state =>
                    await ((AsyncLoadingCache<int, string>)state!).RefreshAsync(1),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<string>? reader = null;
        Task<string>? retry = null;
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            reader = Task.Factory.StartNew(
                static state =>
                {
                    ((AsyncLoadingCache<int, string>)state!)
                        .TryGet(1, out string? value)
                        .Should()
                        .BeTrue();
                    return value!;
                },
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await readExpiry.Entered.WaitAsync(Watchdog);
            publication.Release();
            await ObserveControlledFailure(refresh);

            retry = cache.RefreshAsync(1).AsTask();
            (await retry.WaitAsync(Watchdog)).Should().Be("retry");
            clock.GetTimestamp().Should().Be(timestamp);
            readExpiry.Release();
            (await reader.WaitAsync(Watchdog)).Should().Be("failed refresh");

            cache.Policy.VariableExpiration!.GetExpiresAfter(1).Should().Be(TimeSpan.FromHours(1));
            cache.TryGet(1, out string? current).Should().BeTrue();
            current.Should().Be("retry");
            cache.CleanUp();
            cache.EstimatedCount.Should().Be(1);
            reloads.Should().Be(2);
        }
        finally
        {
            publication.Release();
            readExpiry.Release();
            await Task.WhenAll(
                    ObserveControlledFailure(refresh),
                    reader ?? Task.CompletedTask,
                    retry ?? Task.CompletedTask
                )
                .WaitAsync(Watchdog);
        }

        publication.TimedOut.Should().BeFalse();
        readExpiry.TimedOut.Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OldResidentReadCannotTouchReplacementPolicyIdentity(bool clear)
    {
        await using var access = new BlockingTestHook(Watchdog);
        using var policy = new GatedAccessPolicy(access);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 2,
                MaxConcurrentLoads = 2,
                Policy = policy,
                SupportsBulkLoading = false,
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        policy.BlockNextAccess();
        Task<string> reader = Task.Factory.StartNew(
            static state =>
            {
                ((Cache<int, string>)state!).TryGet(1, out string? value).Should().BeTrue();
                return value!;
            },
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await access.Entered.WaitAsync(Watchdog);
            ReplaceSlot(cache, clear);
            cache.CleanUp();
            policy.ResidentCount.Should().Be(2);
        }
        finally
        {
            access.Release();
            await reader.WaitAsync(Watchdog);
        }

        access.TimedOut.Should().BeFalse();
        (await reader).Should().Be("old");
        policy.GetReadBufferStatistics().Enqueued.Should().Be(1);
        cache.CleanUp();
        AssertReplacementResidents(cache);
        policy.GetReadBufferStatistics().Queued.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BuiltInPolicyDropsQueuedOldAccessAfterSlotReplacement(bool clear)
    {
        var scheduler = new ManualScheduler();
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 2,
                MaxConcurrentLoads = 2,
                RecordStatistics = true,
                MaintenanceScheduler = scheduler,
                SupportsBulkLoading = false,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = 16,
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        cache.TryGet(1, out _).Should().BeTrue();
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(1);
        ReplaceSlot(cache, clear);
        scheduler.RunAll();
        cache.CleanUp();
        AssertReplacementResidents(cache);
        engine.GetPolicyReadBufferStatistics().Queued.Should().Be(0);
    }

    private static CacheEngine<int, TValue> CreateEngine<TValue>(
        LoadingCacheTestHooks hooks,
        bool fixedExpiration = false,
        bool accessExpiration = false
    )
        where TValue : notnull =>
        new(
            new CacheEngineOptions<int, TValue>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                RecordStatistics = true,
                TestHooks = hooks,
                SupportsBulkLoading = false,
                ExpireAfterWrite = fixedExpiration ? TimeSpan.FromMinutes(1) : null,
                ExpireAfterAccess = accessExpiration ? TimeSpan.FromMinutes(1) : null,
                TimeProvider =
                    fixedExpiration || accessExpiration
                        ? new FakeTimeProvider(DateTimeOffset.UnixEpoch)
                        : TimeProvider.System,
            }
        );

    private static LoadingCacheTestHooks FailureHooks(BlockingTestHook publication) =>
        new()
        {
            AfterRefreshPublished = () =>
            {
                publication.Invoke();
                throw new ControlledPublicationFailure();
            },
        };

    private static async Task ObserveControlledFailure<TValue>(Task<TValue> task) =>
        await FluentActions
            .Awaiting(() => task.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<ControlledPublicationFailure>();

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ReplaceSlot(Cache<int, string> cache, bool clear)
    {
        if (clear)
        {
            cache.Clear();
        }
        else
        {
            cache.Invalidate(1).Should().BeTrue();
        }

        cache.Put(1, "new");
        cache.Put(2, "other");
    }

    private static void AssertReplacementResidents(Cache<int, string> cache)
    {
        cache.EstimatedCount.Should().Be(2);
        cache.Policy.Eviction!.WeightedSize.Should().Be(2);
        cache
            .Policy.Eviction.Hottest(2)
            .Should()
            .BeEquivalentTo(new Dictionary<int, string> { [1] = "new", [2] = "other" });
    }

    private readonly record struct TaskObservation<TValue>(Task<TValue> Task);

    private readonly record struct LargeValue(
        [property: UsedImplicitly] long First,
        [property: UsedImplicitly] long Second,
        [property: UsedImplicitly] long Third,
        [property: UsedImplicitly] long Fourth
    );

    private sealed class Payload;

    private sealed class ControlledPublicationFailure : Exception;

    private sealed class BlockingReadExpiry(BlockingTestHook readExpiry) : IExpiry<int, string>
    {
        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromHours(1);

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            TimeSpan.FromHours(1);

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration)
        {
            if (value != "failed refresh")
            {
                return currentDuration;
            }

            readExpiry.Invoke();
            return TimeSpan.Zero;
        }
    }

    private sealed class GatedAccessPolicy(BlockingTestHook access)
        : ICacheEnginePolicy,
            IDisposable
    {
        private readonly WindowTinyLfuEnginePolicy _inner = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => throw new InvalidOperationException("This test must not evict a resident."),
            static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 16
        );
        private int _blockNext;

        internal void BlockNextAccess() => Volatile.Write(ref _blockNext, 1);

        public long Maximum => _inner.Maximum;
        public long WeightedSize => _inner.WeightedSize;
        public int ResidentCount => _inner.ResidentCount;

        public void SetMaximum(long maximum, bool weighted) => _inner.SetMaximum(maximum, weighted);

        public IReadOnlyList<object> Snapshot(bool hottest, int limit) =>
            _inner.Snapshot(hottest, limit);

        public void OnAccess(object? entryToken)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) != 0)
            {
                access.Invoke();
            }

            _inner.OnAccess(entryToken);
        }

        public void OnPublish(object? entryToken, long weight)
        {
            _inner.OnPublish(entryToken, weight);
            _inner.FlushWrites();
        }

        public void OnRemove(object? entryToken)
        {
            _inner.OnRemove(entryToken);
            _inner.FlushWrites();
        }

        public void Clear() => _inner.Clear();

        public bool CleanUp() => _inner.CleanUp();

        public ReadBufferStatistics GetReadBufferStatistics() => _inner.GetReadBufferStatistics();

        public void Dispose() => _inner.Dispose();
    }

    private sealed class ManualScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunAll()
        {
            while (_callbacks.Count != 0)
            {
                _callbacks.Dequeue()();
            }
        }
    }
}
