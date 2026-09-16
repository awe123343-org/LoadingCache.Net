# ADR-0012: Eviction delivery, atomic transforms and prefetch fencing

Accepted, 13 September 2026; missing-value ABA protocol updated on 16 September. Supersedes asynchronous eviction delivery in ADR0004 and the global prefetch stamp in ADR0009; extends ADR0011.

## Eviction delivery

Deliver an automatic eviction event outside all cache locks before the owning synchronous step returns, or before normal successful load promise completion. Do not pass it through a droppable removal queue. Exceptions are observed/counted without rolling back removal. A slow listener delays its operation, not another thread's disposal. Other threads can observe removal before the callback; this is deliberately different from Caffeine's callback inside atomic removal. No cross-operation global ordering is promised. Removal notifications remain bounded, asynchronous and droppable; neither listener is a disposal protocol.

## Atomic transforms

Use optimistic snapshot → callback outside locks → exact-version commit. `AddOrUpdate`, `Compute`, `ComputeIfPresent` and `Merge` may reread and rerun callbacks on contention, like the lock-free callback boundary of .NET `ConcurrentDictionary.AddOrUpdate`; callbacks must tolerate repetition. The commit is atomic, not the whole callback. `GetOrAdd` retains generation-scoped single-flight.

Validate entry, epoch, publication and relevant expiry revisions, never just value equality. `CacheValue` distinguishes absence and a legitimate default value; `CacheMutation.Keep/Set/Remove` avoids treating null as deletion. Pending and hard-expired refresh values are absent to transforms but retain an exact captured slot: set replaces it, remove revokes it, keep preserves it; `ComputeIfPresent` skips its callback. Original waiters may complete but cannot publish over replacement.

Same logical-chain transform re-entry, explicit same-key mutation or cache clear makes the outer transform fail fast while preserving the successful inner mutation. Restore/deactivate scope in `finally`. Different-key dependencies and external contention are allowed; no arbitrary retry cap or general cross-task deadlock guarantee is added.

A missing snapshot captures engine mutation sequence and rollover era. Record every successful exact detach, including natural eviction, failed loads and exceptional refresh cleanup, plus explicit intent/missing-key invalidation. Otherwise absent → cold load → resident replacement → eviction → absent could fool an old callback. Capture the sequence _after_ the snapshot's own expired/collected cleanup. Clear/disposal use epoch/admission fencing. Eligible resident-only replacement can avoid a global sequence update; present/pending snapshots still validate their exact revision. An era changes on sequence exhaustion instead of retaining per-key tombstones or adding an observer lock.

## Bulk prefetch

At pinned Caffeine v3.2.4 `836b65c0a83e5d1641ded9c6de578654bc04b2e9`, `LocalManualCache.bulkLoad` and `LocalAsyncCache.AsyncBulkCompleter.addNewEntries` put extra keys independently. Neither bulk operation is a multi-key transaction. Caffeine does not impose a global unrelated-mutation fence and may overwrite mappings with extras.

Preserve this library's stronger absent-only, same-key mutation protection. Track at most 1,024 distinct mutated keys while bulk groups are active, using weak wrappers in weak-key mode. A group captures epoch, journal token and start sequence. Only same-key newer mutations reject its extra; unrelated changes do not. Clear invalidates the epoch. Capacity overflow or sequence exhaustion rotates the token, clears the journal and resets sequence: old extras fail closed, new groups can prefetch even while an old non-cooperative loader remains. Requested keys keep exact-flight fencing. Rotation never releases C/F reservations; the final group clears the journal. This is a documented bounded-memory difference from Caffeine.

`SynchronousEvictionRaceTests`, `DictionaryViewTests` and `BulkAdversarialTests` cover delivery, absent ABA, pending ownership and prefetch conflicts. Nested loader expiry reads own a synchronous eviction scope; scopes never cross await and ordinary hits allocate none. See [release readiness](../release-readiness.md).
