# Resident service precision diagnostic — 18 September 2026

The fixed control-only diagnostic completed **960,000 successful requests**. The same control still showed substantial p99 variation, so the original resident 5% gate remains **Inconclusive**, not a demonstrated cache regression. This diagnostic had no LoadingCache dependency and changed neither production code nor budgets.

Twelve fresh server/client arms ran on actual .NET 8.0.31/10.0.12, three A/A pairs each in a/b, b/a, a/b order. Each arm used ten-second warmup, 30-second measurement, 2,000 scheduled requests/s, 256 pending, a five-second scheduled-arrival deadline, 1,024 keys and a 1,024-character payload. Original Task.Delay(1) catch-up pacing and response validation remained. No retries, extra rounds, trimming or tuning were added.

The unique sequence ran 18:46:57–18:55:06 UTC: 240,000 warmup and 720,000 measured requests, no rejection/timeout/failure. Verify 24 process identities, runtime/assembly hashes, per-request times and both roles' telemetry; guards ran before arms and at completion. Builds had no warnings/errors; two short smokes passed 6,000 requests each, and each rejected 16 negative controls. An earlier sandbox CookieContainer initialisation failure remains failed environment evidence.

Ratios below are label b/a, not later/earlier. They are instrumented diagnostic observations, not confidence intervals or cache/control speedups.

| Runtime | Pair | Order | p99 a / b (ms) | p99 b/a | Server CPU/request b/a |
| ------- | ---- | ----- | -------------- | ------- | ---------------------- |
| .NET 8  | 0    | a → b | 1.700 / 4.669  | 2.747   | 0.8846                 |
| .NET 8  | 1    | b → a | 1.613 / 1.723  | 1.068   | 1.0640                 |
| .NET 8  | 2    | a → b | 1.616 / 1.636  | 1.012   | 0.9438                 |
| .NET 10 | 0    | a → b | 1.660 / 1.760  | 1.060   | 0.9953                 |
| .NET 10 | 1    | b → a | 3.292 / 1.658  | 0.503   | 0.9957                 |
| .NET 10 | 2    | a → b | 1.902 / 16.057 | 8.441   | 0.9855                 |

For net10 pair 2, 601 tail requests were decomposed individually: summed latency was 49.749% scheduled-to-admission and 50.199% send-to-body-buffered, the remainder setup/validation. This is not subtraction of unrelated percentiles. Dispatch lateness and response waiting are diagnostic leads, not a unique root cause.

One overlapping client interval recorded a 34.677 ms GC-pause delta; other high-tail seconds (13/18, p99 28.811/31.151 ms) had no intersecting client/server GC increment. GC alone cannot explain the batch. Body-buffered timestamps include continuation resumption, not pure network/server time. CPU/GC/ThreadPool/load-average samples give interval correlation, without kernel/thermal tracing. Instrumentation costs prevent substituting these results for the original uninstrumented gate; reproducing control variation does not explain every earlier cache/control difference.

Stop this bounded diagnostic and preserve Inconclusive. Closing the gate requires a predeclared method/environment with demonstrated precision, or an explicit acceptance decision, not more retries until green.

Local, unpublished evidence: `artifacts/opt-in-load-limits-20260918/resident-precision-diagnostic01/` protocol, build04, freeze02, smoke02, formal01, root-final-review01 and analysis01/execution01. Freeze SHA256: `0e9b2ee398ef19574626a681d3b9c266d091c1a9c0792d3bbc53829ffaf51a88`. Terminal summary: `fd6000cf7fd417f5b62f832737293ca5a3401564b954baf490c0e811f21a776f`. The executed local command was `PYTHONDONTWRITEBYTECODE=1 uv run --offline --no-project python run.py formal --freeze freeze02/freeze.json --output formal01`; do not reuse its output. Frozen protocol Not run text records pre-execution state.
