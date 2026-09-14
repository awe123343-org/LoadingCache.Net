# Initial policy simulation: trace version 2

Measured on 12 September 2026. This is a single-threaded, quiescent hit-rate experiment, not engine throughput, tail latency or production-readiness evidence.

## Configuration

- Actual .NET 8.0.31 on macOS ARM64; built with SDK 10.0.100.
- Seed 419, 100,000 requests per trace, capacities 8/32/128/512.
- Scan, cycle, uniform, Zipf, hot-set scan and changing-phase workloads.
- Local LRU, SLRU, fixed/adaptive W-TinyLFU and BitFaster.Caching 2.6.1, with foreground maintenance after each request.
- Twenty-four scenarios and five policies: 120 runs; hit-plus-miss accounting assertions passed.

A module-only snapshot in `artifacts/simulator/trace-v2-source` isolated policy inputs from concurrent engine development. Its source-manifest.json identifies the inputs. Building Simulator.csproj in Release produced zero warnings/errors; `artifacts/simulator/trace-v2-results/commands.json` records commands and exits alongside raw CSV/JSON. These local artifacts are not qualification of the entire engine at that time.

For a new run, build the maintained tool and select the intended installed runtime:

```sh
dotnet run --project tools/LoadingCache.Simulator -c Release -f net8.0 -- --workload phase-changing --capacity 128 --seed 419 --requests 100000
```

## Selected capacity-128 results

| Workload          |     LRU |    SLRU | Fixed W-TinyLFU | Adaptive W-TinyLFU | BitFaster |
| ----------------- | ------: | ------: | --------------: | -----------------: | --------: |
| Phase-changing    | 36.470% | 50.430% |         55.544% |            55.125% |   54.657% |
| Hot-set scan      | 73.251% | 81.774% |         88.390% |            87.902% |   88.287% |
| Zipf exponent 1.1 | 65.303% | 72.107% |         73.217% |            72.347% |   72.770% |

Adaptive was slightly worse than fixed in all three examples. Window changes were observed, but this does not demonstrate a hit-rate benefit from adaptation. Different policies need not choose identical victims; BitFaster here is a policy comparison, not complete feature equivalence.

Further multi-seed, weighted, public-trace and miss-ratio-curve evidence is needed before making broader claims. Historical version-one results under the simulator tool use different traces and must not be merged with version two. Current engine qualification is reported separately in [release readiness](release-readiness.md).
