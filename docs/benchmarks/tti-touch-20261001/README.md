# Bounded TTI access coalescing: 1 October 2026

The capped implementation has a clear throughput benefit for ten readers in this cohort: TTI cycle fell from 4.867 to 2.555 ns/op and hot-key TTI from 99.887 to 2.612 ns/op. Single-reader results are less consistent. One-hour net10 TTI measured 30.046 to 29.720 ns/op with statistics off and 32.387 to 33.813 ns/op with statistics on. The additional one-day row measured 28.047 to 25.078 ns/op with the same one-millisecond cap. These results do not establish a uniform single-reader improvement.

## Contract and implementation

Only validated lock-free fixed-TTI hits coalesce access timestamps, using `min(accessBound >> 20, floor(TimestampFrequency / 1000))` raw timestamp units. This explicitly permits bounded early expiration and policy-age error. An older error can survive runtime duration shrink, but remains at most one millisecond. The former freshness guard band selected exact fallback; it did not already grant this tolerance. See [ADR-0016](../../adr/0016-bounded-tti-access-coalescing.md), [semantics](../../semantics.md) and [concurrency](../../concurrency.md).

The patch adds one readonly per-engine raw cap and a condition before `TouchPublished`. The existing freshness, publication, retirement and forward-only CAS protocols remain. Locked touch paths remain exact. No entry field, configuration option or default-clock change is added.

## Method

- Baseline: main [a179760](https://github.com/awe123343-org/LoadingCache.Net/commit/a179760b1a3de84df3352609b8898d94c63785da), without #38. Patch build label: `tti-cap1ms`, based on that commit.
- Apple M4 Max, arm64; SDK 10.0.401. Explicit `dotnet exec --fx-version 10.0.8` or `8.0.20`. MemoryCache package 10.0.12.
- The main cohort contains 288 independent processes. The one-day supplement adds 24. Each cell has six processes per library per variant, with variant and LC/MC order reversed each round. Main and supplement remain separate cohorts.
- Each process has three 250-ms warmups and seven 250-ms measured samples. Values below are medians of the six process medians. Concurrent ns/op is elapsed read time divided by aggregate operations, not individual request latency. Allocation uses the process-wide managed-allocation delta over the same read window.
- Keys are preboxed objects, with 1,024 residents and capacity 1,024. Cycle readers use phase offsets; hot readers share one key. MC uses SizeLimit=1,024 and Size=1, with absolute expiry as the TTL control and sliding expiry as the TTI control. Net8 runs only the single-reader cells.
- All 312 processes held the shared benchmark lock and recorded AC before/after, battery 100%, power mode 0. Adapter rating: 65 W; this is not measured draw. Builds and tests also held the lock and did not overlap timing.
- All 2,184 measured samples passed independent checks of counts, checksums, statistics, zero misses, runtime and assembly hashes. Process ordering and medians were independently recomputed. The one-hour and one-day preflight cohorts are excluded.
- The original one-hour harness source is byte-identical to the frozen baseline harness. The one-day harness changes only four setup duration constants and references the exact same frozen LC binaries, verified by SHA256.

## Results

Ratios are patch/base; lower is better. MC is an environmental control, not a divisor used to silently correct LC results. B/op values are rounded to three decimals; tiny nonzero coordinator/measurement allocations remain in raw data. MC median B/op also rounds to 0.000 in every row.

| Case (runtime, expiry, readers/pattern, statistics) | LC base ns | LC patch ns | LC ratio | MC base ns | MC patch ns | MC ratio | LC B/op base -> patch |
| --------------------------------------------------- | ---------: | ----------: | -------: | ---------: | ----------: | -------: | --------------------: |
| net10.0 TTL/1h 1/cycle off                          |     28.335 |      24.514 |    0.865 |     17.962 |      18.002 |    1.002 |        0.000 -> 0.000 |
| net10.0 TTI/1h 1/cycle off                          |     30.046 |      29.720 |    0.989 |     18.348 |      18.337 |    0.999 |        0.000 -> 0.000 |
| net10.0 TTL/1h 1/cycle on                           |     30.822 |      32.131 |    1.042 |     18.841 |      18.938 |    1.005 |        0.000 -> 0.000 |
| net10.0 TTI/1h 1/cycle on                           |     32.387 |      33.813 |    1.044 |     18.984 |      19.059 |    1.004 |        0.000 -> 0.000 |
| net10.0 TTL/1h 10/cycle off                         |      2.203 |       2.098 |    0.952 |     36.217 |      35.626 |    0.984 |        0.000 -> 0.000 |
| net10.0 TTI/1h 10/cycle off                         |      4.867 |       2.555 |    0.525 |     36.229 |      37.434 |    1.033 |        0.000 -> 0.000 |
| net10.0 TTL/1h 10/hot off                           |      2.587 |       2.552 |    0.987 |     21.737 |      20.947 |    0.964 |        0.000 -> 0.000 |
| net10.0 TTI/1h 10/hot off                           |     99.887 |       2.612 |    0.026 |     21.114 |      20.712 |    0.981 |        0.000 -> 0.000 |
| net8.0 TTL/1h 1/cycle off                           |     25.544 |      29.656 |    1.161 |     19.007 |      18.821 |    0.990 |        0.000 -> 0.000 |
| net8.0 TTI/1h 1/cycle off                           |     34.719 |      31.511 |    0.908 |     19.026 |      19.286 |    1.014 |        0.000 -> 0.000 |
| net8.0 TTL/1h 1/cycle on                            |     36.456 |      32.944 |    0.904 |     20.501 |      20.610 |    1.005 |        0.000 -> 0.000 |
| net8.0 TTI/1h 1/cycle on                            |     38.820 |      38.331 |    0.987 |     20.564 |      20.579 |    1.001 |        0.000 -> 0.000 |
| net10.0 TTI/24h 1/cycle off                         |     28.047 |      25.078 |    0.894 |     18.392 |      18.404 |    1.001 |        0.000 -> 0.000 |

The ten-reader cycle TTI process ranges are disjoint: baseline 4.213–5.274 ns/op, patch 2.426–2.803. Hot TTI ranges are 95.926–109.613 and 2.531–2.805. MC's median shifts in these cells are +3.3% and −1.9%; these cannot explain the LC improvement's magnitude. No spread-key regression appeared.

The one-day single-reader median ratio is 0.894 (paired-round median ratio also 0.894), with MC at 1.001. Its LC ranges still overlap: baseline 27.470–33.456 and patch 24.824–30.894. It demonstrates a favourable result with the cap present, not a guaranteed single-reader gain.

Single-reader TTL control medians also move in opposite directions across runtimes/settings, from ratio 0.865 to 1.161, despite no added TTL touch work. LC process ranges are wide while single-reader MC controls are comparatively stable. Individual processes mostly stabilise after warmup, so removing the first measured sample would not resolve the between-process variation. The cause of these shifts has not been isolated: they must not be credited to touch coalescing, described as a proven cap-induced regression, or discarded by selecting only fast launches. No uncapped control was measured in these formal cohorts. The single-reader expiry gap to MemoryCache remains open.

## Process medians

Each list is round 1 through round 6, in ns/op; no launch is excluded.

| Case                        | Backend      | Base process medians                              | Patch process medians                          |
| --------------------------- | ------------ | ------------------------------------------------- | ---------------------------------------------- |
| net10.0 TTL/1h 1/cycle off  | loadingcache | 28.662, 28.394, 28.275, 23.984, 26.863, 30.466    | 26.849, 23.756, 24.116, 24.669, 27.976, 24.360 |
| net10.0 TTL/1h 1/cycle off  | memorycache  | 17.704, 17.755, 18.115, 17.999, 17.925, 18.006    | 17.690, 17.970, 18.034, 17.880, 18.090, 18.044 |
| net10.0 TTI/1h 1/cycle off  | loadingcache | 28.094, 30.504, 34.288, 30.238, 29.840, 29.853    | 30.365, 25.304, 29.356, 30.084, 27.245, 30.503 |
| net10.0 TTI/1h 1/cycle off  | memorycache  | 18.388, 18.372, 18.742, 18.325, 18.247, 18.316    | 18.161, 18.409, 18.353, 18.306, 18.321, 18.355 |
| net10.0 TTL/1h 1/cycle on   | loadingcache | 30.453, 29.361, 36.022, 36.856, 28.920, 31.191    | 31.298, 28.686, 32.963, 37.130, 34.603, 31.185 |
| net10.0 TTL/1h 1/cycle on   | memorycache  | 18.863, 18.810, 18.995, 18.776, 18.884, 18.819    | 19.139, 18.888, 19.004, 18.962, 18.856, 18.913 |
| net10.0 TTI/1h 1/cycle on   | loadingcache | 34.093, 33.265, 31.193, 31.509, 30.748, 34.913    | 30.652, 34.845, 35.027, 35.406, 32.781, 29.903 |
| net10.0 TTI/1h 1/cycle on   | memorycache  | 18.935, 18.863, 18.936, 19.032, 19.367, 19.384    | 18.992, 18.805, 19.126, 19.247, 18.820, 19.131 |
| net10.0 TTL/1h 10/cycle off | loadingcache | 2.022, 2.119, 1.979, 2.450, 2.562, 2.286          | 2.112, 2.006, 2.055, 2.084, 2.306, 2.898       |
| net10.0 TTL/1h 10/cycle off | memorycache  | 36.232, 36.578, 36.569, 31.373, 36.202, 18.066    | 39.176, 34.929, 33.781, 36.323, 37.632, 19.792 |
| net10.0 TTI/1h 10/cycle off | loadingcache | 5.274, 4.897, 4.213, 4.836, 5.053, 4.753          | 2.744, 2.426, 2.527, 2.582, 2.485, 2.803       |
| net10.0 TTI/1h 10/cycle off | memorycache  | 36.163, 36.296, 36.803, 37.197, 23.647, 35.711    | 36.973, 36.331, 37.700, 37.213, 37.860, 37.654 |
| net10.0 TTL/1h 10/hot off   | loadingcache | 2.515, 2.573, 2.880, 2.576, 2.599, 2.973          | 2.525, 2.692, 2.639, 2.550, 2.554, 2.549       |
| net10.0 TTL/1h 10/hot off   | memorycache  | 19.395, 20.531, 19.927, 22.944, 24.713, 24.136    | 19.365, 20.625, 21.269, 20.611, 25.265, 25.619 |
| net10.0 TTI/1h 10/hot off   | loadingcache | 109.272, 109.613, 103.488, 96.156, 96.286, 95.926 | 2.620, 2.701, 2.531, 2.554, 2.604, 2.805       |
| net10.0 TTI/1h 10/hot off   | memorycache  | 26.133, 26.700, 19.392, 22.455, 19.774, 19.394    | 25.347, 27.437, 20.513, 20.807, 19.488, 20.616 |
| net8.0 TTL/1h 1/cycle off   | loadingcache | 24.086, 26.431, 25.642, 32.746, 25.445, 24.720    | 24.539, 34.941, 35.434, 24.997, 33.271, 26.041 |
| net8.0 TTL/1h 1/cycle off   | memorycache  | 18.602, 19.451, 19.019, 19.072, 18.995, 18.910    | 18.699, 18.997, 19.042, 18.762, 18.622, 18.881 |
| net8.0 TTI/1h 1/cycle off   | loadingcache | 28.293, 40.079, 28.771, 39.139, 30.299, 39.439    | 28.529, 25.242, 37.453, 34.492, 27.930, 37.152 |
| net8.0 TTI/1h 1/cycle off   | memorycache  | 18.624, 18.903, 19.613, 19.085, 19.702, 18.967    | 18.639, 18.857, 19.324, 19.308, 19.263, 19.381 |
| net8.0 TTL/1h 1/cycle on    | loadingcache | 38.113, 33.788, 34.799, 39.840, 30.413, 40.312    | 33.440, 43.119, 32.448, 30.037, 31.494, 37.910 |
| net8.0 TTL/1h 1/cycle on    | memorycache  | 20.488, 20.302, 20.946, 20.515, 20.470, 20.679    | 20.483, 20.610, 20.638, 20.412, 20.679, 20.611 |
| net8.0 TTI/1h 1/cycle on    | loadingcache | 41.947, 31.499, 43.556, 40.706, 36.934, 35.625    | 36.844, 37.849, 41.060, 38.814, 37.621, 41.629 |
| net8.0 TTI/1h 1/cycle on    | memorycache  | 20.602, 20.694, 20.407, 20.525, 21.087, 20.423    | 20.446, 20.713, 23.090, 20.228, 20.209, 20.749 |
| net10.0 TTI/24h 1/cycle off | loadingcache | 33.456, 27.470, 28.004, 27.681, 28.090, 32.084    | 24.863, 30.254, 25.082, 30.894, 25.075, 24.824 |
| net10.0 TTI/24h 1/cycle off | memorycache  | 18.468, 18.415, 18.211, 18.367, 18.370, 18.621    | 18.581, 18.427, 18.332, 18.382, 18.339, 18.496 |

## Correctness and local evidence

Net8 and net10 core tests each passed 758/758, DI 10/10, stress 10/10, and ConsumerSmoke. The solution build and `dotnet csharpier check src tests benchmarks` exited zero (146 files). `global.json` was not changed. Existing tests and assertions were retained.

Eleven new cases cover inclusive tolerance and advancement, early-expiry bounds, the one-millisecond cap, both fixed-duration mutation method names, exact task/weak/owned/automatic-refresh paths, and a gated `Updating` publication fallback. The first ten were run against the baseline before the product change: five coalescing assertions failed as expected, while five exact-path cases passed. Architecture-sensitive assertions preserve exact expectations for 32-bit processes; this arm64 host did not run a native 32-bit runtime.

Local measurement assets intentionally remain Git-ignored under `artifacts/perf-expiry-baseline/` and `artifacts/perf-tti-touch/`. Main raw cohort: `results/tti-cap1ms-formal-ac-20261001`; supplement: `results/tti-cap1ms-day-formal-ac-20261001`. Each contains the plan, process JSON/logs/receipts, `comparisons.json`, `process-medians.csv` and `validation.json`. Main measurement window: 08:07:05–08:20:59 UTC; supplement: 08:21:31–08:22:40 UTC on 1 October 2026.

LC library SHA256, reused by both duration harnesses:

- Baseline: `0794c9b4457211636f820eba07a308ede9e1cb3a3bd0bd52e02cf112d738ef18`.
- Patch: `505ffb3112be4fc37e4c16d8e673c19452d3b8fbc24032517b89951e022e963b`.

From the local matrix directory, the formal commands were:

```sh
uv run --no-project python3 matrix.py run \
  --cohort tti-cap1ms-formal-ac-20261001 \
  --variants baseline-a tti-cap1ms --tfms net10.0 net8.0 --suite hit
uv run --no-project python3 matrix.py run \
  --cohort tti-cap1ms-day-formal-ac-20261001 \
  --variants baseline-a-day tti-cap1ms-day --tfms net10.0 --suite hit \
  --case-filter '^net10.0-hit-access-w1-cycle-stats-off$'
```

`matrix.py build --label <new-label> --lc-root <worktree>` accepts another source tree without overwriting existing measured labels. `build_long.py` builds the one-day harness against frozen assemblies; it was invoked under the shared lock. The matrix wrapper acquires that lock internally for each timing process; do not wrap it a second time.
