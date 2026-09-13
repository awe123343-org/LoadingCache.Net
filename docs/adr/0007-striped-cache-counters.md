# ADR-0007: Bounded opt-in striped counters

Accepted, 12 September 2026.

A single global increment location can contend on every hit; thread/key dictionaries would retain unbounded diagnostic history. Use fixed cache-owned stripes and distinguish cumulative counters from ownership gauges.

`StripedCacheCounters` accepts power-of-two stripe counts from 1 through 64. Default selection rounds processor count up and caps at 64. Each stripe contains `CacheCounterKind.Count` longs, selected by `unchecked((uint)Environment.CurrentManagedThreadId) & (stripeCount-1)`. Thread IDs may be reused; there is no thread registry, key/tenant label map or claimed cache-line alignment guarantee.

`Add(kind,delta)` requires a known counter and non-negative delta. Zero does not write. `Volatile.Read`/`Interlocked.CompareExchange` saturate at `long.MaxValue`; already saturated fields need no CAS. Ordinary `Interlocked.Add` would wrap negative. Saturated values mean at least that many events.

`Snapshot()` allocates one fixed-size array on the diagnostic path and reads each stripe with saturating addition. Fields and gauges are weakly consistent, not one atomic transaction. In-flight/resident/backlog gauges come from engine ownership, not saturating decrement counters.

Counters cover requests/hits/misses, actual load invocation/outcome/time, bulk, refresh outcomes/backoff/skips, coalescing/rejection, capacity eviction/weight, collection, listener drops/failures and individual removal causes. Capacity `Evictions` includes size, weight and memory pressure, not expiry/collection. Time deltas are non-negative monotonic `TimeSpan` ticks, not wall-clock corrections. Manual task insertion does not invent a loader invocation.

With statistics and Metrics off, the counter field is null: no no-op object or event allocations. Either option enables the bounded store; Metrics-only mode keeps public cumulative statistics hidden. BCL observable instruments read counters outside mutation, with bounded cache/cause/status labels. Listener totals are folded once. Maintenance/drop counters follow the same opt-in visibility rules; current backlog remains independently observable.

`StripedCacheCountersTests` covers parallel exact quiescent totals, per-stripe/aggregate saturation, invalid input without mutation, concurrent non-negative snapshots, processor normalisation, bounded metadata under thread churn and zero delta. Integration tests cover 100 misses/one load, refresh, listener outcomes and Metrics cardinality. These checks do not establish optimal high-core throughput, exporter compatibility or full-platform readiness.

Implementation is independent BCL `long[]`, `Volatile` and `Interlocked`, with no copied upstream source or runtime dependency. Rejected alternatives are global hot fields, historical thread/key maps, per-event tasks/closures/boxing, wrapping arithmetic and counter-derived ownership gauges.
