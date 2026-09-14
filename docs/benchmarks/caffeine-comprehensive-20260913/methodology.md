# Comprehensive Caffeine scenario methodology

These matched public-API drivers extend the existing resident-hit and drained-write
comparisons. They are validated closed-loop workload probes, not JMH/BenchmarkDotNet
microbenchmarks and not production-readiness certification. Formal results belong
to the serial runner's frozen-source report. `harness-smoke/` only verifies the
harness and contracts; its timings must not be used for performance conclusions.

## Identity and configuration

- LoadingCache: exact assembly SHA-256 and runtime identity are emitted by each
  process. Targets are .NET 8 and .NET 10; the local validation hosts are actual
  8.0.31 and 10.0.12, respectively. The core library targets net8.0 in both.
- Caffeine: 3.2.4; upstream v3.2.4 commit
  `836b65c0a83e5d1641ded9c6de578654bc04b2e9`. JAR hash, every harness class hash,
  aggregate harness hash, Java runtime, architecture and JVM arguments are emitted.
  The local host is Zulu Java 25. No direct/same-thread executor is substituted.
- Public maximum is `--capacity` (minimum 64). Weighted cases use maximum weight
  C with positive weights 1..4; .NET additionally requires a resident count limit
  of 4C, which cannot bind before the positive-weight maximum C. No zero-weight
  or oversize-item semantic differences are concealed in the throughput cases.
- Keys are reference objects with integer equality/hash in both languages. Values
  are reference objects carrying integer ID and weight. Weak-key mode switches to
  identity equality. Keys/values are preallocated outside timing and kept strongly
  reachable for lookup cases; this prevents unrelated GC from inventing misses.
- Default loaders perform no network, I/O or simulated sleep. .NET sets
  MaxConcurrentLoads=32, MaxPendingLoadKeys=64, MaximumBulkKeys=32. No measured
  workload approaches these admission limits: at most one single-key backend
  execution or one 16-key bulk call is active. Caffeine has no matching admission
  mechanism; saturation of these .NET-specific limits is a separate contract gate.
- Stats are independently `--statistics on|off`. Disabled hit/miss counters must
  stay zero; enabled resident lookups record hits. Quiet lookups must not change
  the one preflight equality-check hit. No listeners, expiry or metrics are enabled
  unless that scenario names the mechanism.
- `--seed` drives a shared 32-bit LCG, `state = state * 1664525 + 1013904223`
  with wraparound, then unsigned modulo 4C. Trace generation, object construction,
  cache construction and resident prefill are outside timing. This is a fixed
  permutation-like workload for power-of-two bounds, not a claim of realistic
  access locality. Each sample starts from a new cache.
- Every option takes a value. Unknown/duplicate options and invalid bounds fail.
  `all` executes the 41 built-in cases for smoke; formal runs use one scenario per
  process, with explicit warmup/repetition/order settings.

## Timing and validation

A sample brackets the complete closed-loop scenario with monotonic timestamps;
UTC timestamps support run ordering. Work includes cache API actions, inline
value assertions/checksums, gate handling, loader/callback worker completion and
final policy drain. Setup, final policy snapshot validation, disposal and JSON
serialisation are outside the timed interval. `operations` counts scenario API
calls (a GetAll call is one operation, not sixteen); final housekeeping drain and
refresh-completion polling are included in time but not counted as user API work.
Thus compare the same named scenario and configuration. Do not compare raw ops/s
between different scenarios, or treat driver-inclusive costs as bare cache costs.

`seconds` is wall time for those actions. There is no per-request latency array,
no p99, no external arrival queue and no coordinated-omission correction. Default
ThreadPool/ForkJoin scheduling differs by runtime. Both driver-owned gates allow
inline continuation execution on release: an ordinary .NET TaskCompletionSource
matches Java CompletableFuture.complete without adding a driver-only scheduler
hop. Cache-owned promises retain each library's implementation. Automatic-refresh
completion polling uses Task.Yield / Thread.yield; `refresh-auto` and
`runtime-refresh` are scheduler-sensitive observations with no winner ratio.

.NET `allocatedBytes` covers the whole process during the scenario, including
workers and driver. Java covers only the driver thread via ThreadMXBean, excludes
background maintenance/loader/listener workers, and emits -1 if unavailable.
These allocation scopes cannot form a .NET/Java allocation ratio. GC and sync
fan-in contract samples deliberately emit -1. Reports record exact binary input
identity; a serial runner must additionally freeze source and command inputs.

Every ordinary sample verifies final count <= C, final weighted size <= the live
maximum, and zero .NET maintenance backlog. Caffeine has no public backlog API,
so its field is null; `cleanUp` and public bounds remain checked. Value reads must
match the expected preallocated object. Backend calls and true-bulk calls have
exact expected counts. Churn and maximum-mutation cases validate every final
resident value; their checksum may differ because eviction victims need not match.
No cross-runtime victim/checksum equality is required for policy experiments.

## Scenario and semantics matrix

N = cycles, F = fan-in, C = capacity. Unless marked otherwise these are matched
public-workload throughput comparisons; they isolate a mechanism but include the
validation/driver work described above.

The `weight-replace` row describes the corrected workload in
`corrected-weight-source/`, not the original `final-source/` run. The original
generator reused the same object and weight for each key. Both data sets remain
separate; see [the correction and its evidence](../latest-20260914/README.md).

| Scenario                                                | Configuration and per-cycle work                                                          | Assertions / reported work                                                                                                                                                                                                         |
| ------------------------------------------------------- | ----------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `size-churn`                                            | maximumSize C; put a key from 4C keyspace                                                 | N operations; final capacity and resident values                                                                                                                                                                                   |
| `weight-churn`                                          | maximumWeight C, weight 1..4; same 4C-key put trace                                       | N operations; weight/count bounds; positive weights keep .NET resident cap nonbinding                                                                                                                                              |
| `weight-replace`                                        | weighted cache, C/8-key replacement set; alternate ID and weight                          | 2N operations (put/read); exact replacement identity; no capacity eviction expected                                                                                                                                                |
| `mixed`                                                 | C/2 keys; 20% put, 10% invalidate, 70% read                                               | N operations; independent dictionary model verifies presence and identity                                                                                                                                                          |
| `ttl`, `tti`                                            | 10ms fixed expiry; put, advance 6ms/read, advance 6ms/read, advance 11ms/read             | 4N operations; second read misses for TTL, hits for TTI; last always misses                                                                                                                                                        |
| `variable`                                              | create/update/read callbacks each return 10ms                                             | Same 4N sequence as TTI; verifies read renewal                                                                                                                                                                                     |
| `variable-update`                                       | put; +6ms; replace; +6ms/read; +11ms/read                                                 | 4N operations; verifies replacement renewal and later hard expiry                                                                                                                                                                  |
| `ttl-cleanup`, `tti-cleanup`, `variable-cleanup`        | put 16 values, drain registration, advance 2s, explicit cleanup without reads             | 18N operations; pre-advance count is 16; no residents survive cleanup                                                                                                                                                              |
| `runtime-maximum`                                       | alternate max C and C/2, then 16 puts                                                     | 17N operations; final bound matches current maximum                                                                                                                                                                                |
| `runtime-expiry`, `runtime-access`                      | restore TTL/TTI 10ms, put, +6ms, reduce to 5ms, read                                      | 4N operations; existing value expires under new duration                                                                                                                                                                           |
| `runtime-variable`                                      | put, set remaining duration to 5ms, +6ms/read                                             | 3N operations; mutation succeeds and read misses                                                                                                                                                                                   |
| `variable-put`                                          | publish with explicit 5ms duration, +6ms/read                                             | 2N operations; explicit duration overrides default 10ms callback                                                                                                                                                                   |
| `runtime-refresh`                                       | restore refresh 10ms, put alternate value, +6ms, reduce to 5ms, get                       | 4N operations; waits for new value via quiet lookup; N backend calls                                                                                                                                                               |
| `eviction-listener`                                     | size-churn with reliable synchronous policy-removal counter                               | N puts; listener count reported; policy victim/count need not match                                                                                                                                                                |
| `removal-listener`                                      | repeatedly replace one key with alternate objects; wait for callbacks                     | N puts and exactly N-1 callbacks; **mechanism observation only, no equivalent-contract ratio**: .NET bounded async dispatcher sized N+16, Java default async executor has no matching bounded/drop contract                        |
| `strong-lookup`, `weak-key-lookup`, `weak-value-lookup` | C/2 resident key/value pairs; held roots; N reads                                         | all hits, exact identity; preflight equal-but-distinct key hits in strong/weak-value mode and misses in weak-key mode                                                                                                              |
| `quiet-lookup`                                          | same resident set through policy quiet lookup                                             | N hits without changing stats; excludes recency/refresh updates by API contract                                                                                                                                                    |
| `weak-key-cleanup`, `weak-value-cleanup`                | drop target root, retain counterpart; bounded forced GC + cleanup                         | **contract only**, operations=0; target weak reference clears and resident count reaches zero within 32 passes; no GC-throughput or memory-retention ratio                                                                         |
| `sync-miss`, `manual-sync-miss`                         | invalidate one key, get with fixed loader / per-call factory                              | 2N operations; N backend calls and exact returned identity                                                                                                                                                                         |
| `sync-fanin-contract`, `manual-sync-fanin-contract`     | one round of F dedicated callers; loader blocked until all caller scopes started          | **contract only**, operations=0; none completes before release, one backend, F correct results; no claim that every caller reached the internal flight table before release; thread creation is not a cache-throughput measurement |
| `async-completed-miss`, `manual-async-completed-miss`   | invalidate then get with already-completed Task/Future loader                             | 2N operations; N backend calls; explicitly not genuinely async I/O                                                                                                                                                                 |
| `async-gated-miss`, `manual-async-gated-miss`           | invalidate then get; verify task pending; release gate; await result                      | 2N operations; N truly pending backend calls; no sleep/network                                                                                                                                                                     |
| `async-gated-fanin`, `manual-async-gated-fanin`         | invalidate, start F calls to same generation, verify every task pending, release one gate | (F+1)N operations; exactly N backend calls, NF correct results                                                                                                                                                                     |
| `refresh-explicit`                                      | put alternate old value; explicit refresh and await                                       | 2N operations; N reload calls and new resident value                                                                                                                                                                               |
| `refresh-auto`                                          | put alternate value, +11ms, get triggers refresh; await new quiet value                   | 2N operations; N backend calls; initiating read may return old/new according to completion timing, never an unrelated value                                                                                                        |
| `bulk-sync`, `bulk-async`                               | clear, request 16 keys via true bulk capability                                           | 2N operations; N bulk calls, zero single-key calls; exactly 16 requested results; async backend completion is immediate and labelled as such                                                                                        |
| `prefetch-sync`, `prefetch-async`                       | same true bulk call returns extra key 16; quiet-check its admission                       | 3N operations; requested result excludes extra key; extra value is resident; N bulk/zero single-key calls                                                                                                                          |
| `trace-policy`                                          | exact JSON trace fixture; each request does read, miss put, then cleanup                  | **policy hit-rate comparison only**; hits+misses=N, backend=0; input SHA-256 emitted; throughput and victim equality are not acceptance criteria                                                                                   |

Use N substantially greater than C for churn/eviction-listener performance runs;
N <= C can exercise only under-capacity insertion. The CLI does not silently
increase N, change C or remove misses. Small smoke counts are only harness checks.

## TimerWheel cleanup qualification

The matched lazy-cleanup cases explicitly drain all inserted entries before
advancing the manual monotonic clock by 2 seconds; registration and expiry cleanup
are both measured. Moving fake time while registration remained asynchronous
produced another failed Caffeine smoke, so this precondition is checked explicitly.
Caffeine v3.2.4 TimerWheel's first bucket span is `2^30` nanoseconds, about 1.07s
(`caffeine/src/main/java/com/github/benmanes/caffeine/cache/TimerWheel.java:54-55`).
At +11ms its hard-expiry lookup rejects an expired value, but `cleanUp()` can still
retain the variable-expiry node until a bucket boundary. An initial harness smoke
that incorrectly demanded an empty cache at +11ms failed on Caffeine; the corrected
case preserves the hard-expiry tests and gives both cleanup implementations the
same +2s clock advance. No real-time sleep or scheduler guess is used. This case
does not measure prompt-expiration scheduler latency.

## Coverage beyond these drivers

| Mechanism                                                                                                          | Evidence source / comparison boundary                                                                                                                                                                            |
| ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Concurrent resident hits / stats cost                                                                              | Existing HitProbe, CaffeineHitProbe and frozen resident-read reports; these probes need not duplicate that matrix                                                                                                |
| Same/changed resident writes and reliable write drains                                                             | Existing WriteProbe and matched Caffeine write driver; separate write regression and throughput gate                                                                                                             |
| Adaptive W-TinyLFU hit rate and phase changes                                                                      | `trace-policy` with separate scan/cycle/zipf/hotset-scan/phase-changing fixtures; existing Simulator additionally compares LRU/SLRU/fixed/adaptive/BitFaster. Identical trace does not require identical victims |
| Caller cancellation, cache-owned timeouts, finite load permits, shutdown, old-identity/epoch fencing               | Deterministic .NET contract/race tests and long stress; Caffeine has no equivalent waiter-token/lifecycle/admission contract for a blanket throughput ratio                                                      |
| Weak-reference GC collection vs retention                                                                          | Bounded collection contract cases here plus dedicated lifetime/retention tests; forced GC proves neither production retention bounds nor GC performance                                                          |
| Scheduler-driven expiration, refresh failure/backoff/conflicts, bulk fencing, listener rejection/drop/backpressure | Dedicated deterministic contract/race tests; successful-path throughput here does not certify those interleavings                                                                                                |
| Cache-aware dictionary operations, atomic compute/reentrancy                                                       | Dedicated API and adversarial tests. Public map APIs differ; not represented by the put/read/invalidate mixed workload                                                                                           |
| Zero-weight/oversize entries, concurrent maximum changes, overflow                                                 | Dedicated capacity/weight tests; positive-weight throughput matrix does not replace them                                                                                                                         |
| Policy hottest/coldest, age/duration inspection                                                                    | Final coldest resident validation exercises snapshots; ordering/age contracts require dedicated tests, not an invented snapshot latency result                                                                   |
| .NET memory-pressure eviction, owned values/leases/disposal                                                        | Native extensions without matching Caffeine contract; verify separately, never label as Caffeine parity/ratio                                                                                                    |
| Stats/Metrics, DI, options, consumer integration, AOT/trim, package/provenance                                     | Existing unit/consumer/host/build/package gates; System.Diagnostics.Metrics and DI have no like-for-like Caffeine engine throughput operation                                                                    |
| Soft values / removed CacheWriter                                                                                  | Explicitly unsupported per project ADR; not silently counted as implemented                                                                                                                                      |

The full product remains experimental until the repository's actual release gates
are met. Successful scenario probes do not close unrun platforms, long stress,
retention, AOT/package or feature-specific adversarial gates.
