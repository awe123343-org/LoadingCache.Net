using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LoadingCache;

int seconds = ReadInt("--seconds", 28_800, 1, 86_400);
int cycleSeconds = ReadInt("--cycle-seconds", 300, 1, 600);
int warmupCycles = ReadInt("--warmup-cycles", 4, 0, 10);
bool formal = seconds >= 28_800 && cycleSeconds == 300 && warmupCycles == 4;
var elapsed = Stopwatch.StartNew();
var samples = new List<RetentionSample>();
var retired = new Queue<Retired>();
long operations = 0;
int cycle = 0;
int retainedFailures = 0;
Write(
    new
    {
        Event = "start",
        SchemaVersion = 1,
        Profile = "v1-endurance-1",
        formal,
        seconds,
        cycleSeconds,
        warmupCycles,
        Seed = 20260916,
        State.Capacity,
        Workers = 8,
        BatchOperations = 64,
        Runtime = RuntimeInformation.FrameworkDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        OperatingSystem = RuntimeInformation.OSDescription,
        StopwatchFrequency = Stopwatch.Frequency,
        CacheAssemblySha256 = Hash(typeof(CacheBuilder).Assembly.Location),
        ProbeAssemblySha256 = Hash(typeof(State).Assembly.Location),
        RetentionGate = "five-sample window medians; delta <= max(16 MiB,25% baseline); slope <=1 MiB/hour; retired graphs older than two cycles must clear within two observations",
    }
);
try
{
    while (elapsed.Elapsed.TotalSeconds < seconds)
    {
        double remaining = seconds - elapsed.Elapsed.TotalSeconds;
        CycleResult result = await RunCycleAsync(
                cycle,
                Math.Min(cycleSeconds, remaining),
                operations
            )
            .ConfigureAwait(false);
        operations += result.Operations;
        retired.Enqueue(new Retired(cycle, result.Targets));
        while (retired.Count > 128)
            retired.Dequeue();
        // Samples occur after the method owning all cache/task/value strong roots returned.
        Collect();
        int oldTargetsAlive = retired
            .Where(item => item.Cycle <= cycle - 2)
            .Sum(item => item.Targets.Count(reference => reference.IsAlive));
        retainedFailures = oldTargetsAlive == 0 ? 0 : retainedFailures + 1;
        Require(
            retainedFailures < 2,
            "Retired cache graphs survived two old-generation observations."
        );
        long managed = GC.GetTotalMemory(false);
        if (cycle >= warmupCycles)
            samples.Add(new RetentionSample(elapsed.Elapsed.TotalSeconds, managed));
        using var process = Process.GetCurrentProcess();
        Write(
            new
            {
                Event = "retention",
                cycle,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                Operations = operations,
                result.Clears,
                result.Rejections,
                PostDisposeManagedBytes = managed,
                OldTargetsAlive = oldTargetsAlive,
                RetiredTargetCount = retired.Sum(item => item.Targets.Length),
                Heap = GC.GetGCMemoryInfo().HeapSizeBytes,
                Fragmented = GC.GetGCMemoryInfo().FragmentedBytes,
                WorkingSet = process.WorkingSet64,
                PrivateBytes = process.PrivateMemorySize64,
                AllocatedBytes = GC.GetTotalAllocatedBytes(),
                GcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(),
            }
        );
        cycle++;
    }
    // The full run requires enough independent post-warmup windows to evaluate a trend.
    Require(!formal || samples.Count >= 60, "Insufficient retention samples for formal gate.");
    double? baseline = null;
    double? maximumWindowGrowth = null;
    double? slopeBytesPerHour = null;
    if (samples.Count >= 10)
    {
        var windows = samples
            .Chunk(5)
            .Where(chunk => chunk.Length == 5)
            .Select(chunk => new RetentionSample(
                chunk.Average(sample => sample.Seconds),
                chunk.Select(sample => sample.Bytes).Order().ElementAt(2)
            ))
            .ToArray();
        baseline = windows[0].Bytes;
        maximumWindowGrowth = windows.Max(sample => sample.Bytes) - baseline;
        double meanTime = windows.Average(sample => sample.Seconds);
        double meanBytes = windows.Average(sample => (double)sample.Bytes);
        slopeBytesPerHour =
            windows.Sum(sample => (sample.Seconds - meanTime) * (sample.Bytes - meanBytes))
            / windows.Sum(sample => Math.Pow(sample.Seconds - meanTime, 2))
            * 3600;
        Write(
            new
            {
                Event = "retention-verdict",
                baseline,
                maximumWindowGrowth,
                slopeBytesPerHour,
            }
        );
        Require(
            maximumWindowGrowth <= Math.Max(16 * 1024 * 1024, baseline.Value * 0.25),
            "Retained heap window growth exceeded its fixed budget."
        );
        // Accelerated smoke observes too little wall time to interpret an hourly slope.
        Require(
            !formal || slopeBytesPerHour <= 1024 * 1024,
            "Retained heap trend exceeded 1 MiB/hour."
        );
    }
    Require(operations > 0 && cycle >= 1, "No completed workload.");
    Write(
        new
        {
            Event = "passed",
            formal,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            Operations = operations,
            Cycles = cycle,
            RetentionSamples = samples.Count,
            baseline,
            maximumWindowGrowth,
            slopeBytesPerHour,
        }
    );
    return 0;
}
catch (Exception exception)
{
    Write(
        new
        {
            Event = "failed",
            formal,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            Operations = operations,
            cycle,
            Error = exception.ToString(),
        }
    );
    return 1;
}

async Task<CycleResult> RunCycleAsync(int generation, double duration, long priorOperations)
{
    State[] states = [.. Enumerable.Range(0, 4).Select(mode => new State(mode, generation))];
    WeakReference[] targets = [.. states.SelectMany(state => state.Targets())];
    var clock = Stopwatch.StartNew();
    var random = Enumerable
        .Range(0, 8)
        .Select(worker => new Random(unchecked(20260916 + worker * 397 + generation)))
        .ToArray();
    CycleCounters counters = new();
    int batches = 0;
    int clears = 0;
    double nextProgress = 0;
    try
    {
        while (clock.Elapsed.TotalSeconds < duration)
        {
            Task[] jobs =
            [
                .. Enumerable
                    .Range(0, 8)
                    .Select(worker =>
                        Task.Run(async () =>
                        {
                            for (int operation = 0; operation < 64; operation++)
                            {
                                State state = states[random[worker].Next(4)];
                                int key = random[worker].Next(State.Capacity * 4);
                                int kind = random[worker].Next(100);
                                try
                                {
                                    await state.OperateAsync(key, kind).ConfigureAwait(false);
                                }
                                catch (CacheLoadRejectedException)
                                {
                                    Interlocked.Increment(ref counters.Rejected);
                                }
                            }
                        })
                    ),
            ];
            await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            batches++;
            foreach (State state in states)
                await state.QuiesceAsync(clear: batches % 16 == 0).ConfigureAwait(false);
            if (batches % 16 == 0)
                clears++;
            if (clock.Elapsed.TotalSeconds < nextProgress)
                continue;
            Write(
                new
                {
                    Event = "progress",
                    cycle = generation,
                    ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    Operations = priorOperations + (long)batches * 512,
                    Clears = clears,
                    Rejections = Interlocked.Read(ref counters.Rejected),
                    Caches = states.Select(state => state.Snapshot()).ToArray(),
                }
            );
            nextProgress = clock.Elapsed.TotalSeconds + 10;
        }
        foreach (State state in states)
            await state.QuiesceAsync(clear: true).ConfigureAwait(false);
        Write(
            new
            {
                Event = "cleared",
                cycle = generation,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                Operations = priorOperations + (long)batches * 512,
                Caches = states.Select(state => state.Snapshot()).ToArray(),
            }
        );
    }
    finally
    {
        foreach (State state in states)
            await state.DisposeAsync().ConfigureAwait(false);
    }
    return new CycleResult((long)batches * 512, clears + 1, counters.Rejected, targets);
}

static void Collect()
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false);
    GC.WaitForPendingFinalizers();
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false);
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Write<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
int ReadInt(string name, int fallback, int minimum, int maximum)
{
    int index = Array.IndexOf(args, name);
    int value = index < 0 ? fallback : int.Parse(args[index + 1], CultureInfo.InvariantCulture);
    if (value < minimum || value > maximum)
        throw new ArgumentOutOfRangeException(name);
    return value;
}

internal sealed record RetentionSample(double Seconds, long Bytes);

internal sealed record Retired(int Cycle, WeakReference[] Targets);

internal sealed record CycleResult(
    long Operations,
    int Clears,
    long Rejections,
    WeakReference[] Targets
);

internal sealed class State : IAsyncDisposable
{
    internal const int Capacity = 256;
    private const int LoadLimit = 4;
    private readonly int _mode;
    private readonly int _generation;
    private readonly ICache<int, Payload>? _sync;
    private readonly IAsyncCache<int, Payload>? _async;
    private int _active;
    private int _peak;
    private long _loads;
    private long _listeners;

    internal State(int mode, int generation)
    {
        _mode = mode;
        _generation = generation;
        var builder = CacheBuilder
            .Create<int, Payload>()
            .MaximumSize(Capacity)
            .MaxConcurrentLoads(LoadLimit)
            .MaxPendingLoadKeys(LoadLimit)
            .NotificationCapacity(32)
            .RemovalListener(_ => Interlocked.Increment(ref _listeners));
        if (generation % 2 == 1)
            builder.RecordStatistics();
        if (mode < 2)
            _sync = mode == 0 ? builder.Build() : builder.BuildLoading(Load);
        else
            _async = mode == 2 ? builder.BuildAsync() : builder.BuildAsyncLoading(LoadAsync);
    }

    internal WeakReference[] Targets()
    {
        var value = new Payload(-1, _generation * 4 + _mode);
        if (_sync is not null)
            _sync.Put(-1, value);
        else
            _async!.Put(-1, Task.FromResult(value));
        return
        [
            new WeakReference(this),
            new WeakReference((object?)_sync ?? _async!),
            new WeakReference(value),
        ];
    }

    internal async ValueTask OperateAsync(int key, int kind)
    {
        switch (kind)
        {
            case >= 95 when Notifications.Queued > 64:
                throw new InvalidOperationException(
                    "Listener capture and dispatch queues exceeded their combined bound."
                );
            case < 60:
            {
                Payload value = _mode switch
                {
                    0 => _sync!.GetOrAdd(key, Load),
                    1 => ((ILoadingCache<int, Payload>)_sync!).Get(key),
                    2 => await _async!.GetOrAddAsync(key, LoadAsync).ConfigureAwait(false),
                    _ => await ((IAsyncLoadingCache<int, Payload>)_async!)
                        .GetAsync(key)
                        .ConfigureAwait(false),
                };
                Validate(value, key);
                break;
            }
            case < 80:
            {
                var value = new Payload(key, _generation * 4 + _mode);
                if (_sync is not null)
                    _sync.Put(key, value);
                else
                    _async!.Put(key, Task.FromResult(value));
                break;
            }
            case < 95:
                if (_sync is not null)
                    _sync.Invalidate(key);
                else
                    _async!.Invalidate(key);
                break;
            default:
                if (_sync is not null)
                {
                    if (_sync.TryGet(key, out Payload? value))
                        Validate(value, key);
                }
                else if (_async!.TryGetTask(key, out Task<Payload>? value))
                    Validate(await value.ConfigureAwait(false), key);
                break;
        }
    }

    private void Validate(Payload value, int key)
    {
        if (
            value.Key != key
            || value.Generation != _generation * 4 + _mode
            || value.Bytes[0] != (byte)key
            || value.Bytes[^1] != (byte)~key
        )
            throw new InvalidOperationException("Invalid payload identity or content.");
    }

    private void Enter()
    {
        int active = Interlocked.Increment(ref _active);
        int prior;
        do
        {
            prior = Volatile.Read(ref _peak);
        } while (prior < active && Interlocked.CompareExchange(ref _peak, active, prior) != prior);
        if (active > LoadLimit)
            throw new InvalidOperationException("Loader execution limit exceeded.");
        Interlocked.Increment(ref _loads);
    }

    private Payload Load(int key)
    {
        Enter();
        try
        {
            return new Payload(key, _generation * 4 + _mode);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task<Payload> LoadAsync(int key, CancellationToken token)
    {
        Enter();
        try
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return new Payload(key, _generation * 4 + _mode);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    internal async Task QuiesceAsync(bool clear)
    {
        if (clear)
        {
            if (_sync is not null)
                _sync.Clear();
            else
                _async!.Clear();
        }
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_sync is not null)
                _sync.CleanUp();
            else
                _async!.CleanUp();
            CacheStatistics stats = Statistics;
            CacheNotificationStatistics notifications = Notifications;
            if (
                stats is { InFlightLoads: 0, MaintenanceBacklog: 0, WriteBufferBacklog: 0 }
                && notifications is { Queued: 0, HandlerRunning: false }
                && Volatile.Read(ref _active) == 0
            )
                break;
            if (watch.Elapsed.TotalSeconds > 30)
                throw new TimeoutException("Cache did not drain within 30 seconds.");
            await Task.Yield();
        }
        long count = Count;
        long weight = (_sync?.Policy ?? _async!.Policy).Eviction!.WeightedSize;
        if (
            count > Capacity
            || count < 0
            || weight != count
            || (clear && count != 0)
            || Volatile.Read(ref _peak) > LoadLimit
        )
            throw new InvalidOperationException(
                "Quiescent capacity, weight, clear, or loader bound failed."
            );
        if (Statistics.MaintenanceFaults != 0 || Notifications.HandlerFailures != 0)
            throw new InvalidOperationException("Maintenance or listener failed.");
    }

    private long Count => _sync?.EstimatedCount ?? _async!.EstimatedCount;
    private CacheStatistics Statistics => _sync?.Statistics ?? _async!.Statistics;
    private CacheNotificationStatistics Notifications =>
        _sync?.GetNotificationStatistics() ?? _async!.GetNotificationStatistics();

    internal object Snapshot() =>
        new
        {
            Mode = _mode,
            Generation = _generation,
            StatisticsEnabled = _generation % 2 == 1,
            Residents = Count,
            Weight = (_sync?.Policy ?? _async!.Policy).Eviction!.WeightedSize,
            Active = Volatile.Read(ref _active),
            Peak = Volatile.Read(ref _peak),
            Loads = Interlocked.Read(ref _loads),
            ListenerCalls = Interlocked.Read(ref _listeners),
            Statistics,
            Notifications,
        };

    public async ValueTask DisposeAsync()
    {
        if (_sync is not null)
            _sync.Dispose();
        else
            await _async!.DisposeAsync().ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        while (Notifications.HandlerRunning)
        {
            if (watch.Elapsed.TotalSeconds > 30)
                throw new TimeoutException("Listener did not stop after disposal.");
            await Task.Yield();
        }
        if (Notifications.Queued != 0 || Volatile.Read(ref _active) != 0)
            throw new InvalidOperationException("Disposed cache retained active work.");
    }
}

internal sealed class Payload
{
    internal Payload(int key, int generation)
    {
        Key = key;
        Generation = generation;
        Bytes = new byte[256];
        Bytes[0] = (byte)key;
        Bytes[^1] = (byte)~key;
    }

    internal int Key { get; }
    internal int Generation { get; }
    internal byte[] Bytes { get; }
}

internal sealed class CycleCounters
{
    internal long Rejected;
}
