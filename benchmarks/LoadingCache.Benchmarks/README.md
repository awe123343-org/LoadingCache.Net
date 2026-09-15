# Lookup benchmark

This executable measures resident lookup overhead. It does not measure eviction
quality, backend suppression, concurrent throughput or service tail latency.
The dictionary is an unbounded lower-bound reference. The locked LRU,
IMemoryCache and BitFaster cases do not claim equivalent loading semantics.
`Statistics` enables or disables both LoadingCache statistics and MemoryCache
`TrackStatistics`. Compare matching settings; the dictionary, locked LRU and
BitFaster cases do not implement the same statistics contract.

Build and run without a debugger:

```sh
dotnet run --project benchmarks/LoadingCache.Benchmarks/LoadingCache.Benchmarks.csproj -c Release -f net10.0 -- --filter '*LookupBenchmarks*' --exporters json --artifacts artifacts/benchmarks/lookup
```

For a short functional smoke, add `--job Dry`. A Dry job has too few samples for
a performance conclusion. CSV is exported by default. Keep the JSON, CSV and
environment log with the exact source revision or a source hash manifest.

The first local .NET 10 ARM64 Dry run executed all 28 parameter combinations.
It ran while other engineering checks were active and could not obtain high
process priority or all sysctl metadata. Its numbers are not a performance
baseline. A stable-run baseline and the other three measurement categories remain
required by [the methodology](../../docs/benchmark-methodology.md).

## Write maintenance

`WriteMaintenanceBenchmarks` measures pre-generated bounded writes at one and four
workers, then calls `CleanUp` until the policy weighted size, resident count and
maintenance backlog are stable. This includes the post-batch drain, so a buffered
implementation cannot claim an improvement from enqueue cost while leaving policy
work pending. Use the same command and source snapshot for before/after comparisons:

```sh
dotnet run --project benchmarks/LoadingCache.Benchmarks/LoadingCache.Benchmarks.csproj \
  -c Release -f net10.0 --no-restore -- \
  --filter '*WriteMaintenanceBenchmarks*' --job short \
  --exporters json csv --artifacts artifacts/benchmarks/q02-write-buffer/after-net10
```

The workload is a throughput smoke/baseline, not a service-tail-latency claim. Keep
the raw JSON, CSV, environment log, source revision and runtime details together;
later cross-runtime write results are documented in
[the Caffeine report](../../docs/benchmarks/latest-20260914/README.md).
The historical `before-net10` artifact was contaminated and is not a valid baseline.
