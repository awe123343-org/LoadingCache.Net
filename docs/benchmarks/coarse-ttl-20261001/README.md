# Opt-in coarse TTL checks: 1 October 2026

On this M4 Max, enabling coarse checks on the same patched binary reduced
single-reader TTL hit time by 13–18%, with no allocation per hit. With statistics
off, .NET 10 measured 17.39 ns/op against MemoryCache's 17.77 ns/op; .NET 8
measured 19.10 against 18.75 ns/op. This is an opt-in freshness tradeoff:
expiration can be detected late without an upper bound.

## Scope

`EnableCoarseExpirationChecks()` is off by default. With `TimeProvider.System`,
eligible lock-free TTL value lookups can use a cached timestamp. TTI, TTL+TTI,
variable expiry, weak/owned values, automatic refresh, task lookups and all
precise engine work retain their existing paths.

One lazy shared background thread samples Stopwatch after requesting a 1 ms
sleep. It cannot keep the process alive, does not capture the creating request's
execution context, and is not stopped by cache disposal. There is no idle parking
or reference-counted lifecycle. Writes, refresh publication, load timeout,
cleanup and the expiration scheduler keep the precise clock.

## Method

Base is main `6876d5015b95f1039a3d93846c3c14f66057ca39`, including A/#39 and
excluding D/#40. The three variants are base, the C patch with precise defaults,
and the same C patch with coarse checks enabled. The latter two use exactly the
same frozen assemblies; only the builder option differs. Every cell uses six
independent processes per variant/library, all six variant orders, alternating
LC/MemoryCache order, three warmups and seven 250 ms measured samples.

Rows cover TTL single-reader cycle with statistics off/on and a TTI off control,
on .NET 10.0.8 and .NET 8.0.20, selected explicitly with `--fx-version`.
SDK 10.0.401 built the assemblies. Keys are preboxed objects, 1024 residents,
with one-hour expiry. MemoryCache 10.0.12 absolute/sliding rows are the controls.
The 216 formal processes ran on an Apple M4 Max from 20:14:17 to 20:24:57 UTC on
1 October 2026. They used the shared benchmark lock, with AC checks before and
after every process: AC throughout, battery 100%, power mode 0. The adapter
rating is 65 W, not measured power draw. No build or test runs overlapped timing.
The 12-process short preflight is excluded.

The ignored harness adds option setup/reporting, with no changes to the measured
reader coordinator or hot loop. Coarse TTL processes collect 5000 clock-lag
samples at a target 100 microsecond cadence after all hit timing/allocation
windows. This characterises the observed ticker on this machine; it does not
establish a maximum lag, Windows/Linux behaviour or a service guarantee.

An independent pass checked all 216 process receipts and 1512 measured samples:
runtime and binary/backend hashes, variant/backend order, counts, checksums,
statistics, zero misses and power. It recomputed the process and cohort medians
from raw samples. No process was discarded.

## Formal results

Values are medians of six process medians; brackets show the process range. Ratios are direct comparisons within this cohort.

| Case                    |              Base ns/op |         C precise ns/op |          C coarse ns/op | Precise/base | Coarse/precise | Coarse/base | LC B/op base / precise / coarse |
| ----------------------- | ----------------------: | ----------------------: | ----------------------: | -----------: | -------------: | ----------: | ------------------------------: |
| net10.0 TTL / stats off | 20.472 [19.914, 21.043] | 20.611 [20.313, 21.204] | 17.388 [17.261, 18.391] |        1.007 |          0.844 |       0.849 |        0.0000 / 0.0000 / 0.0000 |
| net10.0 TTI / stats off | 21.858 [21.212, 22.362] | 21.112 [21.005, 21.243] | 21.382 [21.002, 21.665] |        0.966 |          1.013 |       0.978 |        0.0000 / 0.0000 / 0.0000 |
| net10.0 TTL / stats on  | 26.927 [25.651, 27.192] | 25.809 [25.204, 26.788] | 21.157 [20.754, 22.274] |        0.959 |          0.820 |       0.786 |        0.0000 / 0.0000 / 0.0000 |
| net8.0 TTL / stats off  | 22.113 [21.672, 23.999] | 21.965 [21.758, 22.313] | 19.101 [18.574, 19.955] |        0.993 |          0.870 |       0.864 |        0.0000 / 0.0000 / 0.0000 |
| net8.0 TTI / stats off  | 23.149 [22.507, 25.731] | 23.069 [22.475, 23.949] | 23.352 [23.090, 24.263] |        0.997 |          1.012 |       1.009 |        0.0000 / 0.0000 / 0.0000 |
| net8.0 TTL / stats on   | 28.959 [28.300, 30.679] | 27.962 [27.315, 28.491] | 23.540 [22.754, 24.563] |        0.966 |          0.842 |       0.813 |        0.0000 / 0.0000 / 0.0000 |

TTI is a precise-clock control in every variant; enabling the option does not select coarse time for it.

### MemoryCache controls

| Case                    |           MC base ns/op |  MC precise-label ns/op |   MC coarse-label ns/op | MC precise/base | MC coarse/precise | LC coarse-label / MC coarse-label |
| ----------------------- | ----------------------: | ----------------------: | ----------------------: | --------------: | ----------------: | --------------------------------: |
| net10.0 TTL / stats off | 17.820 [17.468, 18.135] | 17.631 [17.502, 18.022] | 17.765 [17.442, 18.040] |           0.989 |             1.008 |                             0.979 |
| net10.0 TTI / stats off | 18.182 [18.138, 18.525] | 18.238 [18.113, 18.359] | 18.283 [18.127, 18.481] |           1.003 |             1.002 |                             1.169 |
| net10.0 TTL / stats on  | 18.702 [18.597, 19.277] | 18.729 [18.657, 18.931] | 18.715 [18.664, 18.864] |           1.001 |             0.999 |                             1.130 |
| net8.0 TTL / stats off  | 18.792 [18.507, 19.060] | 18.703 [18.481, 18.917] | 18.749 [18.654, 18.815] |           0.995 |             1.002 |                             1.019 |
| net8.0 TTI / stats off  | 18.807 [18.594, 18.896] | 18.580 [18.479, 18.748] | 18.827 [18.750, 18.893] |           0.988 |             1.013 |                             1.240 |
| net8.0 TTL / stats on   | 20.206 [20.049, 20.303] | 20.198 [20.120, 20.408] | 20.160 [20.073, 20.380] |           1.000 |             0.998 |                             1.168 |

### Observed ticker lag on this macOS host

These are post-timing samples from a busy sampling thread, not a latency bound or a measurement on Windows/Linux. Percentiles use nearest rank. Each case has six processes with 5000 lag samples each.

| Case                                 | Median ms | p95 ms | p99 ms | Maximum observed ms | Per-process maxima, ms                   |
| ------------------------------------ | --------: | -----: | -----: | ------------------: | ---------------------------------------- |
| net10.0-hit-write-w1-cycle-stats-off |     0.627 |  1.193 |  1.245 |               1.913 | 1.261, 1.617, 1.913, 1.515, 1.259, 1.381 |
| net10.0-hit-write-w1-cycle-stats-on  |     0.626 |  1.192 |  1.244 |               2.104 | 1.272, 1.308, 1.627, 2.104, 1.258, 1.261 |
| net8.0-hit-write-w1-cycle-stats-off  |     0.627 |  1.194 |  1.245 |               2.151 | 1.287, 2.076, 1.478, 2.151, 1.444, 1.266 |
| net8.0-hit-write-w1-cycle-stats-on   |     0.627 |  1.194 |  1.246 |               2.144 | 1.853, 1.800, 1.845, 1.966, 1.460, 2.144 |

<details>
<summary>All process medians (ns/op), in round order</summary>

| Case                    | Variant   | Library      | Six process medians                            | Median B/op |
| ----------------------- | --------- | ------------ | ---------------------------------------------- | ----------: |
| net10.0 TTL / stats off | c-base    | loadingcache | 20.838, 20.151, 21.043, 19.914, 20.312, 20.631 |      0.0000 |
| net10.0 TTL / stats off | c-base    | memorycache  | 17.587, 17.468, 17.672, 18.135, 18.057, 17.968 |      0.0000 |
| net10.0 TTL / stats off | c-precise | loadingcache | 20.614, 20.608, 20.625, 21.204, 20.540, 20.313 |      0.0000 |
| net10.0 TTL / stats off | c-precise | memorycache  | 17.581, 17.502, 17.561, 17.680, 18.022, 17.994 |      0.0000 |
| net10.0 TTL / stats off | c-coarse  | loadingcache | 17.361, 17.261, 18.391, 17.868, 17.358, 17.416 |      0.0000 |
| net10.0 TTL / stats off | c-coarse  | memorycache  | 17.442, 17.481, 17.718, 17.812, 18.040, 17.904 |      0.0000 |
| net10.0 TTI / stats off | c-base    | loadingcache | 21.994, 22.362, 21.683, 21.212, 21.723, 22.034 |      0.0000 |
| net10.0 TTI / stats off | c-base    | memorycache  | 18.193, 18.171, 18.143, 18.138, 18.525, 18.215 |      0.0000 |
| net10.0 TTI / stats off | c-precise | loadingcache | 21.005, 21.243, 21.031, 21.028, 21.193, 21.241 |      0.0000 |
| net10.0 TTI / stats off | c-precise | memorycache  | 18.297, 18.160, 18.191, 18.113, 18.286, 18.359 |      0.0000 |
| net10.0 TTI / stats off | c-coarse  | loadingcache | 21.259, 21.002, 21.505, 21.665, 21.620, 21.023 |      0.0000 |
| net10.0 TTI / stats off | c-coarse  | memorycache  | 18.457, 18.276, 18.127, 18.269, 18.290, 18.481 |      0.0000 |
| net10.0 TTL / stats on  | c-base    | loadingcache | 26.984, 27.192, 25.651, 27.176, 26.869, 25.804 |      0.0000 |
| net10.0 TTL / stats on  | c-base    | memorycache  | 18.597, 18.668, 19.277, 18.736, 18.611, 18.775 |      0.0000 |
| net10.0 TTL / stats on  | c-precise | loadingcache | 25.204, 26.788, 25.512, 26.000, 26.474, 25.618 |      0.0000 |
| net10.0 TTL / stats on  | c-precise | memorycache  | 18.794, 18.931, 18.725, 18.686, 18.733, 18.657 |      0.0000 |
| net10.0 TTL / stats on  | c-coarse  | loadingcache | 21.313, 21.002, 21.992, 20.754, 20.784, 22.274 |      0.0000 |
| net10.0 TTL / stats on  | c-coarse  | memorycache  | 18.667, 18.664, 18.682, 18.783, 18.749, 18.864 |      0.0000 |
| net8.0 TTL / stats off  | c-base    | loadingcache | 21.672, 22.037, 22.593, 21.712, 22.188, 23.999 |      0.0000 |
| net8.0 TTL / stats off  | c-base    | memorycache  | 18.763, 18.792, 19.060, 18.507, 18.791, 18.820 |      0.0000 |
| net8.0 TTL / stats off  | c-precise | loadingcache | 22.291, 21.865, 21.758, 22.313, 22.022, 21.908 |      0.0000 |
| net8.0 TTL / stats off  | c-precise | memorycache  | 18.728, 18.677, 18.784, 18.481, 18.534, 18.917 |      0.0000 |
| net8.0 TTL / stats off  | c-coarse  | loadingcache | 19.474, 18.701, 19.955, 18.574, 19.778, 18.729 |      0.0000 |
| net8.0 TTL / stats off  | c-coarse  | memorycache  | 18.765, 18.795, 18.654, 18.815, 18.703, 18.734 |      0.0000 |
| net8.0 TTI / stats off  | c-base    | loadingcache | 22.652, 25.731, 22.507, 23.253, 23.045, 23.284 |      0.0000 |
| net8.0 TTI / stats off  | c-base    | memorycache  | 18.896, 18.802, 18.812, 18.594, 18.837, 18.739 |      0.0000 |
| net8.0 TTI / stats off  | c-precise | loadingcache | 22.656, 22.855, 22.475, 23.949, 23.823, 23.284 |      0.0000 |
| net8.0 TTI / stats off  | c-precise | memorycache  | 18.567, 18.703, 18.593, 18.526, 18.479, 18.748 |      0.0000 |
| net8.0 TTI / stats off  | c-coarse  | loadingcache | 23.291, 23.329, 23.090, 23.376, 24.263, 23.404 |      0.0000 |
| net8.0 TTI / stats off  | c-coarse  | memorycache  | 18.750, 18.809, 18.844, 18.891, 18.893, 18.790 |      0.0000 |
| net8.0 TTL / stats on   | c-base    | loadingcache | 29.025, 28.893, 28.524, 29.753, 28.300, 30.679 |      0.0000 |
| net8.0 TTL / stats on   | c-base    | memorycache  | 20.049, 20.226, 20.303, 20.186, 20.175, 20.272 |      0.0000 |
| net8.0 TTL / stats on   | c-precise | loadingcache | 27.315, 28.070, 28.491, 27.670, 28.294, 27.854 |      0.0000 |
| net8.0 TTL / stats on   | c-precise | memorycache  | 20.408, 20.208, 20.188, 20.167, 20.207, 20.120 |      0.0000 |
| net8.0 TTL / stats on   | c-coarse  | loadingcache | 24.563, 23.610, 23.514, 22.754, 23.114, 23.566 |      0.0000 |
| net8.0 TTL / stats on   | c-coarse  | memorycache  | 20.238, 20.131, 20.177, 20.144, 20.380, 20.073 |      0.0000 |

</details>

## Interpretation and limits

The same-binary coarse/precise ratios are 0.844/0.820 on .NET 10 and 0.870/0.842
on .NET 8 (statistics off/on). Coarse and precise TTL process ranges do not
overlap in these four rows. MemoryCache's corresponding ratios are
1.008/0.999 and 1.002/0.998; controls are reported directly, without normalising
away LoadingCache differences.

With the option off, TTL/statistics-off precise/base ratios are 1.007 and 0.993,
with overlapping ranges. Other base-to-patch differences, including the lower
statistics-on medians, are not attributed to the option. TTI retains the precise
clock in all three variants: its coarse-label/precise-label ratios are 1.013
and 1.012, with overlapping process ranges. No TTI speedup is claimed.

Observed post-timing clock lag had a median of about 0.63 ms, p99 about 1.25 ms,
and maximum 2.15 ms across these runs. These samples used a busy sampling thread
on macOS. They do not cover starvation, long GC pauses, suspension, other
workloads or other operating systems. The lag can be arbitrarily larger;
neither a one-millisecond sleep request nor the measured maximum is a bound.

These results cover single-reader resident hits only. They do not establish
write throughput, multithreaded scaling, idle energy use or end-to-end application
performance. The shared ticker continues running after enabled caches are
disposed. The precise default remains appropriate where delayed TTL detection
is unacceptable.

## Correctness and API

Final Release build passed without warnings/errors. Core tests pass 789/789 on
both net8/net10, DI and stress 10/10 on each, and ConsumerSmoke on both.
`dotnet csharpier check src tests benchmarks` checked 148 files with exit code 0.
The 25 new cases freeze/advance a per-engine internal clock without sleeps,
reject custom providers across builder personalities/orderings, preserve TTI
coalescing and exact paths, verify precise write/refresh timestamps and load
timeout, and check shared ticker progress/monotonicity after disposing a cache.
The timeout test's watchdog is outside the cache TimeoutException assertion.
Existing tests and assertions are unchanged.

The compiler API baseline adds only `EnableCoarseExpirationChecks` (one
signature and its accessibility/init-only records). API self-test and check
pass. `global.json` is unchanged. [ADR-0017](../../adr/0017-coarse-ttl-checks.md)
records the scope, timing contract and ticker lifetime.

## Reproduction and evidence

Ignored source/build/validation records live in
`artifacts/perf-coarse-expiration`. The reusable harness is
`artifacts/perf-expiry-baseline`, with frozen labels `c-base`, `c-final` and the
same-binary configuration aliases `c-precise`/`c-coarse`. All raw JSON, probes
and binaries remain ignored. Formal results are in
`artifacts/perf-expiry-baseline/results/c-formal-ac-20261001`; preflight is
separate. This published report contains all process medians above.

The harness builds against arbitrary worktrees through `-p:LcRoot=<path>` and
locks each build/process internally. Do not wrap the runner in a second copy
of the shared lock. A completed cohort or build label is never overwritten.
The formal command from the repository root was:

```sh
uv run --no-project python3 artifacts/perf-expiry-baseline/matrix.py run \
  --cohort c-formal-ac-20261001 \
  --variants c-base c-precise c-coarse \
  --tfms net10.0 net8.0 --suite hit \
  --case-filter '^net(10|8)[.]0-hit-(write-w1-cycle-stats-(off|on)|access-w1-cycle-stats-off)$' \
  --rounds 6 --warmups 3 --runs 7
```

`analyse_hits.py` in the C evidence directory performs the independent receipt
validation. `generate_results.py` writes the tables and lag percentiles. Final
test/format receipts are in `final-validation/results.json`; API receipts are
in `api-validation/summary.json`.
