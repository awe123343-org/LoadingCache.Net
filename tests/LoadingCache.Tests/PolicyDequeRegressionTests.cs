using FluentAssertions;
using LoadingCache.Policy;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class PolicyDequeRegressionTests
{
    [TestCase(0L)]
    [TestCase(1L)]
    [Parallelizable(ParallelScope.Self)]
    public void WindowCountPressureUsesRecencyIncludingZeroWeight(long weight)
    {
        WindowTinyLfuPolicy<int> policy = new(1_000, seed: 7, adaptive: false);
        PolicyNode<int> first = Node(1, weight);
        PolicyNode<int> second = Node(2, weight);
        PolicyNode<int> third = Node(3, weight);
        policy.Add(first).Should().BeEmpty();
        policy.Add(second).Should().BeEmpty();
        policy.Add(third).Should().BeEmpty();

        policy.RecordAccess(first).Should().BeTrue();
        policy.Snapshot(hottest: false, limit: 4).Should().Equal(second, third, first);
        AssertRepresentation(policy);

        policy.SetMaximumCount(2).Should().Equal(second);
        policy.Snapshot(hottest: false, limit: 4).Should().Equal(third, first);
        policy.WeightedSize.Should().Be(weight * 2);
        AssertRetired(second);
        AssertRepresentation(policy);
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void ProtectedReorderDemotionAndShrinkKeepExactVictimOrder()
    {
        WindowTinyLfuPolicy<int> policy = new(5, seed: 7, adaptive: false);
        PolicyNode<int>[] nodes = [Node(1), Node(2), Node(3), Node(4), Node(5)];
        foreach (PolicyNode<int> node in nodes)
        {
            policy.Add(node).Should().BeEmpty();
        }

        policy.RecordAccess(nodes[0]).Should().BeTrue();
        policy.RecordAccess(nodes[1]).Should().BeTrue();
        policy.RecordAccess(nodes[2]).Should().BeTrue();
        policy.RecordAccess(nodes[0]).Should().BeTrue();
        policy.RecordAccess(nodes[3]).Should().BeTrue();

        nodes[1].Queue.Should().Be(PolicyQueue.Probation);
        policy
            .Snapshot(hottest: false, limit: 5)
            .Should()
            .Equal(nodes[1], nodes[4], nodes[2], nodes[0], nodes[3]);
        AssertRepresentation(policy);

        policy.SetMaximumCount(3).Should().Equal(nodes[1], nodes[2]);
        policy.SetMaximum(2).Should().Equal(nodes[0]);
        policy.Snapshot(hottest: false, limit: 5).Should().Equal(nodes[4], nodes[3]);
        AssertRepresentation(policy);

        foreach (PolicyNode<int> node in nodes)
        {
            policy.Remove(node);
            policy.RecordAccess(node).Should().BeFalse();
            AssertRetired(node);
        }

        policy.ResidentCount.Should().Be(0);
        policy.WeightedSize.Should().Be(0);
        AssertRepresentation(policy);
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void WeightPressureSkipsProtectedZeroAfterWeightChangesAndReorder()
    {
        WindowTinyLfuPolicy<int> policy = new(5, seed: 7, adaptive: false);
        PolicyNode<int> zero = Node(1);
        PolicyNode<int> positive = Node(2);
        PolicyNode<int> window = Node(3);
        policy.Add(zero);
        policy.Add(positive);
        policy.Add(window);
        policy.RecordAccess(zero).Should().BeTrue();
        policy.RecordAccess(positive).Should().BeTrue();
        policy.UpdateWeight(positive, 0).Should().BeEmpty();
        policy.UpdateWeight(positive, 1).Should().BeEmpty();
        policy.RecordAccess(zero).Should().BeTrue();
        policy.UpdateWeight(zero, 0).Should().BeEmpty();
        policy.RecordAccess(positive).Should().BeTrue();
        policy.Remove(window).Should().Equal(window);
        policy.Snapshot(hottest: false, limit: 3).Should().Equal(zero, positive);
        AssertRepresentation(policy);

        PolicyNode<int> incoming = Node(4, 5);
        for (int i = 0; i < 5; i++)
        {
            policy.RecordMiss(incoming.Hash);
        }

        policy.Frequency(incoming.Hash).Should().BeGreaterThan(policy.Frequency(positive.Hash));
        policy.Add(incoming).Should().Equal(positive);
        zero.IsAlive.Should().BeTrue();
        zero.Queue.Should().Be(PolicyQueue.Protected);
        incoming.IsAlive.Should().BeTrue();
        policy.WeightedSize.Should().Be(5);
        AssertRetired(positive);
        AssertRepresentation(policy);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.Self)]
    public void OverflowSelectionExcludesPriorVictimsAndSkipsZeroWeight(bool protectedQueue)
    {
        const long quarter = long.MaxValue / 4;
        WindowTinyLfuPolicy<int> policy = new(
            long.MaxValue,
            seed: 7,
            adaptive: false,
            maximumCount: 16
        );
        PolicyNode<int> zero = Node(1, protectedQueue ? 1 : 0);
        PolicyNode<int>[] positives = [Node(2, quarter), Node(3, quarter), Node(4, quarter)];
        PolicyNode<int>[] residents = [zero, .. positives];
        foreach (PolicyNode<int> node in residents)
        {
            if (protectedQueue)
            {
                policy.Add(node).Should().BeEmpty();
            }
            else
            {
                policy.AddDeferred(node).Should().BeEmpty();
            }
        }

        if (protectedQueue)
        {
            foreach (PolicyNode<int> node in residents)
            {
                policy.RecordAccess(node).Should().BeTrue();
            }

            policy.UpdateWeight(zero, 0).Should().BeEmpty();
            policy.RecordAccess(positives[0]).Should().BeTrue();
            policy.RecordAccess(positives[1]).Should().BeTrue();
            policy.RecordAccess(positives[2]).Should().BeTrue();
        }

        residents
            .Should()
            .OnlyContain(node =>
                node.Queue == (protectedQueue ? PolicyQueue.Protected : PolicyQueue.Window)
            );
        policy.Snapshot(hottest: false, limit: 5).Should().Equal(residents);
        AssertRepresentation(policy);

        PolicyNode<int> incoming = Node(5, long.MaxValue / 2 + 3);
        for (int i = 0; i < 15; i++)
        {
            policy.RecordMiss(incoming.Hash);
        }

        foreach (PolicyNode<int> node in positives)
        {
            policy.Frequency(incoming.Hash).Should().BeGreaterThan(policy.Frequency(node.Hash));
        }

        policy.AddDeferred(incoming).Should().Equal(positives[0], positives[1]);
        zero.IsAlive.Should().BeTrue();
        positives[2].IsAlive.Should().BeTrue();
        incoming.IsAlive.Should().BeTrue();
        policy.ResidentCount.Should().Be(3);
        policy.WeightedSize.Should().Be(quarter + incoming.Weight);
        AssertRetired(positives[0]);
        AssertRetired(positives[1]);
        AssertRepresentation(policy);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.Self)]
    public void ClearedProbationCandidateKeepsEligibilityOrderIndependentOfPrimary(bool countBound)
    {
        WindowTinyLfuPolicy<int> policy = new(
            2,
            seed: 7,
            adaptive: false,
            maximumCount: countBound ? 2 : null
        );
        PolicyNode<int> first = Node(1);
        PolicyNode<int> cleared = Node(2);
        PolicyNode<int> removed = Node(3);
        PolicyNode<int> incoming = Node(4);
        policy.Add(first);
        policy.Add(cleared);
        policy.AddDeferred(removed);
        policy.Maintain(1).Should().BeEmpty();
        cleared.IsCandidate.Should().BeTrue();
        policy.RecordAccess(first).Should().BeTrue();
        policy.Remove(removed).Should().Equal(removed);
        policy.Maintain(1).Should().BeEmpty();

        policy.Snapshot(hottest: false, limit: 3).Should().Equal(cleared, first);
        cleared.IsCandidate.Should().BeFalse();
        first.EligibleNext.Should().BeSameAs(cleared);
        first.EligiblePositiveNext.Should().BeSameAs(cleared);
        AssertRepresentation(policy);

        policy.AddDeferred(incoming).Should().BeEmpty();
        policy.Maintain().Should().Equal(first);
        policy.Snapshot(hottest: false, limit: 3).Should().Equal(cleared, incoming);
        AssertRetired(first);
        AssertRepresentation(policy);
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void BudgetedTransfersAndCandidateClearingDoNotConsumeExtraWork()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 7, adaptive: false, maximumCount: 3);
        // The same hash keeps the admission decision a deterministic cold tie.
        PolicyNode<int>[] nodes = [new(1, 1, 7), new(2, 1, 7), new(3, 1, 7), new(4, 1, 7)];
        foreach (PolicyNode<int> node in nodes)
        {
            policy.AddDeferred(node).Should().BeEmpty();
        }

        policy.Maintain(0).Should().BeEmpty();
        nodes.Should().OnlyContain(node => node.Queue == PolicyQueue.Window);
        for (int i = 0; i < 3; i++)
        {
            policy.Maintain(1).Should().BeEmpty();
            nodes[i].IsCandidate.Should().BeTrue();
            nodes[i + 1].Queue.Should().Be(PolicyQueue.Window);
            policy.ResidentCount.Should().Be(4);
        }

        policy.Maintain(1).Should().Equal(nodes[0]);
        nodes[1].IsCandidate.Should().BeTrue();
        nodes[2].IsCandidate.Should().BeTrue();
        policy.Maintain(1).Should().BeEmpty();
        nodes[1].IsCandidate.Should().BeFalse();
        nodes[2].IsCandidate.Should().BeTrue();
        policy.Maintain(1).Should().BeEmpty();
        nodes[2].IsCandidate.Should().BeFalse();
        policy.Snapshot(hottest: false, limit: 4).Should().Equal(nodes[1], nodes[2], nodes[3]);
        AssertRetired(nodes[0]);
        AssertRepresentation(policy);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.Self)]
    public void ClearRetiresEntriesAndRejectsOldGenerationReads(bool queueReadBeforeClear)
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 5,
            maximumResidentCount: 5,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 8
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken[] tokens =
        [
            new(new object(), 1),
            new(new object(), 2),
            new(new object(), 3),
        ];
        foreach (WindowTinyLfuEnginePolicy.EngineEntryToken token in tokens)
        {
            policy.OnPublish(token, 1);
        }

        policy.CleanUp();
        policy.OnAccess(tokens[0]);
        policy.CleanUp();
        PolicyNode<object>[] oldNodes = [.. tokens.Select(token => token.Node!)];
        oldNodes
            .Select(node => node.Queue)
            .Should()
            .BeEquivalentTo([PolicyQueue.Protected, PolicyQueue.Probation, PolicyQueue.Window]);
        if (queueReadBeforeClear)
        {
            // Clear drains this read first, promoting the last Probation entry.
            // Without it, Clear retires entries from all three queues.
            policy.OnAccess(tokens[1]);
        }

        policy.Clear();
        policy.Snapshot(hottest: false, limit: 5).Should().BeEmpty();
        tokens.Should().OnlyContain(token => token.Node == null);
        foreach (PolicyNode<object> node in oldNodes)
        {
            AssertRetired(node);
        }

        WindowTinyLfuEnginePolicy.EngineEntryToken fresh = new(new object(), 4);
        policy.OnPublish(fresh, 0);
        foreach (WindowTinyLfuEnginePolicy.EngineEntryToken token in tokens)
        {
            policy.OnAccess(token);
        }

        policy.CleanUp();
        policy.Snapshot(hottest: false, limit: 5).Should().Equal(fresh.Entry);
        policy.GetReadBufferStatistics().Queued.Should().Be(0);
    }

    private static PolicyNode<int> Node(int value, long weight = 1) =>
        new(value, weight, unchecked((uint)value * 0x9E3779B9u));

    private static void AssertRepresentation(WindowTinyLfuPolicy<int> policy)
    {
        policy.AssertInvariants();
        foreach (PolicyNode<int> node in policy.Snapshot(hottest: false, limit: int.MaxValue))
        {
            if (node.IsCandidate)
            {
                node.Queue.Should().Be(PolicyQueue.Probation);
            }

            node.PositivePrevious?.PositiveNext.Should().BeSameAs(node);
            node.PositiveNext?.PositivePrevious.Should().BeSameAs(node);
            node.EligiblePrevious?.EligibleNext.Should().BeSameAs(node);
            node.EligibleNext?.EligiblePrevious.Should().BeSameAs(node);
            node.EligiblePositivePrevious?.EligiblePositiveNext.Should().BeSameAs(node);
            node.EligiblePositiveNext?.EligiblePositivePrevious.Should().BeSameAs(node);

            if (node.Queue == PolicyQueue.Probation)
            {
                continue;
            }

            node.EligiblePrevious.Should().BeSameAs(node.Previous);
            node.EligibleNext.Should().BeSameAs(node.Next);
            node.EligiblePositivePrevious.Should().BeSameAs(node.PositivePrevious);
            node.EligiblePositiveNext.Should().BeSameAs(node.PositiveNext);
        }
    }

    private static void AssertRetired<T>(PolicyNode<T> node)
        where T : notnull
    {
        node.IsAlive.Should().BeFalse();
        node.IsCandidate.Should().BeFalse();
        node.Queue.Should().Be(PolicyQueue.None);
        node.Previous.Should().BeNull();
        node.Next.Should().BeNull();
        node.PositivePrevious.Should().BeNull();
        node.PositiveNext.Should().BeNull();
        node.EligiblePrevious.Should().BeNull();
        node.EligibleNext.Should().BeNull();
        node.EligiblePositivePrevious.Should().BeNull();
        node.EligiblePositiveNext.Should().BeNull();
        node.CandidatePrevious.Should().BeNull();
        node.CandidateNext.Should().BeNull();
    }
}
