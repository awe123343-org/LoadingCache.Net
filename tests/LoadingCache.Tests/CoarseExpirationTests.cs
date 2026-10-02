using System.Diagnostics;
using FluentAssertions;
using LoadingCache.Expiration;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class CoarseExpirationTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [MatrixDataSource]
    public void EveryBuilderRejectsACustomProviderRegardlessOfConfigurationOrder(
        [Matrix("manual", "loading", "async", "async-loading")] string personality,
        [Matrix(false, true)] bool providerFirst
    )
    {
        var builder = CacheBuilder.Create<string, string>().MaximumSize(8);
        var clock = new FakeTimeProvider();
        if (providerFirst)
        {
            builder.TimeProvider(clock).EnableCoarseExpirationChecks();
        }
        else
        {
            builder.EnableCoarseExpirationChecks().TimeProvider(clock);
        }

        Action build = () =>
        {
            switch (personality)
            {
                case "manual":
                    builder.Build();
                    break;
                case "loading":
                    builder.BuildLoading(static key => key);
                    break;
                case "async":
                    builder.BuildAsync();
                    break;
                default:
                    builder.BuildAsyncLoading(static (key, _) => Task.FromResult(key));
                    break;
            }
        };
        build
            .Should()
            .ThrowExactly<InvalidOperationException>()
            .WithMessage("*EnableCoarseExpirationChecks*TimeProvider.System*custom*");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void FrozenClockDelaysTtlDetectionOnlyWhenExplicitlyEnabled(bool enabled)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        using var cache = new Cache<string, string>(CreateEngine(clock, coarse, enabled: enabled));
        cache.Put("key", "value");
        clock.Advance(Duration + Duration);

        cache.TryGet("key", out string? value).Should().Be(enabled);
        if (enabled)
        {
            value.Should().Be("value");
            coarse.Advance(clock.GetTimestamp());
            cache.TryGet("key", out _).Should().BeFalse();
        }
    }

    [Test]
    [Arguments("cleanup")]
    [Arguments("quiet")]
    [Arguments("scheduler")]
    public void ExactChecksAndCleanupRemainIndependentOfAFrozenTicker(string operation)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        using var cache = new Cache<string, string>(
            CreateEngine(clock, coarse, scheduler: operation == "scheduler")
        );
        cache.Put("key", "value");
        clock.Advance(Duration);
        if (operation == "cleanup")
        {
            cache.TryGet("key", out _).Should().BeTrue();
            cache.CleanUp();
        }
        else if (operation == "quiet")
        {
            cache.TryGet("key", out _).Should().BeTrue();
            cache.Policy.TryGetQuietly("key", out _).Should().BeFalse();
            // A quiet miss does not physically remove an expired entry.
            cache.EstimatedCount.Should().Be(1);
            cache.CleanUp();
        }
        cache.EstimatedCount.Should().Be(0);
        cache.TryGet("key", out _).Should().BeFalse();
    }

    [Test]
    public async Task TaskLookupUsesPreciseTimeEvenWhenValueLookupUsesCoarseTime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        await using var cache = new AsyncLoadingCache<string, string>(
            CreateEngine(clock, coarse),
            static (_, _) => throw new InvalidOperationException("Unexpected load.")
        );
        cache.Set("key", "value");
        clock.Advance(Duration);
        cache.TryGet("key", out _).Should().BeTrue();
        cache.TryGetTask("key", out _).Should().BeFalse();
    }

    [Test]
    [Arguments("access")]
    [Arguments("both")]
    public void TtiKeepsPreciseTimeAndTheExistingCoalescingBound(string configuration)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        TimeSpan duration = TimeSpan.FromDays(1);
        using var cache = new Cache<string, string>(
            CreateEngine(clock, coarse, configuration, duration: duration)
        );
        cache.Put("key", "value");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.TryGet("key", out _).Should().BeTrue();
        cache
            .Policy.ExpireAfterAccess!.AgeOf("key")
            .Should()
            .Be(Environment.Is64BitProcess ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);
        clock.Advance(TimeSpan.FromTicks(1));
        cache.TryGet("key", out _).Should().BeTrue();
        cache.Policy.ExpireAfterAccess.AgeOf("key").Should().Be(TimeSpan.Zero);
        clock.Advance(duration);
        cache.TryGet("key", out _).Should().BeFalse();
    }

    [Test]
    [Arguments("refresh")]
    [Arguments("weak-keys")]
    [Arguments("weak-values")]
    [Arguments("owned")]
    [Arguments("variable")]
    public void OtherConfigurationsKeepPreciseExpiration(string configuration)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        using var cache = new Cache<string, string>(CreateEngine(clock, coarse, configuration));
        cache.Put("key", "value");
        clock.Advance(Duration);
        cache.TryGet("key", out _).Should().BeFalse();
    }

    [Test]
    public void WritesAndPolicyAgesRemainPreciseWhenTheTickerPredatesTheWrite()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        using var cache = new Cache<string, string>(CreateEngine(clock, coarse));
        clock.Advance(Duration);
        cache.Put("key", "first");
        cache.Policy.ExpireAfterWrite!.AgeOf("key").Should().Be(TimeSpan.Zero);
        cache.TryGet("key", out string? value).Should().BeTrue();
        value.Should().Be("first");
        clock.Advance(TimeSpan.FromSeconds(2));
        cache.Policy.ExpireAfterWrite.GetExpiresAfter("key").Should().Be(TimeSpan.FromSeconds(8));
        cache.Put("key", "replacement");
        cache.Policy.ExpireAfterWrite.AgeOf("key").Should().Be(TimeSpan.Zero);
        clock.Advance(Duration);
        cache.TryGet("key", out value).Should().BeTrue();
        value.Should().Be("replacement");
        cache.CleanUp();
        cache.TryGet("key", out _).Should().BeFalse();
    }

    [Test]
    public async Task ExplicitRefreshUsesItsPreciseCompletionTimestamp()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        await using var cache = new AsyncLoadingCache<string, string>(
            CreateEngine(clock, coarse),
            static (_, _) => Task.FromResult("refreshed")
        );
        cache.Set("key", "old");
        clock.Advance(TimeSpan.FromSeconds(3));
        (await cache.RefreshAsync("key")).Should().Be("refreshed");
        cache.Policy.ExpireAfterWrite!.AgeOf("key").Should().Be(TimeSpan.Zero);
        clock.Advance(Duration - TimeSpan.FromTicks(1));
        cache.Policy.TryGetQuietly("key", out string? value).Should().BeTrue();
        value.Should().Be("refreshed");
        clock.Advance(TimeSpan.FromTicks(1));
        cache.Policy.TryGetQuietly("key", out _).Should().BeFalse();
    }

    [Test]
    public async Task FrozenTickerDoesNotDelayLoadTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var coarse = new CoarseExpirationClock(clock.GetTimestamp());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = new AsyncLoadingCache<string, string>(
            CreateEngine(clock, coarse, timeout: TimeSpan.FromSeconds(1)),
            (_, _) =>
            {
                entered.TrySetResult();
                return completion.Task;
            }
        );
        Task<string> load = cache.GetAsync("key").AsTask();
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            clock.Advance(TimeSpan.FromSeconds(1));
            // Keep the watchdog outside the exception assertion: its TimeoutException
            // must not be mistaken for the cache's own load timeout.
            await Task.WhenAny(load).WaitAsync(Watchdog);
            await FluentActions.Awaiting(() => load).Should().ThrowExactlyAsync<TimeoutException>();
            cache.TryGet("key", out _).Should().BeFalse();
        }
        finally
        {
            completion.TrySetResult("late");
        }
    }

    [Test]
    public void SharedTickerStaysMonotonicAndContinuesAfterOneCacheIsDisposed()
    {
        using ICache<int, string> first = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .ExpireAfterWrite(TimeSpan.FromDays(1))
            .EnableCoarseExpirationChecks()
            .Build();
        using ICache<int, string> second = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .TimeProvider(TimeProvider.System)
            .ExpireAfterWrite(TimeSpan.FromDays(1))
            .EnableCoarseExpirationChecks()
            .Build();
        second.Put(1, "resident");
        CoarseExpirationClock clock = CoarseExpirationClock.Shared;
        long previous = clock.GetTimestamp();
        first.Dispose();
        SpinWait.SpinUntil(() => clock.GetTimestamp() > previous, Watchdog).Should().BeTrue();
        for (int sample = 0; sample < 1_000; sample++)
        {
            long timestamp = clock.GetTimestamp();
            timestamp.Should().BeGreaterThanOrEqualTo(previous);
            timestamp.Should().BeLessThanOrEqualTo(Stopwatch.GetTimestamp());
            previous = timestamp;
        }
        second.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("resident");
    }

    private static CacheEngine<string, string> CreateEngine(
        FakeTimeProvider clock,
        CoarseExpirationClock coarse,
        string configuration = "write",
        bool enabled = true,
        bool scheduler = false,
        TimeSpan? duration = null,
        TimeSpan? timeout = null
    ) =>
        new(
            new CacheEngineOptions<string, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = configuration is "access" or "variable"
                    ? null
                    : duration ?? Duration,
                ExpireAfterAccess = configuration is "access" or "both"
                    ? duration ?? Duration
                    : null,
                RefreshAfterWrite = configuration == "refresh" ? Duration / 2 : null,
                Expiry = configuration == "variable" ? new ConstantExpiry() : null,
                WeakKeys = configuration == "weak-keys",
                WeakValues = configuration == "weak-values",
                OnValueRetired = configuration == "owned" ? static _ => { } : null,
                LoadTimeout = timeout,
                EnableCoarseExpirationChecks = enabled,
                EnableExpirationScheduler = scheduler,
                TestHooks = new LoadingCacheTestHooks { CoarseExpirationClock = coarse },
            }
        );

    private sealed class ConstantExpiry : IExpiry<string, string>
    {
        public TimeSpan ExpireAfterCreate(string key, string value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterUpdate(string key, string value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterRead(string key, string value, TimeSpan currentDuration) =>
            currentDuration;
    }
}
