# Caffeine comparison: source snapshot from 14 September 2026

This historical matrix follows the shared-flight finalisation fix. Runtime/core files were unchanged from original revision `1740fe8`; the measured probe snapshot is identified by `fbb2f7f` and its manifests. These original identifiers are evidence references, not assertions about a later checkout. Current release qualification is separate.

## Environment and coverage

macOS 26.6.2, M1 Pro ARM64, ten cores, 16 GiB; SDK 10.0.100; actual .NET 8.0.31 and 10.0.12; Zulu 25.0.4.1; Caffeine 3.2.4. The MemoryCache adapter uses Microsoft.Extensions.Caching.Memory 10.0.12, not System.Runtime.Caching.

| Experiment | Scope |
| --- | --- |
| Scenarios | 41 scenarios, statistics OFF/ON, three runtimes, two reverse-order rounds; 566 processes including calibration |
| Resident reads | 26 configurations, two rounds, 156 processes and 780 measured samples |
| Writes and drain | 48 configurations per runtime, three runtimes |
| Policy | Six traces, four capacities, three replays: 72 processes and 216 replays |
| Historical MemoryCache comparison | 584 processes, 152 pairs, 2,920 samples; superseded for current comparison by the later matrix linked below |

## Results

Caffeine had lower aggregate cost in all 26 resident-read and all 48 write configurations. LoadingCache/Caffeine cost ratios were:

| Runtime | Read min / median / max | Write min / median / max |
| --- | --- | --- |
| .NET 8 | 1.28 / 2.10 / 4.08 | 1.60 / 6.13 / 15.53 |
| .NET 10 | 1.20 / 1.90 / 3.00 | 1.46 / 4.87 / 9.80 |

For capacity 1,024/statistics OFF, selected ns/op values are:

| Operation | Threads | .NET 8 | .NET 10 | Caffeine |
| --- | ---: | ---: | ---: | ---: |
| Resident read | 1 | 31.113 | 31.642 | 15.809 |
| Resident read | 10 | 4.864 | 3.352 | 1.369 |
| Write | 1 | 573.346 | 487.217 | 100.017 |
| Write | 10 | 399.095 | 367.641 | 229.954 |

Read figures are medians; write figures are means from different harness protocols. The worst .NET 10 read ratio was a capacity-1,024/cyclic/20-reader/statistics-ON case, 6.880 versus 2.290 ns/op. These are inverse-throughput costs, not per-request percentiles.

.NET 10/Caffeine bulk scenario ratios were 4.97 synchronous and 2.89 asynchronous; prefetch ratios were 5.06/2.68. Driver Clear/GetAll work is included, so these are not backend-only costs. GC, callback-dispatch and semantically unequal fan-in scenarios report contracts rather than speedups.

Policy hit-rate differences against Caffeine ranged from −1.327 to +0.444 percentage points on .NET 8 and −2.668 to +0.499 on .NET 10. Selected results:

| Trace / capacity | .NET 8 | .NET 10 | Caffeine |
| --- | ---: | ---: | ---: |
| Cycle / 64 | 7.874% | 6.533% | 9.201% |
| Zipf / 128 | 71.691% | 71.872% | 72.506% |
| Hot-set scan / 128 | 87.789% | 87.829% | 88.073% |
| Changing phase / 512 | 89.222% | 89.258% | 88.931% |

Different admission jitter and maintenance schedules need not select identical victims. These results do not show a universal hit-rate advantage.

## Measurement boundaries

Reads used three warm-up and five measured intervals per process: 500 ms for the Caffeine comparison and 250 ms for the MemoryCache protocol, with reverse ordering. .NET writes used three warm-ups and five measurements of at least 1,048,576 operations. JMH 1.37 used one fork, three one-second warm-ups and three one-second measurements. GC/accounting scope differs between runtimes.

Scenario runs used three warm-ups and five measurements with calibrated equal work, capped at one million cycles. Some Caffeine samples lasted only 12–40 ms; these are too short for small-regression acceptance. No mean was converted to p99. Background cleanup and quiescence are part of the stated operation boundary.

The first MemoryCache probe run failed at process 269 because of harness initialisation; that failure is retained and excluded. The corrected complete run passed. It was not classified as a runtime cache defect.

## Evidence

[evidence-manifest.json](evidence-manifest.json) indexes [evidence.tar.gz](evidence.tar.gz): 6,177 archived files containing commands, logs, JSON, source snapshots, runtime identities and failed/red-green evidence. Compiled binaries are not redistributed. Original archive bytes, including historical prose, are immutable. [verified-summary.json](verified-summary.json) and [validation-summary.json](validation-summary.json) retain checks; read/write/policy/scenario subdirectories contain extracted result tables.

Old one-off run/finish scripts have been removed from the checkout; their historical commands remain archived. For new runs use the maintained [benchmark tools](../../../benchmarks/LoadingCache.Benchmarks/README.md), preserving new output directories and source manifests. Do not attribute this matrix to later runtime changes.

See the [later statistics-matched MemoryCache matrix](../parallel-resident-put-20260916/README.md), [benchmark methodology](../../benchmark-methodology.md) and [release readiness](../../release-readiness.md) for current interpretation and remaining gates.
