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

    // The engine owns the same coordination monitor as the built-in policy. Custom policies use
    // the default forwarding method and retain their existing OnPublish contract.
    void OnPublishLocked(object? entryToken, long weight) => OnPublish(entryToken, weight);

    void OnRemove(object? entryToken);

    // See OnPublishLocked. The suffix documents caller-owned monitor ownership.
    void OnRemoveLocked(object? entryToken) => OnRemove(entryToken);

    // Only the mutation boundary outside engine locks requests a worker. Test
    // policies without buffered writes retain their synchronous contract.
    bool HasPendingWrites => false;

    // Only an initial, non-urgent tail may wait for the engine's one-shot backstop.
    // An active owner continues to service every reliable pending write.
    bool ShouldDeferWriteMaintenance => false;

    // A queued batch needs one request; the drain owner re-arms the signal.
    bool TryRequestWriteMaintenance() => HasPendingWrites;

    // A rejected scheduler may leave best-effort read work queued after the bounded fallback. The
    // next full read buffer must be allowed to request a new coordinator owner.
    void ResetReadMaintenanceSignalAfterFallback() { }

    // Explicit and rejected-scheduler cleanup consume every published head within the existing
    // coordinator budget. The request survives a running owner until no readable work remains.
    void RequestReadDrain() { }

    void FlushWrites() { }

    WriteBufferStatistics GetWriteBufferStatistics() => default;

    void Clear();

    bool CleanUp();

    ReadBufferStatistics GetReadBufferStatistics();

    void Dispose();
}
