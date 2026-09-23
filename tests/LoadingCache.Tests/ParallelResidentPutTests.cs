using TUnit.Assertions.Exceptions;

namespace LoadingCache.Tests;

public sealed class ParallelResidentPutTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task StableReplacementUsesEntryOwnershipUnlessBulkRequiresCoordination(
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
        await Assert.That(probe.Calls).IsEqualTo(1);
        await Assert.That(probe.CoordinationHeld).IsEqualTo(supportsBulk);
        await Assert.That(engine.TryGet(1, out string? value)).IsTrue();
        await Assert.That(value).IsEqualTo("new");
        await Assert.That(engine.GetStatistics().ReplacedRemovals).IsEqualTo(statistics ? 1 : 0);
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
            await Assert
                .That(
                    (Func<Task>)(
                        () =>
                            engine
                                .GetAllAsync(
                                    static (_, _) =>
                                        throw new AssertionException(
                                            "Single loader must not execute."
                                        ),
                                    null,
                                    static (_, _) =>
                                        throw new AssertionException(
                                            "Bulk loader must not execute."
                                        ),
                                    [1],
                                    CancellationToken.None
                                )
                                .AsTask()
                    )
                )
                .ThrowsExactly<InvalidOperationException>();
        }
        else
        {
            await Assert
                .That(() =>
                    engine.GetAll(
                        [1],
                        static _ => throw new AssertionException("Single loader must not execute."),
                        null,
                        static _ => throw new AssertionException("Bulk loader must not execute.")
                    )
                )
                .ThrowsExactly<InvalidOperationException>();
        }

        await Assert.That(engine.EstimatedCount).IsEqualTo(0);
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
