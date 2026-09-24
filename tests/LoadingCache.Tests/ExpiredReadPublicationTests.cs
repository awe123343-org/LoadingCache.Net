using FluentAssertions;
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
                oldValue.Should().Be("old");
                Interlocked.Increment(ref reloads);
                return Task.FromResult("refreshed");
            }
        );
        cache.Set(1, "old");
        cache.CleanUp();
        cache.TryGetTask(1, out Task<string>? oldTask).Should().BeTrue();
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
            staleRead.IsCompleted.Should().BeFalse();
            // Explicit refresh of a physically resident value keeps its Entry.
            // The throwing cold loader ensures this exercises that path.
            (await cache.RefreshAsync(1).AsTask().WaitAsync(Watchdog))
                .Should()
                .Be("refreshed");
            reloads.Should().Be(1);
            cache.TryGetTask(1, out publishedTask).Should().BeTrue();
            (await publishedTask!.WaitAsync(Watchdog)).Should().Be("refreshed");
            (await oldTask!.WaitAsync(Watchdog)).Should().Be("old");
        }
        finally
        {
            cleanup.Release();
            await staleRead.WaitAsync(Watchdog);
        }

        cleanup.TimedOut.Should().BeFalse();
        (bool staleFound, string? observedValue) = await staleRead;
        if (staleFound)
        {
            observedValue.Should().Be("refreshed");
        }

        // The overlapping reader may report its earlier miss or retry the new
        // value. Neither choice authorizes removing the completed refresh.
        cache.Policy.TryGetQuietly(1, out string? current).Should().BeTrue();
        current.Should().Be("refreshed");
        cache.TryGetTask(1, out Task<string>? currentTask).Should().BeTrue();
        currentTask.Should().BeSameAs(publishedTask);
        (await currentTask.WaitAsync(Watchdog)).Should().Be("refreshed");
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.Statistics.ExpiredRemovals.Should().Be(0);
        reloads.Should().Be(1);
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
            staleRead.IsCompleted.Should().BeFalse();
            // Only the policy changes. The Entry, value publication, and original
            // timestamps remain the same, so a revision check alone is insufficient.
            policy.SetDuration(extendedDuration);
            policy.AgeOf(1).Should().Be(originalDuration);
            policy.GetExpiresAfter(1).Should().Be(extendedDuration - originalDuration);
            cache.Policy.TryGetQuietly(1, out string? renewed).Should().BeTrue();
            renewed.Should().Be("resident");
        }
        finally
        {
            cleanup.Release();
            await staleRead.WaitAsync(Watchdog);
        }

        cleanup.TimedOut.Should().BeFalse();
        cache.Policy.TryGetQuietly(1, out string? current).Should().BeTrue();
        current.Should().Be("resident");
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.Statistics.ExpiredRemovals.Should().Be(0);
        engine.AssertInvariants();
        // Quiet observations must not touch TTI, and the extension must not turn
        // expiration off. The unchanged publication expires at its new deadline.
        clock.Advance(extendedDuration - originalDuration);
        cache.Policy.TryGetQuietly(1, out _).Should().BeFalse();
        cache.TryGet(1, out _).Should().BeFalse();
        cache.Statistics.ExpiredRemovals.Should().Be(1);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }
}
