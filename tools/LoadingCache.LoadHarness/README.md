# Loaded-cache and latency harness

This BCL-only executable records real invocation-to-completion p50/p95/p99, loader calls, whole-harness allocations, GC collections, and runtime/assembly identity. It complements BenchmarkDotNet lookup measurements and the policy simulator.

```sh
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario resident --concurrency 4 --operations 20000 --capacity 128
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario mixed --concurrency 4 --operations 20000 --capacity 128 --statistics --cancelable
dotnet run --project tools/LoadingCache.LoadHarness -c Release -f net10.0 -- --scenario fan-in --concurrency 100 --operations 10000 --statistics
```

- `resident`: prepopulated values; verifies zero loader invocations.
- `mixed`: pre-generated uniform keys over four times capacity with genuinely asynchronous, scheduler-only loads. It does not model network or storage latency.
- `fan-in`: each round invalidates one key, starts up to the configured number of waiters, then releases the shared loader. It verifies exactly one backend invocation per round. Gate time is included in each waiter's measured latency.

The harness uses **closed-loop** traffic. External admission/queue delay is excluded and coordinated omission is not corrected. Results do not describe overload behaviour or a service SLA. Percentiles come from individual measured durations using nearest rank, not from the mean. Latency instrumentation and the driver contribute overhead; allocation figures describe the whole harness, not cache allocation per operation.

Prepopulation is the only warmup. Initial runs are exploratory smoke, especially while other work shares the CPU. Formal comparisons require a recorded warmup/repetition protocol and stable environment, multiple concurrency/capacity settings, stats and token configurations, and equivalent loaded-cache baselines. Open-loop overload measurement, retention, and workload-specific backend models remain required validation work. Seed fixes the input trace, not the OS schedule.
