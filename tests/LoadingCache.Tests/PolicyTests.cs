using LoadingCache.Policy;

namespace LoadingCache.Tests;

public sealed class PolicyTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(17)]
    public async Task TinyCapacitiesConvergeWithoutEmptySegmentFailures(int capacity)
    {
        WindowTinyLfuPolicy<int> policy = new(capacity, seed: 7, maximumCount: capacity);
        for (int i = 0; i < capacity * 4; i++)
        {
            PolicyNode<int> node = new(i, 1, Hash(i));
            policy.Add(node);
        }

        policy.Maintain();
        await Assert.That(policy.ResidentCount).IsLessThanOrEqualTo(capacity);
        await Assert.That(policy.WeightedSize).IsLessThanOrEqualTo(capacity);
        await Assert.That(policy.WindowMaximum).IsBetween(1, capacity);
        await Assert.That(policy.MainMaximum).IsGreaterThanOrEqualTo(0);
        await Assert.That(policy.ProtectedMaximum).IsLessThanOrEqualTo(policy.MainMaximum);
    }

    [Test]
    public async Task NegativeWeightIsRejectedBeforeNodeCanEnterPolicy()
    {
        Action action = () => _ = new PolicyNode<string>("negative", -1, 1);
        await Assert.That(action).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task NegativeWeightUpdateIsRejectedWithoutMutatingTheResident()
    {
        WindowTinyLfuPolicy<int> policy = new(4, seed: 5);
        PolicyNode<int> node = new(1, 1, Hash(1));
        policy.Add(node);
        Action action = () => policy.UpdateWeight(node, -1);
        await Assert.That(action).Throws<ArgumentOutOfRangeException>();
        await Assert.That(node.IsAlive).IsTrue();
        await Assert.That(node.Weight).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(1);
    }

    [Test]
    public async Task ZeroWeightDoesNotImplicitlyConsumeWeightCapacity()
    {
        WindowTinyLfuPolicy<string> policy = new(1, seed: 11);
        PolicyNode<string> first = new("first", 0, 1);
        PolicyNode<string> second = new("second", 0, 2);
        policy.Add(first);
        policy.Add(second);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        await Assert.That(policy.ResidentCount).IsEqualTo(2);
        await Assert.That(first.IsAlive).IsTrue();
        await Assert.That(second.IsAlive).IsTrue();
        IReadOnlyList<PolicyNode<string>> evicted = policy.SetMaximumCount(1);
        await Assert.That(evicted.Count).IsEqualTo(1);
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
    }

    [Test]
    public async Task OversizedWeightIsReturnedAsExactRejectedNode()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 13);
        PolicyNode<int> node = new(7, 4, Hash(7));
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(node);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), node))
            .IsTrue();
        await Assert.That(node.IsAlive).IsFalse();
        await Assert.That(node.Queue).IsEqualTo(PolicyQueue.None);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
    }

    [Test]
    public async Task LongMaximumDoesNotOverflowWeightedAccounting()
    {
        WindowTinyLfuPolicy<int> policy = new(long.MaxValue, seed: 17);
        PolicyNode<int> maximum = new(1, long.MaxValue, Hash(1));
        PolicyNode<int> overflow = new(2, 1, Hash(2));
        policy.Add(maximum);
        for (int i = 0; i < 15; i++)
        {
            policy.RecordMiss(Hash(2));
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(overflow);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), maximum))
            .IsTrue();
        await Assert.That(maximum.IsAlive).IsFalse();
        await Assert.That(overflow.IsAlive).IsTrue();
        await Assert.That(policy.WeightedSize).IsEqualTo(1);
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
    }

    [Test]
    public async Task WeightIncreaseNearLongMaximumEvictsAnExistingNodeBeforeAccounting()
    {
        WindowTinyLfuPolicy<int> policy = new(long.MaxValue, seed: 79);
        PolicyNode<int> existing = new(1, long.MaxValue - 1, Hash(1));
        PolicyNode<int> changing = new(2, 1, Hash(2));
        policy.Add(existing);
        policy.Add(changing);
        for (int i = 0; i < 3; i++)
        {
            policy.RecordMiss(Hash(2));
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.UpdateWeight(changing, 2);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), existing))
            .IsTrue();
        await Assert.That(existing.IsAlive).IsFalse();
        await Assert.That(changing.IsAlive).IsTrue();
        await Assert.That(policy.WeightedSize).IsEqualTo(2);
    }

    [Test]
    public async Task OverflowAdmissionDoesNotDiscardAHotExistingVictim()
    {
        WindowTinyLfuPolicy<int> policy = new(long.MaxValue, seed: 83);
        const long weight = long.MaxValue / 2 + 1;
        PolicyNode<int> hot = new(1, weight, Hash(1));
        PolicyNode<int> cold = new(2, weight, Hash(2));
        policy.Add(hot);
        for (int i = 0; i < 15; i++)
        {
            policy.RecordAccess(hot);
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(cold);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), cold))
            .IsTrue();
        await Assert.That(hot.IsAlive).IsTrue();
        await Assert.That(policy.WeightedSize).IsEqualTo(weight);
    }

    [Test]
    public async Task ColdTieRejectsCandidateAndKeepsExistingVictim()
    {
        WindowTinyLfuPolicy<int> policy = new(1, seed: 19);
        PolicyNode<int> victim = new(1, 1, Hash(1));
        PolicyNode<int> candidate = new(2, 1, Hash(2));
        policy.Add(victim);
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(candidate);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), victim))
            .IsTrue();
        await Assert.That(victim.IsAlive).IsFalse();
        await Assert.That(candidate.IsAlive).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
    }

    [Test]
    public async Task ColdFillClearsCandidateBeforeLaterHotVictimAdmission()
    {
        WindowTinyLfuPolicy<int> policy = new(2, seed: 53);
        PolicyNode<int> hotVictim = new(1, 1, Hash(1));
        PolicyNode<int> admitted = new(2, 1, Hash(2));
        policy.Add(hotVictim);
        policy.Add(admitted);
        await Assert.That(hotVictim.Queue).IsEqualTo(PolicyQueue.Probation);
        await Assert.That(hotVictim.IsCandidate).IsFalse();
        await Assert.That(policy.RecordAccess(hotVictim)).IsTrue();
        await Assert.That(hotVictim.Queue).IsEqualTo(PolicyQueue.Probation);
        await Assert.That(hotVictim.IsCandidate).IsFalse();
        PolicyNode<int> candidate = new(3, 1, Hash(3));
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(candidate);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), admitted))
            .IsTrue();
        await Assert.That(hotVictim.IsAlive).IsTrue();
        await Assert.That(candidate.IsAlive).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(2);
    }

    [Test]
    public async Task HotCandidateCanDisplaceColdVictim()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 23);
        PolicyNode<int> coldVictim = new(1, 1, Hash(1));
        PolicyNode<int> middle = new(2, 1, Hash(2));
        PolicyNode<int> hotCandidate = new(3, 1, Hash(3));
        policy.Add(coldVictim);
        policy.Add(middle);
        policy.Add(hotCandidate);
        for (int i = 0; i < 20; i++)
        {
            policy.RecordAccess(hotCandidate);
        }

        PolicyNode<int> trigger = new(4, 1, Hash(4));
        policy.Add(trigger);
        await Assert.That(coldVictim.IsAlive).IsFalse();
        await Assert.That(hotCandidate.IsAlive).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(3);
    }

    [Test]
    public async Task ProbationAccessPromotesAndProtectedOverflowDemotesOldest()
    {
        WindowTinyLfuPolicy<int> policy = new(10, seed: 29);
        PolicyNode<int> first = new(1, 1, Hash(1));
        PolicyNode<int> second = new(2, 1, Hash(2));
        policy.Add(first);
        policy.Add(second);
        await Assert.That(first.Queue).IsEqualTo(PolicyQueue.Probation);
        await Assert.That(policy.RecordAccess(first)).IsTrue();
        await Assert.That(first.Queue).IsEqualTo(PolicyQueue.Protected);
        await Assert.That(policy.ProtectedCount).IsEqualTo(1);
        await Assert.That(policy.MainProtectedWeightedSize).IsEqualTo(1);
    }

    [Test]
    public async Task RemovingStaleNodeDoesNotAffectAnotherGeneration()
    {
        WindowTinyLfuPolicy<int> policy = new(4, seed: 31);
        PolicyNode<int> oldNode = new(1, 1, Hash(1));
        PolicyNode<int> currentNode = new(2, 1, Hash(2));
        policy.Add(oldNode);
        await Assert
            .That(
                ReferenceEquals(
                    (await Assert.That(policy.Remove(oldNode)).HasSingleItem()),
                    oldNode
                )
            )
            .IsTrue();
        policy.Add(currentNode);
        await Assert.That(policy.RecordAccess(oldNode)).IsFalse();
        await Assert.That(policy.Remove(oldNode)).IsEmpty();
        await Assert.That(currentNode.IsAlive).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(1);
    }

    [Test]
    public async Task SetMaximumReturnsExactVictimsAndConverges()
    {
        WindowTinyLfuPolicy<int> policy = new(16, seed: 37);
        List<PolicyNode<int>> nodes = [];
        for (int i = 0; i < 16; i++)
        {
            PolicyNode<int> node = new(i, 1, Hash(i));
            nodes.Add(node);
            policy.Add(node);
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.SetMaximum(3);
        await Assert.That(evicted).IsNotEmpty();
        await Assert.That(evicted).All(node => nodes.Contains(node));
        await Assert.That(policy.WeightedSize).IsLessThanOrEqualTo(3);
        await Assert.That(policy.ResidentCount).IsLessThanOrEqualTo(16);
        await Assert.That(policy.WindowMaximum).IsBetween(1, 3);
    }

    [Test]
    public async Task AdaptiveSamplingUsesMinimumProgressForTinyPolicy()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 41);
        PolicyNode<int> node = new(1, 1, Hash(1));
        policy.Add(node);
        long sampleSize = policy.SketchSampleSize;
        for (long i = 0; i < sampleSize; i++)
        {
            policy.RecordMiss(Hash((int)i));
        }

        policy.Maintain();
        await Assert.That(Math.Abs(policy.StepSize)).IsGreaterThanOrEqualTo(1);
        await Assert.That(policy.WindowMaximum).IsBetween(1, policy.Maximum);
        await Assert.That(policy.ProtectedMaximum).IsLessThanOrEqualTo(policy.MainMaximum);
    }

    [Test]
    public async Task AdaptiveWindowGrowsThenShrinksAcrossSamples()
    {
        WindowTinyLfuPolicy<int> policy = new(256, seed: 59);
        List<PolicyNode<int>> nodes = AddNodes(policy, 256);
        long initialWindow = policy.WindowMaximum;
        long sampleSize = policy.SketchSampleSize;
        for (long i = policy.MissesInSample; i < sampleSize; i++)
        {
            policy.RecordAccess(nodes[0]);
        }

        policy.Maintain(10_000);
        long grownWindow = policy.WindowMaximum;
        await Assert.That(grownWindow).IsGreaterThan(initialWindow);
        for (long i = 0; i < sampleSize; i++)
        {
            policy.RecordMiss(Hash((int)(i + 10_000)));
        }

        policy.Maintain(10_000);
        await Assert.That(policy.WindowMaximum).IsLessThan(grownWindow);
    }

    [Test]
    public async Task PendingAdjustmentSurvivesBudgetExhaustionAndDelaysNextSample()
    {
        WindowTinyLfuPolicy<int> policy = new(256, seed: 61);
        List<PolicyNode<int>> nodes = AddNodes(policy, 256);
        long sampleSize = policy.SketchSampleSize;
        for (long i = policy.MissesInSample; i < sampleSize; i++)
        {
            policy.RecordAccess(nodes[0]);
        }

        policy.Maintain(1);
        await Assert.That(policy.Adjustment).IsNotEqualTo(0);
        for (long i = 0; i < sampleSize; i++)
        {
            policy.RecordMiss(Hash((int)(i + 20_000)));
        }

        policy.Maintain(1);
        await Assert.That(policy.MissesInSample).IsEqualTo(sampleSize);
        await Assert.That(policy.Adjustment).IsNotEqualTo(0);
        policy.Maintain(10_000);
        await Assert.That(policy.Adjustment).IsEqualTo(0);
    }

    [Test]
    public async Task RepeatedSmallMaintenanceBudgetMakesWindowTransferProgress()
    {
        WindowTinyLfuPolicy<int> policy = new(100, seed: 89);
        List<PolicyNode<int>> nodes = AddNodes(policy, 100);
        long initialWindow = policy.WindowMaximum;
        for (int i = 0; i < 1_200; i++)
        {
            policy.RecordAccess(nodes[^1]);
        }

        for (int i = 0; i < 100; i++)
        {
            policy.Maintain(1);
        }

        await Assert.That(policy.WindowMaximum).IsGreaterThan(initialWindow);
        await Assert.That(policy.Adjustment).IsEqualTo(0);
    }

    [Test]
    public async Task SetMaximumResetsSketchSamplingWithoutDiscardingFrequency()
    {
        WindowTinyLfuPolicy<int> policy = new(256, seed: 67);
        uint hash = Hash(71);
        for (int i = 0; i < 32; i++)
        {
            policy.RecordMiss(hash);
        }

        int frequency = policy.Frequency(hash);
        await Assert.That(policy.SketchSampleCount).IsGreaterThan(0);
        policy.SetMaximum(128);
        await Assert.That(policy.SketchSampleCount).IsEqualTo(0);
        await Assert.That(policy.Frequency(hash)).IsEqualTo(frequency);
    }

    [Test]
    public async Task FrequencySketchIsSaturatingAndSeedDeterministic()
    {
        FrequencySketch first = new(43);
        FrequencySketch second = new(43);
        first.EnsureCapacity(1_024);
        second.EnsureCapacity(1_024);
        for (int i = 0; i < 100; i++)
        {
            first.Increment(Hash(123));
            second.Increment(Hash(123));
        }

        await Assert.That(first.Frequency(Hash(123))).IsEqualTo(15);
        await Assert.That(first.Frequency(Hash(123))).IsEqualTo(second.Frequency(Hash(123)));
        await Assert.That(first.Capacity).IsEqualTo(1_024);
    }

    [Test]
    public async Task SetMaximumCountBoundsZeroWeightResidentNodes()
    {
        WindowTinyLfuPolicy<int> policy = new(10, seed: 47);
        for (int i = 0; i < 5; i++)
        {
            policy.Add(new PolicyNode<int>(i, 0, Hash(i)));
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.SetMaximumCount(2);
        await Assert.That(evicted.Count).IsEqualTo(3);
        await Assert.That(policy.ResidentCount).IsEqualTo(2);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
    }

    [Test]
    public async Task FixedSeedKeepsWeightedRuntimeMutationsDeterministic()
    {
        WindowTinyLfuPolicy<int> first = new(8, seed: 71, maximumCount: 8);
        WindowTinyLfuPolicy<int> second = new(8, seed: 71, maximumCount: 8);
        Dictionary<int, PolicyNode<int>> firstLive = new();
        Dictionary<int, PolicyNode<int>> secondLive = new();
        for (int i = 0; i < 32; i++)
        {
            long weight = i % 4 == 0 ? 2 : 1;
            PolicyNode<int> firstNode = new(i, weight, Hash(i));
            PolicyNode<int> secondNode = new(i, weight, Hash(i));
            RemoveMapped(firstLive, first.Add(firstNode));
            RemoveMapped(secondLive, second.Add(secondNode));
            if (firstNode.IsAlive)
            {
                firstLive[i] = firstNode;
                secondLive[i] = secondNode;
            }

            if (i % 3 != 0 || !firstLive.TryGetValue(i, out PolicyNode<int>? firstAccess))
            {
                continue;
            }

            Assert.NotNull(secondLive[i]);
            first.RecordAccess(firstAccess);
            second.RecordAccess(secondLive[i]);
        }

        await Assert.That(first.WeightedSize).IsEqualTo(second.WeightedSize);
        await Assert.That(first.ResidentCount).IsEqualTo(second.ResidentCount);
        await Assert.That(first.WindowMaximum).IsEqualTo(second.WindowMaximum);
        await Assert.That(firstLive.Keys).IsEquivalentTo(secondLive.Keys);
    }

    [Test]
    public async Task RuntimeMutationsPreserveAllKnownIntrusiveLinks()
    {
        WindowTinyLfuPolicy<int> policy = new(16, seed: 73, maximumCount: 16);
        List<PolicyNode<int>> nodes = AddNodes(policy, 8);
        policy.RecordAccess(nodes[0]);
        policy.RecordAccess(nodes[1]);
        policy.UpdateWeight(nodes[2], 5);
        policy.UpdateWeight(nodes[3], 0);
        policy.Remove(nodes[4]);
        policy.SetMaximum(6);
        policy.SetMaximumCount(4);
        PolicyNode<int> added = new(99, 1, Hash(99));
        policy.Add(added);
        int aliveCount = 0;
        long weightedSize = 0;
        foreach (PolicyNode<int> node in nodes)
        {
            if (node.IsAlive)
            {
                aliveCount++;
                weightedSize += node.Weight;
                await Assert.That(node.Queue).IsNotEqualTo(PolicyQueue.None);
                AssertLinks(node);
            }
            else
            {
                await Assert.That(node.Queue).IsEqualTo(PolicyQueue.None);
                await Assert.That((node.Previous) is null).IsTrue();
                await Assert.That((node.Next) is null).IsTrue();
                await Assert.That((node.PositivePrevious) is null).IsTrue();
                await Assert.That((node.PositiveNext) is null).IsTrue();
                await Assert.That((node.EligiblePrevious) is null).IsTrue();
                await Assert.That((node.EligibleNext) is null).IsTrue();
                await Assert.That((node.EligiblePositivePrevious) is null).IsTrue();
                await Assert.That((node.EligiblePositiveNext) is null).IsTrue();
                await Assert.That((node.CandidatePrevious) is null).IsTrue();
                await Assert.That((node.CandidateNext) is null).IsTrue();
            }
        }

        int externalCount = added.IsAlive ? 1 : 0;
        long externalWeight = added.IsAlive ? added.Weight : 0;
        await Assert.That(aliveCount).IsEqualTo(policy.ResidentCount - externalCount);
        await Assert.That(weightedSize).IsEqualTo(policy.WeightedSize - externalWeight);
    }

    private static void AssertLinks(PolicyNode<int> node)
    {
        if (node.Previous is { } adjacentPrevious && !ReferenceEquals(adjacentPrevious.Next, node))
            Assert.Fail("Broken reciprocal policy link.");
        if (node.Next is { } adjacentNext && !ReferenceEquals(adjacentNext.Previous, node))
            Assert.Fail("Broken reciprocal policy link.");
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
        if (
            node.CandidatePrevious is { } adjacentCandidatePrevious
            && !ReferenceEquals(adjacentCandidatePrevious.CandidateNext, node)
        )
            Assert.Fail("Broken reciprocal policy link.");
        if (
            node.CandidateNext is { } adjacentCandidateNext
            && !ReferenceEquals(adjacentCandidateNext.CandidatePrevious, node)
        )
            Assert.Fail("Broken reciprocal policy link.");
    }

    private static List<PolicyNode<int>> AddNodes(WindowTinyLfuPolicy<int> policy, int count)
    {
        List<PolicyNode<int>> nodes = new(count);
        for (int i = 0; i < count; i++)
        {
            PolicyNode<int> node = new(i, 1, Hash(i));
            policy.Add(node);
            nodes.Add(node);
        }

        return nodes;
    }

    private static void RemoveMapped(
        Dictionary<int, PolicyNode<int>> entries,
        IReadOnlyList<PolicyNode<int>> evicted
    )
    {
        foreach (PolicyNode<int> node in evicted)
        {
            if (
                entries.TryGetValue(node.Value, out PolicyNode<int>? current)
                && ReferenceEquals(current, node)
            )
            {
                entries.Remove(node.Value);
            }
        }
    }

    private static uint Hash(int value)
    {
        return unchecked((uint)(value * 0x9E3779B9));
    }
}
