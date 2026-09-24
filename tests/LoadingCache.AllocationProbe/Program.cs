using System.Text.Json;
using FluentAssertions;

namespace LoadingCache.AllocationProbe;

/// <summary>Process entry point for isolated allocation measurements.</summary>
public static class Program
{
    private const long AllocationBudget = 64 * 1024 * 1024;

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["--allocation-check", var scenario])
        {
            return 2;
        }

        // Keep stdout a JSON protocol; assertion-library diagnostics belong on stderr.
        TextWriter reportOutput = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            if (!GC.TryStartNoGCRegion(AllocationBudget))
                throw new InvalidOperationException(
                    "Could not reserve the allocation measurement's NoGCRegion."
                );
            try
            {
                switch (scenario)
                {
                    case "counter":
                        AllocationScenarios.HotCounterUpdatesDoNotAllocatePerEvent();
                        break;
                    case "weak-comparer":
                        AllocationScenarios.WeakKeyObjectComparerDoesNotAllocateDuringRawLookupComparison();
                        break;
                    case "weak-hit":
                        await AllocationScenarios.WeakKeyResidentHitDoesNotAllocateLookupProbe();
                        break;
                    case "known-allocation":
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        byte[] value = new byte[1_024];
                        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                        GC.KeepAlive(value);
                        delta.Should().Be(0, "the negative control must reject a real allocation");
                        break;
                    case "estimated-count":
                        AllocationScenarios.EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry();
                        break;
                    case "resident-put":
                        AllocationScenarios.RepeatedResidentPutHasBoundedAllocation();
                        break;
                    case "allocating-comparer":
                        AllocationScenarios.RawComparisonMeasurementDetectsAllocatingComparer();
                        break;
                    case "forced-collection":
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                        break;
                    default:
                        throw new ArgumentException("Unknown allocation scenario.");
                }
            }
            finally
            {
                // Exhaustion or a forced collection is a failed measurement, never a retry/skip.
                GC.EndNoGCRegion();
            }

            await reportOutput.WriteLineAsync(
                JsonSerializer.Serialize(new Report(scenario, Environment.Version.ToString(), true))
            );
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            await reportOutput.WriteLineAsync(
                JsonSerializer.Serialize(
                    new Report(scenario, Environment.Version.ToString(), false)
                )
            );
            return 1;
        }
    }

    private sealed record Report(string Scenario, string Runtime, bool Success);
}
