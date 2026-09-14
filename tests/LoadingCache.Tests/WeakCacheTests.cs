using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.Maintenance;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class WeakCacheTests
{
    [Test]
    public void WeakKeysUseReferenceIdentityInsteadOfKeyEquality()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        Key first = new(7);
        Key equalByValue = new(7);

        cache.Put(first, new Value());

        cache.TryGet(first, out Value? firstValue).Should().BeTrue();
        firstValue.Should().NotBeNull();
        cache.TryGet(equalByValue, out _).Should().BeFalse();
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public async Task WeakKeyResidentHitDoesNotAllocateLookupProbe()
    {
        if (await AllocationTestProcess.RunIsolatedIfNeededAsync("weak-hit").ConfigureAwait(false))
            return;
        // Keep policy transport out of this measurement: its scheduler and
        // read-buffer work are separate from the authoritative key lookup.
        // The rejecting scheduler also prevents an accidental async work item.
        using CacheEngine<Key, Value> engine = new(
            new CacheEngineOptions<Key, Value>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 1,
                WeakKeys = true,
                Policy = new NoopCacheEnginePolicy(),
                MaintenanceScheduler = new RejectingMaintenanceScheduler(),
            }
        );
        Key key = new(7);
        engine.Put(key, new Value());
        engine.CleanUp();

        for (int index = 0; index < 10_000; index++)
        {
            engine.TryGet(key, out _).Should().BeTrue();
        }
        engine.CleanUp();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allHits = true;
        for (int index = 0; index < 10_000; index++)
        {
            allHits &= engine.TryGet(key, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allHits.Should().BeTrue();
        allocated.Should().Be(0);
    }

    [Test]
    public void WeakKeyLookupDoesNotInvokeKeyEqualityOrHashCodeOverrides()
    {
        using ICache<ThrowingKey, Value> cache = CacheBuilder
            .Create<ThrowingKey, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        ThrowingKey key = new();
        Value value = new();

        cache.Put(key, value);

        cache.TryGet(key, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(value);
    }

    [Test]
    public void ReplacedWeakKeyEntryCanBeCollectedAfterLookupAndCleanup()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        WeakReference key = PopulateReplacedWeakKey(cache);

        ForceCollection(key);

        key.IsAlive.Should().BeFalse();
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void WeakKeyIsCollectedAndRemovedByCleanup()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        WeakReference key = PopulateWeakKey(cache);

        ForceCollection(key);

        key.IsAlive.Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void WeakValuesDoNotKeepAValueAlive()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakValues()
            .Build();
        Key key = new(7);
        WeakReference value = PopulateWeakValue(cache, key);

        ForceCollection(value);

        value.IsAlive.Should().BeFalse();
        cache.TryGet(key, out _).Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void WeakValuesWorkWithSynchronousLoadingCaches()
    {
        using ILoadingCache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakValues()
            .BuildLoading(static _ => new Value());
        Key key = new(7);
        WeakReference value = PopulateWeakLoadedValue(cache, key);

        ForceCollection(value);

        value.IsAlive.Should().BeFalse();
        cache.TryGet(key, out _).Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public async Task WeakKeyAsyncFlightReleasesItsTemporaryKeyRoot()
    {
        var release = new TaskCompletionSource<Value>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using IAsyncLoadingCache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .BuildAsyncLoading((_, _) => release.Task);
        (Task<Value> wait, WeakReference key) = StartWeakKeyLoad(cache, release);

        release.SetResult(new Value());
        (await wait).Should().NotBeNull();
        ForceCollection(key);

        key.IsAlive.Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void CollectedWeakValueCannotRemoveItsSameKeyReplacement()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakValues()
            .Build();
        Key key = new(7);
        WeakReference oldReference = PopulateWeakValue(cache, key);

        ForceCollection(oldReference);
        oldReference.IsAlive.Should().BeFalse();
        cache.TryGet(key, out _).Should().BeFalse();

        Value replacement = new();
        cache.Put(key, replacement);
        cache.CleanUp();

        cache.TryGet(key, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public void ExpiredWeakValueCanBeReplacedWithoutLeavingAnOldGeneration()
    {
        var clock = new FakeTimeProvider();
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakValues()
            .TimeProvider(clock)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .Build();
        Key key = new(7);
        WeakReference oldReference = PopulateWeakValue(cache, key);

        clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet(key, out _).Should().BeFalse();
        ForceCollection(oldReference);
        oldReference.IsAlive.Should().BeFalse();

        Value replacement = new();
        cache.Put(key, replacement);
        cache.CleanUp();

        cache.TryGet(key, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public void StrongValueCanKeepAWeakKeyAliveThroughItsOwnObjectGraph()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        (WeakReference keyReference, Value value) = PopulateStrongValueWithWeakKey(cache);

        ForceCollection(keyReference);

        keyReference.IsAlive.Should().BeTrue();
        GC.KeepAlive(value);
        GC.KeepAlive(cache);
    }

    [Test]
    public void RepeatedWeakCollectionDoesNotLeaveGhostPolicyEntries()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(64)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .WeakValues()
            .Build();
        (WeakReference[] keys, WeakReference[] values) = PopulateWeakEntries(cache, 32);

        ForceCollection([.. keys, .. values]);
        cache.CleanUp();

        cache.EstimatedCount.Should().Be(0);
        cache.Policy.Eviction.Should().NotBeNull();
        cache.Policy.Eviction!.WeightedSize.Should().Be(0);
        cache.Policy.Eviction.Hottest(64).Should().BeEmpty();
        cache.Policy.Eviction.Coldest(64).Should().BeEmpty();
    }

    [Test]
    public void LiveWeakLookupAndDictionarySnapshotNeverExposeCollectedTargets()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .WeakValues()
            .Build();
        (WeakReference[] deadKeys, WeakReference[] deadValues) = PopulateWeakEntries(cache, 1);
        Key liveKey = new(99);
        Value liveValue = new();
        cache.Put(liveKey, liveValue);

        ForceCollection([.. deadKeys, .. deadValues]);

        cache.TryGet(liveKey, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(liveValue);
        KeyValuePair<Key, Value>[] snapshot = cache.AsDictionary().ToArray();
        snapshot
            .Should()
            .ContainSingle(pair =>
                ReferenceEquals(pair.Key, liveKey) && ReferenceEquals(pair.Value, liveValue)
            );
        snapshot.Should().NotContain(pair => pair.Key == null || pair.Value == null);
        cache.AsDictionary().Count.Should().Be(1);
    }

    [Test]
    public void SynchronousRefreshPublishesAWeakValue()
    {
        int calls = 0;
        using ILoadingCache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakValues()
            .RefreshAfterWrite(TimeSpan.FromHours(1))
            .BuildLoading(_ =>
            {
                Interlocked.Increment(ref calls);
                return new Value();
            });
        Key key = new(7);
        (WeakReference firstReference, WeakReference secondReference) = RefreshWeakValue(
            cache,
            key
        );

        calls.Should().Be(2);
        ForceCollection([firstReference, secondReference]);

        cache.TryGet(key, out _).Should().BeFalse();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void WeakKeysRejectAnExplicitComparer()
    {
        Action build = () =>
            CacheBuilder
                .Create<Key, Value>()
                .MaximumSize(8)
                .MaxConcurrentLoads(1)
                .Comparer(EqualityComparer<Key>.Default)
                .WeakKeys()
                .Build();

        build
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("identity");
    }

    [Test]
    public void WeakValuesRejectAsynchronousCachePersonalities()
    {
        Action build = () =>
            CacheBuilder
                .Create<Key, Value>()
                .MaximumSize(8)
                .MaxConcurrentLoads(1)
                .WeakValues()
                .BuildAsync();

        build
            .Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain("asynchronous");
    }

    [Test]
    public void WeakReferenceModesRejectValueTypeArguments()
    {
        Action weakKey = () =>
            CacheBuilder.Create<int, Value>().MaximumSize(8).MaxConcurrentLoads(1).WeakKeys();
        Action weakValue = () =>
            CacheBuilder.Create<Key, int>().MaximumSize(8).MaxConcurrentLoads(1).WeakValues();

        weakKey.Should().Throw<InvalidOperationException>();
        weakValue.Should().Throw<InvalidOperationException>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateWeakKey(ICache<Key, Value> cache)
    {
        Key key = new(7);
        cache.Put(key, new Value());
        WeakReference reference = new(key);
        cache.TryGet(key, out Value? value).Should().BeTrue();
        value.Should().NotBeNull();
        cache.EstimatedCount.Should().Be(1);
        GC.KeepAlive(key);
        GC.KeepAlive(value);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateReplacedWeakKey(ICache<Key, Value> cache)
    {
        Key key = new(7);
        cache.Put(key, new Value());
        cache.Put(key, new Value());
        cache.TryGet(key, out Value? value).Should().BeTrue();
        value.Should().NotBeNull();
        WeakReference reference = new(key);
        GC.KeepAlive(key);
        GC.KeepAlive(value);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateWeakValue(ICache<Key, Value> cache, Key key)
    {
        Value value = new();
        cache.Put(key, value);
        cache.TryGet(key, out Value? liveValue).Should().BeTrue();
        liveValue.Should().BeSameAs(value);
        WeakReference reference = new(value);
        GC.KeepAlive(value);
        GC.KeepAlive(liveValue);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateWeakLoadedValue(ILoadingCache<Key, Value> cache, Key key)
    {
        Value value = cache.Get(key);
        WeakReference reference = new(value);
        cache.TryGet(key, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(value);
        GC.KeepAlive(value);
        GC.KeepAlive(current);
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Key, Value Value) PopulateStrongValueWithWeakKey(
        ICache<Key, Value> cache
    )
    {
        Key key = new(7);
        Value value = new(key);
        cache.Put(key, value);
        WeakReference reference = new(key);
        value.References(key).Should().BeTrue();
        GC.KeepAlive(key);
        return (reference, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference First, WeakReference Second) RefreshWeakValue(
        ILoadingCache<Key, Value> cache,
        Key key
    )
    {
        Value first = cache.Get(key);
        WeakReference firstReference = new(first);
        Value second = cache.RefreshAsync(key).GetAwaiter().GetResult();
        WeakReference secondReference = new(second);
        cache.TryGet(key, out Value? current).Should().BeTrue();
        current.Should().BeSameAs(second);
        GC.KeepAlive(first);
        GC.KeepAlive(second);
        GC.KeepAlive(current);
        return (firstReference, secondReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<Value> Wait, WeakReference Key) StartWeakKeyLoad(
        IAsyncLoadingCache<Key, Value> cache,
        TaskCompletionSource<Value> release
    )
    {
        Key key = new(7);
        Task<Value> wait = cache.GetAsync(key).AsTask();
        WeakReference reference = new(key);
        GC.KeepAlive(release);
        GC.KeepAlive(key);
        return (wait, reference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Keys, WeakReference[] Values) PopulateWeakEntries(
        ICache<Key, Value> cache,
        int count
    )
    {
        var keys = new WeakReference[count];
        var values = new WeakReference[count];
        for (int index = 0; index < count; index++)
        {
            Key key = new(index);
            Value value = new();
            cache.Put(key, value);
            keys[index] = new WeakReference(key);
            values[index] = new WeakReference(value);
            GC.KeepAlive(key);
            GC.KeepAlive(value);
        }

        return (keys, values);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection(params WeakReference[] references)
    {
        for (
            int attempt = 0;
            attempt < 20 && references.Any(static reference => reference.IsAlive);
            attempt++
        )
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Yield();
        }
    }

    private sealed class Key(int number)
    {
        private readonly int _number = number;

        public override bool Equals(object? obj) => obj is Key other && other._number == _number;

        public override int GetHashCode() => _number;
    }

    private sealed class ThrowingKey
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException();

        public override int GetHashCode() => throw new InvalidOperationException();
    }

    private sealed class RejectingMaintenanceScheduler : IMaintenanceScheduler
    {
        public bool TrySchedule(Action callback) => false;
    }

    private sealed class NoopCacheEnginePolicy : ICacheEnginePolicy
    {
        public long Maximum => long.MaxValue;

        public long WeightedSize => 0;

        public int ResidentCount => 0;

        public void SetMaximum(long maximum, bool weighted) { }

        public IReadOnlyList<object> Snapshot(bool hottest, int limit) => Array.Empty<object>();

        public void OnAccess(object? entryToken) { }

        public void OnPublish(object? entryToken, long weight) { }

        public void OnRemove(object? entryToken) { }

        public void Clear() { }

        public bool CleanUp() => false;

        public ReadBufferStatistics GetReadBufferStatistics() => default;

        public void Dispose() { }
    }

    private sealed class Value
    {
        internal Value(Key? referencedKey = null)
        {
            _referencedKey = referencedKey;
        }

        private readonly Key? _referencedKey;

        internal bool References(Key key) => ReferenceEquals(_referencedKey, key);
    }
}
