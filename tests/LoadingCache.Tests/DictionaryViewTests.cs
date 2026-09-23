using JetBrains.Annotations;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class DictionaryViewTests
{
    [Test]
    public async Task ViewUsesCacheComparerAndSupportsMutableDictionaryOperations()
    {
        using ICache<string, string> cache = CacheBuilder
            .Create<string, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .Build();
        SyncCacheDictionary<string, string> dictionary = cache.AsDictionary();
        dictionary.Add("Key", "one");
        await Assert.That(dictionary.ContainsKey("key")).IsTrue();
        await Assert.That(dictionary["KEY"]).IsEqualTo("one");
        await Assert.That(dictionary.TryAdd("kEy", "two")).IsFalse();
        await Assert.That(() => dictionary.Add("KEY", "two")).ThrowsExactly<ArgumentException>();
        await Assert.That(dictionary.TryUpdate("key", "two", "one")).IsTrue();
        await Assert.That(dictionary.TryUpdate("key", "three", "one")).IsFalse();
        await Assert.That(dictionary.TryRemove("KEY", "one")).IsFalse();
        await Assert.That(dictionary.TryRemove("key", "two")).IsTrue();
        await Assert.That(dictionary.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerationAndCollectionsAreSnapshotsOfReadyValues()
    {
        using ICache<int, string> cache = CreateCache<int, string>();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        dictionary[1] = "one";
        KeyValuePair<int, string>[] snapshot = [.. dictionary];
        dictionary[2] = "two";
        await Assert
            .That(snapshot)
            .IsEquivalentTo(
                [new KeyValuePair<int, string>(1, "one")],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert
            .That(dictionary.Keys)
            .IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(dictionary.Values)
            .IsEquivalentTo(["one", "two"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(dictionary.Contains(new KeyValuePair<int, string>(1, "one"))).IsTrue();
        await Assert.That(dictionary.Remove(new KeyValuePair<int, string>(1, "wrong"))).IsFalse();
    }

    [Test]
    public async Task ExpiredValuesAreAbsentFromDictionaryView()
    {
        var clock = new FakeTimeProvider();
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .Build();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        dictionary[1] = "one";
        await Assert.That(dictionary.Count).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(dictionary.ContainsKey(1)).IsFalse();
        await Assert.That(dictionary.Count).IsEqualTo(0);
        await Assert.That(dictionary).IsEmpty();
    }

    [Test]
    public async Task AsyncViewDoesNotLoadOrBlockOnPendingFlight()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        AsyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        var source = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ValueTask<string> pending = cache.GetOrAddAsync(1, (_, _) => source.Task);
        await Assert.That(dictionary.TryGetValue(1, out _)).IsFalse();
        await Assert.That(dictionary.Count).IsEqualTo(0);
        await Assert.That(dictionary.Remove(1)).IsFalse();
        await Assert.That(dictionary.TryAdd(1, "replacement")).IsFalse();
        dictionary[1] = "replacement";
        source.SetResult("stale");
        await Assert.That((await pending)).IsEqualTo("stale");
        await Assert.That(dictionary[1]).IsEqualTo("replacement");
    }

    [Test]
    public async Task DictionaryReadDoesNotTriggerLoadingOrRefresh()
    {
        var clock = new FakeTimeProvider();
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(
                (key, _) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    return Task.FromResult($"{key}:{call}");
                }
            );
        AsyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("1:1");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(dictionary.TryGetValue(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("1:1");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task TryAddTreatsExpiredEntryWithActiveRefreshAsOccupied()
    {
        var clock = new FakeTimeProvider();
        var refreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var refreshResult = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .ExpireAfterWrite(TimeSpan.FromSeconds(2))
            .RefreshAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(clock)
            .BuildAsyncLoading(
                (key, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        return Task.FromResult($"{key}:one");
                    }

                    refreshStarted.TrySetResult(true);
                    return refreshResult.Task;
                }
            );
        AsyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("1:one");
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await cache.GetAsync(1))).IsEqualTo("1:one");
        await Assert.That((await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)))).IsTrue();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(dictionary.TryAdd(1, "replacement")).IsFalse();
        ValueTask<string> refresh = cache.RefreshAsync(1);
        refreshResult.SetResult("1:two");
        await Assert.That((await refresh)).IsEqualTo("1:two");
        await Assert.That(dictionary[1]).IsEqualTo("1:two");
    }

    [Test]
    public async Task AsyncViewExposesAsyncFactoryWithoutBlocking()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        await Assert
            .That((await cache.AsDictionary().GetOrAddAsync(1, (_, _) => Task.FromResult("one"))))
            .IsEqualTo("one");
    }

    [Test]
    public async Task AsyncDictionaryFactoryJoinsExistingFlight()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        AsyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        var source = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        ValueTask<string> first = dictionary.GetOrAddAsync(
            1,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return source.Task;
            }
        );
        ValueTask<string> second = dictionary.GetOrAddAsync(
            1,
            (_, _) => throw new AssertionException("Factory was called twice.")
        );
        source.SetResult("one");
        await Assert.That((await first)).IsEqualTo("one");
        await Assert.That((await second)).IsEqualTo("one");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ConditionalUpdateFencesAConcurrentSameReferenceReplacement()
    {
        var updateValue = new Box("updated");
        var original = new Box("original");
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new ManualResetEventSlim(false);
        Action waitForRelease = release.Wait;
        using ICache<int, Box> cache = CacheBuilder
            .Create<int, Box>()
            .MaximumWeight(8)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(8)
            .Weigher(
                (_, value) =>
                {
                    if (!ReferenceEquals(value, updateValue))
                    {
                        return 1;
                    }

                    entered.TrySetResult(true);
                    waitForRelease();
                    return 1;
                }
            )
            .Build();
        SyncCacheDictionary<int, Box> dictionary = cache.AsDictionary();
        dictionary[1] = original;
        Task<bool> update = Task.Run(() => dictionary.TryUpdate(1, updateValue, original));
        try
        {
            await Assert.That((await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)))).IsTrue();
            dictionary[1] = original;
            release.Set();
            await Assert.That((await update.WaitAsync(TimeSpan.FromSeconds(10)))).IsFalse();
            await Assert.That(ReferenceEquals(dictionary[1], original)).IsTrue();
        }
        finally
        {
            release.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            release.Dispose();
        }
    }

    [Test]
    public async Task ConditionalUpdateDoesNotHoldCacheLocksDuringValueEquality()
    {
        ICache<int, ReentrantValue> cache = CreateCache<int, ReentrantValue>();
        var replacement = new ReentrantValue("same");
        var current = new ReentrantValue("same");
        current.OnEquals = () =>
        {
            Task replacementTask = Task.Run(() => cache.Put(1, current));
            if (!replacementTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new AssertionException("Cache mutation was blocked by value equality.");
            }
        };
        using (cache)
        {
            cache.Put(1, current);
            SyncCacheDictionary<int, ReentrantValue> dictionary = cache.AsDictionary();
            await Assert
                .That(dictionary.TryUpdate(1, replacement, new ReentrantValue("same")))
                .IsFalse();
            await Assert.That(ReferenceEquals(dictionary[1], current)).IsTrue();
        }
    }

    [Test]
    public async Task ConditionalRemoveDoesNotHoldCacheLocksDuringValueEquality()
    {
        ICache<int, ReentrantValue> cache = CreateCache<int, ReentrantValue>();
        var current = new ReentrantValue("same");
        current.OnEquals = () =>
        {
            Task replacementTask = Task.Run(() => cache.Put(1, current));
            if (!replacementTask.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new AssertionException("Cache mutation was blocked by value equality.");
            }
        };
        using (cache)
        {
            cache.Put(1, current);
            SyncCacheDictionary<int, ReentrantValue> dictionary = cache.AsDictionary();
            await Assert.That(dictionary.TryRemove(1, new ReentrantValue("same"))).IsFalse();
            await Assert.That(ReferenceEquals(dictionary[1], current)).IsTrue();
        }
    }

    [Test]
    public async Task SynchronousViewUsesSharedFactoryFlight()
    {
        using ICache<int, string> cache = CreateCache<int, string>();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        int calls = 0;
        dictionary.GetOrAdd(
            1,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return "one";
            }
        );
        dictionary.GetOrAdd(1, _ => throw new AssertionException("Factory was called on a hit."));
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(dictionary[1]).IsEqualTo("one");
    }

    [Test]
    public async Task AddOrUpdateRetriesConcurrentTransformsWithoutLostUpdates()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        dictionary[1] = 0;
        Task[] workers =
        [
            .. Enumerable
                .Range(0, 8)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 250; i++)
                        {
                            dictionary.AddOrUpdate(
                                1,
                                static _ => 1,
                                static (_, current) => current + 1
                            );
                        }
                    })
                ),
        ];
        await Task.WhenAll(workers);
        await Assert.That(dictionary[1]).IsEqualTo(2_000);
    }

    [Test]
    public async Task ComputeRetriesMissingSnapshotAfterSetAndInvalidateAba()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int calls = 0;
        Task<CacheMutation<int>> compute = Task.Run(() =>
            dictionary.Compute(
                1,
                (_, current) =>
                {
                    int call = Interlocked.Increment(ref calls);
                    if (current.HasValue)
                        Assert.Fail("Expected current.HasValue to be false ().");
                    if (call != 1)
                    {
                        return CacheMutation.Set(20);
                    }

                    entered.SetResult(true);
                    release.Task.GetAwaiter().GetResult();
                    return CacheMutation.Set(10);
                }
            )
        );
        await Assert.That((await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)))).IsTrue();
        dictionary[1] = 2;
        await Assert.That(dictionary.Remove(1)).IsTrue();
        release.SetResult(true);
        await Assert
            .That((await compute.WaitAsync(TimeSpan.FromSeconds(10))).Kind)
            .IsEqualTo(CacheMutationKind.Set);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(dictionary[1]).IsEqualTo(20);
    }

    [Test]
    public async Task TryAddTreatsCollectedWeakValueAsAbsent()
    {
        using ICache<int, ReentrantValue> cache = CacheBuilder
            .Create<int, ReentrantValue>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .WeakValues()
            .RecordStatistics()
            .Build();
        WeakReference oldReference = CreateWeakValue(cache);
        ForceCollection(oldReference);
        await Assert.That(oldReference.IsAlive).IsFalse();
        SyncCacheDictionary<int, ReentrantValue> dictionary = cache.AsDictionary();
        var replacement = new ReentrantValue("replacement");
        await Assert.That(dictionary.TryAdd(1, replacement)).IsTrue();
        await Assert.That(ReferenceEquals(dictionary[1], replacement)).IsTrue();
        await Assert.That(cache.Statistics.Collected).IsEqualTo(1);
    }

    [Test]
    public async Task ComputeUsesExplicitMutationForDefaultValuesAndRemoval()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        CacheMutation<int> added = dictionary.Compute(
            1,
            static (_, current) =>
            {
                if (current.HasValue)
                    Assert.Fail("Expected current.HasValue to be false ().");
                return CacheMutation.Set(0);
            }
        );
        await Assert.That(added.Kind).IsEqualTo(CacheMutationKind.Set);
        await Assert.That(dictionary[1]).IsEqualTo(0);
        CacheMutation<int> removed = dictionary.ComputeIfPresent(
            1,
            static (_, current) =>
            {
                if ((current) != (0))
                    Assert.Fail("Expected current to equal (0).");
                return CacheMutation.Remove<int>();
            }
        );
        await Assert.That(removed.Kind).IsEqualTo(CacheMutationKind.Remove);
        await Assert.That(dictionary.ContainsKey(1)).IsFalse();
        bool called = false;
        await Assert
            .That(
                dictionary
                    .ComputeIfPresent(
                        1,
                        (_, _) =>
                        {
                            called = true;
                            return CacheMutation.Set(1);
                        }
                    )
                    .Kind
            )
            .IsEqualTo(CacheMutationKind.Keep);
        await Assert.That(called).IsFalse();
    }

    [Test]
    public async Task MergeCombinesValuesAndKeepsCallbackOutsideCacheLocks()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        await Assert
            .That(dictionary.Merge(1, 2, static (current, added) => current + added))
            .IsEqualTo(2);
        await Assert
            .That(dictionary.Merge(1, 3, static (current, added) => current + added))
            .IsEqualTo(5);
        bool reentered = false;
        await Assert
            .That(() =>
                dictionary.Compute(
                    1,
                    (_, current) =>
                    {
                        if (reentered)
                        {
                            return CacheMutation.Set(current.Value + 1);
                        }

                        reentered = true;
                        dictionary[1] = 10;
                        return CacheMutation.Set(current.Value + 1);
                    }
                )
            )
            .ThrowsExactly<LoadingCacheReentrancyException>();
        await Assert.That(dictionary[1]).IsEqualTo(10);
    }

    [Test]
    public async Task TransformCallbackFailureLeavesExistingValueUntouched()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        dictionary[1] = 7;
        await Assert
            .That(() =>
                dictionary.Compute(
                    1,
                    static (_, _) => throw new InvalidOperationException("callback failure")
                )
            )
            .ThrowsExactly<InvalidOperationException>()
            .WithMessage("callback failure");
        await Assert.That(dictionary[1]).IsEqualTo(7);
    }

    [Test]
    public async Task TransformsReplacePendingAsyncFlightWithoutWaiting()
    {
        await using IAsyncCache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();
        AsyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        var source = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        ValueTask<int> pending = dictionary.GetOrAddAsync(1, (_, _) => source.Task);
        bool called = false;
        await Assert
            .That(
                dictionary
                    .ComputeIfPresent(
                        1,
                        (_, _) =>
                        {
                            called = true;
                            return CacheMutation.Set(9);
                        }
                    )
                    .Kind
            )
            .IsEqualTo(CacheMutationKind.Keep);
        await Assert.That(called).IsFalse();
        await Assert
            .That(dictionary.AddOrUpdate(1, static _ => 5, static (_, value) => value + 1))
            .IsEqualTo(5);
        source.SetResult(4);
        await Assert.That((await pending)).IsEqualTo(4);
        await Assert.That(dictionary[1]).IsEqualTo(5);
    }

    [Test]
    public async Task TransformCallbackClearFailsFastAndRestoresScope()
    {
        using ICache<int, int> cache = CreateCache<int, int>();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        dictionary[1] = 1;
        await Assert
            .That(() =>
                dictionary.Compute(
                    1,
                    (_, _) =>
                    {
                        dictionary.Clear();
                        return CacheMutation.Set(2);
                    }
                )
            )
            .ThrowsExactly<LoadingCacheReentrancyException>();
        await Assert.That(dictionary.ContainsKey(1)).IsFalse();
        dictionary.Compute(1, static (_, _) => CacheMutation.Set(3));
        await Assert.That(dictionary[1]).IsEqualTo(3);
    }

    [Test]
    public async Task OperationsAfterCacheDisposeAreRejected()
    {
        ICache<int, string> cache = CreateCache<int, string>();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        cache.Dispose();
        await Assert.That(() => dictionary.ContainsKey(1)).Throws<ObjectDisposedException>();
        await Assert.That(dictionary.Clear).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task NullKeysAreValidatedBeforeDisposedChecks()
    {
        ICache<string, string> cache = CreateCache<string, string>();
        SyncCacheDictionary<string, string> dictionary = cache.AsDictionary();
        cache.Dispose();
        await Assert
            .That(() => dictionary.ContainsKey(null!))
            .ThrowsExactly<ArgumentNullException>();
        await Assert.That(() => dictionary.Remove(null!)).ThrowsExactly<ArgumentNullException>();
    }

    private static ICache<TKey, TValue> CreateCache<TKey, TValue>()
        where TKey : notnull
        where TValue : notnull =>
        CacheBuilder.Create<TKey, TValue>().MaximumSize(8).MaxConcurrentLoads(8).Build();

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static WeakReference CreateWeakValue(ICache<int, ReentrantValue> cache)
    {
        var value = new ReentrantValue("old");
        cache.Put(1, value);
        WeakReference reference = new(value);
        GC.KeepAlive(value);
        return reference;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining
    )]
    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Yield();
        }
    }

    private sealed record Box([property: UsedImplicitly] string Value);

    private sealed class ReentrantValue(string value) : IEquatable<ReentrantValue>
    {
        private string Value { get; } = value;

        public Action? OnEquals;

        public bool Equals(ReentrantValue? other)
        {
            Action? callback = Interlocked.Exchange(ref OnEquals, null);
            callback?.Invoke();
            return other is not null && Value == other.Value;
        }

        public override bool Equals(object? obj) => Equals(obj as ReentrantValue);

        public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
    }
}
