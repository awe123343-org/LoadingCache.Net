# ADR-0011: Native pressure, dictionary and ownership APIs

Accepted, 12 September 2026.

Provide opt-in memory-pressure eviction under its own name, not `SoftValues`. The default source reports the latest GC memory-load observation, which may be stale and is neither process RSS nor cache bytes. Do not trigger `GC.Collect` or promise OOM prevention. One bounded sampler runs outside the hit path and cache locks; version-fenced trimming cannot cancel cold loads or return permits for running work.

Expose cache-aware `IDictionary`/`IReadOnlyDictionary` facades with explicit atomic methods rather than the authoritative map. Async value views inspect materialised values without waiting/loading. Enumeration is a weakly consistent snapshot excluding pending/expired values; bulk mutation is not a transaction. Compare/compute outside locks, then validate exact entry, epoch and revisions. Equal values do not prove identity unchanged.

Ordinary raw-value APIs cannot know when callers finish using a value. Immediate disposal on eviction would cause use-after-dispose; weak references and `GC.KeepAlive` cannot supply the missing lifetime signal. Automatic disposal therefore uses a separate manual `OwnedCache` and explicit leases. Dispose only after all residency aliases and leases end, outside engine locks and independently of droppable notifications.

Scheduling failure retains a bounded owned-value backlog for cleanup/retry; slots remain occupied until disposal actually finishes. Synchronous disposal does not block on async user work; `DisposeAsync` observes shared engine bookkeeping, while leases continue protecting values. Loading/bulk/variable-expiry/weak lease-returning APIs are not implied by raw-value engine support.

Regression counterexamples: a paused reader outliving eviction; conditional equality paused while another thread replaces an equal value; pressure sampling paused across clear/set; disposal racing the weigher during ownership transfer. Full transfer/failure/alias contracts are in [native policies and ownership](../native-policies-and-ownership.md).

References: [GC memory information](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getgcmemoryinfo?view=net-8.0), [latest-GC memory load](https://learn.microsoft.com/en-us/dotnet/api/system.gcmemoryinfo.memoryloadbytes?view=net-8.0), [MemoryCache size limits](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory?view=aspnetcore-10.0#limit-cache-size-with-setsize-size-and-sizelimit), [disposal ownership](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose).
