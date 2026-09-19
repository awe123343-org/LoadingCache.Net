using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace LoadingCache.Tests;

/// <summary>Runs exact allocation checks independently of the parallel suite's background GC.</summary>
internal static class AllocationTestProcess
{
    private const long AllocationBudget = 64 * 1024 * 1024;
    private static bool _isChild;

    // Background GC discards unused allocation contexts and charges their remaining space to
    // GetAllocatedBytesForCurrentThread. This can happen inside an allocation-free loop.
    // A dedicated process lets us forbid GC during the sample without affecting other tests.
    // No process-wide JIT, tiering or PGO setting is changed. Individual measurement
    // kernels compile before their allocation baseline; zero-byte thresholds remain.
    internal static async Task<bool> RunIsolatedIfNeededAsync(string scenario)
    {
        if (_isChild)
            return false;

        Result result = await RunAsync(scenario).ConfigureAwait(false);
        result
            .ExitCode.Should()
            .Be(0, "allocation child output: {0} {1}", result.Output, result.Error);
        Report report =
            JsonSerializer.Deserialize<Report>(result.Output.Trim())
            ?? throw new InvalidOperationException("Allocation child did not produce a report.");
        report.Scenario.Should().Be(scenario);
        report.Runtime.Should().Be(Environment.Version.ToString());
        report.Success.Should().BeTrue();
        return true;
    }

    internal static async Task<Result> RunAsync(string scenario)
    {
        // Select the muxer alongside the runtime actually hosting this test, not whichever SDK
        // happens to be on PATH. --fx-version also prevents roll-forward to a different patch.
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string host = Path.GetFullPath(
            Path.Combine(
                runtime,
                "..",
                "..",
                "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"
            )
        );
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(Environment.Version.ToString());
        start.ArgumentList.Add(typeof(AllocationTestProcess).Assembly.Location);
        start.ArgumentList.Add("--allocation-check");
        start.ArgumentList.Add(scenario);
        using Process process =
            Process.Start(start)
            ?? throw new InvalidOperationException("Could not start allocation child.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new Result(
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false)
            );
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Observe redirected stream cancellation as well as the process watchdog.
            try
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            throw new TimeoutException("Allocation child exceeded its 30-second watchdog.");
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (args is not ["--allocation-check", var scenario])
        {
            await Console.Error.WriteLineAsync(
                "Use dotnet test for the suite, or --allocation-check <scenario>."
            );
            return 2;
        }

        _isChild = true;
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
                        await new StripedCacheCountersTests()
                            .HotCounterUpdatesDoNotAllocatePerEvent()
                            .ConfigureAwait(false);
                        break;
                    case "weak-comparer":
                        await new ReferenceStorageTests()
                            .WeakKeyObjectComparerDoesNotAllocateDuringRawLookupComparison()
                            .ConfigureAwait(false);
                        break;
                    case "weak-hit":
                        await new WeakCacheTests()
                            .WeakKeyResidentHitDoesNotAllocateLookupProbe()
                            .ConfigureAwait(false);
                        break;
                    case "known-allocation":
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        byte[] value = new byte[1_024];
                        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                        GC.KeepAlive(value);
                        delta.Should().Be(0, "the negative control must reject a real allocation");
                        break;
                    case "estimated-count":
                        await new EstimatedCountTests()
                            .EstimatedCountDoesNotAllocateAValuesSnapshotPerEntry()
                            .ConfigureAwait(false);
                        break;
                    case "resident-put":
                        await new ResidentReplacementTests()
                            .RepeatedResidentPutHasBoundedAllocation()
                            .ConfigureAwait(false);
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
            reportOutput.WriteLine(
                JsonSerializer.Serialize(new Report(scenario, Environment.Version.ToString(), true))
            );
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            reportOutput.WriteLine(
                JsonSerializer.Serialize(
                    new Report(scenario, Environment.Version.ToString(), false)
                )
            );
            return 1;
        }
    }

    internal sealed record Result(int ExitCode, string Output, string Error);

    private sealed record Report(string Scenario, string Runtime, bool Success);
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class AllocationMeasurementTests
{
    [TestCase("known-allocation", "negative control must reject a real allocation")]
    [TestCase("forced-collection", "NoGCRegion")]
    public async Task IsolatedMeasurementRejectsAllocationOrInvalidRegion(
        string scenario,
        string error
    )
    {
        AllocationTestProcess.Result result = await AllocationTestProcess
            .RunAsync(scenario)
            .ConfigureAwait(false);
        result.ExitCode.Should().Be(1);
        result.Error.Should().Contain(error);
        using JsonDocument report = JsonDocument.Parse(result.Output);
        report.RootElement.GetProperty("Success").GetBoolean().Should().BeFalse();
    }
}
