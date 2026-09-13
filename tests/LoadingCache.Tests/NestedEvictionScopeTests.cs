using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class NestedEvictionScopeTests
{
    [Test]
    public void NestedExpiredLookupCompletesItsNotificationBeforeStartingTheNewLoader()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int notifications = 0;
        bool notifiedBeforeNestedLoad = false;
        using var cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .ExpireAfterWrite(TimeSpan.FromSeconds(1))
            .TimeProvider(time)
            .EvictionListener(_ => Interlocked.Increment(ref notifications))
            .Build();
        cache.Put(2, 0);
        time.Advance(TimeSpan.FromSeconds(1));
        int value = cache.GetOrAdd(
            1,
            _ =>
                cache.GetOrAdd(
                    2,
                    _ =>
                    {
                        notifiedBeforeNestedLoad = Volatile.Read(ref notifications) == 1;
                        return 2;
                    }
                )
        );
        value.Should().Be(2);
        notifiedBeforeNestedLoad.Should().BeTrue();
        notifications.Should().Be(1);
    }
}
