using System.Collections;
using FluentAssertions;

namespace LoadingCache.Tests;

public sealed class BulkAllocationSemanticsTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SyncBulkResidentTaskViewsHaveStableIdentity(
        CancellationToken cancellationToken
    )
    {
        using var engine = CreateEngine<int, object>();
        object first = new();
        object second = new();
        engine.GetAll(
            [1, 2],
            static _ => throw new InvalidOperationException("Unexpected single load."),
            null,
            _ => new Dictionary<int, object> { [1] = first, [2] = second }
        );

        Task<object>[] views = new Task<object>[8];
        Parallel.For(
            0,
            views.Length,
            index =>
            {
                engine.TryGetTask(2, out Task<object>? task).Should().BeTrue();
                views[index] = task!;
            }
        );
        foreach (Task<object> view in views)
        {
            view.Should().BeSameAs(views[0]);
            (await view.WaitAsync(Watchdog, cancellationToken)).Should().BeSameAs(second);
        }
        engine.TryGetTask(1, out Task<object>? firstView).Should().BeTrue();
        (await firstView!.WaitAsync(Watchdog, cancellationToken)).Should().BeSameAs(first);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BulkNonLeaderReentrancyFlowsAcrossTaskRun(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        using var engine = CreateEngine<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<IReadOnlyDictionary<string, string>> Loader(
            IReadOnlyCollection<string> keys,
            CancellationToken token
        )
        {
            await Task.Yield();
            await Task.Run(
                async () =>
                {
                    // A broken chain must fail here before joining its own pending flight.
                    LoadChainContext.Contains(engine, "B").Should().BeTrue();
                    if (asynchronous)
                    {
                        await engine.GetAsync("B", static (key, _) => Task.FromResult(key), token);
                    }
                    else
                    {
                        engine.GetOrAdd("B", static key => key, token);
                    }
                },
                token
            );
            return keys.ToDictionary(static key => key, static key => key);
        }

        Task<IReadOnlyDictionary<string, string>> operation = Task.Run(
            async () =>
                asynchronous
                    ? await engine.GetAllAsync(
                        static (key, _) => Task.FromResult(key),
                        null,
                        Loader,
                        ["a", "b"],
                        cancellationToken
                    )
                    : engine.GetAll(
                        ["a", "b"],
                        static key => key,
                        null,
                        keys => Loader(keys, cancellationToken).GetAwaiter().GetResult()
                    ),
            cancellationToken
        );
        await FluentActions
            .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
            .Should()
            .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        engine.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    public async Task NestedBulkCachesRetainTheOuterLogicalChain(
        CancellationToken cancellationToken
    )
    {
        using var outer = CreateEngine<string, string>(StringComparer.OrdinalIgnoreCase);
        using var inner = CreateEngine<string, string>(StringComparer.OrdinalIgnoreCase);
        LoadChainContext.Node? ambient = LoadChainContext.Current;
        async Task<IReadOnlyDictionary<string, string>> InnerLoader(
            IReadOnlyCollection<string> keys,
            CancellationToken token
        )
        {
            await Task.Yield();
            LoadChainContext.Contains(outer, "B").Should().BeTrue();
            await FluentActions
                .Awaiting(() =>
                    outer.GetAsync("B", static (key, _) => Task.FromResult(key), token).AsTask()
                )
                .Should()
                .ThrowExactlyAsync<LoadingCacheReentrancyException>();
            return keys.ToDictionary(static key => key, static key => key);
        }

        IReadOnlyDictionary<string, string> result = await outer
            .GetAllAsync(
                static (key, _) => Task.FromResult(key),
                null,
                async (keys, token) =>
                {
                    IReadOnlyDictionary<string, string> nested = await inner.GetAllAsync(
                        static (key, _) => Task.FromResult(key),
                        null,
                        InnerLoader,
                        ["b", "c"],
                        token
                    );
                    nested.Keys.Should().BeEquivalentTo("b", "c");
                    LoadChainContext.Contains(outer, "B").Should().BeTrue();
                    LoadChainContext.Contains(inner, "b").Should().BeFalse();
                    return keys.ToDictionary(static key => key, static key => key);
                },
                ["a", "b"],
                cancellationToken
            )
            .AsTask()
            .WaitAsync(Watchdog, cancellationToken);
        result.Keys.Should().BeEquivalentTo("a", "b");
        LoadChainContext.Current.Should().BeSameAs(ambient);
        outer.AssertInvariants();
        inner.AssertInvariants();
    }

    [Test]
    public void PartialBulkChainFailureRestoresTheSavedAmbientChain()
    {
        var comparer = new FailingChainComparer();
        using var engine = CreateEngine<int, int>(comparer);
        LoadChainContext.Node? previous = LoadChainContext.Current;
        var ambient = new LoadChainContext.Node(new WeakReference<object>(engine), 99, previous);
        int loaderCalls = 0;
        IReadOnlyDictionary<int, int> Loader(IReadOnlyCollection<int> keys)
        {
            loaderCalls++;
            return keys.ToDictionary(static key => key, static key => key);
        }

        LoadChainContext.Current = ambient;
        try
        {
            Action load = () => engine.GetAll([1, 2, 3], static key => key, null, Loader);
            load.Should().ThrowExactly<ControlledEnumerationFailure>();
            comparer.AncestorFailures.Should().Be(1);
            loaderCalls.Should().Be(0);
            LoadChainContext.Current.Should().BeSameAs(ambient);
            engine.EstimatedCount.Should().Be(0);
            engine.AssertInvariants();

            comparer.Fail = false;
            engine.GetAll([1, 2, 3], static key => key, null, Loader).Should().HaveCount(3);
            loaderCalls.Should().Be(1);
            LoadChainContext.Current.Should().BeSameAs(ambient);
        }
        finally
        {
            LoadChainContext.Current = previous;
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BulkResultsUseOneEnumerationWithoutCountMetadata(
        bool asynchronous,
        bool failMidway,
        CancellationToken cancellationToken
    )
    {
        // A large configured maximum is not a capacity hint for a two-key result.
        using var engine = CreateEngine<int, int>(bulkLimit: int.MaxValue);
        var source = new GuardedResult([new(1, 10), new(2, 20)], failMidway);
        Task<IReadOnlyDictionary<int, int>> operation = Load(
            engine,
            asynchronous,
            [1, 2],
            _ => source,
            cancellationToken
        );
        if (failMidway)
        {
            await FluentActions
                .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
                .Should()
                .ThrowExactlyAsync<ControlledEnumerationFailure>();
            engine.EstimatedCount.Should().Be(0);
        }
        else
        {
            (await operation.WaitAsync(Watchdog, cancellationToken))
                .Should()
                .Equal(new Dictionary<int, int> { [1] = 10, [2] = 20 });
        }
        source.Enumerations.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HitHeavyBulkOnlyOwnsItsColdSubset(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        using var engine = CreateEngine<int, int>(bulkLimit: int.MaxValue);
        for (int key = 0; key < 63; key++)
        {
            engine.Put(key, key * 10);
        }
        int calls = 0;
        IReadOnlyDictionary<int, int> result = await Load(
                engine,
                asynchronous,
                Enumerable.Range(0, 64),
                keys =>
                {
                    keys.Should().Equal(63);
                    calls++;
                    return new Dictionary<int, int> { [63] = 630 };
                },
                cancellationToken
            )
            .WaitAsync(Watchdog, cancellationToken);
        result.Should().Equal(Enumerable.Range(0, 64).ToDictionary(key => key, key => key * 10));
        calls.Should().Be(1);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingInputIsNotCountedOrLoadedTwice(
        bool asynchronous,
        CancellationToken cancellationToken
    )
    {
        using var engine = CreateEngine<int, int>();
        var keys = new ThrowingKeys();
        int calls = 0;
        Task<IReadOnlyDictionary<int, int>> operation = Load(
            engine,
            asynchronous,
            keys,
            _ =>
            {
                calls++;
                return new Dictionary<int, int>();
            },
            cancellationToken
        );
        await FluentActions
            .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
            .Should()
            .ThrowExactlyAsync<ControlledEnumerationFailure>();
        keys.Enumerations.Should().Be(1);
        calls.Should().Be(0);
        engine.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DuplicateOrOversizeOutputCannotPublishAnyKey(
        bool asynchronous,
        bool duplicate,
        CancellationToken cancellationToken
    )
    {
        using var engine = CreateEngine<int, int>(bulkLimit: 2);
        var source = new GuardedResult(
            duplicate ? [new(1, 10), new(1, 20)] : [new(1, 10), new(2, 20), new(3, 30)]
        );
        Task<IReadOnlyDictionary<int, int>> operation = Load(
            engine,
            asynchronous,
            [1, 2],
            _ => source,
            cancellationToken
        );
        await FluentActions
            .Awaiting(() => operation.WaitAsync(Watchdog, cancellationToken))
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();
        engine.EstimatedCount.Should().Be(0);
        source.Enumerations.Should().Be(1);
        engine.AssertInvariants();
    }

    private static CacheEngine<TKey, TValue> CreateEngine<TKey, TValue>(
        IEqualityComparer<TKey>? comparer = null,
        int bulkLimit = 128
    )
        where TKey : notnull
        where TValue : notnull =>
        new(
            new CacheEngineOptions<TKey, TValue>
            {
                MaximumSize = 128,
                MaxConcurrentLoads = 4,
                MaxPendingLoadKeys = bulkLimit,
                MaximumBulkKeys = bulkLimit,
                Comparer = comparer,
            }
        );

    private static Task<IReadOnlyDictionary<int, int>> Load(
        CacheEngine<int, int> engine,
        bool asynchronous,
        IEnumerable<int> keys,
        Func<IReadOnlyCollection<int>, IReadOnlyDictionary<int, int>> loader,
        CancellationToken cancellationToken
    ) =>
        asynchronous
            ? engine
                .GetAllAsync(
                    static (_, _) => throw new InvalidOperationException("Unexpected single load."),
                    null,
                    (requested, _) => Task.FromResult(loader(requested)),
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
                        loader
                    ),
                cancellationToken
            );

    private sealed class FailingChainComparer : IEqualityComparer<int>
    {
        internal bool Fail = true;
        internal int AncestorFailures;
        private bool _firstSiblingChecked;

        public bool Equals(int first, int second)
        {
            // The owner is absent during initial request validation. Arm only once
            // chain construction checks its first sibling, then fail on an ancestor.
            if (first == 1 && second == 2)
            {
                _firstSiblingChecked = true;
            }

            if (Fail && _firstSiblingChecked && first == 99 && second == 3)
            {
                AncestorFailures++;
                throw new ControlledEnumerationFailure();
            }

            return first == second;
        }

        public int GetHashCode(int value) => value;
    }

    private sealed class ThrowingKeys : IReadOnlyCollection<int>
    {
        internal int Enumerations;
        public int Count => throw new InvalidOperationException("Do not read untrusted Count.");

        public IEnumerator<int> GetEnumerator()
        {
            Enumerations++;
            yield return 1;
            throw new ControlledEnumerationFailure();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GuardedResult(KeyValuePair<int, int>[] values, bool failMidway = false)
        : IReadOnlyDictionary<int, int>
    {
        internal int Enumerations;
        public int Count => throw new InvalidOperationException("Do not read untrusted Count.");
        public IEnumerable<int> Keys => throw new NotSupportedException();
        public IEnumerable<int> Values => throw new NotSupportedException();
        public int this[int key] => throw new NotSupportedException();

        public bool ContainsKey(int key) => throw new NotSupportedException();

        public bool TryGetValue(int key, out int value) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
        {
            if (++Enumerations != 1)
            {
                throw new InvalidOperationException("The result was enumerated twice.");
            }
            for (int index = 0; index < values.Length; index++)
            {
                if (failMidway && index == 1)
                {
                    throw new ControlledEnumerationFailure();
                }
                yield return values[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ControlledEnumerationFailure : Exception;
}
