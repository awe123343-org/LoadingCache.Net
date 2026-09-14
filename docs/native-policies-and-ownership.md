# Native policies and value ownership

## Memory-pressure eviction

`MemoryPressureEviction(interval, threshold, trimFraction, maximumTrimCount)` is opt-in. Interval must be positive and within BCL timer limits; threshold/fraction finite in (0,1], trim count positive. Default `GcMemoryPressureSource` observes the latest GC's `MemoryLoadBytes / HighMemoryLoadThresholdBytes`, or zero without a usable threshold. This is neither current RSS nor cache bytes/OOM warning, and does not call `GC.Collect`. A host may inject a thread-safe source.

Capture at most `min(ceil(residentCount*trimFraction), maximumTrimCount)` approximate cold candidates, then sample outside all locks. A single-owner gate coalesces overlapping ticks without a backlog; timer creation suppresses ExecutionContext flow. Sources may re-enter but should return promptly.

At/above threshold, remove only candidates whose entry, epoch and publication revision still match. Set/refresh/clear during sampling protect newer values. Pending cold work is not a candidate and keeps permits; evicting a refreshing resident revokes its refresh publication rights. Candidate selection is O(trim count), without table sorting or replacement candidates to fill concurrent misses. External references may retain removed values.

`Policy.MemoryPressureStatistics` is null when disabled, otherwise reports samples, pressured samples, evictions, errors and the latest exception. Observe provider failures. Disposal stops the timer/fences late samples without waiting for an arbitrary source. No sensitive keys/values are diagnostic labels.

## Mutable dictionary views

`AsDictionary()` exposes cache-aware `IDictionary`/`IReadOnlyDictionary` facades over the same engine. Sync views add `GetOrAdd`; async views add `GetOrAddAsync`. Neither adds another store nor converts pending tasks into blocking reads.

| Method                                     | Contract                                                                                                                                                            |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Indexer get, `TryGetValue`, `ContainsKey`  | Fresh materialised reads, normal access/statistics; no load/refresh/wait.                                                                                           |
| Indexer set                                | New value version, including replacement of a pending flight. Old waiters may finish without publication rights.                                                    |
| `Add`/`TryAdd`                             | Current value/flight occupies the key, including a valid refresh; `Add` throws `ArgumentException` when occupied.                                                   |
| `TryUpdate`                                | Equality/weigher/expiry outside locks, exact entry/epoch/publication/variable revision and freshness at commit. Conflict returns false without rerunning callbacks. |
| `Remove`, `TryRemove(out value)`           | Remove fresh materialised mappings only; pending/expired returns false. Use cache invalidation for pending work.                                                    |
| Comparison remove / collection pair remove | Compare outside locks, then exact-version removal; an equal newer value is protected.                                                                               |
| `GetOrAdd`/`GetOrAddAsync`                 | Engine single-flight; async cancellation belongs to the waiter.                                                                                                     |
| `AddOrUpdate`, `Merge`                     | Optimistic outside-lock factories; contention may reread/retry them.                                                                                                |
| `Compute`                                  | `CacheValue` plus explicit `CacheMutation.Keep/Set/Remove`; zero/false/default values are not deletion sentinels.                                                   |
| `ComputeIfPresent`                         | Skip callbacks for missing/pending/hard-expired refresh values.                                                                                                     |
| `Clear`                                    | Engine epoch boundary, including pending flights.                                                                                                                   |
| Count, keys, values, enumeration, copy     | Independent snapshots excluding pending/expired values; no access/statistics/refresh side effects.                                                                  |

Snapshots cost O(current slots) under engine coordination and may delay mutation; they are not hot-path operations or globally linearizable snapshots. Count differs from physical `EstimatedCount`. Modifying returned copies cannot modify the cache. Admission may immediately evict a successfully written candidate. Multi-step BCL extension methods do not inherit atomicity: use the facade's explicit atomic methods.

Transform factories must tolerate retries and avoid irreversible side effects. Pending async slots count as missing values but are captured exactly: set replaces, remove revokes, keep preserves. Same-chain transform/self-key mutation/clear makes the outer transform fail fast while preserving an already committed inner mutation. Different-key dependencies/external contention remain legal without an arbitrary retry limit. Weak-value conditional comparison uses reference identity; normal values use default equality. Details and absent ABA fencing are in ADR0012.

## Lease-aware disposal

Raw-value APIs cannot detect when a caller finishes. Eviction-time disposal would invalidate live users; weak references and `GC.KeepAlive` cannot fix that contract. Ordinary caches never automatically dispose values.

Manual `OwnedCache<TKey,TValue>` (`TValue : class`) requires an explicit sync `Action<TValue>` or async `Func<TValue,ValueTask>` disposer. No reflection or droppable listener is used as an ownership protocol.

- `Put` transfers ownership. After successful transfer the caller must not use/dispose the raw value; use `PutAndLease` to continue using it.
- `PutAndLease`/`TryGet` return `CacheLease<TValue>`. Active leases protect values through rejection from residency, invalidation, clear, expiry, eviction, pressure and cache disposal.
- Release each lease after use. Do not use and dispose the same lease concurrently or let its raw value escape its lifetime; independent readers take independent leases.
- Reference-identical aliases within one owned cache share one state. Schedule disposal once after all residency aliases and leases end. Registries are not shared across caches: never transfer the same object to two owned caches.
- Objects whose disposal started/completed cannot be inserted again. Terminal markers use ConditionalWeakTable without strongly rooting historical values.
- Explicit positive `MaximumActiveValues` bounds resident, retired-but-leased and pending/running disposal states. Leave replacement headroom. Rejection before registration/invalid parameters/closed registry leaves ownership with the caller. After registration, engine/weigher failure retires the transferred value for disposal.
- Registration atomically acquires generation reference and operation lease before engine/weigher work; shutdown cannot dispose a value while its weigher uses it.
- Disposers run outside locks in the background. Non-returning work retains its active slot. `GetDisposalStatistics` reports active/pending/error state even after shutdown.
- Scheduling failure retains bounded pending ownership. Cleanup/retry can reschedule only work not already scheduled; `RetryPendingDisposals` remains callable after shutdown. Do not falsely report disposal or free its slot early.
- Cache/lease Dispose and DisposeAsync close/release their bookkeeping, not arbitrary user disposal completion. Provide an external signal in the disposer if strict completion is needed. Concurrent sync disposal guarantees closure; async disposal observes shared engine shutdown bookkeeping.

Owned mode supports manual put/read/invalidate/clear, size/weight, fixed TTL/TTI, prompt cleanup, pressure and statistics. It deliberately exposes no raw dictionary/policy snapshots bypassing leases. Lease-returning loading, bulk, variable-expiry and weak modes are not implemented; ordinary engine support does not imply them. Forgotten leases retain values rather than relying on unsafe finaliser guesses.
