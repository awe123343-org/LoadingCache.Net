# LoadingCache resident hit probe

This executable compares bounded LoadingCache and Microsoft MemoryCache resident reads using
the direct comparison contract in
[`docs/benchmarks/caffeine-read-20260913/methodology.md`](../../docs/benchmarks/caffeine-read-20260913/methodology.md).
It uses dedicated OS threads, a two-phase barrier per sample, preallocated
keys, and constrained backend adapters with identical loop/barrier/checksum logic.
The default remains `LoadingCache` with native integer keys; MemoryCache is a probe-only
dependency and does not change the BCL-only core.

Build both supported consumer runtimes:

```sh
dotnet build tools/LoadingCache.HitProbe/LoadingCache.HitProbe.csproj -c Release
```

Run the required smoke case:

```sh
dotnet run --project tools/LoadingCache.HitProbe -c Release -f net10.0 -- \
  --capacity 1024 --residents 1024 --pattern hot --workers 1 \
  --statistics off --warmups 1 --runs 2 --duration-ms 50 \
  --output /tmp/loadingcache-hitprobe.json
```

The command accepts one case at a time. Defaults are capacity 1024, residents
1024, `hot`, one worker, statistics off, three warmups, five measured samples,
and 500 ms per sample. Capacity and residents must be positive powers of two,
with residents no greater than capacity.

The JSON keeps all warmup and measured samples, records runtime/GC and assembly
hash identity, and reports read-only plus cleanup-inclusive throughput. Any
checksum, miss, statistics, thread, or capacity-bound validation failure exits
nonzero.

Use `--backend loadingcache|memorycache`, `--key-mode native|preboxed`, and
`--expiration none|write|access|both`. Preboxed keys are allocated once before
timing, so neither backend allocates a lookup key. Native integer keys measure
the public APIs directly, including boxing required by MemoryCache's object-key API.
Expiration options use one-hour durations. `--statistics on|off` configures
each backend's native counters. Every sample must have zero misses and correct
request counts. MemoryCache has no matching `CleanUp` API, so its cleanup fields
are zero and no `Compact` call is substituted. Null weighted size means the
MemoryCache statistics API did not expose it with statistics disabled.

For LoadingCache investigations, `--diagnostics on` adds sample-boundary snapshots
of maintenance requests/passes, read-buffer reservation/consumption cursors,
ThreadPool counters and the policy seed. It uses reflection outside the timed
worker loop and allocation interval; diagnostics are off by default. This flag only
observes the configured cache; it does not enable internal transport recording.
`readTotalsRecorded` and `readDropsRecorded` identify the actual recording mode in
each snapshot. Enqueued/dequeued totals are `null` when internal success recording
is disabled in an experimental runtime. The retained runtime couples transport
totals and drop recording to statistics; the probe also supports independently
configured totals so rejected experiments remain reproducible. With statistics off,
dropped-event counters are also unavailable (`null`); unrecorded reads
are inferred from the reservation delta and are not classified as full-buffer
drops. Background maintenance can advance between snapshot fields. Keep these
diagnostic runs separate from formal comparisons.

Diagnostics also report each persistent reader's managed thread ID and its
statistics/drop-counter stripe masks and indices. IDs are captured once before
the ready rendezvous; the report is assembled after all samples. Masks come from
the actual cache instances, not a processor-count estimate, and are null when
statistics are disabled. Readers sharing an index may contend on that counter;
this metadata alone does not establish the cause of a throughput difference.

See the [MemoryCache comparison](../../docs/benchmarks/parallel-resident-put-20260916/README.md)
for the matched scenarios and measurement limits. Aggregate wall time per operation
is not a request-latency percentile.
