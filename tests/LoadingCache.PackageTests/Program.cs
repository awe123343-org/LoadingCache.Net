using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LoadingCache;

Console.WriteLine(
    $"Consumer: {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}; dynamic code: {RuntimeFeature.IsDynamicCodeSupported}"
);
if (args.Contains("--expect-native", StringComparer.Ordinal))
{
    Require(!RuntimeFeature.IsDynamicCodeSupported, "Native AOT execution");
}

using (var manual = CacheBuilder.Create<int, int>().MaximumSize(4).MaxConcurrentLoads(2).Build())
{
    Require(manual.GetOrAdd(0, static key => key) == 0, "manual default value");
    manual.PutAll([new KeyValuePair<int, int>(1, 10), new KeyValuePair<int, int>(2, 20)]);
    Require(manual.GetAllPresent([0, 1, 2]).Count == 3, "bulk present lookup");
    Require(manual.Invalidate([0, 1]) == 2, "bulk invalidation");
    manual.CleanUp();
}

using (
    var loading = CacheBuilder
        .Create<string, string>()
        .MaximumSize(8)
        .MaxConcurrentLoads(2)
        .Comparer(StringComparer.OrdinalIgnoreCase)
        .BuildLoading(static key => key.ToUpperInvariant())
)
{
    Require(loading.Get("alpha") == "ALPHA", "synchronous loading");
    Require(loading.GetAll(["alpha", "ALPHA", "beta"]).Count == 2, "comparer-equal bulk keys");
}

await using (
    var manualAsync = CacheBuilder
        .Create<int, int>()
        .MaximumSize(4)
        .MaxConcurrentLoads(2)
        .BuildAsync()
)
{
    var completion = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    manualAsync.Put(1, completion.Task);
    Require(manualAsync.TryGetTask(1, out var flight), "pending task lookup");
    completion.SetResult(42);
    Require(await flight! == 42, "manually supplied task result");
    Require(
        await manualAsync.GetOrAddAsync(
            1,
            static (_, _) => throw new InvalidOperationException("Unexpected load")
        ) == 42,
        "manual async hit"
    );
}

int calls = 0;
var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (
    var loadingAsync = CacheBuilder
        .Create<string, string>()
        .MaximumSize(4)
        .MaxConcurrentLoads(2)
        .RecordStatistics()
        .BuildAsyncLoading(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return gate.Task;
            }
        )
)
{
    using var cancellation = new CancellationTokenSource();
    var canceled = loadingAsync.GetAsync("key", cancellation.Token).AsTask();
    var survivor = loadingAsync.GetAsync("key").AsTask();
    cancellation.Cancel();
    try
    {
        await canceled;
        throw new InvalidOperationException("Caller cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    loadingAsync.Set("key", "new generation");
    gate.SetResult("old generation");
    Require(await survivor == "old generation", "original waiter completes");
    Require(await loadingAsync.GetAsync("key") == "new generation", "late publication is fenced");
    Require(calls == 1, "single flight");
}

using (
    var weighted = CacheBuilder
        .Create<int, string>()
        .MaximumWeight(10)
        .MaximumResidentCount(4)
        .Weigher(static (_, value) => value.Length)
        .MaxConcurrentLoads(2)
        .Build()
)
{
    weighted.Put(1, "abc");
    weighted.CleanUp();
    Require(weighted.Policy.Eviction!.WeightedSize == 3, "weight accounting");
}
await LoaderContractSmoke.RunAsync();
Console.WriteLine("Consumer smoke passed.");
return;

static void Require(bool condition, string operation)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Consumer smoke failed: {operation}");
    }
}
