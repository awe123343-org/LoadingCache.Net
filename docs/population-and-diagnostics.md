# Population and diagnostics

The authoritative contracts are in [operation semantics](semantics.md), with implementation ownership in [concurrency](concurrency.md). This guide highlights configuration and integration choices rather than duplicating those specifications.

## True bulk loading

Implement `IBulkSyncCacheLoader` or `IBulkAsyncCacheLoader` to advertise true bulk capability. Configure positive `MaxPendingLoadKeys=F` and `MaximumBulkKeys=K`, K≤F. `MaxConcurrentLoads=C` is optional. One backend group consumes one C reservation and one F for each newly owned key; joining existing work consumes neither again. Without bulk capability, reads use bounded single-key fallback.

K bounds input enumeration including duplicates, and output snapshot records. Fallback/bulk mutation defaults to 1,024 records. Only missing unowned keys go to the backend; ready values record access and expired values with valid refresh join that refresh. Every owned key has its own promise usable by ordinary Get and overlapping batches. A caller-side read-expiry exception cannot strand already installed shared work.

Validate/copy all output before publication, including required keys, nulls, comparer duplicates, weights and expiry. The result map must remain safely enumerable until copied. Validation failure fails only the newly owned group; publication is independently fenced per key, not a cross-key transaction. Prefetched extras are not returned to the caller or counted as requests; absent-only admission uses a bounded same-key mutation journal. Overflow rotates the journal token and suppresses old extras without blocking new groups. See ADR0009/ADR0012.

Caller cancellation cancels only aggregate waiting. Timeout/shutdown/late completion retain execution/key reservations through real finalisation. A loader waiting on any key it owns fails logical-chain reentrancy checks; this is not arbitrary wait-graph detection.

## Weak references

`WeakKeys()` requires reference keys, uses identity and rejects a custom comparer. `WeakValues()` requires reference values and synchronous manual/loading personalities; async tasks retain results and are rejected for this mode. Weak-value conditional comparisons also use identity.

A lookup captures one strong local reference for the operation. Resident policy/wheel metadata does not strongly retain weak targets, but active loaders, results, snapshots and listener payloads can. A strong value pointing back to a weak key keeps it alive: this is not ConditionalWeakTable ephemeron behaviour. Collected reads miss and exact-version cleanup reclaims metadata. Temporary lookup wrappers have been removed; throughput/allocation evidence remains workload-specific, not a claim that every path allocates zero or matches Caffeine.

## Listeners, statistics and Metrics

Eviction listeners receive automatic removal causes outside all locks before the owning synchronous step or normal successful load completion returns. Slow callbacks delay that step; exceptions are observed without rollback. Other operations' shutdown does not wait for them. Removal listeners asynchronously observe all actual value-version removals through bounded queues, with measured drops/rejections/shutdown losses. No global ordering across concurrent operations or exactly-once delivery is promised.

Capture and dispatch queues each default to 1,024 entries; total payload retention is twice the configured capacity plus constant handoff/active events. Collected events may have no surviving key/value. `GetNotificationStatistics()` remains readable after disposal. Listener events are not safe value-disposal signals; use [OwnedCache leases](native-policies-and-ownership.md).

`RecordStatistics()` enables at most 64 fixed stripes of saturating counters. Requests and backend work are separate: 100 waiting misses can share one load; bulk counts deduplicated requested keys separately from backend calls. Pre-cancelled/quiet operations do not count, prefetch adds no requests, and caller cancellation is not loader cancellation. Snapshots are weak aggregates, not transactions or latency histograms.

`EnableMetrics(cacheName)` independently enables BCL observable instruments; no OpenTelemetry core dependency is required. Use bounded cache-name/cause/outcome labels, never tenant/user/key identifiers in the cache name. Collection reads counters outside engine mutations. Aggregate load duration does not provide p99.

Memory-pressure eviction is separately named and disabled by default. It samples possibly stale GC pressure and trims bounded cold candidates; it cannot reclaim externally retained references or guarantee OOM prevention, and is not JVM soft-reference compatibility.
