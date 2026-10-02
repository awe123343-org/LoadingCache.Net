# Synchronous load allocation reduction

The synchronous miss path now creates its task mirror only when an asynchronous
consumer requests it, uses its private flight object as the completion monitor,
and reuses immutable weak owner references. Flight hashes are stored in a field
and equality remains reference identity, so the active-flight set does not install
an identity hash in the header used by the completion monitor.

On this M4 Max, the synchronous path saves exactly **152 B per loaded entry** in
the controlled allocation case. Aggregate miss-to-load cost falls by about 27%
in the one- and ten-writer cases. Single-reader hit medians rise by 1.4–2.6%;
their process ranges overlap and none reaches the agreed stopping threshold.
This is not a claim of zero hit cost.

## Scope and semantics

- A task requested before or after completion retains stable identity and observes
  the same value or exception as monitor waiters. Existing task mirrors complete
  before the flag permitting flight retirement is published.
- The four-byte flight hash fits existing base-class padding. `SyncFlight`
  shrinks from 224 to 216 B; `AsyncFlight` remains 200 B. `Entry` remains 176 B.
- `AsyncLocal` load-chain semantics are retained, including recursion detection
  across `Task.Run`. Moving optional flight fields into a lazy object is deferred.
- Reused weak owners do not keep a cache or engine alive while an asynchronous
  backend and captured load-chain context remain reachable.
- `ConditionalWeakTable.Add` still installs a runtime identity hash. Bulk sync
  flights still lock themselves and therefore inflate into a sync block on that
  path. This limitation is not removed by the field hash override.
- Bulk per-key tasks and repeated `AsyncLocal` installs are unchanged. Their
  optimisation is separate work.

## Method

Baseline: main `6876d5015b95f1039a3d93846c3c14f66057ca39`. Host: Apple M4 Max,
ARM64 macOS; SDK 10.0.401, runtime pinned to .NET 10.0.8, MemoryCache 10.0.12.
Default runtime GC/JIT settings were retained. Each timing process held the shared
benchmark lock and captured native AC power and power mode 0 before and after.
No timing processes ran concurrently.

Timing uses six fresh processes per variant/backend/case. Each process records
at least three warmups followed by seven measured samples. Tables show medians
of process medians; brackets contain the full range of those six medians.

The load workload has 10,240 distinct, preboxed object keys per fresh cache,
capacity 16,384, no expiry or statistics, and an identity loader. Keys and returned
values are the same object, so there is no per-operation key/value allocation.
Measured samples last at least 150 ms; warmup also lasts at least one second.
All inserted values and settled counts are validated. Cache construction,
validation and disposal are outside the measured windows; concurrent maintenance
is included and final explicit cleanup is reported separately.

MemoryCache uses `GetOrCreate` with the same identity result and no size limit.
Its population semantics do not provide LoadingCache's same-key single flight;
the measured requests use distinct keys. Under ten-way concurrency, ns/load is
elapsed batch wall time divided by all completed operations: inverse aggregate
throughput, not individual caller latency or a percentile.

## Miss-to-load results

| Writers | Variant | LC ns/load [process range] | LC B/load | LC with cleanup ns/load | MC ns/load [process range] | MC B/load |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Baseline | 898.88 [862.45, 1068.59] | 1002.091 | 911.73 | 139.12 [138.45, 140.89] | 253.285 |
| 1 | Candidate | 652.45 [645.32, 694.97] | 850.090 | 664.12 | 139.70 [135.64, 144.69] | 253.285 |
| 10 | Baseline | 1116.88 [1064.93, 1122.36] | 973.500 | 1132.14 | 149.96 [144.02, 154.71] | 232.956 |
| 10 | Candidate | 807.99 [774.24, 818.04] | 821.447 | 820.92 | 153.66 [146.46, 159.71] | 232.872 |

One-writer cost falls by 246.43 ns (27.41%); ten-writer cost falls by 308.89 ns
(27.66%). The LC process ranges do not overlap. The gains remain when final
explicit cleanup is included.

## Allocation-only results

Each row uses one fresh process per variant, 4,096 loaded entries and capacity
8,192. `GC.GetTotalAllocatedBytes(precise: true)` covers loading, completion and
maintenance through quiescence. Cache construction, keys and precreated loader
results are excluded. This growth workload differs from the timing workload,
so its baseline need not equal the B/load figures above.

| Case | Baseline B/entry | Candidate B/entry | Observed saving | Saving explained by source |
| --- | ---: | ---: | ---: | ---: |
| Sync `Get` | 1037.207031 | 885.207031 | 152.000000 | 152.000000 |
| Completed-task `GetAsync` | 1013.458984 | 965.207031 | 48.251953 | 48.000000 |
| Pending `GetAsync` | 1141.796875 | 1093.535156 | 48.261719 | 48.000000 |
| Sync `GetAll` | 1493.013672 | 1468.982422 | 24.031250 | 24.031250 |

The synchronous saving is 96 B for TCS/task, 24 B for the separate monitor,
24 B for the weak owner reference, and 8 B from the smaller flight object.
The async source saving is 48 B for two weak references; the additional observed
fractions are whole-process variation, not another claimed object saving.
The bulk saving is 24 B per key plus 128 B for its one shared flight.

## Single-reader hit gate

The hit workload uses 1,024 resident preboxed keys, a cycle pattern, statistics
off, and one-hour TTL/TTI. Each process records three warmups and seven 250 ms
samples. The stop criterion is a median regression above 3% with non-overlapping
six-process ranges. The same criterion is applied to none, TTL and TTI.

| Expiry | Baseline ns/read [process range] | Candidate ns/read [process range] | Median change | MC-controlled change | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| None | 13.009 [12.575, 13.350] | 13.352 [13.101, 14.711] | +2.64% | +2.76% | Pass; ranges overlap |
| TTL | 20.328 [19.622, 23.094] | 20.812 [19.822, 22.210] | +2.38% | +2.64% | Pass; ranges overlap |
| TTI | 21.237 [20.800, 21.681] | 21.534 [20.857, 21.897] | +1.40% | +1.67% | Pass; ranges overlap |

MC-controlled change compares the candidate LC/MC ratio with the baseline LC/MC
ratio. All three raw hit medians are higher, despite unchanged hit method logic
and `Entry` layout. There were no selective reruns or tuning after these results.

## Validation and provenance

Release validation passes on .NET 8 and .NET 10: 774 core tests, 10 dependency
injection tests and 10 stress tests per runtime, consumer smoke and CSharpier.
New regressions cover task/monitor completion and failure parity, disposal,
concurrent task creation, colliding field hashes with distinct reference identity,
cross-`Task.Run` reentrancy and weak cache/engine ownership.

Local raw receipts remain under `artifacts/perf-sync-load`: the functional receipt,
object-layout proof, hit gate, load/allocation plan, formal summary and a SHA-256
index covering 535 report/command/result files. Those generated artifacts are not
part of this change. The measured production and test hashes match the final
functional-validation hashes.

The initial load-harness build failed a strict LC DLL hash check before any run
because it rebuilt the dependency at another output path. That build is retained
as an excluded attempt. The corrected load and allocation probes reference the
exact frozen LC DLL used by the hit probe; no timing came from the failed build.
