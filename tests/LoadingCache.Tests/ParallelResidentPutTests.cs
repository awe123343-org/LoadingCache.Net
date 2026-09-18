using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

[TestFixture]
public sealed class ParallelResidentPutTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [Parallelizable(ParallelScope.All)]
    public void StableReplacementUsesEntryOwnershipUnlessBulkRequiresCoordination(
        bool supportsBulk,
        bool statistics
    )
    {
        var probe = new CoordinationProbe();
        CacheBuilder<int, string> builder = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2);
        if (statistics)
            builder.RecordStatistics();
        using CacheEngine<int, string> engine = builder.CreateEngine(
            new LoadingCacheTestHooks { BeforeResidentValuePublished = probe.Observe },
            supportsBulkLoading: supportsBulk
        );
        probe.Engine = engine;
        engine.Put(1, "old");
        engine.CleanUp();

        engine.Put(1, "new");

        probe.Calls.Should().Be(1);
        probe.CoordinationHeld.Should().Be(supportsBulk);
        engine.TryGet(1, out string? value).Should().BeTrue();
        value.Should().Be("new");
        engine.GetStatistics().ReplacedRemovals.Should().Be(statistics ? 1 : 0);
        engine.AssertInvariants();
    }

    [TestCase(false)]
    [TestCase(true)]
    [Parallelizable(ParallelScope.All)]
    public async Task OptOutCannotInstallAnActualBulkLoader(bool asynchronous)
    {
        await using CacheEngine<int, string> engine = CacheBuilder
            .Create<int, string>()
            .MaximumSize(8)
            .MaxConcurrentLoads(2)
            .CreateEngine(supportsBulkLoading: false);
        if (asynchronous)
        {
            await engine
                .Awaiting(static current =>
                    current
                        .GetAllAsync(
                            static (_, _) =>
                                throw new AssertionException("Single loader must not execute."),
                            null,
                            static (_, _) =>
                                throw new AssertionException("Bulk loader must not execute."),
                            [1],
                            CancellationToken.None
                        )
                        .AsTask()
                )
                .Should()
                .ThrowExactlyAsync<InvalidOperationException>();
        }
        else
        {
            engine
                .Invoking(static current =>
                    current.GetAll(
                        [1],
                        static _ => throw new AssertionException("Single loader must not execute."),
                        null,
                        static _ => throw new AssertionException("Bulk loader must not execute.")
                    )
                )
                .Should()
                .ThrowExactly<InvalidOperationException>();
        }
        engine.EstimatedCount.Should().Be(0);
        engine.AssertInvariants();
    }

    private sealed class CoordinationProbe
    {
        internal CacheEngine<int, string> Engine { get; set; } = null!;
        internal int Calls { get; private set; }
        internal bool CoordinationHeld { get; private set; }

        internal void Observe()
        {
            Calls++;
            CoordinationHeld = Engine.IsCoordinationLockHeldForTesting;
        }
    }
}
