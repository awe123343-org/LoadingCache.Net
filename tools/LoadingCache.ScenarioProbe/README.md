# Matched feature scenarios

This executable complements the resident-read and drained-write probes. It shares
its scenarios, key/value shapes, input sequence and assertions with
`../../benchmarks/jvm/src/main/kotlin/ScenarioProbe.kt`.

```sh
dotnet build tools/LoadingCache.ScenarioProbe/LoadingCache.ScenarioProbe.csproj -c Release -f net8.0 --artifacts-path /private/tmp/scenario-build -m:1
artifacts/runtime8/dotnet /private/tmp/scenario-build/bin/LoadingCache.ScenarioProbe/release_net8.0/LoadingCache.ScenarioProbe.dll --scenario async-gated-fanin --cycles 1024 --capacity 1024 --fan-in 16 --warmups 2 --runs 3 --statistics on --output /private/tmp/scenario.json
```

Use `-f net10.0`, `release_net10.0` and the actual .NET 10 host for the other target.
`--scenario all` is for bounded smoke, not isolated performance measurement.
Every option takes a value; unknown, duplicate and invalid options fail.

`--scenario trace-policy --trace-file trace.json --cycles N` replays a JSON
array of nonnegative integer keys, with exactly N items and keys <= 1,000,000.
Each request performs a lookup, inserts on miss and drains maintenance. Compare
hit rate, not throughput. The report includes the ordered input file SHA-256.

See [the methodology](../../docs/benchmarks/caffeine-comprehensive-20260913/methodology.md)
for the complete scenario table, operation counts, contract-only cases, scheduler
and allocation caveats. All timings are closed-loop driver-inclusive wall time;
there are no per-request latency percentiles.
