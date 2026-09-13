using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class DictionaryViewTests
{
    [Test]
    public void ViewUsesCacheComparerAndSupportsMutableDictionaryOperations()
    {
        using ICache<string, string> cache = CacheBuilder
            .Create<string, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .Build();
        SyncCacheDictionary<string, string> dictionary = cache.AsDictionary();

        dictionary.Add("Key", "one");

        dictionary.ContainsKey("key").Should().BeTrue();
        dictionary["KEY"].Should().Be("one");
        dictionary.TryAdd("kEy", "two").Should().BeFalse();
        FluentActions
            .Invoking(() => dictionary.Add("KEY", "two"))
            .Should()
            .ThrowExactly<ArgumentException>();
        dictionary.TryUpdate("key", "two", "one").Should().BeTrue();
        dictionary.TryUpdate("key", "three", "one").Should().BeFalse();
        dictionary.TryRemove("KEY", "one").Should().BeFalse();
        dictionary.TryRemove("key", "two").Should().BeTrue();
        dictionary.Count.Should().Be(0);
    }

    [Test]
    public void EnumerationAndCollectionsAreSnapshotsOfReadyValues()
    {
        using ICache<int, string> cache = CreateCache<int, string>();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        dictionary[1] = "one";

        KeyValuePair<int, string>[] snapshot = dictionary.ToArray();
        dictionary[2] = "two";

        snapshot.Should().Equal([new KeyValuePair<int, string>(1, "one")]);
        dictionary.Keys.Should().Equal(1, 2);
        dictionary.Values.Should().Equal("one", "two");
        dictionary.Contains(new KeyValuePair<int, string>(1, "one")).Should().BeTrue();
        dictionary.Remove(new KeyValuePair<int, string>(1, "wrong")).Should().BeFalse();
    }

    [Test]
    public void ExpiredValuesAreAbsentFromDictionaryView()
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

        dictionary.Count.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        dictionary.ContainsKey(1).Should().BeFalse();
        dictionary.Count.Should().Be(0);
        dictionary.Should().BeEmpty();
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

        dictionary.TryGetValue(1, out _).Should().BeFalse();
        dictionary.Count.Should().Be(0);
        dictionary.Remove(1).Should().BeFalse();
        dictionary.TryAdd(1, "replacement").Should().BeFalse();

        dictionary[1] = "replacement";
        source.SetResult("stale");
        (await pending).Should().Be("stale");
        dictionary[1].Should().Be("replacement");
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

        (await cache.GetAsync(1)).Should().Be("1:1");
        clock.Advance(TimeSpan.FromSeconds(1));
        dictionary.TryGetValue(1, out string? value).Should().BeTrue();
        value.Should().Be("1:1");
        calls.Should().Be(1);
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

        (await cache.GetAsync(1)).Should().Be("1:one");
        clock.Advance(TimeSpan.FromSeconds(1));
        (await cache.GetAsync(1)).Should().Be("1:one");
        (await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1));
        dictionary.TryAdd(1, "replacement").Should().BeFalse();

        ValueTask<string> refresh = cache.RefreshAsync(1);
        refreshResult.SetResult("1:two");
        (await refresh).Should().Be("1:two");
        dictionary[1].Should().Be("1:two");
    }

    [Test]
    public async Task AsyncViewExposesAsyncFactoryWithoutBlocking()
    {
        await using IAsyncCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(8)
            .BuildAsync();

        (await cache.AsDictionary().GetOrAddAsync(1, (_, _) => Task.FromResult("one")))
            .Should()
            .Be("one");
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
        (await first).Should().Be("one");
        (await second).Should().Be("one");
        calls.Should().Be(1);
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
        using ICache<int, Box> cache = CacheBuilder
            .Create<int, Box>()
            .MaximumWeight(8)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(8)
            .Weigher(
                (_, value) =>
                {
                    if (ReferenceEquals(value, updateValue))
                    {
                        entered.TrySetResult(true);
                        release.Wait();
                    }

                    return 1;
                }
            )
            .Build();
        SyncCacheDictionary<int, Box> dictionary = cache.AsDictionary();
        dictionary[1] = original;

        Task<bool> update = Task.Run(() => dictionary.TryUpdate(1, updateValue, original));
        try
        {
            (await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
            dictionary[1] = original;
            release.Set();
            (await update.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            dictionary[1].Should().BeSameAs(original);
        }
        finally
        {
            release.Set();
            await update.WaitAsync(TimeSpan.FromSeconds(10));
            release.Dispose();
        }
    }

    [Test]
    public void ConditionalUpdateDoesNotHoldCacheLocksDuringValueEquality()
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

            dictionary.TryUpdate(1, replacement, new ReentrantValue("same")).Should().BeFalse();
            dictionary[1].Should().BeSameAs(current);
        }
    }

    [Test]
    public void ConditionalRemoveDoesNotHoldCacheLocksDuringValueEquality()
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

            dictionary.TryRemove(1, new ReentrantValue("same")).Should().BeFalse();
            dictionary[1].Should().BeSameAs(current);
        }
    }

    [Test]
    public void SynchronousViewUsesSharedFactoryFlight()
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

        calls.Should().Be(1);
        dictionary[1].Should().Be("one");
    }

    [Test]
    public void OperationsAfterCacheDisposeAreRejected()
    {
        ICache<int, string> cache = CreateCache<int, string>();
        SyncCacheDictionary<int, string> dictionary = cache.AsDictionary();
        cache.Dispose();

        FluentActions
            .Invoking(() => dictionary.ContainsKey(1))
            .Should()
            .Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => dictionary.Clear()).Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void NullKeysAreValidatedBeforeDisposedChecks()
    {
        ICache<string, string> cache = CreateCache<string, string>();
        SyncCacheDictionary<string, string> dictionary = cache.AsDictionary();
        cache.Dispose();

        FluentActions
            .Invoking(() => dictionary.ContainsKey(null!))
            .Should()
            .ThrowExactly<ArgumentNullException>();
        FluentActions
            .Invoking(() => dictionary.Remove(null!))
            .Should()
            .ThrowExactly<ArgumentNullException>();
    }

    private static ICache<TKey, TValue> CreateCache<TKey, TValue>()
        where TKey : notnull
        where TValue : notnull =>
        CacheBuilder.Create<TKey, TValue>().MaximumSize(8).MaxConcurrentLoads(8).Build();

    private sealed record Box(string Value);

    private sealed class ReentrantValue(string value) : IEquatable<ReentrantValue>
    {
        public string Value { get; } = value;

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
