# Derived read-ring enqueued count: main comparison

Base `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4`; candidate measured from the uncommitted `perf/read-ring-enqueued-count` worktree. No H1/L1/G2/B1 changes. See method (`artifacts/perf-ring-enqueued-count/method.md`), functional receipt (`artifacts/perf-ring-enqueued-count/functional-receipt.json`), frozen manifest (`artifacts/perf-ring-enqueued-count/builds/manifest.json`) and raw plan (`artifacts/perf-ring-enqueued-count/results/plan.json`).

All 168 planned processes / 1,176 measured samples are retained: 144 formal including MemoryCache controls, plus 24 separate diagnostic processes. .NET 10.0.8 / Apple M4 Max / AC mode 0. Common HitProbe binary; source/cache/MemoryCache hashes verified. Each process has 3 warmups and 7 x 250 ms measured samples, 1,024 preboxed cycling resident keys. Ten-worker figures are batch wall ns/hit, not per-thread latency.

Functional: net8 and net10 each pass 847 core + 10 DI + 10 stress tests; solution build, CSharpier and diff checks pass. The old naive formula fails the new paused-producer regression twice (capacities 1 and 4); the candidate passes. See counterfactual proof (`artifacts/perf-ring-enqueued-count/naive-spike-check/receipt.json`).

GetStatistics visits at most two ring capacities per live ring and performs at most one sampled-maximum CAS per ring, without retry. No per-offer enqueued CAS. Shutdown keeps its existing publication/claim ownership and adds an accepted-only counter; two extra longs per ring. Producer admission, publication fences and retention are unchanged. High-frequency statistics-reader cost is outside this hit benchmark.

## Interpretation for review

- One-worker statistics ON: 16.578 to 15.986 ns/hit; raw -3.57%, paired MC-adjusted -4.11% (about 0.688 ns/hit adjusted), 6/6 adjusted pairs faster, with overlapping process ranges. This is a modest gain in this cohort.
- One-worker statistics OFF also improves (13.548 to 12.811 ns/hit, adjusted -6.19%). The change therefore does not isolate the cost of the removed statistics atomic or show that the statistics surcharge itself shrank. Method/field layout and transport interaction were not separately attributed.
- Ten-worker statistics OFF improves from 1.888 to 1.236 ns/hit, adjusted -37.84%, 6/6 pairs faster with separated ranges. This is a whole-change observation, not an attribution to an atomic that was disabled in that cell.
- Ten-worker statistics ON remains unqualified as an overall improvement. Base has four fast processes (3.478-3.741 ns) and two slow (9.417-9.490); candidate has three fast (2.947-3.043) and three slow (8.635-8.872). Its pooled median lies between modes: 3.663 to 5.839 ns/hit, raw +59.41%, paired MC-adjusted +54.83%, only 3/6 adjusted pairs faster. The within-mode ranges look better, but these are post-hoc, unpaired subsets with small counts; they do not cancel the adverse aggregate observation or prove unchanged mode probabilities.
- Separate diagnostics also retain both modes. Slow processes accept more read events and drain more often: base fast 2.58-2.64% acceptance / 100.7-103.1 passes per million hits versus slow 6.26% / 244.5; candidate fast 2.21-2.88% / 86.4-112.5 versus slow 5.24-5.72% / 204.7-223.4. This is an association, not a root-cause proof. Diagnostic timing is not substituted for formal timing.

The implementation satisfies the functional contract and the declared statistics-OFF gates. Shipping requires accepting the modest single-worker result, extra diagnostic work/metadata and unresolved ten-worker behaviour; these receipts do not justify claiming a general statistics-ON speedup. The measurements alone do not resolve that acceptance decision.

## Formal timing

Values are median [min, max] of six process medians, ns/hit. Adjusted change is the median of six paired `(candidate/MC-candidate)/(base/MC-base)-1` ratios; it is descriptive, not a confidence interval. All modes remain in the arrays below.

| Case | Base | Candidate | MC beside base | MC beside candidate | Raw change | MC-adjusted change | Faster adjusted pairs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| hit-none-w1-off | 13.548 [13.018, 14.395] | 12.811 [12.725, 13.244] | 17.308 [16.722, 17.610] | 17.335 [17.050, 17.717] | -5.44% | -6.19% | 6/6 |
| hit-write-w1-off | 20.148 [19.939, 20.711] | 20.422 [20.019, 20.666] | 17.847 [17.742, 18.217] | 17.928 [17.721, 18.301] | +1.36% | +0.31% | 2/6 |
| hit-access-w1-off | 21.227 [20.955, 21.446] | 21.301 [20.913, 22.134] | 18.276 [18.238, 18.585] | 18.210 [18.170, 18.427] | +0.35% | +0.25% | 1/6 |
| hit-none-w1-on | 16.578 [16.151, 17.687] | 15.986 [15.259, 16.328] | 18.543 [18.358, 18.638] | 18.556 [18.354, 18.683] | -3.57% | -4.11% | 6/6 |
| hit-none-w10-off | 1.888 [1.839, 1.951] | 1.236 [1.174, 1.314] | 34.390 [33.491, 37.445] | 35.504 [34.792, 35.837] | -34.56% | -37.84% | 6/6 |
| hit-none-w10-on | 3.663 [3.478, 9.490] | 5.839 [2.947, 8.872] | 38.486 [38.064, 40.464] | 39.534 [37.912, 40.064] | +59.41% | +54.83% | 3/6 |

The three statistics-OFF one-worker gates pass the predeclared rule: stop only for >3% median regression with strictly non-overlapping worse process ranges. A gate pass is not proof of zero regression in every environment.

## Every formal process median

| Case | Base r1-r6 | Candidate r1-r6 | MC base r1-r6 | MC candidate r1-r6 |
| --- | --- | --- | --- | --- |
| hit-none-w1-off | 13.018 / 13.150 / 13.615 / 13.482 / 14.395 / 13.702 | 13.122 / 13.244 / 12.735 / 12.725 / 12.864 / 12.758 | 16.722 / 17.069 / 17.279 / 17.336 / 17.610 / 17.535 | 17.050 / 17.218 / 17.350 / 17.321 / 17.717 / 17.630 |
| hit-write-w1-off | 19.939 / 20.173 / 20.123 / 19.995 / 20.711 / 20.199 | 20.613 / 20.073 / 20.281 / 20.019 / 20.666 / 20.564 | 17.742 / 17.754 / 17.824 / 17.871 / 18.217 / 17.984 | 17.738 / 17.721 / 17.891 / 18.301 / 18.139 / 17.964 |
| hit-access-w1-off | 21.270 / 21.443 / 21.446 / 21.185 / 21.000 / 20.955 | 21.154 / 21.447 / 22.134 / 20.913 / 21.809 / 20.940 | 18.296 / 18.256 / 18.585 / 18.238 / 18.305 / 18.242 | 18.170 / 18.192 / 18.427 / 18.215 / 18.279 / 18.205 |
| hit-none-w1-on | 16.243 / 17.687 / 16.480 / 16.151 / 16.780 / 16.675 | 15.938 / 16.021 / 16.328 / 15.259 / 15.951 / 16.173 | 18.358 / 18.544 / 18.541 / 18.531 / 18.635 / 18.638 | 18.354 / 18.429 / 18.585 / 18.527 / 18.641 / 18.683 |
| hit-none-w10-off | 1.839 / 1.865 / 1.929 / 1.907 / 1.868 / 1.951 | 1.174 / 1.201 / 1.235 / 1.236 / 1.314 / 1.302 | 34.049 / 34.332 / 34.449 / 33.491 / 37.445 / 35.199 | 35.614 / 35.649 / 35.393 / 35.837 / 34.792 / 34.862 |
| hit-none-w10-on | 3.741 / 9.490 / 3.478 / 9.417 / 3.512 / 3.584 | 8.872 / 2.947 / 8.635 / 2.967 / 8.682 / 3.043 | 38.064 / 38.122 / 38.371 / 38.944 / 40.464 / 38.602 | 39.850 / 40.064 / 39.577 / 39.491 / 37.912 / 39.416 |

## Separate diagnostic processes

These timings are not pooled with formal results. The table retains each process and its seven measured samples; acceptance and drain/request activity are computed from all seven samples. Accepted equals reserved, and accepted + terminal Full/Failed drops equals hits in every diagnostic sample. Maintenance rates are per million hits.

| Workers | Source/round | Median ns | Seven sample ns | Accepted | Drain passes/M hits | Requests/M hits | CPU ns/hit |
| ---: | --- | ---: | --- | ---: | ---: | ---: | ---: |
| 1 | base/1 | 16.479 | 17.887 / 16.299 / 16.986 / 16.301 / 16.479 / 15.988 / 16.975 | 21.82% | 3502.56 | 3317.83 | 50.899 |
| 1 | derived/1 | 17.283 | 17.542 / 17.073 / 16.869 / 17.283 / 17.370 / 17.938 / 17.143 | 21.56% | 3401.03 | 3337.50 | 53.281 |
| 1 | derived/2 | 17.192 | 17.767 / 17.906 / 17.369 / 17.192 / 16.730 / 16.458 / 16.284 | 18.41% | 2947.62 | 2805.76 | 53.093 |
| 1 | base/2 | 16.517 | 16.480 / 17.920 / 16.662 / 15.991 / 16.517 / 17.047 / 16.140 | 22.34% | 3589.20 | 3394.18 | 50.935 |
| 1 | base/3 | 16.482 | 16.339 / 16.739 / 17.058 / 15.668 / 16.259 / 16.678 / 16.482 | 21.60% | 3469.39 | 3282.59 | 50.708 |
| 1 | derived/3 | 15.770 | 15.900 / 15.670 / 16.192 / 15.681 / 15.770 / 15.769 / 16.059 | 20.55% | 3256.34 | 3165.26 | 48.601 |
| 1 | derived/4 | 16.521 | 16.345 / 16.521 / 16.310 / 16.833 / 16.567 / 16.202 / 16.764 | 21.91% | 3472.00 | 3373.98 | 51.166 |
| 1 | base/4 | 16.698 | 17.011 / 16.545 / 16.698 / 17.939 / 17.645 / 16.532 / 16.339 | 22.84% | 3622.58 | 3515.36 | 50.503 |
| 1 | base/5 | 17.123 | 17.969 / 17.076 / 16.936 / 16.781 / 18.603 / 17.367 / 17.123 | 22.98% | 3653.12 | 3535.15 | 53.435 |
| 1 | derived/5 | 15.954 | 15.954 / 16.216 / 15.851 / 15.801 / 16.013 / 16.301 / 15.912 | 20.93% | 3310.31 | 3230.92 | 48.953 |
| 1 | derived/6 | 15.820 | 15.779 / 16.216 / 15.923 / 15.820 / 15.700 / 16.269 / 15.760 | 21.62% | 3405.60 | 3352.13 | 48.204 |
| 1 | base/6 | 17.217 | 16.860 / 17.217 / 18.202 / 17.366 / 17.061 / 18.053 / 17.134 | 22.79% | 3620.69 | 3508.70 | 52.549 |
| 10 | base/1 | 12.598 | 12.494 / 13.138 / 13.341 / 13.352 / 11.771 / 12.598 / 12.290 | 6.26% | 244.53 | 0.07 | 139.259 |
| 10 | derived/1 | 2.075 | 2.088 / 2.063 / 2.100 / 2.099 / 2.059 / 2.075 / 2.047 | 2.86% | 111.83 | 0.01 | 22.920 |
| 10 | derived/2 | 10.918 | 10.888 / 11.052 / 10.953 / 11.055 / 10.587 / 10.918 / 10.292 | 5.72% | 223.45 | 0.04 | 118.764 |
| 10 | base/2 | 2.994 | 2.850 / 3.049 / 2.955 / 2.992 / 3.143 / 3.122 / 2.994 | 2.58% | 100.68 | 0.01 | 33.158 |
| 10 | base/3 | 3.008 | 3.008 / 3.048 / 2.883 / 3.085 / 3.008 / 2.930 / 3.029 | 2.60% | 101.55 | 0.02 | 32.988 |
| 10 | derived/3 | 10.509 | 10.896 / 10.760 / 10.509 / 10.841 / 10.468 / 9.622 / 9.117 | 5.24% | 204.75 | 0.06 | 112.593 |
| 10 | derived/4 | 2.032 | 1.997 / 2.056 / 2.001 / 2.018 / 2.053 / 2.032 / 2.050 | 2.88% | 112.47 | 0.01 | 22.379 |
| 10 | base/4 | 3.027 | 3.059 / 2.962 / 2.966 / 3.009 / 3.066 / 3.027 / 3.062 | 2.64% | 103.13 | 0.02 | 33.156 |
| 10 | base/5 | 2.951 | 2.951 / 2.878 / 2.912 / 2.923 / 2.982 / 3.003 / 3.063 | 2.61% | 101.98 | 0.01 | 32.622 |
| 10 | derived/5 | 1.984 | 1.954 / 1.968 / 2.049 / 1.990 / 1.968 / 1.984 / 1.996 | 2.21% | 86.36 | 0.01 | 21.753 |
| 10 | derived/6 | 2.052 | 2.068 / 2.028 / 2.064 / 2.052 / 2.045 / 2.042 / 2.122 | 2.21% | 86.52 | 0.02 | 22.380 |
| 10 | base/6 | 3.181 | 3.135 / 3.181 / 3.266 / 3.201 / 3.151 / 3.218 / 3.058 | 2.61% | 102.10 | 0.01 | 34.482 |

## Validation and limitations

Every process has zero misses, correct statistics deltas, resident/weight bounds, stable power and expected runtime/assembly identity. Every raw warmup and measured sample remains in its JSON; samples.csv (`artifacts/perf-ring-enqueued-count/samples.csv`) contains all measured samples and summary.json (`artifacts/perf-ring-enqueued-count/summary.json`) preserves process-level diagnostics, stripe/thread identities and paired calculations.

Two harness-runner corrections are retained: an unsupported option was rejected in a smoke before formal timing, and the first formal result passed the benchmark but its validator expected a null diagnostics key that the serializer omits. That result was revalidated and retained without re-running. Before any candidate formal process, the base harness DLL/PDB was copied to its runtime for identical harness bytes. See preflight correction (`artifacts/perf-ring-enqueued-count/preflight-correction.json`) and validation correction (`artifacts/perf-ring-enqueued-count/validation-correction/receipt.json`). No production source changed after functional validation; no timed process was discarded or repeated.

Cross-process spread and the historical ten-worker/statistics-ON two-mode behaviour limit generalisation. Inspect the full process/sample distributions and acceptance/drain rates before attributing a timing shift to the removed atomic. This run does not establish performance on other machines/runtimes, under frequent exporter reads, or parity with historical Caffeine measurements.

## Frozen fingerprints

| Source | Production fingerprint | LoadingCache.dll SHA-256 |
| --- | --- | --- |
| base | `9f130c714b1784472bafdf33a73b4edf578eeb7cc677def5502c7aba6d0d005b` | `d1bbc93d4e5c2d0f1f4aef2953ec33e02701df6f3fe7e05ca463a9099a7a798c` |
| derived | `0043253e7127fd89ff31b8d76b279e27b87bf6a6e1435a2b4309ed698d5e90ee` | `dcb9bf4f26597a50fd04333e76a5ba69a8ee304219a7a29b050ddaa88a6871eb` |

Common harness SHA-256: `fb61a861d5c6bd879f83525dd01f0574c4b475ebb0a60b0c7fcb186df9c268d5`. The HitProbe source is unchanged from the baseline. The local evidence directory retains the source snapshots, exact binaries, all results/logs and receipts, including the two runner corrections.

## Owner decision and rebase

Both original timing rounds above and in [the targeted 24-process report](targeted-24.md) use base `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4`, before H1. The candidate is rebased for shipping onto main `42a4c558972b71aeb0bfbb0b4f3b48a31590d4ac`, which includes H1 and B1. No full timing rerun or relabelling of historical results is performed.

Ten-worker statistics ON is bimodal and pre-existing. The original six-pair round had faster within-mode subsets but an adverse pooled result (raw +59.41%, paired MC-adjusted +54.83%). Those post-hoc subsets do not establish an overall speedup. The fresh targeted round retained 24 processes per arm plus MemoryCache controls: slow counts were 5/24 baseline and 6/24 candidate, with one-sided Fisher p=0.5 (two-sided p=1.0). Fast/slow medians were 3.397867/8.962744 ns baseline and 3.542114/9.268523 ns candidate, increases of +4.25%/+3.41% (approximately +4.3%/+3.4%). Neither cohort is pooled with or substituted for the other.

The original stop/acceptance rule was not met: the targeted within-mode no-worse requirements failed, and shipping was stopped. The owner reviewed both rounds and explicitly accepted shipping on 2 October 2026 despite those regressions. No ten-worker statistics-ON speedup is claimed. The required new-main one-thread statistics-OFF NONE/TTL/TTI hit gate and full functional validation are recorded separately below.

## Post-H1 validation and unresolved test stall

The one-time gate on `42a4c55` retained all 72 processes / 504 measured samples: six processes per source/backend/case, including MemoryCache controls. It used the unchanged common HitProbe binary, .NET 10.0.8, Apple M4 Max and AC power mode 0. All source, binary, runtime, result and power checks passed. The stop rule remained >3% raw median regression with a strictly non-overlapping worse process range. No full timing was rerun.

| Expiry | Base median [min, max] ns/hit | Candidate median [min, max] ns/hit | Raw change | Gate |
| --- | ---: | ---: | ---: | --- |
| NONE | 12.903 [12.236, 13.718] | 12.886 [12.599, 13.717] | -0.13% | Pass |
| TTL | 20.719 [20.210, 22.145] | 20.690 [20.027, 21.104] | -0.14% | Pass |
| TTI | 21.616 [21.251, 22.635] | 21.570 [21.149, 21.989] | -0.21% | Pass |

Functional checks on the rebased candidate passed 888 core, 10 DI and 10 stress cases on each of .NET 8.0.20 and 10.0.12, plus both consumer smokes, Release build, CSharpier and actual prek hooks.

One initial net10 core run on the candidate stalled for 11 minutes 9 seconds after 776 completed cases and was terminated. Native sampling showed a GC-suspension wait and a busy managed worker; no managed frame was identified. The unchanged-source rerun passed, but that did not clear the stall.

After the hit gate, a separate 200-run functional audit used a 120-second limit per run and alternating source order. On each arm, all 50 runs of `StripedCacheCountersTests` (13 cases/run) and all 50 complete net10 core runs passed (877 cases/run on base, 888 on candidate). There were zero timeouts; no new dump was triggered. The counter implementation and suspected test class are byte-identical between arms, but the original stall is **neither proven pre-existing nor cleared**. Shipping was authorised after review of this non-reproduction and code inspection, with the unresolved stall disclosed.

The local evidence directory `artifacts/perf-ring-enqueued-count/post-h1/` retains the gate manifest/raw results, both functional attempts, native sample, all 200 audit commands/TRX/logs, source/binary hashes and audit report. Functional audit durations are not performance measurements and are not pooled with either historical timing round.
