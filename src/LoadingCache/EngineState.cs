using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using LoadingCache.ReferenceStorage;

namespace LoadingCache;

internal sealed partial class CacheEngine<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private abstract class Flight(TKey key, long epoch, long generation)
    {
        internal TKey Key { get; } = key;

        internal long Epoch { get; } = epoch;

        internal long Generation { get; } = generation;

        internal int Started;

        internal int StartRequested;

        internal int ExecutionStarted;

        internal int ExecutionResourcesReleased;

        internal int RetirementCleanupStarted;

        internal int RetirementCleanupCompleted;

        internal int UnderlyingCompleted;

        internal int Retired;

        internal int TerminalClaimed;

        internal int PublishRevoked;

        internal bool IsRefresh;

        internal Entry? RefreshEntry;

        internal TimeSpan PreviousVariableDuration = TimeSpan.MaxValue;

        internal long TimeoutStartTimestamp;

        internal ITimer? TimeoutTimer;

        internal CancellationTokenSource? WorkCancellation;

        internal CancellationToken LoadCancellationToken;

        internal int TimeoutCancellationStarted;

        internal int CancellationCleanupCompleted = 1;

        // Cancellation callbacks can finish before the timeout owner has
        // disposed its timer and notified the shared promises.
        internal int TimeoutFinalizationCompleted = 1;

        internal long LoadStartTimestamp;

        // Set only at the loader invocation boundary.  A timeout can fire
        // while the flight's timer is being armed, before the user callback
        // is invoked; such a flight must not fabricate duration or outcome
        // statistics from the default timestamp.
        internal int StatisticsStarted;

        internal int StatisticsRecorded;

        // A refresh reuses the resident entry object.  The old value version
        // therefore needs its own once-only replacement notification fence.
        internal int ReplacementNotificationRecorded;

        // Keep the asynchronous cancellation cleanup task rooted by the flight
        // until the cache-owned cleanup reaches its retirement boundary.
        // ReSharper disable once NotAccessedField.Local
        internal Task? CancellationTask;

        internal abstract void SetDisposed();
    }

    private sealed class AsyncFlight : Flight
    {
        internal AsyncFlight(
            TKey key,
            long epoch,
            long generation,
            Func<TKey, CancellationToken, Task<TValue>>? factory,
            Task<TValue>? suppliedTask = null
        )
            : base(key, epoch, generation)
        {
            Factory = factory;
            SuppliedTask = suppliedTask;
            Completion = new TaskCompletionSource<TValue>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        internal Func<TKey, CancellationToken, Task<TValue>>? Factory { get; }

        internal Task<TValue>? SuppliedTask { get; }

        internal TaskCompletionSource<TValue> Completion { get; }

        internal void TrySetException(Exception exception)
        {
            Completion.TrySetException(exception);
            _ = Completion.Task.Exception;
        }

        internal override void SetDisposed()
        {
            TrySetException(new ObjectDisposedException(nameof(LoadingCache)));
        }
    }

    private sealed class SyncFlight : Flight
    {
        private readonly object _completion = new();
        private bool _completed;
        private TValue _value = default!;
        private Exception? _exception;

        internal SyncFlight(TKey key, long epoch, long generation, Func<TKey, TValue> factory)
            : base(key, epoch, generation)
        {
            Factory = factory;
            Completion = new TaskCompletionSource<TValue>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        internal Func<TKey, TValue> Factory { get; }

        internal TaskCompletionSource<TValue> Completion { get; }

        internal TValue Wait()
        {
            Exception? exception;
            lock (_completion)
            {
                while (!_completed)
                {
                    Monitor.Wait(_completion);
                }

                exception = _exception;
            }

            if (exception is not null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            return _value;
        }

        internal void Set(TValue value)
        {
            lock (_completion)
            {
                if (_completed)
                {
                    return;
                }

                _value = value;
                _completed = true;
                Monitor.PulseAll(_completion);
                Completion.TrySetResult(value);
            }
        }

        internal void Set(Exception exception)
        {
            lock (_completion)
            {
                if (_completed)
                {
                    return;
                }

                _exception = exception;
                _completed = true;
                Monitor.PulseAll(_completion);
                Completion.TrySetException(exception);
                _ = Completion.Task.Exception;
            }
        }

        internal override void SetDisposed()
        {
            Set(new ObjectDisposedException(nameof(LoadingCache)));
        }
    }

    private sealed class FixedWritePublication(TValue value, long timestamp)
    {
        internal readonly TValue Value = value;
        internal readonly long Timestamp = timestamp;
    }

    private sealed class Entry
    {
        private Entry(TKey key, long epoch, long generation, bool weakKey)
        {
            if (weakKey)
            {
                WeakKey = ReferenceKey<TKey>.CreateWeak(key);
            }
            else
            {
                _strongKey = key;
            }

            Epoch = epoch;
            Generation = generation;
        }

        internal readonly object Sync = new();
        private readonly TKey? _strongKey;
        internal readonly ReferenceKey<TKey>? WeakKey;
        internal readonly long Epoch;
        internal readonly long Generation;
        internal object? PolicyToken;
        internal Task<TValue>? SharedTask;
        internal bool IsReady;
        internal bool Retired;
        internal bool PolicyDetached;

        // Protected by Sync. Ready/map visibility may precede policy finalization; a failed
        // publication stays closed to resident replacement until repaired or retired.
        internal bool PublicationPending;
        internal bool RemovalNotified;
        private TValue _strongValue = default!;
        internal FixedWritePublication? PublishedWrite;

        // Reference, Int32, and Int64 have BCL volatile primitives. Other value types retain
        // the entry lock instead of adding a per-Put box or assuming struct copies are atomic.
        internal static bool SupportsAtomicStrongValue =>
            !typeof(TValue).IsValueType
            || typeof(TValue) == typeof(int)
            || typeof(TValue) == typeof(long);
        internal ReferenceValue<TValue>? WeakValue;
        internal long Weight;
        internal long WriteTimestamp;
        internal long AccessTimestamp;
        internal long VariableTimestamp;
        internal long VariableRevision;

        // Monotonic identity for a value publication on this entry.  Unlike
        // VariableRevision it is not advanced by read-side expiry callbacks;
        // claimed terminal rollback uses it to distinguish a newer value or
        // refresh publication from the flight that is being completed.
        internal long PublicationRevision;
        internal TimeSpan VariableDuration = TimeSpan.MaxValue;
        internal Flight? Flight;

        internal Flight? RefreshFlight;

        internal long RefreshFailureTimestamp;

        internal bool HasRefreshFailure;

        internal static Entry Loading(
            TKey key,
            long epoch,
            long generation,
            Flight flight,
            bool weakKey = false
        )
        {
            var entry = new Entry(key, epoch, generation, weakKey) { Flight = flight };
            if (flight is AsyncFlight asyncFlight)
            {
                entry.SharedTask = asyncFlight.Completion.Task;
            }

            return entry;
        }

        internal static Entry Ready(
            TKey key,
            long epoch,
            long generation,
            TValue value,
            long timestamp,
            long weight,
            TimeSpan? variableDuration = null,
            bool weakKey = false,
            bool weakValue = false,
            bool createSharedTask = true,
            bool createWriteSnapshot = false
        )
        {
            var entry = new Entry(key, epoch, generation, weakKey)
            {
                IsReady = true,
                Weight = weight,
                WriteTimestamp = timestamp,
                AccessTimestamp = timestamp,
                VariableTimestamp = timestamp,
                VariableRevision = 1,
                PublicationRevision = 1,
                VariableDuration = variableDuration ?? TimeSpan.MaxValue,
                SharedTask = !weakValue && createSharedTask ? Task.FromResult(value) : null,
            };
            entry.SetValue(value, weakValue);
            if (createWriteSnapshot)
            {
                entry.PublishInitialWriteSnapshot(value, timestamp);
            }
            return entry;
        }

        internal void PublishWriteSnapshot(TValue value, long timestamp)
        {
            Volatile.Write(ref PublishedWrite, new FixedWritePublication(value, timestamp));
        }

        internal void PublishInitialWriteSnapshot(TValue value, long timestamp)
        {
            if (!SupportsAtomicStrongValue)
            {
                PublishWriteSnapshot(value, timestamp);
            }
        }

        internal void PrepareWriteSnapshotUpdate()
        {
            if (PublishedWrite is null)
            {
                // Initial atomic fields are immutable until this full fence. Readers
                // validate the null marker after acquiring both fields; subsequent
                // mutations cannot become visible before the snapshot replaces it.
                Interlocked.Exchange(
                    ref PublishedWrite,
                    new FixedWritePublication(_strongValue, WriteTimestamp)
                );
            }
        }

        internal bool TryGetKey([MaybeNullWhen(false)] out TKey key)
        {
            if (WeakKey is not null)
            {
                return WeakKey.TryGetTarget(out key);
            }

            key = _strongKey!;
            return true;
        }

        internal bool TryGetValue([MaybeNullWhen(false)] out TValue value)
        {
            if (WeakValue is not null)
            {
                return WeakValue.TryGetValue(out value);
            }

            value = _strongValue;
            return true;
        }

        internal void SetValue(TValue value, bool weak)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (weak)
            {
                WeakValue = ReferenceValue<TValue>.Weak(value);
                _strongValue = default!;
            }
            else
            {
                if (!typeof(TValue).IsValueType)
                {
                    Volatile.Write(ref Unsafe.As<TValue, object>(ref _strongValue), value);
                }
                else if (typeof(TValue) == typeof(int))
                {
                    Volatile.Write(
                        ref Unsafe.As<TValue, int>(ref _strongValue),
                        Unsafe.As<TValue, int>(ref value)
                    );
                }
                else if (typeof(TValue) == typeof(long))
                {
                    Volatile.Write(
                        ref Unsafe.As<TValue, long>(ref _strongValue),
                        Unsafe.As<TValue, long>(ref value)
                    );
                }
                else
                {
                    _strongValue = value;
                }
                WeakValue = null;
            }
        }

        internal TValue ReadStrongValueAtomic()
        {
            if (!typeof(TValue).IsValueType)
            {
                return (TValue)Volatile.Read(ref Unsafe.As<TValue, object>(ref _strongValue));
            }

            if (typeof(TValue) == typeof(int))
            {
                int value = Volatile.Read(ref Unsafe.As<TValue, int>(ref _strongValue));
                return Unsafe.As<int, TValue>(ref value);
            }

            if (typeof(TValue) != typeof(long))
            {
                throw new InvalidOperationException(
                    "The value type requires a locked resident read."
                );
            }

            long longValue = Volatile.Read(ref Unsafe.As<TValue, long>(ref _strongValue));
            return Unsafe.As<long, TValue>(ref longValue);
        }

        /// <summary>
        /// Returns the stable task view for a strong resident value, creating it only when an
        /// asynchronous consumer actually asks for one. Synchronous Put operations otherwise do
        /// not need to allocate a completed Task for every value replacement.
        /// </summary>
        internal Task<TValue>? GetOrCreateSharedTaskLocked()
        {
            if (SharedTask is not null || WeakValue is not null)
            {
                return SharedTask;
            }

            if (!Volatile.Read(ref IsReady) || !TryGetValue(out TValue? value))
            {
                return null;
            }

            return SharedTask = Task.FromResult(value);
        }

        internal bool IsValueCollected => WeakValue?.IsCollected == true;
    }
}
