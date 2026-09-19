using System.Diagnostics.CodeAnalysis;

namespace LoadingCache.ReferenceStorage;

/// <summary>
/// Stores a value through a typed weak target.
/// </summary>
/// <typeparam name="TValue">The non-null value type.</typeparam>
/// <remarks>
/// A weak holder does not promise prompt collection notification. The owning
/// cache must treat a failed <see cref="TryGetValue"/> as a miss and remove the
/// exact entry during lookup or maintenance.
/// </remarks>
internal sealed class ReferenceValue<TValue>
    where TValue : notnull
{
    private readonly WeakReference<object> _weakValue;

    private ReferenceValue(TValue value)
    {
        _weakValue = new WeakReference<object>(value);
    }

    /// <summary>
    /// Gets whether a weak value target has been collected.
    /// </summary>
    internal bool IsCollected => !_weakValue.TryGetTarget(out _);

    /// <summary>Creates a weak value holder.</summary>
    internal static ReferenceValue<TValue> Weak(TValue value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }
        return !typeof(TValue).IsValueType
            ? new ReferenceValue<TValue>(value)
            : throw new InvalidOperationException("Weak values require a reference type.");
    }

    /// <summary>
    /// Obtains a strong local reference when the value is still alive.
    /// </summary>
    internal bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        if (_weakValue.TryGetTarget(out object? target) && target is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
