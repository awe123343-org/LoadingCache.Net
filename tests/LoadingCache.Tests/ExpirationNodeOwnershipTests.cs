using System.Collections;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class ExpirationNodeOwnershipTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [MatrixDataSource]
    public async Task OrphanedEntryCannotAttachItsNodeToTheWheelAfterLifecycleChange(
        [Matrix("bucket", "detached", "never-scheduled")] string nodeState,
        [Matrix("clear", "dispose", "dispose-async")] string operation
    )
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine(clock, "write");
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        object gate = ReadField(engine, "_gate")!;
        object oldWheel;
        object entry;
        object node;
        lock (gate)
        {
            object entries = ReadField(engine, "_entries")!;
            entry = ((IEnumerable)Invoke(entries, "Snapshot")!).Cast<object>().Single();
            oldWheel = ReadField(engine, "_expirationWheel")!;
            node = ReadField(entry, "TimerNode")!;
            ReadProperty<bool>(node, "IsScheduled").Should().BeTrue();

            if (nodeState != "bucket")
            {
                Invoke(oldWheel, "Deschedule", node).Should().Be(true);
                if (nodeState == "never-scheduled")
                {
                    Invoke(oldWheel, "Retire", node).Should().Be(true);
                    node = Activator.CreateInstance(
                        node.GetType(),
                        InstanceMembers,
                        binder: null,
                        args: [entry],
                        culture: null
                    )!;
                    entry.GetType().GetField("TimerNode", InstanceMembers)!.SetValue(entry, node);
                }
            }

            // Model an old Entry retained by a caller but absent from the Clear/Dispose
            // map snapshot. Keep its node so teardown and late scheduling are both tested.
            Invoke(entries, "TryRemoveExact", entry).Should().Be(true);
            ReadField(entry, "TimerNode").Should().BeSameAs(node);
            ReadProperty<bool>(node, "IsRetired").Should().BeFalse();
            ReadProperty<bool>(node, "IsScheduled").Should().Be(nodeState == "bucket");
        }

        switch (operation)
        {
            case "clear":
                cache.Clear();
                cache.Put(1, "new");
                cache.CleanUp();
                break;
            case "dispose":
                // Dispose at this phase to test orphan-node teardown; the using guard also cleans up failed assertions.
                // ReSharper disable once DisposeOnUsingVariable
                cache.Dispose();
                break;
            default:
                await engine.DisposeAsync();
                break;
        }

        lock (gate)
        {
            object newWheel = ReadField(engine, "_expirationWheel")!;
            newWheel.Should().NotBeSameAs(oldWheel);
            ReadProperty<int>(oldWheel, "Count").Should().Be(0);
            ReadField(entry, "TimerNode").Should().BeSameAs(node);
            ReadProperty<bool>(node, "IsRetired").Should().Be(nodeState == "bucket");
            ReadProperty<bool>(node, "IsScheduled").Should().BeFalse();
            ReadProperty<long>(node, "OwnerId").Should().Be(0);

            ulong now = ReadProperty<ulong>(newWheel, "CurrentTime");
            Invoke(engine, "ScheduleExpirationNodeLocked", entry, now);

            ReadField(entry, "TimerNode").Should().BeNull();
            ReadProperty<bool>(node, "IsRetired").Should().BeTrue();
            ReadProperty<bool>(node, "IsScheduled").Should().BeFalse();
            ReadProperty<long>(node, "OwnerId").Should().Be(0);
            ReadProperty<int>(newWheel, "Count").Should().Be(operation == "clear" ? 1 : 0);
            Invoke(oldWheel, "AssertInvariants");
        }
        engine.AssertInvariants();

        if (operation == "clear")
        {
            cache.Policy.TryGetQuietly(1, out string? value).Should().BeTrue();
            value.Should().Be("new");
            clock.Advance(Duration);
            cache.CleanUp();
            cache.EstimatedCount.Should().Be(0);
            engine.AssertInvariants();
        }
    }

    [Test]
    [MatrixDataSource]
    public void InfiniteThenFiniteDurationRecreatesAWorkingSchedule(
        [Matrix("write", "access", "variable")] string expiration,
        [Matrix(false, true)] bool scheduler
    )
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine(clock, expiration, scheduler);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "resident");
        SetDuration(cache, expiration, TimeSpan.MaxValue);
        clock.Advance(Duration + Duration);
        cache.CleanUp();
        cache.Policy.TryGetQuietly(1, out _).Should().BeTrue();
        engine.AssertInvariants();

        SetDuration(cache, expiration, TimeSpan.FromSeconds(30));
        TimeSpan remaining = expiration switch
        {
            "write" => cache.Policy.ExpireAfterWrite!.GetExpiresAfter(1)!.Value,
            "access" => cache.Policy.ExpireAfterAccess!.GetExpiresAfter(1)!.Value,
            _ => cache.Policy.VariableExpiration!.GetExpiresAfter(1)!.Value,
        };
        clock.Advance(remaining - TimeSpan.FromTicks(1));
        cache.Policy.TryGetQuietly(1, out _).Should().BeTrue();
        clock.Advance(TimeSpan.FromTicks(1));
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments("write")]
    [Arguments("access")]
    [Arguments("both")]
    [Arguments("variable")]
    public void ClearDropsAPartialAdvanceBeforeReinsertingTheSameKeys(string expiration)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine(clock, expiration);
        using var cache = new Cache<int, string>(engine);
        for (int key = 0; key < 256; key++)
        {
            cache.Put(key, "old");
        }
        clock.Advance(Duration);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(128);
        cache.Clear();
        engine.AssertInvariants();

        for (int key = 0; key < 256; key++)
        {
            cache.Put(key, "new");
        }
        clock.Advance(Duration / 2);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(256);
        cache.Policy.TryGetQuietly(0, out string? value).Should().BeTrue();
        value.Should().Be("new");
        engine.AssertInvariants();
        clock.Advance(Duration / 2);
        cache.CleanUp();
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    [MatrixDataSource]
    public async Task PausedExpiredReadCannotAffectAnEntryAfterLifecycleChange(
        [Matrix("write", "access", "variable")] string expiration,
        [Matrix("clear", "invalidate", "dispose")] string operation
    )
    {
        await using var cleanup = new BlockingTestHook(Watchdog);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine(
            clock,
            expiration,
            hooks: new LoadingCacheTestHooks { BeforeExpiredReadCleanup = cleanup.Invoke }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        clock.Advance(Duration);
        // Keep this captured cache: the test disposes it while the read is paused, then joins the read in finally.
        // ReSharper disable once AccessToDisposedClosure
        Task<bool> read = Task.Run(() => cache.TryGet(1, out _));
        try
        {
            await cleanup.Entered.WaitAsync(Watchdog);
            if (operation == "dispose")
            {
                // Dispose while the expired read is paused to test late cleanup; retain the using guard for failure cleanup.
                // ReSharper disable once DisposeOnUsingVariable
                cache.Dispose();
            }
            else
            {
                if (operation == "clear")
                {
                    cache.Clear();
                }
                else
                {
                    cache.Invalidate(1).Should().BeTrue();
                }
                cache.Put(1, "new");
            }
        }
        finally
        {
            cleanup.Release();
            await read.WaitAsync(Watchdog);
        }
        (await read).Should().BeFalse();
        cleanup.TimedOut.Should().BeFalse();
        engine.AssertInvariants();

        if (operation == "dispose")
        {
            // Keep this post-disposal call: the synchronous assertion verifies the cache rejects a late write.
            // ReSharper disable once AccessToDisposedClosure
            Action put = () => cache.Put(1, "late");
            put.Should().ThrowExactly<ObjectDisposedException>();
        }
        else
        {
            cache.Policy.TryGetQuietly(1, out string? value).Should().BeTrue();
            value.Should().Be("new");
            clock.Advance(Duration);
            cache.CleanUp();
            cache.EstimatedCount.Should().Be(0);
            engine.AssertInvariants();
        }
    }

    [Test]
    [Arguments("write")]
    [Arguments("access")]
    [Arguments("variable")]
    public async Task RefreshRecreatesTheNodeAfterItsExpiredValueWasDetached(string expiration)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = CreateEngine(clock, expiration);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => throw new InvalidOperationException("Unexpected cold load."),
            (_, _, _) => completion.Task
        );
        cache.Set(1, "old");
        Task<string> refresh = cache.RefreshAsync(1).AsTask();
        try
        {
            clock.Advance(Duration);
            cache.CleanUp();
            cache.TryGet(1, out _).Should().BeFalse();
            engine.AssertInvariants();
        }
        finally
        {
            completion.TrySetResult("refreshed");
            await refresh.WaitAsync(Watchdog);
        }
        (await refresh).Should().Be("refreshed");
        cache.Policy.TryGetQuietly(1, out string? value).Should().BeTrue();
        value.Should().Be("refreshed");
        engine.AssertInvariants();
        clock.Advance(Duration);
        cache.CleanUp();
        cache.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    private static CacheEngine<int, string> CreateEngine(
        FakeTimeProvider clock,
        string expiration,
        bool scheduler = false,
        LoadingCacheTestHooks? hooks = null
    ) =>
        new(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 512,
                MaxConcurrentLoads = 2,
                TimeProvider = clock,
                ExpireAfterWrite = expiration is "write" or "both" ? Duration : null,
                ExpireAfterAccess = expiration is "access" or "both" ? Duration : null,
                Expiry = expiration == "variable" ? new ConstantExpiry() : null,
                EnableExpirationScheduler = scheduler,
                TestHooks = hooks,
            }
        );

    private static void SetDuration(Cache<int, string> cache, string expiration, TimeSpan duration)
    {
        if (expiration == "variable")
        {
            cache.Policy.VariableExpiration!.SetExpiresAfter(1, duration).Should().BeTrue();
        }
        else
        {
            IFixedExpirationPolicy<int, string> policy =
                expiration == "write"
                    ? cache.Policy.ExpireAfterWrite!
                    : cache.Policy.ExpireAfterAccess!;
            policy.SetExpiresAfter(duration);
        }
    }

    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? ReadField(object instance, string name) =>
        instance.GetType().GetField(name, InstanceMembers)!.GetValue(instance);

    private static T ReadProperty<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name, InstanceMembers)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, InstanceMembers)!.Invoke(instance, arguments);

    private sealed class ConstantExpiry : IExpiry<int, string>
    {
        public TimeSpan ExpireAfterCreate(int key, string value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterUpdate(int key, string value, TimeSpan currentDuration) =>
            Duration;

        public TimeSpan ExpireAfterRead(int key, string value, TimeSpan currentDuration) =>
            currentDuration;
    }
}
