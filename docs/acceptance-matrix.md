# Acceptance matrix

These are required checks, not a Passed report. Preserve A–G and H coverage; an implementation milestone does not waive a 1.0 feature. Use gates/barriers/fake time and fixed seeds. Watchdog timeouts prevent hangs, not establish ordering. Reference models compare policy-independent contracts rather than identical victims. Runtime/platform evidence is recorded in [release readiness](release-readiness.md).

| ID  | Controlled input or interleaving                                         | Required observation                                                                                 |
| --- | ------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------- |
| A01 | Comparer-equal keys and valid default values                             | Coalesce/cache zero, false and empty strings without default sentinels.                              |
| A02 | Null key/value/task/options/delegate/provider                            | Clear exceptions, no invalid publication, retry possible.                                            |
| A03 | Missing/zero/negative capacity, invalid duration, mutated options        | Fail fast or retain the construction snapshot; positive finite durations accepted.                   |
| A04 | TryGet pending/expired                                                   | False without loading, refreshing or waiting.                                                        |
| A05 | Pre-cancelled hit/miss/eligible refresh                                  | Cancellation without state/statistics side effects.                                                  |
| A06 | Task/async lambdas, value/reference types, comparer and XML examples     | Compile and run without overload/nullability ambiguity.                                              |
| B01 | 100+ gated same-key callers                                              | Exactly one invocation and consistent results.                                                       |
| B02 | Two distinct loaders signal entry before release                         | Actual simultaneous execution, not merely task creation.                                             |
| B03 | Pause installer after placeholder publication                            | Joiner starts the same flight; no duplicate or stranded promise.                                     |
| B04 | Synchronous throw, fault, own cancellation, null task/value then retry   | Original waiters fail; later generation can succeed.                                                 |
| B05 | Cancel A while B waits; then cancel all waiters                          | B succeeds; all-cancelled shared work can still populate.                                            |
| B06 | Explicit concurrency limit full, join versus distinct miss               | Join succeeds, new flight rejects without invoking backend.                                          |
| B07 | Timeout, new request, old success/fault                                  | No late publication, observed fault, running work retains quota.                                     |
| B08 | Cold/refresh with repeated invalidate/clear                              | Execution/reservations neither exceed configured bounds nor leak.                                    |
| B09 | Blocked cancellation callback after backend completion                   | Running gauge may be zero but reservation persists until cleanup.                                    |
| B10 | CreateTimer fires timeout synchronously                                  | Timeout result without disposed-CTS access or permit leak.                                           |
| C01 | L1, invalidate, L2 success, L1 success                                   | L2 remains current; L1 serves only old waiters.                                                      |
| C02 | C01 with L1 failure                                                      | Old catch/finally cannot delete L2.                                                                  |
| C03 | L1, Set(v2), L1 success/failure                                          | v2 survives.                                                                                         |
| C04 | Load/refresh, clear, new-epoch set, old completion                       | No new-epoch/policy contamination.                                                                   |
| C05 | Pause reader holding E1, evict it, insert E2, replay old read            | No E2 touch/removal or ghost node.                                                                   |
| C06 | Delayed expiry/eviction after set/reload                                 | Conditional removal affects only the observed identity/version.                                      |
| C07 | Set/invalidate/clear at completion/publication/removal/TCS gaps          | No resurrection or newer-value deletion; waiter contract preserved.                                  |
| C08 | Repeated clear with queued old maintenance                               | Bounded retired roots; old events cannot affect new policy.                                          |
| C09 | Failure after terminal claim/publication with newer set/refresh          | Promise terminates; exact rollback protects new owner and ready value/task pairing.                  |
| D01 | Timestamp zero, before/equal/after TTL, wall-clock rollback              | Correct monotonic boundary.                                                                          |
| D02 | Slow loader then advance time after publication                          | TTL starts at publication; refresh success resets it, failure does not.                              |
| D03 | Reordered access timestamps and combined TTL/TTI                         | No timestamp regression/revival; first expiry wins.                                                  |
| D04 | Advance time without requests, then eligible Get                         | No idle reload; one triggered refresh.                                                               |
| D05 | Refresh spans hard expiry                                                | Fresh old hit first, then join existing refresh without duplicate cold load.                         |
| D06 | Explicit/automatic refresh fault                                         | Explicit waiter sees failure; triggering hit succeeds; background fault observed.                    |
| D07 | Failure backoff and hard expiry                                          | No TTL extension or blocked cold reload; explicit bypass tested.                                     |
| D08 | Refresh with set/invalidate/clear/eviction                               | No stale publication/resurrection.                                                                   |
| D09 | Blocked synchronous loader prefix and saturated automatic scheduling     | Old fresh hit progresses; configured reservations/queues remain bounded.                             |
| D10 | Maximum duration, conversion overflow, invalid provider                  | No accidental expiry through overflow; invalid input rejected.                                       |
| D11 | Read-expiry callback crossing deadline, age/duration access and setter   | No revived snapshot; valid refresh remains joinable.                                                 |
| D12 | Shorter refresh variable duration and rollback                           | Prompt timer re-arms to effective deadline, without stranded idle expiry.                            |
| E01 | Tiny capacities, rounding, ties, saturation and ageing                   | Membership/sketch invariants and reproducible decisions.                                             |
| E02 | Full/late lossy read transport                                           | Freshness/authority unchanged; observable drops.                                                     |
| E03 | Full writes, paused consumer, scheduler rejection                        | Reliable fallback without lost lifecycle events.                                                     |
| E04 | Worker exit concurrent with final write                                  | No lost wake-up; reliable drain progresses.                                                          |
| E05 | Quiescence and cleanup                                                   | Map/policy agree, capacity converges, no duplicates/ghosts.                                          |
| E06 | Fixed scan/hotset/cycle/changing/skew traces                             | Report hit ratios without demanding identical victims across policies.                               |
| E07 | Unique keys, faults, retries, cancellations and churn                    | Metadata follows current bounds, not historical keys.                                                |
| E08 | Repeated create/dispose with retired loads                               | Measure live roots with isolated JIT lifetime, not allocation alone.                                 |
| F01 | K→K, K→J→K, comparer-equal keys                                          | Fail fast before deadlock.                                                                           |
| F02 | Legal K→J and subsequent calls                                           | Dependency succeeds and logical scope restores.                                                      |
| F03 | Dispose, late completion, new operation                                  | No publication; new operation throws ObjectDisposedException.                                        |
| F04 | Cooperative/non-cooperative loader and stuck token callback              | Bounded shutdown bookkeeping, observed faults, no arbitrary user wait.                               |
| F05 | Slow/throwing/re-entrant listener and shutdown                           | No deadlock/corruption; bounded async queue and documented sync delay.                               |
| F06 | Actual x64/ARM64 runtime stress                                          | Record OS/architecture/seed; cross-compilation is not execution.                                     |
| F07 | Long timeout/ignored loader then disposal                                | Detach/dispose active timers without waiting or timer-rooting the cache.                             |
| G01 | Fixed-seed model and gated generation oracle                             | Visibility/fencing/cancellation agree; emit seed, trace and schedule on failure.                     |
| G02 | Controlled scheduler/Coyote evaluation                                   | State which primitives are controlled; no universal model-checking claim.                            |
| G03 | Adversarial review                                                       | Check stale completion, ABA, wake-up, ValueTask, tokens, overflow, retention, re-entry and shutdown. |
| H01 | Manual sync fan-in, retry and distinct keys                              | One current flight; independent progress; no async.Result.                                           |
| H02 | Sync loading/reload(old)/GetAll/LoadAll                                  | Genuine sync work with correct failure/re-entry/generation contracts.                                |
| H03 | Async manual factories/tasks and fault/cancel replacement                | Shared flight, independent waits, no orphan policy nodes.                                            |
| H04 | Four builders/personality consumers                                      | Valid feature combinations; no fake stubs or blocking async views.                                   |
| H05 | Bulk duplicates/equal keys/missing/null/extras                           | Deduplicate, validate before publication, fence prefetch.                                            |
| H06 | Bulk plus single load/set/invalidate/clear and later result-map mutation | Join valid work, protect new values, copy results, isolate cancellation.                             |
| H07 | Invalid/duplicate bulk mutations                                         | Snapshot/validate first; atomic per key, not across keys; stable counts.                             |
| H08 | Zero/negative/overflowing/heavy weight                                   | Zero stays zero; reject invalid arithmetic; oversized load result can be returned without residency. |
| H09 | Replacement/refresh/bulk weight delta and throwing/re-entrant weigher    | Callbacks outside locks; only current version changes accounting.                                    |
| H10 | Weight/count limits and zero-weight flood                                | Both bounds converge; no history-dependent metadata.                                                 |
| H11 | Runtime shrink/growth under traffic                                      | Adopt limits through coordination; quiescent cleanup meets them.                                     |
| H12 | Variable create/update/read callbacks and conflicts                      | Outside-lock callbacks; exact revisions fence deadlines; illegal combinations rejected.              |
| H13 | Immediate/max/infinite/overflow durations                                | Documented duration rules and safe monotonic arithmetic.                                             |
| H14 | Every wheel level, wrap, jump and coarse future bucket                   | Exact schedule/deschedule/reschedule links; no missed due node.                                      |
| H15 | Wheel budget, pending remainder and nested removal                       | Visited/rescheduled work counts towards budget; preserve/re-arm remainder.                           |
| H16 | Idle scheduler and time/write/clear/dispose races                        | One necessary cache wake-up, no entry timers or old-epoch revival.                                   |
| H17 | Adaptive phases, decay and sample reset                                  | Real grow/shrink matching ADR formula; retain seeds/traces.                                          |
| H18 | Tiny/weighted adaptation and capped transfer                             | Legal rounding, retained residual adjustment and accounting.                                         |
| H19 | Sketch saturation/ageing/collisions/jitter                               | Reproducible estimator/reset; no misleading short low-bit RNG cycle.                                 |
| H20 | Weak-key GC, stable hash, identity and key/value cycles                  | No wrapper strong root; identity explicit, no ephemeron claim.                                       |
| H21 | Weak-value GC, refresh/load races and collected capacity                 | Miss/Collected cause/convergence; reject async weak values.                                          |
| H22 | Runtime maximum/expiry, quiet lookup and age                             | Effective freshness policy; quiet read has no access/stats/refresh effects.                          |
| H23 | Large hot/cold snapshots under traffic                                   | Bounded approximate snapshots without full-table sorting.                                            |
| H24 | All removal causes and refresh conflict                                  | At most one event per resident version; correct causes/order.                                        |
| H25 | Slow/throwing/re-entrant/rejected/full/shutdown listeners                | Bounded async dispatch/drop counters and observed failure; sync eviction delays only its owner.      |
| H26 | Stats off/on, 100 waiters/one load, extras and overflow                  | Separate requests/backend work, quiet/pre-cancel isolation, saturating counters.                     |
| H27 | Typed/named DI, options, scopes and shutdown                             | Singleton ownership/disposal, no captured request scope, runnable examples.                          |
| H28 | PackageReference restore/run, API/XML, trim/AOT publish/run              | Actual artifact execution with SDK/RID/exit/real metadata evidence.                                  |
| H29 | LRU/SLRU/fixed/adaptive/BitFaster on matching traces                     | Raw hit/miss/MRC evidence; no universal winner from one trace.                                       |
| H30 | Four benchmark categories, latency, retention and platform stress        | Source-bound raw evidence; unrun platforms explicit; never derive p99 from a mean.                   |
