using System.Numerics;

namespace LoadingCache.Policy;

// Copyright 2015 Ben Manes. Portions of the sketch algorithm and constants are
// adapted from Caffeine 3.2.4's FrequencySketch.java (Apache-2.0,
// commit 836b65c0a83e5d1641ded9c6de578654bc04b2e9):
// https://github.com/ben-manes/caffeine/blob/836b65c0a83e5d1641ded9c6de578654bc04b2e9/caffeine/src/main/java/com/github/benmanes/caffeine/cache/FrequencySketch.java
// The uint hashing, bounded table, and caller-selected seed are original .NET integration
// choices. This is an algorithm adaptation, not a source-compatible port or an upstream
// endorsement.
internal sealed class FrequencySketch
{
    private const ulong ResetMask = 0x7777777777777777UL;
    private const ulong OneMask = 0x1111111111111111UL;
    private const int CounterMask = 0xF;
    private const int MaximumCounter = 15;
    private const int MinimumTableLength = 8;
    private const int MaximumTableLength = 1 << 20;

    private readonly uint _seed;
    private ulong[]? _table;
    private int _blockMask;

    internal FrequencySketch(uint seed)
    {
        _seed = seed;
    }

    internal int Capacity => _table?.Length ?? 0;

    internal long SampleSize { get; private set; }

    internal long SampleCount { get; private set; }

    internal void EnsureCapacity(long estimatedEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedEntries);

        long sampleSize =
            estimatedEntries > int.MaxValue / 10L
                ? int.MaxValue
                : Math.Max(10L, estimatedEntries * 10L);
        int tableLength = TableLengthFor(estimatedEntries);

        if (_table is not null && _table.Length >= tableLength)
        {
            SampleSize = sampleSize;
            SampleCount = Math.Min(SampleCount, Math.Max(0, SampleSize - 1));
            return;
        }

        _table = new ulong[tableLength];
        _blockMask = (tableLength >> 3) - 1;
        SampleSize = sampleSize;
        SampleCount = 0;
    }

    internal void ResetSampleCount()
    {
        SampleCount = 0;
    }

    internal int Frequency(uint hash)
    {
        ulong[]? table = _table;
        if (table is null)
        {
            return 0;
        }

        uint blockHash = Spread(hash ^ _seed);
        uint counterHash = Rehash(blockHash);
        int block = checked((int)(blockHash & (uint)_blockMask)) << 3;
        int frequency = MaximumCounter;

        for (int i = 0; i < 4; i++)
        {
            uint h = counterHash >> (i * 8);
            int index = (int)((h >> 1) & 15);
            int offset = (int)(h & 1);
            int slot = block + offset + (i << 1);
            int count = (int)((table[slot] >> (index << 2)) & CounterMask);
            frequency = Math.Min(frequency, count);
        }

        return frequency;
    }

    internal void Increment(uint hash)
    {
        ulong[]? table = _table;
        if (table is null)
        {
            return;
        }

        uint blockHash = Spread(hash ^ _seed);
        uint counterHash = Rehash(blockHash);
        int block = checked((int)(blockHash & (uint)_blockMask)) << 3;

        bool incremented = false;
        for (int i = 0; i < 4; i++)
        {
            uint h = counterHash >> (i * 8);
            int index = (int)((h >> 1) & 15);
            int offset = (int)(h & 1);
            int slot = block + offset + (i << 1);
            incremented |= IncrementAt(table, slot, index);
        }

        if (incremented && ++SampleCount >= SampleSize)
        {
            Reset();
        }
    }

    private void Reset()
    {
        ulong[]? table = _table;
        if (table is null)
        {
            return;
        }

        long oddCounters = 0;
        for (int i = 0; i < table.Length; i++)
        {
            ulong value = table[i];
            oddCounters += BitOperations.PopCount(value & OneMask);
            table[i] = (value >> 1) & ResetMask;
        }

        SampleCount = Math.Max(0, (SampleCount - (oddCounters >> 2)) >> 1);
    }

    private static int TableLengthFor(long estimatedEntries)
    {
        if (estimatedEntries <= MinimumTableLength)
        {
            return MinimumTableLength;
        }

        long target = Math.Min(estimatedEntries, MaximumTableLength);
        int length = MinimumTableLength;
        while (length < target)
        {
            length <<= 1;
        }

        return length;
    }

    private static bool IncrementAt(ulong[] table, int slot, int index)
    {
        int offset = index << 2;
        ulong mask = (ulong)CounterMask << offset;
        if ((table[slot] & mask) == mask)
        {
            return false;
        }

        table[slot] += 1UL << offset;
        return true;
    }

    private static uint Spread(uint value)
    {
        unchecked
        {
            value ^= value >> 17;
            value *= 0xED5AD4BBu;
            value ^= value >> 11;
            value *= 0xAC4C1B51u;
            value ^= value >> 15;
            return value;
        }
    }

    private static uint Rehash(uint value)
    {
        unchecked
        {
            value *= 0x31848BABu;
            value ^= value >> 14;
            return value;
        }
    }
}
