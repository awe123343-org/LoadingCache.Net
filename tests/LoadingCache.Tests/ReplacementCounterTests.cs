using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using LoadingCache.Diagnostics;
using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class ReplacementCounterTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentResidentPutsCountEverySameReferenceReplacement(bool sameKey)
    {
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(16)
            .MaxConcurrentLoads(4)
            .RecordStatistics()
            .Build();
        const int workers = 4;
        const int replacements = 512;
        for (int key = 0; key < (sameKey ? 1 : workers); key++)
        {
            cache.Put(key, "same reference");
        }

        await RunTogether(
            workers,
            worker =>
            {
                for (int index = 0; index < replacements; index++)
                {
                    cache.Put(sameKey ? 0 : worker, "same reference");
                }
            }
        );
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(workers * replacements);
        cache.Clear();
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(workers * replacements);
        await Assert.That(cache.Statistics.ClearedRemovals).IsEqualTo(sameKey ? 1 : workers);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MixedResidentPhysicalMutationAndClearCountsRespectOptIn(
        bool statistics,
        bool metrics
    )
    {
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 16,
                RecordStatistics = statistics,
                MaxConcurrentLoads = 4,
                EnableMetrics = metrics,
                MetricsName = $"replacement-opt-in-{Guid.NewGuid():N}",
            }
        );
        using var cache = new Cache<int, string>(engine);
        ExerciseMixedReplacements(cache);
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(statistics ? 5 : 0);
        await Assert.That(cache.Statistics.ClearedRemovals).IsEqualTo(statistics ? 2 : 0);
        var counters = (StripedCacheCounters?)
            PrivateField(engine.GetType(), "_counters").GetValue(engine);
        if (statistics || metrics)
        {
            Assert.NotNull(counters);
            await Assert.That(counters.Snapshot()[CacheCounterKind.ReplacedRemovals]).IsEqualTo(5);
        }
        else
        {
            await Assert
                .That((counters) is null)
                .IsTrue()
                .Because("disabled diagnostics must not allocate a counter object");
        }
    }

    [Test]
    public async Task PutTaskPhysicalReplacementAndResidentReplacementShareOneTotal()
    {
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 16,
                MaxConcurrentLoads = 4,
                RecordStatistics = true,
            }
        );
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "first");
        engine.PutTask(1, Task.FromResult("task value"));
        await Assert.That(engine.TryGetTask(1, out Task<string>? task)).IsTrue();
        Assert.NotNull(task);
        await Assert.That((await task!.WaitAsync(Watchdog))).IsEqualTo("task value");
        cache.Put(1, "resident replacement");
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(2);
    }

    [Test]
    public async Task SharedSuccessfulRefreshCountsOneReplacement()
    {
        var result = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(16)
            .MaxConcurrentLoads(4)
            .RecordStatistics()
            .BuildAsyncLoading((_, _) => result.Task);
        cache.Set(1, "old");
        Task<string> first = cache.RefreshAsync(1).AsTask();
        Task<string> second = cache.RefreshAsync(1).AsTask();
        result.SetResult("refreshed");
        await Assert
            .That((await Task.WhenAll(first, second).WaitAsync(Watchdog)))
            .All(value => value == "refreshed");
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(1);
        await Assert.That(cache.Statistics.RefreshSuccesses).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RefreshPublicationKeepsItsReplacementAccountingAcrossClearAndRollback(
        bool clear,
        bool fail
    )
    {
        await using var publication = new BlockingTestHook(Watchdog);
        Action blockPublication = publication.Invoke;
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 16,
                RecordStatistics = true,
                MaxConcurrentLoads = 4,
                TestHooks = new LoadingCacheTestHooks
                {
                    AfterRefreshPublished = () =>
                    {
                        blockPublication();
                        if (fail)
                        {
                            throw new InvalidOperationException("Publication failed.");
                        }
                    },
                },
            }
        );
        await using var cache = new AsyncLoadingCache<int, string>(
            engine,
            static (_, _) => Task.FromResult("refreshed")
        );
        cache.Set(1, "old");
        Task<string> refresh = Task
            .Factory.StartNew(
                static state => ((AsyncLoadingCache<int, string>)state!).RefreshAsync(1).AsTask(),
                cache,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
            .Unwrap();
        try
        {
            await publication.Entered.WaitAsync(Watchdog);
            await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(0);
            if (clear)
            {
                cache.Clear();
            }

            publication.Release();
            if (fail)
            {
                await Assert
                    .That((Func<Task>)(() => refresh.WaitAsync(Watchdog)))
                    .ThrowsExactly<InvalidOperationException>();
            }
            else
            {
                await Assert.That((await refresh.WaitAsync(Watchdog))).IsEqualTo("refreshed");
            }

            await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(fail ? 0 : 1);
            await Assert.That(cache.Statistics.ClearedRemovals).IsEqualTo(clear ? 1 : 0);
            await Assert.That(cache.TryGet(1, out string? value)).IsEqualTo(!clear);
            if (!clear)
            {
                await Assert.That(value).IsEqualTo(fail ? "old" : "refreshed");
            }
        }
        finally
        {
            publication.Release();
            try
            {
                await refresh.WaitAsync(Watchdog);
            }
            catch (InvalidOperationException) when (fail) { }
        }

        await Assert.That(publication.TimedOut).IsFalse();
    }

    [Test]
    public async Task MetricsOnlyExposesCombinedReplacementsWhilePublicStatisticsStayZero()
    {
        string cacheName = $"replacement-metrics-{Guid.NewGuid():N}";
        ConcurrentQueue<long> replacements = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = static (instrument, current) =>
        {
            if (
                instrument.Meter.Name == CacheMetrics.MeterName
                && instrument.Name == "loadingcache.removals"
            )
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, tags, _) =>
            {
                bool matches = false;
                bool replaced = false;
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    matches |= tag.Key == "cache.name" && Equals(tag.Value, cacheName);
                    replaced |= tag is { Key: "cause", Value: "replaced" };
                }

                if (matches && replaced)
                {
                    replacements.Enqueue(value);
                }
            }
        );
        listener.Start();
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(16)
            .EnableMetrics(cacheName)
            .MaxConcurrentLoads(4)
            .Build();
        ExerciseMixedReplacements(cache);
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(0);
        listener.RecordObservableInstruments();
        await Assert.That((await Assert.That(replacements).HasSingleItem())).IsEqualTo(5);
    }

    private static void ExerciseMixedReplacements(ICache<int, string> cache)
    {
        cache.Put(1, "same");
        cache.Put(1, "same"); // Same-reference resident replacement.
        cache.AsDictionary()[1] = "physical";
        cache.Put(1, "resident");
        cache.PutAll([
            new KeyValuePair<int, string>(1, "bulk replacement"),
            new KeyValuePair<int, string>(2, "bulk insertion"),
        ]);
        cache.Clear();
        cache.Put(1, "new epoch");
        cache.Put(1, "new epoch");
    }

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new AssertionException($"Missing private field {name}.");

    private static async Task RunTogether(int workerCount, Action<int> action)
    {
        using CountdownEvent ready = new(workerCount);
        using ManualResetEventSlim start = new();
        Task[] workers = new Task[workerCount];
        for (int worker = 0; worker < workerCount; worker++)
        {
            workers[worker] = Task.Factory.StartNew(
                static state =>
                {
                    (
                        int index,
                        Action<int> work,
                        CountdownEvent arrived,
                        ManualResetEventSlim release
                    ) = ((int, Action<int>, CountdownEvent, ManualResetEventSlim))state!;
                    arrived.Signal();
                    release.Wait();
                    work(index);
                },
                (worker, action, ready, start),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
        }

        try
        {
            await Assert.That(ready.Wait(Watchdog)).IsTrue();
        }
        finally
        {
            start.Set();
            await Task.WhenAll(workers).WaitAsync(Watchdog);
        }
    }
}
