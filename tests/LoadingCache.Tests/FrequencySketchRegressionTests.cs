using LoadingCache.Policy;

namespace LoadingCache.Tests;

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
    public async Task UninitializedIncrementsDoNotCreateCounters()
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

        await Assert.That(actual.IsInitialized).IsFalse();
    }

    [Test]
    [Arguments(0u)]
    [Arguments(43u)]
    [Arguments(uint.MaxValue)]
    public async Task HotUniformAndChangedPhasesMatchUnpackedCounters(uint seed)
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

        await Assert.That(expected.ResetCount).IsGreaterThan(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OneSaturatedLaneDoesNotPreventTheOtherThreeUpdates(int saturatedLane)
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

        await Assert.That(actual.Frequency(target)).IsEqualTo(0);
        for (int step = 15; step < 19; step++)
        {
            actual.Increment(target);
            expected.Increment(target);
            AssertEquivalent(actual, expected, target, step);
            await Assert.That(actual.Frequency(target)).IsEqualTo(step - 14);
        }
    }

    [Test]
    public async Task FullySaturatedCountersDoNotAdvanceTheSample()
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

        await Assert.That(actual.SampleCount).IsEqualTo(15);
        await Assert.That(actual.Frequency(hash)).IsEqualTo(15);
        await Assert.That(expected.ResetCount).IsEqualTo(0);
    }

    [Test]
    public async Task ResizeClampAndSampleResetPreserveTheReferenceState()
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

        await Assert.That(actual.SampleCount).IsGreaterThan(10);
        actual.EnsureCapacity(0);
        expected.EnsureCapacity(0);
        AssertEquivalent(actual, expected, 63, 64);
        await Assert.That(actual.SampleCount).IsEqualTo(9);
        await Assert.That(actual.Capacity).IsEqualTo(64);
        actual.Increment(uint.MaxValue);
        expected.Increment(uint.MaxValue);
        AssertEquivalent(actual, expected, uint.MaxValue, 65);
        await Assert.That(expected.ResetCount).IsEqualTo(1);
        actual.EnsureCapacity(128);
        expected.EnsureCapacity(128);
        AssertEquivalent(actual, expected, 63, 66);
        await Assert.That(actual.Frequency(63)).IsEqualTo(0);
        actual.Increment(1);
        expected.Increment(1);
        AssertEquivalent(actual, expected, 1, 67);
        actual.ResetSampleCount();
        expected.ResetSampleCount();
        AssertEquivalent(actual, expected, 1, 68);
        await Assert.That(actual.Frequency(1)).IsEqualTo(1);
        actual.EnsureCapacity(16);
        expected.EnsureCapacity(16);
        AssertEquivalent(actual, expected, 1, 69);
        await Assert.That(actual.Capacity).IsEqualTo(128);
    }

    [Test]
    [Arguments(0L, 8, 10L)]
    [Arguments(1L, 8, 10L)]
    [Arguments(8L, 8, 80L)]
    [Arguments(9L, 16, 90L)]
    [Arguments(65L, 128, 650L)]
    [Arguments(long.MaxValue, 1 << 20, (long)int.MaxValue)]
    public async Task CapacityBoundariesAndExtremeHashesMatchUnpackedCounters(
        long estimatedEntries,
        int capacity,
        long sampleSize
    )
    {
        FrequencySketch actual = new(0x80000000);
        UnpackedSketch expected = new(0x80000000);
        actual.EnsureCapacity(estimatedEntries);
        expected.EnsureCapacity(estimatedEntries);
        await Assert.That(actual.Capacity).IsEqualTo(capacity);
        await Assert.That(actual.SampleSize).IsEqualTo(sampleSize);
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
        string context =
            $"seed={expected.Seed}, step={step}, capacity={expected.Capacity}, hash={hash:X8}: ";
        if ((actual.Capacity) != (expected.Capacity))
            Assert.Fail(
                context + $"Expected capacity {expected.Capacity}, found {actual.Capacity}."
            );
        if ((actual.SampleSize) != (expected.SampleSize))
            Assert.Fail(
                context + $"Expected sample size {expected.SampleSize}, found {actual.SampleSize}."
            );
        if ((actual.SampleCount) != (expected.SampleCount))
            Assert.Fail(
                context
                    + $"Expected sample count {expected.SampleCount}, found {actual.SampleCount}."
            );
        if ((actual.Frequency(hash)) != (expected.Frequency(hash)))
            Assert.Fail(
                context
                    + $"Expected frequency {expected.Frequency(hash)}, found {actual.Frequency(hash)}."
            );
        foreach (uint probe in ProbeHashes)
        {
            if ((actual.Frequency(probe)) != (expected.Frequency(probe)))
                Assert.Fail(
                    context
                        + $"probe={probe:X8}: expected frequency {expected.Frequency(probe)}, found {actual.Frequency(probe)}."
                );
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
            return block * 128 + lane * 32 + row % 2 * 16 + row / 2 % 16;
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
                if (_counters[index] >= 15)
                {
                    continue;
                }

                _counters[index]++;
                changed = true;
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
