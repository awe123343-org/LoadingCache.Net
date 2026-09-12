using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

public sealed class ExpirationEngineTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    public async Task VariableExpiryUsesCreateUpdateAndReadCallbacks()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var expiry = new TestExpiry
        {
            CreateDuration = TimeSpan.FromSeconds(10),
            UpdateDuration = TimeSpan.FromSeconds(20),
            ReadDuration = TimeSpan.FromSeconds(30),
        };
        await using IAsyncLoadingCache<int, string> cache = CreateVariableCache(clock, expiry);

        (await cache.GetAsync(1)).Should().Be("1");
        expiry.CreateCalls.Should().Be(1);
        cache.Policy.VariableExpiration!.GetExpiresAfter(1).Should().Be(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet(1, out string? readValue).Should().BeTrue();
        readValue.Should().Be("1");
        expiry.ReadCalls.Should().Be(1);
        cache.Policy.VariableExpiration.GetExpiresAfter(1).Should().Be(TimeSpan.FromSeconds(30));

        cache.Set(1, "updated");
        expiry.UpdateCalls.Should().Be(1);
        cache.Policy.VariableExpiration.GetExpiresAfter(1).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Test]
    public void VariableAndFixedExpirationAreMutuallyExclusive()
    {
        Action action = () =>
            CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(2)
                .ExpireAfterWrite(TimeSpan.FromSeconds(1))
                .ExpireAfter(new TestExpiry())
                .Build();

        action.Should().ThrowExactly<InvalidOperationException>();
    }

    [Test]
    public async Task VariableZeroAndNegativeDurationsExpireImmediatelyWhileMaxValueDoesNot()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using IAsyncLoadingCache<int, string> cache = CreateVariableCache(
            clock,
            new TestExpiry { CreateDuration = TimeSpan.MaxValue }
        );

        (await cache.GetAsync(1)).Should().Be("1");
        cache.Policy.VariableExpiration!.SetExpiresAfter(1, TimeSpan.Zero).Should().BeTrue();
        cache.TryGet(1, out _).Should().BeFalse();

        cache.Put(2, "negative", TimeSpan.FromTicks(-1));
        cache.TryGet(2, out _).Should().BeFalse();

        cache.Put(3, "long", TimeSpan.MaxValue);
        clock.Advance(TimeSpan.FromDays(365));
        cache.TryGet(3, out string? value).Should().BeTrue();
        value.Should().Be("long");
    }

    [Test]
    public async Task VariableReadCallbackRunsOutsideEntryLock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        IAsyncLoadingCache<int, string>? cache = null;
        var expiry = new TestExpiry
        {
            CreateDuration = TimeSpan.FromMinutes(1),
            ReadDuration = TimeSpan.FromMinutes(1),
            OnRead = () =>
            {
                Task probe = Task.Run(() =>
                {
                    cache!.Policy.VariableExpiration!.GetExpiresAfter(1);
                });
                probe.Wait(Watchdog).Should().BeTrue();
            },
        };
        cache = CreateVariableCache(clock, expiry);

        try
        {
            (await cache.GetAsync(1)).Should().Be("1");
            cache.TryGet(1, out _).Should().BeTrue();
        }
        finally
        {
            await cache.DisposeAsync();
        }
    }

    [Test]
    public async Task StaleReadCallbackCannotOverwriteSameTimestampRuntimeUpdate()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using BlockingTestHook callback = new(Watchdog);
        var expiry = new TestExpiry
        {
            CreateDuration = TimeSpan.FromMinutes(1),
            ReadDuration = TimeSpan.FromSeconds(1),
            OnRead = callback.Invoke,
        };
        await using IAsyncLoadingCache<int, string> cache = CreateVariableCache(clock, expiry);
        Task<bool>? read = null;
        try
        {
            (await cache.GetAsync(1)).Should().Be("1");
            read = Task.Run(() => cache.TryGet(1, out _));
            await callback.Entered.WaitAsync(Watchdog, CancellationToken.None);

            cache
                .Policy.VariableExpiration!.SetExpiresAfter(1, TimeSpan.FromSeconds(7))
                .Should()
                .BeTrue();
            callback.Release();
            (await read.WaitAsync(Watchdog, CancellationToken.None)).Should().BeTrue();
            cache.Policy.VariableExpiration.GetExpiresAfter(1).Should().Be(TimeSpan.FromSeconds(7));
        }
        finally
        {
            callback.Release();
            if (read is not null)
            {
                await read.WaitAsync(Watchdog, CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task SameTimestampReadCallbacksCommitInCompletionOrder()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var bothEntered = new CountdownEvent(2);
        using var releaseFirst = new ManualResetEventSlim(false);
        using var releaseSecond = new ManualResetEventSlim(false);
        var committed = NewSignal();
        var expiry = new TestExpiry
        {
            CreateDuration = TimeSpan.FromMinutes(1),
            ReadDurationFactory = call =>
            {
                bothEntered.Signal();
                ManualResetEventSlim release = call == 1 ? releaseFirst : releaseSecond;
                release.Wait(Watchdog).Should().BeTrue();
                return call == 1 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(7);
            },
        };
        var hooks = new LoadingCacheTestHooks
        {
            AfterReadExpiryUpdate = () => committed.TrySetResult(true),
        };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 4,
                Expiry = expiry,
                TimeProvider = clock,
                TestHooks = hooks,
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            (key, _) =>
                Task.FromResult(key.ToString(System.Globalization.CultureInfo.InvariantCulture))
        );

        (await cache.GetAsync(1)).Should().Be("1");
        Task<bool> first = Task.Run(() => cache.TryGet(1, out _));
        Task<bool> second = Task.Run(() => cache.TryGet(1, out _));
        try
        {
            bothEntered.Wait(Watchdog).Should().BeTrue();

            // The second callback is released first.  The engine hook proves that
            // its revision was committed before the first callback is released.
            releaseSecond.Set();
            await committed.Task.WaitAsync(Watchdog);
            releaseFirst.Set();

            (await Task.WhenAll(first, second).WaitAsync(Watchdog, CancellationToken.None))
                .Should()
                .OnlyContain(result => result);
            cache
                .Policy.VariableExpiration!.GetExpiresAfter(1)
                .Should()
                .Be(TimeSpan.FromSeconds(7));
        }
        finally
        {
            releaseSecond.Set();
            releaseFirst.Set();
            await Task.WhenAll(first, second).WaitAsync(Watchdog, CancellationToken.None);
        }
    }

    [Test]
    public void ExpirationWheelKeepsExtendedAccessEntriesBoundedPerPass()
    {
        const int entryCount = 256;
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(entryCount + 1)
            .MaxConcurrentLoads(4)
            .TimeProvider(clock)
            .ExpireAfterAccess(TimeSpan.FromSeconds(1))
            .Build();

        for (int key = 0; key < entryCount; key++)
        {
            cache.Put(key, key.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (int key = 0; key < entryCount; key++)
        {
            cache.TryGet(key, out _).Should().BeTrue();
        }

        // Every node is due at its original deadline, but each entry has a
        // later access deadline.  The bounded wheel pass must reschedule each
        // exact node without recursively advancing itself.
        clock.Advance(TimeSpan.FromMilliseconds(500));
        cache.CleanUp();
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(entryCount);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        cache.CleanUp();
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public void FixedPolicyExposesRuntimeDurationAndAge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .ExpireAfterWrite(TimeSpan.FromSeconds(10))
            .Build();

        cache.Put(1, "value");
        cache.Policy.ExpireAfterWrite!.GetExpiresAfter(1).Should().Be(TimeSpan.FromSeconds(10));
        cache.Policy.ExpireAfterWrite.AgeOf(1).Should().Be(TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(2));
        cache.Policy.ExpireAfterWrite.AgeOf(1).Should().Be(TimeSpan.FromSeconds(2));
        cache.Policy.ExpireAfterWrite.SetDuration(TimeSpan.FromSeconds(1));
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Test]
    public void PromptExpirationSchedulerUsesConfiguredTimeProvider()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .ExpireAfterWrite(TimeSpan.FromSeconds(5))
            .EnableExpirationScheduler()
            .Build();

        cache.Put(1, "value");
        cache.EstimatedCount.Should().Be(1);
        clock.Advance(TimeSpan.FromSeconds(5));
        cache.EstimatedCount.Should().Be(0);
        cache.TryGet(1, out _).Should().BeFalse();
    }

    [Test]
    public async Task ExpirationTimerArmCannotOverwriteADeadlineInsertedWhileArmIsPaused()
    {
        using var timeProvider = new RecordingTimerProvider();
        var armEntered = NewSignal();
        using var releaseArm = new ManualResetEventSlim(false);
        int armCount = 0;
        var hooks = new LoadingCacheTestHooks
        {
            BeforeExpirationTimerArm = () =>
            {
                if (Interlocked.Increment(ref armCount) != 2)
                {
                    return;
                }

                armEntered.TrySetResult(true);
                releaseArm.Wait(Watchdog).Should().BeTrue();
            },
        };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 4,
                ExpireAfterWrite = TimeSpan.FromSeconds(10),
                EnableExpirationScheduler = true,
                TimeProvider = timeProvider,
                TestHooks = hooks,
            }
        );
        using var cache = new Cache<int, string>(engine);

        cache.Put(1, "value");
        Task staleArm = Task.Run(cache.CleanUp);
        Task? shorterArm = null;
        try
        {
            await armEntered.Task.WaitAsync(Watchdog, CancellationToken.None);
            shorterArm = Task.Run(() =>
                cache.Policy.ExpireAfterWrite!.SetDuration(TimeSpan.FromSeconds(1))
            );
            SpinWait
                .SpinUntil(
                    () =>
                        cache.Policy.ExpireAfterWrite!.GetExpiresAfter(1)
                        == TimeSpan.FromSeconds(1),
                    Watchdog
                )
                .Should()
                .BeTrue();

            releaseArm.Set();
            await Task.WhenAll(staleArm, shorterArm).WaitAsync(Watchdog, CancellationToken.None);
            // The wheel arms at the beginning of the containing bucket (currently
            // 960 ms for a one-second deadline); it must not restore the stale
            // ten-second delay computed by the paused request.
            timeProvider.LastDueTime.Should().BeLessThan(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseArm.Set();
            await Task.WhenAll(staleArm, shorterArm ?? Task.CompletedTask)
                .WaitAsync(Watchdog, CancellationToken.None);
        }
    }

    [Test]
    public async Task PromptExpirationSchedulerRemovesVariableEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .ExpireAfter(new TestExpiry { CreateDuration = TimeSpan.FromSeconds(3) })
            .EnableExpirationScheduler()
            .BuildAsyncLoading(
                (key, _) =>
                    Task.FromResult(key.ToString(System.Globalization.CultureInfo.InvariantCulture))
            );

        (await cache.GetAsync(1)).Should().Be("1");
        clock.Advance(TimeSpan.FromSeconds(3));
        cache.EstimatedCount.Should().Be(0);
    }

    [Test]
    public async Task ExpirationCallbackFailureDoesNotCorruptTheEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var expiry = new TestExpiry
        {
            CreateDuration = TimeSpan.FromMinutes(1),
            ReadDuration = TimeSpan.FromMinutes(1),
            ThrowOnRead = true,
        };
        await using IAsyncLoadingCache<int, string> cache = CreateVariableCache(clock, expiry);

        (await cache.GetAsync(1)).Should().Be("1");
        Exception? failure = null;
        try
        {
            cache.TryGet(1, out _);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        failure.Should().BeOfType<InvalidOperationException>();

        expiry.ThrowOnRead = false;
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("1");
    }

    private static IAsyncLoadingCache<int, string> CreateVariableCache(
        TimeProvider clock,
        TestExpiry expiry
    ) =>
        CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(4)
            .TimeProvider(clock)
            .ExpireAfter(expiry)
            .BuildAsyncLoading(
                (key, _) =>
                    Task.FromResult(key.ToString(System.Globalization.CultureInfo.InvariantCulture))
            );

    private sealed class TestExpiry : IExpiry<int, string>
    {
        internal TimeSpan CreateDuration { get; init; } = TimeSpan.FromMinutes(1);
        internal TimeSpan UpdateDuration { get; init; } = TimeSpan.FromMinutes(1);
        internal TimeSpan ReadDuration { get; init; } = TimeSpan.FromMinutes(1);
        internal Func<int, TimeSpan>? ReadDurationFactory { get; init; }
        internal bool ThrowOnRead { get; set; }
        internal Action? OnRead { get; init; }
        internal int CreateCalls;
        internal int UpdateCalls;
        internal int ReadCalls;

        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration)
        {
            Interlocked.Increment(ref CreateCalls);
            return CreateDuration;
        }

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration)
        {
            Interlocked.Increment(ref UpdateCalls);
            return UpdateDuration;
        }

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration)
        {
            int call = Interlocked.Increment(ref ReadCalls);
            OnRead?.Invoke();
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("read callback failed");
            }

            return ReadDurationFactory?.Invoke(call) ?? ReadDuration;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RecordingTimerProvider : TimeProvider, IDisposable
    {
        private readonly RecordingTimer _timer = new();

        public override long TimestampFrequency => global::System.Diagnostics.Stopwatch.Frequency;

        public override long GetTimestamp() => 0;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        ) => _timer;

        internal TimeSpan LastDueTime => _timer.LastDueTime;

        public void Dispose() => _timer.Dispose();

        private sealed class RecordingTimer : ITimer
        {
            private readonly object _sync = new();
            private TimeSpan _lastDueTime = Timeout.InfiniteTimeSpan;

            internal TimeSpan LastDueTime
            {
                get
                {
                    lock (_sync)
                    {
                        return _lastDueTime;
                    }
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_sync)
                {
                    _lastDueTime = dueTime;
                }

                return true;
            }

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
