using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadingCache;
using LoadingCache.ServiceProbe;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

if (args.Contains("--loading-profile", StringComparer.Ordinal))
{
    await LoadingProfile.RunAsync(args).ConfigureAwait(false);
    return;
}

const int capacity = 1024;
const int payloadCharacters = 1024;
string mode = Read("--mode", "server");
string backend = Read("--backend", "control");
string expiry = Read("--expiry", "ttl");
bool statistics = args.Contains("--statistics", StringComparer.Ordinal);
int rate = ReadInt("--rate", 2000, 1, 100000);
int seconds = ReadInt("--seconds", 30, 1, 300);
int warmupSeconds = ReadInt("--warmup-seconds", 10, 1, 60);
int maxPending = ReadInt("--max-pending", 256, 1, 4096);
int timeoutMilliseconds = ReadInt("--timeout-ms", 5000, 1, 60000);
string output = Read("--output", "service-result.json");
if (
    mode is not ("server" or "client")
    || backend is not ("control" or "cache")
    || expiry is not ("ttl" or "tti")
)
{
    throw new ArgumentException("Expected server/client, control/cache, and ttl/tti.");
}

if (mode == "client")
{
    await RunClient().ConfigureAwait(false);
    return;
}

TenantConfig[] values =
[
    .. Enumerable
        .Range(0, capacity)
        .Select(key => new TenantConfig(key, new string('x', payloadCharacters))),
];
long loads = 0;
IAsyncLoadingCache<int, TenantConfig>? cache = null;
if (backend == "cache")
{
    var cacheBuilder = CacheBuilder
        .Create<int, TenantConfig>()
        .MaximumSize(capacity)
        .MaxConcurrentLoads(16)
        .MaxPendingLoadKeys(16);
    if (expiry == "ttl")
        cacheBuilder.ExpireAfterWrite(TimeSpan.FromMinutes(30));
    else
        cacheBuilder.ExpireAfterAccess(TimeSpan.FromMinutes(10));
    if (statistics)
        cacheBuilder.RecordStatistics();
    cache = cacheBuilder.BuildAsyncLoading(
        (key, _) =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(values[key]);
        }
    );
    foreach (TenantConfig value in values)
        cache.Set(value.TenantId, value);
    cache.CleanUp();
}

var builder = WebApplication.CreateSlimBuilder(
    new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }
);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using WebApplication app = builder.Build();
using ProcessCpuClock cpuClock = new();
Func<TimeSpan> readCpuTime = cpuClock.Read;
TimeSpan cpuStart = TimeSpan.Zero;
long wallStart = 0;
long requestCount = 0;
long requestsAtStart = 0;
int state = 0;
app.MapGet(
    "/config/{key:int}",
    async (int key, HttpContext context) =>
    {
        if ((uint)key >= capacity || Volatile.Read(ref state) == 2)
            return Results.BadRequest();
        TenantConfig value = cache is null
            ? values[key]
            : await cache.GetAsync(key, context.RequestAborted).ConfigureAwait(false);
        Interlocked.Increment(ref requestCount);
        return Results.Json(value);
    }
);
app.MapPost(
    "/begin",
    async () =>
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            throw new InvalidOperationException("Measurement already started.");
        if (cache is not null)
            await DrainCache(cache).ConfigureAwait(false);
        requestsAtStart = Interlocked.Read(ref requestCount);
        cpuStart = readCpuTime();
        wallStart = Stopwatch.GetTimestamp();
        return Results.Ok();
    }
);
app.MapPost(
    "/end",
    async () =>
    {
        if (Interlocked.CompareExchange(ref state, 2, 1) != 1)
            throw new InvalidOperationException("Measurement is not active.");
        long drainStart = Stopwatch.GetTimestamp();
        DrainEvidence? drain = null;
        if (cache is not null)
        {
            drain = await DrainCache(cache).ConfigureAwait(false);
            await cache.DisposeAsync().ConfigureAwait(false);
        }
        double cpuSeconds = (readCpuTime() - cpuStart).TotalSeconds;
        return Results.Json(
            new
            {
                schemaVersion = 1,
                backend,
                expiry,
                statistics,
                capacity,
                payloadCharacters,
                lookupsPerRequest = 1,
                loaderServiceTimeMilliseconds = 0,
                expectedLoads = 0,
                actualLoads = Interlocked.Read(ref loads),
                measuredRequests = Interlocked.Read(ref requestCount) - requestsAtStart,
                cpuSeconds,
                wallSecondsIncludingDrain = Stopwatch.GetElapsedTime(wallStart).TotalSeconds,
                drainSeconds = Stopwatch.GetElapsedTime(drainStart).TotalSeconds,
                drain,
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                os = RuntimeInformation.OSDescription,
                logicalProcessors = Environment.ProcessorCount,
                serverGc = System.Runtime.GCSettings.IsServerGC,
                argv = Environment.GetCommandLineArgs(),
                cacheAssemblySha256 = Hash(typeof(CacheBuilder).Assembly.Location),
                probeAssemblySha256 = Hash(typeof(TenantConfig).Assembly.Location),
                coreLibrarySha256 = Hash(typeof(object).Assembly.Location),
                aspNetCoreAssemblySha256 = Hash(typeof(WebApplication).Assembly.Location),
            }
        );
    }
);
await app.StartAsync().ConfigureAwait(false);
try
{
    string address = app
        .Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!
        .Addresses.Single();
    await File.WriteAllTextAsync(Read("--ready-file", "service-ready.txt"), address)
        .ConfigureAwait(false);
    await app.WaitForShutdownAsync().ConfigureAwait(false);
}
finally
{
    if (cache is not null)
        await cache.DisposeAsync().ConfigureAwait(false);
}

return;

async Task RunClient()
{
    using var handler = new SocketsHttpHandler();
    handler.MaxConnectionsPerServer = maxPending;
    using var client = new HttpClient(handler);
    client.BaseAddress = new Uri(Read("--url", "http://127.0.0.1:5000"), UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
    Phase warmup = await RunPhase(client, warmupSeconds).ConfigureAwait(false);
    using (HttpResponseMessage begin = await client.PostAsync("/begin", null).ConfigureAwait(false))
        begin.EnsureSuccessStatusCode();
    Phase measured = await RunPhase(client, seconds).ConfigureAwait(false);
    using HttpResponseMessage end = await client.PostAsync("/end", null).ConfigureAwait(false);
    end.EnsureSuccessStatusCode();
    JsonElement server = await end.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
    await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    argv = Environment.GetCommandLineArgs(),
                    rate,
                    seconds,
                    warmupSeconds,
                    maxPending,
                    timeoutMilliseconds,
                    stopwatchFrequency = Stopwatch.Frequency,
                    model = "fixed open-loop arrival; latency from scheduled arrival through complete response validation; failed outcomes retained",
                    warmup,
                    measured,
                    server,
                }
            )
        )
        .ConfigureAwait(false);
}

async Task<Phase> RunPhase(HttpClient client, int durationSeconds)
{
    int offered = checked(rate * durationSeconds);
    long[] elapsedTicks = new long[offered];
    string[] outcomes = new string[offered];
    Task[] requests = new Task[offered];
    PhaseConcurrency concurrency = new();
    long origin = Stopwatch.GetTimestamp();
    for (int index = 0; index < offered; index++)
    {
        long scheduled = origin + (long)((double)index * Stopwatch.Frequency / rate);
        while (Stopwatch.GetTimestamp() < scheduled)
            await Task.Delay(1).ConfigureAwait(false);
        if (Volatile.Read(ref concurrency.Pending) >= maxPending)
        {
            outcomes[index] = "rejected";
            elapsedTicks[index] = Stopwatch.GetTimestamp() - scheduled;
            requests[index] = Task.CompletedTask;
            continue;
        }
        Interlocked.Increment(ref concurrency.Pending);
        requests[index] = Send(index, scheduled);
    }
    await Task.WhenAll(requests).ConfigureAwait(false);
    long[] successfulTicks =
    [
        .. elapsedTicks.Where((_, index) => outcomes[index] == "completed").Order(),
    ];
    return new Phase(
        offered,
        outcomes.Count(value => value == "completed"),
        outcomes.Count(value => value == "rejected"),
        outcomes.Count(value => value == "timeout"),
        outcomes.Count(value => value == "failed"),
        Percentile(successfulTicks, 0.5),
        Percentile(successfulTicks, 0.99),
        Stopwatch.GetElapsedTime(origin).TotalSeconds,
        elapsedTicks,
        outcomes
    );

    async Task Send(int index, long scheduled)
    {
        try
        {
            double remaining =
                timeoutMilliseconds - Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds;
            if (remaining <= 0)
            {
                outcomes[index] = "timeout";
                return;
            }
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(remaining)
            );
            int key = index % capacity;
            using HttpResponseMessage response = await client
                .GetAsync(
                    new Uri(
                        $"/config/{key.ToString(CultureInfo.InvariantCulture)}",
                        UriKind.Relative
                    ),
                    cancellation.Token
                )
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            TenantConfig? value = await response
                .Content.ReadFromJsonAsync<TenantConfig>(cancellation.Token)
                .ConfigureAwait(false);
            if (
                value is null
                || value.TenantId != key
                || value.Value.Length != payloadCharacters
                || value.Value.Any(character => character != 'x')
            )
                throw new InvalidOperationException("Response differs from the preloaded config.");
            outcomes[index] = "completed";
        }
        catch (OperationCanceledException)
        {
            outcomes[index] = "timeout";
        }
        catch (Exception exception)
            when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            outcomes[index] = "failed";
        }
        finally
        {
            elapsedTicks[index] = Stopwatch.GetTimestamp() - scheduled;
            Interlocked.Decrement(ref concurrency.Pending);
        }
    }
}

static async Task<DrainEvidence> DrainCache(IAsyncLoadingCache<int, TenantConfig> valueCache)
{
    CacheStatistics initial = valueCache.GetStatistics();
    var watch = Stopwatch.StartNew();
    int attempts = 0;
    while (true)
    {
        valueCache.CleanUp();
        attempts++;
        CacheStatistics snapshot = valueCache.GetStatistics();
        if (IsQuiescent(snapshot))
        {
            // There are no producers after the client has awaited every response. A worker
            // can consume its final queue slot before applying it. This second CleanUp takes
            // the engine coordination lock after that observation, joining any such policy
            // application before the final zero-backlog snapshot. Dispose is not the drain.
            valueCache.CleanUp();
            snapshot = valueCache.GetStatistics();
            CacheNotificationStatistics notifications = valueCache.GetNotificationStatistics();
            if (IsQuiescent(snapshot) && notifications is { Queued: 0, HandlerRunning: false })
                return new DrainEvidence(initial, snapshot, attempts, watch.Elapsed.TotalSeconds);
        }
        if (watch.Elapsed.TotalSeconds >= 30)
            throw new TimeoutException("Cache maintenance did not quiesce before CPU sampling.");
        await Task.Yield();
    }

    static bool IsQuiescent(CacheStatistics snapshot) =>
        snapshot
            is {
                InFlightLoads: 0,
                MaintenanceBacklog: 0,
                WriteBufferBacklog: 0,
                MaintenanceFaults: 0,
            };
}

static double? Percentile(long[] samples, double percentile) =>
    samples.Length == 0
        ? null
        : samples[
            Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)
        ] * (1_000_000d / Stopwatch.Frequency);
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
string Read(string option, string fallback)
{
    int index = Array.IndexOf(args, option);
    return index < 0 ? fallback
        : index + 1 < args.Length ? args[index + 1]
        : throw new ArgumentException($"Missing value after {option}.");
}
int ReadInt(string option, int fallback, int minimum, int maximum)
{
    if (
        !int.TryParse(
            Read(option, fallback.ToString(CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture,
            out int value
        )
        || value < minimum
        || value > maximum
    )
        throw new ArgumentOutOfRangeException(option, $"Expected {minimum} through {maximum}.");
    return value;
}

internal sealed record TenantConfig(int TenantId, string Value);

internal sealed record DrainEvidence(
    [property: JsonInclude] CacheStatistics Initial,
    [property: JsonInclude] CacheStatistics Final,
    [property: JsonInclude] int Attempts,
    [property: JsonInclude] double Seconds
);

internal sealed record Phase(
    [property: JsonInclude] int Offered,
    [property: JsonInclude] int Completed,
    [property: JsonInclude] int Rejected,
    [property: JsonInclude] int Timeout,
    [property: JsonInclude] int Failed,
    [property: JsonInclude] double? P50Microseconds,
    [property: JsonInclude] double? P99Microseconds,
    [property: JsonInclude] double WallSecondsIncludingDrain,
    [property: JsonInclude] long[] LatencyTicks,
    [property: JsonInclude] string[] Outcomes
);

internal sealed class ProcessCpuClock : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();

    internal TimeSpan Read()
    {
        _process.Refresh();
        return _process.TotalProcessorTime;
    }

    public void Dispose() => _process.Dispose();
}

internal sealed class PhaseConcurrency
{
    internal int Pending;
}
