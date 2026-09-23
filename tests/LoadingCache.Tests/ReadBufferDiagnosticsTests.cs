using LoadingCache.Diagnostics;
using LoadingCache.Maintenance;

namespace LoadingCache.Tests;

public sealed class ReadBufferDiagnosticsTests
{
    [Test]
    public async Task StatisticsCaptureDoesNotCountSharedExpandedRingsAgainAfterDisposal()
    {
        TimeSpan watchdog = TimeSpan.FromSeconds(5);
        StripedReadBuffer<int> buffer = new(2, 4);
        try
        {
            await using BlockingTestHook tableCaptured = new(watchdog);
            await Assert.That(buffer.TryOffer(0)).IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.TryRead(out _)).IsTrue();
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
            Task<ReadBufferStatistics> snapshot = Task.Factory.StartNew(
                static state =>
                {
                    (StripedReadBuffer<int> current, BlockingTestHook captured) = ((
                        StripedReadBuffer<int>,
                        BlockingTestHook
                    ))
                        state!;
                    return current.GetStatistics(captured.Invoke);
                },
                (buffer, tableCaptured),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            try
            {
                await tableCaptured.Entered.WaitAsync(watchdog);
                buffer.SetForcedCasFailuresForTesting(3);
                await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Failed);
                await Assert.That(buffer.StripeCountForTesting).IsEqualTo(2);
                buffer.Dispose();
                tableCaptured.Release();
                ReadBufferStatistics statistics = await snapshot.WaitAsync(watchdog);
                await Assert.That(statistics.Enqueued).IsEqualTo(2);
                await Assert.That(statistics.Dequeued).IsEqualTo(1);
                await Assert.That(statistics.DroppedShutdown).IsEqualTo(1);
                await Assert.That(statistics.Queued).IsEqualTo(0);
                await Assert.That(tableCaptured.TimedOut).IsFalse();
            }
            finally
            {
                tableCaptured.Release();
                await snapshot.WaitAsync(watchdog);
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiagnosticShardsAreBoundedAndAllocatedOnlyWhenRecording(bool recordStatistics)
    {
        using StripedReadBuffer<int> buffer = new(1, 1, recordStatistics);
        int expected = recordStatistics
            ? StripedCacheCounters.NormalizeStripeCount(Environment.ProcessorCount)
            : 0;
        await Assert.That(buffer.DiagnosticStripeCountForTesting).IsEqualTo(expected);
        await Assert.That(buffer.DiagnosticStripeCountForTesting).IsBetween(0, 64);
    }

    [Test]
    public async Task SharedDropShardsAndShadowCountersAreAggregatedOnceAfterDisposal()
    {
        StripedReadBuffer<int> buffer = new(2, 2);
        try
        {
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
            int stripes = buffer.DiagnosticStripeCountForTesting;
            for (int stripe = 0; stripe < stripes; stripe++)
            {
                buffer.AddDropStatisticsForTesting(
                    stripe,
                    droppedFull: stripe + 1,
                    droppedFailed: 1
                );
            }

            buffer.AddStatisticsForTesting(0, droppedFull: 7, droppedShutdown: 3);
            buffer.AddStatisticsForTesting(1, droppedFull: 11, droppedShutdown: 4);
            long expectedFull = stripes * (stripes + 1L) / 2 + 18;
            ReadBufferStatistics live = buffer.GetStatistics();
            await Assert.That(live.DroppedFull).IsEqualTo(expectedFull);
            await Assert.That(live.DroppedFailed).IsEqualTo(stripes);
            await Assert.That(live.DroppedShutdown).IsEqualTo(7);
            await Assert.That(live.Queued).IsEqualTo(1);
            buffer.Dispose();
            ReadBufferStatistics disposed = buffer.GetStatistics();
            await Assert.That(disposed.DroppedFull).IsEqualTo(expectedFull);
            await Assert.That(disposed.DroppedFailed).IsEqualTo(stripes);
            await Assert.That(disposed.DroppedShutdown).IsEqualTo(8);
            await Assert.That(disposed.Dropped).IsEqualTo(expectedFull + stripes + 8);
            await Assert.That(disposed.Queued).IsEqualTo(0);
            buffer.Dispose();
            await Assert.That(buffer.GetStatistics().Dropped).IsEqualTo(disposed.Dropped);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Shutdown);
            await Assert.That(buffer.GetStatistics().DroppedFull).IsEqualTo(expectedFull);
            await Assert.That(buffer.GetStatistics().DroppedFailed).IsEqualTo(stripes);
            await Assert.That(buffer.GetStatistics().DroppedShutdown).IsEqualTo(9);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public async Task SharedDropShardsSaturateLocallyAndDuringAggregation()
    {
        using StripedReadBuffer<int> buffer = new(1, 1);
        buffer.AddDropStatisticsForTesting(
            0,
            droppedFull: long.MaxValue - 1,
            droppedFailed: long.MaxValue - 1
        );
        buffer.AddDropStatisticsForTesting(
            buffer.DiagnosticStripeCountForTesting - 1,
            droppedFull: 2,
            droppedFailed: 2
        );
        ReadBufferStatistics statistics = buffer.GetStatistics();
        await Assert.That(statistics.DroppedFull).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.DroppedFailed).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.Dropped).IsEqualTo(long.MaxValue);
        await Assert.That(statistics.Queued).IsEqualTo(0);
    }

    [Test]
    public async Task PolicyClearPreservesSharedDropCountersAcrossReadBatches()
    {
        WindowTinyLfuEnginePolicy policy = new(
            maximum: 1,
            maximumResidentCount: 1,
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 1
        );
        try
        {
            WindowTinyLfuEnginePolicy.EngineEntryToken token = new(new object(), 1);
            for (int batch = 1; batch <= 2; batch++)
            {
                policy.OnAccess(token);
                policy.OnAccess(token);
                await Assert.That(policy.GetReadBufferStatistics().DroppedFull).IsEqualTo(batch);
                policy.Clear();
                await Assert.That(policy.GetReadBufferStatistics().DroppedFull).IsEqualTo(batch);
                await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
            }

            policy.Dispose();
            await Assert.That(policy.GetReadBufferStatistics().DroppedFull).IsEqualTo(2);
            await Assert.That(policy.GetReadBufferStatistics().DroppedFailed).IsEqualTo(0);
        }
        finally
        {
            policy.Dispose();
        }
    }

    [Test]
    public async Task DisabledCountersPreserveReservationsGaugesDrainingAndShutdown()
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics: false);
        try
        {
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
            buffer.SetForcedCasFailuresForTesting(3);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Failed);
            buffer.SetForcedCasFailuresForTesting(0);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.HasPublished).IsTrue();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(2);
            List<int> observed = [];
            await Assert.That(buffer.DrainTo(observed.Add, 1)).IsEqualTo(1);
            await Assert
                .That(observed)
                .IsEquivalentTo([1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(1);
            for (int value = 3; value <= 5; value++)
            {
                await Assert.That(buffer.TryOffer(value)).IsEqualTo(ReadBufferOfferResult.Success);
            }

            await Assert.That(buffer.TryOffer(6)).IsEqualTo(ReadBufferOfferResult.Full);
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(4);
            AssertDisabledCounters(buffer.GetStatistics());
            buffer.Dispose();
            await Assert.That(buffer.TryOffer(7)).IsEqualTo(ReadBufferOfferResult.Shutdown);
            await Assert.That(buffer.HasPublished).IsFalse();
            await Assert.That(buffer.GetStatistics().Queued).IsEqualTo(0);
            await Assert.That(buffer.GetStatistics().IsDisposed).IsTrue();
            AssertDisabledCounters(buffer.GetStatistics());
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task EngineRecordsReadCountersOnlyForStatisticsOrMetricsAndAlwaysSchedulesMaintenance(
        bool recordStatistics,
        bool enableMetrics
    )
    {
        ManualScheduler scheduler = new();
        CacheEngine<int, int> engine = new(
            new CacheEngineOptions<int, int>
            {
                MaximumSize = 1,
                MaxConcurrentLoads = 1,
                RecordStatistics = recordStatistics,
                EnableMetrics = enableMetrics,
                MetricsName = $"read-transport-{Guid.NewGuid():N}",
                MaintenanceScheduler = scheduler,
                MaintenanceReadStripeCount = 1,
                MaintenanceReadStripeCapacity = 1,
            }
        );
        Cache<int, int> cache = new(engine);
        try
        {
            cache.Put(1, 1);
            scheduler.RunAll();
            await Assert.That(cache.TryGet(1, out int value)).IsTrue();
            await Assert.That(value).IsEqualTo(1);
            await Assert.That(scheduler.Pending).IsEqualTo(0);
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(1);
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            await Assert.That(scheduler.Pending).IsEqualTo(1);
            long recorded = recordStatistics || enableMetrics ? 1 : 0;
            ReadBufferStatistics queued = engine.GetPolicyReadBufferStatistics();
            await Assert.That(queued.Queued).IsEqualTo(1);
            await Assert.That(queued.Enqueued).IsEqualTo(recorded);
            await Assert.That(queued.DroppedFull).IsEqualTo(recorded);
            await Assert
                .That(cache.Statistics.DroppedReadEvents)
                .IsEqualTo(recordStatistics ? 1 : 0);
            scheduler.RunAll();
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(0);
            await Assert.That(engine.GetPolicyReadBufferStatistics().Dequeued).IsEqualTo(recorded);
            await Assert.That(cache.TryGet(1, out _)).IsTrue();
            cache.Dispose();
            ReadBufferStatistics disposed = engine.GetPolicyReadBufferStatistics();
            await Assert.That(disposed.IsDisposed).IsTrue();
            await Assert.That(disposed.Queued).IsEqualTo(0);
            await Assert.That(disposed.Dequeued).IsEqualTo(2 * recorded);
            await Assert.That(disposed.DroppedShutdown).IsEqualTo(0);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Test]
    public async Task PublicDroppedReadEventsIncludesForcedReservationFailures()
    {
        StripedReadBuffer<int> buffer = new(1, 2);
        try
        {
            using Cache<int, int> cache = new(
                new CacheEngine<int, int>(
                    new CacheEngineOptions<int, int>
                    {
                        MaximumSize = 1,
                        MaxConcurrentLoads = 1,
                        RecordStatistics = true,
                        Policy = new ReadStatisticsPolicy(buffer),
                    }
                )
            );
            await Assert.That(buffer.TryOffer(1)).IsEqualTo(ReadBufferOfferResult.Success);
            buffer.SetForcedCasFailuresForTesting(3);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Failed);
            ReadBufferStatistics transport = buffer.GetStatistics();
            await Assert.That(transport.Enqueued).IsEqualTo(1);
            await Assert.That(transport.DroppedFailed).IsEqualTo(1);
            await Assert.That(transport.DroppedFull).IsEqualTo(0);
            await Assert.That(transport.DroppedShutdown).IsEqualTo(0);
            await Assert.That(cache.Statistics.DroppedReadEvents).IsEqualTo(1);
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(1);
            buffer.SetForcedCasFailuresForTesting(0);
            await Assert.That(buffer.TryOffer(2)).IsEqualTo(ReadBufferOfferResult.Success);
            await Assert.That(buffer.TryOffer(3)).IsEqualTo(ReadBufferOfferResult.Full);
            await Assert.That(buffer.GetStatistics().DroppedFull).IsEqualTo(1);
            await Assert.That(cache.Statistics.DroppedReadEvents).IsEqualTo(2);
            buffer.Dispose();
            await Assert.That(cache.Statistics.DroppedReadEvents).IsEqualTo(4);
            await Assert.That(cache.Statistics.MaintenanceBacklog).IsEqualTo(0);
            buffer.AddStatisticsForTesting(0, droppedFull: long.MaxValue);
            await Assert.That(cache.Statistics.DroppedReadEvents).IsEqualTo(long.MaxValue);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static void AssertDisabledCounters(ReadBufferStatistics statistics)
    {
        if ((statistics.Enqueued) != (0))
            Assert.Fail("Expected statistics.Enqueued to equal (0).");
        if ((statistics.Dequeued) != (0))
            Assert.Fail("Expected statistics.Dequeued to equal (0).");
        if ((statistics.DroppedFull) != (0))
            Assert.Fail("Expected statistics.DroppedFull to equal (0).");
        if ((statistics.DroppedFailed) != (0))
            Assert.Fail("Expected statistics.DroppedFailed to equal (0).");
        if ((statistics.DroppedShutdown) != (0))
            Assert.Fail("Expected statistics.DroppedShutdown to equal (0).");
        if ((statistics.Dropped) != (0))
            Assert.Fail("Expected statistics.Dropped to equal (0).");
    }

    private sealed class ManualScheduler : IMaintenanceScheduler
    {
        private readonly Queue<Action> _callbacks = new();
        internal int Pending => _callbacks.Count;

        public bool TrySchedule(Action callback)
        {
            _callbacks.Enqueue(callback);
            return true;
        }

        internal void RunAll()
        {
            while (_callbacks.Count != 0)
            {
                _callbacks.Dequeue()();
            }
        }
    }

    private sealed class ReadStatisticsPolicy(StripedReadBuffer<int> buffer) : ICacheEnginePolicy
    {
        public long Maximum => 1;
        public long WeightedSize => 0;
        public int ResidentCount => 0;

        public void SetMaximum(long maximum, bool weighted) { }

        public IReadOnlyList<object> Snapshot(bool hottest, int limit) => [];

        public void OnAccess(object? entryToken) { }

        public void OnPublish(object? entryToken, long weight) { }

        public void OnRemove(object? entryToken) { }

        public void Clear() { }

        public bool CleanUp() => false;

        public ReadBufferStatistics GetReadBufferStatistics() => buffer.GetStatistics();

        public void Dispose() => buffer.Dispose();
    }
}
