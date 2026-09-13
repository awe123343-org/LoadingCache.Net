using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.ReferenceStorage;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ReferenceStorageTests
{
    [Test]
    public void WeakKeyCapturesStableRuntimeIdentityHashAndUsesReferenceIdentity()
    {
        Key first = new(7);
        Key equalByValue = new(7);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(first);

        handle.IdentityHash.Should().Be(RuntimeHelpers.GetHashCode(first));
        handle.Matches(first).Should().BeTrue();
        handle.Matches(equalByValue).Should().BeFalse();
        handle.TryGetTarget(out Key? target).Should().BeTrue();
        target.Should().BeSameAs(first);
    }

    [Test]
    public void WeakKeyRejectsValueTypes()
    {
        Action create = () => ReferenceKey<int>.CreateWeak(42);

        create.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void WeakKeyCollisionDoesNotMergeDistinctTargets()
    {
        Key first = new(1);
        Key second = new(1);
        ReferenceKey<Key> firstHandle = ReferenceKey<Key>.CreateWeakForTesting(first, 123);
        ReferenceKey<Key> secondHandle = ReferenceKey<Key>.CreateWeakForTesting(second, 123);
        IEqualityComparer<ReferenceKey<Key>> comparer = ReferenceKeyComparer<Key>.Instance;

        comparer.GetHashCode(firstHandle).Should().Be(comparer.GetHashCode(secondHandle));
        comparer.Equals(firstHandle, secondHandle).Should().BeFalse();
        firstHandle.Matches(first).Should().BeTrue();
        secondHandle.Matches(second).Should().BeTrue();
    }

    [Test]
    public void WeakKeyObjectComparerMatchesRawKeysByReferenceWithoutCallingOverrides()
    {
        ThrowingKey key = new();
        ThrowingKey equalByValue = new();
        ReferenceKey<ThrowingKey> handle = ReferenceKey<ThrowingKey>.CreateWeak(key);
        IEqualityComparer<object> comparer = WeakKeyObjectComparer<ThrowingKey>.Instance;

        comparer.Equals(handle, key).Should().BeTrue();
        comparer.Equals(key, handle).Should().BeTrue();
        comparer.Equals(handle, equalByValue).Should().BeFalse();
        comparer.GetHashCode(handle).Should().Be(RuntimeHelpers.GetHashCode(key));
        comparer.GetHashCode(key).Should().Be(RuntimeHelpers.GetHashCode(key));
    }

    [Test]
    public void WeakKeyObjectComparerDoesNotAllocateDuringRawLookupComparison()
    {
        Key key = new(1);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        IEqualityComparer<object> comparer = WeakKeyObjectComparer<Key>.Instance;

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allMatches = true;
        for (int index = 0; index < 10_000; index++)
        {
            allMatches &= comparer.Equals(handle, key);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allMatches.Should().BeTrue();
        allocated.Should().Be(0);
    }

    [Test]
    public void WeakKeyObjectComparerPreservesCollisionAndExactRemovalSemantics()
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

        map.TryGetValue(liveKey, out string? value).Should().BeTrue();
        value.Should().Be("live");
        ((ICollection<KeyValuePair<object, string>>)map)
            .Remove(new KeyValuePair<object, string>(deadHandle, "dead"))
            .Should()
            .BeTrue();
        map.TryGetValue(liveKey, out value).Should().BeTrue();
        value.Should().Be("live");
    }

    [Test]
    public void WeakKeyObjectComparerExactRemovalDoesNotRemoveReplacedEntry()
    {
        Key key = new(3);
        ReferenceKey<Key> handle = ReferenceKey<Key>.CreateWeak(key);
        ConcurrentDictionary<object, object> map = new(WeakKeyObjectComparer<Key>.Instance);
        object oldGeneration = new();
        object newGeneration = new();
        map[handle] = oldGeneration;
        map[handle] = newGeneration;

        ((ICollection<KeyValuePair<object, object>>)map)
            .Remove(new KeyValuePair<object, object>(handle, oldGeneration))
            .Should()
            .BeFalse();
        map.TryGetValue(key, out object? current).Should().BeTrue();
        current.Should().BeSameAs(newGeneration);
    }

    [Test]
    public void DeadWeakKeyCanBeRemovedByItsExactWrapper()
    {
        (ReferenceKey<Key> handle, WeakReference weakKey) = CreateWeakKey();
        ConcurrentDictionary<ReferenceKey<Key>, string> map = new(
            ReferenceKeyComparer<Key>.Instance
        )
        {
            [handle] = "old",
        };

        ForceCollection(weakKey);

        handle.IsCollected.Should().BeTrue();
        map.TryRemove(handle, out string? removed).Should().BeTrue();
        removed.Should().Be("old");
        map.Should().BeEmpty();
    }

    [Test]
    public void DeadWrapperDoesNotMatchAReplacementTargetWithTheSameForcedHash()
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
        map.TryGetValue(probe, out string? value).Should().BeTrue();
        value.Should().Be("live");
        map.TryRemove(deadHandle, out string? removed).Should().BeTrue();
        removed.Should().Be("dead");
        map.TryGetValue(probe, out value).Should().BeTrue();
        value.Should().Be("live");
    }

    [Test]
    public void ExactValueRemovalDoesNotRemoveAReplacedGeneration()
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
        entries
            .Remove(new KeyValuePair<ReferenceKey<Key>, object>(handle, oldGeneration))
            .Should()
            .BeFalse();

        map[handle].Should().BeSameAs(newGeneration);
    }

    [Test]
    public void WeakValueReturnsLiveTargetAndDoesNotStronglyRootIt()
    {
        (ReferenceValue<Value> holder, WeakReference weakValue) = CreateWeakValue();

        ForceCollection(weakValue);

        holder.IsCollected.Should().BeTrue();
        holder.TryGetValue(out _).Should().BeFalse();
    }

    [Test]
    public void WeakValueRejectsValueTypes()
    {
        Action create = () => ReferenceValue<int>.Weak(42);

        create.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void StrongValueAlwaysReturnsItsValue()
    {
        Value value = new();
        ReferenceValue<Value> holder = ReferenceValue<Value>.Strong(value);

        holder.IsWeak.Should().BeFalse();
        holder.IsCollected.Should().BeFalse();
        holder.TryGetValue(out Value? actual).Should().BeTrue();
        actual.Should().BeSameAs(value);
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
        holder.TryGetValue(out Value? liveValue).Should().BeTrue();
        liveValue.Should().NotBeNull();
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
