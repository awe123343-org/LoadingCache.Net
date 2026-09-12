/*
 * Copyright 2017 Ben Manes. All Rights Reserved.
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

namespace LoadingCache.Expiration;

internal static class TimerWheelMetadata
{
    internal static readonly int[] BucketCounts = [64, 64, 32, 4, 1];
    internal static readonly ulong[] BucketSpans = [1, 64, 4_096, 131_072, 524_288];
    internal static long NextOwnerId;
}

// Hierarchy and bucket-cascade design adapted from Caffeine 3.2.4's
// TimerWheel.java at commit 836b65c0a83e5d1641ded9c6de578654bc04b2e9:
// https://github.com/ben-manes/caffeine/blob/836b65c0a83e5d1641ded9c6de578654bc04b2e9/caffeine/src/main/java/com/github/benmanes/caffeine/cache/TimerWheel.java
// This .NET implementation redefines the ownership, normalized tick, bounded
// advancement, and callback boundary for this repository's engine.

/// <summary>
/// A five-level hierarchical timer wheel for monotonic normalized timestamps.
/// </summary>
/// <typeparam name="T">The value associated with each timer node.</typeparam>
internal sealed class TimerWheel<T>
    where T : notnull
{
    // The base unit is one normalized tick. The parent engine converts its
    // TimeProvider timestamp into this unit before crossing the module boundary.
    private readonly Bucket[] _buckets;
    private readonly Queue<BucketWork> _work = new();
    private readonly bool[][] _queued;
    private readonly long _ownerId = Interlocked.Increment(ref TimerWheelMetadata.NextOwnerId);
    internal ulong CurrentTime { get; private set; }
    private ulong _nextLinkSequence;
    internal int Count { get; private set; }

    internal TimerWheel(ulong initialTime = 0)
    {
        CurrentTime = initialTime;
        _buckets = new Bucket[TimerWheelMetadata.BucketCounts.Sum()];
        _queued = new bool[TimerWheelMetadata.BucketCounts.Length][];

        int offset = 0;
        for (int level = 0; level < TimerWheelMetadata.BucketCounts.Length; level++)
        {
            _queued[level] = new bool[TimerWheelMetadata.BucketCounts[level]];
            for (int index = 0; index < TimerWheelMetadata.BucketCounts[level]; index++)
            {
                _buckets[offset + index] = new Bucket(level, index);
            }

            offset += TimerWheelMetadata.BucketCounts[level];
        }
    }

    private bool HasPendingWork => _work.Count != 0;

    internal void Schedule(IdentityTimerNode<T> node, ulong deadline)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateDeadline(deadline);
        if (node.IsRetired)
        {
            throw new InvalidOperationException("A retired timer node cannot be scheduled.");
        }

        if (node.IsScheduled || node.OwnerId != 0)
        {
            throw new InvalidOperationException("A timer node is already scheduled.");
        }

        node.SetDeadline(deadline);
        LinkToWheel(node);
        Count++;
    }

    internal bool Deschedule(IdentityTimerNode<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsOwned(node) || !node.IsScheduled)
        {
            return false;
        }

        Unlink(node);
        Count--;
        return true;
    }

    internal bool Reschedule(IdentityTimerNode<T> node, ulong deadline)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateDeadline(deadline);
        if (node.IsRetired || !IsOwned(node) || !node.IsScheduled)
        {
            return false;
        }

        Unlink(node);
        Count--;
        node.SetDeadline(deadline);
        LinkToWheel(node);
        Count++;
        return true;
    }

    internal bool Retire(IdentityTimerNode<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsRetired || (node.IsScheduled && !IsOwned(node)))
        {
            return false;
        }

        Deschedule(node);
        node.Retire();
        return true;
    }

    /// <summary>
    /// Advances to a monotonic normalized timestamp and returns due nodes.
    /// </summary>
    /// <param name="now">The new timestamp, represented as an unchecked <see cref="ulong"/>.</param>
    /// <param name="budget">The maximum number of nodes to visit in this call.</param>
    /// <returns>A bounded batch and continuation state.</returns>
    internal TimerAdvanceResult<T> Advance(ulong now, int budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ValidateAdvance(now);

        ulong previous = CurrentTime;
        if (IsForward(previous, now))
        {
            EnqueueElapsedBuckets(previous, now);
            CurrentTime = now;
        }
        // Backward clock movement leaves CurrentTime unchanged. Existing due work
        // can still be drained at the current monotonic time.

        if (_work.Count == 0)
        {
            Enqueue(0, BucketIndex(0, CurrentTime));
        }

        List<IdentityTimerNode<T>> dueNodes = [];
        int visited = 0;

        while (budget > 0 && _work.Count != 0)
        {
            BucketWork work = _work.Dequeue();
            _queued[work.Level][work.Index] = false;
            ProcessBucket(work, ref budget, ref visited, dueNodes);
        }

        return new TimerAdvanceResult<T>(dueNodes, HasPendingWork);
    }

    internal ulong GetNextDelay()
    {
        if (HasPendingWork)
        {
            return 0;
        }

        ulong minimum = ulong.MaxValue;
        for (int level = 0; level < TimerWheelMetadata.BucketCounts.Length; level++)
        {
            ulong span = TimerWheelMetadata.BucketSpans[level];
            ulong currentTick = CurrentTime / span;
            ulong currentIndex = currentTick & (ulong)(TimerWheelMetadata.BucketCounts[level] - 1);

            for (int index = 0; index < TimerWheelMetadata.BucketCounts[level]; index++)
            {
                if (GetBucket(level, index).Head is null)
                {
                    continue;
                }

                // A level-0 bucket represents one exact normalized tick. A node
                // linked to the current level-0 bucket is therefore already due:
                // future deadlines in the same wheel rotation are placed in a
                // higher level. Avoid walking an unbounded bucket here.
                if (level == 0 && index == (int)currentIndex)
                {
                    return 0;
                }

                ulong distance =
                    ((ulong)index - currentIndex)
                    & (ulong)(TimerWheelMetadata.BucketCounts[level] - 1);
                ulong remainder = CurrentTime % span;
                ulong delay =
                    distance == 0 ? span - remainder : checked(distance * span) - remainder;
                if (delay == 0)
                {
                    delay = span;
                }

                minimum = Math.Min(minimum, delay);
            }
        }

        return minimum;
    }

    internal void AssertInvariants()
    {
        HashSet<IdentityTimerNode<T>> seen = [];
        int count = 0;

        foreach (Bucket bucket in _buckets)
        {
            IdentityTimerNode<T>? previous = null;
            ulong previousSequence = 0;
            for (IdentityTimerNode<T>? node = bucket.Head; node is not null; node = node.Next)
            {
                if (!seen.Add(node))
                {
                    throw new InvalidOperationException("A timer node appears more than once.");
                }

                if (
                    node.BucketId != bucket.Id
                    || node.OwnerId != _ownerId
                    || node.LinkSequence == 0
                    || node.LinkSequence <= previousSequence
                    || node.Previous != previous
                    || node.IsRetired
                )
                {
                    throw new InvalidOperationException("A timer bucket link is inconsistent.");
                }

                previous = node;
                previousSequence = node.LinkSequence;
                count++;
            }

            if (bucket.Tail != previous)
            {
                throw new InvalidOperationException("A timer bucket tail is inconsistent.");
            }
        }

        if (count != Count)
        {
            throw new InvalidOperationException("Timer wheel count is inconsistent.");
        }
    }

    private void ProcessBucket(
        BucketWork work,
        ref int budget,
        ref int visited,
        List<IdentityTimerNode<T>> dueNodes
    )
    {
        Bucket bucket = GetBucket(work.Level, work.Index);
        IdentityTimerNode<T>? node = bucket.Head;
        ulong frontier = work.FrontierSequence;
        if (frontier == 0)
        {
            frontier = bucket.Tail?.LinkSequence ?? 0;
        }

        while (node is not null)
        {
            IdentityTimerNode<T>? next = node.Next;

            // Nodes linked after this work item was captured belong to a later
            // logical batch. They must not be consumed by this continuation,
            // even when the original tail was descheduled or rescheduled.
            if (node.LinkSequence > frontier)
            {
                // Every link is appended at the bucket tail with a strictly
                // increasing sequence. The first post-frontier node proves
                // that no later node belongs to this work item, so stop rather
                // than scanning an unbounded suffix without consuming budget.
                break;
            }

            Unlink(node);
            Count--;

            visited++;
            budget--;
            Process(node, dueNodes);

            if (budget == 0)
            {
                Enqueue(work.Level, work.Index, frontier);
                break;
            }

            node = next;
        }
    }

    private void Process(IdentityTimerNode<T> node, List<IdentityTimerNode<T>> dueNodes)
    {
        if (node.IsRetired)
        {
            return;
        }

        if (IsDue(node.Deadline, CurrentTime))
        {
            node.BucketId = IdentityTimerNode<T>.NotScheduled;
            node.OwnerId = 0;
            node.Previous = null;
            node.Next = null;
            dueNodes.Add(node);
            return;
        }

        LinkToWheel(node);
        Count++;
    }

    private void EnqueueElapsedBuckets(ulong previous, ulong now)
    {
        ulong distance = now - previous;
        if (distance == 0)
        {
            return;
        }

        for (int level = TimerWheelMetadata.BucketCounts.Length - 1; level >= 0; level--)
        {
            ulong span = TimerWheelMetadata.BucketSpans[level];
            ulong previousTick = previous / span;
            ulong currentTick = now / span;
            ulong tickDelta = currentTick - previousTick;
            if (tickDelta == 0)
            {
                continue;
            }

            ulong count = Math.Min((ulong)TimerWheelMetadata.BucketCounts[level], tickDelta + 1);

            for (ulong offset = 0; offset < count; offset++)
            {
                int index = (int)(
                    (previousTick + offset) & (ulong)(TimerWheelMetadata.BucketCounts[level] - 1)
                );
                Enqueue(level, index);
            }
        }
    }

    private void Enqueue(int level, int index, ulong frontierSequence = 0)
    {
        if (_queued[level][index])
        {
            return;
        }

        _queued[level][index] = true;
        _work.Enqueue(new BucketWork(level, index, frontierSequence));
    }

    private void LinkToWheel(IdentityTimerNode<T> node)
    {
        int level = FindLevel(node.Deadline);
        int index = IsDue(node.Deadline, CurrentTime)
            ? BucketIndex(0, CurrentTime)
            : BucketIndex(level, node.Deadline);
        Bucket bucket = GetBucket(level, index);

        if (_nextLinkSequence == ulong.MaxValue)
        {
            throw new InvalidOperationException("Timer link sequence exhausted.");
        }

        node.BucketId = bucket.Id;
        node.OwnerId = _ownerId;
        node.LinkSequence = ++_nextLinkSequence;
        node.Previous = bucket.Tail;
        node.Next = null;
        if (bucket.Tail is null)
        {
            bucket.Head = node;
        }
        else
        {
            bucket.Tail.Next = node;
        }

        bucket.Tail = node;
    }

    private void Unlink(IdentityTimerNode<T> node)
    {
        if (node.BucketId < 0)
        {
            return;
        }

        Bucket bucket = _buckets[node.BucketId];
        IdentityTimerNode<T>? previousNode = node.Previous;
        IdentityTimerNode<T>? nextNode = node.Next;
        if (previousNode is null)
        {
            bucket.Head = nextNode;
        }
        else
        {
            previousNode.Next = nextNode;
        }

        if (nextNode is null)
        {
            bucket.Tail = previousNode;
        }
        else
        {
            nextNode.Previous = previousNode;
        }

        node.BucketId = IdentityTimerNode<T>.NotScheduled;
        node.OwnerId = 0;
        node.LinkSequence = 0;
        node.Previous = null;
        node.Next = null;
    }

    private Bucket GetBucket(int level, int index)
    {
        int offset = 0;
        for (int i = 0; i < level; i++)
        {
            offset += TimerWheelMetadata.BucketCounts[i];
        }

        return _buckets[offset + index];
    }

    private int FindLevel(ulong deadline)
    {
        ulong distance = deadline - CurrentTime;
        if (unchecked((long)distance) <= 0)
        {
            return 0;
        }

        for (int level = 0; level < TimerWheelMetadata.BucketSpans.Length - 1; level++)
        {
            if (distance < TimerWheelMetadata.BucketSpans[level + 1])
            {
                return level;
            }
        }

        return TimerWheelMetadata.BucketSpans.Length - 1;
    }

    private static int BucketIndex(int level, ulong time)
    {
        return (int)(
            (time / TimerWheelMetadata.BucketSpans[level])
            & (ulong)(TimerWheelMetadata.BucketCounts[level] - 1)
        );
    }

    private static bool IsDue(ulong deadline, ulong now)
    {
        return unchecked((long)(deadline - now)) <= 0;
    }

    private static bool IsForward(ulong previous, ulong current)
    {
        return unchecked((long)(current - previous)) >= 0;
    }

    private bool IsOwned(IdentityTimerNode<T> node)
    {
        return node.OwnerId == _ownerId;
    }

    private void ValidateDeadline(ulong deadline)
    {
        if (deadline > CurrentTime && deadline - CurrentTime > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deadline),
                deadline,
                "A timer deadline must be within long.MaxValue normalized ticks of the wheel clock."
            );
        }
    }

    private void ValidateAdvance(ulong now)
    {
        if (now > CurrentTime && now - CurrentTime > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(now),
                now,
                "A timer advance must not exceed long.MaxValue normalized ticks."
            );
        }
    }

    private readonly record struct BucketWork(int Level, int Index, ulong FrontierSequence);

    private sealed class Bucket(int level, int index)
    {
        internal int Id { get; } = GetBucketId(level, index);

        internal IdentityTimerNode<T>? Head { get; set; }

        internal IdentityTimerNode<T>? Tail { get; set; }
    }

    private static int GetBucketId(int level, int index)
    {
        int offset = 0;
        for (int i = 0; i < level; i++)
        {
            offset += TimerWheelMetadata.BucketCounts[i];
        }

        return offset + index;
    }
}
