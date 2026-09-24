# ADR-0013: Reliable bounded policy writes

Accepted, 13 September 2026.

Separate authoritative publication from policy replay with a 256-entry BCL `Queue<T>`. Full producers cooperatively drain and retry; a successful write never disappears. This follows Caffeine v3.2.4's `afterWrite`/cooperative-maintenance concepts, not JCTools queue source or its throughput claims.

Engine, policy adapter and write buffer share one monitor for commit/enqueue/drain/clear. Explicit locked methods avoid repeated monitor acquisition; debug assertions check ownership. Standalone tests retain self-locking entry points, and a built-in adapter with a different gate is rejected. Ordinary hits use entry freshness and lossy read transport; published queue counts use volatile reads. No user loader/weigher/expiry/listener/scheduler executes inside these locks.

Each struct event carries exact token, operation, immutable weight and sequence. Update the token's latest sequence before enqueue; ignore superseded replay. Set the node's applied sequence before a weight update can evict it. Eviction removes authority only when applied sequence is still latest, so a pending update retains admission rights. Pending count lives on tokens and applied sequence on nodes, with no per-write auxiliary dictionaries. Clear/disposal also remove pending counts.

A separate CAS write signal coalesces scheduling outside the gate; an already-set signal takes only a volatile read. Never share it with lossy-read retry state. Drain updates the signal under the common gate, excluding producers; a successor can then schedule. Budgets are 256 writes/256 reads per pass and 32 passes per invocation. Initial/re-arm rejection or maintenance failure flushes a bounded reliable batch through the engine gate; progress cannot depend on a later read.

Replay deferred additions/updates as a batch, then evict. Validate each weight overflow with wide arithmetic before aggregation. Oversized entries are flushed synchronously; standalone policy methods retain immediate semantics. Internal eviction callbacks are non-reentrant; if one throws, finish retired-list bookkeeping before rethrowing the first error. Resize commits count/weight maxima and processes both removal lists before surfacing callback failure.

Without a synchronous eviction listener, policy replay may be deferred. With one, flush/capture inside the original mutation gate, then dispatch in the original operation's scope outside locks before completion. Flushing only after unlocking could let another worker steal delivery ownership. Cleanup, resize and eviction snapshots process available writes. Numerical overshoot/work bounds are in the [resource model](../resource-model.md).

Queue plus the existing gate avoids unnecessary CAS machinery, pooling or async waiters. A bounded Channel is viable but unused subscription/waiting machinery is unnecessary here; no benchmark superiority over Channel is inferred. Replacing the queue alone cannot remove the engine gate. Fully synchronous policy updates remain fallbacks, not the main batching design.

Pinned reference: [BoundedLocalCache](https://github.com/ben-manes/caffeine/blob/836b65c0a83e5d1641ded9c6de578654bc04b2e9/caffeine/src/main/java/com/github/benmanes/caffeine/cache/BoundedLocalCache.java) and its tests for full drain, discarded events, exceptions and drain status. Selected source/tests were read, not upstream-tested here. Local TUnit tests (`WriteBufferTests`, `EngineWriteBufferTests`, `EngineMaintenanceTests`, refresh/listener regressions) are independently written. Mature references do not replace local race/platform/performance evidence.
