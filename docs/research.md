# Research and design basis

Research accessed on 12 September 2026. This is a record of selected primary-source reading, not a whole-repository audit, an upstream test run or legal clearance. Current implementation and qualification are reported separately in [release readiness](release-readiness.md).

## Pinned references

| Project           | Tag or scope          | Commit                                     | Purpose                                                               |
| ----------------- | --------------------- | ------------------------------------------ | --------------------------------------------------------------------- |
| Caffeine          | v3.2.4                | `836b65c0a83e5d1641ded9c6de578654bc04b2e9` | Async ownership, admission, adaptation, expiration and selected tests |
| Caffeine          | master audit snapshot | `d885a95eee51fdfe13f450fd9cba80f58f7e0def` | Selected WindowClimber, sketch and TimerWheel changes                 |
| BitFaster.Caching | v2.6.1                | `a71bec32a6b7f621af7a9d2b1d7c4edd0209b289` | Atomic factory, policy, expiry, partition and selected tests          |
| Guava             | v33.7.1               | `c5b5a383a1f7f4a84c17de910f61181011c95908` | Loading API experience and licence; not a loading-core audit          |
| .NET runtime      | v10.0.12              | `4271d88e0aebf3d04f188f1334c2220d80555ef6` | CLR memory model                                                      |
| .NET runtime      | v8.0.31               | `1219a42122cf5190c7f512850557e38c422430dc` | WeakReference, ConditionalWeakTable and identity hashing              |
| FusionCache       | v2.7.2                | `841ee7e8a467481bb4e6da2e9a07fe629ddda8bc` | README feature comparison and licence only                            |

Release/tag queries and immutable source URLs, byte counts, hashes and inspection scopes are retained in the [initial manifest](research-evidence/manifest.json) and [full-scope evidence](research-evidence/full-scope/README.md). A download is not proof that every method was read. Mutable wiki pages are explanatory references, not version identifiers.

## Ecosystem and reuse decision

.NET already has mature caches. BitFaster provides adaptive W-TinyLFU, atomic factories and a TimerWheel. HybridCache provides stampede protection and aggregate caller cancellation; FusionCache covers resilience and application integration. These do not imply identical ownership, expiry or eviction contracts.

Caffeine distinguishes synchronous computations from incomplete async futures during invalidation. Its request-triggered refresh can coexist with hard expiration. Guava's loading API is useful context, but its invalidation, null and maintenance behaviour must not silently replace this library's explicit contracts.

Three implementation routes were evaluated:

| Route                                 | Benefits                                                         | Contract and maintenance costs                                                                                                                                          |
| ------------------------------------- | ---------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| BitFaster facade                      | Reuses an established .NET policy and concurrency implementation | Needs separate generation/lifetime ownership, pending-versus-resident accounting, combined TTL/TTI and bulk semantics; a wrapper alone does not demonstrate equivalence |
| Adapt selected primitives             | Existing C# sketch, partition and wheel experience               | Internal node/time/event coupling, attribution chains and upstream upgrades need explicit adaptation and tests                                                          |
| BCL authority with a dedicated engine | Direct control of ownership, composition and public API          | Bears the full policy, concurrency, resource and qualification burden                                                                                                   |

The third route was selected; see [ADR 0002](adr/0002-adaptive-policy.md). No adapter spike proved the other routes impossible, and this decision makes no performance claim about BitFaster.

Selected BitFaster observations at the pinned revision:

- `AsyncAtomicFactory` shares an initializer, observes faults and permits retry. Its materialised value equality is unsuitable as this library's exact-entry identity fence; that is a contract mismatch, not an upstream bug.
- `AtomicFactoryAsyncCache` installs a side-effect-free wrapper before evaluating it. Its selected GetOrAdd/Clear/TryRemove/TryUpdate paths do not establish this library's caller-only cancellation or epoch contract.
- Builder tests reject the selected fixed TTL+TTI combination and describe variable-expiry restrictions with atomic/scoped caches. These are version-specific observations.
- `LfuCapacityPartition` uses hit/miss deltas, restart threshold 0.05, step ratio 0.0625 and decay 0.98, clamps main ratio to `[0.2, 0.999]`, and has a readonly integer capacity with a minimum of three. `IBoundedPolicy` exposes capacity and Trim, not a mutable long-weight maximum.
- Its TimerWheel couples TimeOrderNode, Duration and ConcurrentLfuCore; its Caffeine ancestry remains relevant even though the repository is MIT-licensed.

## Policy research

The selected local formulas and differences are specified in [ADR 0002](adr/0002-adaptive-policy.md), with expiration in [ADR 0003](adr/0003-timer-wheel.md).

Caffeine stable starts with `W = M - floor(0.99*M)` and protected capacity `floor(0.8*(M-W))`. Its hit-rate climber starts at `-0.0625*M`, reverses direction when the sampled hit rate falls, restarts for a rate change of at least 0.05 and otherwise decays the step by 0.98. Adjustment truncates towards zero. Node moves are bounded to 1,000; incomplete weighted transfers must restore unused segment quota and retain residual adjustment. Policy samples count replayed accesses and insertions, not public requests or coalesced waiters.

Admission accepts a candidate with higher frequency. Otherwise, a candidate with frequency at least six gets a 1/128 jitter chance. This reduces exploitable sketch collisions; it is not a defence against arbitrary hostile comparers or constant hashes.

The stable sketch uses four 4-bit counters in one eight-long block, takes their minimum, saturates at 15 and ages when successful increments reach its sample size. Halving includes odd-counter correction. Table length is at least eight and a power of two; sample size is capped `10 * entryCapacity`. Weighted caches estimate entry count rather than allocating from weight units. Overflow and CLR allocation limits require local bounds.

The selected master sketch keeps at least 256 longs, can retain a larger table while reducing the sample period, clamps observations below the new period and prevents negative ageing correction. Repeated capacity tracking must neither trigger ageing continuously nor make it unreachable.

The master WindowClimber is broader than the stable hit-rate controller:

| Maximum     | Selected master behaviour                                                                                                     |
| ----------- | ----------------------------------------------------------------------------------------------------------------------------- |
| At most 512 | Reactive, initially grows; restart magnitude at least two; decay 0.995; sample period extends from one to four sketch periods |
| 513–4,096   | Reactive, initially shrinks; decay 0.98; one sketch period                                                                    |
| Above 4,096 | Density-based, sample period capped by `4*M` and sketch period; probes, audits, backoff, anchors and recovery                 |

Normal density steering is approximately `sign(error) * min(0.30*M, abs(error)*0.03*M)`, with a low-signal floor and a 2% window floor. Probe/anchor logic may override it. Only selected reactive, step, sample, reading and normal-steering sections were audited, not the whole density controller. The local adaptive policy is not a complete moving-master port. [Simulator results](simulator-results.md) include cases where fixed W-TinyLFU performs better.

## TimerWheel research

Stable uses bucket counts `[64,64,32,4,1]` and power-of-two nanosecond spans near a second, minute, hour and day. Scheduling selects a level from the remaining duration and a bucket from deadline bits. Advancing scans at most one revolution per level, detaches due buckets and reschedules future entries.

Selected master changes add a controlled pending chain, an advancing guard, a work budget and rewind when work remains. Detached traversal must not trust a stale next pointer after a callback deschedules another node. Exceptions or budget exhaustion must preserve the remainder. Next-delay calculation also considers occupied current buckets and upper-level boundaries.

The local wheel normalises TimeProvider ticks, defines a finite horizon and wrap-safe comparisons, and bounds visited/rescheduled work as well as removals. Lookup checks exact freshness independently of physical cleanup. One scheduler is shared by the cache; there is no timer per entry.

## CLR reference semantics

WeakReference uses a short weak handle by default. A successful `TryGetTarget` produces a strong local; an IsAlive-then-Target sequence is not safe. Weak-key wrappers retain a stable identity hash and compare live targets by ReferenceEquals, with exact-wrapper identity as a separate case. Two collected targets must not compare equal. Policy nodes and events must not accidentally root the key.

ConditionalWeakTable provides dependent reachability, but separately rooted policy entries or values can bypass that property. Its factory may also execute more than once. It was evaluated but not selected. Ordinary weak keys do not break a strong cached value's reference back to its key; no ephemeron guarantee is made.

The CLR APIs do not provide Java ReferenceQueue semantics. Bounded maintenance scans and quiescent CleanUp reclaim collected nodes. Weak values are supported for synchronous personalities; asynchronous weak values are an illegal combination because retained Task/TCS results would contradict the promise. Custom comparers with weak keys and value-type weak targets also fail at construction. Soft values have no equivalent CLR contract; memory-pressure eviction is a separately named feature.

## Primary documentation

- [Caffeine design](https://github.com/ben-manes/caffeine/wiki/Design), [refresh](https://github.com/ben-manes/caffeine/wiki/Refresh), [Guava differences](https://github.com/ben-manes/caffeine/wiki/Guava).
- [BitFaster atomic factories](https://github.com/bitfaster/BitFaster.Caching/wiki/Atomic-GetOrAdd).
- [HybridCache](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid?view=aspnetcore-10.0).
- [ConcurrentDictionary factory semantics](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd?view=net-8.0), [ValueTask](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1?view=net-8.0), [TimeProvider](https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview), [weak references](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/weak-references).

These source readings are engineering evidence, not formal correctness proofs. Attribution and actual source adaptations are recorded in [the upstream map](upstream-map.md); runtime, platform and performance evidence must remain tied to its own source snapshot.
