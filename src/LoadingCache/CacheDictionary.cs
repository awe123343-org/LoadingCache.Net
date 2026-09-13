using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace LoadingCache;

/// <summary>
/// A mutable, cache-aware dictionary view over one cache engine.
/// </summary>
/// <remarks>
/// The view contains only ready, non-expired values. Pending loader flights are
/// intentionally absent from reads and snapshots; an <see cref="Add(TKey, TValue)"/>
/// operation treats a pending flight as occupied, while <see cref="Remove(TKey)"/>
/// leaves it untouched. The view owns no cache resources and does not block on
/// asynchronous flights.
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
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
        get
        {
            if (!TryGetValue(key, out TValue? value))
            {
                throw new KeyNotFoundException($"The key was not present: {key}.");
            }

            return value!;
        }
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
