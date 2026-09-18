# V1 HTTP loading admission profile v2

Status: the fixed v2 profile passed its formal matrix on actual .NET 8.0.31 and
.NET 10.0.12, macOS Arm64, on 2026-09-18 (six serial arms per runtime). Independent
qualification and exact source/build/runtime hashes are recorded in
`artifacts/opt-in-load-limits-20260918/http-v2-evidence-review/formal-qualification.json`.
That evidence belongs to the frozen staged source. The profile is integrated into
the repository; subsequent namespace, serialisation-contract and IDE cleanup is
recorded separately in `artifacts/opt-in-load-limits-20260918/http-integration01/`.
The frozen formal result is not a rerun of those later edits. It does not qualify
new-default long endurance, other
platforms, or overall V1 release readiness. This opt-in profile implements the
Miss/burst section of `v1-performance-gates.md`. The resident profile and its 5%
CPU/p99 thresholds are unchanged. No MemoryCache speedup comparison is made.

Freeze this document, sources, binaries, runtime, and command before measurement.
Run three serial rounds per actual .NET 8/10, alternating statistics OFF/ON order.
`--smoke` shortens normal measurement only; smoke never closes acceptance.

Raw identity is `loading-admission-v2`, schema 4. All cases use loopback Kestrel
HTTP, one async loading lookup per request, integer key/value identity and
capacity 256. Normal loading and its warmup omit both MaxConcurrentLoads and
MaxPendingLoadKeys; both raw fields must be present and null. Fan-in,
expiry-refresh, burst and recovery retain explicit MaxConcurrentLoads=8 and
MaxPendingLoadKeys=8, recorded as integers. Recovery uses the same cache as burst.
This separates default normal loading from explicit-limit lifecycle acceptance.
It does not claim the HTTP fan-in/expiry cases exercise default admission.
Normal/recovery backend service is a real asynchronous 20 ms delay, observed
service times retained. Gated cases intentionally ignore cache cancellation until
the harness releases them; their latency is not a backend RPC latency estimate.
This executable contains host and client; CPU is combined process CPU, not the
resident profile's server-only CPU metric. No relative CPU acceptance applies.

Predeclared acceptance (each stats arm and every round must pass):

These are initial profile risk-screening budgets, not a user production SLO.
At 200 arrivals/s and 20 ms modeled service, mean backend demand is about four
executions. Normal loading has no configured C/F bound; the unchanged tail and
drain budgets reject persistent backlog growth. The explicit limit of eight
continues to apply to the separate overload/timeout/recovery cases.

| Case           | Offered / exact backend calls                        | Required result                                                                                                                                                                                                                                     |
| -------------- | ---------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| fan-in         | 64 same-key / 1                                      | Server has invoked all 64 GetAsync calls before backend release. None returned early. All receive nonzero key 42.                                                                                                                                   |
| expiry-refresh | 1 stale read + 64 expired reads / 1                  | Seed value -1 at fake time 0; advance to 6 s with refresh=5 s / hard TTL=10 s. Stale HTTP response starts refresh. Once backend entered, advance to 11 s. All 64 GetAsync invocations join before release; no expired response returns -1.          |
| burst          | 8 initial distinct + 24 excess + 8 permit checks / 8 | Initial backend calls remain gated. Cache-owned real 2 s deadline yields exactly 8 timeout responses. 24 excess + 8 later checks reject. After timeout and permit checks, active backend=8 and InFlightLoads=8. No ninth invocation before release. |
| recovery       | 64 distinct at 100/s / 64                            | Same cache as burst; release backends, drain, then recovery traffic. All complete; release through final recovery/drain <=5 s. Timed-out keys remain absent.                                                                                        |
| normal warmup  | 400 distinct at 200/s / 400                          | All complete; queues drain before measured phase.                                                                                                                                                                                                   |
| normal         | 6000 distinct at 200/s / 6000                        | 30 s fixed offered schedule, 100% cold misses. All complete, no client/server rejection/timeout/failure. All-outcome p99 <=250 ms, maximum schedule-to-outcome <=1000 ms; final drain <=5 s. Smoke offers 400 over 2 s.                             |

The normal tail/max budgets prevent a growing queue being hidden by a successful
final drain. Arrival latency starts at the original offered timestamp, including
client scheduling and connection queue delay. Up to 256 HTTP requests may be
pending; overflow is a recorded `rejected` outcome, never a dropped sample.
Every request has a 5 s deadline from its scheduled arrival. Control waits and
failure joins are bounded at 15 s; shutdown releases every backend gate first.
The cache-owned 2 s timeout is enabled only for burst, avoiding wall-clock timer
dependence for the fake-expiry oracle. Gate sequencing uses observed invocation
and completion state, never a sleep to guess race ordering.

Raw JSON retains every request's phase/key/scheduled/send/finish ticks, outcome,
status, returned value, server invocation/return ticks, and every backend start /
finish event. Burst also records each cache-owned timeout origin, request ID/key,
timer-creation timestamp and timer due/period. Its dedicated real `TimeProvider`
records the last `GetTimestamp` value returned immediately before `CreateTimer`;
the pinned `EngineTimeout.PrepareFlightExecution` assigns that exact value to
`TimeoutStartTimestamp`. Request scope covers the synchronous `GetAsync` call,
including timer construction while ExecutionContext flow is suppressed. The
scope is not installed on normal, fan-in or fake-expiry paths. The validator
requires return minus that origin >=2 seconds, exact request/key association,
and origin <= timer creation <= backend start. A changed timeout source requires
sequence re-review. HTTP dispatch/backend start are not timeout clock origins.

Schema 4 retains `activeFlights` for every resource observation. Drain must
observe an empty cache-owned flight registry as well as zero backend, executing
load and maintenance gauges: execution can finish before reservation retirement.
The probe binds the existing internal `HasActiveFlights` getter once per cache;
that getter takes the engine gate. There is no request-path reflection or new
public API. Every required drain checkpoint must appear exactly once and retain
its full resource fields; missing rows, statistics or flight state fail closed.
Callers stop/join producers before treating the empty registry as quiescence.
Failure cleanup releases backend gates, joins client work, stops/joins the HTTP
host, then drains and disposes the cache. Each operation retains its existing
bounded watchdog and cleanup failures block validation. The 5-second drain /
recovery acceptance budgets and all offered schedules remain unchanged.

Validator independently recomputes counts, all-outcome/success-only
p99, backend concurrency and exact calls, gate ordering, permit retention, stale
responses, drain, and recovery budget. A successful-only percentile cannot rescue
a failed completion-rate gate. Statistics OFF retains resource gauges but does
not claim maintenance-fault counters are enabled; ON checks actual counters.

Failure controls (`--loading-fault backend-error|wrong-value|stuck-backend`) are
diagnostic runs only. Each must fail normal acceptance and preserve all offered
outcomes; stuck backend is released during bounded cleanup. These controls do not
alter production runtime. Validator mutation self-checks additionally reject a
missing outcome, wrong invocation count, premature permit release, a forged
success-only tail, early cache timeout masked by HTTP dispatch/response delay,
and zero running work with an unretired reservation. Missing required drain rows,
statistics or flight state are negative controls too. Schema/profile mismatches,
hidden normal C/F bounds, missing nullable fields, unconfigured burst bounds and
configured executions above eight are rejected. A paired helper control accepts
nine executions with no configured limit and rejects nine with explicit eight;
the same execution-bound validator checks raw backend intervals and observations.
Failure-control runs are never acceptance evidence.

The runner requires `--build-manifest`: a successful isolated probe build links
exact source/config hashes before/after compilation to every output, and embeds
a verified core build manifest with its frozen DLL. Before every run it rejects
changed or added sources/config, stale probe/core outputs, and mismatched build
frameworks. Both manifests, sources, output hashes, build commands/logs and SDK
records are frozen with the run. Merely hashing current source beside an older
DLL is insufficient. Schema 1/2/3 raw and their frozen validators remain historical
evidence; they cannot pass this schema 4 validator and must not be retrofitted.
The original schema-3 formal limit-8 normal zero-rejection results remain Failed.
Profile v2 changes the declared normal configuration following the approved opt-in
contract; it does not relabel those failures or qualify the changed source using
old endurance evidence. Counts, rates, latency and recovery budgets are unchanged.

Run from the repository root after current-core validation and workload
coordination. Every output directory must be new. The
unchanged provenance builder uses the supplied verified current-core manifest
and does not build the repository core project:

```text
uv run tools/LoadingCache.ServiceProbe/provenance.py --dotnet /absolute/sdk/dotnet --repository /absolute/repo --framework net8.0 --core-manifest /absolute/current-core-build/manifest.json --output /absolute/repo/artifacts/opt-in-load-limits-20260918/http-v2-build-net8
uv run tools/LoadingCache.ServiceProbe/loading.py run --dotnet /absolute/runtime/dotnet --framework net8.0 --repository /absolute/repo --build-manifest /absolute/repo/artifacts/opt-in-load-limits-20260918/http-v2-build-net8/manifest.json --output /absolute/repo/artifacts/opt-in-load-limits-20260918/http-v2-formal-net8
uv run tools/LoadingCache.ServiceProbe/loading.py validate /absolute/repo/artifacts/opt-in-load-limits-20260918/http-v2-formal-net8
uv run tools/LoadingCache.ServiceProbe/loading.py self-check /absolute/repo/artifacts/opt-in-load-limits-20260918/http-v2-formal-net8
```

The raw data, manifest hashes and independent validation are required evidence.
Future runs from the integrated source require fresh build manifests and output
directories; do not retrofit the historical staged manifests. Platforms and
source revisions outside the recorded formal qualification remain Not run.
