# Release readiness

**Managed prereleases are published; stable 1.0 is not yet qualified.** Feature implementation, source-specific verification and release approval are separate. The initial managed NuGet route does not require completing every Native AOT platform combination.

## Published artifacts

[LoadingCache.Net](https://www.nuget.org/packages/LoadingCache.Net/0.1.0-alpha.1.2) and
[LoadingCache.Net.Extensions.DependencyInjection](https://www.nuget.org/packages/LoadingCache.Net.Extensions.DependencyInjection/0.1.0-alpha.1.2)
version **0.1.0-alpha.1.2** were published by [GitHub run 35413554855, attempt 2](https://github.com/awe123343/LoadingCache.Net/actions/runs/35413554855), from original revision `5f616a4fe488318554bf0c6429a914abb64958cd`.

Verification, package consumers, immutable manifest checks, OIDC login and both package/symbol pushes succeeded. Downloaded public nupkg entry payloads matched CI artifacts byte-for-byte, excluding NuGet's added `.signature.p7s`; signature presence, not independent cryptographic trust, was checked. Original published revisions remain historical evidence, not aliases for subsequently amended documentation commits.

The [publishing workflow](nuget-publishing.md) publishes official versions when `VERSION` changes on main, and alpha snapshots through manual dispatch. Both paths require the shared correctness and package-consumer gates. Initial official version 0.1.0 is user-authorised; this is not a claim that all 1.0 gates have passed.

## Verified scope

| Area                    | Executed evidence and limits                                                                                                                                                                                                                                                              |
| ----------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Windows/Linux x64 CI    | Each OS: 1,534 tests (747 core + 10 DI + 10 short stress, on both actual runtimes 8.0.31/10.0.12), zero failed/skipped; 3,068 total. Source/host consumers and API baseline checks passed.                                                                                                |
| Hosted packages         | Core/DI package-only consumers run on .NET 8/10 on both OSes, with exact archive/dependency checks. Formatting and BenchmarkDotNet Dry smoke passed; Dry is not a throughput result.                                                                                                      |
| macOS ARM64 correctness | Current opt-in source: 1,534 tests, source consumers, Release/API/CSharpier checks passed.                                                                                                                                                                                                |
| Linux ARM64 correctness | Same source inventory: 1,534 tests, two source consumers, four Release builds without warnings/errors; independent output/runtime identities. No new Linux long run in this batch.                                                                                                        |
| macOS ARM64 stability   | Twelve ≥1,800-second soaks plus one continuous 28,800.0301995-second endurance, 18,030,661,120 operations, source/build/runtime linkage verified. Explicit C/F profiles; not unlimited-default retention proof. [Detailed results](v1-stability-results-20260918.md).                     |
| Loading service         | Unconfigured normal admission: 72,000 measured + 4,800 warmup requests succeeded; measured p99 28.374–42.506 ms, maximum 70.463 ms. Explicit-limit overload arm separately verified expected rejection/timeout and recovery. This closes that profile, not all service/performance gates. |
| Local package/AOT       | Four real package consumers and eight core/DI × 8/10 × trim/native jobs passed on macOS ARM64. Two net8 linker notices and incomplete preservation of six early full assets JSON files remain documented. [Scope](package-and-aot.md).                                                    |
| IDE inspections         | CLI solution-wide Debug/Release HINT analysis: 23 projects/151 authored C# files, zero errors/warnings/capture findings; 18 reviewed style/Debug hints retained without lowering severity. Not equivalent to live Rider Grazie/HeapView clearance.                                        |
| Performance comparisons | Historical source-bound Caffeine, Guava and latest retained MemoryCache matrices; wins, losses and noise retained. [Methods/results](benchmark-methodology.md). No per-cell superiority requirement.                                                                                      |

Most large local test/qualification artifacts are intentionally not committed. Their recorded paths/hashes identify local evidence, not publicly downloadable raw data. Curated benchmark archives retain their own provenance and negative outcomes. A test count alone does not establish feature or platform completeness.

## Remaining gates and limitations

- **Resident service precision:** the predeclared 5% CPU/p99 gate remains **Inconclusive**. The bounded control-only diagnostic reproduced substantial A/A tail variation without a cache. It neither proves a cache regression nor waives the gate. [Diagnostic](v1-service-precision-diagnostic-20260918.md).
- **Final 1.0 review:** freeze/review the public API and behaviour, complete provenance/dependency/security review and confirm a security contact. Experimental prerelease availability is not sign-off.
- **Source Link:** configuration and symbols are present; actual remote URL retrieval still needs direct verification.
- **Platform/scale:** hosted x64 correctness does not replace long endurance/high-core contention or each architecture/feature combination. AOT claims remain limited to real publish/run evidence.
- **Policy/retention:** existing traces and controlled roots provide evidence, not universal adaptive-policy superiority or leak/race freedom. Preserve source/config/runtime attribution when validating future changes.
- **Live Rider:** previous MCP/Trust-modal state prevented a trustworthy full live result; a blank Problems body with a stale badge is not proof of clearance. CLI findings are reported separately.
- **Semantic limits:** async weak values, JVM soft values and ordinary raw-value automatic disposal are not offered. Owned manual leases and memory-pressure eviction have separate contracts. See [feature matrix](feature-matrix.md) and [Caffeine differences](caffeine-differences.md).

## Historical failures retained

The older build03 endurance workload passed, but its bridge/sequence failed when one `project.assets.json` changed; it is not substitute evidence for the new source. The new isolated-source run separately passed terminal workload and linkage checks.

Old limit-eight HTTP normal-load profiles each had a rejection and remain Failed under that original contract. Admission subsequently became explicitly opt-in; the new unconfigured/default and explicit-overload profiles are separate evidence, not relabelled old results. Earlier sandbox HTTP initialisation failure remains an environment failure, not a successful backend run.

The initial hosted Windows failure was LF/checkout handling and was fixed in the workflow lineage before the successful runs. Earlier probe cleanup/schema failures and discarded micro-optimisations do not count towards retained performance summaries. Original raw/commits are preserved locally rather than expanded into thousands of redundant tracked snapshots.

## Release decision

Use the fixed [performance gates](v1-performance-gates.md), [acceptance matrix](acceptance-matrix.md), package checks and source-bound evidence. Fix known contract/resource failures before stable release. Do not silently move budgets, count interrupted runs cumulatively, extrapolate aggregate ns/op to p99, or infer Passed from exit code/configuration alone. Internal optimisation within preserved contracts can continue after release; winning every competitor microbenchmark is not a 1.0 gate.

## macOS hosted CI

The correctness matrix includes the standard `macos-15` ARM64 runner, with the same .NET 8/10 core, DI, short stress, source/package consumer, host smoke and API baseline checks as Windows/Linux. Hosted results are pending the first run; this does not claim macOS Intel or Native AOT qualification.
