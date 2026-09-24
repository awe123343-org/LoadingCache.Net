using System.Collections.Concurrent;
using FluentAssertions;

namespace LoadingCache.Tests;

public sealed class ResidentReplacementTests
{
    [Test]
    public void ResidentPutWithoutTimePoliciesDoesNotSampleTheClock()
    {
        var clock = new CountingTimestampProvider();
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .TimeProvider(clock)
            .Build();
        cache.Put(1, "first");
        int timestampCalls = clock.TimestampCalls;
        cache.Put(1, "second");
        clock.TimestampCalls.Should().Be(timestampCalls);
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("second");
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public async Task ReplacingResidentValueKeepsOnePolicyResidentAndPublishesNewValue()
    {
        var notifications = new ConcurrentQueue<RemovalNotification<int, string>>();
        var replaced = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using ICache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(2)
            .MaxConcurrentLoads(4)
            .RemovalListener(notification =>
            {
                notifications.Enqueue(notification);
                if (notification.Cause == RemovalCause.Replaced)
                {
                    replaced.TrySetResult(null);
                }
            })
            .RecordStatistics()
            .Build();
        cache.Put(1, "first");
        cache.Put(1, "second");
        cache.CleanUp();
        await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("second");
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.Statistics.ReplacedRemovals.Should().Be(1);
        notifications
            .Should()
            .ContainSingle(notification =>
                notification.Key == 1
                && notification.Value == "first"
                && notification.Cause == RemovalCause.Replaced
            );
    }

    [Test]
    public void RepeatedSameWeightReplacementDoesNotCreatePolicyGhosts()
    {
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(2)
            .MaxConcurrentLoads(4)
            .Build();
        for (int value = 0; value < 32; value++)
        {
            cache.Put(1, value);
        }

        cache.CleanUp();
        cache.EstimatedCount.Should().Be(1);
        cache.Policy.Eviction!.WeightedSize.Should().Be(1);
        cache.TryGet(1, out int current).Should().BeTrue();
        current.Should().Be(31);
    }

    [Test]
    public async Task ComparerEqualReplacementReportsTheResidentKey()
    {
        var notification = new TaskCompletionSource<RemovalNotification<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using ICache<string, string> cache = CacheBuilder
            .Create<string, string>()
            .MaximumSize(2)
            .MaxConcurrentLoads(4)
            .Comparer(StringComparer.OrdinalIgnoreCase)
            .RemovalListener(current =>
            {
                if (current.Cause == RemovalCause.Replaced)
                {
                    notification.TrySetResult(current);
                }
            })
            .Build();
        cache.Put("Canonical", "first");
        cache.Put("canonical", "second");
        RemovalNotification<string, string> replacement = await notification.Task.WaitAsync(
            TimeSpan.FromSeconds(5)
        );
        replacement.Key.Should().Be("Canonical");
        replacement.Value.Should().Be("first");
    }

    [Test]
    public Task RepeatedResidentPutHasBoundedAllocation() =>
        AllocationTestProcess.VerifyAsync("resident-put");

    [Test]
    public async Task DictionaryComputeRetriesAfterResidentPutChangesRevision()
    {
        using ICache<int, int> cache = CacheBuilder
            .Create<int, int>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .Build();
        SyncCacheDictionary<int, int> dictionary = cache.AsDictionary();
        dictionary[1] = 1;
        var callbackEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCallback = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int callbackCalls = 0;
        Task<CacheMutation<int>> compute = Task.Run(() =>
            dictionary.Compute(
                1,
                (_, current) =>
                {
                    int call = Interlocked.Increment(ref callbackCalls);
                    if (call != 1)
                    {
                        return CacheMutation.Keep<int>();
                    }

                    current.Value.Should().Be(1);
                    callbackEntered.TrySetResult(null);
                    releaseCallback.Task.GetAwaiter().GetResult();
                    return CacheMutation.Set(2);
                }
            )
        );
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Put(1, 42);
            releaseCallback.TrySetResult(null);
            (await compute.WaitAsync(TimeSpan.FromSeconds(5)))
                .Kind.Should()
                .Be(CacheMutationKind.Keep);
            callbackCalls.Should().Be(2);
            dictionary[1].Should().Be(42);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void MemoryPressureCandidateCannotRemoveAnInPlaceReplacement()
    {
        CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .MemoryPressureEviction(TimeSpan.FromSeconds(1), trimFraction: 1, maximumTrimCount: 1)
            .CreateEngine(supportsBulkLoading: false);
        using var cache = new Cache<int, string>(engine);
        cache.Put(1, "old");
        cache.CleanUp();
        MemoryPressureSnapshot? snapshot = engine.CaptureMemoryPressureSnapshot(1, 1);
        snapshot.Should().NotBeNull();
        cache.Put(1, "new");
        engine.TrimForMemoryPressure(snapshot).Should().Be(0);
        cache.TryGet(1, out string? current).Should().BeTrue();
        current.Should().Be("new");
    }

    [Test]
    public async Task AsyncSetCreatesOneStableTaskPerResidentVersion()
    {
        await using IAsyncLoadingCache<int, string> cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .BuildAsyncLoading((_, _) => Task.FromResult("loader"));
        cache.Set(1, "first");
        cache.TryGetTask(1, out Task<string>? first).Should().BeTrue();
        cache.TryGetTask(1, out Task<string>? sameVersion).Should().BeTrue();
        sameVersion.Should().BeSameAs(first);
        cache.Set(1, "second");
        cache.TryGetTask(1, out Task<string>? second).Should().BeTrue();
        second.Should().NotBeSameAs(first);
        (await first!).Should().Be("first");
        (await second).Should().Be("second");
    }

    [Test]
    public async Task SameReferenceSetCreatesANewTaskVersionAndReplacementNotification()
    {
        object value = new();
        var replaced = new TaskCompletionSource<RemovalNotification<int, object>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notifications = new ConcurrentQueue<RemovalNotification<int, object>>();
        await using IAsyncLoadingCache<int, object> cache = CacheBuilder
            .Create<int, object>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .RemovalListener(notification =>
            {
                notifications.Enqueue(notification);
                if (notification.Cause == RemovalCause.Replaced)
                {
                    replaced.TrySetResult(notification);
                }
            })
            .BuildAsyncLoading(
                static (_, _) => throw new InvalidOperationException("No load should start.")
            );
        cache.Set(1, value);
        cache.TryGetTask(1, out Task<object>? first).Should().BeTrue();
        cache.Set(1, value);
        cache.TryGetTask(1, out Task<object>? second).Should().BeTrue();
        second.Should().NotBeSameAs(first);
        cache.TryGetTask(1, out Task<object>? sameVersion).Should().BeTrue();
        sameVersion.Should().BeSameAs(second);
        (await first!).Should().BeSameAs(value);
        (await second).Should().BeSameAs(value);
        RemovalNotification<int, object> replacement = await replaced.Task.WaitAsync(
            TimeSpan.FromSeconds(5)
        );
        replacement.Key.Should().Be(1);
        replacement.Value.Should().BeSameAs(value);
        replacement.Cause.Should().Be(RemovalCause.Replaced);
        notifications.Should().ContainSingle();
        cache.EstimatedCount.Should().Be(1);
    }

    [Test]
    public async Task SetAfterReadyPublicationPreservesLateCompletionResult()
    {
        var completionEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseCompletion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var hooks = new LoadingCacheTestHooks
        {
            BeforeCompletion = () =>
            {
                completionEntered.TrySetResult(null);
                releaseCompletion.Task.GetAwaiter().GetResult();
            },
        };
        var engine = new CacheEngine<int, string>(
            new CacheEngineOptions<int, string>
            {
                MaximumSize = 8,
                MaxConcurrentLoads = 2,
                TestHooks = hooks,
                SupportsBulkLoading = false,
            }
        );
        var loader = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = new AsyncLoadingCache<int, string>(engine, (_, _) => loader.Task);
        Task<string> load = cache.GetAsync(1).AsTask();
        loader.SetResult("loaded");
        try
        {
            await completionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Set(1, "set");
            cache.TryGetTask(1, out Task<string>? current).Should().BeTrue();
            (await current!).Should().Be("set");
            releaseCompletion.TrySetResult(null);
            (await load.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("loaded");
            cache.TryGet(1, out string? value).Should().BeTrue();
            value.Should().Be("set");
            cache.TryGetTask(1, out Task<string>? after).Should().BeTrue();
            after.Should().BeSameAs(current);
        }
        finally
        {
            releaseCompletion.TrySetResult(null);
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ReplacingAnOwnedValueWithTheSameReferenceRetiresItOnlyOnce()
    {
        var value = new OwnedDisposableValue();
        var disposed = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var cache = OwnedCache.Create(
            new OwnedCacheOptions<int, OwnedDisposableValue>
            {
                MaximumSize = 2,
                MaximumActiveValues = 3,
            },
            current =>
            {
                current.Dispose();
                disposed.TrySetResult(null);
            }
        );
        cache.Put(1, value);
        cache.Put(1, value);
        value.DisposeCount.Should().Be(0);
        cache.Invalidate(1).Should().BeTrue();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        value.DisposeCount.Should().Be(1);
    }

    private sealed class CountingTimestampProvider : TimeProvider
    {
        private int _timestampCalls;
        internal int TimestampCalls => Volatile.Read(ref _timestampCalls);

        public override long GetTimestamp() => Interlocked.Increment(ref _timestampCalls);
    }

    private sealed class OwnedDisposableValue
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
