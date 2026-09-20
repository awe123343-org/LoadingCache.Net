# Contributing

Renovate proposes dependency updates through pull requests using `renovate.json`.
It tracks GitHub Actions (with commit SHA pins), NuGet packages and tools, the
.NET SDK, and Node/pnpm tooling. Updates require review and the existing CI;
automerge is disabled. Node major upgrades are allowed. Enable the
[Renovate GitHub App](https://github.com/apps/renovate) for this repository to
activate the hosted service. Renovate does not change the release `VERSION`.

Read the [semantics](docs/semantics.md), [concurrency design](docs/concurrency.md)
and [feature matrix](docs/feature-matrix.md) before changing behaviour.
The API is experimental and has not been frozen.

Set up the [formatting hooks](docs/formatting.md) with `make install-tools` and
`make install-hooks`. Fast formatters run on pre-commit and in the `format` CI
job. We intentionally leave automatic unused C# usings checks to CI rather than
local Git hooks because full-solution analysis takes about 11 seconds. Local
builds still enforce IDE0005, and manual checks remain available. Run
`make format-dotnet-style` to remove them automatically.

Run from the repository root:

```sh
dotnet restore LoadingCache.slnx
dotnet tool restore
dotnet csharpier check .
dotnet build LoadingCache.slnx -c Release --no-restore
for framework in net8.0 net10.0; do
    for project in LoadingCache.Tests LoadingCache.DependencyInjection.Tests LoadingCache.StressTests; do
        dotnet test "tests/$project/$project.csproj" -c Release --no-build --no-restore -f "$framework"
    done
    dotnet run --project tests/LoadingCache.ConsumerSmoke/LoadingCache.ConsumerSmoke.csproj -c Release --no-build --no-restore -f "$framework"
done
```

Include validation commands, actual results and the environment in your pull request. A command
listed here is a workflow, not a claim that a platform has passed it.

Development uses the SDK in `global.json`, while the library targets .NET 8.
Install the .NET 8 and .NET 10 runtimes through your normal environment setup to
run both targets. A local runtime can also be selected using VSTest's
`RunConfiguration.DotNetHostPath`, using the path to your installed runtime host. Do not roll .NET 8 tests forward to .NET 10 and
report that as .NET 8 validation.

Use NUnit for test discovery and FluentAssertions for assertions. Preserve exact
exception-type assertions and controlled interleavings when migrating tests.
Core and DI unit-test assemblies use method-level parallel execution with
`ParallelScope.Children` and `FixtureLifeCycle(LifeCycle.InstancePerTestCase)`.
Keep mutable test state local to each case; instance isolation does not protect
static state or shared external resources. NUnit selects its default worker count.
See the [NUnit parallel execution contract](https://docs.nunit.org/articles/nunit/technical-notes/usage/Framework-Parallel-Test-Execution.html)
and [fixture lifecycle contract](https://docs.nunit.org/articles/nunit/writing-tests/attributes/fixturelifecycle.html).
Run `dotnet csharpier format .` to apply formatting and `dotnet csharpier check .`
to verify it. Restore the pinned local tool; do not install a global formatter.

Use controlled gates and a fake monotonic time provider for concurrency and
expiration tests. Watchdog timeouts prevent hangs; sleeps must not establish test
ordering. Preserve prior race regressions when optimizing. Keep nullable analysis,
XML documentation and warnings-as-errors enabled; fix the cause of warnings.

Changes to ownership, cancellation or resource bounds require an ADR and a
counterexample demonstrating the changed behaviour. Do not add speculative public
API, sync-over-async or unbounded queues. Any source/test adaptation must update
`docs/upstream-map.md` with an immutable revision and retained license notices.

Only stage explicitly intended files. Publishing, tagging and remote changes
require maintainer authorization. The [NuGet workflow](docs/nuget-publishing.md)
is manually dispatched and defaults to an artifact preview; publishing requires
explicit opt-in and configured maintainer credentials and policy.
