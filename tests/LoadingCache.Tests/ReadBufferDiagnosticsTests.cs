using FluentAssertions;
using LoadingCache.Diagnostics;
using LoadingCache.Maintenance;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
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
            buffer.TryOffer(0).Should().Be(ReadBufferOfferResult.Success);
            buffer.TryRead(out _).Should().BeTrue();
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
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
                buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Failed);
                buffer.StripeCountForTesting.Should().Be(2);
                buffer.Dispose();
                tableCaptured.Release();

                ReadBufferStatistics statistics = await snapshot.WaitAsync(watchdog);
                statistics.Enqueued.Should().Be(2);
                statistics.Dequeued.Should().Be(1);
                statistics.DroppedShutdown.Should().Be(1);
                statistics.Queued.Should().Be(0);
                tableCaptured.TimedOut.Should().BeFalse();
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

    [TestCase(false)]
    [TestCase(true)]
    public void DiagnosticShardsAreBoundedAndAllocatedOnlyWhenRecording(bool recordStatistics)
    {
        using StripedReadBuffer<int> buffer = new(1, 1, recordStatistics);
        int expected = recordStatistics
            ? StripedCacheCounters.NormalizeStripeCount(Environment.ProcessorCount)
            : 0;
        buffer.DiagnosticStripeCountForTesting.Should().Be(expected);
        buffer.DiagnosticStripeCountForTesting.Should().BeInRange(0, 64);
    }

    [Test]
    public void SharedDropShardsAndShadowCountersAreAggregatedOnceAfterDisposal()
    {
        StripedReadBuffer<int> buffer = new(2, 2);
        try
        {
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
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
            live.DroppedFull.Should().Be(expectedFull);
            live.DroppedFailed.Should().Be(stripes);
            live.DroppedShutdown.Should().Be(7);
            live.Queued.Should().Be(1);

            buffer.Dispose();
            ReadBufferStatistics disposed = buffer.GetStatistics();
            disposed.DroppedFull.Should().Be(expectedFull);
            disposed.DroppedFailed.Should().Be(stripes);
            disposed.DroppedShutdown.Should().Be(8);
            disposed.Dropped.Should().Be(expectedFull + stripes + 8);
            disposed.Queued.Should().Be(0);

            buffer.Dispose();
            buffer.GetStatistics().Dropped.Should().Be(disposed.Dropped);
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Shutdown);
            buffer.GetStatistics().DroppedFull.Should().Be(expectedFull);
            buffer.GetStatistics().DroppedFailed.Should().Be(stripes);
            buffer.GetStatistics().DroppedShutdown.Should().Be(9);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Test]
    public void SharedDropShardsSaturateLocallyAndDuringAggregation()
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
        statistics.DroppedFull.Should().Be(long.MaxValue);
        statistics.DroppedFailed.Should().Be(long.MaxValue);
        statistics.Dropped.Should().Be(long.MaxValue);
        statistics.Queued.Should().Be(0);
    }

    [Test]
    public void PolicyClearPreservesSharedDropCountersAcrossReadBatches()
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
                policy.GetReadBufferStatistics().DroppedFull.Should().Be(batch);
                policy.Clear();
                policy.GetReadBufferStatistics().DroppedFull.Should().Be(batch);
                policy.GetReadBufferStatistics().Queued.Should().Be(0);
            }

            policy.Dispose();
            policy.GetReadBufferStatistics().DroppedFull.Should().Be(2);
            policy.GetReadBufferStatistics().DroppedFailed.Should().Be(0);
        }
        finally
        {
            policy.Dispose();
        }
    }

    [Test]
    public void DisabledCountersPreserveReservationsGaugesDrainingAndShutdown()
    {
        StripedReadBuffer<int> buffer = new(1, 4, recordStatistics: false);
        try
        {
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
            buffer.SetForcedCasFailuresForTesting(3);
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Failed);
            buffer.SetForcedCasFailuresForTesting(0);
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Success);
            buffer.HasPublished.Should().BeTrue();
            buffer.GetStatistics().Queued.Should().Be(2);

            List<int> observed = [];
            buffer.DrainTo(observed.Add, 1).Should().Be(1);
            observed.Should().Equal(1);
            buffer.GetStatistics().Queued.Should().Be(1);
            for (int value = 3; value <= 5; value++)
            {
                buffer.TryOffer(value).Should().Be(ReadBufferOfferResult.Success);
            }

            buffer.TryOffer(6).Should().Be(ReadBufferOfferResult.Full);
            buffer.GetStatistics().Queued.Should().Be(4);
            AssertDisabledCounters(buffer.GetStatistics());

            buffer.Dispose();
            buffer.TryOffer(7).Should().Be(ReadBufferOfferResult.Shutdown);
            buffer.HasPublished.Should().BeFalse();
            buffer.GetStatistics().Queued.Should().Be(0);
            buffer.GetStatistics().IsDisposed.Should().BeTrue();
            AssertDisabledCounters(buffer.GetStatistics());
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void EngineRecordsReadCountersOnlyForStatisticsOrMetricsAndAlwaysSchedulesMaintenance(
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

            cache.TryGet(1, out int value).Should().BeTrue();
            value.Should().Be(1);
            scheduler.Pending.Should().Be(0);
            cache.Statistics.MaintenanceBacklog.Should().Be(1);
            cache.TryGet(1, out _).Should().BeTrue();
            scheduler.Pending.Should().Be(1);

            long recorded = recordStatistics || enableMetrics ? 1 : 0;
            ReadBufferStatistics queued = engine.GetPolicyReadBufferStatistics();
            queued.Queued.Should().Be(1);
            queued.Enqueued.Should().Be(recorded);
            queued.DroppedFull.Should().Be(recorded);
            cache.Statistics.DroppedReadEvents.Should().Be(recordStatistics ? 1 : 0);

            scheduler.RunAll();
            cache.Statistics.MaintenanceBacklog.Should().Be(0);
            engine.GetPolicyReadBufferStatistics().Dequeued.Should().Be(recorded);

            cache.TryGet(1, out _).Should().BeTrue();
            cache.Dispose();
            ReadBufferStatistics disposed = engine.GetPolicyReadBufferStatistics();
            disposed.IsDisposed.Should().BeTrue();
            disposed.Queued.Should().Be(0);
            disposed.Dequeued.Should().Be(2 * recorded);
            disposed.DroppedShutdown.Should().Be(0);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [Test]
    public void PublicDroppedReadEventsIncludesForcedReservationFailures()
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
            buffer.TryOffer(1).Should().Be(ReadBufferOfferResult.Success);
            buffer.SetForcedCasFailuresForTesting(3);
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Failed);

            ReadBufferStatistics transport = buffer.GetStatistics();
            transport.Enqueued.Should().Be(1);
            transport.DroppedFailed.Should().Be(1);
            transport.DroppedFull.Should().Be(0);
            transport.DroppedShutdown.Should().Be(0);
            cache.Statistics.DroppedReadEvents.Should().Be(1);
            cache.Statistics.MaintenanceBacklog.Should().Be(1);

            buffer.SetForcedCasFailuresForTesting(0);
            buffer.TryOffer(2).Should().Be(ReadBufferOfferResult.Success);
            buffer.TryOffer(3).Should().Be(ReadBufferOfferResult.Full);
            buffer.GetStatistics().DroppedFull.Should().Be(1);
            cache.Statistics.DroppedReadEvents.Should().Be(2);

            buffer.Dispose();
            cache.Statistics.DroppedReadEvents.Should().Be(4);
            cache.Statistics.MaintenanceBacklog.Should().Be(0);
            buffer.AddStatisticsForTesting(0, droppedFull: long.MaxValue);
            cache.Statistics.DroppedReadEvents.Should().Be(long.MaxValue);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static void AssertDisabledCounters(ReadBufferStatistics statistics)
    {
        statistics.Enqueued.Should().Be(0);
        statistics.Dequeued.Should().Be(0);
        statistics.DroppedFull.Should().Be(0);
        statistics.DroppedFailed.Should().Be(0);
        statistics.DroppedShutdown.Should().Be(0);
        statistics.Dropped.Should().Be(0);
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
