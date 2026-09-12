using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LoadingCache;

// This tool measures actual request durations in a closed-loop workload. It is
// intentionally separate from lookup microbenchmarks and policy simulation.
string scenario = Read("--scenario", "resident");
int concurrency = ReadInt("--concurrency", 4, 1, 256);
int operations = ReadInt("--operations", 20000, 1, 1000000);
int capacity = ReadInt("--capacity", 128, 1, 1000000);
int seed = ReadInt("--seed", 419, 0, int.MaxValue);
bool statistics = args.Contains("--statistics", StringComparer.Ordinal);
bool cancelable = args.Contains("--cancelable", StringComparer.Ordinal);
if (scenario is not ("resident" or "mixed" or "fan-in"))
{
    throw new ArgumentException("Scenario must be resident, mixed, or fan-in.");
}

using var callerSource = new CancellationTokenSource();
CancellationToken callerToken = cancelable ? callerSource.Token : CancellationToken.None;
long loaderInvocations = 0;
TaskCompletionSource<bool>? releaseLoad = null;
var builder = CacheBuilder.Create<int, int>().MaximumSize(capacity).MaxConcurrentLoads(concurrency);
if (statistics)
{
    builder.RecordStatistics();
}

IAsyncLoadingCache<int, int> cache = builder.BuildAsyncLoading(
    async (key, _) =>
    {
        Interlocked.Increment(ref loaderInvocations);
        if (scenario == "fan-in")
        {
            await Volatile.Read(ref releaseLoad)!.Task.ConfigureAwait(false);
        }
        else
        {
            // A genuine asynchronous completion without external network latency.
            // It models scheduler work, not an RPC service or cache-only overhead.
            await Task.Yield();
        }
        return key;
    }
);

long[] durations = new long[operations];
TaskCompletionSource<bool>? startGate = null;
Task[] workers = [];
Task[] pendingRequests = [];
try
{
    int[] keys = new int[operations];
    var random = new Random(seed);
    for (int index = 0; index < keys.Length; index++)
    {
        keys[index] = random.Next(scenario == "mixed" ? checked(capacity * 4) : capacity);
    }
    if (scenario is "resident" or "mixed")
    {
        for (int key = 0; key < capacity; key++)
        {
            cache.Set(key, key);
        }
    }
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    int[] collectionsBefore = [.. Enumerable.Range(0, 3).Select(GC.CollectionCount)];
    long workloadStarted = Stopwatch.GetTimestamp();
    if (scenario == "fan-in")
    {
        for (int start = 0; start < operations; start += concurrency)
        {
            cache.Invalidate(0);
            Volatile.Write(
                ref releaseLoad,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            int count = Math.Min(concurrency, operations - start);
            Task[] requests = new Task[count];
            pendingRequests = requests;
            for (int index = 0; index < count; index++)
            {
                // Invoke every GetAsync before releasing its shared loader. No sleep
                // is used to guess whether the same-generation callers have joined.
                requests[index] = Measure(cache, callerToken, durations, start + index, 0);
            }
            TaskCompletionSource<bool> release =
                Volatile.Read(ref releaseLoad)
                ?? throw new InvalidOperationException("Fan-in loader gate was not installed.");
            release.TrySetResult(true);
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            pendingRequests = [];
        }
    }
    else
    {
        startGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        workers =
        [
            .. Enumerable
                .Range(0, concurrency)
                .Select(worker =>
                    Task.Run(async () =>
                    {
                        await startGate.Task.ConfigureAwait(false);
                        for (int index = worker; index < operations; index += concurrency)
                        {
                            await Measure(cache, callerToken, durations, index, keys[index])
                                .ConfigureAwait(false);
                        }
                    })
                ),
        ];
        startGate.TrySetResult(true);
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }

    TimeSpan elapsed = Stopwatch.GetElapsedTime(workloadStarted);
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    int[] collections =
    [
        .. Enumerable
            .Range(0, 3)
            .Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation]),
    ];
    cache.CleanUp();
    long actualLoads = Interlocked.Read(ref loaderInvocations);
    long? expectedLoads = scenario switch
    {
        "resident" => 0,
        "fan-in" => (operations + concurrency - 1L) / concurrency,
        _ => null,
    };
    if (expectedLoads is not null && actualLoads != expectedLoads)
    {
        throw new InvalidOperationException(
            $"Expected {expectedLoads} loads; observed {actualLoads}."
        );
    }

    Array.Sort(durations);
    string assemblyPath = typeof(CacheBuilder).Assembly.Location;
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                scenario,
                concurrency,
                operations,
                capacity,
                seed,
                statistics,
                cancelable,
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                os = RuntimeInformation.OSDescription,
                logicalProcessors = Environment.ProcessorCount,
                stopwatchFrequency = Stopwatch.Frequency,
                serverGc = System.Runtime.GCSettings.IsServerGC,
                runtimeEnvironment = new
                {
                    dotnetTieredCompilation = Environment.GetEnvironmentVariable(
                        "DOTNET_TieredCompilation"
                    ),
                    dotnetTieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"),
                    comPlusTieredCompilation = Environment.GetEnvironmentVariable(
                        "COMPlus_TieredCompilation"
                    ),
                    comPlusTieredPgo = Environment.GetEnvironmentVariable("COMPlus_TieredPGO"),
                    unspecifiedMeansRuntimeDefaultNotIndependentlyVerified = true,
                },
                cacheAssemblySha256 = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(assemblyPath))
                ),
                latencyModel = "closed-loop; measured GetAsync invocation-to-completion; external queue delay excluded; no coordinated-omission correction; no overload SLA claim",
                fanInModel = "gated shared loader; early waiters include intentional fan-in gate time; not backend RPC latency",
                warmup = "resident/mixed prepopulation only; no timed warmup; exploratory smoke until stable-run protocol is applied",
                elapsedSeconds = elapsed.TotalSeconds,
                operationsPerSecond = operations / elapsed.TotalSeconds,
                latencyMicroseconds = new
                {
                    p50 = Percentile(0.50),
                    p95 = Percentile(0.95),
                    p99 = Percentile(0.99),
                },
                wholeHarnessAllocatedBytes = allocated,
                gcCollections = collections,
                loaderInvocations = actualLoads,
                expectedLoaderInvocations = expectedLoads,
                backendCallsAvoided = operations - actualLoads,
                estimatedResidentCount = cache.EstimatedCount,
                cacheStatistics = cache.GetStatistics(),
            }
        )
    );
}
finally
{
    startGate?.TrySetResult(true);
    Volatile.Read(ref releaseLoad)?.TrySetResult(true);
    try
    {
        if (pendingRequests.Length > 0)
        {
            await Task.WhenAll(pendingRequests)
                .WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (workers.Length > 0)
        {
            await Task.WhenAll(workers)
                .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
    finally
    {
        await cache.DisposeAsync().ConfigureAwait(false);
    }
}

return;

static async Task Measure(
    IAsyncLoadingCache<int, int> cache,
    CancellationToken callerToken,
    long[] durations,
    int index,
    int key
)
{
    long started = Stopwatch.GetTimestamp();
    int value = await cache.GetAsync(key, callerToken).ConfigureAwait(false);
    durations[index] = Stopwatch.GetTimestamp() - started;
    if (value != key)
    {
        throw new InvalidOperationException(
            $"Wrong value {value} for key {key} at operation {index}."
        );
    }
}

double Percentile(double fraction)
{
    int rank = Math.Clamp(
        (int)Math.Ceiling(fraction * durations.Length) - 1,
        0,
        durations.Length - 1
    );
    return durations[rank] * (1000000d / Stopwatch.Frequency);
}

string Read(string option, string fallback)
{
    int index = Array.IndexOf(args, option);
    return index < 0 ? fallback
        : index + 1 < args.Length ? args[index + 1]
        : throw new ArgumentException($"Missing value after {option}.");
}

int ReadInt(string option, int fallback, int minimum, int maximum)
{
    string raw = Read(option, fallback.ToString(CultureInfo.InvariantCulture));
    if (
        !int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
        || value < minimum
        || value > maximum
    )
    {
        throw new ArgumentOutOfRangeException(
            option,
            $"Expected an integer from {minimum} to {maximum}."
        );
    }
    return value;
}
