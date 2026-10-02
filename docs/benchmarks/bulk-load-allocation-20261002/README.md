# G2 bulk allocation and latency

Baseline: `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4` (merged G1, before H1). M4 Max, .NET 10.0.8, AC power with mode 0. Six fresh processes per source/backend/case; seven measured samples per timing process. Tables show the median of process medians and full min/max process ranges. Raw timings, allocations, commands, power and assembly hashes are retained.

This is the complete fresh cohort authorised after the H1 pause. The previous 72 hit and 10 bulk processes remain intact and are excluded from every table below. The unchanged source and exact DLLs were reverified; no production or test edits or rebuilds occurred during the pause or this restart.

## Change and preserved behaviour

- Sync bulk owned and prefetched entries defer resident task creation to the existing entry-locked lazy path; async bulk task views are unchanged.
- Additional load-chain nodes are built and validated locally against the saved parent and local prefix, then installed/restored once. Every key remains visible to Task.Run/await and comparer-aware reentrancy checks; a construction failure leaves ambient context untouched.
- Result, snapshot, prepared-publication and pending-flight dictionaries use already materialised bounded counts. Loader Count and configured maxima are not capacity hints. Ready/pending/hit-heavy ownership and one-pass validation are preserved.
- G1 already reuses the weak owner. Its saving is not counted again. No Entry/token/engine field layout, public API or hit-path change is included.

Full functional verification: 853 core, 10 DI and 10 stress cases on each net8/net10, both consumer smokes and formatting. The 17 new preservation cases also passed unchanged production; all 56 bulk cases passed after implementation. Coverage includes resident task identity, non-leader cross-thread reentrancy, nested caches, partial chain construction failure, throwing input/output enumeration, duplicate/oversize output and hit-heavy requests. Existing rollback/publication/terminal-race cases remain green.

## Single-thread hit gate

| Case | Base ns | G2 ns | Raw change | Paired MC-adjusted | Gate |
|---|---:|---:|---:|---:|---|
| hit-none-w1 | 12.833 [12.327, 13.144] | 13.137 [12.394, 13.543] | +2.37% | +0.83% | PASS |
| hit-write-w1 | 20.003 [19.659, 20.323] | 19.983 [19.929, 21.563] | -0.10% | +1.17% | PASS |
| hit-access-w1 | 21.676 [20.784, 22.399] | 21.210 [20.748, 21.606] | -2.15% | -1.23% | PASS |

The stop rule is >3% median slowdown AND strictly non-overlapping worse process ranges. All three cases passed this fresh frozen cohort; no selective repeats or hit tuning. The earlier partial cohort is excluded as directed before this run.

## Synchronous bulk miss-to-load

| Workers / keys per call | Base foreground ns/key | G2 foreground ns/key | Change | MC-adjusted | Base settled ns/key | G2 settled ns/key |
|---|---:|---:|---:|---:|---:|---:|
| 1 / 10,240 | 21825.654 [21580.322, 22022.188] | 21457.915 [21148.340, 21658.154] | -1.68% | -0.57% | 21843.135 [21596.562, 22038.955] | 21475.278 [21164.395, 21674.268] |
| 10 / 1,024 | 697.291 [664.337, 717.619] | 485.078 [480.088, 516.887] | -30.43% | -27.27% | 707.450 [678.779, 729.817] | 495.746 [492.918, 527.724] |

Each batch uses a fresh capacity-16,384 cache and 10,240 preboxed keys. One worker makes one 10,240-key GetAll; ten workers each make a 1,024-key GetAll. The different call sizes mean these rows are separate workloads, not a thread-scaling comparison. The existing per-key traversal of the linked reentrancy prefix is unchanged. Ten-worker ns/key is batch wall time divided by total keys, not individual-call latency.

Cache construction, loader-returned dictionaries, prefill, validation and disposal are outside the windows. Foreground includes normal result creation and concurrent maintenance; settled adds explicit quiescence. Every batch validates its loader-call count, returned identities and 10,240 resident keys.

| Workers | Base/G2 foreground B/key | Base/G2 settled B/key | Paired base MC ns/key | Paired G2 MC ns/key |
|---|---:|---:|---:|---:|
| 1 | 1374.938 / 923.855 | 1374.951 / 923.867 | 150.673 [149.731, 155.348] | 149.969 [148.418, 151.043] |
| 10 | 1379.551 / 885.325 | 1379.564 / 885.337 | 160.748 [149.644, 168.419] | 147.432 [140.841, 153.141] |

MC performs per-key GetOrCreate plus a presized result dictionary. It does not provide equivalent bulk single-flight, atomic publication or rollback. The MC-adjusted percentage is the median of six paired (G2/MC_G2)/(base/MC_base)-1 ratios; it is an environment control, not a semantic-equivalence claim.

## Allocation-only completion diagnostics

| Scenario / requested keys | Base B/requested key | G2 B/requested key | Change | Saving B/key | Base/G2 resident tasks | Newly loaded keys |
|---|---:|---:|---:|---:|---:|---:|
| bulk-sync / 4,096 | 1476.982 [1476.982, 1476.982] | 953.256 [953.256, 953.256] | -35.46% | 523.727 | 4096 / 0 | 4,096 |
| bulk-async / 4,096 | 1654.334 [1654.334, 1654.334] | 1202.607 [1202.607, 1202.607] | -27.31% | 451.727 | 4096 / 4096 | 4,096 |
| bulk-async-pending / 4,096 | 1689.639 [1687.438, 1696.541] | 1239.105 [1236.896, 1244.535] | -26.66% | 450.533 | 4096 / 4096 | 4,096 |
| bulk-sync-hit-heavy / 4,096 | 530.115 [530.115, 530.115] | 453.148 [453.148, 453.148] | -14.52% | 76.967 | 1 / 0 | 1 |
| bulk-sync / 8 | 1603.000 [1603.000, 1603.000] | 1147.000 [1147.000, 1147.000] | -28.45% | 456.000 | 8 / 0 | 8 |

Six processes per arm after a 128-key warmup; full load completion and maintenance are included. Precreated caller tasks/dictionaries, reflection setup, prefill, result validation, snapshots and disposal are excluded. Pending async bulk is checked incomplete before releasing the precreated backend promise. No active flight or refresh remains at inspection. The denominator is requested keys: the hit-heavy row loads only one new key. Task counts are inspected before any async view materialisation, whose stable identity is covered functionally.

These are combined-change measurements. The design estimates were 72 B/new key for a sync resident Task and about 144 B/key for repeated AsyncLocal install/restore at 4,096 keys, plus dictionary growth; this cohort does not independently attribute each saving. Completed/pending async rows retain resident tasks. Small and hit-heavy rows guard against treating every request as a dense new-key batch.

## Evidence and limits

- Fresh `method.md` and `results/plan.json`, plus original `../builds/manifest.json`, `../method.md` and `../run.py`: frozen method, order, sources, harnesses and exact binaries.
- `results/*.json`, `*.receipt.json` and `*.log`: all 180 processes and 840 measured timing samples, with AC checks and shared-lock commands.
- `../functional-receipt.json`, `../before-functional-receipt.json` and `../initial-g2-validation/`: source hashes and regression/full-suite results.
- `summary.json`, `validation.json` and `receipt-index.json`: aggregation and final integrity checks.
- A pre-measurement probe compile failure is preserved in `../builds-attempt1-compile-failure/`: the loading personality exposes Put, not Set. Both probe prefill calls were corrected before any measurement; production source was unchanged.

All source, functional files, harnesses and binaries still match their frozen hashes. This report records the frozen pre-commit receipt handoff.

All paths in this evidence list are relative to the local `artifacts/perf-bulk-load/resumed/` directory. Raw receipts and binaries are local artifacts and are not committed with the published report.

## Individual paired timing controls

Every pair is retained. Values below are foreground ns/key (bulk) or ns/hit (hit). The adjusted change is calculated for that round; the tables above use the median of the six adjusted changes.

| Case | Round | Base LC | G2 LC | Base MC | G2 MC | Raw paired change | MC-adjusted change |
|---|---:|---:|---:|---:|---:|---:|---:|
| hit-none-w1 | 1 | 12.622 | 13.533 | 16.986 | 17.962 | +7.22% | +1.39% |
| hit-none-w1 | 2 | 13.135 | 13.157 | 17.680 | 17.661 | +0.17% | +0.28% |
| hit-none-w1 | 3 | 12.327 | 13.117 | 17.480 | 17.686 | +6.41% | +5.17% |
| hit-none-w1 | 4 | 12.817 | 12.394 | 17.582 | 17.555 | -3.30% | -3.15% |
| hit-none-w1 | 5 | 13.144 | 12.705 | 17.516 | 17.790 | -3.34% | -4.83% |
| hit-none-w1 | 6 | 12.849 | 13.543 | 17.590 | 17.665 | +5.40% | +4.96% |
| hit-write-w1 | 1 | 20.323 | 19.989 | 17.878 | 17.909 | -1.64% | -1.81% |
| hit-write-w1 | 2 | 20.217 | 19.978 | 17.963 | 17.894 | -1.18% | -0.81% |
| hit-write-w1 | 3 | 19.913 | 20.318 | 18.017 | 17.944 | +2.03% | +2.45% |
| hit-write-w1 | 4 | 19.659 | 19.934 | 17.925 | 17.878 | +1.40% | +1.66% |
| hit-write-w1 | 5 | 19.674 | 19.929 | 17.835 | 17.943 | +1.30% | +0.69% |
| hit-write-w1 | 6 | 20.092 | 21.563 | 17.999 | 17.974 | +7.32% | +7.47% |
| hit-access-w1 | 1 | 20.793 | 20.748 | 18.213 | 18.127 | -0.22% | +0.26% |
| hit-access-w1 | 2 | 20.784 | 21.137 | 18.234 | 18.286 | +1.70% | +1.41% |
| hit-access-w1 | 3 | 21.835 | 21.606 | 18.243 | 18.239 | -1.05% | -1.03% |
| hit-access-w1 | 4 | 21.766 | 21.207 | 18.261 | 18.227 | -2.57% | -2.39% |
| hit-access-w1 | 5 | 21.587 | 21.214 | 18.128 | 18.074 | -1.73% | -1.43% |
| hit-access-w1 | 6 | 22.399 | 21.523 | 18.384 | 18.426 | -3.91% | -4.13% |
| bulk-sync-w1 | 1 | 21845.068 | 21279.727 | 151.300 | 151.043 | -2.59% | -2.42% |
| bulk-sync-w1 | 2 | 21606.494 | 21457.686 | 150.081 | 148.848 | -0.69% | +0.13% |
| bulk-sync-w1 | 3 | 22022.188 | 21148.340 | 155.348 | 149.932 | -3.97% | -0.50% |
| bulk-sync-w1 | 4 | 21891.719 | 21658.154 | 151.266 | 150.624 | -1.07% | -0.65% |
| bulk-sync-w1 | 5 | 21806.240 | 21458.145 | 149.731 | 148.418 | -1.60% | -0.73% |
| bulk-sync-w1 | 6 | 21580.322 | 21540.527 | 149.827 | 150.006 | -0.18% | -0.30% |
| bulk-sync-w10 | 1 | 664.337 | 480.088 | 168.419 | 146.576 | -27.73% | -16.97% |
| bulk-sync-w10 | 2 | 705.628 | 487.678 | 154.096 | 148.289 | -30.89% | -28.18% |
| bulk-sync-w10 | 3 | 698.275 | 486.866 | 150.761 | 146.173 | -30.28% | -28.09% |
| bulk-sync-w10 | 4 | 686.948 | 516.887 | 167.442 | 140.841 | -24.76% | -10.54% |
| bulk-sync-w10 | 5 | 717.619 | 482.818 | 167.400 | 153.141 | -32.72% | -26.45% |
| bulk-sync-w10 | 6 | 696.308 | 483.289 | 149.644 | 148.596 | -30.59% | -30.10% |

## Source and binary fingerprints

Base commit: `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4`. Original manifest SHA-256: `0586665f69503ca7621f440e712c5d47e998281e59866771a29e11746fe68386`.

| Source | Production fingerprint | LoadingCache.dll SHA-256 |
|---|---|---|
| base | `9f130c714b1784472bafdf33a73b4edf578eeb7cc677def5502c7aba6d0d005b` | `a2e254d3080902a31cb38ea13961be5b2803c796f051b2d6bcd146f8f0811c2c` |
| g2 | `6cf972589480c9562d30ef00fb226ed3558e5e1f71f2341d2f7a52a09882c98f` | `9f97e254daf13bc2200e75a76aa2a6ceb3d9d2f57f8b9bc31251b444d7c341b2` |

Fresh method SHA-256: `57531b9112391eb871bf87db490a92eb64abf36c40bceab81a7577137aef6bf1`; runner SHA-256: `60fd5d3bb4432f51f30b1db37d110555a00dd8907156aa488957e288d3dca44a`.
