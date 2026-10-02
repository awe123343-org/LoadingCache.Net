# Write-path options (2 October 2026)

This note picks up where the [30 September handoff](performance-gaps-20260930.md) stopped. Reads are now ahead of MemoryCache in every measured case, and expiring reads are ahead of Caffeine. Writes and cold loads remain well behind both. This note records:

- where the write cost goes, measured;
- why Caffeine's write path is cheaper;
- which contracts constrain a redesign;
- the candidate directions, ranked, with the reviewer's assessment of each.

It is an exploration brief, not an approved plan. Each direction still needs its own design review, deterministic race tests and qualified A/B measurement before anything lands.

## Status

Since #35 these have landed:

| PR  | Change                                                                | Effect                                                                                    |
| --- | --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| #38 | Read re-arm at quarter stripe                                         | Single-reader hits 0.88–0.96x                                                             |
| #39 | TTI touch coalescing (≤ 1 ms)                                         | 10-reader hot-key TTI 99.9 → 2.6 ns                                                       |
| #40 | Timer node stored on `Entry`                                          | Fixed-expiry Put 0.69–0.77x at capacity 16,384                                            |
| #41 | Opt-in coarse TTL checks                                              | Single-thread TTL hit about 0.84x when enabled                                            |
| #43 | Sync load allocations (G1)                                            | Cold sync load −27%, −152 B/load                                                          |
| #44 | Write signals deferred below a backlog threshold, no expiry only (H1) | 1-writer no-expiry Put −10%, cold sync load −22%; 10 writers and TTL unchanged within ±2% |
| #46 | Bulk chain sibling scan removed (B1)                                  | 10,240-key `GetAll` 21.8 µs → 0.67 µs per key                                             |

Open: #45 (bulk allocation, G2), plus two uncommitted candidates: L1 (single policy boundary per cold load) and statistics-on hits (derived ring enqueue count, direction 4 of the earlier note).

## Current gaps

These figures come from a fresh three-way run on 1 October (Apple M4 Max, .NET 10.0.8, Zulu JDK 25 with Caffeine 3.3.0 under JMH 1.37, three processes per cell). They include #39–#41 but not the later PRs. Results across runtimes are directional only. Units are ns/op.

| Case                                 | LoadingCache | MemoryCache | Caffeine | LC/MC | LC/Caffeine |
| ------------------------------------ | -----------: | ----------: | -------: | ----: | ----------: |
| Hit, no expiry, object key, 1 thread |         12.6 |        18.2 |     10.5 |  0.69 |        1.20 |
| Same, statistics on                  |         16.9 |        18.8 |     11.4 |  0.90 |        1.49 |
| Hit, no expiry, 10 threads, hot key  |         1.47 |        19.7 |     0.65 |  0.07 |        2.25 |
| Hit, TTL, 1 thread                   |         20.7 |        18.5 |     37.8 |  1.12 |        0.55 |
| Hit, TTI, 1 thread                   |         21.7 |        18.6 |     36.4 |  1.17 |        0.60 |
| Put, no expiry, 1 writer, 16,384     |          308 |         127 |     34.4 |  2.43 |        8.96 |
| Put, TTL, 1 writer, 16,384           |          406 |         127 |     67.9 |  3.20 |        5.98 |
| Put, no expiry, 10 writers, 16,384   |          312 |         141 |      290 |  2.21 |        1.08 |
| Put, TTL, 10 writers, 16,384         |          401 |         136 |      274 |  2.95 |        1.46 |

After #44 the 1-writer no-expiry Put is about 260 ns. A cold sync load (miss then load) is about 520–560 ns, against 142 ns for MemoryCache's `GetOrCreate`. Caffeine's loading path was not measured.

MemoryCache numbers under contention or eviction are not equal-semantics numbers. Its size-limit CAS gives up after bounded retries and rejects the entry, and full caches compact rather than admit.

**The gap that matters is the uncontended single-writer path.** At 10 writers Caffeine also falls to about 290 ns, roughly where we are. Its advantage is a fast path that never touches a lock when there is no contention.

## Where the time goes

### Put ablation (P)

Measured on main `6876d50`: no expiry, absent keys, capacity 16,384, six processes per cell, MemoryCache control. These are counterfactual removals; the effects are not additive.

| Variant                      | 1 writer ns |   B | 10 writers ns |   B |
| ---------------------------- | ----------: | --: | ------------: | --: |
| Baseline                     |         285 | 522 |           298 | 495 |
| No policy publish            |         108 | 373 |           151 | 346 |
| No maintenance signal        |         190 | 519 |           259 | 492 |
| Entry and token preallocated |         266 | 298 |           270 | 271 |
| `ConcurrentDictionary` only  |          62 | 149 |            78 | 133 |
| MemoryCache                  |         124 |   — |           135 |   — |

A variant with no engine gate at all could not be measured: the background drain still mutates the policy, and every process failed with token-count underflow or a null dereference.

### Thread profile (P2)

- **1 writer:** 28.5% of writer CPU is Monitor acquisition and spin on `_gate`. On the maintenance thread, 56% is thread-pool idle spin, 19% is Monitor and only about 5% is `PolicyNode` work.
- **10 writers:** summed writer-to-writer wait on `_gate` is about 1,388 ns per insert, against 354 ns writer-to-maintenance. 39% of writer CPU is Monitor acquisition.

### Cold sync load ablation

Measured on main `1619eeb`, before H1. Values are MemoryCache-adjusted savings, 1 worker / 10 workers.

| Removal                                    | Saved ns  |
| ------------------------------------------ | --------- |
| Policy publish and downstream admission    | 314 / 247 |
| Maintenance signal (largely taken by #44)  | 227 / 111 |
| Flight coordination (direct factory → Put) | 227 / 358 |
| Load chain (`AsyncLocal` cycle detection)  | 58 / 65   |
| Entry and token preallocation              | ≈ 0       |

A cold `Get` publishes one policy Add, at completion. It also runs two notification scopes, which means four `CompletePolicyWriteBoundary` calls; a Put runs one scope and two calls.

### Reading the evidence

1. **Allocation is not the bottleneck for time.** Preallocating the Entry and token saves about 19 ns. E1 and E2 cut 17–72 B per insert, but neither produced a stable time gain, and both produced hit-path drift (E1 +17%, E2 +6% on single-thread expiring hits).
2. **The cost is the policy handoff under the engine gate.** That covers the enqueue under `_gate`, scheduling or waking the drain, and the drain then holding the same `_gate` while writers wait. Removing the policy publish cut the 1-writer Put from 285 to 108 ns, and the dictionary floor is 62 ns.
3. **The .NET dictionary itself costs about 40 ns more than Java's.** This is an estimate: the Java side was not measured. `ConcurrentDictionary.TryAdd` takes a striped lock and allocates a node on every insert. `ConcurrentHashMap.putIfAbsent` into an empty bin is a single CAS.

## Why Caffeine's write is cheap

| Aspect                  | LoadingCache                                                                                                    | Caffeine                                                                            |
| ----------------------- | --------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| Lock on uncontended Put | Engine `_gate` for the whole Put: allocate Entry and token, commit to the map, enqueue the policy write, signal | None: `ConcurrentHashMap` bin CAS, lock-free MPSC write buffer, CAS on drain status |
| Drain lock              | The same `_gate`                                                                                                | A separate eviction lock. Writers only `tryLock` it when the buffer is full         |
| Objects per insert      | 4: `Entry` 184 B, `EngineEntryToken` 48 B, `PolicyNode` 136 B, dictionary node 48 B                             | 2: `Node` (policy links embedded) and the `ConcurrentHashMap` node                  |
| Policy layering         | Standalone `WindowTinyLfuPolicy<T>` with its own `PolicyNode<T>`, wrapped by an engine adapter and token        | Policy fields generated into the node classes                                       |
| Size bound              | Deterministic: N+B at a completed boundary (ADR-0013)                                                           | Documented as approximate; may transiently exceed the maximum                       |
| `Clear` / shutdown      | `Clear` resets map, epoch and policy under the gate; disposal closes admission and revokes publication          | `invalidateAll` iterates and removes; concurrent writes may survive it              |
| Eviction listener       | Runs synchronously outside locks, before the owning step returns                                                | Runs on the executor                                                                |

Most of the difference is that the gate buys stronger guarantees. Removing the gate means either relaxing those guarantees on a fast path, or preserving them with a gate-free protocol.

## Constraints a redesign must respect

These come from [semantics](semantics.md), [concurrency](concurrency.md) and the ADRs. Each direction below states which of them it touches.

- **Disposal closure.** After disposal, nothing may publish. A Put paused before its map commit, across `Dispose()`, must not land in the map. This is a hard requirement; see direction 2 of the earlier note.
- **`Clear` and epochs.** Old work and events must not affect the new epoch. The semantics already allow a `Set` overlapping `Clear` to commit _into the new epoch_ ("an ongoing explicit mutation, not a resurrected loader"). So a gate-free Put does not need to be fenced out of the new epoch. It must be either fully in the new epoch (map and policy) or not at all. This is weaker than the requirement the earlier F design assumed.
- **Bounds.** The resident count is at most N+B at a completed boundary, and transient publication is at most N+B+1 (ADR-0013). Weighted caches have their own weight ledger.
- **Single-flight and exact ownership.** Single-flight, exact-entry publication and the refresh rollback revisions are unchanged by any write-path change.
- **Synchronous eviction listeners.** They run before the owning step returns, which pins eviction work to the writer when a listener is configured.
- **Hit path.** No direction may add work to a hit. Every candidate keeps the existing hit gate: none/TTL/TTI single-thread at six processes with a MemoryCache control. Layout changes have repeatedly produced 3–17% hit drift, so measure early.

## Directions

Gains below are planning estimates unless stated as measured.

### W1. Single-node layout for the built-in policy

Merge `Entry`, `EngineEntryToken` and `PolicyNode` into one object for engines using the built-in W-TinyLFU policy. This gives two objects per insert, like Caffeine.

- **What changes:** the deque links, queue tag, weight and sequence fields move into `Entry`. The adapter stops allocating the token and node. The standalone `WindowTinyLfuPolicy<T>` stays for the simulator and tests, or becomes generic over a node interface that `Entry` implements.
- **Expected:** about −184 B per insert. Time gain is uncertain: the preallocation ablation suggests only about 20 ns, but fewer cache lines touched by the drain may help the handoff.
- **Risks:**
    - Entry layout drift on the hit path (seen in E1/E2).
    - The token and node identity protocols must be preserved: `PendingPolicyWrites`, `LastPolicyWriteSequence`, and `Node == null` meaning detached.
    - Ownership must stay split: the engine owns some fields, the policy others, under different gates (relevant to W3).
    - The 2026-09-30 spike that folded only the token saved about 16 B and was not worth it alone.
- **Assessment:** valuable only as a foundation for W3 or W4. Do not ship it alone without a measured time gain.

### W2. L1 and the statistics candidate (small, ready)

- **L1:** one policy completion boundary per notification scope; Dispatch and Dispose deduplicated. The design is done and builds on #44, which has merged. Expected about 10–40 ns per cold load at one worker.
- **Statistics on:** derived ring enqueue count. Measured gains:
    - 1 writer, statistics off: −6%; on: −4%.
    - 10 threads, statistics off: −38%.

    Pending the owner's decision: 10 threads with statistics on is bimodal. The slow-mode share is unchanged (5/24 versus 6/24), but within-mode medians are 3–4% slower.

- **Assessment:** finish both. Cheap, isolated, low risk.

### W3. Split the policy drain off the engine gate (F2)

Design: `artifacts/perf-policy-gate-split/design.md` (local, not committed).

- The write buffer becomes a single-producer ring published under `_gate`: no CAS, release tail.
- The drain runs under its own policy gate P, with lock order P → E → `entry.Sync`.
- The map commit, flights and epochs stay under E.
- Eviction rechecks epoch, exact entry and sequence under E before removing.
- `Clear` and `Dispose` take P → E.

**Expected.** Codex estimated 10–40 ns at 1 writer and 10–50 ns at 10 writers, after H1. The estimate is low because writers still hold E for the commit, and part of the remaining cost is cache-line transfer rather than lock waiting.

**Risks.** Every E → P path changes (`Clear`, `Dispose`, flush and snapshots, shrink, rejected-scheduler fallback, maintenance). The lock order is inverted. H1's live policy-weight read becomes a snapshot plus outstanding credits.

**Assessment.** Large change for a modest gain on its own. It becomes worthwhile together with W4. Shelved until a cheap ceiling probe (see "First steps") shows the drain/gate interaction really is the remaining cost.

### W4. Gate-free Put for an eligible configuration (fast path)

This is the Caffeine-shaped option. For engines with:

- the built-in policy, strong references and unit weight;
- no listeners, owned values or bulk capability;
- the default scheduler;

a Put of an absent key never takes `_gate`:

1. Allocate the Entry (W1 layout), timestamp it, and mark it publication-pending.
2. Commit it with a single `TryAdd` into the captured map instance.
3. Enqueue its policy Add into a lock-free bounded MPSC write buffer, and request the drain with a CAS on the drain status.
4. Only when the buffer is full does the writer `tryLock` P and help drain, as Caffeine does.

The disposal and `Clear` fence follows the existing F design (`artifacts/perf-gate-free-put/design.md`). Use the map instance as the generation: `Clear` swaps in a new dictionary, and `Dispose` swaps in null. A writer paused before `TryAdd` can then only insert into the detached old dictionary, and it removes its own candidate exactly. Because a `Set` may legally commit into the new epoch, the fence only needs "map and policy agree". It does not need a full block.

- **Expected:** this is the only direction aimed at the 1-writer gap. A plausible target is 100–150 ns per Put (dictionary about 62 ns, plus Entry allocation, plus one CAS enqueue). That is unmeasured.
- **Prerequisites:**
    - W1, or at least a token-free policy record.
    - The W3 split, so that the drain no longer needs E for policy state.
    - Every competing writer (load completion, refresh, dictionary views, replacement) must use conditional `TryAdd`/`TryUpdate` with exact-entry retirement. Otherwise a gated writer can overwrite a fast insert.
- **Risks:** the whole lifecycle changes. This is weeks of work, not days. It needs proofs and deterministic tests for every pause point: before and after `TryAdd`, enqueue reserved but not published, and a scheduler claimant that stalls. It also carries the retention and resident-bound allowance of N+P+Q.
- **Assessment:** the highest ceiling and the highest cost. Explore it only after the ceiling probe below confirms the target. The owner must approve the contract position first: keep every current guarantee, or document an approximate bound on the fast path the way Caffeine does.

### W5. Own concurrent hash table

Replace `ConcurrentDictionary` with an engine-owned table in which the `Entry` is the bucket node: open addressing or chained, with a CAS insert into an empty bin.

- **Expected:** removes the dictionary node (48 B) and the striped lock on insert. Up to about 40 ns per Put, and fewer pointer hops on the hit path.
- **Risks:**
    - Resize correctness under concurrent readers, and tombstones.
    - Comparer and weak-key support.
    - Every existing `_entries` use, including `IsCurrent`, enumeration for `Clear` and snapshots, and the `AsDictionary()` views.
    - The hit path depends directly on it.
- **Assessment:** an independent track with a clear ceiling (the 62 ns floor against Java's estimated 15–25 ns). Prototype the table in isolation first, against `ConcurrentDictionary`, for TryAdd, TryGet and resize.

### W6. Things not to repeat

Shelved with evidence. Branches and receipts are under `artifacts/`.

| Direction                                    | Why shelved                                                                                                  |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| E1: reuse the `DrainEvictions` list          | Saved 17–25 B on evicting writes only; single-thread plain hits +17% (layout, unexplained)                   |
| E2: lazy cold `Entry` state                  | Every loaded entry needs the cold state, so +32 B per load; single-thread TTL/TTI hits +6%                   |
| E3: seqlock to replace publication snapshots | No hop to save: the initial publication is already null; complex bridge needed for the non-blocking contract |
| H1 adjustments 1–3                           | Extra gate acquisition made 10-thread load +12%; moving bookkeeping did not fix the 10-writer drain convoy   |
| Coarse clock as default                      | Unbounded staleness, a per-process ticker thread, about 3 ns gain                                            |
| `[ThreadStatic]` load chain                  | Misses a nested `Get` after `Task.Run`: deadlock instead of `LoadingCacheReentrancyException`                |

## First steps (cheap probes before any design)

1. **Ceiling probe for W3 and W4.** In a throwaway worktree, never committed, make a deliberately unsafe single-configuration Put:
    - no `_gate`;
    - `TryAdd`;
    - a lock-free MPSC enqueue of a preallocated policy record;
    - a drain thread that owns the policy under its own lock.

    Measure 1 and 10 writers against the P baseline. If 1-writer Put does not get below about 160 ns, W3 and W4 are not worth their cost. Report the remaining split.

2. **Table probe for W5.** A standalone microbenchmark of a minimal CAS-insert table against `ConcurrentDictionary`: TryAdd of absent keys, TryGet of hits, and resize while reading, at 1 and 10 threads. If TryAdd saves less than 25 ns, drop W5.
3. **Layout probe for W1.** Add the merged policy fields to `Entry` behind no behaviour change: allocated but unused. Run only the hit gate, to see whether the larger Entry alone causes the drift seen in E1/E2.
4. Finish W2 (L1 and the statistics decision).

Only after these probes should a direction get a full design. Send every design for review before code.

## Working rules

- Measure: at least six processes per variant, interleaved, with a MemoryCache control and the shared benchmark lock.
- Check AC power with the native command (`system_profiler SPPowerDataType`); a sandboxed `pmset` has misreported it.
- Pin the runtime with `--fx-version`.
- Report ranges and paired MemoryCache-adjusted ratios. Never drop outliers.
- A stable regression of more than 2% (paired, adjusted) on any 10-thread or TTL row blocks shipping, even when ranges overlap. The owner does not accept multithreaded regressions.
- One PR per direction, one commit, amended. Receipts go to review before the commit.
- Probes and ablations are never committed.
