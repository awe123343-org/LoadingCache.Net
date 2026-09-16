using FluentAssertions;
using FluentAssertions.Execution;
using LoadingCache.Policy;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class FrequencySketchRegressionTests
{
    private static readonly uint[] ProbeHashes =
    [
        0,
        1,
        0x12345678,
        0x7FFFFFFF,
        0x80000000,
        0xFFFFFFFE,
        uint.MaxValue,
    ];

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void UninitializedIncrementsDoNotCreateCounters()
    {
        FrequencySketch actual = new(uint.MaxValue);
        UnpackedSketch expected = new(uint.MaxValue);

        for (int step = 0; step < ProbeHashes.Length; step++)
        {
            uint hash = ProbeHashes[step];
            actual.Increment(hash);
            expected.Increment(hash);
            AssertEquivalent(actual, expected, hash, step);
        }

        actual.IsInitialized.Should().BeFalse();
    }

    [TestCase(0u)]
    [TestCase(43u)]
    [TestCase(uint.MaxValue)]
    [Parallelizable(ParallelScope.Self)]
    public void HotUniformAndChangedPhasesMatchUnpackedCounters(uint seed)
    {
        FrequencySketch actual = new(seed);
        UnpackedSketch expected = new(seed);
        actual.EnsureCapacity(16);
        expected.EnsureCapacity(16);
        uint random = seed ^ 0x9E3779B9;

        for (int step = 0; step < 1_024; step++)
        {
            random = unchecked(random * 1664525u + 1013904223u);
            uint hash = step switch
            {
                < 128 => (step & 3) == 0 ? uint.MaxValue : 0,
                < 640 => random,
                _ => (step & 7) == 0 ? random : 0x80000000u + (uint)((step >> 5) & 3),
            };

            actual.Increment(hash);
            expected.Increment(hash);
            AssertEquivalent(actual, expected, hash, step);
        }

        expected.ResetCount.Should().BeGreaterThan(0);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [Parallelizable(ParallelScope.Self)]
    public void OneSaturatedLaneDoesNotPreventTheOtherThreeUpdates(int saturatedLane)
    {
        const uint target = 0x12345678;
        FrequencySketch actual = new(43);
        UnpackedSketch expected = new(43);
        actual.EnsureCapacity(8);
        expected.EnsureCapacity(8);
        uint collider = FindSingleLaneCollision(expected, target, saturatedLane);

        for (int step = 0; step < 15; step++)
        {
            actual.Increment(collider);
            expected.Increment(collider);
            AssertEquivalent(actual, expected, collider, step);
        }

        actual.Frequency(target).Should().Be(0);
        for (int step = 15; step < 19; step++)
        {
            actual.Increment(target);
            expected.Increment(target);
            AssertEquivalent(actual, expected, target, step);
            actual.Frequency(target).Should().Be(step - 14);
        }
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void FullySaturatedCountersDoNotAdvanceTheSample()
    {
        const uint hash = uint.MaxValue;
        FrequencySketch actual = new(7);
        UnpackedSketch expected = new(7);
        actual.EnsureCapacity(64);
        expected.EnsureCapacity(64);

        for (int step = 0; step < 96; step++)
        {
            actual.Increment(hash);
            expected.Increment(hash);
            AssertEquivalent(actual, expected, hash, step);
        }

        actual.SampleCount.Should().Be(15);
        actual.Frequency(hash).Should().Be(15);
        expected.ResetCount.Should().Be(0);
    }

    [Test]
    [Parallelizable(ParallelScope.Self)]
    public void ResizeClampAndSampleResetPreserveTheReferenceState()
    {
        FrequencySketch actual = new(19);
        UnpackedSketch expected = new(19);
        actual.EnsureCapacity(64);
        expected.EnsureCapacity(64);

        for (uint hash = 0; hash < 64; hash++)
        {
            actual.Increment(hash);
            expected.Increment(hash);
            AssertEquivalent(actual, expected, hash, (int)hash);
        }

        actual.SampleCount.Should().BeGreaterThan(10);
        actual.EnsureCapacity(0);
        expected.EnsureCapacity(0);
        AssertEquivalent(actual, expected, 63, 64);
        actual.SampleCount.Should().Be(9);
        actual.Capacity.Should().Be(64);

        actual.Increment(uint.MaxValue);
        expected.Increment(uint.MaxValue);
        AssertEquivalent(actual, expected, uint.MaxValue, 65);
        expected.ResetCount.Should().Be(1);

        actual.EnsureCapacity(128);
        expected.EnsureCapacity(128);
        AssertEquivalent(actual, expected, 63, 66);
        actual.Frequency(63).Should().Be(0);

        actual.Increment(1);
        expected.Increment(1);
        AssertEquivalent(actual, expected, 1, 67);
        actual.ResetSampleCount();
        expected.ResetSampleCount();
        AssertEquivalent(actual, expected, 1, 68);
        actual.Frequency(1).Should().Be(1);

        actual.EnsureCapacity(16);
        expected.EnsureCapacity(16);
        AssertEquivalent(actual, expected, 1, 69);
        actual.Capacity.Should().Be(128);
    }

    [TestCase(0L, 8, 10L)]
    [TestCase(1L, 8, 10L)]
    [TestCase(8L, 8, 80L)]
    [TestCase(9L, 16, 90L)]
    [TestCase(65L, 128, 650L)]
    [TestCase(long.MaxValue, 1 << 20, (long)int.MaxValue)]
    [Parallelizable(ParallelScope.Self)]
    public void CapacityBoundariesAndExtremeHashesMatchUnpackedCounters(
        long estimatedEntries,
        int capacity,
        long sampleSize
    )
    {
        FrequencySketch actual = new(0x80000000);
        UnpackedSketch expected = new(0x80000000);
        actual.EnsureCapacity(estimatedEntries);
        expected.EnsureCapacity(estimatedEntries);
        actual.Capacity.Should().Be(capacity);
        actual.SampleSize.Should().Be(sampleSize);

        for (int step = 0; step < ProbeHashes.Length; step++)
        {
            uint hash = ProbeHashes[step];
            actual.Increment(hash);
            expected.Increment(hash);
            AssertEquivalent(actual, expected, hash, step);
        }
    }

    private static uint FindSingleLaneCollision(UnpackedSketch sketch, uint target, int lane)
    {
        for (uint candidate = 0; candidate < 4_096; candidate++)
        {
            bool matches = true;
            for (int other = 0; other < 4; other++)
            {
                bool sharesCounter =
                    sketch.CounterIndex(candidate, other) == sketch.CounterIndex(target, other);
                matches &= sharesCounter == (other == lane);
            }

            if (matches)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No single-lane collision found for lane {lane}.");
    }

    private static void AssertEquivalent(
        FrequencySketch actual,
        UnpackedSketch expected,
        uint hash,
        int step
    )
    {
        using var scope = new AssertionScope(
            $"seed={expected.Seed}, step={step}, capacity={expected.Capacity}, hash={hash:X8}"
        );
        actual.Capacity.Should().Be(expected.Capacity);
        actual.SampleSize.Should().Be(expected.SampleSize);
        actual.SampleCount.Should().Be(expected.SampleCount);
        actual.Frequency(hash).Should().Be(expected.Frequency(hash));
        foreach (uint probe in ProbeHashes)
        {
            actual.Frequency(probe).Should().Be(expected.Frequency(probe), $"probe={probe:X8}");
        }
    }

    // Each logical four-bit counter occupies a separate byte. This oracle never uses
    // packed-word increment/masks, and aging divides the independent counters by two.
    private sealed class UnpackedSketch(uint seed)
    {
        private byte[]? _counters;

        internal uint Seed { get; } = seed;
        internal int Capacity => (_counters?.Length ?? 0) / 16;
        internal long SampleSize { get; private set; }
        internal long SampleCount { get; private set; }
        internal int ResetCount { get; private set; }

        internal void EnsureCapacity(long estimatedEntries)
        {
            SampleSize =
                estimatedEntries > int.MaxValue / 10L
                    ? int.MaxValue
                    : Math.Max(10L, estimatedEntries * 10L);
            int capacity = 8;
            while (capacity < Math.Min(estimatedEntries, 1 << 20))
            {
                capacity *= 2;
            }

            if (Capacity >= capacity)
            {
                SampleCount = Math.Min(SampleCount, SampleSize - 1);
                return;
            }

            _counters = new byte[capacity * 16];
            SampleCount = 0;
        }

        internal void ResetSampleCount() => SampleCount = 0;

        internal int CounterIndex(uint hash, int lane)
        {
            uint mixed = hash ^ Seed;
            mixed = unchecked((mixed ^ (mixed >> 17)) * 0xED5AD4BBu);
            mixed = unchecked((mixed ^ (mixed >> 11)) * 0xAC4C1B51u);
            mixed ^= mixed >> 15;
            int block = (int)(mixed % (uint)(Capacity / 8));
            uint counters = unchecked(mixed * 0x31848BABu);
            counters ^= counters >> 14;
            byte row = (byte)(counters >> (lane * 8));
            return block * 128 + lane * 32 + (row % 2) * 16 + (row / 2) % 16;
        }

        internal int Frequency(uint hash)
        {
            if (_counters is null)
            {
                return 0;
            }

            int frequency = 15;
            for (int lane = 0; lane < 4; lane++)
            {
                frequency = Math.Min(frequency, _counters[CounterIndex(hash, lane)]);
            }
            return frequency;
        }

        internal void Increment(uint hash)
        {
            if (_counters is null)
            {
                return;
            }

            bool changed = false;
            for (int lane = 0; lane < 4; lane++)
            {
                int index = CounterIndex(hash, lane);
                if (_counters[index] < 15)
                {
                    _counters[index]++;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            SampleCount++;
            if (SampleCount < SampleSize)
            {
                return;
            }

            long odd = 0;
            for (int index = 0; index < _counters.Length; index++)
            {
                odd += _counters[index] % 2;
                _counters[index] /= 2;
            }
            SampleCount = Math.Max(0, (SampleCount - odd / 4) / 2);
            ResetCount++;
        }
    }
}
