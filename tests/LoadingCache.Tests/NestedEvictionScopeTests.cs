using Microsoft.Extensions.Time.Testing;

namespace LoadingCache.Tests;

public sealed class NestedEvictionScopeTests
{
    [Test]
    public async Task NestedExpiredLookupCompletesItsNotificationBeforeStartingTheNewLoader()
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
        Func<int, Func<int, int>, int> getOrAdd = cache.GetOrAdd;
        int value = cache.GetOrAdd(
            1,
            _ =>
                getOrAdd(
                    2,
                    _ =>
                    {
                        notifiedBeforeNestedLoad = Volatile.Read(ref notifications) == 1;
                        return 2;
                    }
                )
        );
        await Assert.That(value).IsEqualTo(2);
        await Assert.That(notifiedBeforeNestedLoad).IsTrue();
        await Assert.That(notifications).IsEqualTo(1);
    }
}
