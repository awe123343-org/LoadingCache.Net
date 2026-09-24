# V1 stability and endurance profile

Profile `v1-endurance-1`, frozen on 16 September 2026 before formal measurement. The profile defines acceptance; [results](v1-stability-results-20260918.md) separately identify executed source/runtime evidence.

## Workload

Phase one reuses four `LongRunningStabilityTests` cases (mixed/access-only, statistics off/on) and two `FeatureCombinationStabilityTests` cases (off/on). Each executes for at least 1,800 monotonic seconds. TUnit runs six cases concurrently (`--maximum-parallel-tests 6`) in one process, with eight traffic workers per fixture; this is not three continuous hours. Run net8 then net10 separately, avoiding shared benchmark CPU lanes. Preserve the original bulk/weak/listener assertions.

Phase two is one separate 28,800-second process exercising sync manual/loading and async manual/loading. Each cache has size=256, explicit concurrent/pending limits=4 and notification capacity=32. Eight workers use batches of 64 operations; seed=20260916 fixes generated choices, not OS interleaving. Keys=1,024, payload=256 bytes plus metadata; mix=60% get/load, 20% put, 15% invalidate, 5% resident/task read. Async loading yields; sync loading remains genuinely synchronous. Check key/generation/payload on every returned value.

Each batch drains loader, policy read/write backlog and notification queues within 30 seconds, then verifies resident count≤256 and weight=count. Clear every 16 batches and check zero count. Every 300 seconds clear/dispose, wait for listener completion and recreate all four caches, alternating statistics each cycle. Emit progress every ten seconds and each clear/recreation. These operation totals are not throughput benchmarks.

Expected admission rejection is counted separately; unexpected exceptions fail the process. Sampled capture+dispatch queues≤64, loader peak≤4; quiescent gauges are zero. Bulk/weak/expiry/refresh details are exercised by phase-one fixtures, not claimed for every phase-two operation.

## Retention rules

After each owning async cycle method returns, perform two full collections with finaliser waiting between them, avoiding live method locals as false leaks. Combine weak roots, progress, quiescent state and time windows rather than one GC assertion.

- Four warmup cycles (about 20 minutes); then median windows of five samples.
- First complete median window is baseline. Later retained growth must be ≤`max(16 MiB, baseline*25%)`.
- Least-squares slope of complete median windows versus monotonic elapsed time must be ≤1 MiB/hour. Require at least 60 post-warmup samples; exclude an incomplete final window from slope.
- Keep weak facade/loader-state/payload references for at most 128 cycles. A target retired more than two cycles ago and alive in two consecutive observations fails. Recent cycles have grace; this is not exact per-object lifetime proof.
- Record total allocations, generation collection counts, heap/fragmentation, working set and private bytes. OS memory observations do not replace live-graph retention evidence.

Preserve failures and investigate roots; never relax thresholds retrospectively. This fixed workload screens risk rather than proving universal leak/race freedom.

## Execution and evidence

One runtime/CPU lane at a time. Build/format before freezing source, tests, probes and runtime. The runner performs no build/restore and rejects existing output. Copy sources/binaries into inputs and record hashes, commands, overrides, PID, actual runtime, elapsed and exit. Default JIT/tiering/PGO only; inherited tuning/GCStress overrides are rejected.

```sh
dotnet build tools/LoadingCache.EnduranceProbe/LoadingCache.EnduranceProbe.csproj -c Release
dotnet build tests/LoadingCache.StressTests/LoadingCache.StressTests.csproj -c Release
uv run --no-project python tools/LoadingCache.EnduranceProbe/run-acceptance.py --framework net10.0 --runtime artifacts/runtime10/dotnet --output artifacts/validation/v1-stability-smoke-net10 --smoke
uv run --no-project python tools/LoadingCache.EnduranceProbe/run-acceptance.py --framework net8.0 --runtime artifacts/runtime8/dotnet --output artifacts/validation/v1-stability-smoke-net8 --smoke
uv run --no-project python tools/LoadingCache.EnduranceProbe/run-acceptance.py --framework net10.0 --runtime artifacts/runtime10/dotnet --output artifacts/validation/v1-stability-formal-net10
uv run --no-project python tools/LoadingCache.EnduranceProbe/run-acceptance.py --framework net8.0 --runtime artifacts/runtime8/dotnet --output artifacts/validation/v1-stability-formal-net8 --phase soak
```

Smoke uses five-second fixture runs and 30-second endurance with one-second recreation/two warmup cycles. It tests window caps, not hourly-slope qualification. `--phase soak`, `endurance` or default `all` verifies only executed phases. This gate needs both six-case runtime matrices plus one eight-hour endurance, approximately nine hours total; another runtime's eight-hour endurance is optional, not an extra requirement.

The runner emits heartbeat JSONL every 30 seconds; 120 seconds without endurance output fails. Preserve soak JSONL and `endurance.log`. Terminal Passed requires exit, complete duration, test identity, actual runtime and source/input/runtime hashes together. Running text alone is not liveness. Interrupted runs retain evidence and require a new output, never cumulative/resumed duration.
