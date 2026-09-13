using System.Runtime.InteropServices;
using LoadingCache;
using CacheFactory = LoadingCache.LoadingCache;

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

    await using var valueCache = CacheFactory.Create<int, int>(
        loader: static (key, _) => Task.FromResult(key * 2),
        options: new LoadingCacheOptions { MaximumSize = 16, MaxConcurrentLoads = 4 }
    );

    Assert(await valueCache.GetAsync(21) == 42, "value loader result");
    valueCache.Set(21, 43);
    Assert(valueCache.TryGet(21, out int value) && value == 43, "value TryGet");
    _ = valueCache.GetStatistics();

    int referenceLoads = 0;
    await using var referenceCache = CacheFactory.Create<string, string>(
        loader: async (key, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            referenceLoads++;
            return $"Value for {key}";
        },
        options: new LoadingCacheOptions
        {
            MaximumSize = 1_000,
            MaxConcurrentLoads = 16,
            ExpireAfterWrite = TimeSpan.FromMinutes(10),
            ExpireAfterAccess = TimeSpan.FromMinutes(2),
        },
        comparer: StringComparer.OrdinalIgnoreCase
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
