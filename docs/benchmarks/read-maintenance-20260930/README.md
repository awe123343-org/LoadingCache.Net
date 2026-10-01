# Read-maintenance backlog experiment (30 September 2026)

Status: direction 1 qualification is complete with the quarter-stripe threshold. Locality/theory trace criteria, direct AC timing, net8/hot-key coverage and final SDK 10.0.401 correctness/formatter checks pass. The restricted three-way follow-up satisfies decision rule 2; its ten-reader/statistics-OFF result replaces that cell of the earlier direct cohort. The implementation PR targets main. The separate #36 amendment remains deferred pending user approval.

The candidate was implemented on `perf/read-maintenance-backlog` from `adb755b2b50b8090faef9f96d01cc4267a8a5470` (`perf/hot-path-gaps`). #35 has since merged as `05e592b04a5989f5a57abf024ac681c99dec2f53`, with an identical file tree. Before delivery the uncommitted work was preserved and the branch moved to current main, `a179760b1a3de84df3352609b8898d94c63785da`; its sole additional change is TUnit 1.72.4 → 1.72.10. The final full validation uses that dependency version and SDK 10.0.401. The intended PR base is now `main`, with exactly one new commit. Background read maintenance re-arms only when a stripe has a published head and at least a quarter of its capacity reserved. Explicit cleanup and the synchronous cleanup after initially rejected read scheduling use `HasPublished`. ADR-0014 R1-R3 and ADR-0006 record the approved change; the 256-event/32-pass budgets remain unchanged.

The final source/assembly is frozen as `quarter-final`; its [manifest](../../../artifacts/perf-read-maintenance/quarter-final-manifest.json) matches all four current production files and the two tested test files. Compared with the measured quarter spike, executable source is unchanged; only the read-buffer XML summary now says quarter instead of half. Earlier snapshots and their evidence are retained. The direct final comparison uses this new frozen binary rather than reusing an inferred ratio.

## Current power and continuation rules

The user connected a 65 W adapter after the battery diagnostic; Claude confirmed AC began at 22:00:15 local time. A subsequent live `pmset` check reports AC Power with Low Power Mode disabled. The adapter's 65 W rating is user-confirmed, not a measurement of available sustained power or system draw. The report must retain that limitation, particularly for ten-reader loads.

Every timed batch and every process must check `pmset` before starting; if the source is not AC, stop and report instead of running. Each process also captures power afterward, including the charging/discharging text and battery percentage. A transition ends the cohort; do not mix power states or use a process that spans a transition. Hit-ratio replay does not qualify timing results. The two completed battery cohorts below remain appendices and must not supply the PR's formal AC performance numbers.

Reproduction commands must use fresh evidence cohorts with the recorded immutable binaries. Do not rebuild or run other benchmarks concurrently. The root `global.json` has no diff and the installed active SDK is now 10.0.401. No further local override is needed; ignored historical build snapshots retain their original 10.0.300 setup.

The first final-comparison launch (`final-net10-cycle-ac-01`) ended before any samples: even the initial MemoryCache child and `dotnet --list-runtimes` were killed while the local .NET environment was being repaired. The user confirmed repair before work resumed. Its failed receipt and empty log remain retained; it is not a completed benchmark. Repair installed SDK 10.0.401 and runtime 10.0.12 alongside the old versions. Subsequent timing explicitly pins .NET 10.0.8 (or .NET 8.0.20) with `dotnet exec --fx-version`, checks the runtime reported by every timed process, and retains the original frozen binaries. The launcher changed, but the benchmark CLR did not. See the [post-repair environment](../../../artifacts/perf-read-maintenance/environment-repaired.json).

## Follow-up outside this PR: baseline multithreaded slow mode

Track this under directions 1 and 6 of `docs/performance-gaps-20260930.md`: in the diagnostic's .NET 10, ten-reader, statistics-ON workload, base enters the slow mode in 6/12 processes. The median cost rises from 2.375 to 10.708 ns/op, about 4.5×, with higher accepted-read fractions and more maintenance passes per read. Half-full also has this mode (5/12); the approved attribution is an existing performance gap, not a new patch regression.

Later work should establish the cause before selecting a fix, including the read transport's per-offer ring selection, accepted fraction and maintenance work. The recorded statistics/drop stripe mappings have no collisions and do not explain the modes. Do not expand this PR into that investigation. The handoff document exists on the separate `docs/perf-gap-handoff` branch; a focused update patch is retained separately to avoid adding that entire document to this implementation PR.

The prepared [handoff follow-up patch](../../../artifacts/perf-read-maintenance/handoff-follow-up.patch) adds this item to both direction 1 and direction 6. The separate amendment and push to #36 are deferred pending user approval. Its report reference must use a fixed implementation-commit URL. The patch has not been applied to that separate branch or committed.

## Direct final-quarter AC .NET 10 cycling comparison

The completed `final-net10-cycle-ac-02` cohort used six independent processes per variant/configuration, alternating order and a preceding MemoryCache control per LoadingCache process (96 processes total). All 192 power snapshots report AC, power mode 0 and 100% battery; the user-confirmed adapter rating is 65 W. All processes report .NET 10.0.8, the expected frozen assembly/harness hashes, zero misses and valid accounting. The launcher was updated during the environment repair; the CLR was explicitly pinned to the same 10.0.8 version as the earlier cohorts. No builds or other benchmarks ran concurrently.

Costs are medians of process medians, with their ranges, in ns/op. The ten-reader/statistics-ON row uses only the predeclared fast mode; slow observations remain separately reported below.

| Readers | Statistics | Base ns/op | Final quarter ns/op | Quarter/base | MemoryCache control ratio |
| --- | --- | --- | --- | ---: | ---: |
| 1 | Off | 15.844 [14.602, 18.570] | 15.170 [14.539, 16.108] | 0.957 | 1.003 |
| 1 | On | 21.397 [19.326, 28.633] | 18.795 [18.665, 21.916] | 0.878 | 1.007 |
| 10 | Off | 2.022 [1.968, 2.058] | 1.294 [1.260, 1.337] | 0.640 | 1.012 |
| 10 | On, fast mode | 3.266 [3.129, 3.348] | 2.424 [2.258, 2.536] | 0.742 | 0.994 |

The statistics-ON slow-mode frequencies are base **0/6**, final quarter **2/6**; the two slow final-quarter process medians are 11.384 and 11.670 ns/op. The earlier 24-process diagnostic found base 6/12 and half 5/12, establishing the existing slow-mode risk under the agreed attribution rule. This formal cohort does not establish population frequencies or measure read-acceptance correlates. Do not report its complete mixed distribution as unchanged or as an overall regression.

This completed cohort prompted the restricted follow-up below: ten-reader/statistics-OFF final-quarter costs are 1.260–1.337 ns across all six processes, versus base 1.968–2.058 ns. The ratio is 0.640, while the MemoryCache role ratio is 1.012. The earlier quarter snapshot measured 2.029 ns (range 1.985–2.095; see its retained raw summary for exact values) and overlapped half. The final-quarter statistics-ON fast mode is also lower (2.424 ns) than the earlier quarter fast mode (3.168 ns). These observations are retained but are **not yet attributed to the scheduling change**.

The pre-follow-up checks established that all core source files in quarter versus quarter-final are identical except the XML summary changing “half” to “quarter”; executable source is unchanged. Global/Directory.Build/Directory.Packages files match. Restore assets, generated assembly information and generated editor configuration match after normalising snapshot paths. Current production/test hashes still match the final manifest. The binaries have different hashes as recorded. Runtime/settings/harness and workload parameters match, but the launcher changed with the repair. The subsequent IL comparison and restricted timing result below resolve the agreed attribution decision; a precise native-code or launcher mechanism was not investigated.

Claude authorised a restricted follow-up on the repaired host: base, old quarter and quarter-final, ten readers/statistics OFF only, six processes each, all six order permutations and preceding MemoryCache controls. The read-only PE comparison found all 1,590 method bodies and both static RVA initializers identical between old and final quarter. All metadata bytes are also identical after zeroing the module MVID, including token targets, signatures, constants, attributes and field/type layouts; CLR flags match. The helper never loads or executes either candidate. This establishes executable IL/metadata equivalence, not identical native JIT or physical object layout. The completed timing follow-up below meets decision rule 2: the old/final-quarter difference disappears, so use the contemporaneous three-way cohort for the ten-reader/statistics-OFF comparison. It does not meet the binary-specific criterion for classifying an incidental layout/alignment effect. This does not reopen the earlier base/half slow-mode diagnosis. See the [IL comparison](../../../artifacts/perf-read-maintenance/quarter-il-comparison.json) and ignored `il-compare` helper source.

Evidence: [summary](../../../artifacts/perf-read-maintenance/final-net10-cycle-ac-02/summary.json), [per-process medians](../../../artifacts/perf-read-maintenance/final-net10-cycle-ac-02/process-summary.csv), [commands and power](../../../artifacts/perf-read-maintenance/final-net10-cycle-ac-02/processes.jsonl).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net10.0 --framework-version 10.0.8 --patterns cycle --variants base quarter-final --cohort final-net10-cycle-ac-02
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py final-net10-cycle-ac-02
```

### Restricted three-way follow-up after environment repair

Base, old quarter and quarter-final each ran six processes at ten readers/statistics OFF, with the same .NET 10.0.8 CLR, immutable harness, cycling keys and six balanced variant-order permutations. Each LoadingCache process had its own preceding MemoryCache control: 36 processes in total. All 72 process-boundary snapshots show AC, power mode 0 and 100% battery; adapter rating is 65 W. All correctness, runtime and binary-identity checks passed.

| Variant | Median [range] ns/op | Variant/base | MemoryCache median ns/op | Control/base-role |
| --- | --- | ---: | ---: | ---: |
| Base | 1.985 [1.915, 2.024] | 1.000 | 34.990 | 1.000 |
| Old quarter | 1.253 [1.189, 1.349] | 0.631 | 36.128 | 1.033 |
| Final quarter | 1.243 [1.174, 1.326] | 0.626 | 36.156 | 1.033 |

Old and final quarter now overlap: final/old is **0.992**, with a MemoryCache control ratio of **1.001**. Base remains near its previous 2.0 ns level; it has not shifted down with the old quarter. Combined with byte-identical executable IL/metadata, this meets the predeclared **decision rule 2** (old quarter also becomes fast). The prior difference between the two quarter cohorts is therefore treated as an environment/launcher-stage effect, not an effect tied to the final binary. This limited experiment does not identify which environmental or launch condition changed; no native-code layout or false-sharing mechanism was measured, and investigation stops here.

The selected ten-reader/statistics-OFF number is this simultaneous final-quarter/base ratio **0.626**, not the earlier 0.640. Its MemoryCache role ratio is 1.033 and control-normalised ratio is 0.624. These are measurements for this cohort, not a claim that the gain is stable across builds or execution environments. Single-reader direct ratios remain 0.957/0.878, and the statistics-ON fast-mode ratio/frequencies remain separately reported above. Earlier unfavourable or different cohorts remain retained. The approved next step is net8/hot-key qualification, without broadening this investigation.

Evidence: [three-way summary](../../../artifacts/perf-read-maintenance/layout-three-way-net10-ac-01/summary.json), [all process medians](../../../artifacts/perf-read-maintenance/layout-three-way-net10-ac-01/process-summary.csv), [commands/power](../../../artifacts/perf-read-maintenance/layout-three-way-net10-ac-01/processes.jsonl), [IL/metadata comparison](../../../artifacts/perf-read-maintenance/quarter-il-comparison.json).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net10.0 --framework-version 10.0.8 --patterns cycle --workers 10 --statistics off --variants base quarter quarter-final --cohort layout-three-way-net10-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py layout-three-way-net10-ac-01
```

## Final quarter: .NET 8 cycling and hot key

This direct base/quarter-final cohort fixed runtime 8.0.20 with `--fx-version` and used the frozen harness and final core assembly. Each configuration has six processes per variant, alternating round order and a preceding MemoryCache control (192 processes total). All 384 process-boundary snapshots report AC, power mode 0 and 100% battery; the user-confirmed adapter rating is 65 W. Every runtime, assembly, bounds, zero-miss and request-accounting check passed. No builds or other benchmarks ran concurrently.

Costs are median [range] of process medians in ns/op. For ten readers/statistics ON, the table displays the fast bin (<5 ns), with frequencies and slow costs retained separately. The 5 ns cutoff is carried over numerically from the original .NET 10 cycling diagnostic; for other runtime/pattern combinations this is a descriptive split, not an independent diagnosis of the mechanism.

| Pattern | Readers | Statistics | Base ns/op | Quarter ns/op | Quarter/base | MemoryCache control ratio |
| --- | --- | --- | --- | --- | ---: | ---: |
| Cycling | 1 | Off | 19.367 [18.002, 21.970] | 17.342 [16.510, 18.993] | 0.895 | 1.000 |
| Cycling | 1 | On | 24.398 [21.232, 28.532] | 21.992 [20.080, 25.879] | 0.901 | 1.001 |
| Cycling | 10 | Off | 2.276 [2.025, 2.368] | 1.519 [1.423, 1.609] | 0.667 | 1.006 |
| Cycling | 10 | On, fast bin | 3.399 [3.303, 3.436] | 2.313 [2.270, 2.346] | 0.680 | 0.987 |
| Hot key | 1 | Off | 24.788 [21.699, 26.776] | 18.927 [18.748, 19.225] | 0.764 | 0.997 |
| Hot key | 1 | On | 24.623 [23.816, 25.743] | 22.696 [22.299, 24.818] | 0.922 | 1.003 |
| Hot key | 10 | Off | 1.891 [1.872, 2.107] | 1.854 [1.690, 1.936] | 0.980 | 1.024 |
| Hot key | 10 | On, fast bin | 2.749 [2.514, 2.868] | 2.594 [2.495, 2.805] | 0.944 | 1.012 |

| Ten-reader/statistics-ON pattern | Variant | ≥5 ns processes | Slow-bin median [range] ns/op |
| --- | --- | ---: | --- |
| Cycling | Base | 0/6 | None sampled |
| Cycling | Quarter | 3/6 | 19.729 [16.371, 21.111] |
| Hot key | Base | 0/6 | None sampled |
| Hot key | Quarter | 0/6 | None sampled |

Retain every observed slow process and frequency difference. Mixed-bin medians are not representative costs, and six processes do not establish population-frequency equality. The earlier .NET 10 base/half diagnostic established that the baseline can enter a slow mode, but no additional instrumented runtime/hot-key diagnosis was run. Multireader gains remain subject to the environment sensitivity documented by the earlier cohorts.

Evidence: [summary](../../../artifacts/perf-read-maintenance/final-net8-cycle-hot-ac-01/summary.json), [process medians](../../../artifacts/perf-read-maintenance/final-net8-cycle-hot-ac-01/process-summary.csv), [commands/power](../../../artifacts/perf-read-maintenance/final-net8-cycle-hot-ac-01/processes.jsonl).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net8.0 --framework-version 8.0.20 --patterns cycle hot --variants base quarter-final --cohort final-net8-cycle-hot-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py final-net8-cycle-hot-ac-01
```

## Final quarter: .NET 10 hot key

This direct base/quarter-final cohort fixed runtime 10.0.8 with `--fx-version` and used the frozen harness and final core assembly. Each configuration has six processes per variant, alternating round order and a preceding MemoryCache control (96 processes total). All 192 process-boundary snapshots report AC, power mode 0 and 100% battery; the user-confirmed adapter rating is 65 W. Every runtime, assembly, bounds, zero-miss and request-accounting check passed. No builds or other benchmarks ran concurrently.

Costs are median [range] of process medians in ns/op. For ten readers/statistics ON, the table displays the fast bin (<5 ns), with frequencies and slow costs retained separately. The 5 ns cutoff is carried over numerically from the original .NET 10 cycling diagnostic; for other runtime/pattern combinations this is a descriptive split, not an independent diagnosis of the mechanism.

| Pattern | Readers | Statistics | Base ns/op | Quarter ns/op | Quarter/base | MemoryCache control ratio |
| --- | --- | --- | --- | --- | ---: | ---: |
| Hot key | 1 | Off | 18.855 [15.014, 24.489] | 15.518 [14.871, 16.059] | 0.823 | 0.999 |
| Hot key | 1 | On | 20.427 [20.240, 20.970] | 19.328 [17.559, 20.097] | 0.946 | 0.998 |
| Hot key | 10 | Off | 1.452 [1.422, 1.594] | 1.463 [1.420, 1.687] | 1.008 | 0.974 |
| Hot key | 10 | On, fast bin | 2.321 [2.104, 2.365] | 2.285 [2.154, 2.673] | 0.984 | 1.005 |

| Ten-reader/statistics-ON pattern | Variant | ≥5 ns processes | Slow-bin median [range] ns/op |
| --- | --- | ---: | --- |
| Hot key | Base | 0/6 | None sampled |
| Hot key | Quarter | 0/6 | None sampled |

Retain every observed slow process and frequency difference. Mixed-bin medians are not representative costs, and six processes do not establish population-frequency equality. The earlier .NET 10 base/half diagnostic established that the baseline can enter a slow mode, but no additional instrumented runtime/hot-key diagnosis was run. Multireader gains remain subject to the environment sensitivity documented by the earlier cohorts.

Evidence: [summary](../../../artifacts/perf-read-maintenance/final-net10-hot-ac-01/summary.json), [process medians](../../../artifacts/perf-read-maintenance/final-net10-hot-ac-01/process-summary.csv), [commands/power](../../../artifacts/perf-read-maintenance/final-net10-hot-ac-01/processes.jsonl).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net10.0 --framework-version 10.0.8 --patterns hot --variants base quarter-final --cohort final-net10-hot-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py final-net10-hot-ac-01
```

## Retained half control: AC .NET 10 cycling A/B

This completed cohort is retained as a half-threshold control. The selected-quarter PR numbers come from the direct comparisons above. It uses the maintained, uninstrumented HitProbe, preboxed keys, 1,024 residents/capacity, cycling hits without expiration, one/ten readers and statistics off/on. Each configuration has six independent processes per variant, alternating round order, with a preceding MemoryCache control per LoadingCache process; each process has three warm-ups and seven 250 ms measured intervals. There were 96 processes, with no concurrent builds or other benchmarks. All 192 before/after snapshots report AC Power, power mode 0, battery 93–94% and charging. The adapter rating is 65 W. Every process passed the harness checks and source/assembly identity verification.

Costs are medians of the six process medians, with process-median ranges, in ns/op. The MemoryCache cell gives its median for the base-role / half-role controls; its ratio is half-role/base-role. Battery results remain separate appendices.

| Readers | Statistics | Base ns/op | Half ns/op | Half/base | MemoryCache ns/op, base / half role | Control ratio |
| --- | --- | --- | --- | ---: | --- | ---: |
| 1 | Off | 14.934 [14.541, 15.925] | 14.499 [13.598, 15.301] | 0.971 | 17.967 / 18.062 | 1.005 |
| 1 | On | 19.654 [18.688, 22.296] | 18.467 [17.908, 20.549] | 0.940 | 18.900 / 18.924 | 1.001 |
| 10 | Off | 2.036 [1.936, 2.137] | 2.019 [1.942, 2.044] | 0.992 | 34.768 / 36.168 | 1.040 |
| 10 | On, fast mode | 3.104 [3.004, 3.291] | 3.224 [3.104, 3.288] | 1.039 | 38.239 / 37.885 | 0.991 |

Single-reader median costs are approximately 3% and 6% lower, with overlapping process ranges. Ten-reader statistics-off costs are approximately unchanged. Statistics-on must be read with the known bimodality: this formal cohort sampled **0/6 slow base processes and 3/6 slow half processes**, using the predeclared 5 ns cutoff. The full half median (7.852 ns) lies between modes and is not representative of either mode. The table therefore compares fast-mode costs; the slow-mode frequencies remain explicit. The mixed-mode ratio of 2.53 is not interpreted as a regression.

| Ten-reader statistics-ON mode | Base | Half |
| --- | --- | --- |
| Fast mode, median [range] ns/op | 3.104 [3.004, 3.291], n=6 | 3.224 [3.104, 3.288], n=3 |
| Slow mode, median [range] ns/op | None sampled | 12.550 [12.415, 12.867], n=3 |

The earlier limited diagnostic established that the base also has a slow mode, so this is a recurrence of the known process-bimodality risk. Formal diagnostics were off; this cohort does not measure the accepted/drained correlate or establish population mode frequencies. The MemoryCache controls did not show the same slow-process split. Do not discard those candidate processes, describe the complete ten-reader statistics-ON cohort as unchanged, or infer a mode-frequency improvement. No multi-reader speedup is claimed. The remaining authorised configurations will be reported separately.

Evidence: [summary including both modes](../../../artifacts/perf-read-maintenance/formal-net10-cycle-ac-01/summary.json), [process medians](../../../artifacts/perf-read-maintenance/formal-net10-cycle-ac-01/process-summary.csv), [commands and power](../../../artifacts/perf-read-maintenance/formal-net10-cycle-ac-01/processes.jsonl).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net10.0 --patterns cycle --cohort formal-net10-cycle-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py formal-net10-cycle-ac-01
```

## Quarter-full timing comparison

The then-selected half-full candidate and the quarter-full snapshot ran the same .NET 10 cycling matrix: six processes per variant in each one/ten-reader × statistics-off/on case, with alternating order and a preceding MemoryCache control. The only production-code difference in the quarter snapshot is `_capacity >> 2` in the backlog threshold. There were 96 processes, all on AC with power mode 0 and battery 95–96%; the adapter rating is 65 W. All validation and binary identity checks passed.

Cells show median [minimum, maximum] of process medians in ns/op. Ratios are quarter/half; the control ratio compares the corresponding MemoryCache roles.

| Readers | Statistics | Half ns/op | Quarter ns/op | Quarter/half | MemoryCache control ratio |
| --- | --- | --- | --- | ---: | ---: |
| 1 | Off | 13.851 [12.981, 13.970] | 13.801 [13.351, 14.549] | 0.996 | 0.997 |
| 1 | On | 17.765 [17.297, 20.037] | 17.312 [16.341, 18.032] | 0.975 | 0.989 |
| 10 | Off | 1.989 [1.941, 2.034] | 2.029 [1.985, 2.095] | 1.021 | 0.993 |
| 10 | On | 3.139 [3.020, 13.738] | 3.170 [3.069, 13.622] | 1.010 | 0.982 |

The quarter threshold has no consistent timing advantage in this cohort: a small single-reader statistics-on reduction comes with small ten-reader increases, and all process ranges overlap. Control-normalised ratios are 1.001, 0.986, 1.034 and 1.028 in the same row order. The ten-reader statistics-on case includes two slow half processes and one slow quarter process; the fast-mode medians are 3.112/3.168 ns, while the retained slow costs are 12.527–13.738 ns (half) and 13.622 ns (quarter). These small samples do not establish a difference in mode frequencies. Claude selected quarter-full after reviewing timing and trace results: at overlapping timing costs, it postpones fewer observations and makes a smaller change to the previous scheduling contract. The selection is based on that contract preference, not a claimed timing win.

The three-variant trace comparison follows in a separate cohort, with all six base/half/quarter process order permutations balanced over six rounds. Both thresholds were checked against the same contemporaneous base and passed the clarified locality/theory criteria.

Evidence: [timing summary](../../../artifacts/perf-read-maintenance/quarter-net10-cycle-ac-01/summary.json), [process medians](../../../artifacts/perf-read-maintenance/quarter-net10-cycle-ac-01/process-summary.csv), [commands/power](../../../artifacts/perf-read-maintenance/quarter-net10-cycle-ac-01/processes.jsonl).

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py quarter --tfm net10.0 --patterns cycle --cohort quarter-net10-cycle-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py quarter-net10-cycle-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py trace --tfm net10.0 --variants base half quarter --cohort quarter-trace-ac-01
```

### Quarter-full trace comparison and accepted threshold decision

The same maintained MemoryCacheProbe replayed base, half and quarter on the six fixed input traces. Each variant has six independent processes per trace, one warm-up and three measured replays per process. All six variant-order permutations occur once per trace. There were 108 processes and 324 measured replays; every value, accounting, final-capacity, source/assembly and input-identity check passed. All 216 process-boundary power snapshots show AC, power mode 0 and battery 96–97%, with the user-confirmed 65 W adapter. This is an independent cohort, not pooled with earlier trace results.

Values are mean hit percentages across process means. Deltas are percentage points against the same contemporary base.

| Trace | Base hit % | Half hit % | Quarter hit % | Half−base pp | Quarter−base pp | Quarter−half pp |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| scan | 0.0000 | 0.0000 | 0.0000 | +0.0000 | +0.0000 | +0.0000 |
| uniform | 25.1052 | 25.0777 | 25.0987 | -0.0275 | -0.0064 | +0.0210 |
| zipf | 87.8963 | 87.8999 | 87.8596 | +0.0036 | -0.0367 | -0.0403 |
| hotset-scan | 74.6856 | 74.6407 | 74.6333 | -0.0449 | -0.0524 | -0.0074 |
| phase | 99.6094 | 99.6094 | 99.6094 | +0.0000 | +0.0000 | +0.0000 |
| cycle | 84.1701 | 83.8185 | 84.4777 | -0.3516 | +0.3076 | +0.6592 |

Uniform stays close to its approximate 25% theory value in all three variants; scan is exactly zero. No new uniform diagnostic is needed. The decision concerns cycle: half is down 0.3516 pp and quarter is up 0.3076 pp, a quarter-minus-half difference of +0.6592 pp. Cycle process-mean ranges overlap: base 83.1492–85.2980%, half 83.3304–84.0993%, quarter 83.2207–86.5316%. The earlier base/half trace cohort had a smaller cycle delta of -0.1436 pp.

Claude clarified the original intended rule: **the equally weighted mean of the four locality traces must be ≥ -0.25 pp, and every individual class must be ≥ -0.5 pp**. This clarification did not change the acceptance rule to accommodate the result. Both thresholds pass:

| Locality aggregation | Half−base | Quarter−base |
| --- | ---: | ---: |
| Equal-weight mean of four trace deltas, pp | -0.0982 | +0.0546 |
| Worst locality-class delta, pp | -0.3516 | -0.0524 |

The two cycle comparisons (-0.1436 and -0.3516 pp for half) have process ranges about 2–3 pp wide and were accepted as noise. Quarter is selected because it stays closer to the previous scheduling contract at indistinguishable measured timing: less of each stripe can remain delayed, while its trace results also pass (worst class -0.0524 pp). No speedup claim is inferred from choosing quarter. The direct base-versus-quarter timing and runtime/hot-key coverage above use the final frozen candidate.

Evidence: [all variants and paired deltas](../../../artifacts/perf-read-maintenance/quarter-trace-ac-01/summary.json), [per-process hit counts](../../../artifacts/perf-read-maintenance/quarter-trace-ac-01/process-summary.csv), [commands/power](../../../artifacts/perf-read-maintenance/quarter-trace-ac-01/processes.jsonl).

## Trace hit-ratio qualification

Claude approved these acceptance rules after reviewing the first cohort:

- Locality traces (`zipf`, `hotset-scan`, `cycle`, `phase`): the equally weighted mean of the four candidate-minus-base hit-ratio deltas must be at least -0.25 percentage points; every individual class must also be at least -0.5 percentage points. The initial half cohort and the later half/quarter cohort pass.
- Non-locality traces (`uniform`, `scan`): compare with the theoretical value, not with base's observed ratio. Uniform has a keyspace of 4,096, so the full-cache theoretical hit ratio is approximately 1,024 / 4,096 = 25%. Scan has no repeated key and therefore zero hits.
- The limited uniform diagnostic is complete and accepted after review. Its hit/occupancy correlation and reversed half-minus-base delta support treating the original uniform difference as residency fluctuation rather than a policy-quality regression. Do not investigate the original base extreme further. No separate hotset-scan diagnostic is needed.
- The quarter-full comparison uses the same rule and passed. Claude selected quarter as the final production threshold because it delays fewer tail reads at equivalent measured timing.

`LoadingCache.MemoryCacheProbe` replayed six synthetic traces through the actual engine with its normal asynchronous maintenance. Capacity was 1,024, each trace contained 262,144 requests with input seed 419, statistics were off and replay was single-threaded. Each variant ran six independent processes per trace, alternating order by round, with one discarded warm-up and three measured replays per process. Each replay starts with an empty cache. There were 72 completed processes and 216 measured replays. This is hit-ratio qualification, not a throughput comparison or a deterministic policy oracle.

All 144 before/after power snapshots report AC Power, power mode 0 and battery 85%. The adapter rating is 65 W as confirmed by the user. All values, hit/miss accounting and final capacity checks passed. Every recorded core/harness assembly hash and source-manifest entry matches the intended immutable snapshot. Within each workload, all variants and processes have identical trace hashes and checksums.

Each cell is the mean hit ratio across the six process means, followed by the minimum and maximum process means, in percent. Each process mean gives equal weight to its three same-length replays. Delta is half minus base, in **percentage points**; negative values are retained.

| Trace | Base hit % | Half-full hit % | Delta, percentage points | Lower half/base pairs |
| --- | --- | --- | ---: | ---: |
| scan | 0.0000 [0.0000, 0.0000] | 0.0000 [0.0000, 0.0000] | 0.0000 | 0/6 |
| uniform | 25.6991 [25.1511, 27.1327] | 25.1620 [25.0547, 25.3895] | -0.5371 | 5/6 |
| zipf | 87.8980 [87.7112, 88.0109] | 87.8718 [87.6498, 87.9902] | -0.0262 | 4/6 |
| hotset-scan | 74.6983 [74.6128, 74.7391] | 74.6641 [74.6503, 74.6782] | -0.0343 | 5/6 |
| phase | 99.6094 [99.6094, 99.6094] | 99.6094 [99.6094, 99.6094] | 0.0000 | 0/6 |
| cycle | 84.2892 [83.3789, 84.8690] | 84.1456 [83.5875, 84.9402] | -0.1436 | 4/6 |

Uniform's paired changes are -0.0959, +0.0097, -0.1045, -0.7796, -0.2908 and -1.9616 percentage points. Its base processes also show a wider range than half-full. The existing probe checks occupancy after replay and cleanup; it does not record the resident-count trajectory, maintenance passes or read acceptance throughout replay. These results therefore do not establish whether the difference comes from delayed policy observations, asynchronous resident overshoot, or another scheduling effect. None of those explanations is asserted as the cause.

The negative deltas are retained. The locality cases pass Claude's subsequently approved criteria; the uniform difference is accepted as residency fluctuation based on the limited occupancy evidence below. Formal timing is authorised to resume. Neither above-theory uniform hits nor scan results imply better eviction-policy quality.

Evidence: [cohort summary](../../../artifacts/perf-read-maintenance/trace-ac-01/summary.json), [per-process hit ratios and counts](../../../artifacts/perf-read-maintenance/trace-ac-01/process-summary.csv), [commands and power snapshots](../../../artifacts/perf-read-maintenance/trace-ac-01/processes.jsonl). Raw data remains ignored.

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py trace --tfm net10.0 --cohort trace-ac-01
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-cohort.py trace-ac-01
```

### Limited uniform occupancy diagnostic

The approved single diagnostic cohort ran six base and six half-full processes in alternating round order, with the same capacity 1,024, keyspace 4,096, seed 419 and 262,144-request uniform trace. Each process has one warm-up and three measured replays, giving 36 measured replays. All process-boundary power snapshots report AC and power mode 0; adapter rating is 65 W. No hotset-scan diagnostic or additional cohort was run.

Only an ignored temporary copy of the maintained probe changed. Production binaries remained byte-identical to their frozen snapshots and statistics stayed off. Every 512 requests, the replay thread sampled the authoritative strong-key dictionary's Count; this trace has only synchronous Put/TryGet, no loading flights, references or expiration. Count's delegate is resolved before replay. The sampled peak is a lower bound on the actual peak. The estimated fraction of time above capacity holds the previous sampled Count until the next observation; it is an approximation, not continuous measurement. The report also retains request-spaced mean Count and sample proportions.

Accepted and drained observations are the ring reservation/consumption cursor deltas, captured before explicit cleanup under the consumer gate; the single producer has finished publishing at that boundary. Maintenance counts are captured at replay boundaries. Each cache is newly constructed and never cleared or reset during replay. No production statistics or extra hot-path counters were enabled. Count sampling itself consumed 2.10–5.15% of observed replay time, so the sampler can affect asynchronous scheduling; these diagnostic hit ratios are not pooled with the original uninstrumented trace cohort. No timing claims use this temporary probe.

| Metric | Base | Half-full |
| --- | ---: | ---: |
| Mean hit ratio across six process means | 25.1123% | 25.1801% |
| Process-mean hit ratio range | 25.0528–25.2443% | 25.0490–25.5796% |
| Maximum sampled Count | 1,278 | 1,280 |
| Mean estimated time above capacity | 62.62% | 55.91% |
| Process range of estimated time above capacity | 57.60–67.52% | 44.17–65.58% |
| Mean request-spaced sampled Count | 1,028.55 | 1,030.83 |
| Sampled mean Count / 4,096 | 25.1111% | 25.1668% |
| Accepted observations, sum of measured replays | 1,163,567 | 1,143,857 |
| Drained observations, sum of measured replays | 1,163,567 | 1,143,857 |
| Maintenance passes, sum of measured replays | 949,574 | 771,113 |
| Count samples | 9,234 | 9,234 |

All value, accounting, final-capacity, trace identity and assembly/source checks passed. Every sampled Count stayed within the existing N+B bound of 1,024+256. This is an observation of sampled counts, not a new proof of the bound between samples.

The half-full round-3 process has the largest mean sampled Count (1,046.84) and mean hit ratio (25.5796%); Count / keyspace predicts 25.5576%. Across the twelve process means, hit ratio and mean sampled Count have Pearson correlation 0.942 (0.934 over all 36 replays). Aggregate hit ratios are also close to sampled mean Count / keyspace. This supports the explanation that excess uniform hits accompany additional residency. It does not prove the cause of the original uninstrumented base process's 27.13% mean, which this diagnostic did not reproduce.

The original automatic-continuation condition was not met: base spent a larger sampled fraction of time above capacity, but half had a higher mean Count and was farther from nominal 25% on average. This was reported before continuing. Claude subsequently accepted the limited conclusion: uniform hit ratio tracks mean residency (r≈0.94), and half-minus-base changed from -0.5371 to +0.0678 percentage points, so the earlier negative difference is treated as noise from residency fluctuation rather than policy degradation. Neither variant's above-theory uniform hits count as better eviction policy. The particular base 27.13% extreme was not directly reproduced and will not be investigated further.

Maintenance passes fell from 949,574 to 771,113, a **18.79% reduction**, consistent with direction 1's intended reduction in maintenance work. This is diagnostic work-count evidence, not a timing speedup. Together with the locality-trace acceptance, Claude authorised the remaining AC formal timing, quarter-full comparison and runtime/hot-key coverage.

Evidence: [summary](../../../artifacts/perf-read-maintenance/uniform-diagnostic-ac-01/summary.json), [all twelve process rows](../../../artifacts/perf-read-maintenance/uniform-diagnostic-ac-01/process-summary.csv), [replay details](../../../artifacts/perf-read-maintenance/uniform-diagnostic-ac-01/replay-summary.json), [commands/power](../../../artifacts/perf-read-maintenance/uniform-diagnostic-ac-01/processes.jsonl), [temporary-probe source/build hashes](../../../artifacts/perf-read-maintenance/uniform-probe-manifest.json). The source is under ignored `artifacts/perf-read-maintenance/uniform-probe`; the root SDK file is unchanged.

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-uniform-diagnostics.py
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-uniform-diagnostics.py
```

## Appendix A: battery formal A/B method

Apple M4 Max, 14 logical processors, 36 GiB, macOS 27.0.1; SDK 10.0.300 and .NET 10.0.8 ARM64, workstation GC, default tiered JIT/PGO settings. The machine was on battery at the environment capture. No builds or tests ran alongside these measurements.

The maintained `LoadingCache.HitProbe` ran preboxed object keys, capacity/residents 1,024, cycling access, no expiration, one or ten persistent readers, statistics OFF/OFF or ON/ON. Each process used three warm-ups and seven 250 ms measured intervals. These are inverse-throughput costs, not request-latency percentiles.

Each configuration used six independent processes per variant. Base/half order reversed every round; each LoadingCache process had a preceding MemoryCache control. All variants used the same harness assembly and executable path, swapping only `LoadingCache.dll` between sequential processes. Source snapshots, assembly hashes and exact commands are retained. There were 96 completed processes: 24 base, 24 half and 48 controls. Formal timing did not enable diagnostic captures.

### Battery .NET 10 cycling results

Each cell is the median of six process medians, followed by the minimum and maximum process medians, in ns/op. Ratio is half/base. The control ratio compares the MemoryCache processes assigned to the two roles.

| Readers | Statistics | Base | Half-full | Ratio | MemoryCache control ratio |
| --- | --- | --- | --- | --- | --- |
| 1 | Off | 13.80 [12.86, 14.40] | 12.81 [12.51, 13.34] | 0.929 | 1.014 |
| 1 | On | 20.01 [18.55, 20.75] | 16.47 [15.99, 17.74] | 0.823 | 1.004 |
| 10 | Off | 1.336 [1.252, 1.501] | 1.391 [1.227, 1.488] | 1.042 | 1.045 |
| 10 | On | 2.031 [1.972, 2.257] | 2.040 [2.003, 10.535] | 1.004 | 1.012 |

The single-reader results favour the candidate, with cost ratios of 0.93/0.82. Ten-reader performance is approximately unchanged; no multi-reader improvement is claimed. The statistics-ON median hides a slow candidate process, which prompted the separate diagnostic below. Trace qualification is recorded separately above; this battery cohort is not used for formal PR numbers.

In round 2 of the ten-reader statistics-ON case, the half-full process's measured samples ranged from 9.74 to 11.70 ns/op, with a median of 10.54. Its other five process medians were 2.00-2.07. Every sample had zero misses, valid resident/accounting checks and zero GC collections. The preceding MemoryCache control was 32.25 ns/op, compared with 39.32 for the base role in that round; it did not show a corresponding slowdown. The cause is not established. This run is retained, not excluded.

The original six base processes did not exhibit the slow mode; this alone cannot attribute that mode to the patch. The following diagnostic tests that attribution separately from formal timing.

## Appendix B: battery slow-mode diagnostic

Only the .NET 10, ten-reader, statistics-ON cycling case ran, with diagnostics enabled: twelve base and twelve half-full processes in strict alternating order. All other workload parameters and the existing harness/core binaries remained unchanged. This adds 24 diagnostic processes, kept separate from the 96 formal timing processes above. No production or harness code changed for the diagnosis.

A preliminary shell check reported AC, but every one of the 48 snapshots immediately before/after the measured processes reported **Battery Power**, 87% to 86%, with the active `powermode` setting equal to 0. All processes stayed in that same recorded power state. The diagnostic is an independent battery cohort; its measurements are not pooled with the earlier timing cohort or any future AC cohort. Raw power output and UTC timestamps are retained for every process.

Slow mode was defined before running as a process median of at least 5 ns/op, between the previously observed approximately 2 ns and 10 ns modes. Any cutoff from 4 through 8 ns gives the same classification. Each cost cell below is the median and range of process medians **within that mode**, avoiding a misleading median between two modes.

| Variant | Slow processes | Fast mode ns/op | Slow mode ns/op | Accepted %, fast / slow | Maintenance passes per million reads, fast / slow |
| --- | --- | --- | --- | --- | --- |
| Base | 6/12 | 2.375 [2.326, 2.496] | 10.708 [8.978, 11.185] | 2.803 / 6.173 | 109.56 / 241.29 |
| Half-full | 5/12 | 2.419 [2.223, 2.468] | 11.209 [10.523, 12.393] | 3.124 / 6.567 | 122.04 / 256.63 |

Both versions show the slow mode at similar observed frequencies. Their slow-mode cost ranges overlap, and both show a higher accepted proportion and more maintenance passes per completed read in that mode. Under the agreed decision rule, this is **existing process bimodality, not a newly demonstrated half-full regression**. Twelve processes per version do not establish equal population frequencies or prove the underlying cause; the earlier six base processes simply did not sample the slow mode. An A/A extension was therefore not needed for this limited attribution question.

Every process records reader thread IDs and their statistics/drop stripe indices. There were **no collisions in either counter mapping in any of the 24 processes**. Twenty-two processes used IDs 8-17, mapping to stripes 8-15, 0, 1; the other two used IDs 9-18, mapping to 9-15, 0, 1, 2. Both fast and slow modes occur with the same mapping. These observations do not support blaming statistics/drop stripe collisions. The read transport's per-offer fallback-ring choices were not traced. Higher acceptance and pass counts per read are shared correlates of the slow mode, not a demonstrated causal chain.

All diagnostic processes passed hit, checksum and resident checks, with no misses or GC collections. Accepted plus full/failed drops exactly equals completed reads in each process; failed drops were zero. Counts below are sums of the seven measured intervals, excluding warm-ups and post-interval cleanup. Asynchronous draining and separate snapshots can leave small accepted/drained differences at interval boundaries.

| Variant / mode | Accepted | Drained | Dropped full | Maintenance passes |
| --- | ---: | ---: | ---: | ---: |
| Base / fast | 123,193,517 | 123,192,423 | 4,119,324,499 | 481,431 |
| Base / slow | 69,970,366 | 69,969,879 | 1,193,594,434 | 273,553 |
| Half / fast | 149,670,220 | 149,669,662 | 4,927,833,780 | 584,875 |
| Half / slow | 54,169,866 | 54,169,591 | 745,282,294 | 211,846 |

The [per-process CSV](../../../artifacts/perf-read-maintenance/diagnostics-battery-01/process-summary.csv) contains all 24 reader mappings, individual accepted/drained/dropped totals, maintenance counts, mode classifications and power states. The [diagnostic summary](../../../artifacts/perf-read-maintenance/diagnostics-battery-01/summary.json) and [command/power receipts](../../../artifacts/perf-read-maintenance/diagnostics-battery-01/processes.jsonl) retain the full detail. The directory was renamed after the run to reflect its recorded battery state; exact executed command strings and raw JSON bytes remain unchanged, with the original location recorded alongside them.

## Correctness and changed test expectations

- Final quarter Release solution build on current main with SDK 10.0.401 and TUnit 1.72.10: zero warnings/errors. Benchmark binaries remain the recorded SDK 10.0.300 builds; only upstream test dependencies changed during branch alignment.
- Core tests: 753/753 on .NET 8 and .NET 10 (747 existing cases plus six new cases).
- DI: 10/10 on each runtime; short stress: 10/10 on each runtime; both consumer smokes passed.
- Final quarter `dotnet csharpier check src tests benchmarks`: exit 0, 145 files checked; ADR/concurrency `oxfmt --check` and `git diff --check` also exit 0. `global.json` has no diff; SDK 10.0.401 is installed and no new override was used.
- Against the original implementation, the two changed background-tail expectations failed and the other 13 engine-maintenance cases passed.

| Existing test | Change | ADR-0014 |
| --- | --- | --- |
| `LastEnqueueDuringAWorkerPassIsDrainedWithoutLostWakeup` | Renamed `BackgroundPassLeavesSmallReadTailForExplicitCleanup`; background leaves 45 after draining 256, then explicit cleanup retains the previous empty-queue/additional-pass assertions. | R1, R2 |
| `ReadEnqueuedDuringSignalClearHandoffIsNotStranded` | Renamed `SmallReadDuringSignalClearHandoffWaitsForExplicitCleanup`; one observation may wait, then explicit cleanup retains the empty-queue/idle assertions. | R1, R2 |
| `BoundedBatchReleasesTheConsumedPrefixAcrossCounterWrap` | Added backlog assertions; retained existing wrap, ordering and accounting assertions. | R1 |
| `PublishedCountIncludesEventsBeyondAPausedHeadWithoutMakingItReadable` | Added backlog assertions before/after the readable head is consumed; retained paused-publication accounting assertions. | R1, R2 |

New regressions cover full-stripe handoff, explicit cleanup requested during a running owner across multiple passes, initially rejected scheduling consuming a below-threshold tail, and quarter-threshold boundaries at capacities 1, 4 and 64. The two multi-pass cleanup fixtures now use capacity 2,048: 300 reads remain below the quarter threshold of 512, and rejected fallback must consume the final 256 reads in its eighth pass. The wrap test retains one queued observation at capacity four, so the added backlog assertion is now true; existing ordering and accounting assertions remain intact. Existing rejected-rearm, 32-pass fallback and explicit-cleanup rejection assertions remain unchanged.

## Delivery status

- Trace qualification, threshold selection, direct AC timing and net8/hot-key coverage are complete. The selected final-candidate cohorts contain 420 completed processes including controls and the restricted three-way follow-up; the failed zero-sample launch remains separate. Retain historical half controls, both battery appendices and all mode frequencies.
- Use direct .NET 10 cycling ratios 0.957/0.878 for one reader, 0.626 for ten readers/statistics OFF from the contemporaneous three-way comparison, and the 0.742 fast-mode ratio plus base 0/6 versus quarter 2/6 slow processes for ten readers/statistics ON. Do not multiply ratios, collapse the modes or claim environment-independent gains.
- Final [SDK 10.0.401 validation receipts](../../../artifacts/perf-read-maintenance/validation-quarter-sdk401-main/checks.json) record core 753/753, DI/stress 10/10 and ConsumerSmoke on both targets, plus Release build, CSharpier, Markdown and clean SDK-file checks. The earlier [SDK 10.0.300 receipts](../../../artifacts/perf-read-maintenance/validation-quarter-final/checks.json) remain retained. All four production files and both test files still match the frozen final source manifest.
- #35 is merged. Before delivery, the work was aligned to main with all ten work files preserved; [alignment receipt](../../../artifacts/perf-read-maintenance/main-alignment.json). The direction 1 PR will target `main` and use one commit, with identical commit/PR title: `perf(maintenance): re-arm background reads at quarter capacity`.
- The [PR description draft](../../../artifacts/perf-read-maintenance/pr-description.md) records each old-test change against ADR-0014 R1/R2, new regression coverage, results and limits. Raw artifacts are excluded from the proposed commit.
- The separate #36 follow-up patch is prepared and checks cleanly against `docs/perf-gap-handoff` at `2c3633bd4a0b613fa4daf099c1f67e9cf3c76b9b`. It only adds the existing-bimodality follow-up to directions 1 and 6. Its amend/force-with-lease remains deferred pending user approval; direction 1 delivery is separately authorised.

## Local evidence and reproduction

The local evidence directory is [artifacts/perf-read-maintenance](../../../artifacts/perf-read-maintenance/). It retains [source manifests](../../../artifacts/perf-read-maintenance/source-manifest.json), [assembly hashes](../../../artifacts/perf-read-maintenance/assembly-hashes.json), [environment](../../../artifacts/perf-read-maintenance/environment.json), [all process commands and exit codes](../../../artifacts/perf-read-maintenance/processes.jsonl), [summary data](../../../artifacts/perf-read-maintenance/summary.json), raw samples, validation TRX/logs and the proposed PR description. Artifacts are local evidence, not committed binaries.

The historical battery matrix used these commands before the driver gained the required `--cohort` option. Current AC reproduction commands are recorded with their individual cohorts above:

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-matrix.py main --tfm net10.0 --patterns cycle
uv run --no-project python3 artifacts/perf-read-maintenance/analyse.py
```

The diagnostic used the following command (the output directory was subsequently renamed to `diagnostics-battery-01`):

```sh
uv run --no-project python3 artifacts/perf-read-maintenance/run-slow-mode-diagnostics.py --cohort diagnostics-ac-02
uv run --no-project python3 artifacts/perf-read-maintenance/analyse-slow-mode.py diagnostics-battery-01
```

The driver refuses to overwrite existing process results. Create a fresh evidence directory for another formal run. Four immutable source snapshots were built and measured: base, half, quarter and quarter-final. Quarter differs from half by `_capacity >> 2` instead of `_capacity >> 1`; final quarter changes only the XML summary, with equivalent executable IL and metadata apart from MVID. The SDK override is local build setup and must not be committed.

One attempted combined test filter was rejected by the test runner before any tests ran. The subsequent complete core suite passed; both records are retained. No failed workload or unfavourable timing was removed.
