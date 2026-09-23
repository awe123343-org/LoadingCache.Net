using LoadingCache.Policy;

namespace LoadingCache.Tests;

public sealed class PolicyDequeRegressionTests
{
    [Test]
    [Arguments(0L)]
    [Arguments(1L)]
    public async Task WindowCountPressureUsesRecencyIncludingZeroWeight(long weight)
    {
        WindowTinyLfuPolicy<int> policy = new(1_000, seed: 7, adaptive: false);
        PolicyNode<int> first = Node(1, weight);
        PolicyNode<int> second = Node(2, weight);
        PolicyNode<int> third = Node(3, weight);
        await Assert.That(policy.Add(first)).IsEmpty();
        await Assert.That(policy.Add(second)).IsEmpty();
        await Assert.That(policy.Add(third)).IsEmpty();
        await Assert.That(policy.RecordAccess(first)).IsTrue();
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 4))
            .IsEquivalentTo(
                [second, third, first],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        AssertRepresentation(policy);
        await Assert
            .That(policy.SetMaximumCount(2))
            .IsEquivalentTo([second], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 4))
            .IsEquivalentTo([third, first], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(policy.WeightedSize).IsEqualTo(weight * 2);
        AssertRetired(second);
        AssertRepresentation(policy);
    }

    [Test]
    public async Task ProtectedReorderDemotionAndShrinkKeepExactVictimOrder()
    {
        WindowTinyLfuPolicy<int> policy = new(5, seed: 7, adaptive: false);
        PolicyNode<int>[] nodes = [Node(1), Node(2), Node(3), Node(4), Node(5)];
        foreach (PolicyNode<int> node in nodes)
        {
            await Assert.That(policy.Add(node)).IsEmpty();
        }

        await Assert.That(policy.RecordAccess(nodes[0])).IsTrue();
        await Assert.That(policy.RecordAccess(nodes[1])).IsTrue();
        await Assert.That(policy.RecordAccess(nodes[2])).IsTrue();
        await Assert.That(policy.RecordAccess(nodes[0])).IsTrue();
        await Assert.That(policy.RecordAccess(nodes[3])).IsTrue();
        await Assert.That(nodes[1].Queue).IsEqualTo(PolicyQueue.Probation);
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 5))
            .IsEquivalentTo(
                [nodes[1], nodes[4], nodes[2], nodes[0], nodes[3]],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        AssertRepresentation(policy);
        await Assert
            .That(policy.SetMaximumCount(3))
            .IsEquivalentTo(
                [nodes[1], nodes[2]],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert
            .That(policy.SetMaximum(2))
            .IsEquivalentTo([nodes[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 5))
            .IsEquivalentTo(
                [nodes[4], nodes[3]],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        AssertRepresentation(policy);
        foreach (PolicyNode<int> node in nodes)
        {
            policy.Remove(node);
            await Assert.That(policy.RecordAccess(node)).IsFalse();
            AssertRetired(node);
        }

        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        AssertRepresentation(policy);
    }

    [Test]
    public async Task WeightPressureSkipsProtectedZeroAfterWeightChangesAndReorder()
    {
        WindowTinyLfuPolicy<int> policy = new(5, seed: 7, adaptive: false);
        PolicyNode<int> zero = Node(1);
        PolicyNode<int> positive = Node(2);
        PolicyNode<int> window = Node(3);
        policy.Add(zero);
        policy.Add(positive);
        policy.Add(window);
        await Assert.That(policy.RecordAccess(zero)).IsTrue();
        await Assert.That(policy.RecordAccess(positive)).IsTrue();
        await Assert.That(policy.UpdateWeight(positive, 0)).IsEmpty();
        await Assert.That(policy.UpdateWeight(positive, 1)).IsEmpty();
        await Assert.That(policy.RecordAccess(zero)).IsTrue();
        await Assert.That(policy.UpdateWeight(zero, 0)).IsEmpty();
        await Assert.That(policy.RecordAccess(positive)).IsTrue();
        await Assert
            .That(policy.Remove(window))
            .IsEquivalentTo([window], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 3))
            .IsEquivalentTo([zero, positive], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        AssertRepresentation(policy);
        PolicyNode<int> incoming = Node(4, 5);
        for (int i = 0; i < 5; i++)
        {
            policy.RecordMiss(incoming.Hash);
        }

        await Assert
            .That(policy.Frequency(incoming.Hash))
            .IsGreaterThan(policy.Frequency(positive.Hash));
        await Assert
            .That(policy.Add(incoming))
            .IsEquivalentTo([positive], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(zero.IsAlive).IsTrue();
        await Assert.That(zero.Queue).IsEqualTo(PolicyQueue.Protected);
        await Assert.That(incoming.IsAlive).IsTrue();
        await Assert.That(policy.WeightedSize).IsEqualTo(5);
        AssertRetired(positive);
        AssertRepresentation(policy);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OverflowSelectionExcludesPriorVictimsAndSkipsZeroWeight(bool protectedQueue)
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
                await Assert.That(policy.Add(node)).IsEmpty();
            }
            else
            {
                await Assert.That(policy.AddDeferred(node)).IsEmpty();
            }
        }

        if (protectedQueue)
        {
            foreach (PolicyNode<int> node in residents)
            {
                await Assert.That(policy.RecordAccess(node)).IsTrue();
            }

            await Assert.That(policy.UpdateWeight(zero, 0)).IsEmpty();
            await Assert.That(policy.RecordAccess(positives[0])).IsTrue();
            await Assert.That(policy.RecordAccess(positives[1])).IsTrue();
            await Assert.That(policy.RecordAccess(positives[2])).IsTrue();
        }

        await Assert
            .That(residents)
            .All(node =>
                node.Queue == (protectedQueue ? PolicyQueue.Protected : PolicyQueue.Window)
            );
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 5))
            .IsEquivalentTo(residents, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        AssertRepresentation(policy);
        PolicyNode<int> incoming = Node(5, long.MaxValue / 2 + 3);
        for (int i = 0; i < 15; i++)
        {
            policy.RecordMiss(incoming.Hash);
        }

        foreach (PolicyNode<int> node in positives)
        {
            await Assert
                .That(policy.Frequency(incoming.Hash))
                .IsGreaterThan(policy.Frequency(node.Hash));
        }

        await Assert
            .That(policy.AddDeferred(incoming))
            .IsEquivalentTo(
                [positives[0], positives[1]],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        await Assert.That(zero.IsAlive).IsTrue();
        await Assert.That(positives[2].IsAlive).IsTrue();
        await Assert.That(incoming.IsAlive).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(3);
        await Assert.That(policy.WeightedSize).IsEqualTo(quarter + incoming.Weight);
        AssertRetired(positives[0]);
        AssertRetired(positives[1]);
        AssertRepresentation(policy);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClearedProbationCandidateKeepsEligibilityOrderIndependentOfPrimary(
        bool countBound
    )
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
        await Assert.That(policy.Maintain(1)).IsEmpty();
        await Assert.That(cleared.IsCandidate).IsTrue();
        await Assert.That(policy.RecordAccess(first)).IsTrue();
        await Assert
            .That(policy.Remove(removed))
            .IsEquivalentTo([removed], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(policy.Maintain(1)).IsEmpty();
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 3))
            .IsEquivalentTo([cleared, first], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(cleared.IsCandidate).IsFalse();
        await Assert.That(ReferenceEquals(first.EligibleNext, cleared)).IsTrue();
        await Assert.That(ReferenceEquals(first.EligiblePositiveNext, cleared)).IsTrue();
        AssertRepresentation(policy);
        await Assert.That(policy.AddDeferred(incoming)).IsEmpty();
        await Assert
            .That(policy.Maintain())
            .IsEquivalentTo([first], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 3))
            .IsEquivalentTo(
                [cleared, incoming],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        AssertRetired(first);
        AssertRepresentation(policy);
    }

    [Test]
    public async Task BudgetedTransfersAndCandidateClearingDoNotConsumeExtraWork()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 7, adaptive: false, maximumCount: 3);
        // The same hash keeps the admission decision a deterministic cold tie.
        PolicyNode<int>[] nodes = [new(1, 1, 7), new(2, 1, 7), new(3, 1, 7), new(4, 1, 7)];
        foreach (PolicyNode<int> node in nodes)
        {
            await Assert.That(policy.AddDeferred(node)).IsEmpty();
        }

        await Assert.That(policy.Maintain(0)).IsEmpty();
        await Assert.That(nodes).All(node => node.Queue == PolicyQueue.Window);
        for (int i = 0; i < 3; i++)
        {
            await Assert.That(policy.Maintain(1)).IsEmpty();
            await Assert.That(nodes[i].IsCandidate).IsTrue();
            await Assert.That(nodes[i + 1].Queue).IsEqualTo(PolicyQueue.Window);
            await Assert.That(policy.ResidentCount).IsEqualTo(4);
        }

        await Assert
            .That(policy.Maintain(1))
            .IsEquivalentTo([nodes[0]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(nodes[1].IsCandidate).IsTrue();
        await Assert.That(nodes[2].IsCandidate).IsTrue();
        await Assert.That(policy.Maintain(1)).IsEmpty();
        await Assert.That(nodes[1].IsCandidate).IsFalse();
        await Assert.That(nodes[2].IsCandidate).IsTrue();
        await Assert.That(policy.Maintain(1)).IsEmpty();
        await Assert.That(nodes[2].IsCandidate).IsFalse();
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 4))
            .IsEquivalentTo(
                [nodes[1], nodes[2], nodes[3]],
                TUnit.Assertions.Enums.CollectionOrdering.Matching
            );
        AssertRetired(nodes[0]);
        AssertRepresentation(policy);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClearRetiresEntriesAndRejectsOldGenerationReads(bool queueReadBeforeClear)
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
        await Assert
            .That(oldNodes.Select(node => node.Queue))
            .IsEquivalentTo([PolicyQueue.Protected, PolicyQueue.Probation, PolicyQueue.Window]);
        if (queueReadBeforeClear)
        {
            // Clear drains this read first, promoting the last Probation entry.
            // Without it, Clear retires entries from all three queues.
            policy.OnAccess(tokens[1]);
        }

        policy.Clear();
        await Assert.That(policy.Snapshot(hottest: false, limit: 5)).IsEmpty();
        await Assert.That(tokens).All(token => token.Node == null);
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
        await Assert
            .That(policy.Snapshot(hottest: false, limit: 5))
            .IsEquivalentTo([fresh.Entry], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
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
                if ((node.Queue) != (PolicyQueue.Probation))
                    Assert.Fail("Expected node.Queue to equal (PolicyQueue.Probation).");
            }

            if (
                node.PositivePrevious is { } adjacentPositivePrevious
                && !ReferenceEquals(adjacentPositivePrevious.PositiveNext, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (
                node.PositiveNext is { } adjacentPositiveNext
                && !ReferenceEquals(adjacentPositiveNext.PositivePrevious, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (
                node.EligiblePrevious is { } adjacentEligiblePrevious
                && !ReferenceEquals(adjacentEligiblePrevious.EligibleNext, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (
                node.EligibleNext is { } adjacentEligibleNext
                && !ReferenceEquals(adjacentEligibleNext.EligiblePrevious, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (
                node.EligiblePositivePrevious is { } adjacentEligiblePositivePrevious
                && !ReferenceEquals(adjacentEligiblePositivePrevious.EligiblePositiveNext, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (
                node.EligiblePositiveNext is { } adjacentEligiblePositiveNext
                && !ReferenceEquals(adjacentEligiblePositiveNext.EligiblePositivePrevious, node)
            )
                Assert.Fail("Broken reciprocal policy link.");
            if (node.Queue == PolicyQueue.Probation)
            {
                continue;
            }

            if (!(ReferenceEquals(node.EligiblePrevious, node.Previous)))
                Assert.Fail("Expected node.EligiblePrevious to reference (node.Previous).");
            if (!(ReferenceEquals(node.EligibleNext, node.Next)))
                Assert.Fail("Expected node.EligibleNext to reference (node.Next).");
            if (!(ReferenceEquals(node.EligiblePositivePrevious, node.PositivePrevious)))
                Assert.Fail(
                    "Expected node.EligiblePositivePrevious to reference (node.PositivePrevious)."
                );
            if (!(ReferenceEquals(node.EligiblePositiveNext, node.PositiveNext)))
                Assert.Fail("Expected node.EligiblePositiveNext to reference (node.PositiveNext).");
        }
    }

    private static void AssertRetired<T>(PolicyNode<T> node)
        where T : notnull
    {
        if (node.IsAlive)
            Assert.Fail("Expected node.IsAlive to be false ().");
        if (node.IsCandidate)
            Assert.Fail("Expected node.IsCandidate to be false ().");
        if ((node.Queue) != (PolicyQueue.None))
            Assert.Fail("Expected node.Queue to equal (PolicyQueue.None).");
        if (!((node.Previous) is null))
            Assert.Fail("Expected node.Previous to be null ().");
        if (!((node.Next) is null))
            Assert.Fail("Expected node.Next to be null ().");
        if (!((node.PositivePrevious) is null))
            Assert.Fail("Expected node.PositivePrevious to be null ().");
        if (!((node.PositiveNext) is null))
            Assert.Fail("Expected node.PositiveNext to be null ().");
        if (!((node.EligiblePrevious) is null))
            Assert.Fail("Expected node.EligiblePrevious to be null ().");
        if (!((node.EligibleNext) is null))
            Assert.Fail("Expected node.EligibleNext to be null ().");
        if (!((node.EligiblePositivePrevious) is null))
            Assert.Fail("Expected node.EligiblePositivePrevious to be null ().");
        if (!((node.EligiblePositiveNext) is null))
            Assert.Fail("Expected node.EligiblePositiveNext to be null ().");
        if (!((node.CandidatePrevious) is null))
            Assert.Fail("Expected node.CandidatePrevious to be null ().");
        if (!((node.CandidateNext) is null))
            Assert.Fail("Expected node.CandidateNext to be null ().");
    }
}
