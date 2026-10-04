using FluentAssertions;

namespace LoadingCache.Tests;

public sealed class BulkParentChainTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NestedSiblingGetStillDetectsBulkReentrancy(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        await using var engine = CreateEngine<string, string>(StringComparer.OrdinalIgnoreCase);

        Task<IReadOnlyDictionary<string, string>> operation = Load(
            engine,
            asynchronous,
            ["owner", "sibling", "SIBLING", "tail"],
            Loader,
            cancellationToken
        );
        await FluentActions
            .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
            .Should()
            .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        engine.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
        return;

        async Task<IReadOnlyDictionary<string, string>> Loader(
            IReadOnlyCollection<string> keys,
            CancellationToken token
        )
        {
            keys.Should().Equal("owner", "sibling", "tail");
            await Task.Yield();
            await Task.Run(
                async () =>
                {
                    // Check before joining a pending flight, so a broken chain cannot hang.
                    // Keep the captured engine identity: the load awaits this nested task before async cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    LoadChainContext.Contains(engine, "SIBLING").Should().BeTrue();
                    if (asynchronous)
                    {
                        // Keep the captured engine: the load awaits this nested task before async cleanup.
                        // ReSharper disable once AccessToDisposedClosure
                        await engine.GetAsync(
                            "SIBLING",
                            static (key, _) => Task.FromResult(key),
                            token
                        );
                    }
                    else
                    {
                        // Keep the captured engine: the load awaits this nested task before async cleanup.
                        // ReSharper disable once AccessToDisposedClosure
                        engine.GetOrAdd("SIBLING", static key => key, token);
                    }
                },
                token
            );
            return keys.ToDictionary(static key => key, static key => key);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BulkRequestStillDetectsAnAncestorKey(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        await using var engine = CreateEngine<string, string>(StringComparer.OrdinalIgnoreCase);
        int bulkCalls = 0;

        Task<string> operation = Task.Run(
            async () =>
                asynchronous
                    // Keep this engine capture: the load task is awaited before async scope cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    ? await engine.GetAsync("ancestor", OuterLoader, cancellationToken)
                    // Keep this engine capture: the load task is awaited before async scope cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    : engine.GetOrAdd(
                        "ancestor",
                        key => OuterLoader(key, cancellationToken).GetAwaiter().GetResult(),
                        cancellationToken
                    ),
            cancellationToken
        );
        await FluentActions
            .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
            .Should()
            .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        bulkCalls.Should().Be(0);
        engine.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
        return;

        async Task<string> OuterLoader(string key, CancellationToken token)
        {
            await Task.Yield();
            await Load(
                // Keep the outer engine capture: the loader is awaited before async scope cleanup.
                // ReSharper disable once AccessToDisposedClosure
                engine,
                asynchronous,
                ["left", "ANCESTOR", "right"],
                (keys, _) =>
                {
                    bulkCalls++;
                    return Task.FromResult<IReadOnlyDictionary<string, string>>(
                        keys.ToDictionary(static key => key, static key => key)
                    );
                },
                token
            );
            return key;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LargeBulkRetainsEveryValueAndOwnedKeyInTheChain(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        await using var engine = CreateEngine<int, int>();
        int[] requested = [.. Enumerable.Range(0, 4096)];
        var expected = requested.ToDictionary(static key => key, static key => key * 10);
        int bulkCalls = 0;

        IReadOnlyDictionary<int, int> result = await Load(
                engine,
                asynchronous,
                requested,
                Loader,
                cancellationToken
            )
            .WaitAsync(Watchdog, cancellationToken);
        result.Should().Equal(expected);
        bulkCalls.Should().Be(1);
        engine.EstimatedCount.Should().Be(4096);
        IReadOnlyDictionary<int, int> cached = await Load(
                engine,
                asynchronous,
                requested,
                Loader,
                cancellationToken
            )
            .WaitAsync(Watchdog, cancellationToken);
        cached.Should().Equal(expected);
        bulkCalls.Should().Be(1);
        engine.AssertInvariants();
        return;

        async Task<IReadOnlyDictionary<int, int>> Loader(
            IReadOnlyCollection<int> keys,
            CancellationToken token
        )
        {
            keys.Should().Equal(requested);
            bulkCalls++;
            await Task.Yield();
            // Keep the engine identity: both calls await this loader before async scope cleanup.
            // ReSharper disable once AccessToDisposedClosure
            LoadChainContext.Contains(engine, 1).Should().BeTrue();
            // Keep the engine identity: both calls await this loader before async scope cleanup.
            // ReSharper disable once AccessToDisposedClosure
            LoadChainContext.Contains(engine, 2048).Should().BeTrue();
            // Keep the engine identity: both calls await this loader before async scope cleanup.
            // ReSharper disable once AccessToDisposedClosure
            LoadChainContext.Contains(engine, 4095).Should().BeTrue();
            token.ThrowIfCancellationRequested();
            return expected;
        }
    }

    private static CacheEngine<TKey, TValue> CreateEngine<TKey, TValue>(
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull
        where TValue : notnull =>
        new(
            new CacheEngineOptions<TKey, TValue>
            {
                MaximumSize = 8192,
                MaxConcurrentLoads = 4,
                MaxPendingLoadKeys = 8192,
                MaximumBulkKeys = 8192,
                Comparer = comparer,
            }
        );

    private static Task<IReadOnlyDictionary<TKey, TValue>> Load<TKey, TValue>(
        CacheEngine<TKey, TValue> engine,
        bool asynchronous,
        IEnumerable<TKey> keys,
        Func<
            IReadOnlyCollection<TKey>,
            CancellationToken,
            Task<IReadOnlyDictionary<TKey, TValue>>
        > loader,
        CancellationToken cancellationToken
    )
        where TKey : notnull
        where TValue : notnull =>
        asynchronous
            ? engine
                .GetAllAsync(
                    static (_, _) => throw new InvalidOperationException("Unexpected single load."),
                    null,
                    loader,
                    keys,
                    cancellationToken
                )
                .AsTask()
            : Task.Run(
                () =>
                    engine.GetAll(
                        keys,
                        static _ => throw new InvalidOperationException("Unexpected single load."),
                        null,
                        requested => loader(requested, cancellationToken).GetAwaiter().GetResult()
                    ),
                cancellationToken
            );
}
