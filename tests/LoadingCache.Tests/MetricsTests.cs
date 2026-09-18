using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class MetricsTests
{
    [Test]
    public async Task MetricsExposeLoaderOutcomesWithoutEnablingPublicStatistics()
    {
        string cacheName = $"metrics-{Guid.NewGuid():N}";
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CacheMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                string? name = null;
                string? outcome = null;
                string? cause = null;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "cache.name":
                            name = tag.Value as string;
                            break;
                        case "outcome":
                            outcome = tag.Value as string;
                            break;
                        case "cause":
                            cause = tag.Value as string;
                            break;
                    }
                }

                if (name == cacheName)
                {
                    measurements.Add(
                        new MetricMeasurement(instrument.Name, measurement, outcome, cause)
                    );
                }
            }
        );
        listener.Start();

        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(4)
            .EnableMetrics(cacheName)
            .BuildAsyncLoading((key, _) => Task.FromResult($"value-{key}"));

        (await cache.GetAsync(1)).Should().Be("value-1");
        cache.Statistics.LoadsStarted.Should().Be(0);
        listener.RecordObservableInstruments();

        measurements
            .Should()
            .Contain(item =>
                item.Name == "loadingcache.loads" && item.Outcome == "started" && item.Value >= 1
            );
        measurements
            .Should()
            .Contain(item =>
                item.Name == "loadingcache.loads" && item.Outcome == "success" && item.Value >= 1
            );
        measurements
            .Should()
            .Contain(item =>
                item.Name == "loadingcache.inflight"
                && item.Outcome == null
                && item.Cause == null
                && item.Value == 0
            );
    }

    [Test]
    public void InstrumentPublicationCanSynchronouslyRecordWithoutHalfInitializedEngine()
    {
        Exception? publicationError = null;
        int recording = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != CacheMetrics.MeterName)
            {
                return;
            }

            meterListener.EnableMeasurementEvents(instrument);
            if (Interlocked.CompareExchange(ref recording, 1, 0) != 0)
            {
                return;
            }

            try
            {
                meterListener.RecordObservableInstruments();
            }
            catch (Exception exception)
            {
                publicationError = exception;
            }
        };
        listener.Start();

        Action build = () =>
        {
            using ICache<int, string> cache = CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(1)
                .EnableMetrics($"construction-{Guid.NewGuid():N}")
                .Build();
        };

        build.Should().NotThrow();
        publicationError.Should().BeNull();
    }

    [Test]
    public void ThrowingInstrumentPublicationDoesNotLeavePartialMetricsRegistration()
    {
        var throwDuringBuild = new AsyncLocal<bool>();
        int published = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            switch (instrument.Meter.Name)
            {
                case CacheMetrics.MeterName
                    when throwDuringBuild.Value && Interlocked.Increment(ref published) == 1:
                    throw new InvalidOperationException("instrument subscriber failure");
                case CacheMetrics.MeterName:
                    meterListener.EnableMeasurementEvents(instrument);
                    break;
            }
        };
        listener.Start();

        throwDuringBuild.Value = true;
        Action build = () =>
        {
            using ICache<int, string> cache = CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(1)
                .EnableMetrics($"throwing-{Guid.NewGuid():N}")
                .Build();
        };
        build.Should().Throw<InvalidOperationException>();
        throwDuringBuild.Value = false;

        Action secondBuild = () =>
        {
            using ICache<int, string> cache = CacheBuilder
                .Create<int, string>()
                .MaximumSize(4)
                .MaxConcurrentLoads(1)
                .EnableMetrics($"after-throw-{Guid.NewGuid():N}")
                .Build();
        };
        secondBuild.Should().NotThrow();
    }

    [Test]
    public void DisposedMetricsCacheIsNotRetainedByMeterListener()
    {
        using MeterListener listener = new();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CacheMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        WeakReference reference = CreateDisposedMetricsCache();
        ForceCollection(reference);

        reference.IsAlive.Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedMetricsCache()
    {
        ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .MaxConcurrentLoads(1)
            .EnableMetrics($"retention-{Guid.NewGuid():N}")
            .Build();
        var reference = new WeakReference(cache);
        cache.Dispose();
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection(WeakReference reference)
    {
        for (int attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Thread.Yield();
        }
    }

    private readonly record struct MetricMeasurement(
        string Name,
        long Value,
        string? Outcome,
        string? Cause
    );
}
