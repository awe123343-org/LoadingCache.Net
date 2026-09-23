using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class BulkPublicationRevisionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LateBulkPublicationFailureCannotRemoveANewerRefresh(bool recordStatistics)
    {
        await using var timerArm = new BlockingTestHook(Watchdog);
        await using var refreshPublished = new BlockingTestHook(Watchdog);
        await VerifyLateBulkFailure(timerArm, refreshPublished, recordStatistics);
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task PartialBulkPublicationFailureCleansReadyAndPendingEntries(
        bool recordStatistics,
        int failedPublication
    )
    {
        var publication = new FailingReadyPublication(failedPublication);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 1,
                MaxPendingLoadKeys = 2,
                MaximumBulkKeys = 2,
                RecordStatistics = recordStatistics,
                TestHooks = new LoadingCacheTestHooks { BeforeReadyPublish = publication.Invoke },
            }
        );
        using var cache = new LoadingCache<int, string>(
            engine,
            static _ => throw new InvalidOperationException("Unexpected single load."),
            bulkLoader: static _ => new Dictionary<int, string> { [1] = "bulk-1", [2] = "bulk-2" }
        );
        // The failing entry has already cleared Flight but has not published
        // IsReady. An earlier key may be ready and a later key still pending.
        await Assert
            .That(() => cache.GetAll([1, 2]))
            .ThrowsExactly<ControlledReadyPublicationFailure>();
        await Assert.That(publication.Calls).IsEqualTo(failedPublication);
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(2, out _)).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        await Assert.That(cache.Statistics.InFlightLoads).IsEqualTo(0);
        engine.AssertInvariants();
        // Retrying the same keys proves neither a partially initialized entry
        // nor an unprocessed pending key survived the failed publication.
        await Assert
            .That(cache.GetAll([1, 2]))
            .IsEquivalentTo(new Dictionary<int, string> { [1] = "bulk-1", [2] = "bulk-2" });
        await Assert.That(cache.TryGet(1, out string? first)).IsTrue();
        await Assert.That(first).IsEqualTo("bulk-1");
        await Assert.That(cache.TryGet(2, out string? second)).IsTrue();
        await Assert.That(second).IsEqualTo("bulk-2");
        engine.AssertInvariants();
    }

    private static async Task VerifyLateBulkFailure(
        BlockingTestHook timerArm,
        BlockingTestHook refreshPublished,
        bool recordStatistics
    )
    {
        var failingArm = new FailingTimerArm(timerArm);
        var loader = new ControlledBulkLoader();
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                MaxPendingLoadKeys = 4,
                MaximumBulkKeys = 4,
                TimeProvider = new FakeTimeProvider(DateTimeOffset.UnixEpoch),
                ExpireAfterWrite = TimeSpan.FromHours(1),
                EnableExpirationScheduler = true,
                RecordStatistics = recordStatistics,
                TestHooks = new LoadingCacheTestHooks
                {
                    BeforeExpirationTimerArm = failingArm.Invoke,
                    AfterRefreshPublished = refreshPublished.Invoke,
                },
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            loader.LoadAsync,
            loader.ReloadAsync,
            loader.LoadAllAsync
        );
        Task<IReadOnlyDictionary<int, string>> bulk = cache.GetAllAsync([1, 2]).AsTask();
        Task<string>? refresh = null;
        Task<string>? publishedTask;
        try
        {
            await loader.Started.Task.WaitAsync(Watchdog);
            failingArm.Arm();
            loader.Result.TrySetResult(
                new Dictionary<int, string> { [1] = "bulk-1", [2] = "bulk-2" }
            );
            // Both requested values are ready, but bulk completion still owns
            // its promises and pauses outside the engine and entry locks.
            await timerArm.Entered.WaitAsync(Watchdog);
            await Assert.That(bulk.IsCompleted).IsFalse();
            await Assert.That(cache.TryGetTask(1, out Task<string>? originalTask)).IsTrue();
            Assert.NotNull(originalTask);
            await Assert.That((await originalTask!)).IsEqualTo("bulk-1");
            refresh = Task
                .Factory.StartNew(
                    static state =>
                        ((AsyncLoadingCache<int, string>)state!).RefreshAsync(1).AsTask(),
                    cache,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
                .Unwrap();
            await refreshPublished.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.TryGetTask(1, out publishedTask)).IsTrue();
            Assert.NotNull(publishedTask);
            await Assert.That(ReferenceEquals(publishedTask, originalTask)).IsFalse();
            await Assert.That((await publishedTask)).IsEqualTo("refreshed");
            await Assert.That(refresh.IsCompleted).IsFalse();
            // The old bulk outcome can remove its unchanged key 2, but its
            // epoch/generation is not authority over key 1's newer revision.
            timerArm.Release();
            await Assert
                .That((Func<Task>)(() => bulk.WaitAsync(Watchdog)))
                .ThrowsExactly<ControlledTimerArmFailure>();
            await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
            await Assert.That(current).IsEqualTo("refreshed");
            await Assert.That(cache.TryGetTask(1, out Task<string>? currentTask)).IsTrue();
            Assert.NotNull(currentTask);
            await Assert.That(ReferenceEquals(currentTask, publishedTask)).IsTrue();
            await Assert.That((await originalTask)).IsEqualTo("bulk-1");
            await Assert.That(cache.TryGet(2, out _)).IsFalse();
        }
        finally
        {
            loader.Result.TrySetResult(
                new Dictionary<int, string> { [1] = "bulk-1", [2] = "bulk-2" }
            );
            timerArm.Release();
            refreshPublished.Release();
            try
            {
                await ObserveControlledFailure(bulk);
            }
            finally
            {
                if (refresh is not null)
                {
                    await refresh.WaitAsync(Watchdog);
                }
            }
        }

        await Assert.That((await refresh)).IsEqualTo("refreshed");
        await Assert.That(cache.TryGetTask(1, out Task<string>? finalTask)).IsTrue();
        Assert.NotNull(finalTask);
        await Assert.That(ReferenceEquals(finalTask, publishedTask)).IsTrue();
        await Assert.That(loader.ReloadCalls).IsEqualTo(1);
        await Assert.That(timerArm.TimedOut).IsFalse();
        await Assert.That(refreshPublished.TimedOut).IsFalse();
        engine.AssertInvariants();
    }

    private static async Task ObserveControlledFailure(Task task)
    {
        try
        {
            await task.WaitAsync(Watchdog);
        }
        catch (ControlledTimerArmFailure)
        {
            // Observe the deliberate infrastructure failure during cleanup.
        }
    }

    private sealed class FailingTimerArm(BlockingTestHook gate)
    {
        private int _armed;

        internal void Arm() => Volatile.Write(ref _armed, 1);

        internal void Invoke()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            gate.Invoke();
            throw new ControlledTimerArmFailure();
        }
    }

    private sealed class FailingReadyPublication(int failedPublication)
    {
        internal int Calls { get; private set; }

        internal void Invoke()
        {
            if (++Calls == failedPublication)
            {
                throw new ControlledReadyPublicationFailure();
            }
        }
    }

    private sealed class ControlledBulkLoader : IBulkAsyncCacheLoader<int, string>
    {
        private int _reloadCalls;
        internal int ReloadCalls => Volatile.Read(ref _reloadCalls);
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<int, string>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> LoadAsync(int key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The requested bulk value became a single load.");

        public Task<string> ReloadAsync(
            int key,
            string oldValue,
            CancellationToken cancellationToken
        )
        {
            if ((key) != (1))
                Assert.Fail("Expected key to equal (1).");
            if ((oldValue) != ("bulk-1"))
                Assert.Fail("Expected oldValue to equal (\"bulk-1\").");
            Interlocked.Increment(ref _reloadCalls);
            return Task.FromResult("refreshed");
        }

        public Task<IReadOnlyDictionary<int, string>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            if (keys.Count != 2 || !keys.Order().SequenceEqual([1, 2]))
                Assert.Fail("Expected exactly keys 1 and 2.");
            Started.TrySetResult();
            return Result.Task;
        }
    }

    private sealed class ControlledTimerArmFailure : Exception;

    private sealed class ControlledReadyPublicationFailure : Exception;
}
