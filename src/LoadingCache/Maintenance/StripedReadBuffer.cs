/*
 * Copyright 2015 Ben Manes. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
/*
 * Written by Doug Lea with assistance from members of JCP JSR-166
 * Expert Group and released to the public domain, as explained at
 * http://creativecommons.org/publicdomain/zero/1.0/
 */
// Portions of the striped bounded-buffer strategy are adapted from Caffeine 3.2.4
// (BoundedBuffer.java, StripedBuffer.java, and Buffer.java), revision
// 836b65c0a83e5d1641ded9c6de578654bc04b2e9. Caffeine is licensed under the Apache
// License 2.0; the StripedBuffer design also credits Doug Lea and the JSR-166
// Expert Group under the public-domain dedication in the upstream source.
// This file is a .NET-native reimplementation using Volatile and Interlocked.

using System.Runtime.CompilerServices;
using LoadingCache.Diagnostics;

namespace LoadingCache.Maintenance;

internal enum ReadBufferOfferResult : byte
{
    Success,
    Full,
    Failed,
    Shutdown,
}

/// <summary>
/// Provides a bounded, striped transport for best-effort policy read events.
/// </summary>
/// <typeparam name="TEvent">The event type carried by the transport.</typeparam>
/// <remarks>
/// Producers reserve slots with a bounded CAS attempt and publish through a per-slot sequence
/// number. A failed reservation is allowed to drop the event. The table starts with one lazily
/// created ring and grows under a short CAS-protected table lock when reservation contention is
/// observed. The constructor's stripe count is the maximum table size so existing deterministic
/// tests can keep controlling the amount of capacity.
///
/// There is one maintenance consumer. A producer can reserve a slot and pause before publishing;
/// the consumer stops at that slot instead of spinning or advancing past uninitialised data.
/// Disposal first closes admission and detaches the table, then detaches each ring's slot array
/// without waiting for paused producers. A producer that resumes after disposal clears its own
/// reservation and value, so the detached table cannot retain a late event.
/// </remarks>
internal sealed class StripedReadBuffer<TEvent> : IDisposable
{
    private static readonly RingBuffer<TEvent>?[] DisposedTable = [];
    private const int DroppedFullOffset = 0;
    private const int DroppedFailedOffset = 1;

    // Two counters occupy the first two longs. A 17-long stride leaves at least 128 bytes
    // between counters owned by adjacent shards without assuming array-base alignment.
    private const int DropCounterStride = 17;

    private readonly int _maximumStripes;
    private readonly int _stripeCapacity;
    private readonly bool _recordStatistics;
    private readonly long[]? _dropCounters;
    private long[]? _testingEnqueued;
    private long[]? _testingDequeued;
    private long[]? _testingDroppedFull;
    private long[]? _testingDroppedShutdown;
    private readonly object _consumerGate = new();
    private RingBuffer<TEvent>?[]? _table;
    private RingBuffer<TEvent>?[]? _retiredTable;
    private int _tableBusy;
    private int _nextReadStripe;
    private int _disposed;
    private long _droppedShutdown;
    private Action? _beforeReserveForTesting;
    private Action? _beforePublishForTesting;

    internal StripedReadBuffer(int stripeCount, int stripeCapacity, bool recordStatistics = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripeCapacity);

        if ((stripeCapacity & (stripeCapacity - 1)) != 0)
        {
            throw new ArgumentException(
                "Stripe capacity must be a power of two.",
                nameof(stripeCapacity)
            );
        }

        if ((stripeCount & (stripeCount - 1)) != 0)
        {
            throw new ArgumentException(
                "Stripe count must be a power of two.",
                nameof(stripeCount)
            );
        }

        _maximumStripes = stripeCount;
        _stripeCapacity = stripeCapacity;
        _recordStatistics = recordStatistics;
        if (recordStatistics)
        {
            _dropCounters = new long[
                StripedCacheCounters.NormalizeStripeCount(Environment.ProcessorCount)
                    * DropCounterStride
            ];
        }
    }

    /// <summary>
    /// Attempts to publish an event without waiting or allocating asynchronous state.
    /// </summary>
    /// <returns><see langword="true" /> when the selected ring accepted the event.</returns>
    internal bool TryEnqueue(TEvent value)
    {
        ReadBufferOfferResult result = TryOffer(value);
        return result == ReadBufferOfferResult.Success;
    }

    internal ReadBufferOfferResult TryOffer(TEvent value)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_recordStatistics)
            {
                SaturatingIncrement(ref _droppedShutdown);
            }
            return ReadBufferOfferResult.Shutdown;
        }

        ulong mixed = ReadBufferThreadProbe.Value;
        int hash = unchecked((int)mixed);
        int increment = unchecked((int)(mixed >> 32)) | 1;
        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        bool wasUncontended = true;

        if (table is not null && table.Length != 0)
        {
            int mask = table.Length - 1;
            RingBuffer<TEvent>? ring = Volatile.Read(ref table[hash & mask]);
            if (ring is not null)
            {
                ReadBufferOfferResult result = ring.Offer(value);
                if (result != ReadBufferOfferResult.Failed)
                {
                    return result;
                }

                wasUncontended = false;
            }
        }

        ReadBufferOfferResult retry = ExpandOrRetry(value, hash, increment, wasUncontended);
        if (_recordStatistics && retry == ReadBufferOfferResult.Failed)
        {
            RecordDrop(_dropCounters!, DroppedFailedOffset);
        }

        return retry;
    }

    internal int StripeCountForTesting => Volatile.Read(ref _table)?.Length ?? 0;

    internal int DiagnosticStripeCountForTesting => _dropCounters?.Length / DropCounterStride ?? 0;

    internal void AddDropStatisticsForTesting(
        int stripeIndex,
        long droppedFull = 0,
        long droppedFailed = 0
    )
    {
        if (_dropCounters is null || (uint)stripeIndex >= (uint)DiagnosticStripeCountForTesting)
        {
            throw new ArgumentOutOfRangeException(nameof(stripeIndex));
        }

        int start = stripeIndex * DropCounterStride;
        AddNonNegativeForTesting(ref _dropCounters[start + DroppedFullOffset], droppedFull);
        AddNonNegativeForTesting(ref _dropCounters[start + DroppedFailedOffset], droppedFailed);
    }

    internal void SetHooksForTesting(Action? beforeReserve, Action? beforePublish)
    {
        Volatile.Write(ref _beforeReserveForTesting, beforeReserve);
        Volatile.Write(ref _beforePublishForTesting, beforePublish);

        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        if (table is null)
        {
            return;
        }

        for (int index = 0; index < table.Length; index++)
        {
            RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
            ring?.SetHooks(beforeReserve, beforePublish);
        }
    }

    internal void SetCounterForTesting(long counter)
    {
        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        if (table is null || table.Length == 0 || Volatile.Read(ref table[0]) is null)
        {
            throw new InvalidOperationException("The read buffer has not been initialized.");
        }

        Volatile.Read(ref table[0])!.SetCounterForTesting(counter);
        if (_dropCounters is null)
        {
            return;
        }

        // Counter-wrap tests reset a quiescent first ring. Its full-drop accounting now
        // lives in the shared shards; failed-offer history is not part of the ring reset.
        for (int start = 0; start < _dropCounters.Length; start += DropCounterStride)
        {
            Volatile.Write(ref _dropCounters[start + DroppedFullOffset], 0);
        }
    }

    internal void SetForcedCasFailuresForTesting(int failures)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failures);
        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        if (table is null)
        {
            return;
        }

        for (int index = 0; index < table.Length; index++)
        {
            Volatile.Read(ref table[index])?.SetForcedCasFailuresForTesting(failures);
        }
    }

    internal void SetShutdownHookForTesting(Action<Action>? afterSnapshot)
    {
        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        if (table is null || table.Length == 0)
        {
            table = Volatile.Read(ref _retiredTable);
        }
        if (table is null)
        {
            return;
        }

        foreach (RingBuffer<TEvent>? ring in table)
        {
            ring?.SetShutdownHook(afterSnapshot);
        }
    }

    /// <summary>
    /// Attempts to consume one event by scanning each active ring at most once.
    /// </summary>
    /// <remarks>
    /// The consumer owns the read cursor. The short gate only coordinates that cursor with
    /// disposal; ordinary offers never enter it. A just-published ring's late shutdown cleanup
    /// joins the same gate so it cannot clear a value beneath the consumer.
    /// </remarks>
    internal bool TryRead(out TEvent value)
    {
        lock (_consumerGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                value = default!;
                return false;
            }

            RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
            if (table is null || table.Length == 0)
            {
                value = default!;
                return false;
            }

            int mask = table.Length - 1;
            int start = _nextReadStripe & mask;
            for (int offset = 0; offset < table.Length; offset++)
            {
                int stripeIndex = (start + offset) & mask;
                RingBuffer<TEvent>? ring = Volatile.Read(ref table[stripeIndex]);
                if (ring is null || !ring.TryRead(out value))
                {
                    continue;
                }

                _nextReadStripe = (stripeIndex + 1) & mask;
                return true;
            }

            _nextReadStripe = (start + 1) & mask;
            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Drains up to <paramref name="budget" /> published events without allocating a batch.
    /// </summary>
    /// <remarks>
    /// The caller must provide the single maintenance consumer. The action is an internal policy
    /// callback, not a user callback; it must not retain the event or re-enter this transport.
    /// </remarks>
    internal int DrainTo(Action<TEvent> consumer, int budget)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        lock (_consumerGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return 0;
            }

            RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
            if (table is null || table.Length == 0)
            {
                return 0;
            }

            int mask = table.Length - 1;
            int start = _nextReadStripe & mask;
            int drained = 0;
            for (int offset = 0; offset < table.Length && drained < budget; offset++)
            {
                int stripeIndex = (start + offset) & mask;
                RingBuffer<TEvent>? ring = Volatile.Read(ref table[stripeIndex]);
                if (ring is null)
                {
                    continue;
                }

                int remaining = budget - drained;
                drained += ring.DrainTo(consumer, remaining);
                _nextReadStripe = (stripeIndex + 1) & mask;
            }

            if (drained == 0)
            {
                _nextReadStripe = (start + 1) & mask;
            }

            return drained;
        }
    }

    /// <summary>Returns whether at least one published event is immediately readable.</summary>
    internal bool HasPublished
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
            if (table is null)
            {
                return false;
            }

            for (int index = 0; index < table.Length; index++)
            {
                RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
                if (ring is not null && ring.HasPublished)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal ReadBufferStatistics GetStatistics(Action? afterTableCaptureForTesting = null)
    {
        bool isDisposed = Volatile.Read(ref _disposed) != 0;
        long queued = 0;
        long enqueued = 0;
        long dequeued = 0;
        long droppedFull = 0;
        long droppedFailed = 0;
        long droppedShutdown = Volatile.Read(ref _droppedShutdown);
        if (_dropCounters is not null)
        {
            for (int start = 0; start < _dropCounters.Length; start += DropCounterStride)
            {
                droppedFull = SaturatingAdd(
                    droppedFull,
                    Volatile.Read(ref _dropCounters[start + DroppedFullOffset])
                );
                droppedFailed = SaturatingAdd(
                    droppedFailed,
                    Volatile.Read(ref _dropCounters[start + DroppedFailedOffset])
                );
            }
        }

        RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
        afterTableCaptureForTesting?.Invoke();
        if (table is null || table.Length == 0)
        {
            table = Volatile.Read(ref _retiredTable);
        }

        if (table is not null)
        {
            // A captured live table and a subsequently retired expanded table can share rings.
            // Choose one snapshot instead of counting the same ring again after disposal.
            AddRingStatistics(table, ref queued, ref enqueued, ref dequeued, ref droppedShutdown);
        }

        long[]? testingEnqueued = Volatile.Read(ref _testingEnqueued);
        long[]? testingDequeued = Volatile.Read(ref _testingDequeued);
        long[]? testingDroppedFull = Volatile.Read(ref _testingDroppedFull);
        long[]? testingDroppedShutdown = Volatile.Read(ref _testingDroppedShutdown);
        for (int index = 0; index < _maximumStripes; index++)
        {
            if (testingEnqueued is not null)
            {
                enqueued = SaturatingAdd(enqueued, Volatile.Read(ref testingEnqueued[index]));
            }

            if (testingDequeued is not null)
            {
                dequeued = SaturatingAdd(dequeued, Volatile.Read(ref testingDequeued[index]));
            }

            if (testingDroppedFull is not null)
            {
                droppedFull = SaturatingAdd(
                    droppedFull,
                    Volatile.Read(ref testingDroppedFull[index])
                );
            }

            if (testingDroppedShutdown is not null)
            {
                droppedShutdown = SaturatingAdd(
                    droppedShutdown,
                    Volatile.Read(ref testingDroppedShutdown[index])
                );
            }
        }

        return new ReadBufferStatistics(
            isDisposed,
            isDisposed ? 0 : queued,
            enqueued,
            dequeued,
            droppedFull,
            droppedShutdown,
            droppedFailed
        );
    }

    internal void AddStatisticsForTesting(
        int stripeIndex,
        long enqueued = 0,
        long dequeued = 0,
        long droppedFull = 0,
        long droppedShutdown = 0
    )
    {
        if ((uint)stripeIndex >= (uint)_maximumStripes)
        {
            throw new ArgumentOutOfRangeException(nameof(stripeIndex));
        }

        long[] testingEnqueued = EnsureTestingCounters(ref _testingEnqueued);
        long[] testingDequeued = EnsureTestingCounters(ref _testingDequeued);
        long[] testingDroppedFull = EnsureTestingCounters(ref _testingDroppedFull);
        long[] testingDroppedShutdown = EnsureTestingCounters(ref _testingDroppedShutdown);
        AddNonNegativeForTesting(ref testingEnqueued[stripeIndex], enqueued);
        AddNonNegativeForTesting(ref testingDequeued[stripeIndex], dequeued);
        AddNonNegativeForTesting(ref testingDroppedFull[stripeIndex], droppedFull);
        AddNonNegativeForTesting(ref testingDroppedShutdown[stripeIndex], droppedShutdown);
    }

    /// <summary>
    /// Stops admission, detaches the table, and releases all queued event references.
    /// </summary>
    /// <remarks>
    /// This method does not wait for a producer that has reserved a slot but has not published.
    /// Such a producer observes the per-ring disposed bit when it resumes and clears its slot.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RingBuffer<TEvent>?[]? table = Interlocked.Exchange(ref _table, DisposedTable);
        if (table is null || table.Length == 0)
        {
            return;
        }

        lock (_consumerGate)
        {
            for (int index = 0; index < table.Length; index++)
            {
                RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
                ring?.Dispose();
            }

            Volatile.Write(ref _retiredTable, table);
        }
    }

    private ReadBufferOfferResult ExpandOrRetry(
        TEvent value,
        int hash,
        int increment,
        bool wasUncontended
    )
    {
        bool collide = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_recordStatistics)
                {
                    SaturatingIncrement(ref _droppedShutdown);
                }
                return ReadBufferOfferResult.Shutdown;
            }

            RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
            if (table is not null && table.Length != 0)
            {
                int length = table.Length;
                int index = hash & (length - 1);
                RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
                if (ring is null)
                {
                    if (
                        Volatile.Read(ref _tableBusy) == 0
                        && Interlocked.CompareExchange(ref _tableBusy, 1, 0) == 0
                    )
                    {
                        try
                        {
                            if (
                                Volatile.Read(ref _disposed) == 0
                                && ReferenceEquals(Volatile.Read(ref _table), table)
                                && Volatile.Read(ref table[index]) is null
                            )
                            {
                                RingBuffer<TEvent> created = new(
                                    _stripeCapacity,
                                    value,
                                    _recordStatistics,
                                    _dropCounters,
                                    Volatile.Read(ref _beforeReserveForTesting),
                                    Volatile.Read(ref _beforePublishForTesting)
                                );
                                // Pair publication with the disposed recheck. Disposal may have
                                // detached this table and already inspected its previously empty
                                // slot; a release-only store could otherwise escape both checks.
                                Interlocked.Exchange(ref table[index], created);
                                if (Volatile.Read(ref _disposed) == 0)
                                {
                                    return ReadBufferOfferResult.Success;
                                }

                                DisposeCreatedRing(created);
                                return ReadBufferOfferResult.Shutdown;
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref _tableBusy, 0);
                        }
                    }

                    collide = false;
                }
                else if (!wasUncontended)
                {
                    wasUncontended = true;
                }
                else
                {
                    ReadBufferOfferResult result = ring.Offer(value);
                    if (result != ReadBufferOfferResult.Failed)
                    {
                        return result;
                    }

                    if (
                        length >= _maximumStripes
                        || !ReferenceEquals(Volatile.Read(ref _table), table)
                    )
                    {
                        collide = false;
                    }
                    else if (!collide)
                    {
                        collide = true;
                    }
                    else if (
                        Volatile.Read(ref _tableBusy) == 0
                        && Interlocked.CompareExchange(ref _tableBusy, 1, 0) == 0
                    )
                    {
                        try
                        {
                            if (
                                Volatile.Read(ref _disposed) == 0
                                && ReferenceEquals(Volatile.Read(ref _table), table)
                            )
                            {
                                RingBuffer<TEvent>?[] expanded = new RingBuffer<TEvent>?[
                                    length << 1
                                ];
                                for (int copyIndex = 0; copyIndex < length; copyIndex++)
                                {
                                    expanded[copyIndex] = Volatile.Read(ref table[copyIndex]);
                                }
                                if (
                                    ReferenceEquals(
                                        Interlocked.CompareExchange(ref _table, expanded, table),
                                        table
                                    )
                                )
                                {
                                    collide = false;
                                    hash += increment;
                                    continue;
                                }
                            }
                        }
                        finally
                        {
                            Volatile.Write(ref _tableBusy, 0);
                        }

                        collide = false;
                    }
                }

                hash += increment;
                continue;
            }

            if (
                Volatile.Read(ref _tableBusy) != 0
                || Interlocked.CompareExchange(ref _tableBusy, 1, 0) != 0
            )
            {
                continue;
            }

            try
            {
                if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _table) is null)
                {
                    RingBuffer<TEvent> initializedRing = new(
                        _stripeCapacity,
                        value,
                        _recordStatistics,
                        _dropCounters,
                        Volatile.Read(ref _beforeReserveForTesting),
                        Volatile.Read(ref _beforePublishForTesting)
                    );
                    RingBuffer<TEvent>?[] initialized = [initializedRing];
                    if (
                        ReferenceEquals(
                            Interlocked.CompareExchange(ref _table, initialized, null),
                            null
                        )
                    )
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            return ReadBufferOfferResult.Success;
                        }

                        DisposeCreatedRing(initializedRing);
                        return ReadBufferOfferResult.Shutdown;
                    }

                    DisposeCreatedRing(initializedRing);
                }
            }
            finally
            {
                Volatile.Write(ref _tableBusy, 0);
            }
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            return ReadBufferOfferResult.Failed;
        }

        if (_recordStatistics)
        {
            SaturatingIncrement(ref _droppedShutdown);
        }
        return ReadBufferOfferResult.Shutdown;
    }

    private void DisposeCreatedRing(RingBuffer<TEvent> ring)
    {
        // A just-published ring may already be in the consumer's captured table. Its creator
        // must use the same shutdown gate before clearing slots, even after the table detaches.
        lock (_consumerGate)
        {
            ring.Dispose();
        }
    }

    private static void AddRingStatistics(
        RingBuffer<TEvent>?[] table,
        ref long queued,
        ref long enqueued,
        ref long dequeued,
        ref long droppedShutdown
    )
    {
        for (int index = 0; index < table.Length; index++)
        {
            RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
            if (ring is null)
            {
                continue;
            }

            queued = SaturatingAdd(queued, ring.Queued);
            enqueued = SaturatingAdd(enqueued, ring.Enqueued);
            dequeued = SaturatingAdd(dequeued, ring.Dequeued);
            droppedShutdown = SaturatingAdd(droppedShutdown, ring.DroppedShutdown);
        }
    }

    private static void AddNonNegativeForTesting(ref long location, long delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        SaturatingAdd(ref location, delta);
    }

    private long[] EnsureTestingCounters(ref long[]? counters)
    {
        long[]? existing = Volatile.Read(ref counters);
        if (existing is not null)
        {
            return existing;
        }

        long[] created = new long[_maximumStripes];
        Interlocked.CompareExchange(ref counters, created, null);
        return Volatile.Read(ref counters)
            ?? throw new InvalidOperationException("Testing counters were not published.");
    }

    private static void SaturatingIncrement(ref long location) => SaturatingAdd(ref location, 1);

    private static void RecordDrop(long[] counters, int offset)
    {
        int mask = counters.Length / DropCounterStride - 1;
        int stripe = Environment.CurrentManagedThreadId & mask;
        SaturatingIncrement(ref counters[stripe * DropCounterStride + offset]);
    }

    private static void SaturatingAdd(ref long location, long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        while (true)
        {
            long current = Volatile.Read(ref location);
            if (current == long.MaxValue)
            {
                return;
            }

            long next = delta >= long.MaxValue - current ? long.MaxValue : current + delta;
            if (Interlocked.CompareExchange(ref location, next, current) == current)
            {
                return;
            }
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        right >= long.MaxValue - left ? long.MaxValue : left + right;

    private sealed class RingBuffer<T>
    {
        private Slot[]? _slots;
        private readonly int _capacity;
        private readonly int _mask;
        private readonly bool _recordStatistics;
        private readonly long[]? _dropCounters;
        private Action? _beforeReserveForTesting;
        private Action? _beforePublishForTesting;
        private Action<Action>? _afterShutdownSnapshotForTesting;
        private long _readCounter;
        private long _writeCounter;
        private long _enqueued;
        private long _dequeued;
        private long _droppedShutdown;
        private int _forcedCasFailuresForTesting;
        private int _disposed;

        internal RingBuffer(
            int capacity,
            T value,
            bool recordStatistics,
            long[]? dropCounters,
            Action? beforeReserveForTesting = null,
            Action? beforePublishForTesting = null
        )
        {
            _capacity = capacity;
            _mask = capacity - 1;
            _recordStatistics = recordStatistics;
            _dropCounters = dropCounters;
            _beforeReserveForTesting = beforeReserveForTesting;
            _beforePublishForTesting = beforePublishForTesting;
            _slots = new Slot[capacity];
            for (int index = 0; index < capacity; index++)
            {
                _slots[index] = new Slot(index);
            }

            _slots[0].Value = value;
            Volatile.Write(ref _slots[0].Sequence, 1);
            Volatile.Write(ref _writeCounter, 1);
            if (_recordStatistics)
            {
                SaturatingIncrement(ref _enqueued);
            }
        }

        internal void SetHooks(Action? beforeReserve, Action? beforePublish)
        {
            Volatile.Write(ref _beforeReserveForTesting, beforeReserve);
            Volatile.Write(ref _beforePublishForTesting, beforePublish);
        }

        internal void SetCounterForTesting(long counter)
        {
            Slot[]? slots = Volatile.Read(ref _slots);
            ObjectDisposedException.ThrowIf(slots is null, nameof(StripedReadBuffer<T>));

            for (int index = 0; index < slots.Length; index++)
            {
                slots[index].Value = default!;
            }

            ulong start = unchecked((ulong)counter);
            for (int index = 0; index < slots.Length; index++)
            {
                ulong remainder = start % (ulong)_capacity;
                ulong delta = ((ulong)index + (ulong)_capacity - remainder) % (ulong)_capacity;
                Volatile.Write(ref slots[index].Sequence, unchecked((long)(start + delta)));
            }

            Volatile.Write(ref _readCounter, counter);
            Volatile.Write(ref _writeCounter, counter);
            Volatile.Write(ref _enqueued, 0);
            Volatile.Write(ref _dequeued, 0);
            Volatile.Write(ref _droppedShutdown, 0);
        }

        internal void SetForcedCasFailuresForTesting(int failures) =>
            Volatile.Write(ref _forcedCasFailuresForTesting, failures);

        internal void SetShutdownHook(Action<Action>? afterSnapshot) =>
            Volatile.Write(ref _afterShutdownSnapshotForTesting, afterSnapshot);

        internal bool HasPublished
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                long head = Volatile.Read(ref _readCounter);
                long tail = Volatile.Read(ref _writeCounter);
                if (unchecked((ulong)(tail - head)) == 0)
                {
                    return false;
                }

                Slot[]? slots = Volatile.Read(ref _slots);
                if (slots is null)
                {
                    return false;
                }

                ref Slot slot = ref slots[unchecked((int)head) & _mask];
                return Volatile.Read(ref slot.Sequence) == unchecked(head + 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadBufferOfferResult Offer(T value)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (_recordStatistics)
                {
                    SaturatingIncrement(ref _droppedShutdown);
                }
                return ReadBufferOfferResult.Shutdown;
            }

            long tail = Volatile.Read(ref _writeCounter);
            long head = Volatile.Read(ref _readCounter);
            if (unchecked((ulong)(tail - head)) < (ulong)_capacity)
            {
                return OfferAvailable(value, tail);
            }

            if (_recordStatistics)
            {
                RecordDrop(_dropCounters!, DroppedFullOffset);
            }
            return ReadBufferOfferResult.Full;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ReadBufferOfferResult OfferAvailable(T value, long tail)
        {
            Slot[]? slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                if (_recordStatistics)
                {
                    SaturatingIncrement(ref _droppedShutdown);
                }
                return ReadBufferOfferResult.Shutdown;
            }

            ref Slot slot = ref slots[unchecked((int)tail) & _mask];
            if (Volatile.Read(ref slot.Sequence) != tail)
            {
                return ReadBufferOfferResult.Failed;
            }

            Volatile.Read(ref _beforeReserveForTesting)?.Invoke();
            while (true)
            {
                int remaining = Volatile.Read(ref _forcedCasFailuresForTesting);
                if (remaining == 0)
                {
                    break;
                }

                if (
                    Interlocked.CompareExchange(
                        ref _forcedCasFailuresForTesting,
                        remaining - 1,
                        remaining
                    ) == remaining
                )
                {
                    return ReadBufferOfferResult.Failed;
                }
            }

            if (Interlocked.CompareExchange(ref _writeCounter, unchecked(tail + 1), tail) != tail)
            {
                return ReadBufferOfferResult.Failed;
            }

            Volatile.Read(ref _beforePublishForTesting)?.Invoke();
            slot.Value = value;
            if (_recordStatistics)
            {
                SaturatingIncrement(ref _enqueued);
            }
            // Publication must precede the disposed read with full-fence ordering. A release
            // store followed by an acquire read can miss disposal while the publication is
            // still buffered, after the disposer has already cleared and inspected this slot.
            Interlocked.Exchange(ref slot.Sequence, unchecked(tail + 1));

            if (Volatile.Read(ref _disposed) == 0)
            {
                return ReadBufferOfferResult.Success;
            }

            slot.Value = default!;
            if (
                _recordStatistics
                // A producer can be delayed after its event was consumed. In a one-slot
                // ring the next free sequence equals that old publication's sequence.
                && unchecked((ulong)(tail - Volatile.Read(ref _readCounter))) < (ulong)_capacity
                && TryClaimShutdownDrop(ref slot, tail)
            )
            {
                SaturatingIncrement(ref _droppedShutdown);
            }

            return ReadBufferOfferResult.Shutdown;
        }

        internal bool TryRead(out T value)
        {
            long head = Volatile.Read(ref _readCounter);
            long tail = Volatile.Read(ref _writeCounter);
            if (unchecked((ulong)(tail - head)) == 0)
            {
                value = default!;
                return false;
            }

            Slot[]? slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                value = default!;
                return false;
            }

            ref Slot slot = ref slots[unchecked((int)head) & _mask];
            if (Volatile.Read(ref slot.Sequence) != unchecked(head + 1))
            {
                value = default!;
                return false;
            }

            value = slot.Value;
            slot.Value = default!;
            Volatile.Write(ref slot.Sequence, unchecked(head + _capacity));
            Volatile.Write(ref _readCounter, unchecked(head + 1));
            if (_recordStatistics)
            {
                Volatile.Write(ref _dequeued, SaturatingAdd(_dequeued, 1));
            }
            return true;
        }

        internal int DrainTo(Action<T> consumer, int budget)
        {
            Slot[]? slots = Volatile.Read(ref _slots);
            if (slots is null)
            {
                return 0;
            }

            long head = Volatile.Read(ref _readCounter);
            long tail = Volatile.Read(ref _writeCounter);
            int drained = 0;
            try
            {
                while (drained < budget && head != tail)
                {
                    ref Slot slot = ref slots[unchecked((int)head) & _mask];
                    if (Volatile.Read(ref slot.Sequence) != unchecked(head + 1))
                    {
                        break;
                    }

                    T value = slot.Value;
                    slot.Value = default!;
                    Volatile.Write(ref slot.Sequence, unchecked(head + _capacity));
                    head = unchecked(head + 1);
                    drained++;
                    consumer(value);
                }
            }
            finally
            {
                // The single consumer releases capacity once per bounded batch, including the
                // consumed prefix when a callback throws. Producers never wait for this update.
                Volatile.Write(ref _readCounter, head);
                if (_recordStatistics)
                {
                    Volatile.Write(ref _dequeued, SaturatingAdd(_dequeued, drained));
                }
            }

            return drained;
        }

        internal int Queued
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return 0;
                }

                Slot[]? slots = Volatile.Read(ref _slots);
                if (slots is null)
                {
                    return 0;
                }

                // Diagnostics scan the bounded reservation range instead of making every
                // successful producer and consumer update another shared atomic counter. A
                // published slot beyond a paused head still counts, but a reservation does not.
                long head = Volatile.Read(ref _readCounter);
                long tail = Volatile.Read(ref _writeCounter);
                int count = (int)Math.Min(unchecked((ulong)(tail - head)), (ulong)_capacity);
                int queued = 0;
                for (int offset = 0; offset < count; offset++)
                {
                    long position = unchecked(head + offset);
                    ref Slot slot = ref slots[unchecked((int)position) & _mask];
                    if (Volatile.Read(ref slot.Sequence) == unchecked(position + 1))
                    {
                        queued++;
                    }
                }

                return queued;
            }
        }

        internal long Enqueued => Volatile.Read(ref _enqueued);
        internal long Dequeued => Volatile.Read(ref _dequeued);
        internal long DroppedShutdown => Volatile.Read(ref _droppedShutdown);

        internal void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            {
                return;
            }

            long head = Volatile.Read(ref _readCounter);
            long tail = Volatile.Read(ref _writeCounter);
            Volatile.Read(ref _afterShutdownSnapshotForTesting)?.Invoke(Dispose);

            // A paused producer's interior reference keeps this whole array alive. Detach the
            // cache-owned root and clear every value so unrelated queued events can be collected
            // immediately. A late publisher clears its own value after seeing the disposed bit.
            Slot[]? slots = Interlocked.Exchange(ref _slots, null);
            if (slots is null)
            {
                return;
            }

            for (int index = 0; index < slots.Length; index++)
            {
                slots[index].Value = default!;
            }

            if (!_recordStatistics)
            {
                return;
            }

            int count = (int)Math.Min(unchecked((ulong)(tail - head)), (ulong)_capacity);
            int dropped = 0;
            for (int offset = 0; offset < count; offset++)
            {
                long position = unchecked(head + offset);
                ref Slot slot = ref slots[unchecked((int)position) & _mask];
                if (TryClaimShutdownDrop(ref slot, position))
                {
                    dropped++;
                }
            }
            SaturatingAdd(ref _droppedShutdown, dropped);
        }

        private static bool TryClaimShutdownDrop(ref Slot slot, long position)
        {
            // Disposal and a late publisher may both observe this publication. Exactly one
            // claims its shutdown drop; an unpublished reservation is counted when it publishes.
            long published = unchecked(position + 1);
            return Interlocked.CompareExchange(ref slot.Sequence, position, published) == published;
        }

        private struct Slot
        {
            internal Slot(long sequence)
            {
                Sequence = sequence;
            }

            internal T Value = default!;
            internal long Sequence;
        }
    }
}

/// <summary>Shares only a scalar probe across buffers; thread state never roots a cache.</summary>
internal static class ReadBufferThreadProbe
{
    [ThreadStatic]
    private static ulong _value;

    internal static ulong ExchangeForTesting(ulong value)
    {
        ulong previous = _value;
        _value = value;
        return previous;
    }

    internal static ulong Value
    {
        get
        {
            ulong value = _value;
            if (value != 0)
            {
                return value;
            }

            value = unchecked((uint)Environment.CurrentManagedThreadId);
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            _value = value ^= value >> 31;

            return value;
        }
    }
}
