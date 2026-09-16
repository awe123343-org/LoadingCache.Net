using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ReadBeforeEvictionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void PublishedProbationAccessPrecedesWeightEviction(bool recordStatistics)
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 3,
            maximumResidentCount: null,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            recordReadStatistics: recordStatistics
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken hot = new("hot", hash: 0);
        WindowTinyLfuEnginePolicy.EngineEntryToken growing = new("growing", hash: 0);
        WindowTinyLfuEnginePolicy.EngineEntryToken window = new("window", hash: 0);
        policy.OnPublish(hot, 1);
        policy.OnPublish(growing, 1);
        policy.OnPublish(window, 1);
        policy.FlushWrites();
        evicted.Should().BeEmpty();

        // The first two residents are in probation. This accepted access must protect the
        // oldest before the following weight increase needs a victim. No new admission or
        // frequency comparison is involved, so the result does not depend on the sketch seed.
        policy.OnAccess(hot);
        policy.GetReadBufferStatistics().Queued.Should().Be(1);
        policy.OnPublish(growing, 2);
        policy.CleanUp();

        evicted.Should().Equal(growing.Entry);
        policy.Snapshot(hottest: true, limit: 3).Should().BeEquivalentTo([hot.Entry, window.Entry]);
        policy.WeightedSize.Should().Be(2);
        policy.GetReadBufferStatistics().Dequeued.Should().Be(recordStatistics ? 1 : 0);
        policy.GetReadBufferStatistics().Queued.Should().Be(0);
        policy.GetWriteBufferStatistics().Queued.Should().Be(0);
        policy.AssertInvariants();
    }
}
