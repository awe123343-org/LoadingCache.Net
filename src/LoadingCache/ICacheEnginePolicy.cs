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

    void OnAccess(object? entryToken);

    void OnPublish(object? entryToken, long weight);

    void OnRemove(object? entryToken);

    void Clear();

    bool CleanUp();

    ReadBufferStatistics GetReadBufferStatistics();

    void Dispose();
}
