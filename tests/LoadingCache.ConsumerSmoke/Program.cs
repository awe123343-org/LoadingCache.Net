using System.Runtime.InteropServices;
using LoadingCache;

Console.WriteLine(
    $"Consumer smoke runtime: {RuntimeInformation.FrameworkDescription}; architecture: {RuntimeInformation.ProcessArchitecture}"
);
await SmokeAsync();
await LoaderContractSmoke.RunAsync();
await NativePolicySmoke.RunAsync();
return;

static async Task SmokeAsync()
{
    await using var documentedBuilder = CacheBuilder
        .Create<string, string>()
        .MaximumSize(1_000)
        .MaxConcurrentLoads(16)
        .ExpireAfterWrite(TimeSpan.FromMinutes(10))
        .ExpireAfterAccess(TimeSpan.FromMinutes(2))
        .Comparer(StringComparer.Ordinal)
        .BuildAsyncLoading((key, _) => Task.FromResult($"Value for {key}"));
    Assert(
        await documentedBuilder.GetAsync("configuration") == "Value for configuration",
        "README builder example"
    );
    documentedBuilder.Invalidate("configuration");

    await using var valueCache = CacheBuilder
        .Create<int, int>()
        .MaximumSize(16)
        .MaxConcurrentLoads(4)
        .BuildAsyncLoading(static (key, _) => Task.FromResult(key * 2));

    Assert(await valueCache.GetAsync(21) == 42, "value loader result");
    valueCache.Set(21, 43);
    Assert(valueCache.TryGet(21, out int value) && value == 43, "value TryGet");
    _ = valueCache.GetStatistics();

    int referenceLoads = 0;
    await using var referenceCache = CacheBuilder
        .Create<string, string>()
        .MaximumSize(1_000)
        .MaxConcurrentLoads(16)
        .ExpireAfterWrite(TimeSpan.FromMinutes(10))
        .ExpireAfterAccess(TimeSpan.FromMinutes(2))
        .Comparer(StringComparer.OrdinalIgnoreCase)
        .BuildAsyncLoading(
            async (key, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                referenceLoads++;
                return $"Value for {key}";
            }
        );

    Assert(
        await referenceCache.GetAsync("configuration") == "Value for configuration",
        "README loader result"
    );
    Assert(
        await referenceCache.GetAsync("CONFIGURATION") == "Value for configuration",
        "custom comparer shares resident value"
    );
    Assert(referenceLoads == 1, "custom comparer coalesces equivalent key hit");
    referenceCache.Invalidate("configuration");
    Assert(!referenceCache.TryGet("configuration", out _), "README invalidate");
}

static void Assert(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Consumer smoke assertion failed: {description}");
    }
}
