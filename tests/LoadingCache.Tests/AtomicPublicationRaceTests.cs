using JetBrains.Annotations;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class AtomicPublicationRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
            await Assert.That(cache.TryGetTask(1, out oldTask)).IsTrue();
        }

        var readerEntered = NewSignal();
        Task<TaskObservation<string>>? reader = null;
        Task writer = Task.Run(() => cache.Set(1, "new"));
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.TryGet(1, out string? oldValue)).IsTrue();
            await Assert.That(oldValue).IsEqualTo("old");
            reader = Task.Run(() =>
            {
                readerEntered.TrySetResult();
                if (!(cache.TryGetTask(1, out Task<string>? task)))
                    Assert.Fail(
                        "Expected cache.TryGetTask(1, out Task<string>? task) to be true ()."
                    );
                return new TaskObservation<string>(task!);
            });
            await readerEntered.Task.WaitAsync(Watchdog);
            await Assert.That(reader.IsCompleted).IsFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(writer, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
        }

        await Assert.That(publication.TimedOut).IsFalse();
        Task<string> observed = (await reader).Task;
        await Assert.That((await observed)).IsEqualTo("new");
        await Assert.That(cache.TryGetTask(1, out Task<string>? current)).IsTrue();
        Assert.NotNull(current);
        await Assert.That(ReferenceEquals(current, observed)).IsTrue();
        if (oldTask is not null)
        {
            await Assert.That(ReferenceEquals(observed, oldTask)).IsFalse();
            await Assert.That((await oldTask)).IsEqualTo("old");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ExplicitReferenceRefreshPublicationSurvivesLaterSet(bool fixedExpiration) =>
        VerifyRefreshPublication(new Payload(), new Payload(), new Payload(), fixedExpiration);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
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
            await Assert.That(refresh.IsCompleted).IsFalse();
            await Assert.That(cache.TryGet(1, out TValue? published)).IsTrue();
            await Assert.That(published).IsEqualTo(refreshedValue);
            await Assert.That(cache.TryGetTask(1, out Task<TValue>? publishedTask)).IsTrue();
            Assert.NotNull(publishedTask);
            await Assert.That((await publishedTask!)).IsEqualTo(refreshedValue);
            cache.Set(1, replacement);
            await Assert.That(cache.TryGetTask(1, out Task<TValue>? replacementTask)).IsTrue();
            Assert.NotNull(replacementTask);
            await Assert.That((await replacementTask!)).IsEqualTo(replacement);
            await Assert.That((await publishedTask)).IsEqualTo(refreshedValue);
            await Assert.That((await oldTask!)).IsEqualTo(oldValue);
        }
        finally
        {
            completion.Release();
            await refresh.WaitAsync(Watchdog);
        }

        await Assert.That(completion.TimedOut).IsFalse();
        await Assert.That((await refresh)).IsEqualTo(refreshedValue);
        await Assert.That(cache.TryGet(1, out TValue? final)).IsTrue();
        await Assert.That(final).IsEqualTo(replacement);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
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

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FixedExpirationLargeStructRefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            new LargeValue(9, 10, 11, 12),
            replaceBeforeFailure,
            fixedExpiration: true
        );

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task AccessExpirationReferenceRefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            new Payload(),
            new Payload(),
            new Payload(),
            replaceBeforeFailure,
            fixedExpiration: false,
            accessExpiration: true
        );

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task AccessExpirationInt64RefreshFailureFencesLaterSet(bool replaceBeforeFailure) =>
        VerifyRefreshRollback(
            0x12345678abcdef01L,
            0x23456789abcdef12L,
            0x3456789abcdef123L,
            replaceBeforeFailure,
            fixedExpiration: false,
            accessExpiration: true
        );

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
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
            await Assert.That(cache.TryGet(1, out TValue? published)).IsTrue();
            await Assert.That(published).IsEqualTo(refreshedValue);
            await Assert.That(cache.TryGetTask(1, out publishedTask)).IsTrue();
            Assert.NotNull(publishedTask);
            await Assert.That((await publishedTask!)).IsEqualTo(refreshedValue);
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

        await Assert.That(publication.TimedOut).IsFalse();
        await Assert.That(cache.TryGet(1, out TValue? restored)).IsTrue();
        await Assert.That(restored).IsEqualTo(replaceBeforeFailure ? replacement : oldValue);
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? current)).IsTrue();
        Assert.NotNull(current);
        await Assert.That((await current!)).IsEqualTo(restored);
        if (!replaceBeforeFailure)
        {
            await Assert.That(ReferenceEquals(current, oldTask)).IsTrue();
            cache.Set(1, replacement);
        }

        await Assert.That((await oldTask!)).IsEqualTo(oldValue);
        await Assert.That((await publishedTask)).IsEqualTo(refreshedValue);
        await Assert.That(cache.TryGet(1, out TValue? final)).IsTrue();
        await Assert.That(final).IsEqualTo(replacement);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
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
            Assert.NotNull(stale);
            await Assert.That(stale.Candidates).HasSingleItem();
            await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
            await Assert.That(value).IsEqualTo("refresh");
        }
        finally
        {
            publication.Release();
            await ObserveControlledFailure(refresh);
        }

        await Assert.That(publication.TimedOut).IsFalse();
        await Assert.That(cache.TryGet(1, out string? restored)).IsTrue();
        await Assert.That(restored).IsEqualTo("old");
        cache.Set(1, "new");
        await Assert.That(engine.TrimForMemoryPressure(stale)).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("new");
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
                            if ((current.Value) != ("refresh"))
                                Assert.Fail("Expected current.Value to equal (\"refresh\").");
                            pauseTransform();
                            return CacheMutation.Set("stale transform");
                        }

                        if ((current.Value) != ("new"))
                            Assert.Fail("Expected current.Value to equal (\"new\").");
                        return CacheMutation.Keep<string>();
                    }
                )
            );
            await transform.Entered.WaitAsync(Watchdog);
            publication.Release();
            await ObserveControlledFailure(refresh);
            cache.Set(1, "new");
            transform.Release();
            await Assert
                .That((await compute.WaitAsync(Watchdog)).Kind)
                .IsEqualTo(CacheMutationKind.Keep);
            await Assert.That(callbacks).IsEqualTo(2);
            await Assert.That(dictionary[1]).IsEqualTo("new");
        }
        finally
        {
            publication.Release();
            transform.Release();
            await Task.WhenAll(ObserveControlledFailure(refresh), compute ?? Task.CompletedTask)
                .WaitAsync(Watchdog);
        }

        await Assert.That(publication.TimedOut).IsFalse();
        await Assert.That(transform.TimedOut).IsFalse();
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
                    if (!(((AsyncLoadingCache<int, string>)state!).TryGet(1, out string? value)))
                        Assert.Fail(
                            "Expected ((AsyncLoadingCache<int, string>)state!) .TryGet(1, out string? value) to be true ()."
                        );
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
            await Assert.That((await retry.WaitAsync(Watchdog))).IsEqualTo("retry");
            await Assert.That(clock.GetTimestamp()).IsEqualTo(timestamp);
            readExpiry.Release();
            await Assert.That((await reader.WaitAsync(Watchdog))).IsEqualTo("failed refresh");
            await Assert
                .That(cache.Policy.VariableExpiration!.GetExpiresAfter(1))
                .IsEqualTo(TimeSpan.FromHours(1));
            await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
            await Assert.That(current).IsEqualTo("retry");
            cache.CleanUp();
            await Assert.That(cache.EstimatedCount).IsEqualTo(1);
            await Assert.That(reloads).IsEqualTo(2);
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

        await Assert.That(publication.TimedOut).IsFalse();
        await Assert.That(readExpiry.TimedOut).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
                if (!(((Cache<int, string>)state!).TryGet(1, out string? value)))
                    Assert.Fail(
                        "Expected ((Cache<int, string>)state!).TryGet(1, out string? value) to be true ()."
                    );
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
            await Assert.That(policy.ResidentCount).IsEqualTo(2);
        }
        finally
        {
            access.Release();
            await reader.WaitAsync(Watchdog);
        }

        await Assert.That(access.TimedOut).IsFalse();
        await Assert.That((await reader)).IsEqualTo("old");
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(1);
        cache.CleanUp();
        AssertReplacementResidents(cache);
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuiltInPolicyDropsQueuedOldAccessAfterSlotReplacement(bool clear)
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
        await Assert.That(cache.TryGet(1, out _)).IsTrue();
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(1);
        ReplaceSlot(cache, clear);
        scheduler.RunAll();
        cache.CleanUp();
        AssertReplacementResidents(cache);
        await Assert.That(engine.GetPolicyReadBufferStatistics().Queued).IsEqualTo(0);
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
        await Assert
            .That((Func<Task>)(() => task.WaitAsync(Watchdog)))
            .ThrowsExactly<ControlledPublicationFailure>();

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
            if (!(cache.Invalidate(1)))
                Assert.Fail("Expected cache.Invalidate(1) to be true ().");
        }

        cache.Put(1, "new");
        cache.Put(2, "other");
    }

    private static void AssertReplacementResidents(Cache<int, string> cache)
    {
        if ((cache.EstimatedCount) != (2))
            Assert.Fail("Expected cache.EstimatedCount to equal (2).");
        if ((cache.Policy.Eviction!.WeightedSize) != (2))
            Assert.Fail("Expected cache.Policy.Eviction!.WeightedSize to equal (2).");
        if (
            !cache
                .Policy.Eviction.Hottest(2)
                .OrderBy(pair => pair.Key)
                .SequenceEqual(new Dictionary<int, string> { [1] = "new", [2] = "other" })
        )
            Assert.Fail("Unexpected hottest entries after replacement.");
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
