using FluentAssertions;
using LoadingCache.Policy;

namespace LoadingCache.Tests;

public sealed class PolicyTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(17)]
    public void TinyCapacitiesConvergeWithoutEmptySegmentFailures(int capacity)
    {
        WindowTinyLfuPolicy<int> policy = new(capacity, seed: 7, maximumCount: capacity);
        for (int i = 0; i < capacity * 4; i++)
        {
            PolicyNode<int> node = new(i, 1, Hash(i));
            policy.Add(node);
        }

        policy.Maintain();
        policy.ResidentCount.Should().BeLessThanOrEqualTo(capacity);
        policy.WeightedSize.Should().BeLessThanOrEqualTo(capacity);
        policy.WindowMaximum.Should().BeInRange(1, capacity);
        policy.MainMaximum.Should().BeGreaterThanOrEqualTo(0);
        policy.ProtectedMaximum.Should().BeLessThanOrEqualTo(policy.MainMaximum);
    }

    [Test]
    public void NegativeWeightIsRejectedBeforeNodeCanEnterPolicy()
    {
        Action action = () => _ = new PolicyNode<string>("negative", -1, 1);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void NegativeWeightUpdateIsRejectedWithoutMutatingTheResident()
    {
        WindowTinyLfuPolicy<int> policy = new(4, seed: 5);
        PolicyNode<int> node = new(1, 1, Hash(1));
        policy.Add(node);
        Action action = () => policy.UpdateWeight(node, -1);
        action.Should().Throw<ArgumentOutOfRangeException>();
        node.IsAlive.Should().BeTrue();
        node.Weight.Should().Be(1);
        policy.WeightedSize.Should().Be(1);
    }

    [Test]
    public void ZeroWeightDoesNotImplicitlyConsumeWeightCapacity()
    {
        WindowTinyLfuPolicy<string> policy = new(1, seed: 11);
        PolicyNode<string> first = new("first", 0, 1);
        PolicyNode<string> second = new("second", 0, 2);
        policy.Add(first);
        policy.Add(second);
        policy.WeightedSize.Should().Be(0);
        policy.ResidentCount.Should().Be(2);
        first.IsAlive.Should().BeTrue();
        second.IsAlive.Should().BeTrue();
        IReadOnlyList<PolicyNode<string>> evicted = policy.SetMaximumCount(1);
        evicted.Should().HaveCount(1);
        policy.ResidentCount.Should().Be(1);
        policy.WeightedSize.Should().Be(0);
    }

    [Test]
    public void OversizedWeightIsReturnedAsExactRejectedNode()
    {
        WindowTinyLfuPolicy<int> policy = new(3, seed: 13);
        PolicyNode<int> node = new(7, 4, Hash(7));
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(node);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(node);
        node.IsAlive.Should().BeFalse();
        node.Queue.Should().Be(PolicyQueue.None);
        policy.WeightedSize.Should().Be(0);
        policy.ResidentCount.Should().Be(0);
    }

    [Test]
    public void LongMaximumDoesNotOverflowWeightedAccounting()
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
        evicted.Should().ContainSingle().Which.Should().BeSameAs(maximum);
        maximum.IsAlive.Should().BeFalse();
        overflow.IsAlive.Should().BeTrue();
        policy.WeightedSize.Should().Be(1);
        policy.ResidentCount.Should().Be(1);
    }

    [Test]
    public void WeightIncreaseNearLongMaximumEvictsAnExistingNodeBeforeAccounting()
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
        evicted.Should().ContainSingle().Which.Should().BeSameAs(existing);
        existing.IsAlive.Should().BeFalse();
        changing.IsAlive.Should().BeTrue();
        policy.WeightedSize.Should().Be(2);
    }

    [Test]
    public void OverflowAdmissionDoesNotDiscardAHotExistingVictim()
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
        evicted.Should().ContainSingle().Which.Should().BeSameAs(cold);
        hot.IsAlive.Should().BeTrue();
        policy.WeightedSize.Should().Be(weight);
    }

    [Test]
    public void ColdTieRejectsCandidateAndKeepsExistingVictim()
    {
        WindowTinyLfuPolicy<int> policy = new(1, seed: 19);
        PolicyNode<int> victim = new(1, 1, Hash(1));
        PolicyNode<int> candidate = new(2, 1, Hash(2));
        policy.Add(victim);
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(candidate);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(victim);
        victim.IsAlive.Should().BeFalse();
        candidate.IsAlive.Should().BeTrue();
        policy.ResidentCount.Should().Be(1);
    }

    [Test]
    public void ColdFillClearsCandidateBeforeLaterHotVictimAdmission()
    {
        WindowTinyLfuPolicy<int> policy = new(2, seed: 53);
        PolicyNode<int> hotVictim = new(1, 1, Hash(1));
        PolicyNode<int> admitted = new(2, 1, Hash(2));
        policy.Add(hotVictim);
        policy.Add(admitted);
        hotVictim.Queue.Should().Be(PolicyQueue.Probation);
        hotVictim.IsCandidate.Should().BeFalse();
        policy.RecordAccess(hotVictim).Should().BeTrue();
        hotVictim.Queue.Should().Be(PolicyQueue.Probation);
        hotVictim.IsCandidate.Should().BeFalse();
        PolicyNode<int> candidate = new(3, 1, Hash(3));
        IReadOnlyList<PolicyNode<int>> evicted = policy.Add(candidate);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(admitted);
        hotVictim.IsAlive.Should().BeTrue();
        candidate.IsAlive.Should().BeTrue();
        policy.ResidentCount.Should().Be(2);
    }

    [Test]
    public void HotCandidateCanDisplaceColdVictim()
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
        coldVictim.IsAlive.Should().BeFalse();
        hotCandidate.IsAlive.Should().BeTrue();
        policy.ResidentCount.Should().Be(3);
    }

    [Test]
    public void ProbationAccessPromotesAndProtectedOverflowDemotesOldest()
    {
        WindowTinyLfuPolicy<int> policy = new(10, seed: 29);
        PolicyNode<int> first = new(1, 1, Hash(1));
        PolicyNode<int> second = new(2, 1, Hash(2));
        policy.Add(first);
        policy.Add(second);
        first.Queue.Should().Be(PolicyQueue.Probation);
        policy.RecordAccess(first).Should().BeTrue();
        first.Queue.Should().Be(PolicyQueue.Protected);
        policy.ProtectedCount.Should().Be(1);
        policy.MainProtectedWeightedSize.Should().Be(1);
    }

    [Test]
    public void RemovingStaleNodeDoesNotAffectAnotherGeneration()
    {
        WindowTinyLfuPolicy<int> policy = new(4, seed: 31);
        PolicyNode<int> oldNode = new(1, 1, Hash(1));
        PolicyNode<int> currentNode = new(2, 1, Hash(2));
        policy.Add(oldNode);
        policy.Remove(oldNode).Should().ContainSingle().Which.Should().BeSameAs(oldNode);
        policy.Add(currentNode);
        policy.RecordAccess(oldNode).Should().BeFalse();
        policy.Remove(oldNode).Should().BeEmpty();
        currentNode.IsAlive.Should().BeTrue();
        policy.ResidentCount.Should().Be(1);
        policy.WeightedSize.Should().Be(1);
    }

    [Test]
    public void SetMaximumReturnsExactVictimsAndConverges()
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
        evicted.Should().NotBeEmpty();
        evicted.Should().OnlyContain(node => nodes.Contains(node));
        policy.WeightedSize.Should().BeLessThanOrEqualTo(3);
        policy.ResidentCount.Should().BeLessThanOrEqualTo(16);
        policy.WindowMaximum.Should().BeInRange(1, 3);
    }

    [Test]
    public void AdaptiveSamplingUsesMinimumProgressForTinyPolicy()
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
        Math.Abs(policy.StepSize).Should().BeGreaterThanOrEqualTo(1);
        policy.WindowMaximum.Should().BeInRange(1, policy.Maximum);
        policy.ProtectedMaximum.Should().BeLessThanOrEqualTo(policy.MainMaximum);
    }

    [Test]
    public void AdaptiveWindowGrowsThenShrinksAcrossSamples()
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
        grownWindow.Should().BeGreaterThan(initialWindow);
        for (long i = 0; i < sampleSize; i++)
        {
            policy.RecordMiss(Hash((int)(i + 10_000)));
        }

        policy.Maintain(10_000);
        policy.WindowMaximum.Should().BeLessThan(grownWindow);
    }

    [Test]
    public void PendingAdjustmentSurvivesBudgetExhaustionAndDelaysNextSample()
    {
        WindowTinyLfuPolicy<int> policy = new(256, seed: 61);
        List<PolicyNode<int>> nodes = AddNodes(policy, 256);
        long sampleSize = policy.SketchSampleSize;
        for (long i = policy.MissesInSample; i < sampleSize; i++)
        {
            policy.RecordAccess(nodes[0]);
        }

        policy.Maintain(1);
        policy.Adjustment.Should().NotBe(0);
        for (long i = 0; i < sampleSize; i++)
        {
            policy.RecordMiss(Hash((int)(i + 20_000)));
        }

        policy.Maintain(1);
        policy.MissesInSample.Should().Be(sampleSize);
        policy.Adjustment.Should().NotBe(0);
        policy.Maintain(10_000);
        policy.Adjustment.Should().Be(0);
    }

    [Test]
    public void RepeatedSmallMaintenanceBudgetMakesWindowTransferProgress()
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

        policy.WindowMaximum.Should().BeGreaterThan(initialWindow);
        policy.Adjustment.Should().Be(0);
    }

    [Test]
    public void SetMaximumResetsSketchSamplingWithoutDiscardingFrequency()
    {
        WindowTinyLfuPolicy<int> policy = new(256, seed: 67);
        uint hash = Hash(71);
        for (int i = 0; i < 32; i++)
        {
            policy.RecordMiss(hash);
        }

        int frequency = policy.Frequency(hash);
        policy.SketchSampleCount.Should().BeGreaterThan(0);
        policy.SetMaximum(128);
        policy.SketchSampleCount.Should().Be(0);
        policy.Frequency(hash).Should().Be(frequency);
    }

    [Test]
    public void FrequencySketchIsSaturatingAndSeedDeterministic()
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

        first.Frequency(Hash(123)).Should().Be(15);
        first.Frequency(Hash(123)).Should().Be(second.Frequency(Hash(123)));
        first.Capacity.Should().Be(1_024);
    }

    [Test]
    public void SetMaximumCountBoundsZeroWeightResidentNodes()
    {
        WindowTinyLfuPolicy<int> policy = new(10, seed: 47);
        for (int i = 0; i < 5; i++)
        {
            policy.Add(new PolicyNode<int>(i, 0, Hash(i)));
        }

        IReadOnlyList<PolicyNode<int>> evicted = policy.SetMaximumCount(2);
        evicted.Should().HaveCount(3);
        policy.ResidentCount.Should().Be(2);
        policy.WeightedSize.Should().Be(0);
    }

    [Test]
    public void FixedSeedKeepsWeightedRuntimeMutationsDeterministic()
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

            secondLive[i].Should().NotBeNull();
            first.RecordAccess(firstAccess);
            second.RecordAccess(secondLive[i]);
        }

        first.WeightedSize.Should().Be(second.WeightedSize);
        first.ResidentCount.Should().Be(second.ResidentCount);
        first.WindowMaximum.Should().Be(second.WindowMaximum);
        firstLive.Keys.Should().BeEquivalentTo(secondLive.Keys);
    }

    [Test]
    public void RuntimeMutationsPreserveAllKnownIntrusiveLinks()
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
                node.Queue.Should().NotBe(PolicyQueue.None);
                AssertLinks(node);
            }
            else
            {
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

        int externalCount = added.IsAlive ? 1 : 0;
        long externalWeight = added.IsAlive ? added.Weight : 0;
        aliveCount.Should().Be(policy.ResidentCount - externalCount);
        weightedSize.Should().Be(policy.WeightedSize - externalWeight);
    }

    private static void AssertLinks(PolicyNode<int> node)
    {
        node.Previous?.Next.Should().BeSameAs(node);
        node.Next?.Previous.Should().BeSameAs(node);
        node.PositivePrevious?.PositiveNext.Should().BeSameAs(node);
        node.PositiveNext?.PositivePrevious.Should().BeSameAs(node);
        node.EligiblePrevious?.EligibleNext.Should().BeSameAs(node);
        node.EligibleNext?.EligiblePrevious.Should().BeSameAs(node);
        node.EligiblePositivePrevious?.EligiblePositiveNext.Should().BeSameAs(node);
        node.EligiblePositiveNext?.EligiblePositivePrevious.Should().BeSameAs(node);
        node.CandidatePrevious?.CandidateNext.Should().BeSameAs(node);
        node.CandidateNext?.CandidatePrevious.Should().BeSameAs(node);
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
