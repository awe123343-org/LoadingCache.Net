using LoadingCache;

internal static class NativePolicySmoke
{
    internal static async Task RunAsync()
    {
        WeakReferenceSmoke();
        await BulkLoadingSmokeAsync();
        await ListenerSmokeAsync();
        using var manual = CacheBuilder
            .Create<string, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .MemoryPressureEviction(TimeSpan.FromMinutes(1))
            .Build();
        IDictionary<string, int> dictionary = manual.AsDictionary();
        Require(!dictionary.IsReadOnly, "mutable IDictionary contract");
        dictionary.Add("key", 1);
        var view = manual.AsDictionary();
        Require(view.TryUpdate("KEY", 2, 1), "dictionary conditional update");
        Require(manual.Policy.TryGetQuietly("key", out int value) && value == 2, "quiet lookup");
        manual.Policy.Eviction!.SetMaximum(8);
        Require(manual.Policy.Eviction.Maximum == 8, "runtime maximum");
        Require(manual.Policy.Eviction.Hottest(1).Count == 1, "ordered policy snapshot");
        Require(view.TryRemove("key", 2), "dictionary conditional remove");

        await using var asynchronous = CacheBuilder
            .Create<int, int>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .BuildAsync();
        var asyncView = asynchronous.AsDictionary();
        Require(
            await asyncView.GetOrAddAsync(1, static (key, _) => Task.FromResult(key)) == 1,
            "async dictionary factory"
        );
        Require(asyncView[1] == 1, "async materialized view");

        var disposed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var owned = OwnedCache.Create(
            new OwnedCacheOptions<string, MemoryStream>
            {
                MaximumSize = 4,
                MaximumActiveValues = 8,
            },
            stream =>
            {
                stream.Dispose();
                disposed.TrySetResult(true);
            }
        );
        using var lease = owned.PutAndLease("stream", new MemoryStream());
        owned.Invalidate("stream");
        Require(lease.Value.CanRead && !disposed.Task.IsCompleted, "lease survives eviction");
        lease.Dispose();
        Require(
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "automatic owned-value disposal"
        );
    }

    private static void WeakReferenceSmoke()
    {
        using var cache = CacheBuilder
            .Create<ReferenceToken, ReferenceToken>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .WeakKeys()
            .WeakValues()
            .Build();
        var key = new ReferenceToken(1);
        var value = new ReferenceToken(2);
        cache.Put(key, value);
        Require(
            cache.TryGet(key, out var found) && ReferenceEquals(found, value),
            "weak live lookup"
        );
        Require(!cache.TryGet(new ReferenceToken(1), out _), "weak key identity");
        Require(!cache.AsDictionary().TryRemove(key, new ReferenceToken(2)), "weak value identity");
        Require(cache.AsDictionary().TryRemove(key, value), "weak conditional removal");
        GC.KeepAlive(key);
        GC.KeepAlive(value);
    }

    private sealed record ReferenceToken(int Number);

    private static async Task ListenerSmokeAsync()
    {
        var removed = new TaskCompletionSource<RemovalNotification<int, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .RecordStatistics()
            .EnableMetrics("consumer-smoke")
            .NotificationCapacity(8)
            .RemovalListener(notification => removed.TrySetResult(notification))
            .Build();
        cache.Put(1, "one");
        cache.Invalidate(1);
        var notification = await removed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(
            notification.Cause == RemovalCause.Explicit && notification.Value == "one",
            "removal notification"
        );
    }

    private static async Task BulkLoadingSmokeAsync()
    {
        var syncLoader = new SyncBulkLoader();
        using var synchronous = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .RecordStatistics()
            .BuildLoading(syncLoader);
        var result = synchronous.GetAll([1, 2, 1]);
        Require(result.Count == 2 && result[1] == 10 && result[2] == 20, "sync bulk result");
        Require(
            syncLoader.Calls == 1 && synchronous.Statistics.LoadsStarted == 1,
            "one bulk backend call"
        );
        Require(synchronous.TryGet(99, out int prefetched) && prefetched == 990, "bulk prefetch");

        var asyncLoader = new AsyncBulkLoader();
        await using var asynchronous = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(1)
            .MaxPendingLoadKeys(4)
            .MaximumBulkKeys(4)
            .RecordStatistics()
            .BuildAsyncLoading(asyncLoader);
        var asyncResult = await asynchronous.GetAllAsync([1, 2]);
        Require(asyncResult.Count == 2 && asyncResult[2] == 20, "async bulk result");
        Require(
            asyncLoader.Calls == 1 && asynchronous.Statistics.LoadsStarted == 1,
            "one async bulk backend call"
        );
        Require(asynchronous.TryGet(99, out int extra) && extra == 990, "async prefetch");
    }

    private static Dictionary<int, int> CreateBulkResult(IReadOnlyCollection<int> keys)
    {
        var result = keys.ToDictionary(key => key, key => key * 10);
        result.Add(99, 990);
        return result;
    }

    private sealed class SyncBulkLoader : IBulkSyncCacheLoader<int, int>
    {
        internal int Calls { get; private set; }

        public int Load(int key) => key * 10;

        public IReadOnlyDictionary<int, int> LoadAll(IReadOnlyCollection<int> keys)
        {
            Calls++;
            return CreateBulkResult(keys);
        }
    }

    private sealed class AsyncBulkLoader : IBulkAsyncCacheLoader<int, int>
    {
        internal int Calls { get; private set; }

        public Task<int> LoadAsync(int key, CancellationToken cancellationToken) =>
            Task.FromResult(key * 10);

        public Task<IReadOnlyDictionary<int, int>> LoadAllAsync(
            IReadOnlyCollection<int> keys,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<int, int>>(CreateBulkResult(keys));
        }
    }

    private static void Require(bool condition, string operation)
    {
        if (!condition)
            throw new InvalidOperationException($"Native policy smoke failed: {operation}");
    }
}
