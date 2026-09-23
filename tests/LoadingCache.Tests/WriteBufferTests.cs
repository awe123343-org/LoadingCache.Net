using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class WriteBufferTests
{
    [Test]
    public async Task FullBufferRejectsWithoutDroppingTheNewEvent()
    {
        BoundedWriteBuffer<int> buffer = new(2);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsTrue();
        await Assert.That(buffer.TryEnqueue(3)).IsFalse();
        await Assert.That(buffer.TryDequeue(out int first)).IsTrue();
        await Assert.That(buffer.TryDequeue(out int second)).IsTrue();
        await Assert.That(buffer.TryDequeue(out _)).IsFalse();
        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(2);
        WriteBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.Capacity).IsEqualTo(2);
        await Assert.That(statistics.Queued).IsEqualTo(0);
        await Assert.That(statistics.Enqueued).IsEqualTo(2);
        await Assert.That(statistics.Dequeued).IsEqualTo(2);
        await Assert.That(statistics.Full).IsEqualTo(1);
        await Assert.That(statistics.Dropped).IsEqualTo(0);
        buffer.Dispose();
    }

    [Test]
    public async Task ClearAndDisposeAreTheOnlyExplicitDiscardBoundaries()
    {
        BoundedWriteBuffer<int> buffer = new(4);
        await Assert.That(buffer.TryEnqueue(1)).IsTrue();
        await Assert.That(buffer.TryEnqueue(2)).IsTrue();
        await Assert.That(buffer.Clear()).IsEqualTo(2);
        await Assert.That(buffer.TryDequeue(out _)).IsFalse();
        await Assert.That(buffer.TryEnqueue(3)).IsTrue();
        buffer.Dispose();
        await Assert.That(buffer.TryEnqueue(4)).IsFalse();
        WriteBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedClear).IsEqualTo(2);
        await Assert.That(statistics.DroppedShutdown).IsEqualTo(2);
        await Assert.That(statistics.Dropped).IsEqualTo(4);
        await Assert.That(statistics.IsDisposed).IsTrue();
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentProducersPreserveEveryAcceptedEvent()
    {
        const int producerCount = 8;
        const int eventsPerProducer = 64;
        const int total = producerCount * eventsPerProducer;
        BoundedWriteBuffer<int> buffer = new(total);
        ConcurrentBag<int> accepted = [];
        try
        {
            RunProducers(buffer, accepted, total);
            List<int> observed = [];
            while (buffer.TryDequeue(out int value))
            {
                observed.Add(value);
            }

            await Assert.That(observed.Count).IsEqualTo(accepted.Count);
            await Assert.That(observed).HasDistinctItems();
            await Assert.That(observed).IsEquivalentTo(accepted);
            await Assert.That(buffer.GetStatistics().Full).IsEqualTo(0);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public async Task DisposeRacingTheConsumerLeavesNoQueuedReferences()
    {
        BoundedWriteBuffer<int> buffer = new(512);
        try
        {
            for (int value = 0; value < buffer.Capacity; value++)
            {
                await Assert.That(buffer.TryEnqueue(value)).IsTrue();
            }

            Task consumer = StartConsumer(buffer);
            Task disposer = StartDisposer(buffer);
            await Task.WhenAll(consumer, disposer);
            WriteBufferStatistics statistics = buffer.GetStatistics();
            await Assert.That(statistics.IsDisposed).IsTrue();
            await Assert.That(statistics.Queued).IsEqualTo(0);
            await Assert
                .That((statistics.Dequeued + statistics.DroppedShutdown))
                .IsEqualTo(statistics.Enqueued);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public async Task DisposeReleasesQueuedEventReferences()
    {
        BoundedWriteBuffer<object> buffer = new(1);
        WeakReference reference = EnqueueObject(buffer);
        try
        {
            buffer.Dispose();
            ForceCollection(reference);
            await Assert.That(reference.IsAlive).IsFalse();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public async Task PolicyPublishesAndRemovesOnlyWhenItsWriteOwnerDrains()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 1
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        policy.OnPublish(first, 1);
        await Assert.That((first.Node) is null).IsTrue();
        await Assert.That(policy.HasPendingWrites).IsTrue();
        policy.FlushWrites();
        Assert.NotNull(first.Node);
        await Assert.That(policy.HasPendingWrites).IsFalse();
        policy.OnRemove(first);
        Assert.NotNull(first.Node);
        policy.FlushWrites();
        await Assert.That((first.Node) is null).IsTrue();
        await Assert.That(evicted).IsEmpty();
    }

    [Test]
    public async Task FullPolicyWriteBufferDrainsOlderEventsBeforeAcceptingNewerOnes()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 1
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        WriteBufferStatistics beforeFlush = policy.GetWriteBufferStatistics();
        await Assert.That(beforeFlush.Full).IsGreaterThan(0);
        await Assert.That(beforeFlush.Enqueued).IsEqualTo(2);
        await Assert.That(beforeFlush.Dequeued).IsEqualTo(1);
        await Assert.That((second.Node) is null).IsTrue();
        policy.FlushWrites();
        Assert.NotNull(second.Node);
        await Assert.That(policy.ResidentCount).IsEqualTo(2);
        WriteBufferStatistics afterFlush = policy.GetWriteBufferStatistics();
        await Assert.That(afterFlush.Queued).IsEqualTo(0);
        await Assert.That(afterFlush.Enqueued).IsEqualTo(afterFlush.Dequeued);
    }

    [Test]
    public async Task RemoveThenRepublishSameTokenRetainsTheNewerQueuedState()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 2
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken token = new(new object(), 1);
        policy.OnPublish(token, 1);
        policy.FlushWrites();
        policy.OnRemove(token);
        policy.OnPublish(token, 1);
        policy.FlushWrites();
        Assert.NotNull(token.Node);
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(1);
    }

    [Test]
    public async Task SupersededPublishDoesNotEvictAnUnrelatedResidentEntry()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 1,
            maximumResidentCount: 1,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken resident = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken invalidated = new(new object(), 2);
        policy.OnPublish(resident, 1);
        policy.FlushWrites();
        policy.OnPublish(invalidated, 1);
        policy.OnRemove(invalidated);
        policy.FlushWrites();
        Assert.NotNull(resident.Node);
        await Assert.That((invalidated.Node) is null).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(evicted).IsEmpty();
        policy.AssertInvariants();
    }

    [Test]
    public async Task WeightIncreasePastMaximumReportsTheExactEntryEviction()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        object entry = new();
        WindowTinyLfuEnginePolicy.EngineEntryToken token = new(entry, 1);
        policy.OnPublish(token, 1);
        policy.FlushWrites();
        policy.OnPublish(token, 3);
        policy.FlushWrites();
        await Assert.That((token.Node) is null).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), entry))
            .IsTrue();
        policy.AssertInvariants();
    }

    [Test]
    public async Task DeferredBatchPreservesLongWeightOverflowAccounting()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: long.MaxValue,
            maximumResidentCount: 2,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        object residentEntry = new();
        object rejectedEntry = new();
        WindowTinyLfuEnginePolicy.EngineEntryToken resident = new(residentEntry, 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken rejected = new(rejectedEntry, 2);
        policy.OnPublish(resident, long.MaxValue);
        policy.OnPublish(rejected, 1);
        policy.FlushWrites();
        Assert.NotNull(resident.Node);
        await Assert.That((rejected.Node) is null).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(long.MaxValue);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), rejectedEntry))
            .IsTrue();
        policy.AssertInvariants();
    }

    [Test]
    public async Task DeferredBatchKeepsTinyCountBoundForZeroWeightEntries()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 1,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        WindowTinyLfuEnginePolicy.EngineEntryToken third = new(new object(), 3);
        policy.OnPublish(first, 0);
        policy.OnPublish(second, 0);
        policy.OnPublish(third, 0);
        policy.FlushWrites();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        await Assert.That(policy.Snapshot(hottest: false, limit: 4)).HasSingleItem();
        policy.AssertInvariants();
    }

    [Test]
    public async Task OversizedPublicationIsRemovedAtSynchronousFlushBoundary()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        object entry = new();
        WindowTinyLfuEnginePolicy.EngineEntryToken token = new(entry, 1);
        policy.OnPublish(token, 3);
        policy.FlushWrites();
        await Assert.That((token.Node) is null).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        await Assert.That(policy.WeightedSize).IsEqualTo(0);
        await Assert
            .That(ReferenceEquals((await Assert.That(evicted).HasSingleItem()), entry))
            .IsTrue();
        policy.AssertInvariants();
    }

    [Test]
    public async Task NegativePublicationWeightIsRejectedBeforeQueueAdmission()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken token = new(new object(), 1);
        await Assert.That(() => policy.OnPublish(token, -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(token.PendingPolicyWrites).IsEqualTo(0);
        await Assert.That(policy.HasPendingWrites).IsFalse();
        await Assert.That(policy.GetWriteBufferStatistics().Enqueued).IsEqualTo(0);
        policy.AssertInvariants();
    }

    [Test]
    public async Task ResizeCommitsMaximumBeforeCallbackFailureAndClearUsesIt()
    {
        int callbackCount = 0;
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 4,
            maximumResidentCount: 4,
            _ =>
            {
                if (callbackCount++ == 0)
                {
                    throw new InvalidOperationException("injected resize callback failure");
                }
            },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        WindowTinyLfuEnginePolicy.EngineEntryToken third = new(new object(), 3);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.OnPublish(third, 1);
        policy.FlushWrites();
        await Assert
            .That(() => policy.SetMaximum(1, weighted: true))
            .Throws<InvalidOperationException>();
        await Assert.That(policy.Maximum).IsEqualTo(1);
        policy.Clear();
        await Assert.That(policy.Maximum).IsEqualTo(1);
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        policy.AssertInvariants();
    }

    [Test]
    public async Task NonWeightedResizeCommitsBothLimitsBeforeCallbackFailureAndClearUsesThem()
    {
        int callbackCount = 0;
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 4,
            maximumResidentCount: 4,
            _ =>
            {
                if (callbackCount++ == 0)
                {
                    throw new InvalidOperationException("injected resize callback failure");
                }
            },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        policy.OnPublish(first, 2);
        policy.OnPublish(second, 2);
        policy.FlushWrites();
        await Assert
            .That(() => policy.SetMaximum(1, weighted: false))
            .Throws<InvalidOperationException>();
        await Assert.That(policy.Maximum).IsEqualTo(1);
        policy.Clear();
        await Assert.That(policy.Maximum).IsEqualTo(1);
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        policy.AssertInvariants();
    }

    [Test]
    public async Task EvictionCallbackFailureStillCleansEveryRetiredNode()
    {
        List<object> evicted = [];
        int callbackCount = 0;
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 1,
            maximumResidentCount: 3,
            entry =>
            {
                evicted.Add(entry);
                if (callbackCount++ == 0)
                {
                    throw new InvalidOperationException("injected eviction callback failure");
                }
            },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        WindowTinyLfuEnginePolicy.EngineEntryToken third = new(new object(), 3);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.OnPublish(third, 1);
        await Assert.That(() => policy.FlushWrites()).Throws<InvalidOperationException>();
        await Assert.That(evicted.Count).IsEqualTo(2);
        policy.AssertInvariants();
        policy.FlushWrites();
        policy.AssertInvariants();
    }

    [Test]
    public async Task FullFallbackInstallsSequenceBeforeApplyingOlderEvent()
    {
        List<object> evicted = [];
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 1,
            maximumResidentCount: 1,
            evicted.Add,
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 1
        );
        object residentEntry = new();
        WindowTinyLfuEnginePolicy.EngineEntryToken resident = new(residentEntry, 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken invalidated = new(new object(), 2);
        policy.OnPublish(resident, 1);
        policy.FlushWrites();
        policy.OnPublish(invalidated, 1);
        policy.OnRemove(invalidated);
        policy.FlushWrites();
        Assert.NotNull(resident.Node);
        await Assert.That((invalidated.Node) is null).IsTrue();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        await Assert.That(evicted).IsEmpty();
        policy.AssertInvariants();
    }

    [Test]
    public async Task FlushWritesProcessesOnlyTheCapturedBatch()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 1,
            maximumResidentCount: 1,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 2
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.FlushWrites();
        await Assert.That(policy.HasPendingWrites).IsFalse();
        await Assert.That(policy.ResidentCount).IsEqualTo(1);
        policy.AssertInvariants();
    }

    [Test]
    public async Task SequenceRolloverResetsLiveNodeFencesBeforeIssuingNewEvents()
    {
        using WindowTinyLfuEnginePolicy policy = new(
            maximum: 2,
            maximumResidentCount: 2,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 4,
            writeBufferCapacity: 4
        );
        WindowTinyLfuEnginePolicy.EngineEntryToken first = new(new object(), 1);
        WindowTinyLfuEnginePolicy.EngineEntryToken second = new(new object(), 2);
        policy.OnPublish(first, 1);
        policy.FlushWrites();
        policy.SetWriteSequenceForTesting(long.MaxValue - 1);
        policy.OnPublish(first, 1);
        policy.OnPublish(second, 1);
        policy.FlushWrites();
        Assert.NotNull(first.Node);
        Assert.NotNull(second.Node);
        await Assert.That(policy.ResidentCount).IsEqualTo(2);
        policy.AssertInvariants();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EnqueueObject(BoundedWriteBuffer<object> buffer)
    {
        object value = new();
        WeakReference reference = new(value);
        if (!(buffer.TryEnqueue(value)))
            Assert.Fail("Expected buffer.TryEnqueue(value) to be true ().");
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 4 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static void RunProducers(
        BoundedWriteBuffer<int> buffer,
        ConcurrentBag<int> accepted,
        int total
    )
    {
        Parallel.For(
            0,
            total,
            value =>
            {
                if (!(buffer.TryEnqueue(value)))
                    Assert.Fail("Expected buffer.TryEnqueue(value) to be true ().");
                accepted.Add(value);
            }
        );
    }

    private static Task StartConsumer(BoundedWriteBuffer<int> buffer) =>
        Task.Run(() =>
        {
            while (buffer.TryDequeue(out _)) { }
        });

    private static Task StartDisposer(BoundedWriteBuffer<int> buffer) => Task.Run(buffer.Dispose);
}
