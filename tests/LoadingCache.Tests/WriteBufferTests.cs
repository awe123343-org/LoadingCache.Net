using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class WriteBufferTests
{
    [Test]
    public void FullBufferRejectsWithoutDroppingTheNewEvent()
    {
        BoundedWriteBuffer<int> buffer = new(2);

        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeTrue();
        buffer.TryEnqueue(3).Should().BeFalse();

        buffer.TryDequeue(out int first).Should().BeTrue();
        buffer.TryDequeue(out int second).Should().BeTrue();
        buffer.TryDequeue(out _).Should().BeFalse();
        first.Should().Be(1);
        second.Should().Be(2);

        WriteBufferStatistics statistics = buffer.GetStatistics();
        statistics.Capacity.Should().Be(2);
        statistics.Queued.Should().Be(0);
        statistics.Enqueued.Should().Be(2);
        statistics.Dequeued.Should().Be(2);
        statistics.Full.Should().Be(1);
        statistics.Dropped.Should().Be(0);
        buffer.Dispose();
    }

    [Test]
    public void ClearAndDisposeAreTheOnlyExplicitDiscardBoundaries()
    {
        BoundedWriteBuffer<int> buffer = new(4);

        buffer.TryEnqueue(1).Should().BeTrue();
        buffer.TryEnqueue(2).Should().BeTrue();
        buffer.Clear().Should().Be(2);
        buffer.TryDequeue(out _).Should().BeFalse();

        buffer.TryEnqueue(3).Should().BeTrue();
        buffer.Dispose();
        buffer.TryEnqueue(4).Should().BeFalse();

        WriteBufferStatistics statistics = buffer.GetStatistics();
        statistics.DroppedClear.Should().Be(2);
        statistics.DroppedShutdown.Should().Be(2);
        statistics.Dropped.Should().Be(4);
        statistics.IsDisposed.Should().BeTrue();
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public void ConcurrentProducersPreserveEveryAcceptedEvent()
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

            observed.Should().HaveSameCount(accepted);
            observed.Should().OnlyHaveUniqueItems();
            observed.Should().BeEquivalentTo(accepted);
            buffer.GetStatistics().Full.Should().Be(0);
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
                buffer.TryEnqueue(value).Should().BeTrue();
            }

            Task consumer = StartConsumer(buffer);
            Task disposer = StartDisposer(buffer);

            await Task.WhenAll(consumer, disposer);

            WriteBufferStatistics statistics = buffer.GetStatistics();
            statistics.IsDisposed.Should().BeTrue();
            statistics.Queued.Should().Be(0);
            (statistics.Dequeued + statistics.DroppedShutdown).Should().Be(statistics.Enqueued);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public void DisposeReleasesQueuedEventReferences()
    {
        BoundedWriteBuffer<object> buffer = new(1);
        WeakReference reference = EnqueueObject(buffer);

        try
        {
            buffer.Dispose();
            ForceCollection(reference);

            reference.IsAlive.Should().BeFalse();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public void PolicyPublishesAndRemovesOnlyWhenItsWriteOwnerDrains()
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
        first.Node.Should().BeNull();
        policy.HasPendingWrites.Should().BeTrue();

        policy.FlushWrites();
        first.Node.Should().NotBeNull();
        policy.HasPendingWrites.Should().BeFalse();

        policy.OnRemove(first);
        first.Node.Should().NotBeNull();
        policy.FlushWrites();
        first.Node.Should().BeNull();
        evicted.Should().BeEmpty();
    }

    [Test]
    public void FullPolicyWriteBufferDrainsOlderEventsBeforeAcceptingNewerOnes()
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
        beforeFlush.Full.Should().BeGreaterThan(0);
        beforeFlush.Enqueued.Should().Be(2);
        beforeFlush.Dequeued.Should().Be(1);
        second.Node.Should().BeNull();

        policy.FlushWrites();

        second.Node.Should().NotBeNull();
        policy.ResidentCount.Should().Be(2);
        WriteBufferStatistics afterFlush = policy.GetWriteBufferStatistics();
        afterFlush.Queued.Should().Be(0);
        afterFlush.Enqueued.Should().Be(afterFlush.Dequeued);
    }

    [Test]
    public void RemoveThenRepublishSameTokenRetainsTheNewerQueuedState()
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

        token.Node.Should().NotBeNull();
        policy.ResidentCount.Should().Be(1);
        policy.WeightedSize.Should().Be(1);
    }

    [Test]
    public void SupersededPublishDoesNotEvictAnUnrelatedResidentEntry()
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

        resident.Node.Should().NotBeNull();
        invalidated.Node.Should().BeNull();
        policy.ResidentCount.Should().Be(1);
        evicted.Should().BeEmpty();
        policy.AssertInvariants();
    }

    [Test]
    public void WeightIncreasePastMaximumReportsTheExactEntryEviction()
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

        token.Node.Should().BeNull();
        policy.ResidentCount.Should().Be(0);
        policy.WeightedSize.Should().Be(0);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(entry);
        policy.AssertInvariants();
    }

    [Test]
    public void DeferredBatchPreservesLongWeightOverflowAccounting()
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

        resident.Node.Should().NotBeNull();
        rejected.Node.Should().BeNull();
        policy.ResidentCount.Should().Be(1);
        policy.WeightedSize.Should().Be(long.MaxValue);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(rejectedEntry);
        policy.AssertInvariants();
    }

    [Test]
    public void DeferredBatchKeepsTinyCountBoundForZeroWeightEntries()
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

        policy.ResidentCount.Should().Be(1);
        policy.WeightedSize.Should().Be(0);
        policy.Snapshot(hottest: false, limit: 4).Should().ContainSingle();
        policy.AssertInvariants();
    }

    [Test]
    public void OversizedPublicationIsRemovedAtSynchronousFlushBoundary()
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

        token.Node.Should().BeNull();
        policy.ResidentCount.Should().Be(0);
        policy.WeightedSize.Should().Be(0);
        evicted.Should().ContainSingle().Which.Should().BeSameAs(entry);
        policy.AssertInvariants();
    }

    [Test]
    public void NegativePublicationWeightIsRejectedBeforeQueueAdmission()
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

        policy.Invoking(p => p.OnPublish(token, -1)).Should().Throw<ArgumentOutOfRangeException>();

        token.PendingPolicyWrites.Should().Be(0);
        policy.HasPendingWrites.Should().BeFalse();
        policy.GetWriteBufferStatistics().Enqueued.Should().Be(0);
        policy.AssertInvariants();
    }

    [Test]
    public void ResizeCommitsMaximumBeforeCallbackFailureAndClearUsesIt()
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

        policy
            .Invoking(static p => p.SetMaximum(1, weighted: true))
            .Should()
            .Throw<InvalidOperationException>();
        policy.Maximum.Should().Be(1);

        policy.Clear();

        policy.Maximum.Should().Be(1);
        policy.ResidentCount.Should().Be(0);
        policy.AssertInvariants();
    }

    [Test]
    public void NonWeightedResizeCommitsBothLimitsBeforeCallbackFailureAndClearUsesThem()
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

        policy
            .Invoking(static p => p.SetMaximum(1, weighted: false))
            .Should()
            .Throw<InvalidOperationException>();
        policy.Maximum.Should().Be(1);

        policy.Clear();

        policy.Maximum.Should().Be(1);
        policy.ResidentCount.Should().Be(0);
        policy.AssertInvariants();
    }

    [Test]
    public void EvictionCallbackFailureStillCleansEveryRetiredNode()
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

        policy.Invoking(static p => p.FlushWrites()).Should().Throw<InvalidOperationException>();
        evicted.Should().HaveCount(2);
        policy.AssertInvariants();

        policy.FlushWrites();
        policy.AssertInvariants();
    }

    [Test]
    public void FullFallbackInstallsSequenceBeforeApplyingOlderEvent()
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

        resident.Node.Should().NotBeNull();
        invalidated.Node.Should().BeNull();
        policy.ResidentCount.Should().Be(1);
        evicted.Should().BeEmpty();
        policy.AssertInvariants();
    }

    [Test]
    public void FlushWritesProcessesOnlyTheCapturedBatch()
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

        policy.HasPendingWrites.Should().BeFalse();
        policy.ResidentCount.Should().Be(1);
        policy.AssertInvariants();
    }

    [Test]
    public void SequenceRolloverResetsLiveNodeFencesBeforeIssuingNewEvents()
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

        first.Node.Should().NotBeNull();
        second.Node.Should().NotBeNull();
        policy.ResidentCount.Should().Be(2);
        policy.AssertInvariants();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EnqueueObject(BoundedWriteBuffer<object> buffer)
    {
        object value = new();
        WeakReference reference = new(value);
        buffer.TryEnqueue(value).Should().BeTrue();
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
                buffer.TryEnqueue(value).Should().BeTrue();
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
