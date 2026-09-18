# Operation semantics

The four cache personalities share one engine and policy implementation. This document defines observable behaviour; [release readiness](release-readiness.md) records verification separately. A successful load need not still be resident when its waiter receives the result. Mapping visibility, flight ownership, maintenance and statistics are not one globally linearizable transaction.

## Inputs and failures

Keys and values are `notnull`; null keys, explicit values, loaders, options and time providers are rejected at runtime. Capacity must be explicitly positive. `MaximumSize` and `MaximumWeight` are mutually exclusive; weighted caches also require a weigher and `MaximumResidentCount`. `MaxConcurrentLoads` is optional: omitted/null means no configured limit, while zero or negative values are invalid. Fixed durations are disabled by null and otherwise must be positive, including `TimeSpan.MaxValue`; negative infinity sentinels are not accepted. Variable expiry has different duration rules below.

Options are validated and copied at construction. The comparer is fixed and must be thread-safe, stable, fast, pure and non-reentrant. Key equality and hash codes must not change while cached.

Basic argument validation precedes the disposed check, which precedes cancellation. A pre-cancelled call returns cancellation even on a hit, without request statistics, loading or refresh side effects. Once started, cancellation and success may race; the first completion wins. Callers must handle exceptions both when invoking and when awaiting an operation.

A synchronous loader exception, null task, null result, fault or loader cancellation terminates the flight. Null results/tasks are `InvalidOperationException` contract violations, not negative caching. Failures are not retained permanently; a subsequent request may retry. Non-null default values such as zero, false and the empty string are valid.

## Operations and visibility

| Operation        | Behaviour and coordination boundary                                                                                                                                                                              |
| ---------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Construction     | Validate and snapshot configuration; no loader invocation.                                                                                                                                                       |
| `GetAsync` hit   | Return a fresh value in a single-use `ValueTask`, recording access. The read boundary follows the publication protocol in [concurrency](concurrency.md). Later invalidation cannot retract a returned reference. |
| `GetAsync` miss  | Install or join the current generation's shared flight. Installation/join and result publication are separate events.                                                                                            |
| `TryGet`         | Return only a completed, hard-fresh value; record access on a hit. Do not start/join a load, wait or trigger refresh.                                                                                            |
| `TryGetTask`     | Return an existing task, including a pending shared flight, without starting work. Ready results count as hits; pending/absent results count as misses. A pending result does not create another waiter or load. |
| `Put` / `Set`    | Commit a new value version and applicable timestamps. Older work cannot overwrite or remove it. Normal admission/eviction still applies.                                                                         |
| `Invalidate`     | Remove the current slot, including a placeholder, under exact identity coordination. Return whether a slot was removed. Do not wait for or cancel its old loader.                                                |
| `Clear`          | Under the engine gate, clear the authoritative map, advance the epoch and reset policy/wheel state. Old work/events cannot affect the new epoch; execution reservations are not reset.                           |
| `CleanUp`        | Process currently executable maintenance with bounded progress. Do not wait for arbitrary loaders or callbacks. With no publishers, repeated bounded maintenance reaches the documented quiescent invariants.    |
| `EstimatedCount` | Approximate physically resident ready mappings, excluding pending loads. Physically retained expired values may still count.                                                                                     |
| Statistics       | Weakly consistent aggregate counters and ownership gauges, not a transaction snapshot.                                                                                                                           |
| Disposal         | Close admission and revoke publication before signalling cache-owned cancellation. Existing promises race to their first terminal result. Never dispose ordinary cached values automatically.                    |

Operations that overlap invalidation may return an already acquired old value or flight result. An operation beginning after invalidation returns must not read/join that retired slot. All deferred removal, expiry, eviction and failure handling checks the exact entry/epoch and, where values can change in-place, the publication revision.

Stable resident replacement may reuse an entry/node only with the built-in policy, strong references, no expiry or automatic refresh, no flight and unchanged weight. Every replacement still advances publication/variable revisions and creates a new task view on demand, even for the same object reference. The original resident key remains the representative for comparer-equal keys. Without bulk capability, listeners or owned values, a revalidated entry-only commit is permitted; other combinations use engine coordination. This does not weaken conditional mutation, notification or capacity semantics.

Synchronous `Put` does not eagerly allocate a completed task. `TryGetTask` creates and reuses a task for that exact value version under the same freshness/entry lock. A later replacement does not change previously returned tasks. No-time-policy replacement does not read a clock or update unused timestamps.

Expired/collected read cleanup must revalidate entry identity, observed publication revision and current freshness/collection. A same-entry refresh or a duration extension can invalidate the original cleanup decision; the overlapping read may still report its original miss.

## Loading, cancellation and reentrancy

Single-flight means one current flight per cache instance, comparer-equal key and generation. Invalidation, clear or replacement can leave old and new loaders running concurrently, but only the current owner can publish. Installing a placeholder does not give its creator exclusive responsibility for starting it: a joiner can help start the flight exactly once. Loader side effects never belong inside a potentially repeated `ConcurrentDictionary.GetOrAdd` factory.

Caller tokens cancel only that caller's wait, using a detached waiting scope such as `Task.WaitAsync`. They are not forwarded as the shared loader token. Even if every waiter cancels, shared work may finish and populate the cache. Wait registrations are released when waiting ends. The loader token belongs to cache lifetime and optional load timeout.

Success publication and failure removal check disposed state, epoch, exact entry and flight ownership in the same critical section. Internal identity is reference identity, not value equality. `Set`, `Invalidate` or `Clear` may occur between publication and promise completion.

A logical loader chain detects `K → K` and `K → J → K` using the cache comparer and fails before self-waiting. `K → J` is legal; scope is restored in `finally`. A ready hit need not wait and is not rejected merely for sharing an ambient key. `AsyncLocal` is not a general cross-task cycle detector. Background scheduling avoids retaining request `ExecutionContext`; `ConfigureAwait(false)` alone does not suppress it.

Manual synchronous factories and synchronous loading use genuine synchronous work/signalling, never an async loader wrapped with `.Result`. Different keys can execute concurrently. Manual async factories execute only for the winning flight. `Put(Task<TValue>)` installs a new generation; fault/cancellation/null completion cannot remove a newer generation. The caller's original task remains valid after invalidation. Cache personalities share implementation, not blocking views of each other.

## Expiration and refresh

Freshness uses monotonic `TimeProvider` timestamps/elapsed time, not wall-clock differences or loader start time. Timestamp zero is valid. `elapsed >= duration` is expired. Successful value publication resets write/access timestamps; successful access advances access time without regression. When TTL and TTI coexist, either can expire the value. An old/lost read event cannot revive it. Freshness is a read-time guarantee, not a guarantee throughout the caller's use.

Physical cleanup may lag freshness. With no prompt scheduler, an idle cache can retain expired references until subsequent activity, cleanup, clear or disposal. Ordinary hits do not scan the table, and there is no per-entry timer. Duration conversion must handle large values without overflow. The fixed write-only immutable-publication path and other synchronised freshness paths are specified in [concurrency](concurrency.md).

`RefreshAfterWrite` makes an entry eligible when a subsequent `Get` accesses it; idle time does not trigger reloads and `TryGet` does not schedule them. A still-fresh hit returns the old snapshot immediately. Automatic refresh reserves a shared flight and offloads the loader's synchronous prefix; there is no per-hit task or unbounded refresh queue. When configured admission is full, automatic refresh may be skipped and counted without failing the hit.

Explicit `RefreshAsync` joins a current refresh/cold flight or creates one. Without a value it behaves like a loading miss. It waits for the result, respects caller-only cancellation and bypasses automatic failure backoff, but not configured admission limits. Once the old value hard-expires, reads join the still-valid refresh rather than returning stale data or starting a duplicate load. Cleanup may detach resident policy membership while retaining that flight.

Refresh success publishes only for the current entry/epoch/flight and resets timestamps. Failure or timeout preserves the old value only until its original hard expiry, is visible to explicit waiters and does not fail the original automatic-refresh hit. Background faults are observed. Automatic retry backoff defaults to one second and is configurable to a positive duration; it neither extends TTL nor prevents necessary cold loading after expiry. Eviction, set, invalidation, clear and disposal revoke refresh publication rights.

## Admission, timeout and shutdown

`MaxConcurrentLoads(C)` is opt-in and rejects a new distinct flight with `CacheLoadRejectedException` when full; hits and existing-flight joins consume no new permit. There is no wait queue. `MaxPendingLoadKeys(F)` independently bounds owned key states. If omitted it inherits explicit C, raised to a larger configured `MaximumBulkKeys(K)` where applicable. With neither C nor F configured, there is no cache-configured active-work bound. True bulk requires F and K, with K ≤ F; C remains optional. See [ADR0015](adr/0015-opt-in-load-admission.md).

Pending loads do not consume resident capacity and ordinary capacity eviction does not remove cold placeholders. All epochs share reservations. Invalidation, clear and timeout cannot release work that is still executing or completing cancellation/finalisation. Host admission remains responsible for caller tasks/references and memory used inside loaders.

`LoadTimeout` is a shared result deadline and a cooperative cancellation signal, not proof that backend work stopped. It terminates the promise, fences publication and observes late faults. A timed-out loader retains its execution permit until underlying work and required cleanup really finish. Timeout increments `LoadTimeouts` (and `RefreshFailures` for refresh), not a fabricated loader-cancellation outcome. Terminal load outcome counters are mutually exclusive.

Disposal rejects new operations with `ObjectDisposedException`, attempts to terminate active promises with that exception, and starts cache-owned cancellation outside locks. A concurrent loader outcome may win promise completion, but cannot publish after closure. All `DisposeAsync` calls share bookkeeping completion: active-snapshot promises are terminal and cancellation has begun before return. It does not await arbitrary loaders/token callbacks. Concurrent synchronous disposal guarantees closure but may return before the first caller finishes teardown; it does not block on async work.

Backend terminal ownership is separate from promise completion. A bulk group's bounded completion gate completes all owned-key promises and its owner promise consistently; shutdown can win while a backend owner is blocked in a listener. The gate performs only internal signalling, not callbacks, timer disposal or engine retirement. Reservations remain until actual retirement conditions hold.

Finalisation retains the active registry through timer/CTS cleanup attempts and promise notification. A timeout with no running backend may still need that registry so disposal can find its pending promise. Ordinary completion coordinates terminal signalling with reservation removal, so the next immediate distinct load is not rejected by an already completed ordinary reservation. Timer-disposal exceptions are secondary infrastructure failures: counted in `MaintenanceFaults` when enabled, without replacing the loader outcome or rolling back a valid/newer publication. They do not prove a custom provider released its resources.

Bulk failure removes only its pending owners or exact entry/revision publications. Publication ownership is recorded before clearing the flight or performing fallible initialisation, allowing partial initialisation to be cleaned up and retried without deleting newer refresh/set values. Refresh rollback restores the old data/deadline using a _new_ revision and snapshot identity, preventing ABA.

## Bulk and callback commits

Input enumeration is snapshotted, bounded and comparer-deduplicated before new flights are installed. Nulls, enumeration exceptions and oversized input fail before installation. `GetAllPresent` is a per-key freshness check and a weakly consistent result, not a multi-key transaction.

Loading bulk reads join existing flights and reserve only missing keys. Explicit bulk capability uses one `LoadAll`; absence of that capability uses single-key fallback. Do not infer capability by catching `NotSupportedException` from user code. Bulk caller cancellation affects only its aggregate wait, including newly owned bulk and existing shared single-key flights.

Copy and validate the entire loader result before publication. Missing required keys, nulls or comparer-duplicate results fail the newly owned group; existing flights are unaffected and later requests may retry. The loader must permit safe enumeration while its result is copied. Subsequent map mutation cannot alter the snapshot. Validated results publish independently with per-key fencing; loss of publication rights does not prevent original waiters receiving the computed result.

Extra prefetched keys are admitted only while the original epoch is valid, the key is absent and no newer explicit mutation of that key conflicts. Unrelated mutations do not block prefetch. A bounded 1,024-key mutation journal exists only while bulk groups are active; overflow/sequence exhaustion rotates its token, suppressing old groups' extras while allowing new groups to proceed. Requested keys still use exact ownership. Weak-key journals do not strongly retain targets. Extras are not caller requests/hits/misses and never overwrite existing mappings.

`PutAll` and multi-key invalidation validate input first, then commit each key independently; concurrent operations may interleave. `GetAll` does not imply bulk refresh: eligible keys refresh separately.

Weighers run outside locks before explicit mutation commits. A `Set` overlapping `Clear` may legitimately commit into the new epoch after clear: it is an ongoing explicit mutation, not a resurrected loader. Clear does not rerun the weigher. Invalid/throwing weights leave current mappings intact. Variable-expiry callbacks depend on old data/duration, so their results require snapshot/revision validation before commit; conflicts cannot write stale deadlines, and retries are bounded.

## Weight and variable expiry

Weight is immutable per value version, recalculated for creation, update, refresh and bulk results. Negative/overflowing weights fail without corrupting accounting. Zero weight remains zero but is bounded by the independent resident-count cap and is still subject to expiry/invalidation/collection. Oversized values may be rejected from residency while successful load waiters receive the result.

`SetMaximum` accepts positive values up to `int.MaxValue` for size and `long.MaxValue` for weight. It coordinates resize/eviction, preserves legal proportions on growth and handles tiny/zero-weight cases on shrink. Clear does not restore the builder maximum. Pending flights and execution quotas are unaffected. After publishers stop and cleanup completes, count/weight converge to their bounds.

Variable expiry is mutually exclusive with fixed TTL/TTI; refresh can coexist with either. Create/update/read callbacks receive value, monotonic time and applicable remaining duration. Zero or negative duration means immediate expiry; `TimeSpan.MaxValue` is a maximum finite duration. Lookup freshness never waits for the TimerWheel. Scheduling, rescheduling and removal use exact nodes, including wrap, large jumps and budgeted remainder handling. A single prompt timer schedules the next necessary wake-up.

Runtime fixed-duration changes affect later freshness checks without rewriting original write/access age. Explicit variable-duration changes affect only the validated value version. Old queued observations cannot revive expired/replaced versions.

## Weak references, listeners and diagnostics

Weak keys require reference types and use identity, not value equality. Stored hashes and dead-wrapper identity remain stable after collection. Ready entries/policy/timer metadata must not strongly root the key; active loaders may retain it. Values referring to keys follow ordinary CLR reachability: this is not an ephemeron guarantee. Weak values require reference types and synchronous personalities; async tasks retain results, so async weak-value combinations fail at build time. Collected reads miss and deferred cleanup removes only the observed version. JVM soft values are intentionally unsupported.

A resident value version generates at most one removal event. Causes distinguish explicit removal, replacement, expiry, size, weight, collection, clear and memory pressure. Eviction listeners receive automatic causes (expiry, collection, size, weight, memory pressure), not explicit/replaced/cleared events. They run reliably outside all cache locks before the owning synchronous step or normal successful load completion returns. They may delay that operation; shutdown does not wait for another operation's slow callback. This differs from Caffeine's callback lock/visibility boundary.

Removal listeners use bounded asynchronous capture/dispatch and may drop on pressure, scheduler rejection or shutdown. Neither listener is a disposal protocol. `GetNotificationStatistics()` alone remains usable after disposal to inspect shutdown drops; it neither reopens admission nor waits for callbacks.

Statistics default off. Enabled counters are bounded, striped and saturating; gauges reflect real ownership. Pre-cancelled/invalid/disposed calls do not count. Fresh `Get`/`TryGet` count hits; absent/pending/expired reads count misses. One hundred callers sharing a new flight can produce 100 misses, one load and 99 coalesced waiters. Caller cancellation is not loader cancellation. Successful invalidated work remains a load success. Clear does not reset lifetime counters.

`LoadsStarted`, outcomes and total load time count actual delegate invocations. Manual `Put(Task)` does not invent one, although a cache-owned deadline can still count a timeout. Explicit refresh uses refresh counters; joining a flight also records miss/coalescing. Bulk request counts are per deduplicated requested key, backend counts per invocation. `Evictions` counts capacity removals (size/weight/memory pressure), with other causes separate.

Disabled public cumulative counters are zero; in-flight/backlog gauges remain meaningful. Metrics-only mode may maintain internal counters while public cumulative statistics stay hidden. `InFlightLoads` excludes unstarted/finalising reservations and is not remaining admission capacity. Read/write backlog is approximate; full reliable writes drain/retry, never silently drop. Read drop totals combine terminal Full/Failed/Shutdown once, not each CAS retry. Snapshot aggregation avoids double-counting rings shared across expanded tables. Zero hidden counters do not mean no read events were dropped. Labels contain bounded cache/cause/status dimensions, never keys, tenants or content.

## Policy views

Only enabled features have policy views; construction-time absent expiry cannot be introduced through a fast-path cache. Quiet lookup returns a fresh resident value without statistics, access/expiry updates, refresh or loading. It need not physically remove an expired value.

Hot/cold snapshots are independent read-only copies, not exact global frequency rankings. Hottest traverses protected/window/probation MRU-first; coldest traverses probation/window/protected LRU-first. At most the requested non-negative number of candidates is visited, so expired candidates may reduce result count. No fill-up scan, user callback or access recording occurs. Complexity is O(limit); dictionary enumeration separately costs O(slots). Runtime policy setters use engine coordination rather than mutating external options.
