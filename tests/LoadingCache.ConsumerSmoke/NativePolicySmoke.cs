using LoadingCache;

internal static class NativePolicySmoke
{
    internal static async Task RunAsync()
    {
        ResidentPublicationSmoke(11, 22);
        ResidentPublicationSmoke(0x12345678abcdef01L, 0x23456789abcdef12L);
        ResidentPublicationSmoke(new ReferenceToken(1), new ReferenceToken(2));
        ResidentPublicationSmoke((11L, 22L), (33L, 44L));
        WeakReferenceSmoke();
        await BulkLoadingSmokeAsync();
        await ListenerSmokeAsync();
        await AtomicDictionarySmokeAsync();
        SynchronousEvictionSmoke();
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
        await using (var lease = owned.PutAndLease("stream", new MemoryStream()))
        {
            owned.Invalidate("stream");
            Require(lease.Value.CanRead && !disposed.Task.IsCompleted, "lease survives eviction");
        }
        Require(
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            "automatic owned-value disposal"
        );
    }

    private static void ResidentPublicationSmoke<TValue>(TValue first, TValue second)
        where TValue : notnull
    {
        using var cache = CacheBuilder
            .Create<int, TValue>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .Build();
        cache.Put(1, first);
        Require(
            cache.TryGet(1, out TValue? before)
                && EqualityComparer<TValue>.Default.Equals(before, first),
            "resident initial publication"
        );
        cache.Put(1, second);
        Require(
            cache.TryGet(1, out TValue? after)
                && EqualityComparer<TValue>.Default.Equals(after, second),
            "resident replacement publication"
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

    private static async Task AtomicDictionarySmokeAsync()
    {
        await using var cache = CacheBuilder
            .Create<string, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(4)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .BuildAsync();
        var view = cache.AsDictionary();
        Require(
            view.AddOrUpdate("count", static _ => 0, static (_, value) => value + 1) == 0,
            "atomic add accepts default value"
        );
        Require(
            view.AddOrUpdate("COUNT", static _ => 0, static (_, value) => value + 1) == 1,
            "atomic update uses comparer"
        );
        Require(view.Merge("count", 2, static (left, right) => left + right) == 3, "atomic merge");
        var kept = view.Compute(
            "count",
            static (_, current) =>
                current.HasValue ? CacheMutation.Keep<int>() : CacheMutation.Set(0)
        );
        Require(kept.Kind == CacheMutationKind.Keep && view["count"] == 3, "explicit keep");
        var removed = view.ComputeIfPresent("count", static (_, _) => CacheMutation.Remove<int>());
        Require(
            removed.Kind == CacheMutationKind.Remove && !view.ContainsKey("count"),
            "explicit removal without null sentinel"
        );

        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var pending = view.GetOrAddAsync("pending", (_, _) => completion.Task);
        Require(
            view.AddOrUpdate("pending", static _ => 7, static (_, value) => value + 1) == 7,
            "atomic update replaces pending flight without blocking"
        );
        completion.SetResult(2);
        Require(
            await pending == 2 && view["pending"] == 7,
            "late completion cannot replace atomic update"
        );
    }

    private static void SynchronousEvictionSmoke()
    {
        EvictionCounter counter = new();
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumWeight(1)
            .MaximumResidentCount(8)
            .MaxConcurrentLoads(4)
            .Weigher(static (_, _) => 2)
            .EvictionListener(counter.OnEviction)
            .Build();
        cache.Put(1, 1);
        cache.CleanUp();
        Require(counter.Calls == 1, "synchronous eviction delivered once before cleanup returns");
    }

    // Generated record equality uses the value component alongside reference identity checks.
    private sealed record ReferenceToken(
        // ReSharper disable once NotAccessedPositionalProperty.Local
        int Number
    );

    private sealed class EvictionCounter
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal void OnEviction(RemovalNotification<int, int> notification)
        {
            Require(notification.Cause == RemovalCause.Weight, "weighted eviction cause");
            Interlocked.Increment(ref _calls);
        }
    }

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
            notification is { Cause: RemovalCause.Explicit, Value: "one" },
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
