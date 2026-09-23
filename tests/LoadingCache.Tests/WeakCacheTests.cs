using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class WeakCacheTests
{
    [Test]
    public async Task WeakKeysUseReferenceIdentityInsteadOfKeyEquality()
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
        await Assert.That(cache.TryGet(first, out Value? firstValue)).IsTrue();
        Assert.NotNull(firstValue);
        await Assert.That(cache.TryGet(equalByValue, out _)).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
    }

    [Test]
    public Task WeakKeyResidentHitDoesNotAllocateLookupProbe() =>
        AllocationTestProcess.VerifyAsync("weak-hit");

    [Test]
    public async Task WeakKeyLookupDoesNotInvokeKeyEqualityOrHashCodeOverrides()
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
        await Assert.That(cache.TryGet(key, out Value? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, value)).IsTrue();
    }

    [Test]
    public async Task ReplacedWeakKeyEntryCanBeCollectedAfterLookupAndCleanup()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        WeakReference key = PopulateReplacedWeakKey(cache);
        ForceCollection(key);
        await Assert.That(key.IsAlive).IsFalse();
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task WeakKeyIsCollectedAndRemovedByCleanup()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        WeakReference key = PopulateWeakKey(cache);
        ForceCollection(key);
        await Assert.That(key.IsAlive).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task WeakValuesDoNotKeepAValueAlive()
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
        await Assert.That(value.IsAlive).IsFalse();
        await Assert.That(cache.TryGet(key, out _)).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task WeakValuesWorkWithSynchronousLoadingCaches()
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
        await Assert.That(value.IsAlive).IsFalse();
        await Assert.That(cache.TryGet(key, out _)).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
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
        Assert.NotNull((await wait));
        ForceCollection(key);
        await Assert.That(key.IsAlive).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        cache.CleanUp();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task CollectedWeakValueCannotRemoveItsSameKeyReplacement()
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
        await Assert.That(oldReference.IsAlive).IsFalse();
        await Assert.That(cache.TryGet(key, out _)).IsFalse();
        Value replacement = new();
        cache.Put(key, replacement);
        cache.CleanUp();
        await Assert.That(cache.TryGet(key, out Value? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExpiredWeakValueCanBeReplacedWithoutLeavingAnOldGeneration()
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
        await Assert.That(cache.TryGet(key, out _)).IsFalse();
        ForceCollection(oldReference);
        await Assert.That(oldReference.IsAlive).IsFalse();
        Value replacement = new();
        cache.Put(key, replacement);
        cache.CleanUp();
        await Assert.That(cache.TryGet(key, out Value? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
    }

    [Test]
    public async Task StrongValueCanKeepAWeakKeyAliveThroughItsOwnObjectGraph()
    {
        using ICache<Key, Value> cache = CacheBuilder
            .Create<Key, Value>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .WeakKeys()
            .Build();
        (WeakReference keyReference, Value value) = PopulateStrongValueWithWeakKey(cache);
        ForceCollection(keyReference);
        await Assert.That(keyReference.IsAlive).IsTrue();
        GC.KeepAlive(value);
        GC.KeepAlive(cache);
    }

    [Test]
    public async Task RepeatedWeakCollectionDoesNotLeaveGhostPolicyEntries()
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
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
        Assert.NotNull(cache.Policy.Eviction);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(0);
        await Assert.That(cache.Policy.Eviction.Hottest(64)).IsEmpty();
        await Assert.That(cache.Policy.Eviction.Coldest(64)).IsEmpty();
    }

    [Test]
    public async Task LiveWeakLookupAndDictionarySnapshotNeverExposeCollectedTargets()
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
        await Assert.That(cache.TryGet(liveKey, out Value? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, liveValue)).IsTrue();
        KeyValuePair<Key, Value>[] snapshot = [.. cache.AsDictionary()];
        await Assert
            .That(snapshot)
            .HasSingleItem(pair =>
                ReferenceEquals(pair.Key, liveKey) && ReferenceEquals(pair.Value, liveValue)
            );
        await Assert.That(snapshot).DoesNotContain(pair => pair.Key == null || pair.Value == null);
        await Assert.That(cache.AsDictionary().Count).IsEqualTo(1);
    }

    [Test]
    public async Task SynchronousRefreshPublishesAWeakValue()
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
        await Assert.That(calls).IsEqualTo(2);
        ForceCollection(firstReference, secondReference);
        await Assert.That(cache.TryGet(key, out _)).IsFalse();
        await Assert.That(cache.EstimatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task WeakKeysRejectAnExplicitComparer()
    {
        Action build = () =>
            CacheBuilder
                .Create<Key, Value>()
                .MaximumSize(8)
                .MaxConcurrentLoads(1)
                .Comparer(EqualityComparer<Key>.Default)
                .WeakKeys()
                .Build();
        await Assert
            .That(build)
            .Throws<InvalidOperationException>()
            .WithMessageContaining("identity");
    }

    [Test]
    public async Task WeakValuesRejectAsynchronousCachePersonalities()
    {
        Action build = () =>
            CacheBuilder
                .Create<Key, Value>()
                .MaximumSize(8)
                .MaxConcurrentLoads(1)
                .WeakValues()
                .BuildAsync();
        await Assert
            .That(build)
            .Throws<InvalidOperationException>()
            .WithMessageContaining("asynchronous");
    }

    [Test]
    public async Task WeakReferenceModesRejectValueTypeArguments()
    {
        Action weakKey = () =>
            CacheBuilder.Create<int, Value>().MaximumSize(8).MaxConcurrentLoads(1).WeakKeys();
        Action weakValue = () =>
            CacheBuilder.Create<Key, int>().MaximumSize(8).MaxConcurrentLoads(1).WeakValues();
        await Assert.That(weakKey).Throws<InvalidOperationException>();
        await Assert.That(weakValue).Throws<InvalidOperationException>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateWeakKey(ICache<Key, Value> cache)
    {
        Key key = new(7);
        cache.Put(key, new Value());
        WeakReference reference = new(key);
        if (!(cache.TryGet(key, out Value? value)))
            Assert.Fail("Expected cache.TryGet(key, out Value? value) to be true ().");
        if (!((value) is not null))
            Assert.Fail("Expected value to be non-null ().");
        if ((cache.EstimatedCount) != (1))
            Assert.Fail("Expected cache.EstimatedCount to equal (1).");
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
        if (!(cache.TryGet(key, out Value? value)))
            Assert.Fail("Expected cache.TryGet(key, out Value? value) to be true ().");
        if (!((value) is not null))
            Assert.Fail("Expected value to be non-null ().");
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
        if (!(cache.TryGet(key, out Value? liveValue)))
            Assert.Fail("Expected cache.TryGet(key, out Value? liveValue) to be true ().");
        if (!(ReferenceEquals(liveValue, value)))
            Assert.Fail("Expected liveValue to reference (value).");
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
        if (!(cache.TryGet(key, out Value? current)))
            Assert.Fail("Expected cache.TryGet(key, out Value? current) to be true ().");
        if (!(ReferenceEquals(current, value)))
            Assert.Fail("Expected current to reference (value).");
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
        if (!(value.References(key)))
            Assert.Fail("Expected value.References(key) to be true ().");
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
        if (!(cache.TryGet(key, out Value? current)))
            Assert.Fail("Expected cache.TryGet(key, out Value? current) to be true ().");
        if (!(ReferenceEquals(current, second)))
            Assert.Fail("Expected current to reference (second).");
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
