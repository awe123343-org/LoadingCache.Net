using FluentAssertions;
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
                current.Should().Be("old");
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
                        readerCache.TryGet(1, out string? value).Should().BeTrue();
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
            reader.IsCompleted.Should().BeFalse();
            if (replaceWithSet)
            {
                // Fixed-write Set replaces the physical Entry and revokes the
                // old refresh's publication, but still serves its original waiter.
                cache.Set(1, expectedValue);
            }

            reloadResult.TrySetResult("refreshed");
            (await refresh.WaitAsync(Watchdog)).Should().Be("refreshed");
            cache.Policy.ExpireAfterWrite!.SetDuration(TimeSpan.FromSeconds(20));
            timestamp.Returned.IsCompleted.Should().BeFalse();
            // The new duration must not make the captured, expired old value
            // eligible again after a different publication has replaced it.
            timestamp.Release();
            (await reader.WaitAsync(Watchdog)).Should().Be(expectedValue);
        }
        finally
        {
            reloadResult.TrySetResult("refreshed");
            timestamp.Release();
            await Task.WhenAll(refresh, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
        }

        timestamp.TimedOut.Should().BeFalse();
        cache.TryGet(1, out string? currentValue).Should().BeTrue();
        currentValue.Should().Be(expectedValue);
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
            cache.Invalidate(1).Should().BeTrue();
            firstLoad = cache.GetAsync(1).AsTask();
            await loadEntered.Task.WaitAsync(Watchdog);
            cache.TryGetTask(1, out Task<string>? pendingTask).Should().BeTrue();
            pendingTask!.IsCompleted.Should().BeFalse();
            timestamp.Returned.IsCompleted.Should().BeFalse();
            // Retrying the lookup must leave this newer Loading entry intact.
            timestamp.Release();
            (await reader.WaitAsync(Watchdog)).Should().BeFalse();
            cache.TryGetTask(1, out Task<string>? currentTask).Should().BeTrue();
            currentTask.Should().BeSameAs(pendingTask);
            secondLoad = cache.GetAsync(1).AsTask();
            secondLoad.IsCompleted.Should().BeFalse();
            Volatile.Read(ref loadCount[0]).Should().Be(1);
            loadResult.TrySetResult("loaded");
            (await firstLoad.WaitAsync(Watchdog)).Should().Be("loaded");
            (await secondLoad.WaitAsync(Watchdog)).Should().Be("loaded");
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

        timestamp.TimedOut.Should().BeFalse();
        Volatile.Read(ref loadCount[0]).Should().Be(1);
        cache.TryGet(1, out string? currentValue).Should().BeTrue();
        currentValue.Should().Be("loaded");
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
        cache.TryGetTask(1, out Task<TValue>? oldTask).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        Task<TValue> refresh = StartRefresh(cache);
        Task<TValue>[] readers = [];
        try
        {
            // Mutable value, timestamps and Task are already new, but the
            // composite snapshot has not been published and entry.Sync is held.
            await publication.Entered.WaitAsync(Watchdog);
            refresh.IsCompleted.Should().BeFalse();
            readers = StartReaders(cache);
            TValue[] observations = await Task.WhenAll(readers).WaitAsync(ReaderWatchdog);
            observations.Should().HaveCount(2);
            AssertValue(observations[0], oldValue);
            AssertValue(observations[1], oldValue);
            publication.Returned.IsCompleted.Should().BeFalse();
            refresh.IsCompleted.Should().BeFalse();
        }
        finally
        {
            publication.Release();
            await Task.WhenAll(readers.Append<Task>(refresh)).WaitAsync(Watchdog);
        }

        publication.TimedOut.Should().BeFalse();
        AssertValue(await refresh, refreshedValue);
        cache.TryGet(1, out TValue? currentValue).Should().BeTrue();
        AssertValue(currentValue, refreshedValue);
        cache.TryGetTask(1, out Task<TValue>? currentTask).Should().BeTrue();
        currentTask.Should().NotBeSameAs(oldTask);
        AssertValue(await currentTask, refreshedValue);
        AssertValue(await oldTask!, oldValue);
        // A complete new publication expires at t=15, not the old t=10 deadline.
        clock.Advance(TimeSpan.FromSeconds(5));
        cache.TryGet(1, out currentValue).Should().BeTrue();
        AssertValue(currentValue, refreshedValue);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        cache.TryGet(1, out currentValue).Should().BeTrue();
        AssertValue(currentValue, refreshedValue);
        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGetTask(1, out _).Should().BeFalse();
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
        cache.TryGetTask(1, out Task<TValue>? oldTask).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(5));
        Task<TValue> refresh = StartRefresh(cache);
        Task<TValue>[] readers = [];
        Task<TValue>? publishedTask;
        try
        {
            // Preserve a caller-owned Task from the successful new publication
            // before the controlled finalization failure begins its rollback.
            await published.Entered.WaitAsync(Watchdog);
            cache.TryGetTask(1, out publishedTask).Should().BeTrue();
            publishedTask.Should().NotBeSameAs(oldTask);
            AssertValue(await publishedTask, refreshedValue);
            published.Release();
            await restoration.Entered.WaitAsync(Watchdog);
            // SetValue(old) has run while entry.Sync is held; the still-published
            // new snapshot must remain the complete reader-visible version.
            refresh.IsCompleted.Should().BeFalse();
            readers = StartReaders(cache);
            TValue[] observations = await Task.WhenAll(readers).WaitAsync(ReaderWatchdog);
            observations.Should().HaveCount(2);
            AssertValue(observations[0], refreshedValue);
            AssertValue(observations[1], refreshedValue);
            restoration.Returned.IsCompleted.Should().BeFalse();
            refresh.IsCompleted.Should().BeFalse();
        }
        finally
        {
            published.Release();
            restoration.Release();
            await Task.WhenAll(readers.Append(ObserveControlledFailure(refresh)))
                .WaitAsync(Watchdog);
        }

        published.TimedOut.Should().BeFalse();
        restoration.TimedOut.Should().BeFalse();
        cache.TryGet(1, out TValue? restoredValue).Should().BeTrue();
        AssertValue(restoredValue, oldValue);
        cache.TryGetTask(1, out Task<TValue>? restoredTask).Should().BeTrue();
        restoredTask.Should().BeSameAs(oldTask);
        AssertValue(await restoredTask, oldValue);
        AssertValue(await oldTask!, oldValue);
        AssertValue(await publishedTask, refreshedValue);
        // Rollback restores the old t=0 write time, so t=10 is hard-expired.
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        cache.TryGet(1, out restoredValue).Should().BeTrue();
        AssertValue(restoredValue, oldValue);
        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGetTask(1, out _).Should().BeFalse();
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
                    readerCache.TryGet(1, out TValue? value).Should().BeTrue();
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
                        read.IsCompletedSuccessfully.Should().BeTrue();
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
        await FluentActions
            .Awaiting(() => refresh.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<ControlledPublicationFailure>();

    private static void AssertValue<TValue>(TValue? actual, TValue expected)
        where TValue : notnull
    {
        if (actual is LargeValue actualLarge && expected is LargeValue expectedLarge)
        {
            actualLarge.First.Should().Be(expectedLarge.First);
            actualLarge.Second.Should().Be(expectedLarge.Second);
            actualLarge.Third.Should().Be(expectedLarge.Third);
            actualLarge.Fourth.Should().Be(expectedLarge.Fourth);
        }
        else
        {
            actual.Should().Be(expected);
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
