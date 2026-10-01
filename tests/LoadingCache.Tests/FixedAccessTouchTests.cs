using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class FixedAccessTouchTests
{
    [Test]
    public void HitsAtTheToleranceKeepTheRecordedAccessButLaterHitsAdvanceIt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan duration = TimeSpan.FromSeconds(1);
        using ICache<int, string> cache = CreateCache(clock, duration);
        cache.Put(1, "resident");

        // At this clock frequency, a one-second TTI permits nine TimeSpan ticks.
        TimeSpan tolerance = TimeSpan.FromTicks(9);
        clock.Advance(tolerance);
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
        TimeSpan recordedAge = Environment.Is64BitProcess ? tolerance : TimeSpan.Zero;
        cache.Policy.ExpireAfterAccess!.AgeOf(1).Should().Be(recordedAge);
        cache.Policy.ExpireAfterAccess.GetExpiresAfter(1).Should().Be(duration - recordedAge);

        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(1, out _).Should().BeTrue();
        cache.Policy.ExpireAfterAccess.AgeOf(1).Should().Be(TimeSpan.Zero);
        cache.Policy.ExpireAfterAccess.GetExpiresAfter(1).Should().Be(duration);
    }

    [Test]
    public void CoalescedAccessCannotExpireMoreThanTheToleranceEarly()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan duration = TimeSpan.FromSeconds(1);
        TimeSpan tolerance = TimeSpan.FromTicks(9);
        using ICache<int, string> cache = CreateCache(clock, duration);
        cache.Put(1, "resident");
        clock.Advance(tolerance);
        cache.TryGet(1, out _).Should().BeTrue();

        // Quiet reads observe the boundary without extending it themselves.
        clock.Advance(duration - tolerance - TimeSpan.FromTicks(1));
        cache.Policy.TryGetQuietly(1, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromTicks(1));
        cache.Policy.TryGetQuietly(1, out _).Should().Be(!Environment.Is64BitProcess);
        clock.Advance(tolerance);
        cache.Policy.TryGetQuietly(1, out _).Should().BeFalse();
    }

    [Test]
    public void LongDurationsCapAccessCoalescingAtOneMillisecond()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        TimeSpan duration = TimeSpan.FromDays(1);
        using ICache<int, string> cache = CreateCache(clock, duration);
        cache.Put(1, "resident");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.TryGet(1, out _).Should().BeTrue();
        cache
            .Policy.ExpireAfterAccess!.AgeOf(1)
            .Should()
            .Be(Environment.Is64BitProcess ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);

        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(1, out _).Should().BeTrue();
        cache.Policy.ExpireAfterAccess.AgeOf(1).Should().Be(TimeSpan.Zero);
        cache.Policy.ExpireAfterAccess.GetExpiresAfter(1).Should().Be(duration);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ShorteningDurationKeepsPreviouslyCoalescedAccessAge(bool useAlias)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using ICache<int, string> cache = CreateCache(clock, TimeSpan.FromDays(1));
        cache.Put(1, "resident");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.TryGet(1, out _).Should().BeTrue();

        IFixedExpirationPolicy<int, string> policy = cache.Policy.ExpireAfterAccess!;
        if (useAlias)
        {
            policy.SetExpiresAfter(TimeSpan.FromMilliseconds(1));
        }
        else
        {
            policy.SetDuration(TimeSpan.FromMilliseconds(1));
        }

        // The old one-millisecond error survives shrinking to a zero tolerance.
        // A 32-bit process uses exact locked touches and retains the last access.
        cache.TryGet(1, out _).Should().Be(!Environment.Is64BitProcess);
    }

    [Test]
    public async Task TaskLookupsKeepExactAccessTimestamps()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                TimeProvider = clock,
                ExpireAfterAccess = TimeSpan.FromDays(1),
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => throw new InvalidOperationException("Unexpected load.")
        );
        cache.Set(1, "resident");
        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGetTask(1, out Task<string>? value).Should().BeTrue();
        (await value!).Should().Be("resident");
        cache.Policy.ExpireAfterAccess!.AgeOf(1).Should().Be(TimeSpan.Zero);
    }

    [Test]
    [Arguments("refresh")]
    [Arguments("weak-keys")]
    [Arguments("weak-values")]
    [Arguments("owned")]
    public void OtherReadConfigurationsKeepExactAccessTimestamps(string configuration)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = new CacheEngine<object, object>(
            new CacheEngineOptions<object, object>
            {
                MaximumSize = 8,
                TimeProvider = clock,
                ExpireAfterAccess = TimeSpan.FromDays(1),
                RefreshAfterWrite = configuration == "refresh" ? TimeSpan.FromDays(1) : null,
                WeakKeys = configuration == "weak-keys",
                WeakValues = configuration == "weak-values",
                OnValueRetired = configuration == "owned" ? static _ => { } : null,
            }
        );
        using var cache = new Cache<object, object>(engine);
        object key = new();
        object value = new();
        cache.Put(key, value);
        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet(key, out object? observed).Should().BeTrue();
        observed.Should().BeSameAs(value);
        cache.Policy.ExpireAfterAccess!.AgeOf(key).Should().Be(TimeSpan.Zero);
        GC.KeepAlive(key);
        GC.KeepAlive(value);
    }

    private static ICache<int, string> CreateCache(FakeTimeProvider clock, TimeSpan duration) =>
        CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .TimeProvider(clock)
            .ExpireAfterAccess(duration)
            .Build();
}
