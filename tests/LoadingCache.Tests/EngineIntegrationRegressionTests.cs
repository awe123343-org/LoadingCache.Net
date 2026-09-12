using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

/// <summary>Integration checks for the shared engine's public behavior.</summary>
public sealed class EngineIntegrationRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ResidentHitProgressesWhileAnotherKeyHoldsTheMutationGate()
    {
        using var comparer = new PausingComparer();
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Comparer(comparer)
            .Build();
        cache.Put(1, 42);
        comparer.Armed = true;

        Task writer = Task.Run(() => cache.Put(99, 99));
        try
        {
            await comparer.Entered.Task.WaitAsync(Watchdog);
            Task<int> reader = Task.Run(() =>
                cache.GetOrAdd(
                    1,
                    static _ => throw new AssertionException("A resident hit invoked its factory.")
                )
            );
            (await reader.WaitAsync(Watchdog)).Should().Be(42);
        }
        finally
        {
            comparer.Release.Set();
            await writer.WaitAsync(Watchdog);
        }
    }

    [Test]
    public async Task ReplacingAResidentWithAPendingTaskRemovesItsPolicyWeight()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumWeight(100)
            .MaximumResidentCount(2)
            .MaxConcurrentLoads(2)
            .Weigher((_, value) => value.Length)
            .BuildAsync();
        (await cache.GetOrAddAsync(1, (_, _) => Task.FromResult("old"))).Should().Be("old");
        cache.Policy.Eviction!.WeightedSize.Should().Be(3);
        var pending = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        cache.Put(1, pending.Task);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
        cache.Policy.Eviction.WeightedSize.Should().Be(0);
        pending.SetResult("replacement");
        (
            await cache.GetOrAddAsync(
                1,
                (_, _) => throw new AssertionException("The stored task must be joined.")
            )
        )
            .Should()
            .Be("replacement");
        cache.CleanUp();
        cache.Policy.Eviction.WeightedSize.Should().Be(11);
    }

    [Test]
    public async Task ManualAsyncTaskLookupHasStableIdentityForTheCurrentGeneration()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .BuildAsync();
        var source = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Task<string> waiter = cache.GetOrAddAsync(1, (_, _) => source.Task).AsTask();
        cache.TryGetTask(1, out Task<string>? before).Should().BeTrue();
        before.Should().BeSameAs(waiter);
        source.SetResult("ready");
        (await waiter.WaitAsync(Watchdog)).Should().Be("ready");
        cache.TryGetTask(1, out Task<string>? after).Should().BeTrue();
        after.Should().BeSameAs(before);
        cache.TryGetTask(1, out Task<string>? again).Should().BeTrue();
        again.Should().BeSameAs(after);
    }

    [Test]
    public void SynchronousColdFactoryExecutesOnItsCallingThread()
    {
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Build();
        int caller = Environment.CurrentManagedThreadId;
        cache.GetOrAdd(1, _ => Environment.CurrentManagedThreadId).Should().Be(caller);
    }

    // Deliberate test pause at the dictionary boundary, equivalent to suspending
    // a producer thread. Real comparers must remain fast and non-reentrant.
    private sealed class PausingComparer : IEqualityComparer<int>, IDisposable
    {
        internal volatile bool Armed;
        internal TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public bool Equals(int x, int y) => x == y;

        public int GetHashCode(int key)
        {
            if (!Armed || key != 99)
            {
                return key;
            }

            Entered.TrySetResult(true);
            return Release.Wait(Watchdog)
                ? key
                : throw new TimeoutException("The test did not release its paused writer.");
        }

        public void Dispose() => Release.Dispose();
    }
}
