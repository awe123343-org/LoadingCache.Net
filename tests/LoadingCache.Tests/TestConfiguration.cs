namespace LoadingCache.Tests;

public static class TestConfiguration
{
    [Before(HookType.TestDiscovery)]
    public static void Configure(BeforeTestDiscoveryContext context)
    {
        // Blocking race gates need worker capacity for continuations. Bound concurrent cases
        // to the CPU count; TUnit's 4x CPU default can starve these deliberate interleavings.
        context.Settings.Parallelism.MaximumParallelTests = Math.Max(2, Environment.ProcessorCount);
    }
}
