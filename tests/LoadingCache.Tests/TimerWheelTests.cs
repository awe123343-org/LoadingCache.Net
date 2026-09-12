using System.Globalization;
using FluentAssertions;
using LoadingCache.Expiration;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class TimerWheelTests
{
    [Test]
    public void ExpiresNodesAtEachHierarchyBoundary()
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

        due.Select(node => node.Value).Should().BeEquivalentTo(nodes.Select(node => node.Value));
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void SameBucketFutureNodeIsNotReturnedEarly()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("future");
        wheel.Schedule(node, 60);

        wheel.Advance(10, 32).DueNodes.Should().BeEmpty();
        node.IsScheduled.Should().BeTrue();
        wheel.Advance(59, 32).DueNodes.Should().BeEmpty();
        wheel.Advance(60, 32).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(node);

        wheel.AssertInvariants();
    }

    [Test]
    public void RescheduleAndDeschedulePreserveIdentityLinks()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> rescheduled = new("rescheduled");
        IdentityTimerNode<string> removed = new("removed");

        wheel.Schedule(rescheduled, 100);
        wheel.Reschedule(rescheduled, 5).Should().BeTrue();
        wheel.Advance(5, 8).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(rescheduled);

        wheel.Schedule(removed, 200);
        wheel.Deschedule(removed).Should().BeTrue();
        wheel.Deschedule(removed).Should().BeFalse();
        wheel.Advance(200, 8).DueNodes.Should().BeEmpty();

        wheel.Retire(rescheduled).Should().BeTrue();
        wheel.Retire(rescheduled).Should().BeFalse();
        wheel.AssertInvariants();
    }

    [Test]
    public void RetiringAQueuedNodeDoesNotStrandItsBucket()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> retired = new("retired");
        IdentityTimerNode<string> survivor = new("survivor");
        wheel.Schedule(retired, 64);
        wheel.Schedule(survivor, 65);

        wheel.Retire(retired).Should().BeTrue();
        wheel.Advance(65, 16).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(survivor);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void BudgetReturnsAContinuationWithoutDroppingNodes()
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

        due.Should().BeEquivalentTo(nodes);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void BudgetContinuationDoesNotReprocessRescheduledNodes()
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

        due.Should().BeEquivalentTo(nodes);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void DescheduledContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);

        wheel.Advance(524_288, 1).HasPending.Should().BeTrue();
        wheel.Deschedule(tail).Should().BeTrue();
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

        settled.Should().BeTrue();
        wheel.Count.Should().Be(10_001);
        wheel.GetNextDelay().Should().BeGreaterThan(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void RetiredContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);

        wheel.Advance(524_288, 1).HasPending.Should().BeTrue();
        wheel.Retire(tail).Should().BeTrue();

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

        settled.Should().BeTrue();
        wheel.Count.Should().Be(1);
        wheel.GetNextDelay().Should().BeGreaterThan(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void RescheduledContinuationTailDoesNotBusyLoop()
    {
        TimerWheel<int> wheel = new();
        IdentityTimerNode<int> survivor = new(1);
        IdentityTimerNode<int> tail = new(2);
        wheel.Schedule(survivor, 100_000_000);
        wheel.Schedule(tail, 100_000_000);

        wheel.Advance(524_288, 1).HasPending.Should().BeTrue();
        wheel.Reschedule(tail, 600_000).Should().BeTrue();

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

        settled.Should().BeTrue();
        wheel.Count.Should().Be(2);
        wheel.Advance(600_000, 16).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(tail);
        wheel.AssertInvariants();
    }

    [Test]
    public void CrossWheelDescheduleCannotMutateTheOwningWheel()
    {
        TimerWheel<string> owner = new();
        TimerWheel<string> other = new();
        IdentityTimerNode<string> node = new("owned");
        owner.Schedule(node, 100);

        other.Deschedule(node).Should().BeFalse();
        node.IsScheduled.Should().BeTrue();
        other.Retire(node).Should().BeFalse();
        owner.Advance(100, 16).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(node);
        owner.AssertInvariants();
        other.AssertInvariants();
    }

    [Test]
    public void WrapAroundUsesUnsignedMonotonicDistance()
    {
        const ulong start = ulong.MaxValue - 2;
        TimerWheel<string> wheel = new(start);
        IdentityTimerNode<string> beforeWrap = new("before-wrap");
        IdentityTimerNode<string> afterWrap = new("after-wrap");
        wheel.Schedule(beforeWrap, unchecked(start + 1));
        wheel.Schedule(afterWrap, unchecked(start + 4));

        TimerAdvanceResult<string> result = wheel.Advance(1, 16);

        result.DueNodes.Should().BeEquivalentTo([beforeWrap, afterWrap]);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void LargeForwardJumpVisitsOuterWheelAndExpiresDueNodes()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("large-jump");
        wheel.Schedule(node, 1000);

        TimerAdvanceResult<string> result = wheel.Advance(1_000_000, 128);

        result.DueNodes.Should().ContainSingle().Which.Should().BeSameAs(node);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void PastDeadlineIsPlacedInTheCurrentBucketForImmediateCleanup()
    {
        TimerWheel<string> wheel = new(100);
        IdentityTimerNode<string> node = new("past");
        wheel.Schedule(node, 50);

        wheel.GetNextDelay().Should().Be(0);
        wheel.Advance(100, 16).DueNodes.Should().ContainSingle().Which.Should().BeSameAs(node);
        wheel.AssertInvariants();
    }

    [Test]
    public void DeadlineBeyondTheSignedHalfRangeIsRejected()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("too-far");

        Action action = () => wheel.Schedule(node, (ulong)long.MaxValue + 1);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void MaximumSupportedForwardDurationUsesTheOuterWheel()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("maximum");
        wheel.Schedule(node, long.MaxValue);

        wheel
            .Advance(long.MaxValue, int.MaxValue)
            .DueNodes.Should()
            .ContainSingle()
            .Which.Should()
            .BeSameAs(node);
        wheel.Count.Should().Be(0);
        wheel.AssertInvariants();
    }

    [Test]
    public void BackwardClockMoveDoesNotExpireFutureNodes()
    {
        TimerWheel<string> wheel = new(100);
        IdentityTimerNode<string> node = new("future");
        wheel.Schedule(node, 200);

        wheel.Advance(90, 16).DueNodes.Should().BeEmpty();
        node.IsScheduled.Should().BeTrue();
        wheel.CurrentTime.Should().Be(100);
        wheel.AssertInvariants();
    }

    [Test]
    public void NextDelayIsBoundedByTheNextBucketBoundary()
    {
        TimerWheel<string> wheel = new();
        IdentityTimerNode<string> node = new("next");
        wheel.Schedule(node, 100);

        wheel.GetNextDelay().Should().Be(64);
        wheel.Advance(64, 16).DueNodes.Should().BeEmpty();
        wheel.GetNextDelay().Should().Be(36);
        wheel.Advance(100, 16).DueNodes.Should().ContainSingle();
        wheel.GetNextDelay().Should().Be(ulong.MaxValue);
    }

    [Test]
    public void FixedSeedOracleFindsEveryDueNodeAfterRepeatedAdvances()
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

        expired.Should().BeEquivalentTo(scheduled.Keys);
        wheel.Count.Should().Be(0);
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
