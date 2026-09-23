using TUnit.Assertions.Exceptions;

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
        Task writer = Task.Factory.StartNew(
            static state => ((ICache<int, int>)state!).Put(99, 99),
            cache,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default
        );
        Task<int>? reader = null;
        try
        {
            await comparer.Entered.Task.WaitAsync(Watchdog);
            reader = Task.Factory.StartNew(
                static state =>
                    ((ICache<int, int>)state!).GetOrAdd(
                        1,
                        static _ =>
                            throw new AssertionException("A resident hit invoked its factory.")
                    ),
                cache,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            );
            await Assert.That((await reader.WaitAsync(Watchdog))).IsEqualTo(42);
        }
        finally
        {
            comparer.Release.Set();
            await Task.WhenAll(writer, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
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
        await Assert
            .That((await cache.GetOrAddAsync(1, (_, _) => Task.FromResult("old"))))
            .IsEqualTo("old");
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(3);
        var pending = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        cache.Put(1, pending.Task);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsEqualTo(0);
        pending.SetResult("replacement");
        await Assert
            .That(
                (
                    await cache.GetOrAddAsync(
                        1,
                        (_, _) => throw new AssertionException("The stored task must be joined.")
                    )
                )
            )
            .IsEqualTo("replacement");
        cache.CleanUp();
        await Assert.That(cache.Policy.Eviction.WeightedSize).IsEqualTo(11);
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
        await Assert.That(cache.TryGetTask(1, out Task<string>? before)).IsTrue();
        Assert.NotNull(before);
        await Assert.That(ReferenceEquals(before, waiter)).IsTrue();
        source.SetResult("ready");
        await Assert.That((await waiter.WaitAsync(Watchdog))).IsEqualTo("ready");
        await Assert.That(cache.TryGetTask(1, out Task<string>? after)).IsTrue();
        Assert.NotNull(after);
        await Assert.That(ReferenceEquals(after, before)).IsTrue();
        await Assert.That(cache.TryGetTask(1, out Task<string>? again)).IsTrue();
        Assert.NotNull(again);
        await Assert.That(ReferenceEquals(again, after)).IsTrue();
    }

    [Test]
    public async Task SynchronousColdFactoryExecutesOnItsCallingThread()
    {
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Build();
        int caller = Environment.CurrentManagedThreadId;
        await Assert
            .That(cache.GetOrAdd(1, _ => Environment.CurrentManagedThreadId))
            .IsEqualTo(caller);
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
