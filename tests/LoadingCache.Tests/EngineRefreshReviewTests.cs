using Microsoft.Extensions.Time.Testing;

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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
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
            await Assert
                .That((await oldRead.WaitAsync(TimeSpan.FromSeconds(5))))
                .IsTrue()
                .Because("this overlapping reader checked freshness before the clock advanced");
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            await Assert.That(joined.IsCompleted).IsFalse();
            await Assert.That(calls).IsEqualTo(2);
            resumeReload.TrySetResult(2);
            await Assert.That((await joined.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo(2);
        }
        finally
        {
            resumeRead.TrySetResult();
            resumeReload.TrySetResult(2);
            await Task.WhenAll(oldRead, refresh).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A retired refresh cannot publish or remove a replacement in a new slot or epoch.</summary>
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            await Assert.That(calls).IsEqualTo(2);
            await Assert.That(refresh.IsCompleted).IsFalse();
            if (clear)
            {
                cache.Clear();
            }
            else
            {
                await Assert.That(cache.Invalidate(1)).IsTrue();
            }

            cache.Set(1, 99);
            if (fail)
            {
                release.TrySetException(new InvalidOperationException("retired refresh"));
                await Assert
                    .That(() => refresh.WaitAsync(TimeSpan.FromSeconds(5)))
                    .ThrowsExactly<InvalidOperationException>();
            }
            else
            {
                release.TrySetResult(2);
                await Assert.That((await refresh.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo(2);
            }

            cache.CleanUp();
            await Assert.That(cache.TryGet(1, out int value)).IsTrue();
            await Assert.That(value).IsEqualTo(99);
            await Assert.That(cache.EstimatedCount).IsEqualTo(1);
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
    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            if (variableExpiry)
            {
                IVariableExpirationPolicy<int, int> policy = cache.Policy.VariableExpiration!;
                await Assert.That(policy.AgeOf(1)).IsEqualTo(TimeSpan.FromSeconds(2));
                await Assert.That(policy.GetExpiresAfter(1)).IsEqualTo(TimeSpan.Zero);
                await Assert.That(policy.SetExpiresAfter(1, TimeSpan.FromDays(1))).IsFalse();
            }
            else
            {
                IFixedExpirationPolicy<int, int> policy = cache.Policy.ExpireAfterWrite!;
                await Assert.That(policy.AgeOf(1)).IsEqualTo(TimeSpan.FromSeconds(2));
                await Assert.That(policy.GetExpiresAfter(1)).IsEqualTo(TimeSpan.Zero);
            }

            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            await Assert.That(joined.IsCompleted).IsFalse();
            await Assert.That(calls).IsEqualTo(2);
            release.TrySetResult(2);
            await Assert.That((await joined.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo(2);
        }
        finally
        {
            release.TrySetResult(2);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Expiration cleanup must hide the old value while preserving the refresh flight.</summary>
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
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
        await Assert.That((await cache.GetAsync(1))).IsEqualTo(1);
        Task<int> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            await Assert.That(calls).IsEqualTo(2);
            await Assert.That(refresh.IsCompleted).IsFalse();
            clock.Advance(TimeSpan.FromSeconds(2));
            cache.CleanUp();
            await Assert.That(cache.TryGet(1, out _)).IsFalse();
            Task<int> joined = cache.GetAsync(1).AsTask();
            await Assert.That(joined.IsCompleted).IsFalse();
            await Assert
                .That(calls)
                .IsEqualTo(2)
                .Because("cleanup must preserve the one existing refresh");
            release.TrySetResult(2);
            await Assert.That((await joined.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo(2);
        }
        finally
        {
            release.TrySetResult(2);
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Assert.That(cache.TryGet(1, out int current)).IsTrue();
        await Assert.That(current).IsEqualTo(2);
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
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
