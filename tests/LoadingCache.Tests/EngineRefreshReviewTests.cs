using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

/// <summary>Adversarial checks for refresh ownership across expiration maintenance.</summary>
public sealed class EngineRefreshReviewTests
{
    /// <summary>A delayed read-expiry callback cannot revive an expired snapshot or lose its refresh.</summary>
    [Test]
    public async Task ReadExpiryCallbackCrossingHardDeadlinePreservesRefresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resumeReload = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .TimeProvider(clock)
            .ExpireAfter(new GatedReadExpiry(entered, resumeRead))
            .BuildAsyncLoading(
                (_, _) =>
                    Interlocked.Increment(ref calls) == 1 ? Task.FromResult(1) : resumeReload.Task
            );
        (await cache.GetAsync(1)).Should().Be(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        Task<bool> oldRead = Task.Factory.StartNew(
            static state => ((IAsyncLoadingCache<int, int>)state!).TryGet(1, out _),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(2));
            resumeRead.TrySetResult();
            (await oldRead.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should()
                .BeTrue("this overlapping reader checked freshness before the clock advanced");
            cache.TryGet(1, out _).Should().BeFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            joined.IsCompleted.Should().BeFalse();
            calls.Should().Be(2);
            resumeReload.TrySetResult(2);
            (await joined.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
        }
        finally
        {
            resumeRead.TrySetResult();
            resumeReload.TrySetResult(2);
            await Task.WhenAll(oldRead, refresh).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A retired refresh cannot publish or remove a replacement in a new slot or epoch.</summary>
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task InvalidationAndClearFenceLateRefreshOutcomes(bool clear, bool fail)
    {
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        await using IAsyncLoadingCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildAsyncLoading(
                (_, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(1) : release.Task
            );
        (await cache.GetAsync(1)).Should().Be(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            calls.Should().Be(2);
            refresh.IsCompleted.Should().BeFalse();
            if (clear)
            {
                cache.Clear();
            }
            else
            {
                cache.Invalidate(1).Should().BeTrue();
            }
            cache.Set(1, 99);
            if (fail)
            {
                release.TrySetException(new InvalidOperationException("retired refresh"));
                await FluentActions
                    .Awaiting(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Should()
                    .ThrowExactlyAsync<InvalidOperationException>();
            }
            else
            {
                release.TrySetResult(2);
                (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
            }
            cache.CleanUp();
            cache.TryGet(1, out int value).Should().BeTrue();
            value.Should().Be(99);
            cache.EstimatedCount.Should().Be(1);
        }
        finally
        {
            release.TrySetResult(2);
            try
            {
                await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException) when (fail)
            {
                // The failure is asserted above; still observe it on assertion failure.
            }
        }
    }

    /// <summary>Expiry inspection and rejected extension must preserve an expired refresh owner.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ExpirationPolicyOperationsPreserveAnExpiredRefresh(bool variableExpiry)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        CacheBuilder<int, int> builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .TimeProvider(clock);
        if (variableExpiry)
        {
            builder.ExpireAfter(new TwoSecondExpiry());
        }
        else
        {
            builder.ExpireAfterWrite(TimeSpan.FromSeconds(2));
        }
        await using IAsyncLoadingCache<int, int> cache = builder.BuildAsyncLoading(
            (_, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(1) : release.Task
        );
        (await cache.GetAsync(1)).Should().Be(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            if (variableExpiry)
            {
                IVariableExpirationPolicy<int, int> policy = cache.Policy.VariableExpiration!;
                policy.AgeOf(1).Should().Be(TimeSpan.FromSeconds(2));
                policy.GetExpiresAfter(1).Should().Be(TimeSpan.Zero);
                policy.SetExpiresAfter(1, TimeSpan.FromDays(1)).Should().BeFalse();
            }
            else
            {
                IFixedExpirationPolicy<int, int> policy = cache.Policy.ExpireAfterWrite!;
                policy.AgeOf(1).Should().Be(TimeSpan.FromSeconds(2));
                policy.GetExpiresAfter(1).Should().Be(TimeSpan.Zero);
            }
            cache.TryGet(1, out _).Should().BeFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            joined.IsCompleted.Should().BeFalse();
            calls.Should().Be(2);
            release.TrySetResult(2);
            (await joined.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
        }
        finally
        {
            release.TrySetResult(2);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Expiration cleanup must hide the old value while preserving the refresh flight.</summary>
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ExpirationMaintenancePreservesAnOngoingRefresh(
        bool promptScheduler,
        bool variableExpiry
    )
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var release = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        CacheBuilder<int, int> builder = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock);
        if (variableExpiry)
        {
            builder.ExpireAfter(new TwoSecondExpiry());
        }
        else
        {
            builder.ExpireAfterWrite(TimeSpan.FromSeconds(2));
        }
        if (promptScheduler)
        {
            builder.EnableExpirationScheduler();
        }

        await using IAsyncLoadingCache<int, int> cache = builder.BuildAsyncLoading(
            (_, _) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(1) : release.Task
        );
        (await cache.GetAsync(1)).Should().Be(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            calls.Should().Be(2);
            refresh.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromSeconds(2));
            cache.CleanUp();
            cache.TryGet(1, out _).Should().BeFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            joined.IsCompleted.Should().BeFalse();
            calls.Should().Be(2, "cleanup must preserve the one existing refresh");
            release.TrySetResult(2);
            (await joined.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
        }
        finally
        {
            release.TrySetResult(2);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        cache.TryGet(1, out int current).Should().BeTrue();
        current.Should().Be(2);
        cache.EstimatedCount.Should().Be(1);
    }

    private sealed class TwoSecondExpiry : IExpiry<int, int>
    {
        public TimeSpan ExpireAfterCreate(int key, int value, TimeSpan currentDuration) =>
            TimeSpan.FromSeconds(2);

        public TimeSpan ExpireAfterUpdate(int key, int value, TimeSpan currentDuration) =>
            TimeSpan.FromSeconds(2);

        public TimeSpan ExpireAfterRead(int key, int value, TimeSpan currentDuration) =>
            currentDuration;
    }

    private sealed class GatedReadExpiry(TaskCompletionSource entered, TaskCompletionSource resume)
        : IExpiry<int, int>
    {
        public TimeSpan ExpireAfterCreate(int key, int value, TimeSpan currentDuration) =>
            TimeSpan.FromSeconds(2);

        public TimeSpan ExpireAfterUpdate(int key, int value, TimeSpan currentDuration) =>
            TimeSpan.FromSeconds(2);

        public TimeSpan ExpireAfterRead(int key, int value, TimeSpan currentDuration)
        {
            entered.TrySetResult();
            resume.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return TimeSpan.FromDays(1);
        }
    }
}
