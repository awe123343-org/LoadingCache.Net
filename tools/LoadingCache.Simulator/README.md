# Policy simulator

Replay a deterministic synthetic trace and emit JSON:

```sh
dotnet run --project tools/LoadingCache.Simulator -c Release -- --seed 419 --capacity 128 --requests 100000 --workload phase-changing
```

The executable targets .NET 8. If .NET 8 is only installed in the repository's validation directory, build with the development SDK and run the DLL using `artifacts/runtime8/dotnet`.

Workloads: `scan` (unique sequential keys), `cycle` (512 keys), `uniform` (2,048 keys), `zipf` (2,048 keys, exponent 1.1), `hotset-scan` (128 hot keys with an interleaved unique scan), and `phase-changing` (hot set, recency cycle, shifted hot set, pure scan). Generate a miss-ratio curve by repeating the same trace seed and workload at several capacities.

Baselines are LRU, SLRU, fixed Window TinyLFU, adaptive Window TinyLFU, and BitFaster.Caching 2.6.1 ConcurrentLfu. BitFaster uses a foreground scheduler and `DoMaintenance` after every request; the local policy also runs maintenance after each request. This compares quiescent policy decisions, not concurrent engine throughput or buffering overhead. BitFaster is explicitly unavailable below capacity 3; the other policies still run for tiny capacities.

Trace version 2 records workload, capacity, seed, requests, actual hits/misses, and sampled window sizes. Adaptive jitter is seeded for the local policy; BitFaster has its own internal policy decisions and is not a per-key victim oracle. The earlier JSON files in `results/` came from trace version 1: their historical `with-scan` name describes uniformly sampled cold keys, not a sequential scan. Keep them as history; do not compare their numbers to version 2 as if the inputs matched.

These results measure hit rate only. No policy is assumed to win every trace. Full engine integration, public trace provenance, retention measurements, concurrent latency, and backend work suppression remain separate release gates.
