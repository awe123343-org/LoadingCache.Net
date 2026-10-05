# Release readiness

**The proposed 0.2.0 release remains experimental: no support SLA, and public APIs may still change.** Feature implementation, source-specific verification and release approval are separate. The final merge/publish decision belongs to the owner.

## Earlier published artifacts

[LoadingCache.Net](https://www.nuget.org/packages/LoadingCache.Net/0.1.0-alpha.1.2) and
[LoadingCache.Net.Extensions.DependencyInjection](https://www.nuget.org/packages/LoadingCache.Net.Extensions.DependencyInjection/0.1.0-alpha.1.2)
version **0.1.0-alpha.1.2** were published by [GitHub run 35413554855, attempt 2](https://github.com/awe123343/LoadingCache.Net/actions/runs/35413554855), from original revision `5f616a4fe488318554bf0c6429a914abb64958cd`.

Verification, package consumers, immutable manifest checks, OIDC login and both package/symbol pushes succeeded. Downloaded public nupkg entry payloads matched CI artifacts byte-for-byte, excluding NuGet's added `.signature.p7s`; signature presence, not independent cryptographic trust, was checked. Original published revisions remain historical evidence, not aliases for subsequently amended documentation commits.

The [publishing workflow](nuget-publishing.md) publishes official versions when `VERSION` changes on main, and alpha snapshots through manual dispatch. Both paths require the shared correctness and package-consumer gates. The proposed change is 0.1.0 → 0.2.0; a version number does not imply stable APIs or universal qualification.

## 0.2.0 qualification

Qualification source: `4939a4c2da0ab8f77981d00c54f764895861df5d`, tree
`370ef7c5481a5507a05799a5ccd4ddf924b5c01d`. P2/P3/local soaks/P5 executed on
`e65f6f62a98fa02bb4bd5bfb94df16740a0e1d62`; the owner subsequently changed one
comment word (`cleanup` → `clean-up`). Fresh deterministic Release net8 core/DI
builds have identical full IL (1,703/64 methods), so runtime evidence carries
forward. Remote Source Link verification was refreshed against pushed 4939a4c.
The older `v1-*` protocol names are historical filenames, not a 1.0 release claim.
Protocols, thresholds and qualification budgets were unchanged.

| Phase                    | Outcome and source-specific evidence                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| P1 API/behaviour         | PASS from a597845 carried through the dependency/workflow updates and behaviour-neutral Rider cleanup, then comment-only 4939a4c. The legacy non-generic factory/options removal is an intentional source/binary break from published 0.1.0 and 0.1.0-alpha.1.2 packages; generic synchronous loading remains. [Migration and timing contracts](semantics.md). No API stability promise.                                                                                                                                                |
| Rider cleanup            | Live MCP per-file union: 162/162 C# files across production, tests, benchmarks, tools and samples, zero remaining problems/suggestions or timeouts. InspectCode at SUGGESTION and HINT: zero. 778 reconciled rule findings: 52 fixed, 726 suppressed with concrete reasons at the matching scope. Release net8/net10 suites: 1,850 tests, zero failed; source/package/host consumers, CSharpier/prek and unchanged API baseline passed. Shipped net8 IL unchanged. [PR #53](https://github.com/awe123343-org/LoadingCache.Net/pull/53). |
| P2 packaging/AOT         | PASS: four package-only consumers (core/DI × net8/net10) and eight macOS ARM64 trim/native jobs. Both packaged DLL/PDB identities matched; renewed Source Link verification against 4939a4c fetched all 53 tracked documents (core 52, DI 1), HTTP 200, exact frozen bytes/PDB checksums, plus six checksum-valid embedded generated sources. Consumers/AOT were carried by identical shipped IL, not rerun. [Protocols](aot-validation.md).                                                                                            |
| P3 loading/admission     | PASS: all 12 formal arms on actual .NET 8.0.31/10.0.12, six diagnostic fault controls and both mutation self-checks. Normal measured p99 23.044–48.058 ms under the fixed 250 ms budget; configured admission, drain and recovery gates passed. Native AC, shared lock and pinned hosts verified. [Protocol](v1-loading-admission-profile.md).                                                                                                                                                                                          |
| P4 local soaks           | PASS: 12/12 independent ≥1,800-second soaks, six per runtime. Net8 retained from attempt-2; net10 from separately sealed attempt-3. Local endurance was aborted by owner decision after approximately 6,040 seconds, with no gate verdict; it is not an eight-hour pass. [Protocol](v1-endurance-profile.md).                                                                                                                                                                                                                           |
| P4 M1 Pro endurance      | PASS: separate M1 Pro macOS ARM64 run at 4939a4c on actual .NET 10.0.12. One continuous 28,800.0305-second process, 20,181,628,928 operations, 96 cycles; 92 retention samples, maximum growth 136,960 bytes, slope 14,321 bytes/hour. All correctness, retention, native power/lid/display and source/build/runtime linkage gates passed. Operation totals are descriptive, not an absolute throughput budget.                                                                                                                         |
| P5 resident service      | **Inconclusive.** Net8 completed 30/30 valid fresh hosts, but maximum A/A CPU/request and p99 differences were 5.894%/8.638%, above 5%. Net10 was not admitted: first no-cache A/A control host offered 60,000 measured requests, completed 59,960 and rejected 40 at the unchanged client `maxPending=256`; timeout/failed zero, warmup clean. Owner classified measurement-environment validity failure, not a cache result. No rerun; no cache regression or 5% admission pass is established. [Protocol](v1-service-profile.md).    |
| Private security reports | **Done:** GitHub Private Vulnerability Reporting is enabled; use the Security tab's **Report a vulnerability**. [Security policy](../SECURITY.md). No support SLA.                                                                                                                                                                                                                                                                                                                                                                      |
| Final release CI         | Require the shared correctness/package-consumer workflow to pass at the exact release head before the PR opens; the PR records the run. Merge and publication remain the owner's decision.                                                                                                                                                                                                                                                                                                                                              |

Input seals (SHA256): P3
`a044841a032dc68abaa359333cc1cee843a208c6c195934ccef738451e3c28bb`, P5
`2c5d4da18cbcd7897682086e66b72adbc92115ac407559eb158584e0ae9ccfe5`, P4 M1
endurance `d164efc90864492ae7233c7005130b7ea9fdb02b618b2f02af12a501727cef02`,
comment-only IL carry-forward
`b7d9fd844c90a1a41a70515d8ba1490b020047e52722c26bf5003d46e6e63c87`,
renewed Source Link
`b8454d2c67dbfe211d9a4ac89d56d40eb51d40d77e0f2d5375222c3aceda257b`.
Original failures, owner decisions, raw data and output hashes are retained locally;
the stopped P5 report does not fabricate a 60-host completion receipt.

## Earlier verified scope

The following evidence predates this qualification source and remains historical;
its test totals, long runs and service numbers are not current-source substitutes.

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

- **Resident service precision:** the predeclared 5% CPU/p99 gate remains **Inconclusive**, now with the 0.2.0 evidence above. Earlier control-only diagnostics also reproduced A/A tail variation without a cache. This neither proves a cache regression nor waives the gate. [Historical diagnostic](v1-service-precision-diagnostic-20260918.md).
- **Endurance scope:** the eight-hour run covers one macOS ARM64 .NET 10 machine; it is not Linux, x64 or high-core endurance evidence. Experimental availability is not universal platform, security or API stability sign-off.
- **Source Link:** remote retrieval is verified for the exact 4939a4c qualification source; retain commit attribution rather than treating configuration alone as evidence.
- **Platform/scale:** hosted x64 correctness does not replace long endurance/high-core contention or each architecture/feature combination. AOT claims remain limited to real publish/run evidence.
- **Policy/retention:** existing traces and controlled roots provide evidence, not universal adaptive-policy superiority or leak/race freedom. Preserve source/config/runtime attribution when validating future changes.
- **Live Rider:** the final whole-solution per-file union is clear; `get_project_problems=0` alone was not used as proof of Suggestion/Hint coverage. Earlier disconnected/Trust-modal results remain historical.
- **Semantic limits:** async weak values, JVM soft values and ordinary raw-value automatic disposal are not offered. Owned manual leases and memory-pressure eviction have separate contracts. See [feature matrix](feature-matrix.md) and [Caffeine differences](caffeine-differences.md).

## Historical failures retained

Current campaign: P4 attempt-1 failed the preflight sleep-assertion harness before
any jobs. Attempt-2 net10 was environmentally invalidated by battery/clamshell
sleep; independent net8 evidence was retained and net10 was rerun only with owner
approval in attempt-3. Local endurance then ended by owner decision, without a
verdict. The separate M1 attempt-1 was invalidated by a 100 W → 140 W adapter swap
without AC loss; attempt-2 then passed. Partial endurance durations are never
added together.

P5's first net10 no-cache control rejection is retained with raw outcomes and an
overall Inconclusive owner verdict; it is not a passed resident workload. The net8
A/A precision result independently makes the overall profile Inconclusive.
Auxiliary report/notification/pre-registration corrections are disclosed in their
retained reports: P2 optional timing lookup, P3 notification formatting, P4
preparation/stop-summary tooling, P5 post-stop process access and comment-only
carry-forward preregistration path resolution. Qualification
inputs and execution receipts were preserved; these corrections are not job retries.

The older build03 endurance workload passed, but its bridge/sequence failed when one `project.assets.json` changed; it is not substitute evidence for the new source. The new isolated-source run separately passed terminal workload and linkage checks.

Old limit-eight HTTP normal-load profiles each had a rejection and remain Failed under that original contract. Admission subsequently became explicitly opt-in; the new unconfigured/default and explicit-overload profiles are separate evidence, not relabelled old results. Earlier sandbox HTTP initialisation failure remains an environment failure, not a successful backend run.

The initial hosted Windows failure was LF/checkout handling and was fixed in the workflow lineage before the successful runs. Earlier probe cleanup/schema failures and discarded micro-optimisations do not count towards retained performance summaries. Original raw/commits are preserved locally rather than expanded into thousands of redundant tracked snapshots.

## Release decision

Use the fixed [performance gates](v1-performance-gates.md), [acceptance matrix](acceptance-matrix.md), package checks and source-bound evidence. The owner pre-decided that resident A/A precision Inconclusive can proceed to an experimental 0.2.0 proposal, with disclosure; a demonstrated cache regression with adequate precision would stop. Do not silently move budgets, count interrupted runs cumulatively, extrapolate aggregate ns/op to p99, or infer Passed from exit code/configuration alone. Winning every competitor microbenchmark is not a release requirement. The final release remains experimental, without an SLA, and APIs may still change.

## macOS hosted CI

The correctness matrix includes the standard `macos-15` ARM64 runner, with the same .NET 8/10 core, DI, short stress, source/package consumer, host smoke and API baseline checks as Windows/Linux. The final release head must pass the shared workflow; this does not claim macOS Intel or every Native AOT configuration.
