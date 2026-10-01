# Performance gaps and next steps (30 September 2026)

This note hands off the MemoryCache and Caffeine performance gaps that remain after [#35](https://github.com/awe123343-org/LoadingCache.Net/pull/35). It records root causes, rejected experiments, proposed directions and their constraints. A second pass of quick spikes ran the same day; see [Second-pass spikes](#second-pass-spikes). The original spike figures are exploratory A/B results, not release-qualification evidence. Direction 1 has since been implemented and qualified in [#38](https://github.com/awe123343-org/LoadingCache.Net/pull/38); its [fixed-commit measurement report](https://github.com/awe123343-org/LoadingCache.Net/blob/811e7d0b5d21ac5ea98bd0bdaba5b0db097c0d24/docs/benchmarks/read-maintenance-20260930/README.md) records the accepted results and limitations. Re-measure other proposals with the [maintained tools](../benchmarks/LoadingCache.Benchmarks/README.md) before claiming a result.

## Status

[#35](https://github.com/awe123343-org/LoadingCache.Net/pull/35) is merged as [`05e592b`](https://github.com/awe123343-org/LoadingCache.Net/commit/05e592b04a5989f5a57abf024ac681c99dec2f53). It makes these changes and keeps the maintenance guarantees documented for that baseline:

- Fixed-expiry hits no longer take a lock or do floating-point clock math on each hit.
- Statistics and read-buffer drop counters no longer false-share with stripe 0.
- Each entry has one fewer lock object.
- The policy adapter no longer keeps a separate node set. Clear, Dispose and the sequence-wrap reset walk the policy deques instead.

On a quiet machine #35 also cut contended TTI hits on .NET 10 (3 alternated rounds, MemoryCache control 1.00):

- 10 threads over 1,024 keys: 14.65 → 5.84 ns.
- 10 threads on one hot key: 213.6 → 100.1 ns.

Direction 1 is handled by #38. The remaining gaps need further measurement, an ADR decision or a redesign:

| Priority | Gap                                                                             | Direction                                                                                                               | Current state                                                                                                                                                                                                                                                                                                                  |
| -------- | ------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1        | Plain hits are bimodal: 13–110 ns per process                                   | Quarter-stripe background threshold; explicit and rejected-scheduler cleanup retain `HasPublished` within their budgets | Handled in [#38](https://github.com/awe123343-org/LoadingCache.Net/pull/38): ADR-0014/0006 revised; [measurements](https://github.com/awe123343-org/LoadingCache.Net/blob/811e7d0b5d21ac5ea98bd0bdaba5b0db097c0d24/docs/benchmarks/read-maintenance-20260930/README.md); existing multithreaded bimodality remains a follow-up |
| 2        | Writes are about 4.1–4.9x slower than Caffeine (estimated; 4.9–6.2x before #35) | Commit `Put` without the engine `_gate`                                                                                 | Moving work out of the lock gave nothing; only the redesign is left                                                                                                                                                                                                                                                            |
| 3        | Expiring hits pay about 10 ns per clock read; TTI hot keys share a CAS          | Opt-in coarse `TimeProvider`; skip tiny access-time advances                                                            | Coarse clock is about 20% faster with no engine change; the touch skip slows spread keys, cause unknown                                                                                                                                                                                                                        |
| 4        | Statistics add about 5.5 ns to a single-thread hit                              | Derive the ring's enqueued count                                                                                        | The spike passes the tests; it needs a clean measurement                                                                                                                                                                                                                                                                       |
| 5        | Per-insert allocation                                                           | Shrink `PolicyNode`                                                                                                     | Node set removed in #35; folding the token into the entry saves only about 16 B                                                                                                                                                                                                                                                |

## Baseline gaps

These figures come from the dated [Caffeine](benchmarks/latest-20260914/README.md) and [MemoryCache](benchmarks/parallel-resident-put-20260916/README.md) matrices. They were measured on an M1 Pro with .NET 10, capacity 1,024 and statistics off unless stated. Read figures are inverse-throughput medians and write figures are means, both in ns/op.

| Case                                 | LoadingCache |        Reference | Ratio |
| ------------------------------------ | -----------: | ---------------: | ----: |
| Hit, object keys, 1 thread           |         27.6 | MemoryCache 22.7 |  1.22 |
| Hit, object keys, statistics on      |         35.4 | MemoryCache 23.4 |  1.52 |
| Hit, expire-after-write              |         44.4 | MemoryCache 22.9 |  1.94 |
| Hit, expire-after-access             |         60.5 | MemoryCache 23.5 |  2.57 |
| Hit, int keys, 1 thread              |         31.6 |    Caffeine 15.8 |  2.00 |
| Hit, one hot key, 10 threads         |         3.35 |    Caffeine 1.37 |  2.45 |
| Same, statistics on                  |         7.84 |    Caffeine 2.68 |  2.92 |
| Write, capacity 1,024, evicting      |          487 |     Caffeine 100 |  4.87 |
| Write, capacity 16,384, non-evicting |          146 |      Caffeine 23 |  6.18 |

## Measurement notes

- Machines:
    - The 30 September A/B runs used an Apple M4 Max (14 cores, 36 GiB, macOS 27.0.1), SDK 10.0.300, and runtimes 8.0.20 and 10.0.8.
    - The dated matrices ran on an M1 Pro, so absolute values from the two do not compare. Ratios within one run do.
- `global.json` pins SDK 10.0.401. A machine without it needs a temporary local override; do not commit the override.
- The throwaway harness is not committed. Its setup:
    - Cache: `ICache<int,int>` or `ICache<object,int>`, capacity 1,024, 1,024 residents, `TryGet(keys[i & 1023])`.
    - Timing: three warm-ups and seven samples of at least 150 ms; the median is reported.
    - Base and patched builds came from a `git archive` snapshot and the working tree. Processes alternated between the two builds.
    - Every process also ran a MemoryCache case as a drift control.
- Plain-hit timings are bimodal per process (see direction 1). To get a usable comparison:
    - Run at least six processes per variant, alternating between variants.
    - Report ranges, not just medians.
    - Keep the MemoryCache control.
    - Record read-buffer accepted and dropped counts with each sample.
- The second-pass spikes ran eight agents benchmarking at once, so their single-thread figures carry 10% or more of noise. The contended TTI check and the node-set A/B ran on a quiet machine.

## Second-pass spikes

> **Status of the spike diffs.** The diffs in this note, inline and in `<details>` blocks, are exploratory. Each was written in minutes to measure a direction. None has had a concurrency review, and reviews of this PR have already found disposal, closure and expiry-contract holes in several of them. Treat them as evidence of where the cost is, not as code to apply. Each direction must be redesigned from its stated invariants, with its own tests for pause points, `Dispose()`/`Clear()` races and documented semantics. The diffs are not a starting patch.

Each spike started from #35 at `bd46756` (before the node-set removal) in its own worktree and had about eight minutes. Each code spike ran the full `LoadingCache.Tests` suite on .NET 10 once and at least three alternated harness rounds (some cases more); the clock spike changed no code. A read-only reviewer checked the drain-once, backlog-threshold and node-set spikes; the TTI touch and clock spikes were not reviewed. Ratios are patched ÷ base.

| Spike                               | Change                                                                        | Result                                                                                                                    | Tests                     | Verdict                                                                                                  |
| ----------------------------------- | ----------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- | ------------------------- | -------------------------------------------------------------------------------------------------------- |
| Drain-once, CleanUp drains to empty | Explicit CleanUp sets a drain flag; background passes do not re-arm for reads | 10 threads: 0.51 (statistics off), 0.73 (statistics on), no overlap; 1 thread: noise                                      | 743/747, 4 ADR-0014 tests | Review concern: a concurrent background pass can clear the flag. Set it under `_policyGate`              |
| Backlog threshold                   | Re-arm only while some stripe is at least half full                           | 1 thread: 0.75–0.82 in three of four cases, no overlap (object keys with statistics: 0.96, overlapping); 10 threads: flat | 744/747, 3 ADR-0014 tests | Review concern: explicit CleanUp uses the same check and can return with drainable reads left            |
| Node-set removal                    | Drop `_nodes`; walk the policy deques                                         | Spike: 47 B fewer per insert, timing noise. Quiet re-run: non-evicting writes 0.90 and 53 B fewer, evicting 0.96          | 747/747                   | Review OK; landed in #35                                                                                 |
| Fold token into entry               | `Entry` derives from `EngineEntryToken`                                       | About 16 B fewer per insert; timing within noise                                                                          | 747/747                   | Low value; the class becomes unsealed and maintenance fields share the entry's cache lines               |
| Shrink the `Put` critical section   | Allocate entry and token, and hash the key, before taking `_gate`             | No visible gain; evicting writes allocate 13% more (unexplained)                                                          | 747/747                   | Rejected: the write timestamp is taken before the gate, so an entry can expire up to one lock wait early |
| Derive the enqueued count           | Remove the per-offer `_enqueued` CAS; compute it from the write counter       | Inconclusive: the drift control moved 1.47x                                                                               | 747/747                   | Re-measure on a quiet machine; `GetStatistics` becomes an O(capacity) scan per ring                      |
| TTI touch skip                      | Skip the access-time CAS when it would advance less than 1 µs                 | 10 threads on one hot key: 94.8 → 6.8 ns. 10 threads over 1,024 keys: 6.9 → 14.0 ns                                       | 747/747                   | Do not land until the spread-key slowdown is explained                                                   |
| Clock options                       | Microbenchmark only                                                           | See direction 3                                                                                                           | —                         | An opt-in coarse `TimeProvider` makes expiring hits about 20% faster                                     |

The spike diffs below were made against `bd46756`. Direction 1's drafts are superseded by #38; retain them only as historical experiments. Other spike diffs need offsets against the #35 baseline `adb755b` (merged as `05e592b`). They are not CSharpier-formatted (the backlog-threshold and enqueued-count diffs fail `dotnet csharpier check`), so run `dotnet csharpier format` on the touched files before building.

## Directions

The same applies here: requirements listed under each direction are the known minimum, not a complete correctness argument.

### 1. Read maintenance chases readers

**Handled in [#38](https://github.com/awe123343-org/LoadingCache.Net/pull/38).** Background read re-arm now requires a published head and at least **one quarter** of a stripe reserved. Explicit `CleanUp` and the synchronous fallback after initial scheduling rejection retain `HasPublished`; the drain request is protected by `_policyGate`. The 256-event/32-pass budgets and full-stripe clear-then-recheck handoff remain intact, with ADR-0014/0006 and regression tests updated.

The direct AC M4 Max/.NET 10 cycling comparison gives single-reader quarter/base cost ratios **0.957 / 0.878** (statistics off/on). Ten-reader results are environment-sensitive and statistics-on is bimodal; use the [fixed-commit measurement report](https://github.com/awe123343-org/LoadingCache.Net/blob/811e7d0b5d21ac5ea98bd0bdaba5b0db097c0d24/docs/benchmarks/read-maintenance-20260930/README.md) for the contemporaneous three-way comparison, fast-mode costs, slow-process frequencies, trace acceptance and net8/hot-key coverage. These results do not establish a gain stable across environments. The experiments and original plan below are retained as history, not instructions to reapply direction 1.

`WindowTinyLfuEnginePolicy.UpdateMaintenanceSignalLocked` (line 667 at `5f05923`) keeps the maintenance worker armed while any read is published. `MaintenanceCoordinator` then runs up to 32 passes (line 304) before rescheduling. Under sustained hits the worker drains the read buffer continuously. As a result, most offers succeed and pay the ring CAS, the publication barrier, the full-fence exchange and cache-line transfers. When the ring is full instead, an offer is dropped cheaply.

- **How often offers are accepted:** from 2.4% to 95% of hits, depending on the process (60 thousand to 4.4 million worker passes per second).
- **Cost follows that ratio:** about 13 ns per hit at 2.4%, 20–48 ns at 45–91%, and 110 ns at 95%.
- This is the likely cause of the process-to-process spread in the dated matrices, including the .NET 10 int-key outlier (43.7 ns versus 24.4 ns for MemoryCache). That link is inferred, not proven.

**Follow-up after direction 1 qualification (outside the current PR):** a separate M4 Max diagnostic with .NET 10, ten readers and statistics enabled reproduced a slow mode in 6/12 base processes and 5/12 half-full processes. In base, the mode medians were 2.375 and 10.708 ns/op (about 4.5x apart). Slow mode correlated with higher accepted-read fractions and more maintenance passes per read. Recorded statistics/drop stripe mappings had no collisions; per-offer read-buffer fallback-ring choices were not traced. This is an existing multithreaded gap under the agreed attribution rule; establish its cause before selecting another scheduling or ring-mapping change. The diagnostic ran on battery and is separate from AC performance qualification. See the direction 1 [evidence report](https://github.com/awe123343-org/LoadingCache.Net/blob/811e7d0b5d21ac5ea98bd0bdaba5b0db097c0d24/docs/benchmarks/read-maintenance-20260930/README.md).

**Original direction, superseded by #38's quarter-stripe threshold:** drain once, as Caffeine does. A background pass that finds only reads leaves the rest in the buffer until the next full stripe, write or explicit cleanup.

**First experiment.** Replacing the body of `UpdateMaintenanceSignalLocked` with `Volatile.Write(ref _maintenanceSignal, 0); return false;` gave these .NET 10 results (hot key, capacity and residents 1,024, statistics off; two rounds per cell, each figure one process's median):

| Case                    | `5f05923`     | Drain-once                 |
| ----------------------- | ------------- | -------------------------- |
| 1 thread, int keys      | 14.45 / 23.93 | 12.80 / 12.54              |
| 1 thread, object keys   | 28.26 / 18.55 | 15.76 / 16.26              |
| 10 threads, object keys | 1.90 / 3.85   | 2.82 / 1.31 (inconclusive) |

Combined with a thread-probe change, 1-thread int-key hits went from 20.5/22.8 to 11.6/10.4 ns, and 10-thread hits from 1.71/1.96 to 1.15/1.30 ns.

That experiment contradicts ADR-0014's "re-arm on immediately consumable work" and fails five `EngineMaintenanceTests`:

| Test                                                                     | Contract it encodes                                                                                           |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `ReadEnqueuedDuringSignalClearHandoffIsNotStranded`                      | A read published while the worker clears its signal is drained by that worker                                 |
| `LastEnqueueDuringAWorkerPassIsDrainedWithoutLostWakeup`                 | A read published during a pass keeps the worker running until the buffer is empty                             |
| `AcceptedWorkerThatCannotRearmAllowsTheNextFullHitToRetry`               | A worker that uses up its passes with reads left tries to reschedule, and releases the signal if rejected     |
| `FullReadStripeCanRetryAfterARejectedBudgetedFallback`                   | When scheduling is rejected, the inline fallback runs the full 32-pass budget while reads remain              |
| `ExplicitCleanupRejectionAllowsAFullReadToRecoverAfterSchedulingResumes` | An explicit cleanup with a rejecting scheduler reports that a fallback is still needed while reads are queued |

**Second pass.** Two production-shaped variants:

- **Drain-once with a CleanUp flag** (+29/−1, three files):
    - Explicit `CleanUp` calls `RequestReadDrain()`. Passes re-arm for reads only while that flag is set, and clear it once reads are empty. Every other pass clears the read signal and reports no more work.
    - 10-thread hits: 3.11 → 1.60 ns (statistics off) and 4.31 → 3.15 ns (statistics on), with no overlap. Single-thread gains were within noise.
    - `ExplicitCleanupRejectionAllowsAFullReadToRecoverAfterSchedulingResumes` now passes. The other four tests above still fail. The rejected-scheduler fallback now runs one pass instead of up to 32.
    - **Race found in review:** `RequestReadDrain` is a plain `Volatile.Write` under the engine `_gate` but not under `_policyGate`. A background pass already inside `CleanUp()` can clear the flag after the request. `MaintenanceCoordinator.CleanUp()` then sees `Running` and returns, and the follow-up pass drains only 256 reads. Taking `_policyGate` inside `RequestReadDrain` fixes it.
- **Backlog threshold** (+38/−2, two files):
    - Re-arm only while some stripe has a published head and is at least half full. A full ring always counts as a backlog, so the clear-then-recheck handoff still covers a producer that saw Full.
    - Single-thread hits: int keys 15.4 → 12.65 ns, int keys with statistics 24.31 → 18.19 ns, object keys 18.62 → 14.04 ns, with no overlap. The drift control was 1.43x slower during the patched runs, so the gain is not a lighter machine. 10-thread hits were flat.
    - Three of the five tests fail, including `ExplicitCleanupRejectionAllowsAFullReadToRecoverAfterSchedulingResumes`.
    - **Contract break found in review:** explicit and fallback `CleanUp` go through the same `UpdateMaintenanceSignalLocked`, so `CleanUp()` can return with drainable reads left. The threshold must apply only to the background re-arm.

**Original implementation plan, superseded by the approved #38 design and qualification:**

- Combine the two: the background re-arm uses drain-once (or the threshold), and explicit and fallback cleanup keep `HasPublished` and drain to empty.
- Set the drain request under `_policyGate`.
- Amend ADR-0014: reads are delayable, and only a full stripe, a write or an explicit cleanup must lead to a drain.
- Replace the failing tests with tests of the new contract. Keep the no-lost-wakeup guarantee for writes.
- Following ADR-0014's acceptance rule, report accepted, drained and dropped counts for equal configurations. Part of the gain could come from draining fewer observations.
- Show single-thread hits at or below 16 ns on the M4 Max over six alternated processes per variant, with no multi-thread regression beyond the drift control.

<details>
<summary>Spike: drain-once with a CleanUp flag</summary>

```diff
--- a/src/LoadingCache/CacheEngine.cs
+++ b/src/LoadingCache/CacheEngine.cs
@@ -1135,6 +1135,8 @@ internal sealed partial class CacheEngine<TKey, TValue> : ILoadingCacheKeyOwner,
             {
                 AdvanceExpirationLocked(GetExpirationNowLocked());
             }
+
+            _policy.RequestReadDrain();
         }

         MaintenanceCleanupResult cleanup = _maintenanceCoordinator.CleanUp();
--- a/src/LoadingCache/ICacheEnginePolicy.cs
+++ b/src/LoadingCache/ICacheEnginePolicy.cs
@@ -44,6 +44,10 @@ internal interface ICacheEnginePolicy
     // next full read buffer must be allowed to request a new coordinator owner.
     void ResetReadMaintenanceSignalAfterFallback() { }

+    // Explicit CleanUp asks maintenance to drain best-effort reads to empty; background passes
+    // otherwise drain them once and leave the rest delayable until the next full read buffer.
+    void RequestReadDrain() { }
+
     void FlushWrites() { }

     WriteBufferStatistics GetWriteBufferStatistics() => default;
--- a/src/LoadingCache/WindowTinyLfuEnginePolicy.cs
+++ b/src/LoadingCache/WindowTinyLfuEnginePolicy.cs
@@ -34,6 +34,7 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl
     private long _nextWriteSequence;
     private int _maintenanceSignal;
     private int _writeMaintenanceSignal;
+    private int _readDrainRequested;
     private bool _evictionPending;
     private bool _skipReadBuffer;

@@ -313,7 +314,7 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl
             Process(maintained);
             _beforeMaintenanceSignalClear?.Invoke();
             bool writesPending = UpdateWriteMaintenanceSignalLocked();
-            return writesRemain || writesPending || UpdateMaintenanceSignalLocked();
+            return writesRemain || writesPending || UpdateReadMaintenanceAfterPassLocked();
         }
     }

@@ -345,6 +346,8 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl
         && Volatile.Read(ref _writeMaintenanceSignal) == 0
         && Interlocked.CompareExchange(ref _writeMaintenanceSignal, 1, 0) == 0;

+    public void RequestReadDrain() => Volatile.Write(ref _readDrainRequested, 1);
+
     public void ResetReadMaintenanceSignalAfterFallback()
     {
         lock (_policyGate)
@@ -664,6 +667,25 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl
         _pendingAccesses.DrainTo(_accessConsumer, budget);
     }

+    private bool UpdateReadMaintenanceAfterPassLocked()
+    {
+        if (Volatile.Read(ref _readDrainRequested) == 0)
+        {
+            // Drain-once: a background pass that found only reads does not re-arm. Remaining reads
+            // stay delayable until the next full read buffer, write, or explicit CleanUp.
+            Volatile.Write(ref _maintenanceSignal, 0);
+            return false;
+        }
+
+        if (UpdateMaintenanceSignalLocked())
+        {
+            return true;
+        }
+
+        Volatile.Write(ref _readDrainRequested, 0);
+        return false;
+    }
+
     private bool UpdateMaintenanceSignalLocked()
     {
         if (_pendingAccesses.HasPublished)
```

</details>

<details>
<summary>Spike: backlog threshold</summary>

```diff
--- a/src/LoadingCache/Maintenance/StripedReadBuffer.cs
+++ b/src/LoadingCache/Maintenance/StripedReadBuffer.cs
@@ -388,6 +388,35 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable
         }
     }

+    /// <summary>Returns whether any ring has a published head and is at least half full.</summary>
+    internal bool HasBacklog
+    {
+        get
+        {
+            if (Volatile.Read(ref _disposed) != 0)
+            {
+                return false;
+            }
+
+            RingBuffer<TEvent>?[]? table = Volatile.Read(ref _table);
+            if (table is null)
+            {
+                return false;
+            }
+
+            for (int index = 0; index < table.Length; index++)
+            {
+                RingBuffer<TEvent>? ring = Volatile.Read(ref table[index]);
+                if (ring is not null && ring.HasBacklog)
+                {
+                    return true;
+                }
+            }
+
+            return false;
+        }
+    }
+
     internal ReadBufferStatistics GetStatistics(Action? afterTableCaptureForTesting = null)
     {
         bool isDisposed = Volatile.Read(ref _disposed) != 0;
@@ -920,6 +949,12 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable
             }
         }

+        // A full ring is always a backlog, so a producer that saw Full is covered by the re-check.
+        internal bool HasBacklog =>
+            HasPublished
+            && unchecked((ulong)(Volatile.Read(ref _writeCounter) - Volatile.Read(ref _readCounter)))
+                >= (ulong)Math.Max(1, _capacity >> 1);
+
         [MethodImpl(MethodImplOptions.AggressiveInlining)]
         internal ReadBufferOfferResult Offer(T value)
         {
--- a/src/LoadingCache/WindowTinyLfuEnginePolicy.cs
+++ b/src/LoadingCache/WindowTinyLfuEnginePolicy.cs
@@ -666,7 +666,8 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl

     private bool UpdateMaintenanceSignalLocked()
     {
-        if (_pendingAccesses.HasPublished)
+        // Re-arm only for a meaningful backlog; a small remainder waits for the next full offer.
+        if (_pendingAccesses.HasBacklog)
         {
             Volatile.Write(ref _maintenanceSignal, 1);
             return true;
@@ -676,7 +677,7 @@ internal sealed class WindowTinyLfuEnginePolicy : ICacheEnginePolicy, IDisposabl
         // this handoff either observes the cleared signal and requests a worker
         // or is observed by this second check and keeps the worker alive.
         Volatile.Write(ref _maintenanceSignal, 0);
-        if (!_pendingAccesses.HasPublished)
+        if (!_pendingAccesses.HasBacklog)
         {
             return false;
         }
```

The reviewer also noted that the ring's `HasBacklog` counts reserved slots, not published ones, so a stripe with a published head and several paused reservations can keep re-arming. That is bounded by the coordinator's pass budget.

</details>

### 2. Writes serialize on the engine gate

Even with a single writer, dotnet-trace showed 45–50% of writer time in `Monitor.Enter_Slowpath`. `PutCore` inserts under `lock (_gate)`, and the background worker holds the same lock in `DrainPolicyMaintenance`. `CompletePolicyWriteBoundary` re-queues the worker whenever its signal clears.

A throwaway test disabled the background request:

- Evicting writes went from 255–276 to 185 ns.
- Non-evicting writes went from 160–183 to 118 ns.

So about 30% of write cost is the writer and the worker handing the lock back and forth.

Three tunings that keep `Put` under `_gate` were rejected:

- **Schedule only once the write buffer is half full:** fails 14 tests (6 of 22 `EngineWriteBufferTests`, 8 of 12 `EngineMaintenanceTests`). Writes below the threshold never get a drain scheduled, which ADR-0013 and ADR-0006 forbid.
- **Let the worker back off with `Monitor.TryEnter`:** passes the tests but is slower:
    - Evicting: 230 → 283 ns.
    - Non-evicting: 164 → 197 ns.
    - 10 threads: 262/280 → 617/553 ns.

    The worker keeps rescheduling itself and still takes the lock between writes.

- **Allocate and hash before taking the gate** (second pass): no measurable gain, and evicting writes allocated 13% more, for reasons not found. It also takes the write timestamp before the lock, so an entry can expire up to one lock wait early.

**Direction:** do what Caffeine does:

- Commit `Put` through the concurrent map without `_gate`.
- Publish policy work to a lock-free bounded write buffer.
- Take the lock only to drain: by `TryEnter`, or synchronously when the buffer is full.

This redesigns the ADR-0013 write path. The redesign must re-establish:

- Replacement ordering.
- Removal notifications.
- Weights.
- Eviction-scope dispatch.
- The parallel resident-put contract in ADR-0012.
- Closure and epoch fencing. Today the second disposed/epoch check inside `_gate` stops a `Put` that passed the first check from publishing into a cache that `Dispose()` or `Clear()` has since closed or reset. An ungated commit needs an atomic closure fence that either refuses the commit or removes its own mapping, such as an admission counter that `Dispose()` and `Clear()` close and drain, or a post-commit epoch recheck. It also needs regression tests for a `Put` paused immediately before its map commit across `Dispose()` and across `Clear()`.

Expect at least a 30% single-thread gain, and more under contention. Even then writes would stay roughly 3x slower than Caffeine, so pair the redesign with fewer objects per insert (direction 5).

### 3. Expiring hits

After #35 a fixed-expiry hit costs one integer compare plus one clock read. On .NET 10 in the same harness, object-key hits cost 24.5 ns with expire-after-write and 29.3 ns with expire-after-access, against 17.9 ns for MemoryCache with sliding expiration. The fast path excludes refresh, variable expiry, weak and soft values, ownership, and 32-bit processes; extending it to any of these is a separate change.

**Clock cost.** A standalone microbenchmark on the M4 Max gave these ns per call (median of two runs, five rounds each; "10 threads" is the per-call cost with ten threads calling at once):

| Clock read                         | .NET 10, 1 thread | .NET 10, 10 threads | .NET 8, 1 thread | .NET 8, 10 threads |
| ---------------------------------- | ----------------: | ------------------: | ---------------: | -----------------: |
| `Stopwatch.GetTimestamp`           |              11.0 |                10.8 |             10.1 |               11.2 |
| `TimeProvider.System.GetTimestamp` |              10.6 |                12.5 |              8.8 |                9.5 |
| `Environment.TickCount64`          |              10.3 |                11.6 |             11.6 |               13.3 |
| `DateTime.UtcNow`                  |              15.9 |                 192 |             15.4 |                246 |
| Cached value refreshed every 1 ms  |              0.33 |                0.27 |             0.26 |               0.30 |

`TickCount64` is not cheaper than `Stopwatch` on macOS, and the engine does not use `DateTime.UtcNow`. Measure Linux x64 before designing around these numbers.

**Coarse clock, no engine change.** `CacheBuilder.TimeProvider()` already accepts a provider that overrides only `GetTimestamp()`:

```csharp
sealed class CoarseTimeProvider : TimeProvider
{
    public static readonly CoarseTimeProvider Instance = new();
    private long _now = Stopwatch.GetTimestamp();

    private CoarseTimeProvider() =>
        new Thread(() =>
        {
            while (true)
            {
                Volatile.Write(ref _now, Stopwatch.GetTimestamp());
                Thread.Sleep(1);
            }
        }) { IsBackground = true }.Start();

    public override long GetTimestamp() => Volatile.Read(ref _now);
}
```

With it, object-key hits on .NET 10 went from 27.4 to 21.9 ns with expire-after-access (no overlap between runs) and from 27.1 to 21.3 ns with expire-after-write (medians; the runs overlapped), and from 34.3 to 27.0 ns (expire-after-access) on .NET 8. Timers (`CreateTimer`) and `TimestampFrequency` stay on the system defaults, so every engine timestamp stays on one clock. The cost is precision, and there is no upper bound on it. Timestamps typically lag by about one refresh interval: about 1–1.3 ms was observed with `Thread.Sleep(1)` on macOS, and Windows defaults to a 15.6 ms timer resolution. `Thread.Sleep` has no maximum wake-up time. CPU starvation, a long GC pause or system suspension can freeze `_now` far longer, and every engine computation that reads this provider is delayed by the same amount. That includes hit expiry checks, load timeouts (`OnFlightTimeout`) and refresh timing. A built-in opt-in must therefore describe the lag as observed, not guaranteed, or confine the coarse timestamp to the lock-free hit check while load timeouts, refresh and write timestamps keep the precise clock.

**TTI touch.** #35 advances access time with a forward-only CAS on every hit; Caffeine uses a plain store. With 10 threads on one hot key, a hit costs about 95–100 ns after #35; before #35, when each hit took the entry lock, it cost about 214 ns. Skipping advances smaller than 1 µs cut the hot-key case to 6.8 ns. But 10 threads over 1,024 keys went from 6.9 to 14.0 ns, consistently, and a field-layout build ruled layout out. Find that cause before landing. Any skip threshold, including 100 ns (`TimestampFrequency / TimeSpan.TicksPerSecond`), changes the documented contract that a successful access advances access time ([semantics](semantics.md)). For example, with a 2 µs TTI and a 1 µs threshold, a hit 0.5 µs after the previous touch can still expire 1.5 µs later. Near a deadline even 100 ns changes the expiry decision, and the exact locked fallback cannot recover a skipped access. Landing a skip therefore requires an explicit, documented coalescing contract covering bounded early expiry, `AgeOf`/`GetExpiresAfter` deviation and runtime duration changes. Re-checking the forward-only concurrency argument is not enough.

<details>
<summary>Spike: TTI touch skip</summary>

```diff
--- a/src/LoadingCache/CacheEngine.cs
+++ b/src/LoadingCache/CacheEngine.cs
@@ -29,6 +29,7 @@ internal sealed partial class CacheEngine<TKey, TValue> : ILoadingCacheKeyOwner,
     // Raw timestamp deltas below these bounds are certainly fresh; -1 means disabled.
     private long _expireAfterWriteFreshBound;
     private long _expireAfterAccessFreshBound;
+    private readonly long _touchSkipDelta;
     private long _refreshAfterWriteTicks;
     private readonly long _loadTimeoutTicks;
     private readonly long _refreshFailureBackoffTicks;
@@ -222,6 +223,8 @@ internal sealed partial class CacheEngine<TKey, TValue> : ILoadingCacheKeyOwner,
         _timeProvider = options.TimeProvider;
         _expireAfterWriteFreshBound = ToFreshTimestampBound(_expireAfterWriteTicks);
         _expireAfterAccessFreshBound = ToFreshTimestampBound(_expireAfterAccessTicks);
+        // ponytail: fixed 1us touch granularity; hot keys otherwise CAS one line per hit.
+        _touchSkipDelta = _timeProvider.TimestampFrequency / 1_000_000;
         // The policy view exposes only features enabled at construction, so a cache without
         // these policies cannot acquire a read-side clock dependency through policy mutation.
         _requiresReadTime =
--- a/src/LoadingCache/EngineExpiration.cs
+++ b/src/LoadingCache/EngineExpiration.cs
@@ -808,13 +808,14 @@ internal sealed partial class CacheEngine<TKey, TValue>
         return duration;
     }

-    private static void TouchWithoutLock(Entry entry, long now)
+    private void TouchWithoutLock(Entry entry, long now)
     {
         // Compare timestamp units before TimeSpan truncation: a negative fraction of a tick
         // must not move access time backwards. Subtraction preserves signed wraparound.
         // Lock-free fixed-expiry hits also advance it, so only a forward CAS publishes.
+        // Advances under _touchSkipDelta are skipped so hot keys do not CAS on every hit.
         long observed = Volatile.Read(ref entry.AccessTimestamp);
-        while (unchecked(now - observed) > 0)
+        while (unchecked(now - observed) > _touchSkipDelta)
         {
             long current = Interlocked.CompareExchange(ref entry.AccessTimestamp, now, observed);
             if (current == observed)
@@ -827,14 +828,14 @@ internal sealed partial class CacheEngine<TKey, TValue>

     // Advances access time for a validated lock-free hit. A writer changes access time only
     // after replacing the publication with a full fence, so retry only while it is unchanged.
-    private static void TouchPublished(
+    private void TouchPublished(
         Entry entry,
         FixedWritePublication? publication,
         long observed,
         long now
     )
     {
-        while (unchecked(now - observed) > 0)
+        while (unchecked(now - observed) > _touchSkipDelta)
         {
             long current = Interlocked.CompareExchange(ref entry.AccessTimestamp, now, observed);
             if (
```

</details>

### 4. Statistics on single-thread hits

After #35, statistics add about 5.5 ns to a single-thread hit:

- The hit counter costs about 2.5 ns: a non-inlined `Environment.CurrentManagedThreadId` call (about 1 ns) plus the saturating CAS.
- The drop counter on a full ring costs about 2 ns.
- The ring's `_enqueued` CAS costs the rest.

With statistics on, 72% of single-thread hits and 98% of multi-thread hits also record a drop.

**Tried and rejected:**

- An `Interlocked.Increment` shortcut below the saturation limit: no gain, and it weakens ADR-0007's no-wrap guarantee when a large add races it.
- A `[ThreadStatic]` cached thread ID: no gain.

Using more stripes than cores would change the ADR-0007 default.

**Second pass:** removing the per-offer `_enqueued` CAS passes all tests. `Enqueued` becomes the write counter's advance past a base, minus reservations in `[head, tail)` that have not published; `ReadBufferBatchTests` requires that a paused reservation is not counted. The A/B was inconclusive because the drift control moved 1.47x, so re-measure it on a quiet machine. `GetStatistics` now scans up to the ring capacity per ring, the same as `Queued` already does. The drop counter was left as is: uncontended it is already one atomic, and an `Interlocked.Increment` variant could wrap past `long.MaxValue`. **Known defect in this spike:** after `Dispose()` detaches `_slots`, the `slots is null` branch skips the scan. A producer paused after advancing `_writeCounter` but before publishing therefore stays counted in `tail - _enqueuedBase`. Disposal does not wait for paused producers, so `GetStatistics()` can report that event as enqueued indefinitely, breaking shutdown accounting such as `DroppedShutdown >= Enqueued`. There is a second window. A producer in `OfferAvailable` can pass the disposed and `_slots` checks, pause before its `_writeCounter` CAS, let `Dispose()` detach the slots, then resume to reserve and publish. A snapshot taken at detachment misses that late publication, and the tail-derived formula counts it before it publishes. A derived-count design must therefore either close the counter at detachment so that late reservations fail (for example, the disposer CASes a closed marker into `_writeCounter`), or track late post-detachment publications explicitly. Add regression tests that pause a producer at both points: after the reservation CAS and before it. With the gain still unmeasured, weigh this extra machinery against keeping the per-offer CAS.

<details>
<summary>Spike: derive the enqueued count</summary>

```diff
--- a/src/LoadingCache/Maintenance/StripedReadBuffer.cs
+++ b/src/LoadingCache/Maintenance/StripedReadBuffer.cs
@@ -820,7 +820,8 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable
         private Action<Action>? _afterShutdownSnapshotForTesting;
         private long _readCounter;
         private long _writeCounter;
-        private long _enqueued;
+        // Every successful reservation publishes, so enqueued is the write counter's advance.
+        private long _enqueuedBase;
         private long _dequeued;
         private long _droppedShutdown;
         private int _forcedCasFailuresForTesting;
@@ -850,10 +851,6 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable
             _slots[0].Value = value;
             Volatile.Write(ref _slots[0].Sequence, 1);
             Volatile.Write(ref _writeCounter, 1);
-            if (_recordStatistics)
-            {
-                SaturatingIncrement(ref _enqueued);
-            }
         }

         internal void SetHooks(Action? beforeReserve, Action? beforePublish)
@@ -882,7 +879,7 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable

             Volatile.Write(ref _readCounter, counter);
             Volatile.Write(ref _writeCounter, counter);
-            Volatile.Write(ref _enqueued, 0);
+            Volatile.Write(ref _enqueuedBase, counter);
             Volatile.Write(ref _dequeued, 0);
             Volatile.Write(ref _droppedShutdown, 0);
         }
@@ -993,10 +990,6 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable

             Volatile.Read(ref _beforePublishForTesting)?.Invoke();
             slot.Value = value;
-            if (_recordStatistics)
-            {
-                SaturatingIncrement(ref _enqueued);
-            }
             // Publication must precede the disposed read with full-fence ordering. A release
             // store followed by an acquire read can miss disposal while the publication is
             // still buffered, after the disposer has already cleared and inspected this slot.
@@ -1136,7 +1129,38 @@ internal sealed class StripedReadBuffer<TEvent> : IDisposable
             }
         }

-        internal long Enqueued => Volatile.Read(ref _enqueued);
+        internal long Enqueued
+        {
+            get
+            {
+                if (!_recordStatistics)
+                {
+                    return 0;
+                }
+
+                // Reservations advance the write counter before their value is stored, so the
+                // bounded range still being published is scanned out, as Queued does.
+                long head = Volatile.Read(ref _readCounter);
+                long tail = Volatile.Read(ref _writeCounter);
+                ulong enqueued = unchecked((ulong)(tail - Volatile.Read(ref _enqueuedBase)));
+                Slot[]? slots = Volatile.Read(ref _slots);
+                if (slots is not null)
+                {
+                    int count = (int)Math.Min(unchecked((ulong)(tail - head)), (ulong)_capacity);
+                    for (int offset = 0; offset < count; offset++)
+                    {
+                        long position = unchecked(head + offset);
+                        ref Slot slot = ref slots[unchecked((int)position) & _mask];
+                        if (Volatile.Read(ref slot.Sequence) == position)
+                        {
+                            enqueued--;
+                        }
+                    }
+                }
+
+                return (long)Math.Min(enqueued, long.MaxValue);
+            }
+        }
         internal long Dequeued => Volatile.Read(ref _dequeued);
         internal long DroppedShutdown => Volatile.Read(ref _droppedShutdown);
```

</details>

### 5. Per-insert allocation

Before #35 an insert allocated about 439 B: five objects (`Entry` about 176 B, its separate lock object 24 B, `EngineEntryToken` 48 B, `PolicyNode` about 136 B with ten link fields plus an owner ID, and the dictionary node about 40 B), plus the policy adapter's node set, whose cost per insert depends on how often it resizes. Garbage collection took about 12% of writer-thread time.

- #35 removed the separate lock object and the node set. In the main harness, where the cache is still filling and the set keeps resizing, removing the set alone made non-evicting writes 0.90x and saved 53 B per insert on a quiet machine.
- Folding `EngineEntryToken` into `Entry` (second pass) passes all tests but saves only about 16 B, because the token's fields, including a self-reference, move into the entry. It also unseals the token class and puts maintenance-thread fields on the entry's cache lines. Not worth it alone.
- **Remaining lead:** shrink `PolicyNode`. Take it on together with the direction 2 redesign, which changes what the policy needs per node.

### 6. Dropped plain-hit micro changes

Three micro changes were tried and dropped:

- Skipping the thread probe when there is a single stripe.
- Writing the drain cursor once per pass.
- Skipping redundant signal stores.

They made .NET 8 and object-key hits faster, but made .NET 10 multi-thread hits with statistics off 1.32–1.37x slower. Revisit them after direction 1, because their effect depends on how often the worker runs.

Before revisiting these micro changes, investigate the existing ten-reader/statistics-ON process bimodality tracked in direction 1: base slow mode occurred in 6/12 diagnostic processes, with about 4.5x the fast-mode cost and higher accepted-read/maintenance-pass rates. Separate the modes and preserve their frequencies in comparisons; a single process or a pooled median can conceal this gap. Keep this investigation outside the quarter-full maintenance PR.

Not tried:

- Padding ring slots onto separate cache lines.
- `Unsafe.As` for the reference-value cast in `Entry.ReadStrongValueAtomic`.
- Removing the `EntryStore` indirection.

## Follow-up checklist

- [x] Handle direction 1 in [#38](https://github.com/awe123343-org/LoadingCache.Net/pull/38): quarter-stripe threshold, synchronized drain request, revised ADR-0014/0006, preserved cleanup/fallback boundaries and regression tests; [measurements](https://github.com/awe123343-org/LoadingCache.Net/blob/811e7d0b5d21ac5ea98bd0bdaba5b0db097c0d24/docs/benchmarks/read-maintenance-20260930/README.md).
- [ ] Investigate the existing ten-reader/statistics-ON bimodality recorded under directions 1 and 6 before selecting another read-transport change.
- [ ] Ship an opt-in coarse `TimeProvider`, or document the snippet, with its precision contract.
- [ ] Explain the spread-key slowdown from the TTI touch skip, and adopt an explicit access-coalescing contract (bounded early expiry, `AgeOf`/`GetExpiresAfter` deviation, runtime duration changes) before landing it.
- [ ] Fix the derived enqueued count's `Dispose()` accounting races, both a reservation paused before publication and a producer paused before its reservation CAS, then re-measure on a quiet machine; drop the direction if the fix costs more than it saves.
- [ ] Design the lock-free `Put` (direction 2) together with a smaller `PolicyNode`.
- [ ] After each direction, re-run the MemoryCache and Caffeine matrices on one documented machine with the maintained tools.
- [ ] Keep `LoadingCache.Tests`, `LoadingCache.StressTests`, the DI tests and `ConsumerSmoke` green on .NET 8 and .NET 10.
