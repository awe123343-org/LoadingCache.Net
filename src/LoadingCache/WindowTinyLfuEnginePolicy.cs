using System.Security.Cryptography;
using LoadingCache.Maintenance;
using LoadingCache.Policy;

namespace LoadingCache;

/// <summary>
/// Adapter for the policy module.  The engine owns the authoritative map; the
/// policy only owns identity-bearing resident nodes and reports evictions back
/// through exact <see cref="CacheEngine{TKey, TValue}.Entry"/> tokens.
/// </summary>
internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposable
{
    private int? _maximumResidentCount;
    private readonly uint _policySeed = CreatePolicySeed();
    private WindowTinyLfuPolicy<object> _policy;
    private readonly Action<object> _evicted;
    private readonly Func<bool> _requestMaintenance;
    private readonly Action? _beforeMaintenance;
    private readonly Action? _beforeMaintenanceSignalClear;
    private readonly StripedReadBuffer<EngineEntryToken> _pendingAccesses;
    private const int MaximumReadDrainPerPass = 256;
    private readonly object _policyGate = new();
    private readonly HashSet<PolicyNode<object>> _nodes = [];
    private int _maintenanceSignal;

    internal WindowTinyLfuEnginePolicy(
        long maximum,
        int? maximumResidentCount,
        Action<object> evicted,
        Func<bool> requestMaintenance,
        Action? beforeMaintenance,
        Action? beforeMaintenanceSignalClear,
        int readStripeCount,
        int readStripeCapacity
    )
    {
        Maximum = maximum;
        _maximumResidentCount = maximumResidentCount;
        _policy = CreatePolicy();
        _evicted = evicted;
        _requestMaintenance = requestMaintenance;
        _beforeMaintenance = beforeMaintenance;
        _beforeMaintenanceSignalClear = beforeMaintenanceSignalClear;
        _pendingAccesses = new StripedReadBuffer<EngineEntryToken>(
            readStripeCount,
            readStripeCapacity
        );
    }

    public long Maximum { get; private set; }

    public int ResidentCount
    {
        get
        {
            lock (_policyGate)
            {
                return _policy.ResidentCount;
            }
        }
    }

    public void SetMaximum(long maximum, bool weighted)
    {
        lock (_policyGate)
        {
            DrainAccessesLocked(MaximumReadDrainPerPass);
            if (!weighted)
            {
                _maximumResidentCount = checked((int)maximum);
                Process(_policy.SetMaximumCount(_maximumResidentCount));
            }

            Process(_policy.SetMaximum(maximum));
            Maximum = maximum;
        }
    }

    public IReadOnlyList<object> Snapshot(bool hottest, int limit)
    {
        lock (_policyGate)
        {
            var nodes = _policy.Snapshot(hottest, limit);
            var entries = new List<object>(nodes.Count);
            foreach (var node in nodes)
            {
                if (node.Value is EngineEntryToken token)
                {
                    entries.Add(token.Entry);
                }
            }
            return entries;
        }
    }

    public long WeightedSize
    {
        get
        {
            lock (_policyGate)
            {
                return _policy.WeightedSize;
            }
        }
    }

    public void OnAccess(object? entryToken)
    {
        if (entryToken is not EngineEntryToken token)
        {
            return;
        }

        // A full stripe is allowed to drop this best-effort observation, but
        // it must not suppress recovery when a rejected scheduler left the
        // signal clear and an older event is still queued.
        _pendingAccesses.TryEnqueue(token);

        // One signal covers the current bounded batch.  Producers do not take
        // the policy lock or ask the coordinator on every resident hit.
        if (
            Volatile.Read(ref _maintenanceSignal) != 0
            || Interlocked.CompareExchange(ref _maintenanceSignal, 1, 0) != 0
        )
        {
            return;
        }

        if (!_requestMaintenance())
        {
            return;
        }

        // The bounded synchronous fallback could not schedule the
        // remaining best-effort reads. Let a future producer retry.
        Volatile.Write(ref _maintenanceSignal, 0);
    }

    public void OnPublish(object? entryToken, long weight)
    {
        if (entryToken is not EngineEntryToken token)
        {
            return;
        }

        lock (_policyGate)
        {
            DrainAccessesLocked(MaximumReadDrainPerPass);
            if (token.Node is not null && token.Node.IsAlive)
            {
                IReadOnlyList<PolicyNode<object>> changed = _policy.UpdateWeight(
                    token.Node,
                    weight
                );
                Process(changed);
                return;
            }

            var node = new PolicyNode<object>(token, weight, token.Hash);
            token.Node = node;
            _nodes.Add(node);
            Process(_policy.Add(node));
        }
    }

    public void OnRemove(object? entryToken)
    {
        if (entryToken is not EngineEntryToken token || token.Node is null)
        {
            return;
        }

        lock (_policyGate)
        {
            DrainAccessesLocked(MaximumReadDrainPerPass);
            PolicyNode<object>? node = token.Node;
            if (node is null)
            {
                return;
            }

            token.Node = null;
            _nodes.Remove(node);
            _policy.Remove(node);
        }
    }

    public void Clear()
    {
        lock (_policyGate)
        {
            DrainAccessesLocked(MaximumReadDrainPerPass);
            PolicyNode<object>[] nodes = [.. _nodes];
            foreach (PolicyNode<object> node in nodes)
            {
                if (node.IsAlive)
                {
                    _policy.Remove(node);
                }
            }

            _nodes.Clear();
            _policy = CreatePolicy();
        }
    }

    public bool CleanUp()
    {
        lock (_policyGate)
        {
            _beforeMaintenance?.Invoke();
            DrainAccessesLocked(MaximumReadDrainPerPass);
            Process(_policy.Maintain());
            _beforeMaintenanceSignalClear?.Invoke();
            return UpdateMaintenanceSignalLocked();
        }
    }

    public ReadBufferStatistics GetReadBufferStatistics() => _pendingAccesses.GetStatistics();

    public void Dispose()
    {
        _pendingAccesses.Dispose();
    }

    private void DrainAccessesLocked(int budget)
    {
        int drained = 0;
        while (drained < budget && _pendingAccesses.TryRead(out EngineEntryToken token))
        {
            PolicyNode<object>? node = token.Node;
            if (node is not null && node.IsAlive)
            {
                _policy.RecordAccess(node);
            }

            drained++;
        }
    }

    private bool UpdateMaintenanceSignalLocked()
    {
        if (_pendingAccesses.GetStatistics().Queued != 0)
        {
            Volatile.Write(ref _maintenanceSignal, 1);
            return true;
        }

        // Clear the signal, then re-check the transport. A producer that races
        // this handoff either observes the cleared signal and requests a worker
        // or is observed by this second check and keeps the worker alive.
        Volatile.Write(ref _maintenanceSignal, 0);
        if (_pendingAccesses.GetStatistics().Queued == 0)
        {
            return false;
        }

        Volatile.Write(ref _maintenanceSignal, 1);
        return true;
    }

    private WindowTinyLfuPolicy<object> CreatePolicy()
    {
        return new WindowTinyLfuPolicy<object>(
            Maximum,
            seed: _policySeed,
            maximumCount: _maximumResidentCount
        );
    }

    private static uint CreatePolicySeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private void Process(IReadOnlyList<PolicyNode<object>> changed)
    {
        foreach (PolicyNode<object> node in changed)
        {
            if (node.Value is not EngineEntryToken token)
            {
                continue;
            }

            if (ReferenceEquals(token.Node, node))
            {
                token.Node = null;
            }

            _nodes.Remove(node);
            _evicted(token.Entry);
        }
    }

    internal sealed class EngineEntryToken
    {
        internal EngineEntryToken(object entry, uint hash)
        {
            Entry = entry;
            Hash = hash;
        }

        internal object Entry { get; }

        internal uint Hash { get; }

        private PolicyNode<object>? _node;

        internal PolicyNode<object>? Node
        {
            get => Volatile.Read(ref _node);
            set => Volatile.Write(ref _node, value);
        }
    }
}
