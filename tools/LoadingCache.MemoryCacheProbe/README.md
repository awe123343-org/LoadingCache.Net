# MemoryCache workload comparison

Release-only probes for resident replacement, engine hit ratio under capacity
pressure, and native async factory coalescing. Both engines use the same
preallocated reference keys and values. The core has no new dependency.

```sh
dotnet build tools/LoadingCache.MemoryCacheProbe -c Release
dotnet run --project tools/LoadingCache.MemoryCacheProbe -c Release -f net10.0 -- \
  --backend memorycache --scenario replacement --capacity 1024 --workers 4 \
  --operations 32768 --batches 4 --value-mode changed --statistics off \
  --warmups 3 --runs 5 --output /tmp/memorycache-replacement.json
```

`--backend loadingcache|memorycache` selects one engine per process. Replacement
uses half the capacity and disjoint worker partitions; every final value is
checked by identity after timing. `--value-mode same|changed` distinguishes a
same-reference replacement from alternating two preallocated values. Allocation
and GC deltas cover the complete process between release and finish barriers;
the additional per-thread allocation counter can include GC allocation-context
padding and is not an exact object-allocation oracle.

`--minimum-warmup-ms 1000` keeps running complete warmup scenarios for at least
one second as well as satisfying `--warmups`. This includes setup/disposal and
gives default tiered JIT/PGO time to initialize when individual write batches are
short. Every extra warmup is retained in JSON; measured sample count stays fixed.

`--scenario trace --trace-kind scan|uniform|zipf|hotset-scan|phase|cycle` uses a
fixed seed and precomputed trace, or `--trace-file` reads integer JSON/newline input.
It reports observed engine hit ratio, including each implementation's admission
and asynchronous maintenance. It is not a pure policy oracle or a same-work
throughput comparison. `--operations` is the request count; `--workers` does not
parallelize trace replay.

`--scenario fanin --workers 100` creates 100 overlapping same-key requests behind
a controlled loader gate. LoadingCache must invoke one loader and native
MemoryCache must invoke 100 factories. All callers must receive the correct value.
This measures backend-work suppression, not latency or a universal speedup.

All JSON includes warmups, samples, runtime/package/assembly/source identities,
configuration, and validation outcomes. See the [comparison report](../../docs/benchmarks/parallel-resident-put-20260916/README.md)
for frozen inputs, exact commands, raw data, and limits.
