namespace LoadingCache.Tests;

public sealed class ReadBeforeEvictionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublishedProbationAccessPrecedesWeightEviction(bool recordStatistics)
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
        await Assert.That(evicted).IsEmpty();
        // The first two residents are in probation. This accepted access must protect the
        // oldest before the following weight increase needs a victim. No new admission or
        // frequency comparison is involved, so the result does not depend on the sketch seed.
        policy.OnAccess(hot);
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(1);
        policy.OnPublish(growing, 2);
        policy.CleanUp();
        await Assert
            .That(evicted)
            .IsEquivalentTo([growing.Entry], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert
            .That(policy.Snapshot(hottest: true, limit: 3))
            .IsEquivalentTo([hot.Entry, window.Entry]);
        await Assert.That(policy.WeightedSize).IsEqualTo(2);
        await Assert
            .That(policy.GetReadBufferStatistics().Dequeued)
            .IsEqualTo(recordStatistics ? 1 : 0);
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(policy.GetWriteBufferStatistics().Queued).IsEqualTo(0);
        policy.AssertInvariants();
    }
}
