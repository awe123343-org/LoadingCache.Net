using System.Text.Json;
using BitFaster.Caching.Lfu;
using BitFaster.Caching.Scheduler;
using JetBrains.Annotations;
using LoadingCache.Policy;

const int defaultCapacity = 64;
const int defaultRequests = 100_000;
const int defaultSeed = 20260912;
const int windowHistoryInterval = 1_024;

int seed = ReadIntArgument(args, "--seed", defaultSeed);
int capacity = ReadIntArgument(args, "--capacity", defaultCapacity);
int requests = ReadIntArgument(args, "--requests", defaultRequests);
if (capacity <= 0)
{
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
}

if (requests <= 0)
{
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requests);
}

string workload = ReadStringArgument(args, "--workload", "phase-changing");
int[] trace = CreateTrace(seed, requests, workload);
List<SimulationResult> results =
[
    new LruSimulator(capacity).Run(trace),
    new SlruSimulator(capacity).Run(trace),
    new PolicySimulator(capacity, seed, adaptive: false).Run(trace, "fixed-window-tinylfu"),
    new PolicySimulator(capacity, seed, adaptive: true).Run(trace, "adaptive-window-tinylfu"),
];
if (capacity >= 3)
{
    results.Add(new BitFasterSimulator(capacity).Run(trace));
}

var output = new
{
    seed,
    capacity,
    requests,
    workload,
    traceVersion = 2,
    bitFasterVersion = "2.6.1",
    bitFasterAvailable = capacity >= 3,
    bitFasterLimitation = capacity < 3 ? "ConcurrentLfu requires capacity >= 3" : null,
    windowHistoryInterval,
    results,
};

Console.WriteLine(JsonSerializer.Serialize(output, SimulatorJsonOptions.Default));
return;

static int ReadIntArgument(string[] arguments, string name, int fallback)
{
    for (int i = 0; i + 1 < arguments.Length; i++)
    {
        if (string.Equals(arguments[i], name, StringComparison.Ordinal))
        {
            return int.Parse(arguments[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    return fallback;
}

static string ReadStringArgument(string[] arguments, string name, string fallback)
{
    int index = Array.IndexOf(arguments, name);
    if (index < 0)
    {
        return fallback;
    }
    return index + 1 == arguments.Length
        ? throw new ArgumentException("Missing workload argument.", nameof(arguments))
        : arguments[index + 1];
}

static int[] CreateTrace(int seed, int requestCount, string workload)
{
    string[] known = ["phase-changing", "hotset-scan", "scan", "cycle", "uniform", "zipf"];
    if (!known.Contains(workload, StringComparer.Ordinal))
    {
        throw new ArgumentException(
            "Unknown workload. Choose phase-changing, hotset-scan, scan, cycle, uniform, or zipf.",
            nameof(workload)
        );
    }
    Random random = new(seed);
    int[] trace = new int[requestCount];
    const int hotSetSize = 128;
    double[] zipfCdf = new double[2048];
    double total = 0;
    for (int index = 0; index < zipfCdf.Length; index++)
    {
        total += 1 / Math.Pow(index + 1, 1.1);
        zipfCdf[index] = total;
    }
    for (int index = 0; index < zipfCdf.Length; index++)
    {
        zipfCdf[index] /= total;
    }
    int scanKey = 10_000;

    for (int i = 0; i < trace.Length; i++)
    {
        int phase = (int)((long)i * 4 / trace.Length);
        int rank = Array.BinarySearch(zipfCdf, random.NextDouble());
        trace[i] = workload switch
        {
            "scan" => i,
            "cycle" => i % 512,
            "uniform" => random.Next(2048),
            "zipf" => rank >= 0 ? rank : ~rank,
            "hotset-scan" => i % 10 == 0 ? scanKey++ : random.Next(hotSetSize),
            _ => phase switch
            {
                0 => i % 10 == 0 ? scanKey++ : random.Next(hotSetSize),
                1 => i % 256,
                2 => i % 10 == 0 ? scanKey++ : 512 + random.Next(hotSetSize),
                _ => scanKey++,
            },
        };
    }

    return trace;
}

internal sealed class BitFasterSimulator
{
    private readonly int _capacity;

    internal BitFasterSimulator(int capacity) => _capacity = capacity;

    public SimulationResult Run(int[] trace)
    {
        var cache = new ConcurrentLfu<int, int>(
            1,
            _capacity,
            new ForegroundScheduler(),
            EqualityComparer<int>.Default
        );
        long hits = 0;
        foreach (int key in trace)
        {
            if (cache.TryGet(key, out _))
            {
                hits++;
            }
            else
            {
                cache.AddOrUpdate(key, key);
            }
            cache.DoMaintenance();
        }
        return new SimulationResult(
            "bitfaster-2.6.1-foreground-quiescent",
            trace.Length,
            hits,
            trace.Length - hits,
            (double)hits / trace.Length
        );
    }
}

internal static class SimulatorJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new() { WriteIndented = true };
}

internal readonly record struct SimulationResult(
    [property: UsedImplicitly] string Policy,
    [property: UsedImplicitly] int Requests,
    [property: UsedImplicitly] long Hits,
    [property: UsedImplicitly] long Misses,
    [property: UsedImplicitly] double HitRate,
    [property: UsedImplicitly] IReadOnlyList<long>? WindowMaximumHistory = null
);

internal sealed class LruSimulator
{
    private readonly int _capacity;

    internal LruSimulator(int capacity)
    {
        _capacity = capacity;
    }

    public SimulationResult Run(int[] trace)
    {
        Dictionary<int, LinkedListNode<int>> map = new();
        LinkedList<int> order = new();
        long hits = 0;

        foreach (int key in trace)
        {
            if (map.Remove(key, out LinkedListNode<int>? node))
            {
                hits++;
                order.Remove(node);
                order.AddLast(node);
                map[key] = node;
                continue;
            }

            LinkedListNode<int> added = order.AddLast(key);
            map[key] = added;
            if (map.Count <= _capacity)
            {
                continue;
            }

            LinkedListNode<int> oldest = order.First!;
            order.RemoveFirst();
            map.Remove(oldest.Value);
        }

        return Result("lru", trace.Length, hits);
    }

    private static SimulationResult Result(string policy, int requests, long hits)
    {
        long misses = requests - hits;
        return new SimulationResult(policy, requests, hits, misses, (double)hits / requests);
    }
}

internal sealed class SlruSimulator
{
    private readonly int _capacity;
    private readonly int _protectedCapacity;

    internal SlruSimulator(int capacity)
    {
        _capacity = capacity;
        _protectedCapacity = Math.Max(0, capacity * 4 / 5);
    }

    public SimulationResult Run(int[] trace)
    {
        Dictionary<int, SlruEntry> map = new();
        LinkedList<int> probation = new();
        LinkedList<int> protectedSpace = new();
        long hits = 0;

        foreach (int key in trace)
        {
            if (map.TryGetValue(key, out SlruEntry? entry))
            {
                hits++;
                if (entry.IsProtected)
                {
                    protectedSpace.Remove(entry.Node);
                    protectedSpace.AddLast(entry.Node);
                }
                else
                {
                    probation.Remove(entry.Node);
                    entry.IsProtected = true;
                    protectedSpace.AddLast(entry.Node);
                    if (protectedSpace.Count > _protectedCapacity)
                    {
                        LinkedListNode<int> demoted = protectedSpace.First!;
                        protectedSpace.RemoveFirst();
                        SlruEntry demotedEntry = map[demoted.Value];
                        demotedEntry.IsProtected = false;
                        probation.AddLast(demoted);
                    }
                }

                continue;
            }

            LinkedListNode<int> added = probation.AddLast(key);
            map[key] = new SlruEntry(added);
            while (map.Count > _capacity)
            {
                LinkedListNode<int>? victim = probation.First;
                if (victim is null)
                {
                    victim = protectedSpace.First;
                    if (victim is null)
                    {
                        break;
                    }

                    protectedSpace.RemoveFirst();
                }
                else
                {
                    probation.RemoveFirst();
                }

                map.Remove(victim.Value);
            }
        }

        long misses = trace.Length - hits;
        return new SimulationResult(
            "slru",
            trace.Length,
            hits,
            misses,
            (double)hits / trace.Length
        );
    }

    private sealed class SlruEntry
    {
        internal SlruEntry(LinkedListNode<int> node)
        {
            Node = node;
        }

        internal LinkedListNode<int> Node { get; }

        internal bool IsProtected { get; set; }
    }
}

internal sealed class PolicySimulator
{
    private const int WindowHistoryInterval = 1_024;
    private readonly WindowTinyLfuPolicy<int> _policy;
    private readonly Dictionary<int, PolicyNode<int>> _entries = new();

    internal PolicySimulator(int capacity, int seed, bool adaptive)
    {
        _policy = new WindowTinyLfuPolicy<int>(capacity, unchecked((uint)seed), adaptive, capacity);
    }

    internal SimulationResult Run(int[] trace, string policyName)
    {
        long hits = 0;
        List<long> windowHistory = [];
        for (int index = 0; index < trace.Length; index++)
        {
            int key = trace[index];
            if (_entries.TryGetValue(key, out PolicyNode<int>? current) && current.IsAlive)
            {
                hits++;
                _ = _policy.RecordAccess(current);
                RemoveEvicted(_policy.Maintain());
                if ((index + 1) % WindowHistoryInterval == 0 || index == trace.Length - 1)
                {
                    windowHistory.Add(_policy.WindowMaximum);
                }

                continue;
            }

            PolicyNode<int> node = new(key, 1, Hash(key));
            IReadOnlyList<PolicyNode<int>> evicted = _policy.Add(node);
            RemoveEvicted(evicted);
            if (node.IsAlive)
            {
                _entries[key] = node;
            }

            RemoveEvicted(_policy.Maintain());

            if ((index + 1) % WindowHistoryInterval == 0 || index == trace.Length - 1)
            {
                windowHistory.Add(_policy.WindowMaximum);
            }
        }

        long misses = trace.Length - hits;
        return new SimulationResult(
            policyName,
            trace.Length,
            hits,
            misses,
            (double)hits / trace.Length,
            windowHistory
        );
    }

    private void RemoveEvicted(IReadOnlyList<PolicyNode<int>> evicted)
    {
        foreach (PolicyNode<int> node in evicted)
        {
            if (
                _entries.TryGetValue(node.Value, out PolicyNode<int>? mapped)
                && ReferenceEquals(mapped, node)
            )
            {
                _entries.Remove(node.Value);
            }
        }
    }

    private static uint Hash(int value)
    {
        return unchecked((uint)(value * 0x9E3779B9));
    }
}
