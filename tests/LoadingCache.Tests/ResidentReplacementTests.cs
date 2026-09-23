using System.Collections.Concurrent;

namespace LoadingCache.Tests;

public sealed class ResidentReplacementTests
{
    [Test]
    public async Task ResidentPutWithoutTimePoliciesDoesNotSampleTheClock()
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
        await Assert.That(clock.TimestampCalls).IsEqualTo(timestampCalls);
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("second");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
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
        await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("second");
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.Statistics.ReplacedRemovals).IsEqualTo(1);
        await Assert
            .That(notifications)
            .HasSingleItem(notification =>
                notification.Key == 1
                && notification.Value == "first"
                && notification.Cause == RemovalCause.Replaced
            );
    }

    [Test]
    public async Task RepeatedSameWeightReplacementDoesNotCreatePolicyGhosts()
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
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
        await Assert.That(cache.Policy.Eviction!.WeightedSize).IsEqualTo(1);
        await Assert.That(cache.TryGet(1, out int current)).IsTrue();
        await Assert.That(current).IsEqualTo(31);
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
        await Assert.That(replacement.Key).IsEqualTo("Canonical");
        await Assert.That(replacement.Value).IsEqualTo("first");
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

                    if ((current.Value) != (1))
                        Assert.Fail("Expected current.Value to equal (1).");
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
            await Assert
                .That((await compute.WaitAsync(TimeSpan.FromSeconds(5))).Kind)
                .IsEqualTo(CacheMutationKind.Keep);
            await Assert.That(callbackCalls).IsEqualTo(2);
            await Assert.That(dictionary[1]).IsEqualTo(42);
        }
        finally
        {
            releaseCallback.TrySetResult(null);
            await compute.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task MemoryPressureCandidateCannotRemoveAnInPlaceReplacement()
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
        Assert.NotNull(snapshot);
        cache.Put(1, "new");
        await Assert.That(engine.TrimForMemoryPressure(snapshot)).IsEqualTo(0);
        await Assert.That(cache.TryGet(1, out string? current)).IsTrue();
        await Assert.That(current).IsEqualTo("new");
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
        await Assert.That(cache.TryGetTask(1, out Task<string>? first)).IsTrue();
        Assert.NotNull(first);
        await Assert.That(cache.TryGetTask(1, out Task<string>? sameVersion)).IsTrue();
        Assert.NotNull(sameVersion);
        await Assert.That(ReferenceEquals(sameVersion, first)).IsTrue();
        cache.Set(1, "second");
        await Assert.That(cache.TryGetTask(1, out Task<string>? second)).IsTrue();
        Assert.NotNull(second);
        await Assert.That(ReferenceEquals(second, first)).IsFalse();
        await Assert.That((await first!)).IsEqualTo("first");
        await Assert.That((await second)).IsEqualTo("second");
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
        await Assert.That(cache.TryGetTask(1, out Task<object>? first)).IsTrue();
        Assert.NotNull(first);
        cache.Set(1, value);
        await Assert.That(cache.TryGetTask(1, out Task<object>? second)).IsTrue();
        Assert.NotNull(second);
        await Assert.That(ReferenceEquals(second, first)).IsFalse();
        await Assert.That(cache.TryGetTask(1, out Task<object>? sameVersion)).IsTrue();
        Assert.NotNull(sameVersion);
        await Assert.That(ReferenceEquals(sameVersion, second)).IsTrue();
        await Assert.That(ReferenceEquals((await first!), value)).IsTrue();
        await Assert.That(ReferenceEquals((await second), value)).IsTrue();
        RemovalNotification<int, object> replacement = await replaced.Task.WaitAsync(
            TimeSpan.FromSeconds(5)
        );
        await Assert.That(replacement.Key).IsEqualTo(1);
        await Assert.That(ReferenceEquals(replacement.Value, value)).IsTrue();
        await Assert.That(replacement.Cause).IsEqualTo(RemovalCause.Replaced);
        await Assert.That(notifications).HasSingleItem();
        await Assert.That(cache.EstimatedCount).IsEqualTo(1);
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
            await Assert.That(cache.TryGetTask(1, out Task<string>? current)).IsTrue();
            Assert.NotNull(current);
            await Assert.That((await current!)).IsEqualTo("set");
            releaseCompletion.TrySetResult(null);
            await Assert.That((await load.WaitAsync(TimeSpan.FromSeconds(5)))).IsEqualTo("loaded");
            await Assert.That(cache.TryGet(1, out string? value)).IsTrue();
            await Assert.That(value).IsEqualTo("set");
            await Assert.That(cache.TryGetTask(1, out Task<string>? after)).IsTrue();
            Assert.NotNull(after);
            await Assert.That(ReferenceEquals(after, current)).IsTrue();
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
        await Assert.That(value.DisposeCount).IsEqualTo(0);
        await Assert.That(cache.Invalidate(1)).IsTrue();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(value.DisposeCount).IsEqualTo(1);
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
