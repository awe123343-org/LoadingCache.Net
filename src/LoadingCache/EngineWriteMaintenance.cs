using LoadingCache.Diagnostics;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private WriteMaintenanceBackstop? _writeMaintenanceBackstop;

    private void DeferPolicyWriteMaintenance()
    {
        WriteMaintenanceBackstop? backstop = Volatile.Read(ref _writeMaintenanceBackstop);
        if (backstop is null)
        {
            var created = new WriteMaintenanceBackstop(this, _timeProvider);
            backstop =
                Interlocked.CompareExchange(ref _writeMaintenanceBackstop, created, null)
                ?? created;
        }

        // Disposal may have observed a null holder before this producer installed it.
        if (Volatile.Read(ref _disposed) != 0)
        {
            backstop.Dispose();
            return;
        }

        if (!backstop.TryArm())
        {
            // Timer-provider failure cannot make reliable progress depend on later traffic.
            DrainRejectedPolicyWrites();
        }
    }

    private sealed class WriteMaintenanceBackstop(
        CacheEngine<TKey, TValue> owner,
        TimeProvider timeProvider
    ) : IDisposable
    {
        private const int Idle = 0;
        private const int Armed = 1;
        private const int Disabled = 2;
        private const int Disposed = 3;
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1);
        private readonly WeakReference<CacheEngine<TKey, TValue>> _owner = new(owner);
        private ITimer? _timer;
        private int _state;

        internal bool TryArm()
        {
            int state = Volatile.Read(ref _state);
            if (state == Armed || state == Disposed)
                return true;
            if (state == Disabled)
                return false;
            state = Interlocked.CompareExchange(ref _state, Armed, Idle);
            if (state != Idle)
                return state != Disabled;

            try
            {
                ITimer? timer = Volatile.Read(ref _timer);
                if (timer is null)
                {
                    // Only the idle-to-armed winner creates a timer. Start it inactive so an
                    // eager provider callback before installation cannot consume this arm.
                    timer = CreateTimer();
                    Volatile.Write(ref _timer, timer);
                    if (Volatile.Read(ref _state) == Disposed)
                    {
                        DisposeTimer(Interlocked.Exchange(ref _timer, null));
                        return true;
                    }
                }

                // Provider code and a synchronous callback both run outside cache locks.
                // Later producers observe Armed and never postpone this deadline.
                if (timer.Change(Delay, Timeout.InfiniteTimeSpan))
                    return true;
            }
            catch (Exception)
            {
                // The caller performs the existing bounded write fallback.
            }

            int observed;
            do
            {
                observed = Volatile.Read(ref _state);
                if (observed == Disposed)
                    return true;
            } while (Interlocked.CompareExchange(ref _state, Disabled, observed) != observed);
            DisposeTimer(Interlocked.Exchange(ref _timer, null));
            return false;
        }

        private ITimer CreateTimer()
        {
            if (ExecutionContext.IsFlowSuppressed())
                return Create();

            using (ExecutionContext.SuppressFlow())
                return Create();

            ITimer Create() =>
                timeProvider.CreateTimer(
                    static state => ((WriteMaintenanceBackstop)state!).OnTimer(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan
                );
        }

        private void OnTimer()
        {
            if (
                Volatile.Read(ref _timer) is null
                || Interlocked.CompareExchange(ref _state, Idle, Armed) != Armed
            )
                return;

            // Release before requesting: a racing new tail can arm its own deadline. An old
            // callback after Clear may drain current work early, never revive old identities.
            if (_owner.TryGetTarget(out CacheEngine<TKey, TValue>? cache))
                cache.CompletePolicyWriteBoundary(force: true);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _state, Disposed);
            DisposeTimer(Interlocked.Exchange(ref _timer, null));
        }

        private void DisposeTimer(ITimer? timer)
        {
            try
            {
                timer?.Dispose();
            }
            catch (Exception)
            {
                // A provider cleanup failure is secondary, as for flight timeout timers.
                if (_owner.TryGetTarget(out CacheEngine<TKey, TValue>? cache))
                    cache.RecordCounter(CacheCounterKind.TimerDisposalFailures);
            }
        }
    }
}
