# V1 HTTP service admission profile

Frozen design: 2026-09-16. Implementation and execution results are separate: this profile is **Not run** until a source-bound summary is attached. It checks the initial 5% CPU/p99 budget in [v1-performance-gates.md](v1-performance-gates.md); it does not replace miss/burst, retention, endurance, platform, or package gates.

## Workload fixed before measurement

The existing ASP.NET Core sample returns a tenant configuration using an async loading cache. The probe preserves that host boundary: an HTTP GET parses one integer tenant ID, reads one configuration, and serializes the same JSON response. It adds no CPU loop, delay, hashing, or extra business operation to dilute cache cost. Client validation is in a separate process.

| Parameter             | Fixed value and reason                                                                                          |
| --------------------- | --------------------------------------------------------------------------------------------------------------- |
| Keyspace and capacity | 1,024 tenant configurations, fully preloaded; cyclic uniform keys                                               |
| Value                 | Tenant ID plus a 1,024-character ASCII config string; identical values and response serialisation in both arms  |
| Cache operations      | One `GetAsync` per request; request cancellation is passed as the waiter token                                  |
| Control               | The same preloaded array indexed by parsed tenant ID; same HTTP route and serialisation                         |
| Cache                 | Bounded async loading cache; TTL 30 minutes or TTI 10 minutes, statistics OFF and ON separately                 |
| Loading limits        | `MaxConcurrentLoads=16`, `MaxPendingLoadKeys=16`; resident admission still requires zero loader calls           |
| Hit/miss ratio        | 100% resident hits; expected backend calls exactly zero including warmup                                        |
| Loader service time   | 0 ms, returns the same preloaded value; any invocation fails the resident profile                               |
| Arrival               | Fixed 2,000 requests/second, independent of prior completion; 10-second warmup then 30-second measurement       |
| Admission             | At most 256 client requests in flight; additional scheduled requests are explicitly rejected                    |
| Deadline              | 5 seconds from scheduled arrival, including dispatch delay; warmup must also have full completion               |
| Transport             | Loopback HTTP/1.1 using ASP.NET Core and BCL HttpClient; client/host are separate processes on the same machine |
| Runtime               | Actual .NET 8 and .NET 10 separately, Release build, default JIT/tiering/PGO/GC configuration                   |

The rate is a modest backend configuration endpoint load, not a saturation claim. The payload models a small configuration document, not arbitrary work. There is no real production trace: this sample is an initial admission workload only. A service doing hundreds of lookups per request needs its own budget. TTL/TTI are active metadata costs here, not expiry correctness or reload stress.

## Measurement and decision

One client starts its fixed arrival schedule after `/begin`. Each raw latency is measured from the intended arrival through response-body read and validation; scheduler/HTTP connection queues are included. The driver uses asynchronous 1 ms pacing and dispatches every due arrival, so local timer granularity can create small bursts; these are retained in the latency, never subtracted. The raw outcome arrays include completed/rejected/timeout/failed requests. Success p99 is labelled separately; any incomplete request prevents admission.

The server records whole-process CPU after warmup until `/end` has drained queued cache work and awaited cache disposal. Before `/begin`, the completed warmup is also drained, outside the measured CPU window. At `/end`, the client has awaited every response and there are no cache producers. The probe repeats `CleanUp` until loader/read/write gauges are zero, then calls `CleanUp` again to cross the engine coordination lock after the zero-queue observation and rechecks the gauges. This joins a worker that consumed its final slot before finishing its policy application. The final snapshot, initial snapshot, attempt count and elapsed time are retained; failure to drain within 30 seconds invalidates the run. Only then does disposal release resources. Disposal itself is not proof of completed maintenance: it may drop queued read records and does not wait for every scheduled callback.

CPU includes background maintenance, final queue drain and disposal, and excludes the independent load generator. The control uses the same timing endpoints. Runtime startup and warmup are excluded from both arms; steady-state host CPU per completed request, total CPU seconds, request counts, wall time and drain duration are retained. Instrumentation response serialisation, server shutdown and any inert ThreadPool callback bookkeeping after the snapshot are not service work attributed by this window. The public gauges establish completed cache queue work, not a process-wide ThreadPool join.

Each runtime first runs three fresh-process A/A pairs, then three fresh-process cache/control pairs for every TTL/TTI × stats OFF/ON profile, reversing pair order in the middle round. The exact same arrival profile and values are used. All A/A absolute CPU/request and p99 relative differences must be ≤5% for initial precision admission. This conservative finite-repeat check is not a statistical confidence interval. If any A/A pair exceeds 5%, report **inconclusive** regardless of cache ratios; investigate the environment before deciding whether to predeclare a new experiment. Do not rerun until green.

With adequate A/A precision, all paired CPU/request and p99 increments must be ≤5%, and every request must complete correctly with zero backend calls. Otherwise report **budget-not-met**, not a proven regression. Per-pair ratios remain visible; no pooled winner hides directional instability. No source change during a matrix is permitted. `--smoke` uses 1-second warmup, 2-second measurement and one pair per cell; its status is always **smoke-only**.

## Reproduction

The tool is deliberately standalone and is not added to the solution. The coordinator builds/executes only while it owns the CPU lane:

```sh
dotnet restore tools/LoadingCache.ServiceProbe/LoadingCache.ServiceProbe.csproj
dotnet build tools/LoadingCache.ServiceProbe/LoadingCache.ServiceProbe.csproj -c Release --no-restore
uv run --no-project tools/LoadingCache.ServiceProbe/run.py --dotnet artifacts/runtime8/dotnet --framework net8.0 --output artifacts/v1-service/net8-smoke --smoke
uv run --no-project tools/LoadingCache.ServiceProbe/run.py --dotnet artifacts/runtime8/dotnet --framework net8.0 --output artifacts/v1-service/net8-formal
uv run --no-project tools/LoadingCache.ServiceProbe/run.py --dotnet artifacts/runtime10/dotnet --framework net10.0 --output artifacts/v1-service/net10-formal
```

The selected runtime installation must include exactly one matching patch each of `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`. The runner records both installations, rejects runtime tuning/roll-forward overrides, and compares every server's actual runtime and cache/probe/CoreLib/ASP.NET Core assembly hashes with those selected bytes. It executes a frozen copy of the prebuilt output and archives relevant source/project inputs. Source paths and bytes, frozen inputs and selected runtime files must remain unchanged throughout the matrix. The manifest retains argv, Git state, SHA256, effective runtime environment and the process-only polling watcher setting inherited from the host sample workaround.

`commands.json`, process exits, logs, raw request durations/outcomes, paired summaries and JSON hash lists are retained. An atomic `summary.json` begins as `running`; any setup, execution or verification exception changes it to `failed`, preserving completed arms and their raw data. The runner independently checks offered/outcome counts and reconstructs p99 from raw ticks. Invalid response content becomes a failed request and cannot be admitted. A source hash is not proof of a clean build; the coordinator must build these exact sources first and preserve that build log.

One complete runtime matrix has 30 fresh hosts, about 20 minutes of requested traffic plus startup/drain. Runs are serial. Neither the raw counts nor the 5% threshold may be changed after inspecting the formal results.
