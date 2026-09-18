# ADR-0015: Opt-in loader admission

Accepted, 18 September 2026.

Requiring `MaxConcurrentLoads` for every cache made backend protection mandatory rather than an explicit loading-cache policy. Pinned Caffeine v3.2.4 `836b65c0a83e5d1641ded9c6de578654bc04b2e9` has no equivalent built-in loader-concurrency bound, although executors/loaders may reject work. HTTP 429 belongs to this repository's service probe mapping of `CacheLoadRejectedException`, not the cache API.

All four builders now omit the concurrency bound by default. Legacy `LoadingCache.Create` options use `int?`: omitted and explicit null are equivalent; zero/negative values remain invalid. Explicit C retains fail-fast, no-queue admission shared by cold loads, refresh and bulk. Hits and joins take no new permit. Cache-owned cancellation and same-generation single-flight are unchanged.

F (`MaxPendingLoadKeys`) independently bounds key states. Omitted F inherits explicit C, raised to a larger configured K (`MaximumBulkKeys`) where applicable. With C/F omitted there is no configured bound. True bulk still requires positive F/K with K≤F; C is optional. The separate 1,024-record fallback/bulk-mutation input limit is unchanged.

Internal integer counters use `int.MaxValue` as a representation ceiling for unconfigured limits, not preallocated array/queue sizing or a memory guarantee. Active registry/reservations/finalisation remain intact: invalidation, clear and timeout cannot release still-running work or unfinished cancellation cleanup, and late completion stays fenced. Host admission controls unbounded-by-configuration work.

Changing public `int` to `int?` is a binary API change requiring recompilation and reviewed compiler-generated API baselines. Source readers of the property must handle null; accepting integer initialisers does not imply binary compatibility. The library remains experimental.

`LoadLimitOptionsTests` covers four builders, omitted/null legacy options, 16 gated distinct flights at resident capacity one, joins/caller cancellation, explicit C and independent F rejection, and invalid values. True sync/async bulk tests omit C but retain F/K. Existing explicit-limit timeout/clear/invalidation/finalisation regressions remain. Normal unconfigured loading and explicit overload profiles are verified separately; old limit-eight HTTP failures retain their original scope rather than being renamed Passed. No new upstream source adaptation is introduced.
