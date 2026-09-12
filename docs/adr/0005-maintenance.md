# ADR-0005: Bounded maintenance transports and coalesced scheduling

- Status: Accepted; read transport superseded by [ADR0014](0014-cas-read-transport.md)
- Date: 2026-09-12
- Scope: bounded maintenance primitives

## Context

The policy engine needs to move frequent read-side recency/frequency observations without putting
the normal hit path behind a global policy lock. Those observations may be lost because they only
affect policy quality. Lifecycle and mapping writes have a different contract: their exact entry
identity and generation must be preserved and a full transport must be reported to the owner so it
can drain cooperatively or use a synchronous fallback.

The maintenance worker also needs one owner for policy replay. A producer must be able to request
work while a worker is queued or running without creating one task per event, losing a wake-up, or
recursively invoking an inline test scheduler. Scheduler failure cannot leave correctness-relevant
work silently stranded.

## Decision

### Read-side transport

`StripedReadBuffer<TEvent>` uses one BCL `Channel<TEvent>` per fixed power-of-two stripe. Each
channel is created with:

- `BoundedChannelFullMode.Wait`, so `TryWrite` returns `false` when the bounded channel is full;
- `SingleReader = false`, because the maintenance owner and the shutdown drain can consume a
  completed channel concurrently;
- `SingleWriter = false`, because cache hit producers are concurrent;
- `AllowSynchronousContinuations = false`, avoiding producer continuations running inline.

The producer chooses `Environment.CurrentManagedThreadId & (stripeCount - 1)`. `TryEnqueue` is
fully synchronous: it performs no wait, `Task`, `CancellationTokenSource`, or closure allocation.
Counters are sharded alongside the channels to avoid a global diagnostic hot spot. `Queued` is a
bounded point-in-time sum of `ChannelReader.Count`, rather than a producer-side reservation that
could transiently exceed physical channel contents. A failed write increments either `DroppedFull`
or `DroppedShutdown`.

`TryRead` is owned by one maintenance consumer for normal policy replay. It scans at most every
stripe once, beginning at a round-robin cursor, and advances the cursor after a successful read (and
after an empty scan). The channels also permit the shutdown drain to consume concurrently after
completion. FIFO ordering is guaranteed within each stripe while the normal consumer owns it. There
is no global ordering guarantee for concurrent producers. The payload is transported by exact
object/value identity; the buffer never looks up a key or substitutes a newer generation.

The read transport is intentionally lossy. A full stripe drops the newest event and records the
drop. It is suitable for approximate recency/frequency policy observations only. Freshness checks,
mapping visibility, entry retirement, generation fencing, and statistics that require exactness
must not depend on successful read-event publication.

### Maintenance coordination

`MaintenanceCoordinator` owns only worker scheduling state. The state machine is:

```text
Idle -> Scheduled -> Running -> Idle
                    |    ^
                    v    |
              RunningRequired
```

`Disposed` is terminal. `Request` changes `Idle` to `Scheduled` and asks the injected scheduler
for one worker. Requests while `Scheduled` coalesce. Requests while `Running` or
`RunningRequired` set/retain `RunningRequired`; the current worker performs another bounded drain
pass before returning to `Idle`. Each invocation has a finite, configurable pass budget (32 by
default); after the budget it re-arms one worker instead of allowing a continuously productive
queue to monopolize a worker or make `CleanUp` unbounded.

The `Func<bool>` drain delegate is invoked outside the coordinator lock. It performs one bounded
pass and returns whether more work remains. The coordinator does not hold a policy lock, invoke a
user callback, or perform `await` while changing its state. `CleanUp` can claim a scheduled callback
or run bounded passes synchronously while idle. Its result reports `MoreWork` and
`FallbackRequired`, so the engine can continue synchronously when a re-arm is rejected. A stale
queued callback then observes `Running`/`Idle` and returns without creating a second owner.

The default scheduler uses `ThreadPool.UnsafeQueueUserWorkItem`, so the request's
`ExecutionContext` is not captured. Test or host schedulers can be injected; they should enqueue
the callback and return rather than invoke it inline. Inline invocation is detected and converted to
an explicit synchronous fallback requirement instead of recursive re-entry. A rejected or throwing
scheduler returns `MaintenanceRequestResult.ScheduleRejected` for an initial request, or sets
`FallbackRequired` on the bounded cleanup result after a re-arm. The caller must call `CleanUp` or
perform an equivalent synchronous fallback before treating a correctness-relevant write as
complete. A request after disposal returns `Disposed`.

Drain exceptions are caught and counted in `MaintenanceStatistics`; they do not escape a background
worker or strand the state machine. Disposal stops new requests and does not wait for an already
running drain, because the drain may contain non-cooperative user-owned work. The active drain sees
the terminal state after its current bounded pass and exits.

## Alternatives considered

### Unbounded `ConcurrentQueue<T>`

Rejected. It hides producer pressure as retained memory and cannot provide a meaningful backlog
bound under scan-heavy workloads.

### `Channel` with `DropWrite` or `DropOldest`

Rejected for the read buffer implementation. Some channel drop modes can report a successful write
while discarding an item, making admission and drop diagnostics ambiguous. Explicit `TryWrite` with
`FullMode.Wait` makes the drop visible to the producer.

### One `Task.Run` or timer per event

Rejected. It creates unbounded scheduler pressure and per-hit allocations, and it complicates
shutdown ownership. One coalesced worker handles a bounded pass at a time.

### Recursive rescheduling for `moreWork`

Rejected. An inline scheduler would turn a long backlog into stack growth. The coordinator caps
each invocation, detects inline re-entry, and reports a synchronous fallback requirement.

## Consequences and limits

- Read-event drops reduce policy precision but cannot be used as a freshness or mapping signal.
- Capacity is bounded per stripe; a hot thread can fill its stripe while another stripe is empty.
  The fixed stripe count/capacity must therefore be selected from workload measurements.
- The read buffer guarantees per-stripe FIFO only. Cross-stripe order is unspecified.
- `MaintenanceCoordinator` is a scheduling primitive, not the policy owner. The engine must supply
  the gate-to-policy drain and ensure lifecycle writes use reliable fallback when a schedule is
  rejected.
- `Dispose` does not force a non-cooperative drain delegate to stop. It prevents future requests and
  prevents publication after the owner observes the terminal state.
- Listener/removal notification queues remain a separate bounded transport with their own drop and
  shutdown contract; this ADR does not make read-policy drops acceptable for notifications.

## Provenance

The implementation is an independent .NET composition of the public `System.Threading.Channels`
BCL API and `ThreadPool.UnsafeQueueUserWorkItem`. No Caffeine, Guava, or BitFaster source file was
copied or translated. Caffeine's public design documentation informed the separation between
authoritative cache state and amortized maintenance, but this ADR does not claim source adaptation
or upstream compatibility.

## Evidence

`tests/LoadingCache.Tests/MaintenanceTests.cs` covers constructor validation, explicit full-channel
drops, exact identity/FIFO, concurrent bounded publication, shutdown drops, concurrent consumer and
dispose draining, request coalescing, running-to-required handoff, stale callback cleanup, scheduler
rejection/throw, bounded cleanup with continuous work, inline scheduler stack safety, observed drain
faults, disposal, and default scheduler `ExecutionContext` isolation (17 focused cases).

Validation status is recorded in [release readiness](../release-readiness.md).
