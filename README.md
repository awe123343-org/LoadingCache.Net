# LoadingCache

[![CI](https://github.com/awe123343-org/LoadingCache.Net/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/awe123343-org/LoadingCache.Net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/LoadingCache.Net)](https://www.nuget.org/packages/LoadingCache.Net)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)
[![pre-commit: prek](https://img.shields.io/badge/pre--commit-prek-white?logo=precommit&logoColor=FAB040&labelColor=white)](https://github.com/j178/prek)

A typed, in-process cache for **.NET 8+**: adaptive Window TinyLFU eviction,
single-flight loading, expiration and refresh, with a BCL-only core.

One managed package targets `net8.0` and can be used by both .NET 8 and .NET 10
applications. DI integration is an optional separate package.

**Experimental.** The API is not frozen and the full 1.0 release gates are not
complete. Caffeine inspires the engine; this is not an official port or a claim
of identical behaviour or performance.

[Quick start](#quick-start) · [Features](#features) ·
[Benchmarks](docs/benchmark-methodology.md) ·
[Documentation](docs/README.md) · [Release status](docs/release-readiness.md)

## Quick start

Install the prerelease package (one `net8.0` asset supports .NET 8 and .NET 10):

```sh
dotnet add package LoadingCache.Net --prerelease
```

For DI integration, add `LoadingCache.Net.Extensions.DependencyInjection` at the
same version. [NuGet](https://www.nuget.org/packages/LoadingCache.Net) lists the
published versions; the API remains experimental.

```csharp
using LoadingCache;

await using var cache = CacheBuilder.Create<string, string>()
    .MaximumSize(1_000)
    .ExpireAfterWrite(TimeSpan.FromMinutes(10))
    .ExpireAfterAccess(TimeSpan.FromMinutes(2))
    .Comparer(StringComparer.Ordinal)
    .BuildAsyncLoading((key, _) => Task.FromResult($"Value for {key}"));

var value = await cache.GetAsync("configuration");
cache.Invalidate("configuration");
```

Replace the sample loader with your asynchronous backend call. Resident capacity
must be explicitly positive; the sample size is not production sizing advice.
The loader receives a cache-owned token. Pass a request token to `GetAsync` to
cancel only that caller's wait.

Loader concurrency is unlimited by configuration unless you opt in with
`.MaxConcurrentLoads(16)`. At that limit, a new distinct-key load fails with
`CacheLoadRejectedException`; resident hits and callers joining an existing flight
still work. There is no loader wait queue. Use this option when the cache should
protect backend capacity, or manage admission in your host.

## Choose a cache

All four APIs share the same policy engine. Synchronous loading uses a genuine
synchronous loader; it does not block on the asynchronous implementation.

| API                  | Builder                     | Populate or load                          |
| -------------------- | --------------------------- | ----------------------------------------- |
| Manual synchronous   | `Build()`                   | `Put`, `GetOrAdd`                         |
| Loading synchronous  | `BuildLoading(loader)`      | `Get`, `GetAll`, `RefreshAsync`           |
| Manual asynchronous  | `BuildAsync()`              | `Put(Task<TValue>)`, `GetOrAddAsync`      |
| Loading asynchronous | `BuildAsyncLoading(loader)` | `GetAsync`, `GetAllAsync`, `RefreshAsync` |

## Features

| Capability              | What is available                                                                                                         |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Capacity                | Adaptive Window TinyLFU; size or weight limits; runtime maximum adjustment                                                |
| Expiration              | After write, after access, or variable expiry; monotonic `TimeProvider`; TimerWheel; optional idle cleanup scheduler      |
| Loading                 | Same-generation single-flight; independent waiter cancellation; load timeout; true bulk loading and fenced prefetch       |
| Refresh                 | Request-triggered background refresh, explicit refresh, reload with the old value, failure backoff                        |
| References              | Identity-based weak keys; weak values in synchronous caches                                                               |
| Inspection and mutation | Quiet lookup, approximate hot/cold snapshots, mutable `IDictionary` views with conditional updates and compute operations |
| Diagnostics             | Synchronous eviction listener, bounded asynchronous removal listener, optional striped statistics and BCL Metrics         |
| .NET integration        | Typed/named DI registration, opt-in memory-pressure eviction, lease-based automatic disposal through `OwnedCache`         |

Statistics are disabled by default. Call `RecordStatistics()` on the builder to enable them.

For true bulk loading, implement `IBulkSyncCacheLoader` or `IBulkAsyncCacheLoader`
and configure `MaxPendingLoadKeys` and `MaximumBulkKeys`. `MaxConcurrentLoads`
remains optional. Without that capability, bulk reads use single-key loading.
Weighted caches also require a `Weigher` and `MaximumResidentCount`, which bounds
zero-weight entries. See the [feature matrix](docs/feature-matrix.md) for supported
combinations and verification gaps.

## Contracts that matter

- **Cancellation belongs to the waiter.** Cancelling one or all callers does not
  cancel their shared load. A failed load can be retried; null keys, values and
  loader tasks are rejected.
- **Old work cannot overwrite new state.** `Put`/`Set`, `Invalidate` and `Clear`
  revoke older publication rights. Existing waiters may still receive the old
  result, but it cannot replace a newer value.
- **Expiration stays authoritative.** Expired values are misses even before
  physical cleanup. Refresh can return the previous value until its hard
  expiration; failure does not extend that deadline.
- **Capacity is a convergence bound.** Concurrent work can temporarily exceed
  resident limits; after publishers stop, `CleanUp()` converges to the configured
  bound. Opt-in load limits separately account for work that still runs after
  timeout, invalidation or clear. Unconfigured loader concurrency has no fixed
  cache-owned resource bound; resident capacity is not a process memory limit.
- **Callbacks have different delivery contracts.** Eviction listeners run
  synchronously outside cache locks and can delay the triggering step. Removal
  listeners use a bounded asynchronous queue and can drop notifications under
  pressure or shutdown. Neither is a value-disposal protocol.
- **Values belong to their callers too.** Regular caches do not dispose cached
  values. Use the separate manual `OwnedCache` API and caller leases when the
  cache must own disposal. Shutdown cannot forcibly stop arbitrary loader or
  callback code.

Keep key equality stable. Include tenant and authorisation dimensions in the
key; a shared loader must not derive results from the first waiter's ambient
request context or capture a scoped `DbContext` in a singleton.

Weak values are rejected by async caches because shared tasks retain their
results. JVM soft values have no equivalent CLR contract; opt-in memory-pressure
eviction is a separate policy, not an OOM guarantee. See
[Caffeine differences](docs/caffeine-differences.md),
[operation semantics](docs/semantics.md) and
[ownership rules](docs/native-policies-and-ownership.md) for the exact boundaries.

## Benchmarks and release status

See [benchmarks](docs/benchmark-methodology.md) for workload-specific comparisons
with Caffeine and MemoryCache, measurement methods and reproducible results.
[Release status](docs/release-readiness.md) tracks validation coverage, known
limitations and the remaining 1.0 requirements.

## Development

Use the SDK pinned in `global.json` and install both .NET 8 and .NET 10 runtimes.
Tests use TUnit with its native assertions with test-case-level parallelism; CSharpier is
a repository-local tool. Nullable analysis and warnings-as-errors are enabled.

```sh
dotnet restore LoadingCache.slnx
dotnet tool restore
dotnet build LoadingCache.slnx -c Release --no-restore
dotnet csharpier check .
```

[CONTRIBUTING](CONTRIBUTING.md) has test and consumer commands;
[the documentation index](docs/README.md) links the design, samples and benchmark
tools. The [NuGet workflow](docs/nuget-publishing.md) publishes official versions when `VERSION` changes on `main`,
and next-patch alpha snapshots through a manual Action. Both paths require CI to pass. NuGet identity and Trusted Publishing must be configured.

Licensed under [Apache-2.0](LICENSE). Source attribution and dependencies are
recorded in [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md) and the
[upstream map](docs/upstream-map.md).
