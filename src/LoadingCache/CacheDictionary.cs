using System.Collections;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;

namespace LoadingCache;

/// <summary>Describes the result requested by an atomic dictionary transform.</summary>
public enum CacheMutationKind
{
    /// <summary>Leave the current mapping unchanged.</summary>
    Keep,

    /// <summary>Store the value supplied by <see cref="CacheMutation{TValue}.Value"/>.</summary>
    Set,

    /// <summary>Remove the current mapping.</summary>
    Remove,
}

/// <summary>Factory methods for explicit cache dictionary mutations.</summary>
public static class CacheMutation
{
    /// <summary>Leaves a mapping unchanged.</summary>
    public static CacheMutation<TValue> Keep<TValue>()
        where TValue : notnull => new(CacheMutationKind.Keep);

    /// <summary>Stores <paramref name="value"/> as a mapping.</summary>
    public static CacheMutation<TValue> Set<TValue>(TValue value)
        where TValue : notnull => new(CacheMutationKind.Set, value);

    /// <summary>Removes a mapping.</summary>
    public static CacheMutation<TValue> Remove<TValue>()
        where TValue : notnull => new(CacheMutationKind.Remove);
}

/// <summary>Represents an explicit atomic update for a cache dictionary.</summary>
/// <typeparam name="TValue">The cache value type.</typeparam>
public readonly struct CacheMutation<TValue>
    where TValue : notnull
{
    private readonly TValue _value;

    internal CacheMutation(CacheMutationKind kind, TValue value = default!)
    {
        if (kind == CacheMutationKind.Set)
        {
            ArgumentNullException.ThrowIfNull(value);
        }

        Kind = kind;
        _value = value;
    }

    /// <summary>Gets the requested mutation kind.</summary>
    public CacheMutationKind Kind { get; }

    /// <summary>
    /// Gets the replacement value for a <see cref="CacheMutationKind.Set"/> mutation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mutation does not set a value.</exception>
    public TValue Value =>
        Kind == CacheMutationKind.Set
            ? _value
            : throw new InvalidOperationException("This cache mutation does not set a value.");
}

/// <summary>Factory methods for explicit present-or-missing cache values.</summary>
public static class CacheValue
{
    /// <summary>Returns a missing mapping.</summary>
    public static CacheValue<TValue> Missing<TValue>()
        where TValue : notnull => default;

    /// <summary>Returns a present mapping containing <paramref name="value"/>.</summary>
    public static CacheValue<TValue> Present<TValue>(TValue value)
        where TValue : notnull => new(value);
}

/// <summary>Represents an optional value supplied to an atomic dictionary transform.</summary>
/// <typeparam name="TValue">The cache value type.</typeparam>
[PublicAPI]
public readonly struct CacheValue<TValue>
    where TValue : notnull
{
    private readonly TValue _value;

    internal CacheValue(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        HasValue = true;
    }

    /// <summary>Gets whether this instance contains a mapping.</summary>
    public bool HasValue { get; }

    /// <summary>Gets the contained value.</summary>
    /// <exception cref="InvalidOperationException">This instance represents a missing mapping.</exception>
    public TValue Value =>
        HasValue ? _value : throw new InvalidOperationException("This cache value is missing.");

    /// <summary>Attempts to read the contained value.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = _value;
        return HasValue;
    }
}

/// <summary>
/// A mutable, cache-aware dictionary view over one cache engine.
/// </summary>
/// <remarks>
/// The view contains only ready, non-expired values. Pending loader flights are
/// intentionally absent from reads and snapshots; an <see cref="Add(TKey, TValue)"/>
/// operation treats a pending flight as occupied, while <see cref="Remove(TKey)"/>
/// leaves it untouched. The view owns no cache resources and does not block on
/// asynchronous flights. Atomic transforms treat a pending flight as a missing
/// value and fence it by exact entry identity; <see cref="ComputeIfPresent"/>
/// skips its callback while a flight is pending.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
[PublicAPI]
public class CacheDictionary<TKey, TValue>
    : IDictionary<TKey, TValue>,
        IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private protected readonly CacheEngine<TKey, TValue> Engine;

    internal CacheDictionary(CacheEngine<TKey, TValue> engine)
    {
        Engine = engine;
    }

    /// <summary>Gets the current number of ready, non-expired values.</summary>
    public int Count => Engine.DictionaryCount();

    /// <summary>Gets a snapshot of the current keys.</summary>
    public ICollection<TKey> Keys => Snapshot().Select(static pair => pair.Key).ToArray();

    /// <summary>Gets a snapshot of the current values.</summary>
    public ICollection<TValue> Values => Snapshot().Select(static pair => pair.Value).ToArray();

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    /// <summary>Gets or replaces the value for a key.</summary>
    public TValue this[TKey key]
    {
        get =>
            TryGetValue(key, out TValue? value)
                ? value!
                : throw new KeyNotFoundException($"The key was not present: {key}.");
        set
        {
            ValidateValue(value);
            Engine.DictionarySet(key, value);
        }
    }

    /// <summary>Gets a point-in-time snapshot enumerator.</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<TKey, TValue>>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Attempts to read a ready value without loading or refreshing it.</summary>
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        Engine.DictionaryTryGet(key, out value);

    /// <summary>Returns whether a ready value is present for a key.</summary>
    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    /// <summary>Atomically adds a value when the key is not occupied.</summary>
    public bool TryAdd(TKey key, TValue value) => Engine.DictionaryTryAdd(key, value);

    /// <summary>
    /// Atomically replaces a value when the current value equals
    /// <paramref name="comparisonValue"/>.
    /// </summary>
    public bool TryUpdate(TKey key, TValue value, TValue comparisonValue) =>
        Engine.DictionaryTryUpdate(key, value, comparisonValue);

    /// <summary>Atomically removes a ready value and returns it.</summary>
    public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        Engine.DictionaryTryRemove(key, out value);

    /// <summary>Atomically removes a value when it equals the comparison value.</summary>
    public bool TryRemove(TKey key, TValue comparisonValue) =>
        Engine.DictionaryTryRemove(key, comparisonValue);

    /// <summary>Adds a value and throws when the key is occupied.</summary>
    public void Add(TKey key, TValue value)
    {
        if (!TryAdd(key, value))
        {
            throw new ArgumentException("An item with the same key already exists.", nameof(key));
        }
    }

    /// <summary>Removes a ready value for a key.</summary>
    public bool Remove(TKey key) => Engine.DictionaryTryRemove(key, out _);

    /// <summary>
    /// Adds a value when absent or computes a replacement from the current value.
    /// Factories run outside cache locks and may be invoked more than once when
    /// concurrent mutations race, matching .NET concurrent dictionary semantics.
    /// </summary>
    public TValue AddOrUpdate(
        TKey key,
        Func<TKey, TValue> addValueFactory,
        Func<TKey, TValue, TValue> updateValueFactory
    )
    {
        ArgumentNullException.ThrowIfNull(addValueFactory);
        ArgumentNullException.ThrowIfNull(updateValueFactory);
        return Engine.DictionaryAddOrUpdate(key, addValueFactory, updateValueFactory);
    }

    /// <summary>
    /// Computes a value from an explicit present-or-missing state. Returning
    /// <c>CacheMutation.Remove&lt;TValue&gt;()</c> removes the mapping without
    /// using <see langword="null"/> as a deletion sentinel. A missing snapshot
    /// is fenced against structural removals and explicit invalidation intent.
    /// Unrelated changes may cause the callback to run again; changes to an
    /// existing value need not do so.
    /// </summary>
    public CacheMutation<TValue> Compute(
        TKey key,
        Func<TKey, CacheValue<TValue>, CacheMutation<TValue>> transform
    )
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Engine.DictionaryCompute(key, transform);
    }

    /// <summary>
    /// Computes a value only when the key is present. The callback is not invoked
    /// for a missing key; the returned <see cref="CacheMutationKind.Keep"/> then
    /// indicates that no mapping was changed.
    /// </summary>
    public CacheMutation<TValue> ComputeIfPresent(
        TKey key,
        Func<TKey, TValue, CacheMutation<TValue>> transform
    )
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Engine.DictionaryComputeIfPresent(key, transform);
    }

    /// <summary>
    /// Adds <paramref name="value"/> when absent or combines it with the current
    /// value. The merge callback runs outside cache locks and may be retried.
    /// </summary>
    public TValue Merge(TKey key, TValue value, Func<TValue, TValue, TValue> mergeFactory)
    {
        ValidateValue(value);
        ArgumentNullException.ThrowIfNull(mergeFactory);
        return Engine.DictionaryMerge(key, value, mergeFactory);
    }

    /// <summary>Removes all current cache entries, including pending flights.</summary>
    public void Clear() => Engine.Clear();

    /// <summary>Adds an exact key/value pair and throws when it is already present.</summary>
    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    /// <summary>Tests whether an exact key/value pair is present.</summary>
    public bool Contains(KeyValuePair<TKey, TValue> item) =>
        Engine.DictionaryContains(item.Key, item.Value);

    /// <summary>Removes an exact key/value pair atomically.</summary>
    public bool Remove(KeyValuePair<TKey, TValue> item) =>
        Engine.DictionaryTryRemove(item.Key, item.Value);

    /// <summary>Copies a snapshot into an array.</summary>
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

        KeyValuePair<TKey, TValue>[] snapshot = Snapshot();
        if (array.Length - arrayIndex < snapshot.Length)
        {
            throw new ArgumentException("The destination array is too small.", nameof(array));
        }

        snapshot.CopyTo(array, arrayIndex);
    }

    /// <summary>Captures the ready, non-expired entries visible to this view.</summary>
    protected KeyValuePair<TKey, TValue>[] Snapshot() => Engine.DictionarySnapshot();

    private static void ValidateValue(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
    }
}

/// <summary>A synchronous cache dictionary view with an atomic value factory.</summary>
[PublicAPI]
public sealed class SyncCacheDictionary<TKey, TValue> : CacheDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal SyncCacheDictionary(CacheEngine<TKey, TValue> engine)
        : base(engine) { }

    /// <summary>Gets an existing value or computes one with the shared flight.</summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return Engine.DictionaryGetOrAdd(key, valueFactory);
    }
}

/// <summary>An asynchronous cache dictionary view with an atomic async factory.</summary>
public sealed class AsyncCacheDictionary<TKey, TValue> : CacheDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal AsyncCacheDictionary(CacheEngine<TKey, TValue> engine)
        : base(engine) { }

    /// <summary>
    /// Gets an existing value or joins/starts the shared asynchronous flight.
    /// </summary>
    public ValueTask<TValue> GetOrAddAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> valueFactory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return Engine.GetOrAddAsync(key, valueFactory, cancellationToken);
    }
}
