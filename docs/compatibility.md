# Compatibility policy

The minimum consumer runtime is .NET 8. The core produces a single `net8.0`
assembly. Tests and the source consumer target `net8.0`, `net10.0` and `net11.0`; actual
runtime and platform results are recorded in [release readiness](release-readiness.md).
The repository build uses the .NET 11 SDK pinned in `global.json` and C# 12.

The same managed NuGet package serves .NET 8, .NET 10 and .NET 11 applications; consumers
do not need a runtime-specific package. Core and the optional DI integration are
separate packages. Normal package CI runs on Windows and Linux with all three runtimes.
As agreed on 2026-09-18, Native AOT platform qualification is tracked separately
from the initial managed NuGet release. Existing AOT analyzers and smoke samples
remain; untested AOT platforms are not advertised as verified.

The API is experimental and may change before a stable release. The
[public API baseline and versioning policy](api-compatibility.md) define review
and mechanical checks; they do not certify V1 compatibility or readiness.
Before 1.0, compile all examples, test the packed consumer and establish the
supported OS/architecture/runtime matrices on the final candidate.

The explicit .NET 8 requirement supersedes the original commission's default.
Before release, reassess the runtime lifecycle and consumer migration needs as
described in [ADR-0001](adr/0001-framework-and-tooling.md). Do not silently raise
the minimum target or equate runtime binary compatibility with Microsoft's
servicing support. Release verification must cover current supported patches.

Local ARM64 correctness tests do not establish ARM64 stress, x64, Windows/Linux,
trimming or Native AOT support. AOT requires actual publish-and-run evidence.

## Dependency upgrades

The SDK version does not change the minimum consumer runtime. Microsoft.Extensions
10.x is selected for net8.0; newer targets use 11.x. The published net8.0 DI asset
therefore retains a supported dependency group. Renovate keeps only the legacy
10.x package declarations below 11; the newer declarations upgrade independently.
The setup-dotnet 10.x entries install a consumer runtime, independently of the
SDK selected by global.json, and must stay on that runtime line.

TUnit 1.83.0 selects its netstandard2.0 engine on net8.0. That assembly requires
Microsoft.Bcl.AsyncInterfaces 9.0.0, which its NuGet dependency group omits.
The three test projects import `tests/TUnit.Net8Compatibility.props`, which adds
10.0.15 through a private net8.0-only PackageReference with VersionOverride.
The override is independent of any future normal centrally managed reference.
Renovate's below-11 cap matches only this workaround file; references elsewhere
remain independently upgradeable. Version 11 no longer supports net8.0.
Once upstream fixes its dependency metadata and .NET 8 tests pass without the
workaround, remove the imports, workaround file and file-scoped Renovate rule.
Keep any independently required references and their own compatibility policy.

Correctness and package-consumer builds use -warnaserror so MSBuild dependency
support warnings fail CI as well as compiler/analyzer warnings. No support-warning
suppression is used. New hosted .NET 11 coverage must pass before claiming Windows
or Linux verification; Native AOT qualification remains separate.
