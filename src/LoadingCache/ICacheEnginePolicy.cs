using LoadingCache.Maintenance;

namespace LoadingCache;

/// <summary>
/// Narrow seam between the shared data plane and policy metadata.  Policy
/// implementations receive exact entry tokens and may enqueue maintenance, but
/// never become authoritative for key/value visibility.
/// </summary>
internal interface ICacheEnginePolicy
{
    long Maximum { get; }

    long WeightedSize { get; }

    int ResidentCount { get; }

    void SetMaximum(long maximum, bool weighted);

    // Returns exact authoritative entry identities in approximate policy order.
    IReadOnlyList<object> Snapshot(bool hottest, int limit);

    void OnAccess(object? entryToken);

    void OnPublish(object? entryToken, long weight);

    void OnRemove(object? entryToken);

    // Only the mutation boundary outside engine locks requests a worker. Test
    // policies without buffered writes retain their synchronous contract.
    bool HasPendingWrites => false;

    // A queued batch needs one request; the drain owner re-arms the signal.
    bool TryRequestWriteMaintenance() => HasPendingWrites;

    void FlushWrites() { }

    WriteBufferStatistics GetWriteBufferStatistics() => default;

    void Clear();

    bool CleanUp();

    ReadBufferStatistics GetReadBufferStatistics();

    void Dispose();
}
