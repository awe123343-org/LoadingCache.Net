using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class BulkPublicationRevisionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task LateBulkPublicationFailureCannotRemoveANewerRefresh(bool recordStatistics)
    {
        await using var timerArm = new BlockingTestHook(Watchdog);
        await using var refreshPublished = new BlockingTestHook(Watchdog);
        await VerifyLateBulkFailure(timerArm, refreshPublished, recordStatistics);
    }

    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    public void PartialBulkPublicationFailureCleansReadyAndPendingEntries(
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
        cache
            .Invoking(static current => current.GetAll([1, 2]))
            .Should()
            .ThrowExactly<ControlledReadyPublicationFailure>();
        publication.Calls.Should().Be(failedPublication);
        cache.TryGet(1, out _).Should().BeFalse();
        cache.TryGet(2, out _).Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
        cache.Statistics.InFlightLoads.Should().Be(0);
        engine.AssertInvariants();

        // Retrying the same keys proves neither a partially initialized entry
        // nor an unprocessed pending key survived the failed publication.
        cache
            .GetAll([1, 2])
            .Should()
            .BeEquivalentTo(new Dictionary<int, string> { [1] = "bulk-1", [2] = "bulk-2" });
        cache.TryGet(1, out string? first).Should().BeTrue();
        first.Should().Be("bulk-1");
        cache.TryGet(2, out string? second).Should().BeTrue();
        second.Should().Be("bulk-2");
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
            bulk.IsCompleted.Should().BeFalse();
            cache.TryGetTask(1, out Task<string>? originalTask).Should().BeTrue();
            (await originalTask!).Should().Be("bulk-1");

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
            cache.TryGetTask(1, out publishedTask).Should().BeTrue();
            publishedTask.Should().NotBeSameAs(originalTask);
            (await publishedTask).Should().Be("refreshed");
            refresh.IsCompleted.Should().BeFalse();

            // The old bulk outcome can remove its unchanged key 2, but its
            // epoch/generation is not authority over key 1's newer revision.
            timerArm.Release();
            await FluentActions
                .Awaiting(() => bulk.WaitAsync(Watchdog))
                .Should()
                .ThrowExactlyAsync<ControlledTimerArmFailure>();
            cache.TryGet(1, out string? current).Should().BeTrue();
            current.Should().Be("refreshed");
            cache.TryGetTask(1, out Task<string>? currentTask).Should().BeTrue();
            currentTask.Should().BeSameAs(publishedTask);
            (await originalTask).Should().Be("bulk-1");
            cache.TryGet(2, out _).Should().BeFalse();
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

        (await refresh).Should().Be("refreshed");
        cache.TryGetTask(1, out Task<string>? finalTask).Should().BeTrue();
        finalTask.Should().BeSameAs(publishedTask);
        loader.ReloadCalls.Should().Be(1);
        timerArm.TimedOut.Should().BeFalse();
        refreshPublished.TimedOut.Should().BeFalse();
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
            key.Should().Be(1);
            oldValue.Should().Be("bulk-1");
            Interlocked.Increment(ref _reloadCalls);
            return Task.FromResult("refreshed");
        }

        public Task<IReadOnlyDictionary<int, string>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            keys.Should().BeEquivalentTo([1, 2]);
            Started.TrySetResult();
            return Result.Task;
        }
    }

    private sealed class ControlledTimerArmFailure : Exception;

    private sealed class ControlledReadyPublicationFailure : Exception;
}
