# ADR-0014: CAS read transport and CLR lifetime

Accepted for the experimental engine, 13 September 2026. Replaces the Channel/Monitor read transport in ADR0005; its scheduling contract remains relevant.

Caffeine v3.2.4 `836b65c0a83e5d1641ded9c6de578654bc04b2e9` `BoundedBuffer`, `StripedBuffer`, `afterRead` and `skipReadBuffer` inform bounded CAS rings, lazy stripes, bounded retry and batch drain. This is source-informed adaptation, not clean-room work or a mechanical JVM memory-model translation.

FULL requests maintenance; bounded-retry FAILED may discard a policy observation. Reliable writes stay separate. Neither can weaken freshness, request statistics, entry/epoch fencing or value publication. Use CLR `Interlocked`/`Volatile`, not Java final-field, VarHandle, layout or opaque-access assumptions. An unpublished reserved head blocks only its stripe; re-arm on immediately consumable work, never spin forever waiting for a producer.

Disposal closes admission and detaches/clears event storage without waiting for paused producers. Producer-held references may last until the call returns, but late publication must self-clear and cannot retain other payloads or revive the cache. Consumer/disposer exclusion, full-fence publication, sequence drop ownership and live-or-retired diagnostics aggregation are detailed in [concurrency](../concurrency.md).

A rejected re-arm after an accepted worker's budget exhausted originally left the read signal set. The regression `AcceptedWorkerThatCannotRearmAllowsTheNextFullHitToRetry` requires bounded reliable fallback to release the read signal under the policy gate, allowing a later FULL to retry. Scheduled/running owners still coalesce; accepted reads remain delayable until full, a write or explicit cleanup.

Cold-start bypass is deliberately narrower than Caffeine: built-in size policy, strong references, no expiry/refresh. Write maintenance initialises the sketch around half capacity; once enabled, shrinking residency does not restore bypass. Resize forces activation; clear starts a new policy generation. One release/acquire adapter flag avoids repeatedly reading policy/sketch objects on hits. Old observations still carry old exact tokens.

The production stripe capacity became 64 rather than 16 on 14 September to amortise .NET ThreadPool handoffs. Per-pass reads remain capped at 256. The metadata/root bound quadruples; active 64-bit ring payload grows by 768 bytes, allocated lazily. A 128-slot candidate regressed cycling reads and was rejected. The 64-slot choice had mixed individual measurements, including a .NET 8 single-reader cycling cost increase in one comparison; later same-binary A/A variation prevented attributing every timing difference to source. Native profiling placed the principal observed SpinWait caller in ThreadPool waiting, not an entry/policy lock; that is not a unique root-cause proof.

Acceptance compares equal configurations and reports accepted/drained/dropped events, not a speedup obtained by losing more work. Controlled saturation, reservation holes, wrap, capacity-one reuse, shutdown, handoff, multi-stripe progress and freshness regressions are required. See [benchmark methodology](../benchmark-methodology.md) and [release readiness](../release-readiness.md) for measured scope; this design does not claim universal speedup or Caffeine parity.
