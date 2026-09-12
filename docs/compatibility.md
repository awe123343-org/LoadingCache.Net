# Compatibility policy

The minimum consumer runtime is .NET 8. The core produces a single `net8.0`
assembly. Tests and the source consumer target `net8.0` and `net10.0`; actual
runtime and platform results are recorded in [release readiness](release-readiness.md).
The repository build uses the .NET 10 SDK pinned in `global.json` and C# 12.

The API is experimental and may change before a stable release. No binary or
source compatibility guarantee has been established. Before 1.0, baseline the
public API, compile all examples, test the packed consumer, establish supported
OS/architecture/runtime matrices, and define a documented versioning policy.

The explicit .NET 8 requirement supersedes the original commission's default.
Before release, reassess the runtime lifecycle and consumer migration needs as
described in [ADR-0001](adr/0001-framework-and-tooling.md). Do not silently raise
the minimum target or equate runtime binary compatibility with Microsoft's
servicing support. Release verification must cover current supported patches.

Local ARM64 correctness tests do not establish ARM64 stress, x64, Windows/Linux,
trimming or Native AOT support. AOT requires actual publish-and-run evidence.
