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
    private readonly BoundedWriteBuffer<PolicyWriteEvent> _pendingWrites;
    private const int MaximumReadDrainPerPass = 256;
    private const int MaximumWriteDrainPerPass = 256;
    private readonly object _policyGate;
    private readonly HashSet<PolicyNode<object>> _nodes = [];
    private long _nextWriteSequence;
    private int _maintenanceSignal;
    private int _writeMaintenanceSignal;

    internal WindowTinyLfuEnginePolicy(
        long maximum,
        int? maximumResidentCount,
        Action<object> evicted,
        Func<bool> requestMaintenance,
        Action? beforeMaintenance,
        Action? beforeMaintenanceSignalClear,
        int readStripeCount,
        int readStripeCapacity,
        int writeBufferCapacity = 256,
        object? coordinationGate = null
    )
    {
        Maximum = maximum;
        _maximumResidentCount = maximumResidentCount;
        _policyGate = coordinationGate ?? new object();
        _policy = CreatePolicy();
        _evicted = evicted;
        _requestMaintenance = requestMaintenance;
        _beforeMaintenance = beforeMaintenance;
        _beforeMaintenanceSignalClear = beforeMaintenanceSignalClear;
        _pendingAccesses = new StripedReadBuffer<EngineEntryToken>(
            readStripeCount,
            readStripeCapacity
        );
        _pendingWrites = new BoundedWriteBuffer<PolicyWriteEvent>(writeBufferCapacity, _policyGate);
    }

    public long Maximum { get; private set; }

    public int ResidentCount
    {
        get
        {
            lock (_policyGate)
            {
                FlushWritesLocked();
                return _policy.ResidentCount;
            }
        }
    }

    public void SetMaximum(long maximum, bool weighted)
    {
        lock (_policyGate)
        {
            FlushWritesLocked();
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
            FlushWritesLocked();
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
                FlushWritesLocked();
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
            EnqueueWriteLocked(
                new PolicyWriteEvent(
                    PolicyWriteKind.Publish,
                    token,
                    weight,
                    NextWriteSequenceLocked()
                )
            );
        }
    }

    public void OnRemove(object? entryToken)
    {
        if (entryToken is not EngineEntryToken token)
        {
            return;
        }

        lock (_policyGate)
        {
            if (token.Node is null && token.PendingPolicyWrites == 0)
            {
                return;
            }

            EnqueueWriteLocked(
                new PolicyWriteEvent(
                    PolicyWriteKind.Remove,
                    token,
                    weight: 0,
                    NextWriteSequenceLocked()
                )
            );
        }
    }

    public void Clear()
    {
        lock (_policyGate)
        {
            _pendingWrites.Clear(ReleasePendingWrite);
            DrainAccessesLocked(MaximumReadDrainPerPass);
            PolicyNode<object>[] nodes = [.. _nodes];
            foreach (PolicyNode<object> node in nodes)
            {
                if (node.Value is EngineEntryToken token && ReferenceEquals(token.Node, node))
                {
                    token.Node = null;
                }

                if (node.IsAlive)
                {
                    _policy.Remove(node);
                }
            }

            _nodes.Clear();
            _policy = CreatePolicy();
            UpdateWriteMaintenanceSignalLocked();
            UpdateMaintenanceSignalLocked();
        }
    }

    public bool CleanUp()
    {
        lock (_policyGate)
        {
            _beforeMaintenance?.Invoke();
            bool writesRemain = DrainWritesLocked(MaximumWriteDrainPerPass);
            DrainAccessesLocked(MaximumReadDrainPerPass);
            Process(_policy.Maintain());
            _beforeMaintenanceSignalClear?.Invoke();
            bool writesPending = UpdateWriteMaintenanceSignalLocked();
            return writesRemain || writesPending || UpdateMaintenanceSignalLocked();
        }
    }

    /// <summary>
    /// Returns whether correctness-relevant policy writes are waiting for the owner.
    /// </summary>
    public bool HasPendingWrites
    {
        get => _pendingWrites.Queued != 0;
    }

    /// <summary>
    /// Drains one bounded snapshot of queued writes.  The caller must invoke this only at an engine
    /// boundary where policy callbacks are allowed to enqueue their own exact removal events; a
    /// caller that requires quiescence should repeat while <see cref="HasPendingWrites"/> is true.
    /// </summary>
    public void FlushWrites()
    {
        lock (_policyGate)
        {
            FlushWritesLocked();
            UpdateWriteMaintenanceSignalLocked();
        }
    }

    /// <summary>
    /// Claims the write maintenance signal when a reliable write is waiting. The caller owns
    /// scheduling after a successful claim; this method never invokes the scheduler.
    /// </summary>
    public bool TryRequestWriteMaintenance() =>
        _pendingWrites.Queued != 0
        && Interlocked.CompareExchange(ref _writeMaintenanceSignal, 1, 0) == 0;

    public WriteBufferStatistics GetWriteBufferStatistics() => _pendingWrites.GetStatistics();

    public ReadBufferStatistics GetReadBufferStatistics() => _pendingAccesses.GetStatistics();

    internal void AssertInvariants()
    {
        lock (_policyGate)
        {
            _policy.AssertInvariants();

            WriteBufferStatistics statistics = _pendingWrites.GetStatistics();
            if (statistics.Queued > statistics.Capacity)
            {
                throw new InvalidOperationException("Policy write buffer exceeds its capacity.");
            }

            List<PolicyWriteEvent> pendingWrites = [];
            if (_pendingWrites.CopyTo(pendingWrites) != statistics.Queued)
            {
                throw new InvalidOperationException(
                    "Policy pending-write transport is inconsistent."
                );
            }

            Dictionary<EngineEntryToken, int> observedPendingWrites = [];
            foreach (PolicyWriteEvent pendingWrite in pendingWrites)
            {
                observedPendingWrites.TryGetValue(pendingWrite.Token, out int count);
                observedPendingWrites[pendingWrite.Token] = count + 1;
                if (pendingWrite.Token.Node is { } node && !_nodes.Contains(node))
                {
                    throw new InvalidOperationException(
                        "A pending-write token points at an unknown policy node."
                    );
                }
            }

            foreach (KeyValuePair<EngineEntryToken, int> pair in observedPendingWrites)
            {
                if (pair.Key.PendingPolicyWrites != pair.Value)
                {
                    throw new InvalidOperationException("Policy pending-write metadata is stale.");
                }
            }

            if (_nodes.Count != _policy.ResidentCount)
            {
                throw new InvalidOperationException(
                    "Policy adapter node accounting is inconsistent."
                );
            }

            foreach (PolicyNode<object> node in _nodes)
            {
                if (node.Value is not EngineEntryToken token)
                {
                    throw new InvalidOperationException("A policy node has an invalid token.");
                }

                int observedCount = observedPendingWrites.TryGetValue(token, out int count)
                    ? count
                    : 0;
                if (
                    !node.IsAlive
                    || !ReferenceEquals(token.Node, node)
                    || token.PendingPolicyWrites != observedCount
                    || node.AppliedPolicyWriteSequence > token.LastPolicyWriteSequence
                )
                {
                    throw new InvalidOperationException("A policy node has a stale identity link.");
                }
            }
        }
    }

    internal void SetWriteSequenceForTesting(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        lock (_policyGate)
        {
            _nextWriteSequence = sequence;
        }
    }

    public void Dispose()
    {
        lock (_policyGate)
        {
            _pendingWrites.Dispose(ReleasePendingWrite);
            foreach (PolicyNode<object> node in _nodes)
            {
                if (node.Value is EngineEntryToken token && ReferenceEquals(token.Node, node))
                {
                    token.Node = null;
                }
            }

            _policy = CreatePolicy();
            _nodes.Clear();
            Volatile.Write(ref _maintenanceSignal, 0);
            Volatile.Write(ref _writeMaintenanceSignal, 0);
        }

        _pendingAccesses.Dispose();
    }

    private void EnqueueWriteLocked(PolicyWriteEvent write)
    {
        while (true)
        {
            if (_pendingWrites.TryEnqueue(write))
            {
                write.Token.PendingPolicyWrites = checked(write.Token.PendingPolicyWrites + 1);
                return;
            }

            // A write may never be dropped.  Removing one older event creates a slot, and
            // applying it before retrying preserves the FIFO order of events already accepted.
            if (!TryDequeueWriteLocked(out PolicyWriteEvent older))
            {
                if (_pendingWrites.IsDisposed)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "The reliable policy write buffer rejected an event without reporting a queued event."
                );
            }

            ApplyWriteLocked(older);
        }
    }

    private static void ReleasePendingWrite(PolicyWriteEvent write)
    {
        if (write.Token.PendingPolicyWrites <= 0)
        {
            throw new InvalidOperationException("A policy write token count underflowed.");
        }

        write.Token.PendingPolicyWrites--;
    }

    private long NextWriteSequenceLocked()
    {
        if (_nextWriteSequence == long.MaxValue)
        {
            // Sequence comparisons are only meaningful while an old sequence remains live. A
            // quiescent flush and reset make the wrap explicit instead of allowing a signed
            // overflow to turn a stale event into a newer one.
            FlushWritesLocked();
            foreach (PolicyNode<object> node in _nodes)
            {
                node.AppliedPolicyWriteSequence = 0;
                if (node.Value is EngineEntryToken token)
                {
                    token.LastPolicyWriteSequence = 0;
                }
            }

            _nextWriteSequence = 0;
        }

        return ++_nextWriteSequence;
    }

    private bool DrainWritesLocked(int budget)
    {
        int drained = 0;
        while (drained < budget && TryDequeueWriteLocked(out PolicyWriteEvent write))
        {
            ApplyWriteLocked(write);
            drained++;
        }

        return _pendingWrites.Queued != 0;
    }

    private void FlushWritesLocked()
    {
        // Process one captured bounded batch. Engine eviction callbacks detach
        // the exact node before invoking RemoveCurrentEntryLocked, so ordinary
        // removal does not enqueue a second policy event. Keeping this batch
        // bounded still protects the boundary if a newer event is concurrently
        // pending for the same token or a custom policy callback re-enters.
        int budget = _pendingWrites.Queued;
        if (budget != 0)
        {
            DrainWritesLocked(budget);
        }
    }

    private bool TryDequeueWriteLocked(out PolicyWriteEvent write)
    {
        if (_pendingWrites.TryDequeue(out write))
        {
            if (write.Token.PendingPolicyWrites <= 0)
            {
                throw new InvalidOperationException("A policy write token count underflowed.");
            }

            write.Token.PendingPolicyWrites--;
            return true;
        }

        write = default;
        return false;
    }

    private void ApplyWriteLocked(PolicyWriteEvent write)
    {
        EngineEntryToken token = write.Token;
        if (token.LastPolicyWriteSequence > write.Sequence)
        {
            // A later exact-token event superseded this one before the owner
            // reached it. In particular, do not transiently admit a publish
            // that is already followed by an invalidate/remove: admission can
            // evict an unrelated live entry even though this token is no
            // longer resident in the authoritative map.
            return;
        }

        if (write.Kind == PolicyWriteKind.Remove)
        {
            ApplyRemoveLocked(token, write.Sequence);
            return;
        }

        PolicyNode<object>? node = token.Node;
        if (node is not null && node.IsAlive)
        {
            if (node.AppliedPolicyWriteSequence > write.Sequence)
            {
                return;
            }

            node.AppliedPolicyWriteSequence = write.Sequence;
            Process(_policy.UpdateWeight(node, write.Weight));
            return;
        }

        node = new PolicyNode<object>(token, write.Weight, token.Hash);
        node.AppliedPolicyWriteSequence = write.Sequence;
        token.Node = node;
        _nodes.Add(node);
        Process(_policy.Add(node));
    }

    private void ApplyRemoveLocked(EngineEntryToken token, long sequence)
    {
        PolicyNode<object>? node = token.Node;
        if (node is null)
        {
            return;
        }

        if (node.AppliedPolicyWriteSequence > sequence)
        {
            return;
        }

        token.Node = null;
        _nodes.Remove(node);
        _policy.Remove(node);
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

    private bool UpdateWriteMaintenanceSignalLocked()
    {
        if (_pendingWrites.Queued != 0)
        {
            Volatile.Write(ref _writeMaintenanceSignal, 1);
            return true;
        }

        Volatile.Write(ref _writeMaintenanceSignal, 0);
        return false;
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
            if (node.AppliedPolicyWriteSequence >= token.LastPolicyWriteSequence)
            {
                _evicted(token.Entry);
            }
        }
    }

    private enum PolicyWriteKind : byte
    {
        Publish,
        Remove,
    }

    private readonly struct PolicyWriteEvent
    {
        internal PolicyWriteEvent(
            PolicyWriteKind kind,
            EngineEntryToken token,
            long weight,
            long sequence
        )
        {
            Kind = kind;
            Token = token;
            Weight = weight;
            Sequence = sequence;
            token.LastPolicyWriteSequence = sequence;
        }

        internal PolicyWriteKind Kind { get; }

        internal EngineEntryToken Token { get; }

        internal long Weight { get; }

        internal long Sequence { get; }
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

        internal int PendingPolicyWrites { get; set; }

        internal long LastPolicyWriteSequence { get; set; }

        private PolicyNode<object>? _node;

        internal PolicyNode<object>? Node
        {
            get => Volatile.Read(ref _node);
            set => Volatile.Write(ref _node, value);
        }
    }
}
