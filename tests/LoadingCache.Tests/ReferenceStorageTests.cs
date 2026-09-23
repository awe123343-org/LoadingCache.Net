using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LoadingCache.ReferenceStorage;

namespace LoadingCache.Tests;

public sealed class ReferenceStorageTests
{
    [Test]
    public async Task WeakKeyCapturesStableRuntimeIdentityHashAndUsesReferenceIdentity()
    {
        Key first = new(7);
        Key equalByValue = new(7);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(first);
        await Assert.That(handle.IdentityHash).IsEqualTo(RuntimeHelpers.GetHashCode(first));
        await Assert.That(handle.Matches(first)).IsTrue();
        await Assert.That(handle.Matches(equalByValue)).IsFalse();
        await Assert.That(handle.TryGetTarget(out Key? target)).IsTrue();
        await Assert.That(ReferenceEquals(target, first)).IsTrue();
    }

    [Test]
    public async Task WeakKeyRejectsValueTypes()
    {
        Action create = () => ReferenceKey<int>.CreateWeak(42);
        await Assert.That(create).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WeakKeyCollisionDoesNotMergeDistinctTargets()
    {
        Key first = new(1);
        Key second = new(1);
        ReferenceKey<Key> firstHandle = ReferenceKey<Key>.CreateWeakForTesting(first, 123);
        ReferenceKey<Key> secondHandle = ReferenceKey<Key>.CreateWeakForTesting(second, 123);
        IEqualityComparer<ReferenceKey<Key>> comparer = ReferenceKeyComparer<Key>.Instance;
        await Assert
            .That(comparer.GetHashCode(firstHandle))
            .IsEqualTo(comparer.GetHashCode(secondHandle));
        await Assert.That(comparer.Equals(firstHandle, secondHandle)).IsFalse();
        await Assert.That(firstHandle.Matches(first)).IsTrue();
        await Assert.That(secondHandle.Matches(second)).IsTrue();
    }

    [Test]
    public async Task WeakKeyObjectComparerMatchesRawKeysByReferenceWithoutCallingOverrides()
    {
        ThrowingKey key = new();
        ThrowingKey equalByValue = new();
        ReferenceKey<ThrowingKey> handle = ReferenceKey<ThrowingKey>.CreateWeak(key);
        IEqualityComparer<object> comparer = WeakKeyObjectComparer<ThrowingKey>.Instance;
        await Assert.That(comparer.Equals(handle, key)).IsTrue();
        await Assert.That(comparer.Equals(key, handle)).IsTrue();
        await Assert.That(comparer.Equals(handle, equalByValue)).IsFalse();
        await Assert.That(comparer.GetHashCode(handle)).IsEqualTo(RuntimeHelpers.GetHashCode(key));
        await Assert.That(comparer.GetHashCode(key)).IsEqualTo(RuntimeHelpers.GetHashCode(key));
    }

    [Test]
    public Task WeakKeyObjectComparerDoesNotAllocateDuringRawLookupComparison() =>
        AllocationTestProcess.VerifyAsync("weak-comparer");

    [Test]
    public Task RawComparisonMeasurementDetectsAllocatingComparer() =>
        AllocationTestProcess.VerifyAsync("allocating-comparer");

    [Test]
    public async Task WeakKeyObjectComparerPreservesCollisionAndExactRemovalSemantics()
    {
        Key liveKey = new(2);
        int collisionHash = RuntimeHelpers.GetHashCode(liveKey);
        (ReferenceKey<Key> deadHandle, WeakReference deadKey) = CreateWeakKey(collisionHash);
        ReferenceKey<Key> liveHandle = ReferenceKey<Key>.CreateWeakForTesting(
            liveKey,
            collisionHash
        );
        ConcurrentDictionary<object, string> map = new(WeakKeyObjectComparer<Key>.Instance)
        {
            [deadHandle] = "dead",
            [liveHandle] = "live",
        };
        ForceCollection(deadKey);
        await Assert.That(map.TryGetValue(liveKey, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("live");
        await Assert
            .That(
                ((ICollection<KeyValuePair<object, string>>)map).Remove(
                    new KeyValuePair<object, string>(deadHandle, "dead")
                )
            )
            .IsTrue();
        await Assert.That(map.TryGetValue(liveKey, out value)).IsTrue();
        await Assert.That(value).IsEqualTo("live");
    }

    [Test]
    public async Task WeakKeyObjectComparerExactRemovalDoesNotRemoveReplacedEntry()
    {
        Key key = new(3);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        ConcurrentDictionary<object, object> map = new(WeakKeyObjectComparer<Key>.Instance);
        object oldGeneration = new();
        object newGeneration = new();
        map[handle] = oldGeneration;
        map[handle] = newGeneration;
        await Assert
            .That(
                ((ICollection<KeyValuePair<object, object>>)map).Remove(
                    new KeyValuePair<object, object>(handle, oldGeneration)
                )
            )
            .IsFalse();
        await Assert.That(map.TryGetValue(key, out object? current)).IsTrue();
        await Assert.That(ReferenceEquals(current, newGeneration)).IsTrue();
    }

    [Test]
    public async Task DeadWeakKeyCanBeRemovedByItsExactWrapper()
    {
        (ReferenceKey<Key> handle, WeakReference weakKey) = CreateWeakKey();
        ConcurrentDictionary<ReferenceKey<Key>, string> map = new(
            ReferenceKeyComparer<Key>.Instance
        )
        {
            [handle] = "old",
        };
        ForceCollection(weakKey);
        await Assert.That(handle.IsCollected).IsTrue();
        await Assert.That(map.TryRemove(handle, out string? removed)).IsTrue();
        await Assert.That(removed).IsEqualTo("old");
        await Assert.That(map).IsEmpty();
    }

    [Test]
    public async Task DeadWrapperDoesNotMatchAReplacementTargetWithTheSameForcedHash()
    {
        (ReferenceKey<Key> deadHandle, WeakReference deadKey) = CreateWeakKey(456);
        Key liveKey = new(2);
        ReferenceKey<Key> liveHandle = ReferenceKey<Key>.CreateWeakForTesting(liveKey, 456);
        ConcurrentDictionary<ReferenceKey<Key>, string> map = new(
            ReferenceKeyComparer<Key>.Instance
        )
        {
            [deadHandle] = "dead",
            [liveHandle] = "live",
        };
        ForceCollection(deadKey);
        ReferenceKey<Key> probe = ReferenceKey<Key>.CreateProbeForTesting(liveKey, 456);
        await Assert.That(map.TryGetValue(probe, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("live");
        await Assert.That(map.TryRemove(deadHandle, out string? removed)).IsTrue();
        await Assert.That(removed).IsEqualTo("dead");
        await Assert.That(map.TryGetValue(probe, out value)).IsTrue();
        await Assert.That(value).IsEqualTo("live");
    }

    [Test]
    public async Task ExactValueRemovalDoesNotRemoveAReplacedGeneration()
    {
        Key key = new(3);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        ConcurrentDictionary<ReferenceKey<Key>, object> map = new(
            ReferenceKeyComparer<Key>.Instance
        );
        object oldGeneration = new();
        object newGeneration = new();
        map[handle] = oldGeneration;
        map[handle] = newGeneration;
        ICollection<KeyValuePair<ReferenceKey<Key>, object>> entries = map;
        await Assert
            .That(
                entries.Remove(new KeyValuePair<ReferenceKey<Key>, object>(handle, oldGeneration))
            )
            .IsFalse();
        await Assert.That(ReferenceEquals(map[handle], newGeneration)).IsTrue();
    }

    [Test]
    public async Task WeakValueReturnsLiveTargetAndDoesNotStronglyRootIt()
    {
        (ReferenceValue<Value> holder, WeakReference weakValue) = CreateWeakValue();
        ForceCollection(weakValue);
        await Assert.That(holder.IsCollected).IsTrue();
        await Assert.That(holder.TryGetValue(out _)).IsFalse();
    }

    [Test]
    public async Task WeakValueRejectsValueTypes()
    {
        Action create = () => ReferenceValue<int>.Weak(42);
        await Assert.That(create).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WeakValueReturnsItsLiveValue()
    {
        Value value = new();
        ReferenceValue<Value> holder = ReferenceValue<Value>.Weak(value);
        await Assert.That(holder.IsCollected).IsFalse();
        await Assert.That(holder.TryGetValue(out Value? actual)).IsTrue();
        await Assert.That(ReferenceEquals(actual, value)).IsTrue();
        GC.KeepAlive(value);
    }

    [Test]
    public async Task WeakValueRejectsNull()
    {
        Action create = () => ReferenceValue<Value>.Weak(null!);
        await Assert.That(create).Throws<ArgumentNullException>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReferenceKey<Key> Handle, WeakReference Target) CreateWeakKey(
        int identityHash = 0
    )
    {
        Key key = new(1);
        ReferenceKey<Key> handle =
            identityHash == 0
                ? ReferenceKey<Key>.CreateWeak(key)
                : ReferenceKey<Key>.CreateWeakForTesting(key, identityHash);
        return (handle, new WeakReference(key));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ReferenceValue<Value> Holder, WeakReference Target) CreateWeakValue()
    {
        Value value = new();
        ReferenceValue<Value> holder = ReferenceValue<Value>.Weak(value);
        // The positive assertion needs a live owner. After this helper returns,
        // collection is legal at any point, including before an explicit GC.
        AssertLiveValue(holder);
        WeakReference reference = new(value);
        GC.KeepAlive(value);
        return (holder, reference);
    }

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertLiveValue(ReferenceValue<Value> holder)
    {
        if (!(holder.TryGetValue(out Value? liveValue)))
            Assert.Fail("Expected holder.TryGetValue(out Value? liveValue) to be true ().");
        if (!((liveValue) is not null))
            Assert.Fail("Expected liveValue to be non-null ().");
    }

    private sealed class Key
    {
        internal Key(int value)
        {
            Number = value;
        }

        private int Number { get; }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Number == other.Number;
        }

        public override int GetHashCode()
        {
            return Number;
        }
    }

    private sealed class ThrowingKey
    {
        public override bool Equals(object? obj) => throw new InvalidOperationException();

        public override int GetHashCode() => throw new InvalidOperationException();
    }

    private sealed class Value;
}
