# Matching harness contract

Each program runs ONE case and accepts identical flags:
`--capacity N --residents N --pattern hot|cycle --workers N --statistics on|off`
`--warmups N --runs N --duration-ms N --output PATH`.

Capacity and residents must be positive powers of two, residents <= capacity.
Default execution: capacity1024, residents1024, hot, workers1, stats off, warmups3, runs5,
duration500ms. Smoke uses warmups1/runs2/duration50ms.

Use a size-bounded strong manual cache, default maintenance scheduler, no expiry/refresh,
listeners, custom comparer or loader. Fill keys0..residents-1 with value key+1; call CleanUp
before timing and verify exact count. Cache survives all warmup/measured iterations.
Preallocate keys outside timing (.NET int[]; Java Integer[] so no per-hit boxing).

Create persistent dedicated OS/platform reader threads, not task-pool/virtual threads.
A two-phase barrier with main coordinates every iteration. Main sets a shared absolute
deadline immediately before releasing the start barrier; worker reads it after the barrier.
Main measures wall duration from immediately before start release through finish barrier.
Each worker repeatedly performs chunks of 1024 lookups and checks monotonic time once per chunk.
Every iteration starts at index worker*17, mask = pattern hot ? 0 : residents-1.
Lookup key = keys[index & mask], increment index; .NET TryGet, Java getIfPresent.
Accumulate returned values in a local long checksum, count misses locally. Publish operation
count/checksum/misses once before finish barrier, never a shared per-hit counter.

After timing, verify each checksum analytically from starting index, operations, mask and
values key+1; misses MUST be zero. All workers must do nonzero operations. Stats on hit delta
must equal total operations and miss delta zero; stats off remains zero. CleanUp outside timing,
record cleanup duration and exact count/weighted size bound. Report both read-only elapsed and
elapsed+cleanup throughput. Each iteration records per-worker operations to expose imbalance.
Barrier waits/thread shutdown must have finite watchdogs and propagate worker failures.
No forced GC, JIT/tiering overrides, per-hit clocks, console output or random key generation.

Emit JSON schemaVersion1 with runtime/OS/architecture/processors/GC information, case flags,
sample index/warmup flag, wallSeconds, cleanupSeconds, operations, checksum, misses,
workersOperations, hitsDelta, missesDelta, residentCount, weightedSize, boundsPassed.
Record .NET process managed allocation delta via GC.GetTotalAllocatedBytes. Java may use
ThreadMXBean live-thread allocation deltas with its coverage clearly labelled; allocation
measurements with different scopes are not used to claim allocation parity. Include GC counts.
Any validation failure must make the process fail; raw warmup samples are retained.

Cases: capacity1024 × (residents1/hot, residents1024/hot, residents1024/cycle) × workers1/4/10/20
× stats off/on. Additionally capacity16384/residents16384/cycle/workers1 or10/stats off.
Two fresh-process rounds per case, alternating .NET/Java then Java/.NET order. Parent records
exact argv/cwd/exit codes, source/assembly/JAR hashes and compares runtime results by case.

The metric is closed-loop aggregate throughput, not request latency or a service SLA.
ns/op = elapsed/total operations is reciprocal throughput under concurrency, not p50/p99.
Default CLR and JVM scheduling/GC, generic specialisation/boxing and read-buffer loss differ;
the comparison includes these real implementation costs. Cache size/stats/trace are aligned.

These probes measure the entire public resident lookup, not just the ring transport. A changed
drop rate affects approximation quality and maintenance cost, so offered-event throughput from
the previous transport-only probe is not substituted for successful cache lookups here.
