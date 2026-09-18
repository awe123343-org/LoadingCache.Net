# Resource bounds

These are ownership and numerical bounds, not a process-memory guarantee. [Release readiness](release-readiness.md) distinguishes measured retention evidence from design arguments. All configuration is snapshotted at construction.

Let N be `MaximumSize` or weighted `MaximumResidentCount`, W the weight maximum, C configured concurrent loads, F configured pending-key reservations, K the bulk record limit, B write capacity, R total read capacity and Q notification capacity. C is opt-in; absent F inherits explicit C, raised to a larger configured K. With neither C nor F configured, active/retired loaders, their CTS/registrations and refresh scheduling have **no cache-configured global bound**. True bulk still requires F/K. Host admission controls caller and backend resources.

| Resource        | Bound and lifetime                                                                                                                                                                                              |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Resident values | Quiescent count ≤ N; weighted size ≤ W. Zero-weight values still count towards N.                                                                                                                               |
| Flights         | C counts reservations across epochs, including unstarted work and finalisation; bulk additionally consumes one F per owned key. Release only after required work, cancellation cleanup and terminal signalling. |
| Waiters         | Caller-controlled count; registrations detach after each wait. All waiters cancelling does not cancel shared work.                                                                                              |
| Retired epochs  | No retained historical map; outstanding work retains only necessary entries/keys/context. Configured C/F bound that work, otherwise it is unbounded by the cache.                                               |
| Reads           | R exact-node/epoch events, lossy when full. Freshness, mapping correctness and exact request statistics are independent.                                                                                        |
| Writes          | B reliable events plus one coordinated publisher; pressure requires cooperative drain/fallback.                                                                                                                 |
| Refresh         | One flight per current generation; reserve before scheduling. Configured C/F bound accepted work; full admission skips automatic refresh but can reject explicit refresh.                                       |
| Notifications   | Capture and dispatch each hold Q events: 2Q plus a constant number of handoff/active events. Values may be retained until delivery/drop.                                                                        |
| Policy          | Intrusive resident nodes and a capped sketch sized by entry estimates, not weight bytes or historical keys.                                                                                                     |
| Expiration      | Constant wheel buckets and one link set per timed node; bounded pending work. One optional prompt timer per cache, no per-entry timer.                                                                          |
| Statistics      | At most 64 × `CacheCounterKind.Count` long slots; fixed-size snapshot allocation on demand, no historical thread/key registry.                                                                                  |
| Weak wrappers   | Counted with node/transport metadata; collection alone does not remove wrappers. Cleanup removes exact collected versions.                                                                                      |

## Publication overshoot

Authoritative commit, reliable enqueue and cooperative drain share the engine gate, so at most one publisher has committed but not enqueued. B defaults to 256.

- After an eviction batch, policy nodes ≤ N and queue ≤ B. Every resident not yet reflected in policy has a pending latest publish. Thus resident count ≤ N+B at completed mutation boundaries, and ≤ N+B+1 during a publication.
- With no new publishers, cleanup leaves writes=0, count ≤ N and weight ≤ W. Check map/node identity, links, membership and accounting together.
- Applied weight ≤ W and at most B unapplied latest values each ≤ W yield authoritative weight ≤ (B+1)W at completed boundaries. This is a mathematical bound, not a safe `long` multiplication. A currently publishing oversized value adds at most `long.MaxValue` before forced flush/rejection.
- Pending-token metadata ≤ B plus the current event. During replay of d events before batch eviction, nodes ≤ N+d and remaining writes ≤ B−d, so combined policy metadata ≤ N+B. Sequence state resides on nodes/tokens, not extra historical dictionaries.
- Policy/read/write strong roots are bounded by N+B+R plus constant active tokens. Timers, flights and listener payloads must be counted separately. Backing-array capacity may reflect the largest configured N: shrinking live membership does not promise immediate array shrinking, but removed nodes must not remain strongly rooted.
- A flush consumes at most its starting B events under the engine gate. Heavy admission may evict N nodes per event: conservative policy work is O(BN), not constant latency. Eviction detaches the node before exact mapping removal and does not recursively enqueue another event. User callbacks remain outside locks.

`EstimatedCount` is a concurrent observation, not an atomic proof of those internal bounds. Runtime shrink flushes/converges under coordination before return; callbacks can subsequently introduce new work. Weigher callback concurrency and allocations outside the gate belong to callers.

## Read transport and snapshots

Production rings contain 64 slots. The lazy table grows from one stripe to at most
`S = 4 * ceilPowerOfTwo(Environment.ProcessorCount)`, giving `R = 64*S`.
Ten logical processors yield S=64, R=4,096. Test overrides may choose other bounds. Compared with 16-slot rings, the 64-slot payload adds 768 bytes per active ring on a 64-bit runtime, excluding unchanged headers/alignment; unused stripes are not allocated.

Slots form a contiguous struct array. The queued gauge scans at most R published sequences, including publications behind an unpublished head; it excludes reservations. Immediate drainability is a separate head check. A batch publishes its consumed cursor once, including the consumed prefix on callback failure.

Read cumulative diagnostics exist only for `RecordStatistics || EnableMetrics`. Full/Failed use a shared 17D-long array, where D is CPU count rounded up to a power of two and capped at 64. Each shard uses its first two fields; neighbouring counters are separated by at least 128 bytes without assuming base alignment. Payload is 136D bytes: 2,176 bytes at D=16, at most 8,704 bytes, plus headers. Both options off means no array or cumulative updates; progress/gauges still work. Clear/expansion/disposal do not reset lifetime totals. Aggregate the shared array once and choose one live-or-retired table, never both.

Disposal detaches storage under the consumer gate and clears all slot values in O(R), leaving sequences for ownership checks. A paused producer's interior reference may retain the array but must not retain unrelated queued payloads. Late publication self-clears after its full-fence shutdown check. Retired rings retain numeric diagnostics only; disposal does not wait for a paused producer.

The no-expiry atomic value read adds no per-read snapshot or per-put box. Fixed write-only entries contain one publication reference: reference/Int32/Int64 initial values need no separate snapshot until refresh; other structs use an immutable snapshot from initial publication. Old snapshots live only as long as readers, with no version registry. Prepared bulk publication adds exact-entry/revision metadata (16 bytes for the two fields on a 64-bit runtime), still within K/F bounds.

## Expiration, pressure, bulk and ownership

A hard-expiry cache owns one TimerWheel; each pass visits at most 128 due nodes and preserves remaining work. Without a prompt scheduler, idle expired references remain until activity/cleanup/clear/disposal. Lookup freshness never depends on physical removal.

Memory pressure uses one timer and one concurrent sample, at most `maximumTrimCount` candidates, and no per-tick accumulating work queue. The source callback runs outside locks. Hot/cold policy snapshots visit O(limit) candidates; caller-requested dictionary snapshots visit O(slots) and their copies belong to callers.

A true bulk group consumes one C reservation and one F per owned key. Input/output records, including comparer duplicates before deduplication, are bounded by K ≤ F. Single-key fallback/bulk mutation default to 1,024 records unless K is configured. Prefetch uses at most 1,024 mutation-journal keys while groups are active; token rotation fails closed for old extras without retaining unbounded tombstones.

`OwnedCache` additionally requires `MaximumActiveValues=A`. Distinct resident aliases, retired-but-leased values and pending/running disposers share A. Each object has one active identity state with separate generation and lease counts. Historical disposal markers use `ConditionalWeakTable`, not strong roots. Scheduler rejection retains the bounded pending state for `CleanUp`/`RetryPendingDisposals`; a stuck disposer retains its slot. Caller lease count/references are outside the cache bound. A forgotten lease deliberately retains its value; no unsafe finaliser guesses when usage ends.

## Timeout and shutdown

Close admission and revoke publication before signalling cancellation outside locks. First-terminal completion chooses waiter results. Bookkeeping may wait for bounded internal coordination, never arbitrary loader/listener/token callback code. Late faults are observed.

`_runningLoads` counts actual delegate execution; `_reservedLoads` and the active registry also cover installation, cancellation cleanup and finalisation. Backend completion alone does not return a permit while `CancelAsync` callbacks are still running. Timer/CTS cleanup attempts retain registry ownership until promises are terminal. Timeout, invalidation and clear cannot bypass configured C/F by detaching non-cooperative work.

Retention verification must cover distinct failures/retries/cancellation, repeated clear/invalidate/create/dispose, ignored cancellation, collected weak entries, saturated transports and paused maintenance. Allocation measurements are not retention measurements. Controlled GC tests isolate JIT roots and inspect exact graphs; endurance also checks time windows, progress, backlog and retired roots. Neither proves all workloads leak-free.
