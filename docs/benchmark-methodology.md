# Benchmark methodology and evidence

Measurements use Release builds without a debugger. BenchmarkDotNet, JMH and custom harness results are labelled separately. A Dry run proves that a harness executes; it does not establish a speedup or zero allocation. See [BenchmarkDotNet guidance](https://benchmarkdotnet.org/articles/guides/good-practices.html) and the separate [V1 acceptance budgets](v1-performance-gates.md).

## Retained comparisons

| Evidence                                                                    | Scope and result                                                                                                                                                                                                                                |
| --------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [MemoryCache matrix](benchmarks/parallel-resident-put-20260916/README.md)   | 584 processes, 152 backend pairs, 2,920 measured samples. Of 100 main comparisons, LoadingCache was cheaper in both rounds in 68, dearer in 27 and mixed in five. Reads: 39/26/3; replacements: 29/1/2. Single-reader expiration remains a gap. |
| [Caffeine matrix](benchmarks/latest-20260914/README.md)                     | Earlier fixed source: 41 scenarios, 26 resident-read configurations, 48 write/drain configurations and policy traces. Caffeine was cheaper in all read and write configurations; these are aggregate costs, not request percentiles.            |
| [Guava resident reads](benchmarks/guava-read-20260913/README.md)            | 26 cases, 208 processes, 1,664 samples. Default segmented capacity and non-default concurrency-level-one results are separated. No inference about writes or loading.                                                                           |
| [Policy simulation](simulator-results.md)                                   | Trace v2, 24 scenarios and 120 policy runs, including cases where adaptive W-TinyLFU loses to fixed W-TinyLFU.                                                                                                                                  |
| [Service precision diagnostic](v1-service-precision-diagnostic-20260918.md) | Resident A/A controls cannot resolve the original 5% budget; that gate remains Inconclusive.                                                                                                                                                    |

Statistics OFF is compared with OFF, ON with ON; the default is OFF. In the MemoryCache matrix, the OFF partition is 36/12/2 and ON is 32/15/3. These partition the same 100 comparisons and must not be added to the read/replacement totals. Each report binds its own source, binaries and commands. A new result must not silently replace a failed or unfavourable earlier one.

## Separate experiments

| Experiment              | Controls                                                                                                                    | Outputs                                                                                  |
| ----------------------- | --------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| Lookup cost             | Resident hot set; ConcurrentDictionary as a functionality lower bound, locked LRU, IMemoryCache, BitFaster and LoadingCache | ns/op, allocation and GC; token and statistics configurations                            |
| Concurrency and latency | One, several, CPU-count and oversubscribed readers; one-hot, uniform, pre-generated Zipf and mixed mutation                 | Throughput and directly sampled p50/p95/p99, queue delay, capacity overshoot and backlog |
| Policy quality          | Identical traces through LRU, SLRU, fixed/adaptive W-TinyLFU and BitFaster                                                  | Hit ratio, admission, ageing and miss-ratio curves; not identical victims                |
| Backend suppression     | Same-key fan-in, distinct misses, completed/gated loaders, TTL churn and refresh                                            | Backend invocations, request accounting, rejection, latency and retained resources       |

Pre-generate keys and request data, consume results and keep setup out of the measured section. Scan, changing hot sets, skew, expiration and failed refresh exercise different mechanisms. Network mocks, random-number generation and console output must not dominate a purported lookup measurement. HybridCache/FusionCache are relevant only where population semantics are comparable.

Aggregate wall time divided by completed operations is inverse throughput, not a request-latency distribution. Closed-loop latency includes its stated driver costs but omits independent arrival pressure and suffers coordinated omission. An open-loop harness must use scheduled arrival times, include queue delay and count every rejected or dropped request. Never derive p99 from a mean or present a closed-loop saturated run as an overload SLA.

Measure retained bytes separately from allocations: use isolated processes, fixed warm-up/quiescence, an empty-cache baseline and root/heap inspection. Record capacity, key/value types and active loaders. Weak-reference checks must isolate JIT lifetimes; one forced collection is insufficient evidence of bounded retention.

## Rejected experiments and unresolved causes

The development history contains unsuccessful candidates; they are not installed merely because a microbenchmark or disassembly looked better. Redundant snapshots and one-off coordinators have been removed from the checkout. The main results and their immutable evidence archives above remain. The following negative conclusions still constrain future work:

| Candidate or diagnostic             | Recorded outcome                                                                                                                                                                                        |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Background idle retry               | 64-process A/B: 2 cheaper, 10 dearer, 4 mixed. Scheduling intentions fell 68–70%, but maintenance passes per read rose roughly 4.2–4.6 times. Reverted.                                                 |
| Read-mode selector                  | 48 processes/336 samples: 3 cheaper, 2 dearer, 7 mixed; .NET 10 TTL/OFF increased 2.3% and 5.9%. Reverted.                                                                                              |
| Byte read-mode bitmask              | Fewer selector instructions, but 2 cheaper, 3 dearer, 7 mixed; .NET 8 TTL/ON increased 2.9%/3.4% and .NET 10 hot/ON 2.1%/1.8%. Reverted.                                                                |
| Bonus SPSC lane                     | 64 processes/448 samples: 0 cheaper, 13 dearer, 3 mixed. .NET 10 TTL/OFF increased 22.0%/19.5%; TTI/ON 27.6%/29.5%. Diagnostics were OFF because the old cursor probe omitted the lane. Reverted.       |
| Owner helper and final inline hint  | Neither met the intended .NET 10 statistics-ON public-caller native-code gate. Diagnostic captures were excluded from timing; no normal-performance regression claim was made. Reverted.                |
| Scalar expiry guard                 | Predicate microbenchmark gains did not settle engine performance: 112-process A/B was 15 cheaper, 4 dearer, 9 mixed. Reverted.                                                                          |
| Replacement-counter candidate       | Reverted; 13 general regressions retained. A same-binary mixed A/A control showed role throughput differences above 50%, so two-round direction alone could not qualify small gains.                    |
| Fixed-work expiry profiles          | Clock-stack cost was similar in the selected TTL case; differences clustered around read-buffer offer and inlined TryGet work. Native captures did not establish per-instruction cost or false sharing. |
| .NET 8 statistics-ON expiry profile | Roughly 57–58 versus 26 sampled CPU ns/read, plus background spin/yield. A process-level spin control reduced background CPU without improving reader cost. Not a production setting or latency result. |
| Historical image/path comparison    | Identical IL images differed by about 16% until execution path and arguments were fixed. This did not establish the cause of an earlier 54.683 ns observation, heap layout or OS scheduling.            |

The previously measured 40 B/op weak-key lookup belonged to a pre-optimisation snapshot; it is not a current allocation claim. Correctness regressions covering sub-tick expiry, finalisation and quiescence remain in the test suite. Archived failed harness runs are not automatically runtime defects.

## Reproduction and reporting

Use the maintained [benchmark projects](../benchmarks/LoadingCache.Benchmarks/README.md), [load harness](../tools/LoadingCache.LoadHarness/README.md) and tool READMEs. Historical archive commands identify the exact measured revisions; they are not guaranteed to rebuild unchanged against a later SDK or source tree.

```sh
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario resident --concurrency 4 --operations 10000 --capacity 128
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario mixed --concurrency 4 --operations 10000 --capacity 128 --statistics --cancelable
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario fan-in --concurrency 100 --operations 10000 --statistics
```

Initial harness smoke used actual .NET 8.0.31 and 10.0.0, 10,000 requests per scenario: resident loads were zero; 100-way fan-in used 100 loaders; mixed runs used 7,508/7,455 loaders. These are smoke observations, not deterministic hit-ratio or SLA claims. Compiled inputs, command receipts and accounting checks were retained in local artifacts. The initial 28 BenchmarkDotNet Dry processes likewise qualified execution only.

Record source revision/content manifest, SDK and actual runtime, CPU/OS/architecture, cores, GC/JIT/PGO, power state, seed, capacity, concurrency, token/statistics configuration, warm-up, iterations, full commands and exit status. Keep JSON/CSV and failed runs. Profiler captures are separate from normal measurements. Shared CI runs correctness and benchmark smoke; strict performance acceptance needs a sufficiently stable host.

Cross-language published numbers are not same-host speedups. Java/.NET allocation and GC boundaries may differ even in local comparisons. Still outstanding are broad high-core-count/cross-platform matrices, retained-heap coverage for all feature combinations and a resident service measurement precise enough for its agreed budget. Current stability and loading-service evidence are in [release readiness](release-readiness.md), not inferred from this methodology.
