using LoadingCache;

internal static class LoaderContractSmoke
{
    internal static async Task RunAsync()
    {
        using ILoadingCache<ConfigKey, Config> sync = CacheBuilder
            .Create<ConfigKey, Config>()
            .MaximumSize(16)
            .MaxConcurrentLoads(4)
            .BuildLoading(new SyncLoader());
        var key = new ConfigKey("tenant-a", "routing");
        Config first = sync.Get(key);
        Config updated = await sync.RefreshAsync(key).WaitAsync(TimeSpan.FromSeconds(10));
        Require(first.Version == 1 && updated.Version == 2, "sync reload receives old value");
        Require(sync.Get(key) == updated, "sync reload publishes updated value");

        await using IAsyncLoadingCache<ConfigKey, Config> asyncCache = CacheBuilder
            .Create<ConfigKey, Config>()
            .MaximumSize(16)
            .MaxConcurrentLoads(4)
            .RefreshAfterWrite(TimeSpan.FromMinutes(1))
            .BuildAsyncLoading(new AsyncLoader());
        first = await asyncCache.GetAsync(key);
        updated = await asyncCache.RefreshAsync(key).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Require(first.Version == 1 && updated.Version == 2, "async reload receives old value");
        Require(await asyncCache.GetAsync(key) == updated, "async reload publishes updated value");
        Require(
            asyncCache.Policy.RefreshAfterWrite?.Duration == TimeSpan.FromMinutes(1),
            "refresh policy is inspectable"
        );
        Console.WriteLine("Loader interface and old-value reload consumer smoke passed.");
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Loader consumer failed: {operation}");
        }
    }

    private readonly record struct ConfigKey(string Tenant, string Name);

    private sealed record Config(int Version);

    private sealed class SyncLoader : ISyncCacheLoader<ConfigKey, Config>
    {
        public Config Load(ConfigKey key) => new(1);

        public Config Reload(ConfigKey key, Config oldValue) => new(oldValue.Version + 1);
    }

    private sealed class AsyncLoader : IAsyncCacheLoader<ConfigKey, Config>
    {
        public async Task<Config> LoadAsync(ConfigKey key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new Config(1);
        }

        public async Task<Config> ReloadAsync(
            ConfigKey key,
            Config oldValue,
            CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return new Config(oldValue.Version + 1);
        }
    }
}
