using System.Collections.Concurrent;
using LoadingCache.ReferenceStorage;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    /// <summary>
    /// The authoritative mapping facade.  Strong-key caches retain the existing
    /// BCL comparer and dictionary path.  Weak-key caches use a separate BCL
    /// dictionary keyed by an identity-hashed weak wrapper; the wrapper never
    /// stores the target strongly.
    /// </summary>
    private sealed class EntryStore
    {
        private readonly bool _weakKeys;
        private readonly ConcurrentDictionary<TKey, Entry>? _strong;

        // The weak-key dictionary is object-keyed so live callers can probe it
        // with the raw key reference.  Creating a ReferenceKey probe for every
        // hit allocates; the comparer understands both raw keys and the weak
        // wrappers stored in this dictionary.
        private readonly ConcurrentDictionary<object, Entry>? _weak;

        internal EntryStore(bool weakKeys, IEqualityComparer<TKey> comparer)
        {
            _weakKeys = weakKeys;
            if (weakKeys)
            {
                _weak = new ConcurrentDictionary<object, Entry>(
                    WeakKeyObjectComparer<TKey>.Instance
                );
            }
            else
            {
                _strong = new ConcurrentDictionary<TKey, Entry>(comparer);
            }
        }

        internal int Count => _weakKeys ? _weak!.Count : _strong!.Count;

        internal IEnumerable<Entry> Values => _weakKeys ? _weak!.Values : _strong!.Values;

        internal long CountMaterialized() =>
            _weakKeys ? CountMaterialized(_weak!) : CountMaterialized(_strong!);

        private static long CountMaterialized<TDictionaryKey>(
            ConcurrentDictionary<TDictionaryKey, Entry> entries
        )
            where TDictionaryKey : notnull
        {
            long count = 0;
            foreach (KeyValuePair<TDictionaryKey, Entry> pair in entries)
            {
                Entry entry = pair.Value;
                if (
                    Volatile.Read(ref entry.IsReady)
                    && !Volatile.Read(ref entry.PolicyDetached)
                    && entry.TryGetKey(out _)
                    && !entry.IsValueCollected
                )
                {
                    count++;
                }
            }

            return count;
        }

        internal bool TryGetValue(
            TKey key,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Entry? entry
        )
        {
            ArgumentNullException.ThrowIfNull(key);
            return !_weakKeys
                ? _strong!.TryGetValue(key, out entry)
                : _weak!.TryGetValue(key, out entry);
        }

        internal Entry this[TKey key]
        {
            set
            {
                ArgumentNullException.ThrowIfNull(key);
                if (!_weakKeys)
                {
                    _strong![key] = value;
                    return;
                }

                ReferenceKey<TKey> slot =
                    value.WeakKey
                    ?? throw new InvalidOperationException(
                        "A weak-key entry must carry its weak identity slot."
                    );
                _weak![slot] = value;
            }
        }

        internal bool TryRemoveExact(Entry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (_weakKeys)
            {
                ReferenceKey<TKey>? key = entry.WeakKey;
                if (key is not null)
                {
                    return ((ICollection<KeyValuePair<object, Entry>>)_weak!).Remove(
                        new KeyValuePair<object, Entry>(key, entry)
                    );
                }

                return false;
            }

            if (!entry.TryGetKey(out TKey? strongKey))
            {
                return false;
            }

            return ((ICollection<KeyValuePair<TKey, Entry>>)_strong!).Remove(
                new KeyValuePair<TKey, Entry>(strongKey, entry)
            );
        }

        internal bool IsCurrent(Entry entry)
        {
            if (_weakKeys)
            {
                return entry.WeakKey is not null
                    && _weak!.TryGetValue(entry.WeakKey, out Entry? current)
                    && ReferenceEquals(current, entry);
            }

            return entry.TryGetKey(out TKey? key)
                && _strong!.TryGetValue(key, out Entry? currentEntry)
                && ReferenceEquals(currentEntry, entry);
        }

        internal void Clear()
        {
            if (_weakKeys)
            {
                _weak!.Clear();
            }
            else
            {
                _strong!.Clear();
            }
        }

        internal IReadOnlyList<Entry> Snapshot() => [.. Values];
    }

    private readonly bool _weakKeys;
    private readonly bool _weakValues;

    private void CleanUpCollectedReferencesLocked()
    {
        if (!_weakKeys && !_weakValues)
        {
            return;
        }

        foreach (Entry entry in _entries.Snapshot())
        {
            if (
                (_weakKeys && !entry.TryGetKey(out _))
                || (_weakValues && Volatile.Read(ref entry.IsReady) && entry.IsValueCollected)
            )
            {
                RemoveCurrentEntryLocked(entry, collected: true);
            }
        }
    }
}
