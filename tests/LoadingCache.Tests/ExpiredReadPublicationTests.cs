using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class ExpiredReadPublicationTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [MatrixDataSource]
    public async Task DelayedExpiredReadCannotRemoveACompletedRefresh(
        [Matrix("write", "access", "both")] string expiration,
        [Matrix(false, true)] bool recordStatistics,
        [Matrix(false, true)] bool materializeTask
    )
    {
        await using var cleanup = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan duration = TimeSpan.FromSeconds(10);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = expiration is "write" or "both" ? duration : null,
                ExpireAfterAccess = expiration is "access" or "both" ? duration : null,
                RecordStatistics = recordStatistics,
                TestHooks = new LoadingCacheTestHooks { BeforeExpiredReadCleanup = cleanup.Invoke },
            }
        );
        int reloads = 0;
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) =>
                throw new InvalidOperationException("The resident refresh became a cold load."),
            (_, oldValue, _) =>
            {
                if ((oldValue) != ("old"))
                    Assert.Fail("Expected oldValue to equal (\"old\").");
                Interlocked.Increment(ref reloads);
                return Task.FromResult("refreshed");
            }
        );
        cache.Set(1, "old");
        cache.CleanUp();
        await Assert.That(cache.TryGetTask(1, out Task<string>? oldTask)).IsTrue();
        Assert.NotNull(oldTask);
        clock.Advance(duration);
        Task<(bool Found, string? Value)> staleRead = Task
            .Factory.StartNew(
                static async state =>
                {
                    (AsyncLoadingCache<int, string> readerCache, bool readTask) = ((
                        AsyncLoadingCache<int, string>,
                        bool
                    ))
                        state!;
                    if (readTask)
                    {
                        bool found = readerCache.TryGetTask(1, out Task<string>? task);
                        return (found, found ? await task!.WaitAsync(Watchdog) : null);
                    }

                    bool valueFound = readerCache.TryGet(1, out string? value);
                    return (valueFound, value);
                },
                (cache, materializeTask),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        Task<string>? publishedTask;
        try
        {
            // The reader has observed hard expiry and released entry.Sync,
            // but has not acquired the engine gate for physical cleanup.
            await cleanup.Entered.WaitAsync(Watchdog);
            await Assert.That(staleRead.IsCompleted).IsFalse();
            // Explicit refresh of a physically resident value keeps its Entry.
            // The throwing cold loader ensures this exercises that path.
            await Assert
                .That((await cache.RefreshAsync(1).AsTask().WaitAsync(Watchdog)))
                .IsEqualTo("refreshed");
            await Assert.That(reloads).IsEqualTo(1);
            await Assert.That(cache.TryGetTask(1, out publishedTask)).IsTrue();
            Assert.NotNull(publishedTask);
            await Assert.That((await publishedTask!.WaitAsync(Watchdog))).IsEqualTo("refreshed");
            await Assert.That((await oldTask!.WaitAsync(Watchdog))).IsEqualTo("old");
        }
        finally
        {
            cleanup.Release();
            await staleRead.WaitAsync(Watchdog);
        }

        await Assert.That(cleanup.TimedOut).IsFalse();
        (bool staleFound, string? observedValue) = await staleRead;
        if (staleFound)
        {
            await Assert.That(observedValue).IsEqualTo("refreshed");
        }

        // The overlapping reader may report its earlier miss or retry the new
        // value. Neither choice authorizes removing the completed refresh.
        await Assert.That(cache.Policy.TryGetQuietly(1, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("refreshed");
        await Assert.That(cache.TryGetTask(1, out Task<string>? currentTask)).IsTrue();
        Assert.NotNull(currentTask);
        await Assert.That(ReferenceEquals(currentTask, publishedTask)).IsTrue();
        await Assert.That((await currentTask.WaitAsync(Watchdog))).IsEqualTo("refreshed");
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.Statistics.ExpiredRemovals).IsEqualTo(0);
        await Assert.That(reloads).IsEqualTo(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task DelayedExpiredReadHonorsAnExtendedDuration(
        bool extendWriteDuration,
        bool enableOtherExpiration
    )
    {
        await using var cleanup = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan originalDuration = TimeSpan.FromSeconds(10);
        TimeSpan extendedDuration = TimeSpan.FromSeconds(20);
        TimeSpan otherDuration = TimeSpan.FromSeconds(30);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite =
                    extendWriteDuration ? originalDuration
                    : enableOtherExpiration ? otherDuration
                    : null,
                ExpireAfterAccess =
                    !extendWriteDuration ? originalDuration
                    : enableOtherExpiration ? otherDuration
                    : null,
                RecordStatistics = true,
                TestHooks = new LoadingCacheTestHooks { BeforeExpiredReadCleanup = cleanup.Invoke },
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "resident");
        cache.CleanUp();
        clock.Advance(originalDuration);
        Task staleRead = Task.Factory.StartNew(
            static state => ((Cache<int, string>)state!).TryGet(1, out _),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        IFixedExpirationPolicy<int, string> policy = extendWriteDuration
            ? cache.Policy.ExpireAfterWrite!
            : cache.Policy.ExpireAfterAccess!;
        try
        {
            await cleanup.Entered.WaitAsync(Watchdog);
            await Assert.That(staleRead.IsCompleted).IsFalse();
            // Only the policy changes. The Entry, value publication, and original
            // timestamps remain the same, so a revision check alone is insufficient.
            policy.SetDuration(extendedDuration);
            await Assert.That(policy.AgeOf(1)).IsEqualTo(originalDuration);
            await Assert
                .That(policy.GetExpiresAfter(1))
                .IsEqualTo(extendedDuration - originalDuration);
            await Assert.That(cache.Policy.TryGetQuietly(1, out string? renewed)).IsTrue();
            await Assert.That(renewed).IsEqualTo("resident");
        }
        finally
        {
            cleanup.Release();
            await staleRead.WaitAsync(Watchdog);
        }

        await Assert.That(cleanup.TimedOut).IsFalse();
        await Assert.That(cache.Policy.TryGetQuietly(1, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("resident");
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.Statistics.ExpiredRemovals).IsEqualTo(0);
        engine.AssertInvariants();
        // Quiet observations must not touch TTI, and the extension must not turn
        // expiration off. The unchanged publication expires at its new deadline.
        clock.Advance(extendedDuration - originalDuration);
        await Assert.That(cache.Policy.TryGetQuietly(1, out _)).IsFalse();
        await Assert.That(cache.TryGet(1, out _)).IsFalse();
        await Assert.That(cache.Statistics.ExpiredRemovals).IsEqualTo(1);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        engine.AssertInvariants();
    }
}
