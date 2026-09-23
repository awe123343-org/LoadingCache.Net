using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class FixedWritePublicationTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReaderWatchdog = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(10);

    // These tests specify nonblocking fixed-write snapshots. The previous locked
    // reader can preserve correctness while failing this new progress requirement.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ReferenceReadsKeepOldSnapshotWhileRefreshPublicationIsPaused(
        bool recordStatistics
    ) => VerifyPausedPublication("old", "refreshed", recordStatistics);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task LargeStructReadsKeepOldSnapshotWhileRefreshPublicationIsPaused(
        bool recordStatistics
    ) =>
        VerifyPausedPublication(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            recordStatistics
        );

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ReferenceReadsKeepNewSnapshotWhileRefreshRollbackIsPaused(bool recordStatistics) =>
        VerifyPausedRollback("old", "refreshed", recordStatistics);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task LargeStructReadsKeepNewSnapshotWhileRefreshRollbackIsPaused(
        bool recordStatistics
    ) =>
        VerifyPausedRollback(
            new LargeValue(1, 2, 3, 4),
            new LargeValue(5, 6, 7, 8),
            recordStatistics
        );

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DurationExtensionCannotReviveAnExpiredCapturedPublication(
        bool recordStatistics,
        bool replaceWithSet
    )
    {
        await using var timestamp = new BlockingTestHook(Watchdog);
        var clock = new ReaderGatedTimeProvider(timestamp);
        var reloadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reloadResult = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = TimeToLive,
                RecordStatistics = recordStatistics,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) =>
                throw new InvalidOperationException("The pending resident refresh became a load."),
            (_, current, _) =>
            {
                if ((current) != ("old"))
                    Assert.Fail("Expected current to equal (\"old\").");
                reloadEntered.TrySetResult();
                return reloadResult.Task;
            }
        );
        cache.Set(1, "old");
        cache.CleanUp();
        clock.Advance(TimeSpan.FromSeconds(9));
        string expectedValue = replaceWithSet ? "replacement" : "refreshed";
        Task<string> refresh = StartRefresh(cache);
        Task<string>? reader = null;
        try
        {
            await reloadEntered.Task.WaitAsync(Watchdog);
            clock.Advance(TimeSpan.FromSeconds(2));
            reader = Task.Factory.StartNew(
                static state =>
                {
                    var (readerCache, readerClock) = ((
                        AsyncLoadingCache<int, string>,
                        ReaderGatedTimeProvider
                    ))
                        state!;
                    return readerClock.ReadAtNextTimestamp(() =>
                    {
                        if (!(readerCache.TryGet(1, out string? value)))
                            Assert.Fail(
                                "Expected readerCache.TryGet(1, out string? value) to be true ()."
                            );
                        return value!;
                    });
                },
                (cache, clock),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            // At t=11 the reader has captured old@t=0, already past its t=10
            // deadline, and pauses before returning the clock observation.
            await timestamp.Entered.WaitAsync(Watchdog);
            await Assert.That(reader.IsCompleted).IsFalse();
            if (replaceWithSet)
            {
                // Fixed-write Set replaces the physical Entry and revokes the
                // old refresh's publication, but still serves its original waiter.
                cache.Set(1, expectedValue);
            }

            reloadResult.TrySetResult("refreshed");
            await Assert.That((await refresh.WaitAsync(Watchdog))).IsEqualTo("refreshed");
            cache.Policy.ExpireAfterWrite!.SetDuration(TimeSpan.FromSeconds(20));
            await Assert.That(timestamp.Returned.IsCompleted).IsFalse();
            // The new duration must not make the captured, expired old value
            // eligible again after a different publication has replaced it.
            timestamp.Release();
            await Assert.That((await reader.WaitAsync(Watchdog))).IsEqualTo(expectedValue);
        }
        finally
        {
            reloadResult.TrySetResult("refreshed");
            timestamp.Release();
            await Task.WhenAll(refresh, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
        }

        await Assert.That(timestamp.TimedOut).IsFalse();
        await Assert.That(cache.TryGet(1, out string? currentValue)).IsTrue();
        await Assert.That(currentValue).IsEqualTo(expectedValue);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredCapturedPublicationCannotRemoveANewerPendingLoad(bool recordStatistics)
    {
        await using var timestamp = new BlockingTestHook(Watchdog);
        var clock = new ReaderGatedTimeProvider(timestamp);
        var loadEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var loadResult = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int[] loadCount = [0];
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = TimeToLive,
                RecordStatistics = recordStatistics,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (_, _) =>
            {
                Interlocked.Increment(ref loadCount[0]);
                loadEntered.TrySetResult();
                return loadResult.Task;
            }
        );
        cache.Set(1, "old");
        cache.CleanUp();
        clock.Advance(TimeSpan.FromSeconds(11));
        Task<bool> reader = Task.Factory.StartNew(
            static state =>
            {
                var (readerCache, readerClock) = ((
                    AsyncLoadingCache<int, string>,
                    ReaderGatedTimeProvider
                ))
                    state!;
                return readerClock.ReadAtNextTimestamp(() => readerCache.TryGet(1, out _));
            },
            (cache, clock),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
        Task<string>? firstLoad = null;
        Task<string>? secondLoad = null;
        try
        {
            // The reader holds old@t=0 past its deadline, before observing t=11.
            await timestamp.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.Invalidate(1)).IsTrue();
            firstLoad = cache.GetAsync(1).AsTask();
            await loadEntered.Task.WaitAsync(Watchdog);
            await Assert.That(cache.TryGetTask(1, out Task<string>? pendingTask)).IsTrue();
            Assert.NotNull(pendingTask);
            await Assert.That(pendingTask!.IsCompleted).IsFalse();
            await Assert.That(timestamp.Returned.IsCompleted).IsFalse();
            // Retrying the lookup must leave this newer Loading entry intact.
            timestamp.Release();
            await Assert.That((await reader.WaitAsync(Watchdog))).IsFalse();
            await Assert.That(cache.TryGetTask(1, out Task<string>? currentTask)).IsTrue();
            Assert.NotNull(currentTask);
            await Assert.That(ReferenceEquals(currentTask, pendingTask)).IsTrue();
            secondLoad = cache.GetAsync(1).AsTask();
            await Assert.That(secondLoad.IsCompleted).IsFalse();
            await Assert.That(Volatile.Read(ref loadCount[0])).IsEqualTo(1);
            loadResult.TrySetResult("loaded");
            await Assert.That((await firstLoad.WaitAsync(Watchdog))).IsEqualTo("loaded");
            await Assert.That((await secondLoad.WaitAsync(Watchdog))).IsEqualTo("loaded");
        }
        finally
        {
            timestamp.Release();
            loadResult.TrySetResult("loaded");
            await Task.WhenAll(
                    reader,
                    firstLoad ?? Task.CompletedTask,
                    secondLoad ?? Task.CompletedTask
                )
                .WaitAsync(Watchdog);
        }

        await Assert.That(timestamp.TimedOut).IsFalse();
        await Assert.That(Volatile.Read(ref loadCount[0])).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out string? currentValue)).IsTrue();
        await Assert.That(currentValue).IsEqualTo("loaded");
        engine.AssertInvariants();
    }

    private static async Task VerifyPausedPublication<TValue>(
        TValue oldValue,
        TValue refreshedValue,
        bool recordStatistics
    )
        where TValue : notnull
    {
        await using var publication = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine<TValue>(
            clock,
            recordStatistics,
            new LoadingCacheTestHooks { BeforeRefreshSnapshotPublished = publication.Invoke }
        );
        await using var cache = CreateCache(engine, oldValue, refreshedValue);
        cache.Set(1, oldValue);
        cache.CleanUp();
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
        clock.Advance(TimeSpan.FromSeconds(5));
        Task<TValue> refresh = StartRefresh(cache);
        Task<TValue>[] readers = [];
        try
        {
            // Mutable value, timestamps and Task are already new, but the
            // composite snapshot has not been published and entry.Sync is held.
            await publication.Entered.WaitAsync(Watchdog);
            await Assert.That(refresh.IsCompleted).IsFalse();
            readers = StartReaders(cache);
            TValue[] observations = await Task.WhenAll(readers).WaitAsync(ReaderWatchdog);
            await Assert.That(observations.Count).IsEqualTo(2);
            AssertValue(observations[0], oldValue);
            AssertValue(observations[1], oldValue);
            await Assert.That(publication.Returned.IsCompleted).IsFalse();
            await Assert.That(refresh.IsCompleted).IsFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(readers.Append<Task>(refresh)).WaitAsync(Watchdog);
        }

        await Assert.That(publication.TimedOut).IsFalse();
        AssertValue(await refresh, refreshedValue);
        await Assert.That(cache.TryGet(1, out TValue? currentValue)).IsTrue();
        AssertValue(currentValue, refreshedValue);
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? currentTask)).IsTrue();
        Assert.NotNull(currentTask);
        await Assert.That(ReferenceEquals(currentTask, oldTask)).IsFalse();
        AssertValue(await currentTask, refreshedValue);
        AssertValue(await oldTask!, oldValue);
        // A complete new publication expires at t=15, not the old t=10 deadline.
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(cache.TryGet(1, out currentValue)).IsTrue();
        AssertValue(currentValue, refreshedValue);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out currentValue)).IsTrue();
        AssertValue(currentValue, refreshedValue);
        clock.Advance(TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGetTask(1, out _)).IsFalse();
        cache.CleanUp();
        engine.AssertInvariants();
    }

    private static async Task VerifyPausedRollback<TValue>(
        TValue oldValue,
        TValue refreshedValue,
        bool recordStatistics
    )
        where TValue : notnull
    {
        await using var published = new FailingPublicationHook(Watchdog);
        await using var restoration = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine<TValue>(
            clock,
            recordStatistics,
            new LoadingCacheTestHooks
            {
                AfterRefreshPublished = published.InvokeThenThrow,
                BeforeRefreshSnapshotRestored = restoration.Invoke,
            }
        );
        await using var cache = CreateCache(engine, oldValue, refreshedValue);
        cache.Set(1, oldValue);
        cache.CleanUp();
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
        clock.Advance(TimeSpan.FromSeconds(5));
        Task<TValue> refresh = StartRefresh(cache);
        Task<TValue>[] readers = [];
        Task<TValue>? publishedTask;
        try
        {
            // Preserve a caller-owned Task from the successful new publication
            // before the controlled finalization failure begins its rollback.
            await published.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.TryGetTask(1, out publishedTask)).IsTrue();
            Assert.NotNull(publishedTask);
            await Assert.That(ReferenceEquals(publishedTask, oldTask)).IsFalse();
            AssertValue(await publishedTask, refreshedValue);
            published.Release();
            await restoration.Entered.WaitAsync(Watchdog);
            // SetValue(old) has run while entry.Sync is held; the still-published
            // new snapshot must remain the complete reader-visible version.
            await Assert.That(refresh.IsCompleted).IsFalse();
            readers = StartReaders(cache);
            TValue[] observations = await Task.WhenAll(readers).WaitAsync(ReaderWatchdog);
            await Assert.That(observations.Count).IsEqualTo(2);
            AssertValue(observations[0], refreshedValue);
            AssertValue(observations[1], refreshedValue);
            await Assert.That(restoration.Returned.IsCompleted).IsFalse();
            await Assert.That(refresh.IsCompleted).IsFalse();
        }
        finally
        {
            published.Release();
            restoration.Release();
            await Task.WhenAll(readers.Append(ObserveControlledFailure(refresh)))
                .WaitAsync(Watchdog);
        }

        await Assert.That(published.TimedOut).IsFalse();
        await Assert.That(restoration.TimedOut).IsFalse();
        await Assert.That(cache.TryGet(1, out TValue? restoredValue)).IsTrue();
        AssertValue(restoredValue, oldValue);
        await Assert.That(cache.TryGetTask(1, out Task<TValue>? restoredTask)).IsTrue();
        Assert.NotNull(restoredTask);
        await Assert.That(ReferenceEquals(restoredTask, oldTask)).IsTrue();
        AssertValue(await restoredTask, oldValue);
        AssertValue(await oldTask!, oldValue);
        AssertValue(await publishedTask, refreshedValue);
        // Rollback restores the old t=0 write time, so t=10 is hard-expired.
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out restoredValue)).IsTrue();
        AssertValue(restoredValue, oldValue);
        clock.Advance(TimeSpan.FromTicks(1));
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGetTask(1, out _)).IsFalse();
        cache.CleanUp();
        engine.AssertInvariants();
    }

    private static CacheEngine<int, TValue> CreateEngine<TValue>(
        FakeTimeProvider clock,
        bool recordStatistics,
        LoadingCacheTestHooks hooks
    )
        where TValue : notnull =>
        new(
            new CacheEngineOptions<int, TValue>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = TimeToLive,
                RecordStatistics = recordStatistics,
                TestHooks = hooks,
            }
        );

    private static AsyncLoadingCache<int, TValue> CreateCache<TValue>(
        CacheEngine<int, TValue> engine,
        TValue oldValue,
        TValue refreshedValue
    )
        where TValue : notnull =>
        new(
            engine,
            static (_, _) =>
                throw new InvalidOperationException(
                    "A resident read unexpectedly invoked the loader."
                ),
            (_, current, _) =>
            {
                AssertValue(current, oldValue);
                return Task.FromResult(refreshedValue);
            }
        );

    private static Task<TValue> StartRefresh<TValue>(AsyncLoadingCache<int, TValue> cache)
        where TValue : notnull =>
        Task
            .Factory.StartNew(
                static state => ((AsyncLoadingCache<int, TValue>)state!).RefreshAsync(1).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();

    private static Task<TValue>[] StartReaders<TValue>(AsyncLoadingCache<int, TValue> cache)
        where TValue : notnull =>
        [
            Task.Factory.StartNew(
                static state =>
                {
                    var readerCache = (AsyncLoadingCache<int, TValue>)state!;
                    if (!(readerCache.TryGet(1, out TValue? value)))
                        Assert.Fail(
                            "Expected readerCache.TryGet(1, out TValue? value) to be true ()."
                        );
                    return value!;
                },
                cache,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ),
            Task
                .Factory.StartNew(
                    static state =>
                    {
                        var readerCache = (AsyncLoadingCache<int, TValue>)state!;
                        ValueTask<TValue> read = readerCache.GetAsync(1);
                        if (!(read.IsCompletedSuccessfully))
                            Assert.Fail("Expected read.IsCompletedSuccessfully to be true ().");
                        return read.AsTask();
                    },
                    cache,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
                .Unwrap(),
        ];

    private static async Task ObserveControlledFailure<TValue>(Task<TValue> refresh) =>
        await Assert
            .That((Func<Task>)(() => refresh.WaitAsync(Watchdog)))
            .ThrowsExactly<ControlledPublicationFailure>();

    private static void AssertValue<TValue>(TValue? actual, TValue expected)
        where TValue : notnull
    {
        if (actual is LargeValue actualLarge && expected is LargeValue expectedLarge)
        {
            if ((actualLarge.First) != (expectedLarge.First))
                Assert.Fail("Expected actualLarge.First to equal (expectedLarge.First).");
            if ((actualLarge.Second) != (expectedLarge.Second))
                Assert.Fail("Expected actualLarge.Second to equal (expectedLarge.Second).");
            if ((actualLarge.Third) != (expectedLarge.Third))
                Assert.Fail("Expected actualLarge.Third to equal (expectedLarge.Third).");
            if ((actualLarge.Fourth) != (expectedLarge.Fourth))
                Assert.Fail("Expected actualLarge.Fourth to equal (expectedLarge.Fourth).");
        }
        else
        {
            if (!EqualityComparer<TValue>.Default.Equals(actual, expected))
                Assert.Fail("Expected actual to equal (expected).");
        }
    }

    private sealed class FailingPublicationHook(TimeSpan timeout) : IAsyncDisposable
    {
        private readonly BlockingTestHook _publication = new(timeout);
        internal Task Entered => _publication.Entered;
        internal bool TimedOut => _publication.TimedOut;

        internal void InvokeThenThrow()
        {
            _publication.Invoke();
            throw new ControlledPublicationFailure();
        }

        internal void Release() => _publication.Release();

        public ValueTask DisposeAsync() => _publication.DisposeAsync();
    }

    private sealed class ReaderGatedTimeProvider(BlockingTestHook timestamp) : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
        private readonly AsyncLocal<bool> _pauseNextTimestamp = new();
        public override long TimestampFrequency => _clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public override long GetTimestamp()
        {
            if (!_pauseNextTimestamp.Value)
            {
                return _clock.GetTimestamp();
            }

            _pauseNextTimestamp.Value = false;
            timestamp.Invoke();
            return _clock.GetTimestamp();
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        ) => _clock.CreateTimer(callback, state, dueTime, period);

        internal void Advance(TimeSpan duration) => _clock.Advance(duration);

        internal TResult ReadAtNextTimestamp<TResult>(Func<TResult> read)
        {
            bool previous = _pauseNextTimestamp.Value;
            _pauseNextTimestamp.Value = true;
            try
            {
                return read();
            }
            finally
            {
                _pauseNextTimestamp.Value = previous;
            }
        }
    }

    private sealed class ControlledPublicationFailure : Exception;

    private readonly record struct LargeValue(long First, long Second, long Third, long Fourth);
}
