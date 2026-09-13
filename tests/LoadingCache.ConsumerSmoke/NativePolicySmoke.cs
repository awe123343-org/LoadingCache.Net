using LoadingCache;

internal static class NativePolicySmoke
{
    internal static async Task RunAsync()
    {
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

    private static void Require(bool condition, string operation)
    {
        if (!condition)
            throw new InvalidOperationException($"Native policy smoke failed: {operation}");
    }
}
