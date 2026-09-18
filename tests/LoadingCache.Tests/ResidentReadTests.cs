using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ResidentReadTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public Task Int32ReadDoesNotWaitForResidentPublicationLock() =>
        VerifyReadDuringResidentPublication(11, 22);

    [Test]
    public Task Int64ReadDoesNotWaitForResidentPublicationLock() =>
        VerifyReadDuringResidentPublication(0x12345678abcdef01L, 0x23456789abcdef12L);

    [Test]
    public Task ReferenceReadDoesNotWaitForResidentPublicationLock() =>
        VerifyReadDuringResidentPublication("first", "second");

    private static async Task VerifyReadDuringResidentPublication<TValue>(
        TValue first,
        TValue second
    )
        where TValue : notnull
    {
        await using var publicationGate = new BlockingTestHook(Watchdog);
        CacheEngine<int, TValue> engine = CacheBuilder
            .Create<int, TValue>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .RecordStatistics()
            .CreateEngine(
                new LoadingCacheTestHooks { BeforeResidentValuePublished = publicationGate.Invoke },
                supportsBulkLoading: false
            );
        using var cache = new Cache<int, TValue>(engine);
        await VerifyReadDuringResidentPublication(cache, publicationGate, first, second);
    }

    private static async Task VerifyReadDuringResidentPublication<TValue>(
        Cache<int, TValue> cache,
        BlockingTestHook publicationGate,
        TValue first,
        TValue second
    )
        where TValue : notnull
    {
        cache.Put(1, first);
        Task writer = Task.Run(() => cache.Put(1, second));
        Task<TValue>? reader = null;
        try
        {
            // The replacement pauses before publishing while holding the entry lock.
            // A plain resident hit must finish before that writer is released.
            await publicationGate.Entered.WaitAsync(Watchdog);
            reader = Task.Run(() =>
            {
                cache.TryGet(1, out TValue? value).Should().BeTrue();
                return value!;
            });
            (await reader.WaitAsync(Watchdog)).Should().Be(first);
        }
        finally
        {
            publicationGate.Release();
            await Task.WhenAll(writer, reader ?? Task.CompletedTask).WaitAsync(Watchdog);
        }

        publicationGate.TimedOut.Should().BeFalse();
        cache.TryGet(1, out TValue? current).Should().BeTrue();
        current.Should().Be(second);
        cache.Statistics.Hits.Should().Be(2);
        cache.Invalidate(1).Should().BeTrue();
        cache.TryGet(1, out _).Should().BeFalse();
        cache.Put(1, first);
        cache.Clear();
        cache.TryGet(1, out _).Should().BeFalse();
        cache.Put(1, second);
        cache.TryGet(1, out current).Should().BeTrue();
        current.Should().Be(second);
    }

    [Test]
    public async Task ResidentValueAndTaskReadsWithoutTimePoliciesDoNotSampleTheClock()
    {
        var clock = new RejectReadTimeProvider();
        await using IAsyncLoadingCache<string, string> cache = CacheBuilder
            .Create<string, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .TimeProvider(clock)
            .BuildAsyncLoading((_, _) => Task.FromResult("unexpected load"));
        cache.Set("Canonical", "resident");
        clock.RejectTimestamps = true;

        cache.TryGet("canonical", out string? value).Should().BeTrue();
        value.Should().Be("resident");
        (await cache.GetAsync("CANONICAL")).Should().Be("resident");
        cache.TryGetTask("canonical", out Task<string>? first).Should().BeTrue();
        cache.TryGetTask("CANONICAL", out Task<string>? second).Should().BeTrue();
        second.Should().BeSameAs(first);
        (await first!).Should().Be("resident");
    }

    [Test]
    public void LargeStructFallbackWithoutTimePoliciesDoesNotSampleTheClock()
    {
        var clock = new RejectReadTimeProvider();
        using ICache<int, LargeValue> cache = CacheBuilder
            .Create<int, LargeValue>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .Build();
        var expected = new LargeValue(7);
        cache.Put(1, expected);
        clock.RejectTimestamps = true;
        cache.TryGet(1, out LargeValue value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Test]
    public Task Int64ConcurrentReplacementNeverTearsTheValue() =>
        VerifyConcurrentReplacement(
            static version => ((long)version << 32) | (uint)~version,
            static value => (int)(value >> 32) == ~(int)value
        );

    [Test]
    public Task ReferenceConcurrentReplacementPublishesInitializedObjects() =>
        VerifyConcurrentReplacement(
            static version => new ReferenceValue(version),
            static value => value.Complement == ~value.Version
        );

    [Test]
    public Task LargeStructConcurrentReplacementNeverTearsTheValue() =>
        VerifyConcurrentReplacement(
            static version => new LargeValue(version),
            static value => value.IsConsistent
        );

    private static async Task VerifyConcurrentReplacement<TValue>(
        Func<int, TValue> createValue,
        Func<TValue, bool> isConsistent
    )
        where TValue : notnull
    {
        using ICache<int, TValue> cache = CacheBuilder
            .Create<int, TValue>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Build();
        await VerifyConcurrentReplacement(cache, createValue, isConsistent);
    }

    private static async Task VerifyConcurrentReplacement<TValue>(
        ICache<int, TValue> cache,
        Func<int, TValue> createValue,
        Func<TValue, bool> isConsistent
    )
        where TValue : notnull
    {
        const int iterations = 4096;
        cache.Put(1, createValue(0));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task writer = Task.Run(async () =>
        {
            await start.Task;
            for (int version = 1; version <= iterations; version++)
            {
                cache.Put(1, createValue(version));
            }
        });
        Task[] readers =
        [
            .. Enumerable
                .Range(0, 3)
                .Select(_ =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        for (int iteration = 0; iteration < iterations; iteration++)
                        {
                            if (!cache.TryGet(1, out TValue? value) || !isConsistent(value))
                            {
                                throw new InvalidOperationException(
                                    "A resident publication was torn or lost."
                                );
                            }
                        }
                    })
                ),
        ];
        start.SetResult();
        await Task.WhenAll(readers.Append(writer)).WaitAsync(Watchdog);
        cache.TryGet(1, out TValue? final).Should().BeTrue();
        final.Should().BeEquivalentTo(createValue(iterations));
    }

    [Test]
    public void AccessExpirationAndRuntimeDurationChangesKeepTheirClockPath()
    {
        var clock = new FakeTimeProvider();
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .ExpireAfterAccess(TimeSpan.FromSeconds(10))
            .Build();
        cache.Put(1, 1);
        clock.Advance(TimeSpan.FromSeconds(9));
        cache.TryGet(1, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(9));
        cache.TryGet(1, out _).Should().BeTrue();
        cache.Policy.ExpireAfterAccess!.SetDuration(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet(1, out _).Should().BeFalse();
    }

    private sealed class RejectReadTimeProvider : TimeProvider
    {
        internal bool RejectTimestamps { get; set; }

        public override long GetTimestamp() =>
            RejectTimestamps
                ? throw new InvalidOperationException("A timeless resident read sampled time.")
                : 0;
    }

    private sealed class ReferenceValue(int version)
    {
        public int Version { get; } = version;
        public int Complement { get; } = ~version;
    }

    private readonly record struct LargeValue(long Version)
    {
        private long Complement { get; } = ~Version;
        private long Doubled { get; } = Version * 2;
        private long Tripled { get; } = Version * 3;

        internal bool IsConsistent =>
            Complement == ~Version && Doubled == Version * 2 && Tripled == Version * 3;
    }
}
