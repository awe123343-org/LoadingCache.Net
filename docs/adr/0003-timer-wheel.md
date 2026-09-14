# ADR-0003: Hierarchical TimerWheel

Accepted, 12 September 2026. The wheel provides physical cleanup; authoritative lookup freshness never depends on its progress.

Use Caffeine v3.2.4 (`836b65c0a83e5d1641ded9c6de578654bc04b2e9`) and selected master fixes (`d885a95eee51fdfe13f450fd9cba80f58f7e0def`) as the TimerWheel/source-test basis. BitFaster 2.6.1 is a conceptual cross-check, not copied implementation. Attribution is recorded in the [upstream map](../upstream-map.md).

Five levels have 64, 64, 32, 4 and 1 buckets, with spans 1, 64, 4,096, 131,072 and 524,288 normalised ticks. Intrusive circular links support constant-time scheduling/descheduling/rescheduling. Ownership belongs to one engine maintenance owner; no node may belong to another wheel or multiple buckets.

Use unchecked unsigned timestamp arithmetic with a signed half-range comparison; supported monotonic distance is at most `long.MaxValue`. Bound large jumps and work rather than visiting every elapsed tick. The fixed pending-bucket queue holds at most 165 buckets and deduplicates work with bits.

`Advance(now,budget)` caps visited nodes and preserves an immutable link-sequence frontier. A mutable tail is unsafe: expiry/rescheduling can detach it and strand or endlessly revisit the remainder. Detach before returning due values for processing outside wheel callbacks. Nested removal/rescheduling and cross-wheel misuse must not corrupt membership. Preserve unfinished work for another pass; a prompt scheduler re-arms only the next necessary wake-up, not one timer per entry.

`GetNextDelay` is a coarse scheduling hint, not permission to return an expired value. Clear and stale events use exact node/owner identity. Normalisation and duration conversion must handle zero origins, large durations, boundaries and wrap without wall-clock assumptions.

TimerWheel tests cover every level boundary, counter wrap, large forward jumps, reschedule/detach/nested removal, removed-tail regressions, cross-wheel ownership, capped work/re-arm and fixed-seed oracle comparisons. Standalone tests do not replace integrated expiry/scheduler/retention testing; see [release readiness](../release-readiness.md).
