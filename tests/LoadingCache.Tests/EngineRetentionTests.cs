using System.Runtime.CompilerServices;

namespace LoadingCache.Tests;

/// <summary>Checks selected live-root boundaries without relying on allocation counts.</summary>
public sealed class EngineRetentionTests
{
    /// <summary>A retired load must not keep unrelated values from its old epoch alive.</summary>
    [Test]
    public async Task RetiredLoadDoesNotRetainOtherValuesFromClearedEpoch()
    {
        var release = new TaskCompletionSource<Payload>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using IAsyncLoadingCache<int, Payload> cache = CacheBuilder
            .Create<int, Payload>()
            .MaximumSize(64)
            .MaxConcurrentLoads(1)
            .BuildAsyncLoading((_, _) => release.Task);
        Task<Payload> oldWaiter = cache.GetAsync(0).AsTask();
        WeakReference[] retiredValues = PopulateAndClear(cache);
        try
        {
            await Assert.That(oldWaiter.IsCompleted).IsFalse();
            CollectTargets(retiredValues);
            await Assert.That(retiredValues).All(reference => !reference.IsAlive);
            await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        }
        finally
        {
            release.TrySetResult(new Payload());
            await oldWaiter.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        GC.KeepAlive(cache);
    }

    /// <summary>A detached refresh may retain its old value, but not an old policy chain.</summary>
    [Test]
    public async Task RetiredRefreshDoesNotRetainUnrelatedPolicyValues()
    {
        var release = new TaskCompletionSource<Payload>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        await using IAsyncLoadingCache<int, Payload> cache = CacheBuilder
            .Create<int, Payload>()
            .MaximumSize(64)
            .MaxConcurrentLoads(1)
            .BuildAsyncLoading(
                (_, _) =>
                    Interlocked.Increment(ref calls) == 1
                        ? Task.FromResult(new Payload())
                        : release.Task
            );
        _ = await cache.GetAsync(0);
        Task<Payload> refresh = cache.RefreshAsync(0).AsTask();
        WeakReference[] retiredValues = PopulateAndClear(cache);
        try
        {
            await Assert.That(refresh.IsCompleted).IsFalse();
            CollectTargets(retiredValues);
            await Assert.That(retiredValues).All(reference => !reference.IsAlive);
        }
        finally
        {
            release.TrySetResult(new Payload());
            await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        GC.KeepAlive(cache);
    }

    /// <summary>Explicit removal releases both mapping and policy references to old values.</summary>
    [Test]
    public async Task RepeatedReplacementAndCleanupDoNotRetainHistoricalValues()
    {
        using ICache<int, Payload> cache = CacheBuilder
            .Create<int, Payload>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .Build();
        WeakReference[] retiredValues = ReplaceAndInvalidate(cache);
        cache.CleanUp();
        CollectTargets(retiredValues);
        await Assert.That(retiredValues).All(reference => !reference.IsAlive);
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        ((Cache<int, Payload>)cache).AssertInvariants();
        GC.KeepAlive(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PopulateAndClear(IAsyncLoadingCache<int, Payload> cache)
    {
        var references = new WeakReference[32];
        for (int index = 0; index < references.Length; index++)
        {
            var value = new Payload();
            references[index] = new WeakReference(value);
            cache.Set(index + 1, value);
        }

        cache.Clear();
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ReplaceAndInvalidate(ICache<int, Payload> cache)
    {
        var references = new WeakReference[256];
        for (int index = 0; index < references.Length; index++)
        {
            var value = new Payload();
            references[index] = new WeakReference(value);
            cache.Put(index % 8, value);
        }

        cache.Clear();
        return references;
    }

    private static void CollectTargets(WeakReference[] references)
    {
        // NoInlining factories keep temporary local roots outside this frame.
        // This is evidence for these specific graphs, not a full leak proof.
        for (
            int attempt = 0;
            attempt < 8 && references.Any(reference => reference.IsAlive);
            attempt++
        )
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class Payload;
}
