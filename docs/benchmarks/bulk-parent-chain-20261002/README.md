# B1: validate bulk keys against the parent chain

Baseline `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4` was selected while G2 PR #45 remained unmerged. B1 is independent of G2 and H1. M4 Max, .NET 10.0.8, AC mode 0. All source, tests, harnesses, commands, and exact DLLs are bound to frozen receipts.

## Change and distinct-key proof

The pre-G2 loop keeps its existing per-key ambient push/pop and exception cleanup. Save the parent before the loop, check each non-leader key against that parent, and push every node as before. Sync and async bulk both call this one loop. A Contains overload takes the explicit starting node; the ordinary two-argument path retains its existing behaviour. Node ownership, layout, and public APIs are unchanged.

The owned-key distinctness is already guaranteed by the same engine comparer:

```csharp
// EngineBulk.cs:384
var seen = new HashSet<TKey>(Comparer);

// EngineBulk.cs:398-403
if (!seen.Add(key))
    continue;
snapshot.Add(key);
```

GetAll/GetAllAsync consume SnapshotBulkKeys before installing a bulk group. CollectBulkStateLocked iterates that distinct request and adds only newly owned keys; the sync group copies plan.OwnedKeys at EngineBulk.cs:428 and the async group at line 484. Checking one batch sibling against another cannot discover a comparer-equivalent key. The parent already contains the leading owner installed by InvokeSyncFactory/InvokeAsyncFactory, so ancestor checks remain intact.

The old construction traversed 1+2+...+(N−1) batch-prefix nodes. B1 traverses only the saved ambient chain for each key: O(N × ambient depth). The chain still contains every owned key while user loader code runs.

## Functional validation

Six new cases cover all three requested boundaries for sync and async: a nested comparer-equivalent sibling Get across Task.Run/await throws; outer load of ancestor → GetAll containing ANCESTOR throws before the bulk loader; and a deterministic 4,096-key load retains all returned/resident values, exposes sibling keys to logical-chain checks, and serves the second bulk call without reloading. There are no timing assertions; the watchdog only prevents a deadlock from hanging the suite.

All six cases pass unchanged base production and all 45 bulk cases pass B1. Complete candidate validation passes 842 core, 10 DI, and 10 stress cases on each net8/net10, both consumer smokes, build, formatting, and actual prek hooks. The initial incorrect test-selection attempt ran zero cases and is preserved separately; the verified TUnit selection then ran all six.

## Single-worker hit gate

| Case | Base ns/hit [range] | B1 ns/hit [range] | Raw change | Paired MC-adjusted |
|---|---:|---:|---:|---:|
| hit-none | 12.868 [12.004, 13.526] | 12.765 [12.398, 13.072] | -0.80% | -0.24% |
| hit-write | 20.156 [19.861, 21.194] | 20.102 [19.857, 20.486] | -0.27% | -0.94% |
| hit-access | 21.182 [20.931, 21.962] | 21.379 [20.905, 21.452] | +0.93% | +0.20% |

Six processes per source/backend/case; three warmups and seven 250 ms samples. The unchanged stop rule is >3% raw median slowdown AND strictly non-overlapping worse process ranges. The first frozen cohort passes; no selective repeats or hit tuning.

## Single-worker all-absent GetAll

| Keys per call | Base ns/key [range] | B1 ns/key [range] | Change | Base B/key [range] | B1 B/key [range] |
|---:|---:|---:|---:|---:|---:|
| 16 | 1,032.574 [980.870, 1,043.709] | 932.107 [919.285, 955.238] | -9.73% | 1,193.253 [1,192.955, 1,193.443] | 1,192.695 [1,192.676, 1,192.711] |
| 256 | 837.817 [836.860, 859.928] | 349.248 [332.992, 356.455] | -58.31% | 1,319.760 [1,319.568, 1,319.964] | 1,319.886 [1,319.773, 1,319.911] |
| 1,024 | 2,321.176 [2,306.079, 2,341.891] | 287.548 [286.408, 291.646] | -87.61% | 1,397.953 [1,397.953, 1,397.953] | 1,397.953 [1,397.887, 1,397.953] |
| 4,096 | 10,268.958 [8,867.163, 10,347.186] | 573.326 [572.512, 578.416] | -94.42% | 1,461.343 [1,461.300, 1,461.343] | 1,461.300 [1,461.300, 1,461.300] |
| 10,240 | 21,845.469 [21,749.941, 21,985.010] | 673.915 [670.933, 680.311] | -96.92% | 1,374.938 [1,374.938, 1,374.941] | 1,374.987 [1,374.984, 1,374.990] |

Three fresh processes per arm/size, using the same precreated-dictionary loader and capacity 16,384 with no expiry/statistics. Keys/values are preboxed; one GetAll per fresh cache. The unchanged barrier adds a fixed per-call cost, especially visible at 16 keys. No MC arm is added to this requested batch-size sweep.

## Ten workers, 1,024 keys per call

| Base ns/key [range] | B1 ns/key [range] | Raw change | Paired MC-adjusted | Winning pairs | Base/B1 B/key |
|---:|---:|---:|---:|---:|---:|
| 714.350 [680.117, 736.762] | 457.237 [443.896, 474.365] | -35.99% | -34.76% | 6/6 | 1379.798 / 1379.499 |

Paired MC ns/key: base 149.712 [140.136, 171.514]; B1 147.095 [144.233, 172.953].

Each batch contains 10,240 keys split into ten disjoint calls. ns/key is aggregate batch wall time divided by all keys. MC performs per-key GetOrCreate plus a presized result dictionary; it is an environment control, not equivalent bulk/single-flight/rollback semantics. The adjusted figure is the median of six paired (B1/MC_B1)/(base/MC_base)−1 values.

Bulk runs use three minimum warmups and at least one second of warmup, then seven samples accumulating ≥150 ms of foreground work. Tables are medians of process medians with full process ranges. Foreground excludes construction, precreated loader dictionaries, validation, disposal, and later cleanup; normal caller-result allocation and concurrent maintenance are included.

## Explicitly settled bulk results

| Case | Base settled ns/key | B1 settled ns/key | Base settled B/key | B1 settled B/key |
|---|---:|---:|---:|---:|
| bulk-w1-n16 | 1,077.841 [1,026.635, 1,088.680] | 977.909 [964.509, 999.738] | 1,202.500 [1,202.486, 1,202.500] | 1,202.490 [1,202.487, 1,202.500] |
| bulk-w1-n256 | 850.315 [849.475, 873.407] | 362.569 [345.586, 370.159] | 1,320.656 [1,320.656, 1,320.656] | 1,320.656 [1,320.656, 1,320.656] |
| bulk-w1-n1024 | 2,333.202 [2,318.254, 2,354.046] | 299.032 [297.753, 302.918] | 1,398.078 [1,398.078, 1,398.078] | 1,398.078 [1,398.078, 1,398.078] |
| bulk-w1-n4096 | 10,291.052 [8,878.193, 10,369.250] | 583.621 [583.335, 588.707] | 1,461.374 [1,461.331, 1,461.374] | 1,461.331 [1,461.331, 1,461.331] |
| bulk-w1-n10240 | 21,861.553 [21,766.016, 22,001.445] | 683.030 [679.517, 689.055] | 1,374.951 [1,374.951, 1,374.954] | 1,374.999 [1,374.997, 1,375.003] |
| bulk-w10-n10240 | 726.746 [690.361, 746.723] | 467.806 [454.552, 487.237] | 1,379.810 [1,379.406, 1,379.940] | 1,379.511 [1,379.108, 1,379.853] |

Settled figures add explicit quiescence. Each measured batch validates its loader-call count, result identities, and final resident count. Every process holds the common lock and checks native AC/mode state before and after.

## Paired timing controls

| Case | Round | Base LC ns | B1 LC ns | Base MC ns | B1 MC ns | Adjusted change |
|---|---:|---:|---:|---:|---:|---:|
| hit-none | 1 | 12.426 | 12.434 | 17.272 | 17.110 | +1.01% |
| hit-none | 2 | 12.004 | 13.072 | 17.425 | 17.242 | +10.05% |
| hit-none | 3 | 13.400 | 12.398 | 17.753 | 17.455 | -5.90% |
| hit-none | 4 | 12.476 | 12.736 | 17.565 | 17.659 | +1.54% |
| hit-none | 5 | 13.259 | 12.898 | 17.974 | 17.748 | -1.49% |
| hit-none | 6 | 13.526 | 12.793 | 17.810 | 17.895 | -5.87% |
| hit-write | 1 | 20.128 | 20.217 | 17.943 | 18.332 | -1.69% |
| hit-write | 2 | 20.153 | 20.023 | 18.038 | 17.954 | -0.18% |
| hit-write | 3 | 19.861 | 19.875 | 17.890 | 17.887 | +0.09% |
| hit-write | 4 | 20.159 | 20.486 | 18.492 | 17.965 | +4.60% |
| hit-write | 5 | 21.194 | 19.857 | 17.963 | 17.934 | -6.16% |
| hit-write | 6 | 20.950 | 20.182 | 17.929 | 17.908 | -3.56% |
| hit-access | 1 | 21.962 | 20.905 | 18.349 | 18.239 | -4.24% |
| hit-access | 2 | 21.389 | 21.361 | 18.253 | 18.186 | +0.24% |
| hit-access | 3 | 20.978 | 21.420 | 18.287 | 18.604 | +0.37% |
| hit-access | 4 | 20.931 | 21.452 | 18.451 | 18.304 | +3.31% |
| hit-access | 5 | 21.004 | 21.133 | 18.135 | 18.282 | -0.19% |
| hit-access | 6 | 21.360 | 21.396 | 18.205 | 18.206 | +0.16% |
| bulk-w10-n10240 | 1 | 698.774 | 459.344 | 143.838 | 144.233 | -34.44% |
| bulk-w10-n10240 | 2 | 680.117 | 467.712 | 140.136 | 148.419 | -35.07% |
| bulk-w10-n10240 | 3 | 706.137 | 455.130 | 154.685 | 145.772 | -31.61% |
| bulk-w10-n10240 | 4 | 736.762 | 443.896 | 171.514 | 144.852 | -28.66% |
| bulk-w10-n10240 | 5 | 722.563 | 455.022 | 144.739 | 172.953 | -47.30% |
| bulk-w10-n10240 | 6 | 724.097 | 474.365 | 161.552 | 165.594 | -36.09% |

## Boundaries and follow-up

A nested Get inside the bulk loader still scans the N-node logical chain: O(N) per lookup. A single set-node or a different Node contract is excluded; consider that separately only if measurements justify it.

Whichever of G2 and B1 lands second needs a rebase of the shared loop. G2 also contains a synthetic comparer-failure test that throws specifically on sibling 2 versus 3; B1 deliberately removes that comparison. On integration, trigger that rollback test through an ancestor comparison while preserving its ambient-restoration assertion. This independent B1 branch does not edit G2.

Evidence stays in local artifacts/perf-bulk-parent-chain/: method.md, results/plan.json, all 126 raw results/receipts/logs, builds/manifest.json and source snapshots, before-functional-receipt.json, functional-receipt.json, b1-initial-validation/, summary.json, validation.json, and receipt-index.json. All 882 measured samples are fresh; no earlier G2 or diagnostic measurements are reused.

## Frozen fingerprints

| Source | Production fingerprint | LoadingCache.dll SHA-256 |
|---|---|---|
| base | `9f130c714b1784472bafdf33a73b4edf578eeb7cc677def5502c7aba6d0d005b` | `7ec850494d3415230bf3e2eadb04b4df44d7bb68e168f31c290d29e62e1d9bd5` |
| b1 | `f2cc5b117a5cb7c93f665298c1f08729d901c9cb2df287777a5661837dde0441` | `9eefe82bfa356a8bb1d7df28eb33a13a2033363260dbf06e79384081fa21ff5a` |
