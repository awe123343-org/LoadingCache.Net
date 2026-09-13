# ADR-0006: Shared-engine maintenance integration

- Status: Accepted; synchronous write transport superseded by [ADR-0013](0013-bounded-write-maintenance.md)
- Date: 2026-09-12
- Scope: shared `CacheEngine` policy maintenance

## Context

The authoritative key/value map and flight ownership live in `CacheEngine`, while the adaptive
policy owns only identity-bearing resident nodes. A resident hit must not acquire the engine or
policy mutation lock. Policy recency and frequency observations therefore need a bounded,
best-effort transport and a coalesced maintenance owner. Publication, removal, clear, and
eviction decisions remain correctness-relevant and are processed synchronously under the engine to
policy lock order.

## Decision

`CacheEngine` creates one `MaintenanceCoordinator` and one bounded `StripedReadBuffer` through its
policy adapter. A ready hit enqueues an exact `EngineEntryToken` and uses a volatile read followed
by a compare-and-exchange only when the maintenance signal is clear. The common hit path does not
take either the engine gate or the policy gate. A full read stripe drops the event and increments
the existing diagnostic counter; this can reduce policy quality but cannot alter freshness,
mapping visibility, generation fencing, or loader ownership.

The coordinator has one drain owner. Its drain callback acquires the engine gate and then the
policy gate, in that order. It drains at most the configured per-pass read budget and returns
whether more work remains. A worker handoff re-checks the bounded transport after clearing the
signal, so a producer racing the handoff cannot strand a queued read. The focused test seam can
pause immediately before that re-check and proves the race with a real scheduled worker.

If the injected scheduler rejects an initial request, the producer performs one coordinator
cleanup invocation. The coordinator invocation has its finite pass budget; the engine does not
wrap it in an unbounded loop. If a lossy read batch remains, the adapter clears its signal so a
later hit or explicit `CleanUp` may retry. Mapping writes do not rely on the lossy transport:
`OnPublish` and `OnRemove` synchronously drain a bounded batch and apply exact node identity under
the policy gate. Eviction callbacks are invoked through the existing engine fencing path, never
from the ordinary hit path.

`Clear` removes all current policy nodes and replaces the policy state. Pending read tokens retain
their old node identity; replay after clear or set therefore sees a retired/null node and cannot
touch or evict a newer generation. Disposal stops the coordinator and disposes the read transport
and policy roots; it does not wait for arbitrary user-owned loader work.

The engine's resident size and weighted-size bounds remain soft during concurrent publication and
maintenance. `CleanUp` performs expiration while holding the engine gate, releases it, and then
drains policy maintenance. It does not await loaders or user callbacks.

## Lock and publication order

The supported order is:

```text
engine gate -> policy gate -> policy data structures
```

The hit path uses an entry freshness/read snapshot and the bounded read transport only. The
policy adapter never becomes authoritative for key/value visibility. Exact entry tokens carry
the entry identity and node pointer; events do not look up by key and cannot act on a replacement
node.

## Alternatives considered

### Central policy lock on every hit

Rejected. It serializes the common resident path and makes a maintenance pause visible to all
callers.

### Unbounded read queue

Rejected. Scan-heavy workloads could retain one event per hit and turn policy bookkeeping into an
unbounded memory consumer.

### Unbounded synchronous fallback after scheduler rejection

Rejected. A continuously productive read stream could make one caller run forever. The coordinator
pass budget bounds each fallback; a later producer or explicit cleanup can retry the remaining
best-effort observations.

### Key-only policy events

Rejected. A delayed read or eviction event could operate on a replacement generation. Exact token
identity and node fencing are required for clear/set/invalidate races.

## Consequences and limits

- Recency/frequency events can be dropped when a stripe is full; hit correctness and expiration do
  not depend on them.
- Read events are FIFO within a stripe, with no global ordering across stripes.
- Scheduler rejection provides bounded progress per caller, not a promise that an unavailable host
  scheduler will eventually run background work.
- `CleanUp` is the explicit quiescence point for checking resident count and policy membership
  invariants; concurrent soft-bound overshoot remains documented behaviour.
- The maintenance hooks used by tests are internal and are not part of the public API.

## Evidence

`tests/LoadingCache.Tests/EngineMaintenanceTests.cs` covers eight focused cases on both target
frameworks: hit progress during a paused policy worker, bounded read drops, rejected-scheduler
fallback, recovery when a full read stripe meets a budgeted rejected fallback, enqueue during a
running worker, enqueue during the signal-clear handoff, stale events after clear/set, and
quiescent size/weight convergence with invariant checks.

Validation status is recorded in [release readiness](../release-readiness.md).
