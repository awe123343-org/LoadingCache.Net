using FluentAssertions;
using FluentAssertions.Execution;

namespace LoadingCache.Tests;

public sealed class ParallelResidentPutTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
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

    [Test]
    [Arguments(false)]
    [Arguments(true)]
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
                                throw new AssertionFailedException(
                                    "Single loader must not execute."
                                ),
                            null,
                            static (_, _) =>
                                throw new AssertionFailedException("Bulk loader must not execute."),
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
                        static _ =>
                            throw new AssertionFailedException("Single loader must not execute."),
                        null,
                        static _ =>
                            throw new AssertionFailedException("Bulk loader must not execute.")
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
