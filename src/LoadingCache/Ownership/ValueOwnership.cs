using System.Runtime.CompilerServices;

namespace LoadingCache.Ownership;

/// <summary>
/// Tracks cache ownership and caller leases for disposable reference values.
/// </summary>
/// <typeparam name="TValue">The reference type being owned.</typeparam>
/// <remarks>
/// This is deliberately an internal primitive. A cache integration creates one
/// token per exact entry generation, calls <see cref="Retire"/> when that
/// generation leaves the authoritative mapping, and acquires a lease before
/// exposing a value to a caller. Retiring by key or by value is intentionally
/// not supported because either operation can cross an ABA boundary.
///
/// The active-state limit covers resident values, retired values with live
/// leases, and values whose disposer is still running. A non-cooperative
/// disposer therefore consumes its slot until it actually completes.
/// </remarks>
internal sealed class ValueOwnership<TValue> : IDisposable
    where TValue : class
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<TValue, LeaseState> _states = new();
    private readonly ConditionalWeakTable<TValue, TerminalMarker> _terminalStates = new();
    private readonly HashSet<LeaseState> _activeStates = [];
    private readonly Action<TValue>? _disposeValue;
    private readonly Func<TValue, ValueTask>? _disposeValueAsync;
    private readonly Action<Exception>? _observeDisposalFailure;
    private readonly Func<Action, bool> _scheduleDisposal;
    private readonly int _maximumActiveValues;
    private long _pendingDisposals;
    private Exception? _lastDisposalError;
    private bool _disposed;

    internal ValueOwnership(
        int maximumActiveValues,
        Action<TValue> disposeValue,
        Action<Exception>? observeDisposalFailure = null,
        Func<Action, bool>? scheduleDisposal = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActiveValues);
        ArgumentNullException.ThrowIfNull(disposeValue);
        _maximumActiveValues = maximumActiveValues;
        _disposeValue = disposeValue;
        _observeDisposalFailure = observeDisposalFailure;
        _scheduleDisposal = scheduleDisposal ?? ScheduleOnThreadPool;
    }

    internal ValueOwnership(
        int maximumActiveValues,
        Func<TValue, ValueTask> disposeValueAsync,
        Action<Exception>? observeDisposalFailure = null,
        Func<Action, bool>? scheduleDisposal = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActiveValues);
        ArgumentNullException.ThrowIfNull(disposeValueAsync);
        _maximumActiveValues = maximumActiveValues;
        _disposeValueAsync = disposeValueAsync;
        _observeDisposalFailure = observeDisposalFailure;
        _scheduleDisposal = scheduleDisposal ?? ScheduleOnThreadPool;
    }

    /// <summary>Represents cache ownership of one exact entry generation.</summary>
    internal sealed class Token
    {
        private int _retired;

        private Token(ValueOwnership<TValue> owner, LeaseState state)
        {
            Owner = owner;
            State = state;
        }

        private ValueOwnership<TValue> Owner { get; }

        private LeaseState State { get; }

        internal static Token Create(ValueOwnership<TValue> owner, LeaseState state) =>
            new(owner, state);

        internal bool TryRetire() => Interlocked.Exchange(ref _retired, 1) == 0;

        internal bool IsRetired => Volatile.Read(ref _retired) != 0;

        internal LeaseState LeaseState => State;

        internal ValueOwnership<TValue> Ownership => Owner;
    }

    /// <summary>Represents one shared value identity.</summary>
    internal sealed class LeaseState
    {
        internal LeaseState(TValue value)
        {
            Value = value;
        }

        internal TValue? Value { get; private set; }

        internal int CacheReferences;

        internal int LeaseReferences;

        internal int DisposalStarted;

        internal int DisposalCompleted;

        internal bool DisposalPending;

        internal bool DisposalScheduled;

        internal void ClearValue()
        {
            Value = null;
        }
    }

    /// <summary>One atomic ownership/lease release operation.</summary>
    internal sealed class LeaseRegistration
    {
        internal LeaseRegistration(ValueOwnership<TValue> owner, LeaseState state)
        {
            Owner = owner;
            State = state;
        }

        internal ValueOwnership<TValue> Owner { get; }

        internal LeaseState State { get; }
    }

    /// <summary>
    /// Registers one value as resident and returns an exact generation token.
    /// </summary>
    internal Token Publish(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            LeaseState state = PublishLocked(value);
            return Token.Create(this, state);
        }
    }

    /// <summary>
    /// Publishes a generation and acquires a lease in one ownership critical
    /// section. The lease pins the value while the engine computes metadata.
    /// </summary>
    internal (Token Token, CacheLease<TValue> Lease) PublishAndAcquire(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            LeaseState state = PublishLocked(value);
            checked
            {
                state.LeaseReferences++;
            }

            Token token = Token.Create(this, state);
            return (token, new CacheLease<TValue>(new LeaseRegistration(this, state)));
        }
    }

    /// <summary>Acquires a caller lease for an exact entry generation.</summary>
    internal CacheLease<TValue> Acquire(Token token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!ReferenceEquals(token.Ownership, this))
        {
            throw new ArgumentException(
                "The token belongs to another ownership registry.",
                nameof(token)
            );
        }

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            LeaseState state = token.LeaseState;
            if (
                token.IsRetired
                || state.Value is null
                || !_states.TryGetValue(state.Value, out LeaseState? current)
                || !ReferenceEquals(current, state)
                || Volatile.Read(ref state.DisposalStarted) != 0
            )
            {
                ObjectDisposedException.ThrowIf(true, nameof(CacheLease<TValue>));
            }
            checked
            {
                state.LeaseReferences++;
            }

            return new CacheLease<TValue>(new LeaseRegistration(this, state));
        }
    }

    /// <summary>
    /// Retires one exact entry generation. Disposal is scheduled only after
    /// all leases and aliases have released the shared value.
    /// </summary>
    internal void Retire(Token token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!ReferenceEquals(token.Ownership, this))
        {
            throw new ArgumentException(
                "The token belongs to another ownership registry.",
                nameof(token)
            );
        }

        if (!token.TryRetire())
        {
            return;
        }

        LeaseState? stateToDispose = null;
        lock (_gate)
        {
            LeaseState state = token.LeaseState;
            if (state.Value is null)
            {
                return;
            }
            if (
                !_states.TryGetValue(state.Value, out LeaseState? current)
                || !ReferenceEquals(current, state)
                || state.CacheReferences <= 0
            )
            {
                return;
            }

            state.CacheReferences--;
            if (state is { CacheReferences: 0, LeaseReferences: 0 })
            {
                state.DisposalStarted = 1;
                stateToDispose = state;
            }
        }

        QueueDisposal(stateToDispose);
    }

    internal void ReleaseLease(LeaseState state)
    {
        LeaseState? stateToDispose = null;
        lock (_gate)
        {
            if (state.Value is null)
            {
                return;
            }
            if (
                !_states.TryGetValue(state.Value, out LeaseState? current)
                || !ReferenceEquals(current, state)
            )
            {
                return;
            }

            if (state.LeaseReferences <= 0)
            {
                throw new InvalidOperationException("The cache lease was released more than once.");
            }

            state.LeaseReferences--;
            if (state is { CacheReferences: 0, LeaseReferences: 0 })
            {
                state.DisposalStarted = 1;
                stateToDispose = state;
            }
        }

        QueueDisposal(stateToDispose);
    }

    public void Dispose()
    {
        List<LeaseState>? statesToDispose = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (LeaseState state in _activeStates)
            {
                state.CacheReferences = 0;
                if (state is not { LeaseReferences: 0, DisposalStarted: 0 })
                {
                    continue;
                }

                state.DisposalStarted = 1;
                (statesToDispose ??= []).Add(state);
            }
        }

        if (statesToDispose is null)
        {
            return;
        }

        foreach (LeaseState state in statesToDispose)
        {
            QueueDisposal(state);
        }
    }

    private void QueueDisposal(LeaseState? state)
    {
        if (state is null)
        {
            return;
        }

        lock (_gate)
        {
            if (state.DisposalPending)
                return;
            state.DisposalPending = true;
            Interlocked.Increment(ref _pendingDisposals);
        }
        TryScheduleDisposal(state);
    }

    // The backlog is bounded by MaximumActiveValues and preserves ownership
    // when a scheduler rejects work. No user disposer is executed inline.
    internal int RetryPendingDisposals()
    {
        LeaseState[] pending;
        lock (_gate)
        {
            pending =
            [
                .. _activeStates.Where(static state =>
                    state
                        is { DisposalPending: true, DisposalScheduled: false, DisposalCompleted: 0 }
                ),
            ];
        }
        int accepted = 0;
        foreach (LeaseState state in pending)
        {
            if (TryScheduleDisposal(state))
                accepted++;
        }
        return accepted;
    }

    private bool TryScheduleDisposal(LeaseState state)
    {
        lock (_gate)
        {
            if (state.DisposalScheduled || state.DisposalCompleted != 0)
                return false;
            state.DisposalScheduled = true;
        }
        try
        {
            if (_scheduleDisposal(() => _ = RunDisposal(state)))
            {
                return true;
            }
            RecordDisposalFailure(
                new InvalidOperationException(
                    "The value-disposal scheduler rejected work. Call RetryPendingDisposals to retry."
                )
            );
        }
        catch (Exception exception)
        {
            RecordDisposalFailure(exception);
        }
        lock (_gate)
        {
            state.DisposalScheduled = false;
        }
        return false;
    }

    private static bool ScheduleOnThreadPool(Action work) =>
        ThreadPool.UnsafeQueueUserWorkItem(static action => action(), work, preferLocal: false);

    private async Task RunDisposal(LeaseState state)
    {
        try
        {
            if (_disposeValue is not null)
            {
                _disposeValue(state.Value!);
            }
            else
            {
                await _disposeValueAsync!(state.Value!).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ObserveDisposalFailure(exception);
        }
        finally
        {
            CompleteDisposal(state);
        }
    }

    private void CompleteDisposal(LeaseState state)
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref state.DisposalCompleted, 1) != 0)
            {
                return;
            }

            TValue value = state.Value!;
            _terminalStates.GetValue(value, static _ => new TerminalMarker());
            _states.Remove(value);
            _activeStates.Remove(state);
            state.ClearValue();
            Interlocked.Decrement(ref _pendingDisposals);
        }
    }

    private void ObserveDisposalFailure(Exception exception)
    {
        RecordDisposalFailure(exception);
        try
        {
            _observeDisposalFailure?.Invoke(exception);
        }
        catch
        {
            // Disposal diagnostics are best effort and cannot affect cache state.
        }
    }

    private void RecordDisposalFailure(Exception exception) =>
        Volatile.Write(ref _lastDisposalError, exception);

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ValueOwnership<TValue>));
    }

    internal OwnedCacheDisposalStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new OwnedCacheDisposalStatistics(
                _activeStates.Count,
                Volatile.Read(ref _pendingDisposals),
                Volatile.Read(ref _lastDisposalError)
            );
        }
    }

    private LeaseState PublishLocked(TValue value)
    {
        ThrowIfDisposedLocked();
        if (_terminalStates.TryGetValue(value, out _))
        {
            throw new ValueOwnershipCapacityException(
                "A value that has been disposed cannot be republished."
            );
        }

        if (_states.TryGetValue(value, out LeaseState? existing))
        {
            if (
                Volatile.Read(ref existing.DisposalStarted) != 0
                || Volatile.Read(ref existing.DisposalCompleted) != 0
            )
            {
                throw new ValueOwnershipCapacityException(
                    "The value is still being disposed and cannot be republished."
                );
            }
            checked
            {
                existing.CacheReferences++;
            }
            return existing;
        }

        if (_activeStates.Count >= _maximumActiveValues)
        {
            throw new ValueOwnershipCapacityException(
                "The owned cache has reached its active value bound."
            );
        }

        var state = new LeaseState(value) { CacheReferences = 1 };
        _states.Add(value, state);
        _activeStates.Add(state);
        return state;
    }

    private sealed class TerminalMarker;
}

internal sealed class ValueOwnershipCapacityException : InvalidOperationException
{
    internal ValueOwnershipCapacityException(string message)
        : base(message) { }
}
