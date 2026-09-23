using System.Globalization;
using LoadingCache.Expiration;

namespace LoadingCache.Tests;

public sealed class TimerWheelTests
{
    [Test]
    public async Task ExpiresNodesAtEachHierarchyBoundary()
    {
        TimerWheel<string> wheel = new();
        ulong[] deadlines =
        [
            1,
            63,
            64,
            65,
            4_095,
            4_096,
            4_097,
            131_071,
            131_072,
            131_073,
            524_287,
            524_288,
        ];
        IdentityTimerNode<string>[] nodes =
        [
            .. deadlines.Select(
                (deadline, index) =>
                {
                    IdentityTimerNode<string> node = new(
                        index.ToString(CultureInfo.InvariantCulture)
                    );
                    wheel.Schedule(node, deadline);
                    return node;
                }
            ),
        ];
        List<IdentityTimerNode<string>> due = Drain(wheel, 524_288, 64);
        await Assert
            .That(due.Select(node => node.Value))
            .IsEquivalentTo(nodes.Select(node => node.Value));
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task SameBucketFutureNodeIsNotReturnedEarly()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("future");
        wheel.Schedule(node, 60);
        await Assert.That(wheel.Advance(10, 32).DueNodes).IsEmpty();
        await Assert.That(node.IsScheduled).IsTrue();
        await Assert.That(wheel.Advance(59, 32).DueNodes).IsEmpty();
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(wheel.Advance(60, 32).DueNodes).HasSingleItem()),
                    node
                )
            )
            .IsTrue();
        wheel.AssertInvariants();
    }

    [Test]
    public async Task RescheduleAndDeschedulePreserveIdentityLinks()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> rescheduled = new("rescheduled");
        IdentityTimerNode<string> removed = new("removed");
        wheel.Schedule(rescheduled, 100);
        await Assert.That(wheel.Reschedule(rescheduled, 5)).IsTrue();
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(wheel.Advance(5, 8).DueNodes).HasSingleItem()),
                    rescheduled
                )
            )
            .IsTrue();
        wheel.Schedule(removed, 200);
        await Assert.That(wheel.Deschedule(removed)).IsTrue();
        await Assert.That(wheel.Deschedule(removed)).IsFalse();
        await Assert.That(wheel.Advance(200, 8).DueNodes).IsEmpty();
        await Assert.That(wheel.Retire(rescheduled)).IsTrue();
        await Assert.That(wheel.Retire(rescheduled)).IsFalse();
        wheel.AssertInvariants();
    }

    [Test]
    public async Task RetiringAQueuedNodeDoesNotStrandItsBucket()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> retired = new("retired");
        IdentityTimerNode<string> survivor = new("survivor");
        wheel.Schedule(retired, 64);
        wheel.Schedule(survivor, 65);
        await Assert.That(wheel.Retire(retired)).IsTrue();
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(wheel.Advance(65, 16).DueNodes).HasSingleItem()),
                    survivor
                )
            )
            .IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task BudgetReturnsAContinuationWithoutDroppingNodes()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string>[] nodes =
        [
            .. Enumerable
                .Range(1, 32)
                .Select(value => new IdentityTimerNode<string>(
                    value.ToString(CultureInfo.InvariantCulture)
                )),
        ];
        foreach (IdentityTimerNode<string> node in nodes)
        {
            wheel.Schedule(node, uint.Parse(node.Value, CultureInfo.InvariantCulture));
        }

        HashSet<IdentityTimerNode<string>> due = [];
        TimerAdvanceResult<string> result = wheel.Advance(32, 3);
        while (true)
        {
            due.UnionWith(result.DueNodes);
            if (!result.HasPending)
            {
                break;
            }

            result = wheel.Advance(32, 3);
        }

        await Assert.That(due).IsEquivalentTo(nodes);
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task BudgetContinuationDoesNotReprocessRescheduledNodes()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string>[] nodes =
        [
            .. Enumerable
                .Range(0, 100)
                .Select(value => new IdentityTimerNode<string>(
                    value.ToString(CultureInfo.InvariantCulture)
                )),
        ];
        foreach (IdentityTimerNode<string> node in nodes)
        {
            wheel.Schedule(node, 1);
        }

        List<IdentityTimerNode<string>> due = Drain(wheel, 1, 10);
        await Assert.That(due).IsEquivalentTo(nodes);
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task DescheduledContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);
        await Assert.That(wheel.Advance(524_288, 1).HasPending).IsTrue();
        await Assert.That(wheel.Deschedule(tail)).IsTrue();
        for (int value = 3; value < 10_003; value++)
        {
            wheel.Schedule(new IdentityTimerNode<int>(value), 100_000_000);
        }

        bool settled = false;
        for (int i = 0; i < 100; i++)
        {
            if (wheel.Advance(524_288, 1).HasPending)
            {
                continue;
            }

            settled = true;
            break;
        }

        await Assert.That(settled).IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(10_001);
        await Assert.That(wheel.GetNextDelay()).IsGreaterThan(0UL);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task RetiredContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);
        await Assert.That(wheel.Advance(524_288, 1).HasPending).IsTrue();
        await Assert.That(wheel.Retire(tail)).IsTrue();
        bool settled = false;
        for (int i = 0; i < 100; i++)
        {
            if (wheel.Advance(524_288, 1).HasPending)
            {
                continue;
            }

            settled = true;
            break;
        }

        await Assert.That(settled).IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(1);
        await Assert.That(wheel.GetNextDelay()).IsGreaterThan(0UL);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task RescheduledContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);
        await Assert.That(wheel.Advance(524_288, 1).HasPending).IsTrue();
        await Assert.That(wheel.Reschedule(tail, 600_000)).IsTrue();
        bool settled = false;
        for (int i = 0; i < 100; i++)
        {
            if (wheel.Advance(524_288, 1).HasPending)
            {
                continue;
            }

            settled = true;
            break;
        }

        await Assert.That(settled).IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(2);
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(wheel.Advance(600_000, 16).DueNodes).HasSingleItem()),
                    tail
                )
            )
            .IsTrue();
        wheel.AssertInvariants();
    }

    [Test]
    public async Task CrossWheelDescheduleCannotMutateTheOwningWheel()
    {
        TimerWheel<string> owner = new();
        TimerWheel<string> other = new();
        IdentityTimerNode<string> node = new("owned");
        owner.Schedule(node, 100);
        await Assert.That(other.Deschedule(node)).IsFalse();
        await Assert.That(node.IsScheduled).IsTrue();
        await Assert.That(other.Retire(node)).IsFalse();
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(owner.Advance(100, 16).DueNodes).HasSingleItem()),
                    node
                )
            )
            .IsTrue();
        owner.AssertInvariants();
        other.AssertInvariants();
    }

    [Test]
    public async Task WrapAroundUsesUnsignedMonotonicDistance()
    {
        const ulong start = ulong.MaxValue - 2;
        TimerWheel<string> wheel = new(start);
        IdentityTimerNode<string> beforeWrap = new("before-wrap");
        IdentityTimerNode<string> afterWrap = new("after-wrap");
        wheel.Schedule(beforeWrap, unchecked(start + 1));
        wheel.Schedule(afterWrap, unchecked(start + 4));
        TimerAdvanceResult<string> result = wheel.Advance(1, 16);
        await Assert.That(result.DueNodes).IsEquivalentTo([beforeWrap, afterWrap]);
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task LargeForwardJumpVisitsOuterWheelAndExpiresDueNodes()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("large-jump");
        wheel.Schedule(node, 1000);
        TimerAdvanceResult<string> result = wheel.Advance(1_000_000, 128);
        await Assert
            .That(ReferenceEquals((await Assert.That(result.DueNodes).HasSingleItem()), node))
            .IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task PastDeadlineIsPlacedInTheCurrentBucketForImmediateCleanup()
    {
        TimerWheel<string> wheel = new(100);
        IdentityTimerNode<string> node = new("past");
        wheel.Schedule(node, 50);
        await Assert.That(wheel.GetNextDelay()).IsEqualTo(0UL);
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(wheel.Advance(100, 16).DueNodes).HasSingleItem()),
                    node
                )
            )
            .IsTrue();
        wheel.AssertInvariants();
    }

    [Test]
    public async Task DeadlineBeyondTheSignedHalfRangeIsRejected()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("too-far");
        Action action = () => wheel.Schedule(node, (ulong)long.MaxValue + 1);
        await Assert.That(action).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MaximumSupportedForwardDurationUsesTheOuterWheel()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("maximum");
        wheel.Schedule(node, long.MaxValue);
        await Assert
            .That(
                ReferenceEquals(
                    (
                        await Assert
                            .That(wheel.Advance(long.MaxValue, int.MaxValue).DueNodes)
                            .HasSingleItem()
                    ),
                    node
                )
            )
            .IsTrue();
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task BackwardClockMoveDoesNotExpireFutureNodes()
    {
        TimerWheel<string> wheel = new(100);
        IdentityTimerNode<string> node = new("future");
        wheel.Schedule(node, 200);
        await Assert.That(wheel.Advance(90, 16).DueNodes).IsEmpty();
        await Assert.That(node.IsScheduled).IsTrue();
        await Assert.That(wheel.CurrentTime).IsEqualTo(100UL);
        wheel.AssertInvariants();
    }

    [Test]
    public async Task NextDelayIsBoundedByTheNextBucketBoundary()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("next");
        wheel.Schedule(node, 100);
        await Assert.That(wheel.GetNextDelay()).IsEqualTo(64UL);
        await Assert.That(wheel.Advance(64, 16).DueNodes).IsEmpty();
        await Assert.That(wheel.GetNextDelay()).IsEqualTo(36UL);
        await Assert.That(wheel.Advance(100, 16).DueNodes).HasSingleItem();
        await Assert.That(wheel.GetNextDelay()).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task FixedSeedOracleFindsEveryDueNodeAfterRepeatedAdvances()
    {
        Random random = new(0x5EED);
        TimerWheel<int> wheel = new();
        Dictionary<int, IdentityTimerNode<int>> scheduled = [];
        for (int key = 0; key < 256; key++)
        {
            ulong deadline = (ulong)random.Next(1, 50_000);
            IdentityTimerNode<int> node = new(key);
            scheduled.Add(key, node);
            wheel.Schedule(node, deadline);
        }

        HashSet<int> expired = [];
        for (ulong now = 0; now <= 50_000; now += 257)
        {
            TimerAdvanceResult<int> result = wheel.Advance(now, 64);
            while (true)
            {
                expired.UnionWith(result.DueNodes.Select(node => node.Value));
                if (!result.HasPending)
                {
                    break;
                }

                result = wheel.Advance(now, 64);
            }
        }

        TimerAdvanceResult<int> final = wheel.Advance(50_000, 64);
        while (true)
        {
            expired.UnionWith(final.DueNodes.Select(node => node.Value));
            if (!final.HasPending)
            {
                break;
            }

            final = wheel.Advance(50_000, 64);
        }

        await Assert.That(expired).IsEquivalentTo(scheduled.Keys);
        await Assert.That(wheel.Count).IsEqualTo(0);
        wheel.AssertInvariants();
    }

    private static List<IdentityTimerNode<T>> Drain<T>(TimerWheel<T> wheel, ulong now, int budget)
        where T : notnull
    {
        List<IdentityTimerNode<T>> result = [];
        TimerAdvanceResult<T> batch = wheel.Advance(now, budget);
        while (true)
        {
            result.AddRange(batch.DueNodes);
            if (!batch.HasPending)
            {
                return result;
            }

            batch = wheel.Advance(now, budget);
        }
    }
}
