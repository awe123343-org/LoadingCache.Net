using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace LoadingCache.ServiceProbe;

internal static class LoadingProfile
{
    internal const int Limit = 8;
    internal static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task RunAsync(string[] args)
    {
        string Read(string name, string fallback)
        {
            int index = Array.IndexOf(args, name);
            return index < 0 ? fallback
                : index + 1 < args.Length ? args[index + 1]
                : throw new ArgumentException($"Missing value after {name}.");
        }
        string output = Read("--output", "loading-result.json");
        string fault = Read("--loading-fault", "none");
        if (fault is not ("none" or "backend-error" or "wrong-value" or "stuck-backend"))
            throw new ArgumentException("Unknown loading failure control.");
        bool statistics = args.Contains("--statistics", StringComparer.Ordinal);
        bool smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var cases = new List<object>();
        string? error = null;
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        try
        {
            foreach (string name in new[] { "fan-in", "expiry-refresh", "burst", "normal" })
            {
                await using var state = new LoadingState(name, statistics, fault);
                try
                {
                    await state.StartAsync().ConfigureAwait(false);
                    await RunCaseAsync(state, smoke).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    state.PrimaryError = exception.ToString();
                    throw;
                }
                finally
                {
                    // Preserve partial/failing request and backend records too.
                    try
                    {
                        await state.CleanupAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        cases.Add(state.Result());
                    }
                }
                if (state.CleanupErrors.Count > 0)
                    throw new InvalidOperationException(
                        "Loading profile cleanup failed; see case cleanupErrors."
                    );
            }
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            throw;
        }
        finally
        {
            process.Refresh();
            await File.WriteAllTextAsync(
                    output,
                    JsonSerializer.Serialize<object>(
                        new
                        {
                            schemaVersion = 4,
                            profile = "loading-admission-v2",
                            statistics,
                            smoke,
                            fault,
                            error,
                            argv = Environment.GetCommandLineArgs(),
                            stopwatchFrequency = Stopwatch.Frequency,
                            runtime = RuntimeInformation.FrameworkDescription,
                            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                            os = RuntimeInformation.OSDescription,
                            cpuScope = "combined HTTP client and server process including cleanup",
                            cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                            cacheAssemblySha256 = Hash(typeof(CacheBuilder).Assembly.Location),
                            probeAssemblySha256 = Hash(typeof(LoadingProfile).Assembly.Location),
                            coreLibrarySha256 = Hash(typeof(object).Assembly.Location),
                            aspNetCoreAssemblySha256 = Hash(
                                typeof(WebApplication).Assembly.Location
                            ),
                            cases,
                        },
                        JsonOptions
                    )
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task RunCaseAsync(LoadingState state, bool smoke)
    {
        if (state.Name == "normal")
        {
            await state.ScheduleAsync("warmup", 400, 200, 1000).ConfigureAwait(false);
            await state.DrainAsync("warmup-drained").ConfigureAwait(false);
            await state
                .ScheduleAsync("normal", smoke ? 400 : 6000, 200, 10000)
                .ConfigureAwait(false);
            await state.DrainAsync("normal-drained").ConfigureAwait(false);
            return;
        }
        if (state.Name == "expiry-refresh")
        {
            state.Cache.Set(42, -1);
            state.Clock.Advance(TimeSpan.FromSeconds(6));
            await state.SendAsync("stale", 42, Stopwatch.GetTimestamp()).ConfigureAwait(false);
            await UntilAsync(() => state.Active == 1).ConfigureAwait(false);
            state.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        if (state.Name is "fan-in" or "expiry-refresh")
        {
            Task[] callers =
            [
                .. Enumerable
                    .Range(0, 64)
                    .Select(_ => state.SendAsync("fan-in", 42, Stopwatch.GetTimestamp())),
            ];
            await UntilAsync(() => state.Invoked.Count == (state.Name == "fan-in" ? 64 : 65))
                .ConfigureAwait(false);
            state.Observe("before-release");
            state.Release();
            await Task.WhenAll(callers).WaitAsync(Watchdog).ConfigureAwait(false);
            await state.DrainAsync("fan-in-drained").ConfigureAwait(false);
            return;
        }

        Task[] admitted =
        [
            .. Enumerable
                .Range(0, Limit)
                .Select(key => state.SendAsync("burst-admitted", key, Stopwatch.GetTimestamp())),
        ];
        await UntilAsync(() => state.Active == Limit).ConfigureAwait(false);
        Task[] excess =
        [
            .. Enumerable
                .Range(Limit, 24)
                .Select(key => state.SendAsync("burst-excess", key, Stopwatch.GetTimestamp())),
        ];
        await Task.WhenAll(excess.Concat(admitted)).WaitAsync(Watchdog).ConfigureAwait(false);
        state.Observe("after-timeout");
        Task[] checks =
        [
            .. Enumerable
                .Range(100, Limit)
                .Select(key => state.SendAsync("permit-check", key, Stopwatch.GetTimestamp())),
        ];
        await Task.WhenAll(checks).WaitAsync(Watchdog).ConfigureAwait(false);
        state.Observe("permits-retained");
        state.Release();
        await state.DrainAsync("burst-drained").ConfigureAwait(false);
        state.TimedOutKeysAbsent = Enumerable
            .Range(0, Limit)
            .All(key => !state.Cache.TryGet(key, out _));
        await state.ScheduleAsync("recovery", 64, 100, 1000).ConfigureAwait(false);
        await state.DrainAsync("recovery-drained").ConfigureAwait(false);
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > Watchdog)
                throw new TimeoutException("Loading profile control condition timed out.");
            await Task.Yield();
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed class LoadingState : IAsyncDisposable
{
    private readonly TaskCompletionSource<bool> _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<bool> _failureRelease = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly ConcurrentBag<LoadingRequest> _requests = [];
    private readonly ConcurrentBag<Task> _clientTasks = [];
    private readonly ConcurrentBag<LoadingBackend> _backend = [];
    private readonly List<object> _observations = [];
    private readonly string _fault;
    private readonly Func<bool> _hasActiveFlights;
    private WebApplication? _app;
    private HttpClient? _client;
    private int _active;
    private int _pending;
    private int _requestId;
    private int _loadId;
    private bool _cleaned;
    private long _released;

    internal LoadingState(string name, bool statistics, string fault)
    {
        Name = name;
        _fault = name == "normal" ? fault : "none";
        var builder = CacheBuilder.Create<int, int>().MaximumSize(256);
        if (ConfiguredLoadLimit is { } limit)
            builder.MaxConcurrentLoads(limit).MaxPendingLoadKeys(limit);
        if (statistics)
            builder.RecordStatistics();
        if (name == "expiry-refresh")
            builder
                .TimeProvider(Clock)
                .ExpireAfterWrite(TimeSpan.FromSeconds(10))
                .RefreshAfterWrite(TimeSpan.FromSeconds(5));
        if (name == "burst")
            builder.TimeProvider(TimeoutClock).LoadTimeout(TimeSpan.FromSeconds(2));
        Cache = builder.BuildAsyncLoading(LoadAsync);
        _hasActiveFlights = LoadingFlightDiagnostics.Bind(Cache);
    }

    internal string Name { get; }
    private int? ConfiguredLoadLimit => Name == "normal" ? null : LoadingProfile.Limit;
    internal LoadingClock Clock { get; } = new();
    private LoadingTimeoutClock TimeoutClock { get; } = new();
    internal IAsyncLoadingCache<int, int> Cache { get; }
    internal ConcurrentDictionary<int, long> Invoked { get; } = new();
    private ConcurrentDictionary<int, long> Returned { get; } = new();
    internal int Active => Volatile.Read(ref _active);
    private bool HasActiveFlights => _hasActiveFlights();
    internal bool? TimedOutKeysAbsent { get; set; }
    internal string? PrimaryError { get; set; }
    internal List<string> CleanupErrors { get; } = [];

    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }
        );
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.MapGet(
            "/load/{key:int}/{id:int}",
            async (int key, int id, HttpContext context) =>
            {
                try
                {
                    // Observe after GetAsync has synchronously reserved/joined a flight.
                    ValueTask<int> pending;
                    if (Name == "burst")
                        TimeoutClock.BeginRequest(id, key);
                    try
                    {
                        pending = Cache.GetAsync(key, context.RequestAborted);
                    }
                    finally
                    {
                        if (Name == "burst")
                            TimeoutClock.EndRequest();
                    }
                    Invoked[id] = Stopwatch.GetTimestamp();
                    int value = await pending.ConfigureAwait(false);
                    return Results.Json(value);
                }
                catch (CacheLoadRejectedException)
                {
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }
                catch (TimeoutException)
                {
                    return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }
                catch (InvalidOperationException)
                {
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }
                finally
                {
                    Returned[id] = Stopwatch.GetTimestamp();
                }
            }
        );
        await _app.StartAsync().ConfigureAwait(false);
        string address = _app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        _client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 256 })
        {
            BaseAddress = new Uri(address, UriKind.Absolute),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private async Task<int> LoadAsync(int key, CancellationToken token)
    {
        _ = token; // Intentionally non-cooperative oracle; release gates own cleanup.
        var sample = new LoadingBackend(
            Interlocked.Increment(ref _loadId),
            key,
            Stopwatch.GetTimestamp()
        );
        _backend.Add(sample);
        Interlocked.Increment(ref _active);
        try
        {
            if (Name != "normal" && Volatile.Read(ref _released) == 0)
                await _release.Task.ConfigureAwait(false);
            else
            {
                if (_fault == "stuck-backend" && key == 10000)
                    await _failureRelease.Task.ConfigureAwait(false);
                sample.ServiceStart = Stopwatch.GetTimestamp();
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                if (_fault == "backend-error" && key == 10000)
                    throw new InvalidOperationException("Injected backend failure.");
            }
            return _fault == "wrong-value" && key == 10000 ? -2 : key;
        }
        finally
        {
            sample.Finished = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _active);
        }
    }

    internal void Release()
    {
        Interlocked.CompareExchange(ref _released, Stopwatch.GetTimestamp(), 0);
        _release.TrySetResult(true);
    }

    internal void Observe(string label) =>
        _observations.Add(
            new
            {
                label,
                timestamp = Stopwatch.GetTimestamp(),
                active = Active,
                activeFlights = HasActiveFlights,
                backendCalls = _backend.Count,
                invoked = Invoked.Count,
                returned = Returned.Count,
                pending = Volatile.Read(ref _pending),
                statistics = Cache.GetStatistics(),
                notifications = Cache.GetNotificationStatistics(),
                residents = Cache.EstimatedCount,
                gcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(),
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(),
            }
        );

    internal async Task DrainAsync(string label)
    {
        long start = Stopwatch.GetTimestamp();
        await LoadingProfile
            .UntilAsync(() => LoadingFlightDiagnostics.IsDrained(Cache, Active, _hasActiveFlights))
            .ConfigureAwait(false);
        _observations.Add(
            new
            {
                label = label + "-duration",
                start,
                timestamp = Stopwatch.GetTimestamp(),
            }
        );
        Observe(label);
    }

    internal async Task ScheduleAsync(string phase, int count, int rate, int firstKey)
    {
        long origin = Stopwatch.GetTimestamp();
        var tasks = new Task[count];
        for (int index = 0; index < count; index++)
        {
            long scheduled = origin + (long)((double)index * Stopwatch.Frequency / rate);
            while (Stopwatch.GetTimestamp() < scheduled)
                await Task.Delay(1).ConfigureAwait(false);
            tasks[index] = SendAsync(phase, firstKey + index, scheduled);
            if (index % rate == 0)
                Observe(phase + "-sample");
        }
        await Task.WhenAll(tasks).WaitAsync(LoadingProfile.Watchdog).ConfigureAwait(false);
    }

    internal Task SendAsync(string phase, int key, long scheduled)
    {
        Task task = SendCoreAsync(phase, key, scheduled);
        _clientTasks.Add(task);
        return task;
    }

    private async Task SendCoreAsync(string phase, int key, long scheduled)
    {
        var request = new LoadingRequest(
            Interlocked.Increment(ref _requestId),
            phase,
            key,
            scheduled
        );
        _requests.Add(request);
        request.PendingAtAdmission = Interlocked.Increment(ref _pending);
        if (request.PendingAtAdmission > 256)
        {
            request.Outcome = "rejected";
            request.Detail = "client-capacity";
            request.Finished = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _pending);
            return;
        }
        try
        {
            double remaining = 5000 - Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds;
            if (remaining <= 0)
            {
                request.Outcome = "timeout";
                request.Detail = "arrival-deadline";
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
            request.Sent = Stopwatch.GetTimestamp();
            using HttpResponseMessage response = await _client!
                .GetAsync(
                    $"/load/{key}/{request.Id}",
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token
                )
                .ConfigureAwait(false);
            request.HeadersReceived = Stopwatch.GetTimestamp();
            request.StatusCode = (int)response.StatusCode;
            request.Outcome = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "rejected",
                HttpStatusCode.GatewayTimeout => "timeout",
                HttpStatusCode.OK => "completed",
                _ => "failed",
            };
            if (response.IsSuccessStatusCode)
            {
                request.Value = await response
                    .Content.ReadFromJsonAsync<int>(timeout.Token)
                    .ConfigureAwait(false);
                if (request.Value != (phase == "stale" ? -1 : key))
                {
                    request.Outcome = "failed";
                    request.Detail = "wrong-value";
                }
            }
        }
        catch (OperationCanceledException)
        {
            request.Outcome = "timeout";
            request.Detail = "client-deadline";
        }
        catch (Exception exception)
            when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            request.Outcome = "failed";
            request.Detail = exception.GetType().Name;
        }
        finally
        {
            request.Finished = Stopwatch.GetTimestamp();
            Interlocked.Decrement(ref _pending);
        }
    }

    internal async Task CleanupAsync()
    {
        if (_cleaned)
            return;
        _cleaned = true;
        Release();
        _failureRelease.TrySetResult(true);
        await AttemptAsync(async () =>
            {
                await Task.WhenAll(_clientTasks)
                    .WaitAsync(LoadingProfile.Watchdog)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        if (_app is not null)
        {
            await AttemptAsync(async () =>
                {
                    using var stop = new CancellationTokenSource(LoadingProfile.Watchdog);
                    await _app.StopAsync(stop.Token)
                        .WaitAsync(LoadingProfile.Watchdog, CancellationToken.None)
                        .ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        await AttemptAsync(() => DrainAsync("cleanup-drained")).ConfigureAwait(false);
        await AttemptAsync(async () =>
            {
                await Cache
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(LoadingProfile.Watchdog)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        _client?.Dispose();
        if (_app is not null)
        {
            await AttemptAsync(async () =>
                {
                    await _app.DisposeAsync()
                        .AsTask()
                        .WaitAsync(LoadingProfile.Watchdog)
                        .ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
    }

    private async Task AttemptAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CleanupErrors.Add(exception.ToString());
        }
    }

    internal object Result() =>
        new
        {
            name = Name,
            primaryError = PrimaryError,
            cleanupErrors = CleanupErrors,
            capacity = 256,
            maxConcurrentLoads = ConfiguredLoadLimit,
            maxPendingLoadKeys = ConfiguredLoadLimit,
            backendServiceMilliseconds = 20,
            releaseTimestamp = Volatile.Read(ref _released),
            timedOutKeysAbsent = TimedOutKeysAbsent,
            requests = _requests.OrderBy(row => row.Id).ToArray(),
            backend = _backend.OrderBy(row => row.Id).ToArray(),
            invoked = Invoked.OrderBy(pair => pair.Key).ToArray(),
            returned = Returned.OrderBy(pair => pair.Key).ToArray(),
            timeoutOrigins = TimeoutClock.Origins.OrderBy(row => row.RequestId).ToArray(),
            observations = _observations,
        };

    public ValueTask DisposeAsync() => new(CleanupAsync());
}

internal static class LoadingFlightDiagnostics
{
    // The service probe is not an InternalsVisibleTo friend. Bind the existing
    // locked diagnostic once per cache, outside request paths, and fail closed
    // if a future core changes this internal layout. Do not inspect the registry.
    private static readonly FieldInfo EngineField =
        typeof(AsyncLoadingCache<int, int>).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException("Cache engine diagnostic field is unavailable.");
    private static readonly MethodInfo HasActiveFlightsGetter =
        EngineField
            .FieldType.GetProperty(
                "HasActiveFlights",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?.GetGetMethod(nonPublic: true)
        ?? throw new InvalidOperationException("Active-flight diagnostic getter is unavailable.");

    internal static Func<bool> Bind(IAsyncLoadingCache<int, int> cache) =>
        HasActiveFlightsGetter.CreateDelegate<Func<bool>>(
            EngineField.GetValue(cache)
                ?? throw new InvalidOperationException("Cache engine diagnostic target is missing.")
        );

    internal static bool IsDrained(
        IAsyncLoadingCache<int, int> cache,
        int active,
        Func<bool> hasActiveFlights
    )
    {
        // Callers must stop/join producers before using an empty registry as
        // a quiescence boundary; public executing-load gauges alone are weaker.
        cache.CleanUp();
        CacheStatistics stats = cache.GetStatistics();
        if (
            active != 0
            || stats.InFlightLoads != 0
            || hasActiveFlights()
            || stats.MaintenanceBacklog != 0
            || stats.WriteBufferBacklog != 0
        )
            return false;
        cache.CleanUp();
        stats = cache.GetStatistics();
        return stats is { InFlightLoads: 0, MaintenanceBacklog: 0, WriteBufferBacklog: 0 }
            && !hasActiveFlights()
            && cache.GetNotificationStatistics() is { Queued: 0, HandlerRunning: false };
    }
}

internal sealed class LoadingClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

internal sealed class LoadingTimeoutClock : TimeProvider
{
    private readonly AsyncLocal<RequestScope?> _request = new();
    internal ConcurrentBag<LoadingTimeoutOrigin> Origins { get; } = [];
    public override long TimestampFrequency => Stopwatch.Frequency;

    internal void BeginRequest(int id, int key) => _request.Value = new RequestScope(id, key);

    internal void EndRequest() => _request.Value = null;

    public override long GetTimestamp()
    {
        long timestamp = Stopwatch.GetTimestamp();
        if (_request.Value is { } request)
            request.LastTimestamp = timestamp;
        return timestamp;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        // EngineTimeout.PrepareFlightExecution assigns TimeoutStartTimestamp
        // from its last GetTimestamp before synchronously calling CreateTimer.
        // This seam is pinned to the source/build manifest, not HTTP dispatch.
        RequestScope request =
            _request.Value
            ?? throw new InvalidOperationException("Timeout timer has no request scope.");
        if (
            request.LastTimestamp == 0
            || request.TimerCreated
            || dueTime != TimeSpan.FromSeconds(2)
            || period != Timeout.InfiniteTimeSpan
        )
            throw new InvalidOperationException("Unexpected cache timeout timer sequence.");
        request.TimerCreated = true;
        Origins.Add(
            new LoadingTimeoutOrigin(
                request.Id,
                request.Key,
                request.LastTimestamp,
                Stopwatch.GetTimestamp(),
                dueTime.Ticks,
                period.Ticks
            )
        );
        return System.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class RequestScope(int id, int key)
    {
        internal int Id { get; } = id;
        internal int Key { get; } = key;
        internal long LastTimestamp { get; set; }
        internal bool TimerCreated { get; set; }
    }
}

internal sealed record LoadingTimeoutOrigin(
    int RequestId,
    [property: JsonInclude] int Key,
    [property: JsonInclude] long TimeoutStartTimestamp,
    [property: JsonInclude] long TimerCreatedTimestamp,
    [property: JsonInclude] long DueTimeTicks,
    [property: JsonInclude] long PeriodTicks
);

internal sealed class LoadingRequest(int id, string phase, int key, long scheduled)
{
    public int Id { get; } = id;

    [JsonInclude]
    public string Phase { get; } = phase;

    [JsonInclude]
    public int Key { get; } = key;

    [JsonInclude]
    public long Scheduled { get; } = scheduled;

    [JsonInclude]
    public long Sent { get; set; }

    [JsonInclude]
    public long HeadersReceived { get; set; }
    public int PendingAtAdmission { get; set; }

    [JsonInclude]
    public long Finished { get; set; }

    [JsonInclude]
    public string Outcome { get; set; } = "unfinished";

    [JsonInclude]
    public string? Detail { get; set; }

    [JsonInclude]
    public int? StatusCode { get; set; }
    public int? Value { get; set; }
}

internal sealed class LoadingBackend(int id, int key, long started)
{
    public int Id { get; } = id;

    [JsonInclude]
    public int Key { get; } = key;

    [JsonInclude]
    public long Started { get; } = started;

    [JsonInclude]
    public long ServiceStart { get; set; }

    [JsonInclude]
    public long Finished { get; set; }
}
