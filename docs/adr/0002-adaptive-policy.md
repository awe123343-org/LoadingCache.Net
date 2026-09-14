# ADR-0002: Adaptive Window TinyLFU

Accepted, 12 September 2026. Verification is tracked in [release readiness](../release-readiness.md).

## Basis and alternatives

Use Caffeine v3.2.4 (`836b65c0a83e5d1641ded9c6de578654bc04b2e9`) for the three-segment policy, reactive hill climbing and sketch/admission. Incorporate tiny-cache tuning from master `d885a95eee51fdfe13f450fd9cba80f58f7e0def`; do not claim parity with that revision's larger-cache density/probe/audit/anchor controller. Adaptive policy remains a 1.0 requirement.

BitFaster v2.6.1 already has adaptive partitioning and a TimerWheel. An independent engine seam was selected for tiny capacities, long weight units, runtime maximum changes, exact-generation fencing and four population APIs. Its readonly integer partition and internal node/time coupling cannot be removed by a thin wrapper. This is a maintainability/contract choice, not a claim that .NET lacked these algorithms. See [research](../research.md).

## Geometry and adaptation

M is a positive long maximum. Size entries weigh one; weighted entries retain the validated non-negative weight of their version. Call the weigher outside engine locks. Zero-weight entries remain subject to expiry, invalidation, collection and the independent resident-count cap.

Initially `W=ceil(M/100)`, `P=floor(4*(M-W)/5)` and probation receives the remainder. Use integer quotient/remainder or UInt128, not overflowing multiplication or a rounded double-to-long cast near `long.MaxValue`. M=1 has one window; M=2 has window=1, probation=1, protected=0. M≥3 uses the three-segment geometry.

| Quantity                    | Selected rule                                                                                                                                                                                                           |
| --------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Policy observation          | Accepted resident access is a hit; new candidate insertion is a miss. Failed loads/coalesced waiters do not create extra insertions. Public request counters are separate; lossy reads make policy samples approximate. |
| Entry estimate N            | Size maximum or weighted resident-node estimate, never maximum weight bytes. Zero-weight hits must not imply weight demand.                                                                                             |
| Sketch sample S             | `max(10,min(10*N,int.MaxValue))`; capped table storage and its approximation require testing.                                                                                                                           |
| Initial/restart magnitude B | `max(M/16.0,2.0)`                                                                                                                                                                                                       |
| Initial direction           | Grow for M≤512, shrink otherwise.                                                                                                                                                                                       |
| Decay D                     | 0.995 for M≤512, otherwise 0.98.                                                                                                                                                                                        |
| Sample period L             | For M≤512, `floor(S*clamp((M/16.0)/max(abs(step),(M/16.0)/4),1,4))`, using ratio=1 for a zero denominator; otherwise S.                                                                                                 |
| Restart                     | Absolute hit-rate change ≥0.05, including equality.                                                                                                                                                                     |

```text
rate = hits / (double)(hits + misses)
delta = rate - previousRate
amount = delta >= 0 ? step : -step
adjustment = truncateTowardZero(amount)
step = abs(delta) >= 0.05 ? copySign(B, amount) : D * amount
previousRate = rate
hits = misses = 0
```

Keep fractional steps; truncate only the integer adjustment. A permanent one-unit minimum would prevent convergence. Long sample counters need checked/saturating arithmetic and reset at the threshold. Insufficient samples do not choose a new direction, but existing residual transfers can progress.

Growth quota is `min(adjustment,P)`; shrink quota is `min(-adjustment,W-1)`. Move whole nodes from probation (then protected where allowed) into the window, or window LRU into probation; demote protected overflow first. Charge full weight, never split nodes. A too-heavy head may permit the other upstream-defined head, not an unbounded search. Roll back unapplied capacity changes and retain signed residual work, including across maintenance without a fresh sample.

A transfer visits at most 1,000 nodes and the caller's remaining maintenance budget, including demotions and zero-weight scans. Preserve incomplete work and re-arm; repeated quiescent cleanup converges. Engine removal always checks the exact node/generation.

Runtime resize scales the existing split then clamps legal geometry, rather than resetting every increase to 1%. Reinitialise step/sample and discard residuals from the old geometry. This is a local contract, not exact upstream resize equivalence.

## Sketch and admission

Four packed 4-bit saturating counters estimate frequency by their minimum, capped at 15. Accept a candidate with greater frequency than the victim. Otherwise accept with probability 1/128 only when candidate frequency≥6; reject low-frequency ties. Use a per-cache BCL random source and reproducible internal test seeds; neither public fixed seeds nor low LCG bits provide HashDoS protection.

Hash arithmetic explicitly wraps with `unchecked`. Age every nibble by halving, correct observation count for odd counters and clamp it non-negative. When shrinking the sample, set count to `min(count,S-1)` to retain ageing progress and avoid premature or unreachable reset under repeated weighted estimates.

## Ownership, evidence and attribution

One maintenance owner mutates identity/epoch-bearing nodes, deques, sketch and bounded snapshots. Policy never owns the authoritative map, removes by key or calls loaders, listeners, weighers, expiry callbacks or user comparers. Read observations may be lost; lifecycle/weight writes may not. Ordinary hits do not acquire the global eviction lock.

Required regressions cover capacities 1/2/3 and 512/513; equal/positive/negative rate changes and the 0.05 boundary; fractional decay/residual progress; zero/heavy/overflowing weights and resize; saturation/ageing/retracking/collisions/forced jitter branches; queue membership and exact identity. Simulator traces compare LRU, SLRU, fixed/adaptive W-TinyLFU and BitFaster on stationary, scan and changing-hotset workloads. No universal hit-rate superiority is claimed.

Adapted Caffeine source retains Apache-2.0 attribution and modification notices. Record paths, revisions and tests in the [upstream map](../upstream-map.md); this is not clean-room work or a complete legal/source audit.
