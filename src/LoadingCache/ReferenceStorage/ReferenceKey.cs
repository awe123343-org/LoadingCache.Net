using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LoadingCache.ReferenceStorage;

/// <summary>
/// An identity-bearing key handle for a weak-key mapping.
/// </summary>
/// <typeparam name="TKey">The reference type used as the key.</typeparam>
/// <remarks>
/// A weak handle never derives its hash from a target after the target has been
/// collected. The owning mapping must use <see cref="ReferenceKeyComparer{TKey}"/>
/// and remove dead handles during its normal cleanup path.
/// </remarks>
internal sealed class ReferenceKey<TKey>
    where TKey : notnull
{
    private readonly TKey _strongTarget;
    private readonly WeakReference<object>? _weakTarget;

    private ReferenceKey(TKey target, bool weak, int identityHash)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (weak && typeof(TKey).IsValueType)
        {
            throw new ArgumentException("Weak keys require a reference type.", nameof(target));
        }

        IdentityHash = identityHash;
        if (weak)
        {
            _weakTarget = new WeakReference<object>(target);
            _strongTarget = default!;
        }
        else
        {
            _strongTarget = target;
        }
    }

    /// <summary>
    /// Gets the stable identity hash captured when this handle was created.
    /// </summary>
    internal int IdentityHash { get; }

    /// <summary>
    /// Gets whether this handle stores its target weakly.
    /// </summary>
    private bool IsWeak => _weakTarget is not null;

    /// <summary>
    /// Gets whether a weak target has been collected.
    /// </summary>
    internal bool IsCollected => IsWeak && !_weakTarget!.TryGetTarget(out _);

    /// <summary>
    /// Creates a weak identity handle and captures the CLR identity hash once.
    /// </summary>
    internal static ReferenceKey<TKey> CreateWeak(TKey key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        EnsureReferenceType();

        object target = key;
        return new ReferenceKey<TKey>(key, weak: true, RuntimeHelpers.GetHashCode(target));
    }

    /// <summary>
    /// Creates an ephemeral strong probe for a live lookup. The probe is never
    /// inserted into the mapping and only keeps the caller's target alive for
    /// the dictionary operation.
    /// </summary>
    internal static ReferenceKey<TKey> CreateProbe(TKey key)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        EnsureReferenceType();
        object target = key;
        return new ReferenceKey<TKey>(key, weak: false, RuntimeHelpers.GetHashCode(target));
    }

    /// <summary>
    /// Creates a weak handle with a controlled hash for collision tests.
    /// </summary>
    /// <remarks>
    /// This is a test seam only. Production callers must use <see cref="CreateWeak"/>.
    /// </remarks>
    internal static ReferenceKey<TKey> CreateWeakForTesting(TKey key, int identityHash)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        EnsureReferenceType();
        return new ReferenceKey<TKey>(key, weak: true, identityHash);
    }

    /// <summary>
    /// Creates a probe with a controlled hash for collision tests.
    /// </summary>
    /// <remarks>This is a test seam only.</remarks>
    internal static ReferenceKey<TKey> CreateProbeForTesting(TKey key, int identityHash)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        EnsureReferenceType();
        return new ReferenceKey<TKey>(key, weak: false, identityHash);
    }

    /// <summary>
    /// Attempts to obtain a strong local reference to the target.
    /// </summary>
    internal bool TryGetTarget([MaybeNullWhen(false)] out TKey target)
    {
        if (!IsWeak)
        {
            target = _strongTarget;
            return true;
        }

        if (_weakTarget!.TryGetTarget(out object? value) && value is TKey typed)
        {
            target = typed;
            return true;
        }

        target = default!;
        return false;
    }

    /// <summary>
    /// Tests whether a live candidate is the same object as this key target.
    /// </summary>
    internal bool Matches(TKey candidate)
    {
        if (candidate is null)
        {
            throw new ArgumentNullException(nameof(candidate));
        }

        return TryGetTarget(out TKey? target) && ReferenceEquals(target, candidate);
    }

    /// <summary>
    /// Tests exact wrapper identity or live target identity.
    /// </summary>
    internal bool IsSameIdentity(ReferenceKey<TKey> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (IdentityHash != other.IdentityHash)
        {
            return false;
        }

        if (!TryGetTarget(out TKey? left) || !other.TryGetTarget(out TKey? right))
        {
            return false;
        }

        return ReferenceEquals(left, right);
    }

    private static void EnsureReferenceType()
    {
        if (typeof(TKey).IsValueType)
        {
            throw new InvalidOperationException("Weak keys require a reference type.");
        }
    }
}

/// <summary>
/// Reference-identity comparer for <see cref="ReferenceKey{TKey}"/> wrappers.
/// </summary>
internal sealed class ReferenceKeyComparer<TKey> : IEqualityComparer<ReferenceKey<TKey>>
    where TKey : notnull
{
    internal static ReferenceKeyComparer<TKey> Instance { get; } = new();

    public bool Equals(ReferenceKey<TKey>? x, ReferenceKey<TKey>? y)
    {
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        return x.IsSameIdentity(y);
    }

    public int GetHashCode(ReferenceKey<TKey> obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.IdentityHash;
    }
}

/// <summary>
/// Compares raw live keys with the weak wrappers stored by a weak-key entry
/// store without invoking a key's virtual equality or hash-code implementation.
/// </summary>
/// <typeparam name="TKey">The reference type used as the key.</typeparam>
internal sealed class WeakKeyObjectComparer<TKey> : IEqualityComparer<object>
    where TKey : notnull
{
    internal static WeakKeyObjectComparer<TKey> Instance { get; } = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        if (GetHashCode(x) != GetHashCode(y))
        {
            return false;
        }

        if (x is ReferenceKey<TKey> xReference)
        {
            return y is ReferenceKey<TKey> yReference
                ? xReference.IsSameIdentity(yReference)
                : y is TKey yKey && xReference.Matches(yKey);
        }

        if (y is ReferenceKey<TKey> yReferenceOnly)
        {
            return x is TKey xKey && yReferenceOnly.Matches(xKey);
        }

        return ReferenceEquals(x, y);
    }

    public int GetHashCode(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj is ReferenceKey<TKey> reference
            ? reference.IdentityHash
            : RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>Reference-identity comparer for public key snapshots in weak-key mode.</summary>
internal sealed class ReferenceIdentityComparer<TKey> : IEqualityComparer<TKey>
    where TKey : notnull
{
    internal static ReferenceIdentityComparer<TKey> Instance { get; } = new();

    public bool Equals(TKey? x, TKey? y) => ReferenceEquals(x, y);

    public int GetHashCode(TKey obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return RuntimeHelpers.GetHashCode(obj);
    }
}
