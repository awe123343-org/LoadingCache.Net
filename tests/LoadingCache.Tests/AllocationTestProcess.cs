using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;

namespace LoadingCache.Tests;

/// <summary>Runs exact allocation checks independently of the parallel suite's background GC.</summary>
internal static class AllocationTestProcess
{
    // Background GC discards unused allocation contexts and charges their remaining space to
    // GetAllocatedBytesForCurrentThread. This can happen inside an allocation-free loop.
    // A dedicated process lets us forbid GC during the sample without affecting other tests.
    // No process-wide JIT, tiering or PGO setting is changed. Individual measurement
    // kernels compile before their allocation baseline; zero-byte thresholds remain.
    internal static async Task VerifyAsync(string scenario)
    {
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
        start.ArgumentList.Add(typeof(AllocationProbe.Program).Assembly.Location);
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

    internal sealed record Result(int ExitCode, string Output, string Error);

    private sealed record Report(string Scenario, string Runtime, bool Success);
}

public sealed class AllocationMeasurementTests
{
    [Test]
    [Arguments("known-allocation", "negative control must reject a real allocation")]
    [Arguments("forced-collection", "NoGCRegion")]
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
