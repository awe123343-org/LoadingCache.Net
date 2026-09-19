# Documentation

Start with the [project README](../README.md) for installation and an example. The API is prerelease; implementation, benchmark results and release qualification are separate claims.

## Using the cache

| Topic                                                | Guide                                                                                |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------ |
| Features and legal combinations                      | [Feature matrix](feature-matrix.md), [Caffeine differences](caffeine-differences.md) |
| Cancellation, refresh, invalidation and shutdown     | [Operation semantics](semantics.md)                                                  |
| Bulk loading, weak references, listeners and metrics | [Population and diagnostics](population-and-diagnostics.md)                          |
| Dictionary views, memory pressure and value leases   | [Native policies and ownership](native-policies-and-ownership.md)                    |
| Typed/named singleton registration                   | [Dependency injection](dependency-injection.md)                                      |
| ASP.NET Core, gRPC and Worker examples               | [Host samples](host-samples.md)                                                      |

## Results and qualification

- [MemoryCache comparison](benchmarks/parallel-resident-put-20260916/README.md), [Caffeine matrix](benchmarks/latest-20260914/README.md), [Guava read comparison](benchmarks/guava-read-20260913/README.md)
- [Benchmark methodology](benchmark-methodology.md), [simulator results](simulator-results.md), [benchmark tools](../benchmarks/LoadingCache.Benchmarks/README.md)
- [V1 acceptance gates](v1-performance-gates.md), [endurance protocol](v1-endurance-profile.md), [stability results](v1-stability-results-20260918.md)
- [Resident service precision limits](v1-service-precision-diagnostic-20260918.md), [release readiness and remaining gaps](release-readiness.md)

Dated evidence retains its original source identity, including failed runs. Older results do not qualify later code; stress totals are not throughput comparisons.

## Contributing and releasing

- [Contribution workflow](../CONTRIBUTING.md) and [formatting](formatting.md)
- [Concurrency](concurrency.md), [resource bounds](resource-model.md), [acceptance matrix](acceptance-matrix.md)
- [Architecture decisions](adr/README.md), [research](research.md), [source provenance](upstream-map.md)
- [Package and AOT validation](package-and-aot.md), [NuGet publishing](nuget-publishing.md)
- [Compatibility](compatibility.md), [security reporting](../SECURITY.md), [changelog](../CHANGELOG.md)
