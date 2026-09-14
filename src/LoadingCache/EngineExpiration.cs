using LoadingCache.Expiration;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private TimeSpan? GetExpireAfterAccess() => GetConfiguredDuration(ref _expireAfterAccessTicks);

    private TimeSpan? GetExpireAfterWrite() => GetConfiguredDuration(ref _expireAfterWriteTicks);

    private TimeSpan? GetRefreshAfterWrite() => GetConfiguredDuration(ref _refreshAfterWriteTicks);

    private void SetExpireAfterAccess(TimeSpan duration)
    {
        SetFixedDuration(ref _expireAfterAccessTicks, duration);
    }

    private void SetExpireAfterWrite(TimeSpan duration)
    {
        SetFixedDuration(ref _expireAfterWriteTicks, duration);
    }

    private void SetRefreshAfterWrite(TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        ThrowIfDisposed();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            Volatile.Write(ref _refreshAfterWriteTicks, duration.Ticks);
        }
    }

    private void SetFixedDuration(ref long target, TimeSpan duration)
    {
        ValidateDuration(duration, nameof(duration));
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            Volatile.Write(ref target, duration.Ticks);
            RescheduleAllExpirationNodesLocked();
        }

        evictionScope.Dispatch();
        RequestExpirationTimer();
    }

    private TimeSpan? GetExpiresAfter(TKey key, ExpirationKind kind)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ThrowIfDisposed();
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        lock (entry.Sync)
        {
            if (!Volatile.Read(ref entry.IsReady))
            {
                return null;
            }

            if ((_weakKeys && !entry.TryGetKey(out _)) || !entry.TryGetValue(out _))
            {
                return null;
            }

            long now = _timeProvider.GetTimestamp();
            return IsExpired(entry, now) ? TimeSpan.Zero : GetRemainingDuration(entry, now, kind);
        }
    }

    private TimeSpan? GetAgeOf(TKey key, ExpirationKind kind)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ThrowIfDisposed();
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        lock (entry.Sync)
        {
            if (!Volatile.Read(ref entry.IsReady))
            {
                return null;
            }

            if ((_weakKeys && !entry.TryGetKey(out _)) || !entry.TryGetValue(out _))
            {
                return null;
            }

            long now = _timeProvider.GetTimestamp();
            long timestamp = kind switch
            {
                ExpirationKind.Access => entry.AccessTimestamp,
                ExpirationKind.Write => entry.WriteTimestamp,
                ExpirationKind.Refresh => entry.WriteTimestamp,
                _ => entry.VariableTimestamp,
            };
            TimeSpan age = _timeProvider.GetElapsedTime(timestamp, now);
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
    }

    private bool SetVariableExpiresAfter(TKey key, TimeSpan duration)
    {
        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }
        ValidateVariableDuration(duration, nameof(duration));
        ThrowIfDisposed();
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        bool reschedule = false;
        bool result;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (!_entries.TryGetValue(key, out Entry? entry) || !Volatile.Read(ref entry.IsReady))
            {
                result = false;
            }
            else
            {
                long now = _timeProvider.GetTimestamp();
                bool expired;
                bool collected;
                lock (entry.Sync)
                {
                    bool ready = Volatile.Read(ref entry.IsReady);
                    collected =
                        ready
                        && (
                            (_weakKeys && !entry.TryGetKey(out _))
                            || (_weakValues && !entry.TryGetValue(out _))
                        );
                    expired = !ready || (!collected && IsExpired(entry, now));
                    if (expired || collected)
                    {
                        // The cleanup below runs after the entry snapshot lock so
                        // an ongoing refresh can retain its exact flight owner.
                    }
                    else
                    {
                        entry.VariableTimestamp = now;
                        entry.VariableDuration = duration;
                        entry.VariableRevision++;
                        if (duration <= TimeSpan.Zero)
                        {
                            RemoveExpiredEntryLocked(entry);
                        }
                        else
                        {
                            reschedule = true;
                        }
                    }
                }

                if (collected)
                {
                    RemoveCurrentEntryLocked(entry, collected: true);
                    result = false;
                }
                else if (expired)
                {
                    RemoveExpiredEntryLocked(entry);
                    result = false;
                }
                else
                {
                    result = true;
                    if (reschedule && _expirationWheel is not null)
                    {
                        ulong normalizedNow = GetExpirationNowLocked();
                        AdvanceExpirationLocked(normalizedNow);
                        ScheduleExpirationNodeLocked(entry, normalizedNow);
                    }
                }
            }
        }

        evictionScope.Dispatch();
        if (result)
        {
            RequestExpirationTimer();
        }
        return result;
    }

    private void PutWithVariableDuration(TKey key, TValue value, TimeSpan duration)
    {
        Put(key, value, duration);
    }

    private (bool IsUpdate, TimeSpan CurrentDuration) CaptureVariableUpdate(TKey key)
    {
        if (_expiry is null || !_entries.TryGetValue(key, out Entry? entry))
        {
            return (false, TimeSpan.MaxValue);
        }

        lock (entry.Sync)
        {
            if (!Volatile.Read(ref entry.IsReady))
            {
                return (false, TimeSpan.MaxValue);
            }

            long now = _timeProvider.GetTimestamp();
            return IsExpired(entry, now)
                ? (false, TimeSpan.MaxValue)
                : (true, GetRemainingDuration(entry, now, ExpirationKind.Variable));
        }
    }

    private TimeSpan ComputeCreateDuration(TKey key, TValue value)
    {
        return _expiry is null
            ? TimeSpan.MaxValue
            : NormalizeVariableDuration(_expiry.ExpireAfterCreate(key, value, TimeSpan.MaxValue));
    }

    private TimeSpan ComputeUpdateDuration(TKey key, TValue value, TimeSpan currentDuration)
    {
        return _expiry is null
            ? TimeSpan.MaxValue
            : NormalizeVariableDuration(_expiry.ExpireAfterUpdate(key, value, currentDuration));
    }

    private bool IsExpired(Entry entry, long now)
    {
        long writeTicks = Volatile.Read(ref _expireAfterWriteTicks);
        if (
            writeTicks >= 0
            && _timeProvider.GetElapsedTime(entry.WriteTimestamp, now)
                >= TimeSpan.FromTicks(writeTicks)
        )
        {
            return true;
        }

        long accessTicks = Volatile.Read(ref _expireAfterAccessTicks);
        if (
            accessTicks >= 0
            && _timeProvider.GetElapsedTime(entry.AccessTimestamp, now)
                >= TimeSpan.FromTicks(accessTicks)
        )
        {
            return true;
        }

        return _expiry is not null
            && entry.VariableDuration != TimeSpan.MaxValue
            && _timeProvider.GetElapsedTime(entry.VariableTimestamp, now) >= entry.VariableDuration;
    }

    private TimeSpan GetRemainingDuration(Entry entry, long now, ExpirationKind kind)
    {
        TimeSpan duration;
        long timestamp;
        switch (kind)
        {
            case ExpirationKind.Access:
                duration = GetConfiguredDuration(ref _expireAfterAccessTicks) ?? TimeSpan.MaxValue;
                timestamp = entry.AccessTimestamp;
                break;
            case ExpirationKind.Write:
                duration = GetConfiguredDuration(ref _expireAfterWriteTicks) ?? TimeSpan.MaxValue;
                timestamp = entry.WriteTimestamp;
                break;
            case ExpirationKind.Refresh:
                duration = GetConfiguredDuration(ref _refreshAfterWriteTicks) ?? TimeSpan.MaxValue;
                timestamp = entry.WriteTimestamp;
                break;
            case ExpirationKind.Variable:
                duration = entry.VariableDuration;
                timestamp = entry.VariableTimestamp;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(kind),
                    kind,
                    "Unknown expiration kind."
                );
        }

        if (duration == TimeSpan.MaxValue)
        {
            return TimeSpan.MaxValue;
        }

        TimeSpan elapsed = _timeProvider.GetElapsedTime(timestamp, now);
        if (elapsed <= TimeSpan.Zero)
        {
            return duration;
        }

        return elapsed >= duration ? TimeSpan.Zero : duration - elapsed;
    }

    private ITimer CreateExpirationTimer()
    {
        static void OnTimer(object? state)
        {
            ((CacheEngine<TKey, TValue>)state!).OnExpirationTimer();
        }

        if (ExecutionContext.IsFlowSuppressed())
        {
            return _timeProvider.CreateTimer(
                OnTimer,
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
        }

        using (ExecutionContext.SuppressFlow())
        {
            return _timeProvider.CreateTimer(
                OnTimer,
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    private void RequestExpirationTimer()
    {
        using SynchronousEvictionScope evictionScope = BeginSynchronousEvictionScope();
        try
        {
            ITimer? timer = _expirationTimer;
            if (!_enableExpirationScheduler || timer is null)
            {
                return;
            }

            lock (_expirationTimerGate)
            {
                if (_disposed != 0)
                {
                    return;
                }

                TimeSpan delay;
                long armRevision;
                lock (_gate)
                {
                    if (_disposed != 0 || _expirationWheel is null)
                    {
                        return;
                    }

                    AdvanceExpirationLocked(GetExpirationNowLocked());
                    delay = GetTimerDelay(_expirationWheel);
                    armRevision = ++_expirationArmRevision;
                }

                if (Volatile.Read(ref _expirationTimerRunning) != 0)
                {
                    Volatile.Write(ref _expirationTimerRearmRequested, 1);
                    return;
                }

                // All timer arm submissions are serialized by _expirationTimerGate.
                // The revision is captured while holding _gate, so a later request
                // cannot submit an older delay after a newer request has changed the
                // wheel.
                if (armRevision != Volatile.Read(ref _expirationArmRevision))
                {
                    return;
                }

                InvokeHook(_testHooks?.BeforeExpirationTimerArm);
                if (_disposed != 0)
                {
                    return;
                }

                try
                {
                    timer.Change(delay, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Disposal wins the race with a prompt re-arm.  The cache is
                    // already fenced and no late timer callback may publish state.
                }
            }
        }
        finally
        {
            // Expiration advancement may detach entries while holding the gate;
            // reliable eviction callbacks run only after this method has left
            // every internal lock and before its caller observes completion.
            evictionScope.Dispatch();
        }
    }

    private void OnExpirationTimer()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _expirationTimerRunning, 1) != 0)
        {
            Volatile.Write(ref _expirationTimerRearmRequested, 1);
            return;
        }

        try
        {
            // A maintenance pass can discover a new earlier deadline while a
            // write races with the timer callback.  Re-arm at most once in the
            // same callback; the next timer tick handles another bounded pass.
            for (int pass = 0; pass < 2; pass++)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                Volatile.Write(ref _expirationTimerRearmRequested, 0);
                try
                {
                    CleanUp();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (Volatile.Read(ref _expirationTimerRearmRequested) == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _expirationTimerRunning, 0);
            if (Volatile.Read(ref _disposed) == 0)
            {
                RequestExpirationTimer();
            }
        }
    }

    private void AdvanceExpirationLocked(ulong now)
    {
        TimerWheel<Entry>? wheel = _expirationWheel;
        Dictionary<Entry, IdentityTimerNode<Entry>>? nodes = _expirationNodes;
        if (wheel is null || nodes is null)
        {
            return;
        }

        if (now < wheel.CurrentTime)
        {
            now = wheel.CurrentTime;
        }

        TimerAdvanceResult<Entry> advanced = wheel.Advance(now, ExpirationAdvanceBudget);
        long timestamp = _timeProvider.GetTimestamp();
        foreach (IdentityTimerNode<Entry> node in advanced.DueNodes)
        {
            Entry entry = node.Value;
            if (
                !nodes.TryGetValue(entry, out IdentityTimerNode<Entry>? currentNode)
                || !ReferenceEquals(currentNode, node)
                || !_entries.IsCurrent(entry)
            )
            {
                if (!node.IsRetired)
                {
                    wheel.Retire(node);
                }

                nodes.Remove(entry);
                continue;
            }

            bool expired;
            bool collected;
            lock (entry.Sync)
            {
                bool ready = Volatile.Read(ref entry.IsReady);
                collected =
                    ready
                    && (
                        (_weakKeys && !entry.TryGetKey(out _))
                        || (_weakValues && !entry.TryGetValue(out _))
                    );
                expired = !ready || (!collected && IsExpired(entry, timestamp));
            }

            if (collected)
            {
                RemoveCurrentEntryLocked(entry, collected: true);
            }
            else if (expired)
            {
                RemoveExpiredEntryLocked(entry);
            }
            else
            {
                ScheduleExpirationNodeLocked(entry, now);
            }
        }
    }

    private static TimeSpan GetTimerDelay(TimerWheel<Entry> wheel)
    {
        ulong delay = wheel.GetNextDelay();
        if (delay == ulong.MaxValue)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan duration =
            delay > long.MaxValue
                ? TimeSpan.MaxValue
                : TimeSpan.FromMilliseconds((long)delay * NormalizedExpirationTickMilliseconds);
        return ClampTimerDueTime(duration);
    }

    private ulong GetExpirationNowLocked()
    {
        if (!_expirationClockInitialized)
        {
            _expirationOriginTimestamp = _timeProvider.GetTimestamp();
            _expirationClockInitialized = true;
            _expirationNow = 0;
        }

        TimeSpan elapsed = _timeProvider.GetElapsedTime(
            _expirationOriginTimestamp,
            _timeProvider.GetTimestamp()
        );
        if (elapsed <= TimeSpan.Zero)
        {
            return _expirationNow;
        }

        ulong candidate = (ulong)(elapsed.Ticks / TimeSpan.TicksPerMillisecond);
        if (candidate > _expirationNow)
        {
            _expirationNow = candidate;
        }

        return _expirationNow;
    }

    private void ScheduleExpirationNodeLocked(Entry entry, ulong normalizedNow)
    {
        TimerWheel<Entry>? wheel = _expirationWheel;
        Dictionary<Entry, IdentityTimerNode<Entry>>? nodes = _expirationNodes;
        if (wheel is null || nodes is null)
        {
            return;
        }

        ulong now = normalizedNow < wheel.CurrentTime ? wheel.CurrentTime : normalizedNow;

        if (!_entries.IsCurrent(entry) || !Volatile.Read(ref entry.IsReady))
        {
            RetireExpirationNodeLocked(entry);
            return;
        }

        Flight? refreshFlight = entry.RefreshFlight;
        if (
            entry.PolicyDetached
            && refreshFlight is not null
            && IsCurrentRefreshFlightLocked(entry, refreshFlight)
        )
        {
            RetireExpirationNodeLocked(entry);
            return;
        }

        TimeSpan remaining;
        bool collected;
        lock (entry.Sync)
        {
            if (!Volatile.Read(ref entry.IsReady))
            {
                RetireExpirationNodeLocked(entry);
                return;
            }

            collected =
                (_weakKeys && !entry.TryGetKey(out _))
                || (_weakValues && !entry.TryGetValue(out _));
            remaining = collected ? TimeSpan.Zero : GetEntryExpirationDuration(entry, now);
        }

        if (collected)
        {
            RetireExpirationNodeLocked(entry);
            RemoveCurrentEntryLocked(entry, collected: true);
            return;
        }

        if (remaining == TimeSpan.MaxValue)
        {
            RetireExpirationNodeLocked(entry);
            return;
        }

        ulong deadline = AddExpirationTicks(now, remaining);
        if (!nodes.TryGetValue(entry, out IdentityTimerNode<Entry>? node) || node.IsRetired)
        {
            node = new IdentityTimerNode<Entry>(entry);
            nodes[entry] = node;
            wheel.Schedule(node, deadline);
        }
        else if (node.IsScheduled)
        {
            wheel.Reschedule(node, deadline);
        }
        else
        {
            wheel.Schedule(node, deadline);
        }
    }

    private void RescheduleAllExpirationNodesLocked()
    {
        if (_expirationWheel is null)
        {
            return;
        }

        ulong normalizedNow = GetExpirationNowLocked();
        AdvanceExpirationLocked(normalizedNow);

        foreach (Entry entry in _entries.Values)
        {
            ScheduleExpirationNodeLocked(entry, normalizedNow);
        }
    }

    private void RetireExpirationNodeLocked(Entry entry)
    {
        TimerWheel<Entry>? wheel = _expirationWheel;
        Dictionary<Entry, IdentityTimerNode<Entry>>? nodes = _expirationNodes;
        if (
            wheel is null
            || nodes is null
            || !nodes.Remove(entry, out IdentityTimerNode<Entry>? node)
        )
        {
            return;
        }

        if (!node.IsRetired)
        {
            wheel.Retire(node);
        }
    }

    private void ResetExpirationStateLocked()
    {
        TimerWheel<Entry>? wheel = _expirationWheel;
        Dictionary<Entry, IdentityTimerNode<Entry>>? nodes = _expirationNodes;
        if (wheel is null || nodes is null)
        {
            return;
        }

        foreach (IdentityTimerNode<Entry> node in nodes.Values)
        {
            if (!node.IsRetired)
            {
                wheel.Retire(node);
            }
        }

        nodes.Clear();
        ulong now = GetExpirationNowLocked();
        _expirationWheel = new TimerWheel<Entry>(now);
    }

    private TimeSpan GetEntryExpirationDuration(Entry entry, ulong normalizedNow)
    {
        long timestamp = _timeProvider.GetTimestamp();
        TimeSpan minimum = TimeSpan.MaxValue;
        long writeTicks = Volatile.Read(ref _expireAfterWriteTicks);
        if (writeTicks >= 0)
        {
            minimum = GetRemainingDuration(entry, timestamp, ExpirationKind.Write);
        }

        long accessTicks = Volatile.Read(ref _expireAfterAccessTicks);
        if (accessTicks >= 0)
        {
            TimeSpan access = GetRemainingDuration(entry, timestamp, ExpirationKind.Access);
            minimum = access < minimum ? access : minimum;
        }

        if (_expiry is not null && entry.VariableDuration != TimeSpan.MaxValue)
        {
            TimeSpan variable = GetRemainingDuration(entry, timestamp, ExpirationKind.Variable);
            minimum = variable < minimum ? variable : minimum;
        }

        _ = normalizedNow;
        return minimum;
    }

    private static ulong AddExpirationTicks(ulong now, TimeSpan remaining)
    {
        long ticks = remaining.Ticks;
        if (ticks <= 0)
        {
            return now;
        }

        const ulong tickUnit = TimeSpan.TicksPerMillisecond;
        ulong delta = (ulong)ticks / tickUnit;
        if ((ulong)ticks % tickUnit != 0)
        {
            delta++;
        }
        ulong maximum = ulong.MaxValue - now;
        return delta > maximum ? ulong.MaxValue : now + delta;
    }

    private static TimeSpan ClampTimerDueTime(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // System.Threading.Timer and the TimeProvider timer implementations
        // accept a finite due time only up to UInt32.MaxValue - 1 ms.
        TimeSpan bclMaximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
        return duration > bclMaximum ? bclMaximum : duration;
    }

    private void StopExpirationTimer()
    {
        ITimer? timer = _expirationTimer;
        if (!_enableExpirationScheduler || timer is null)
        {
            return;
        }

        lock (_expirationTimerGate)
        {
            try
            {
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }

            timer.Dispose();
        }
    }

    private static TimeSpan? GetConfiguredDuration(ref long ticks)
    {
        long value = Volatile.Read(ref ticks);
        return value < 0 ? null : TimeSpan.FromTicks(value);
    }

    private static TimeSpan NormalizeVariableDuration(TimeSpan duration)
    {
        ValidateVariableDuration(duration, nameof(duration));
        return duration;
    }

    private static void TouchWithoutLock(Entry entry, long now)
    {
        // Compare timestamp units before TimeSpan truncation: a negative fraction of a tick
        // must not move access time backwards. Subtraction preserves signed wraparound.
        if (unchecked(now - entry.AccessTimestamp) >= 0)
        {
            entry.AccessTimestamp = now;
        }
    }

    private static void ValidateDuration(TimeSpan? duration, string parameterName)
    {
        if (
            duration.HasValue
            && (duration.Value <= TimeSpan.Zero || duration.Value == Timeout.InfiniteTimeSpan)
        )
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The expiration duration must be finite and positive."
            );
        }
    }

    private static void ValidateVariableDuration(TimeSpan duration, string parameterName)
    {
        // Variable expiration deliberately accepts the complete TimeSpan range.
        // Zero and negative values mean "expire immediately", while MaxValue is
        // the explicit no-deadline value.  Unlike fixed policy configuration,
        // rejecting these values would make the runtime policy API unable to
        // implement an immediate invalidation or a very long lease.
        _ = parameterName;
        _ = duration;
    }
}
