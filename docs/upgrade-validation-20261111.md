# SDK 11 and dependency upgrade validation

Base: `08fe273944d2f9882bdd36bdff83884c3485a1f7`; uncommitted work on
`fix/upgrade-compatibility`. Local platform: macOS ARM64.

## Changes and reproduced failures

- SDK 11.0.100: reproduced CA2027 in TimeoutAdversarialTests on net8.0 and
  net10.0. The completion-only watchdog now cancels its delay in finally,
  preserving observation of faulted/cancelled operations by the calling test.
- CSharpier 1.4.0: reproduced 53 unformatted C# files and applied the formatter.
  Production C# changes are formatting only.
- TUnit 1.83.0: reproduced the missing Microsoft.Bcl.AsyncInterfaces 9.0.0
  assembly in the .NET 8 DI test host before any tests ran. Its netstandard2.0
  engine dependency group omits this dependency. An explicit private reference
  on net8.0 fixes startup for all three test projects.
- Microsoft.Extensions 11.0.0: reproduced 27 net8.0 support warnings. Select
  10.0.15 (TimeProvider.Testing 10.11.0) on net8.0, and 11.0.0 on newer targets.
  Inspected packed DI metadata: its net8.0 dependency is DI.Abstractions 10.0.15.
- Added net11.0 to core/DI/stress tests, allocation children, source/package
  consumers and correctness CI. Core remains one net8.0 asset.
- Build correctness and isolated package consumers with `-warnaserror`, including
  MSBuild package-support warnings. Preserve 10.x runtime acquisition in CI;
  global.json independently selects SDK 11.

## Executed validation

Temporary SDK/runtime installation: `/tmp/loadingcache-upgrade-dotnet`; no global
SDK/tool configuration was changed. Commands used that directory on PATH and as
DOTNET_ROOT.

- `dotnet restore LoadingCache.slnx`: passed.
- `dotnet build LoadingCache.slnx -c Release --no-restore -warnaserror`: passed,
  zero warnings/errors.
- `dotnet test --project tests/<project>/<project>.csproj -c Release --no-build
  --no-restore -f <tfm> --report-trx`: core 905, DI 10, short stress 10 passed
  on each actual runtime 8.0.34, 10.0.15 and 11.0.0; no failures/skips.
- Source consumer: passed on all three actual runtimes.
- `uv run --no-project python tools/pack-release.py --local-validation --output
  artifacts/nuget-upgrade`: passed.
- `uv run --no-project python tools/package-consumers.py --feed
  artifacts/nuget-upgrade/packages --output artifacts/nuget-upgrade-consumers
  --core-id LoadingCache.LocalValidation --di-id
  LoadingCache.Extensions.DependencyInjection.LocalValidation --version
  0.1.0-alpha.localvalidation`: all six core/DI consumers passed; archive hashes
  matched and sources remained unchanged during validation.
- API baseline self-test and comparison: passed, using the exact ci.yml commands.
- ASP.NET Core, gRPC and Worker smoke: passed. Sandbox execution stalled before
  host startup; the same binaries passed with loopback access and 30-second
  process deadlines outside the sandbox.
- BenchmarkDotNet LookupBenchmarks Dry execution: 36 cases executed; smoke only,
  not performance qualification.
- Release-tool Python unit tests: 14 passed.
- CSharpier check: 190 files passed; changed Python passed Ruff lint/format.
- Renovate 44.228.0 strict config validation passed with the RegExp fallback
  (native RE2 unavailable). Its real NuGet extractor and package-rule matcher
  independently verified all eight conditional Microsoft.Extensions declarations
  and separate SDK/runtime restrictions. This does not claim a hosted bot run.

Local evidence: `/tmp/loadingcache-upgrade-*.log`,
`/tmp/loadingcache-tunit-before.log`, `/tmp/loadingcache-csharpier-before.log`,
`/tmp/loadingcache-upgrade-results/*.trx`, and this worktree's
`artifacts/nuget-upgrade{,-consumers}/` and `artifacts/api-baseline/check.json`.
These are local paths, not published artifacts.

## Pending

Hosted Windows/Linux/macOS CI for this patch: Not run. Native AOT, long endurance
and performance qualification: Not run. Hosted results are tracked in the
integration PR. The project remains experimental. Require all current-commit
CI jobs before merging or superseding the individual upgrade PRs.
