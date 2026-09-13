namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private void RetireOwnedValuesLocked()
    {
        if (_onValueRetired is null)
        {
            return;
        }

        foreach (Entry entry in _entries.Values)
        {
            if (Volatile.Read(ref entry.IsReady))
            {
                _onValueRetired(entry.Value);
            }
        }
    }
}
