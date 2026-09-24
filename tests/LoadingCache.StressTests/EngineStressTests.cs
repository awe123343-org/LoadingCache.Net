using System.Runtime.InteropServices;
using FluentAssertions;

namespace LoadingCache.StressTests;

/// <summary>Bounded mixed-operation stress; fixed seeds reproduce input, not OS schedules.</summary>
public sealed class EngineStressTests
{
    /// <summary>Exercises mixed operations and checks the quiescent store/policy invariants.</summary>
    [Test]
    [Arguments(419)]
    [Arguments(20260912)]
    public async Task MixedTrafficConvergesWithoutGhostNodes(int seed)
    {
        const int workers = 8;
        const int operations = 2_000;
        const int maximum = 64;
        int invocations = 0;
        int active = 0;
        int peak = 0;
        AsyncLoadingCache<int, Payload> cache =
            (AsyncLoadingCache<int, Payload>)
                CacheBuilder
                    .Create<int, Payload>()
                    .MaximumSize(maximum)
                    .MaxConcurrentLoads(workers * 2)
                    .RecordStatistics()
                    .BuildAsyncLoading(
                        async (key, token) =>
                        {
                            Interlocked.Increment(ref invocations);
                            int running = Interlocked.Increment(ref active);
                            UpdateMaximum(ref peak, running);
                            try
                            {
                                await Task.Yield();
                                token.ThrowIfCancellationRequested();
                                return new Payload(key, "loader");
                            }
                            finally
                            {
                                Interlocked.Decrement(ref active);
                            }
                        }
                    );
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[][] traces = [.. Enumerable.Range(0, workers).Select(_ => new string[128])];
        Task[] jobs = [];
        try
        {
            jobs =
            [
                .. Enumerable
                    .Range(0, workers)
                    .Select(worker =>
                        Task.Run(async () =>
                        {
                            Random random = new(unchecked(seed * 397 + worker));
                            await start.Task.ConfigureAwait(false);
                            for (int index = 0; index < operations; index++)
                            {
                                int key = random.Next(128);
                                int operation = random.Next(100);
                                traces[worker][index % traces[worker].Length] =
                                    $"{index}:{operation}:{key}";
                                switch (operation)
                                {
                                    case < 60:
                                        Payload loaded = await cache.GetAsync(key);
                                        loaded.Key.Should().Be(key);
                                        (loaded.Origin is "loader" or "set").Should().BeTrue();
                                        break;
                                    case < 78:
                                        cache.Set(key, new Payload(key, "set"));
                                        break;
                                    case < 90:
                                        cache.Invalidate(key);
                                        break;
                                    case < 95:
                                        if (cache.TryGet(key, out Payload? value))
                                        {
                                            value.Key.Should().Be(key);
                                            (value.Origin is "loader" or "set").Should().BeTrue();
                                        }

                                        break;
                                    case < 98:
                                        cache.CleanUp();
                                        break;
                                    default:
                                        cache.Clear();
                                        break;
                                }
                            }
                        })
                    ),
            ];
            start.SetResult();
            await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(60));
            cache.CleanUp();
            cache.AssertInvariants();
            cache.EstimatedCount.Should().BeLessThanOrEqualTo(maximum);
            cache.GetStatistics().InFlightLoads.Should().Be(0);
            active.Should().Be(0);
            peak.Should().BeLessThanOrEqualTo(workers * 2);
        }
        catch
        {
            await TestContext
                .Current!.ErrorOutputWriter.WriteLineAsync(
                    $"seed={seed}; final 128 operations per worker, circular index:"
                )
                .ConfigureAwait(false);
            for (int worker = 0; worker < workers; worker++)
            {
                await TestContext
                    .Current.ErrorOutputWriter.WriteLineAsync(
                        $"worker {worker}: {string.Join(',', traces[worker])}"
                    )
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            start.TrySetResult();
            try
            {
                if (jobs.Length > 0)
                {
                    await Task.WhenAll(jobs)
                        .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await cache.DisposeAsync().ConfigureAwait(false);
            }
        }

        await TestContext
            .Current!.OutputWriter.WriteLineAsync(
                $"seed={seed}; operations={workers * operations}; loads={invocations}; peak={peak}; {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}"
            )
            .ConfigureAwait(false);
    }

    /// <summary>Exercises concurrent weighted replacement, including zero-weight values.</summary>
    [Test]
    [Arguments(419)]
    [Arguments(20260912)]
    public async Task ConcurrentWeightedReplacementConverges(int seed)
    {
        const int workers = 8;
        const int maximumCount = 32;
        Cache<int, int> cache =
            (Cache<int, int>)
                CacheBuilder
                    .Create<int, int>()
                    .MaximumWeight(256)
                    .MaximumResidentCount(maximumCount)
                    .Weigher(static (_, value) => value)
                    .MaxConcurrentLoads(workers)
                    .Build();
        try
        {
            await RunWeightedWorkersAsync(cache, seed, workers).ConfigureAwait(false);
            cache.CleanUp();
            cache.AssertInvariants();
            cache.Policy.Eviction!.WeightedSize.Should().BeLessThanOrEqualTo(256);
            cache.EstimatedCount.Should().BeLessThanOrEqualTo(maximumCount);
        }
        finally
        {
            cache.Dispose();
        }

        await TestContext
            .Current!.OutputWriter.WriteLineAsync(
                $"weighted seed={seed}; operations=16000; {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.ProcessArchitecture}"
            )
            .ConfigureAwait(false);
    }

    private static async Task RunWeightedWorkersAsync(Cache<int, int> cache, int seed, int workers)
    {
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] jobs = [];
        try
        {
            jobs =
            [
                .. Enumerable
                    .Range(0, workers)
                    .Select(worker =>
                        Task.Run(async () =>
                        {
                            Random random = new(unchecked(seed * 397 + worker));
                            await start.Task.ConfigureAwait(false);
                            for (int index = 0; index < 2_000; index++)
                            {
                                int key = random.Next(64);
                                cache.Put(key, random.Next(20));
                                if (index % 3 == 0)
                                {
                                    cache.TryGet(key, out _);
                                }

                                if (index % 17 == 0)
                                {
                                    cache.Invalidate(key);
                                }
                            }
                        })
                    ),
            ];
            start.SetResult();
            await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        finally
        {
            start.TrySetResult();
            if (jobs.Length > 0)
            {
                await Task.WhenAll(jobs)
                    .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (observed >= value)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, observed) != observed);
    }

    private sealed record Payload(int Key, string Origin);
}
