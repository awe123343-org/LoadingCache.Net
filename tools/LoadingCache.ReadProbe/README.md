# Read transport probe

`LoadingCache.ReadProbe` compares the bounded read transport through one common harness. The
timed workload is the same for every transport build; `--transport` is metadata only and never
selects a different measurement path.

The probe uses preallocated sealed reference events. Concurrent scenarios use persistent
producer/consumer threads, explicit gates, and a bounded join watchdog; the single-producer
scenario runs inline to avoid measuring context switches. Event generation, thread creation,
random work, and progress output are outside the timed interval. Each scenario repeats its
preallocated event batch until
`--target-operations` is reached (the default is 1,048,576 attempts), so a 16,384-event batch does
not turn the result into a thread-startup measurement.

The single-producer scenario uses fixed 16-event offer/drain chunks inline on one thread. This
keeps the transport bounded while measuring offer/drain work without context-switch overhead and
ensures accepted events are consumed on every cycle. Concurrent scenarios drain until all
producers finish, then perform two additional empty drains after observing zero remaining
producers. That final confirmation closes the race where a last published event becomes visible
between an empty read and the producer-count check.

`--drain-mode try-read` is the common baseline available in both the old queue and CAS-ring
snapshots. `--drain-mode batch` binds the optional internal `DrainTo(Action<TEvent>, int)` method
once during setup and measures that path without reflection in the hot loop. A snapshot without
`DrainTo` rejects batch mode clearly; it never silently changes the requested mode. This keeps the
same source usable with both snapshots while making the two measurement paths explicit.

Each sample reports attempts, accepted events, drained events, policy drops split into full and
contention/failure drops, shutdown drops, unaccounted rejections, acceptance fraction, writer and
process allocation, elapsed and cleanup-drain time, GC deltas, identity checksums, and final
bounded transport counters. A sample fails if accepted identities are not drained exactly, the
final queue is non-empty, or authoritative counters are inconsistent. A post-dispose offer supplies
an explicit shutdown-drop observation. Optional `DroppedFailed` statistics are read when available;
old snapshots without that counter remain comparable.

The default pure-transport configuration is four stripes with capacity 256 per stripe. The default
concurrency matrix is 1, 4, 10, and 20 producers. Production configurations can be recorded by
passing their stripe settings explicitly; no production setting is silently substituted.

Build a focused smoke run on an installed runtime:

```sh
dotnet build tools/LoadingCache.ReadProbe/LoadingCache.ReadProbe.csproj \
  -c Release -f net10.0 --no-restore

dotnet run --project tools/LoadingCache.ReadProbe \
  -c Release -f net10.0 --no-build -- \
  --transport oldQueue --mode all --drain-mode try-read \
  --stripe-count 4 --stripe-capacity 256 \
  --operations-per-producer 64 --target-operations 4096 \
  --runs 1 --warmups 0 --no-progress \
  --output /private/tmp/loadingcache-readprobe-smoke.json
```

Run the exact same command in the CAS-ring snapshot, changing only `--transport newRing` and the
source/build directory. To compare the optional batch path, use `--drain-mode batch` in the
CAS-ring snapshot; retain a separate label from the `try-read` baseline. For formal runs, retain
the raw JSON, runtime, source assembly hash, exact configuration, and command line together.

The probe has no public-library API dependency beyond the test-only `InternalsVisibleTo` entry for
`LoadingCache.ReadProbe`; it uses the existing internal `StripedReadBuffer<TEvent>` contract. The
CAS-ring implementation must preserve the common contract in the frozen comparison snapshot,
including bounded admission, identity-preserving reads, statistics, and idempotent disposal.

## Controlled cache-hit paths

An independent opt-in mode runs the real `Cache<object,int>.TryGet` path with a manually
controlled maintenance scheduler. The normal transport CLI above is unchanged:

```sh
dotnet tools/LoadingCache.ReadProbe/bin/Release/net10.0/LoadingCache.ReadProbe.dll \
  --cache-path accepted on write /private/tmp/cache-accepted-on-write.json
```

The exact positional syntax is `--cache-path accepted|full on|off
none|write|access|both output.json`; missing, extra, or invalid arguments fail immediately.
Each process uses 1,024 resident preboxed integer keys, a single reader and one fixed read stripe
of capacity 64, `TimeProvider.System`, and one-hour durations for enabled expiry policies. Expiration timers
are disabled; the normal read-time clock, freshness checks, and publication fences remain intact.

Each sample contains 8,192 chunks of 64 hits (524,288 operations). Accepted mode explicitly
cleans up and runs queued callbacks before each chunk, verifies an empty ring, times only the
64-hit loop, then verifies 64 queued events. Full mode first fills 64 slots and performs one
extra hit to confirm Full and a queued maintenance callback. It leaves that callback pending
throughout the sample and verifies the ring remains full after every chunk. Preparation reads
are outside the reported operation/counter interval. All samples end with a drain and exact
1,024-resident/weighted-size checks. No callbacks execute concurrently with readers.

Warmup repeats complete samples for at least one second of wall time; every warmup record and its
actual operation count is retained. Five measured samples follow. Statistics ON verifies exact
Hits, Enqueued, and Full deltas, zero misses and unexpected drops. OFF verifies counters remain
zero and uses the controlled queue transitions to establish the path fractions. Every sample
also verifies the cyclic value checksum. Failures produce no success report.

JSON records runtime, architecture, tiering environment, cache/harness assembly SHA-256 hashes,
arguments, configuration, before/after snapshots, path fractions, read ticks/ns per operation,
whole-sample wall ticks, cleanup ticks, current-thread/process allocation and GC deltas. Read
timing includes loop/key indexing, checksum and miss counting; snapshots, cleanup, and validation
are outside it. Allocation covers the whole sample, including that untimed work, and excludes
JSON serialisation. Hashes identify binaries; retain the corresponding source manifest separately.

This is a path-isolation diagnostic. Forced maintenance cadence and short timed chunks do not
represent production throughput, tail latency/p99, or a MemoryCache comparison. Compare frozen
builds with identical configuration and keep normal engine measurements separate.
