# Changelog

## 0.2.0 — experimental

This release remains experimental, without a support SLA; public APIs may still change.

- Remove the legacy static `LoadingCache.Create` and `LoadingCacheOptions`; use
  `CacheBuilder.Create<TKey, TValue>().MaximumSize(...).BuildAsyncLoading(...)`
  or an explicit weight/resident bound instead. This intentionally breaks source
  and binary compatibility with published 0.1.0 and 0.1.0-alpha.1.2 packages; generic synchronous
  `LoadingCache<TKey, TValue>` remains supported.
- Add `CacheBuilder.EnableCoarseExpirationChecks()` (#41): opt-in TTL-only checks
  that may detect expiry late, with no maximum delay guarantee.
- Document fixed-TTI access coalescing with at most one millisecond of timestamp
  error, including runtime duration changes.
- Document eligible idle write-maintenance deferral and internal read-enqueue
  diagnostics without adding a public statistics field or timing SLA.
- Record source-specific API, Rider, package/Source Link, AOT/trim and loading
  service qualification in [release readiness](docs/release-readiness.md).
  Resident service precision remains Inconclusive under the unchanged 5% gate.
- Direct private security reports to GitHub Private Vulnerability Reporting.
- Migrate tests from NUnit to TUnit, retaining FluentAssertions (#17).

### Performance

- Make eligible fixed-expiry hits lock-free and pad counters (#35).
- Re-arm background reads at a quarter-stripe threshold (#38).
- Coalesce nearby eligible fixed-TTI touches (#39).
- Store timer nodes on entries instead of in a separate index (#40).
- Trim synchronous-load allocations (#43).
- Defer idle write signals only for eligible no-expiry caches (#44).
- Trim bulk-load allocations and redundant sibling scans (#45/#46).
- Skip expiration-timer requests when no timer exists (#50).
- Derive internal read-enqueue totals instead of incrementing an atomic on every successful read offer (#49).

## 0.1.0 — experimental

- Preserve the engineering commission and establish contracts, research,
  provenance, acceptance criteria and an execution plan.
- Implement the first bounded async ownership slice and controlled contract tests.
- Target .NET 8, run NUnit/FluentAssertions contracts on .NET 8 and .NET 10, and
  use a pinned repository-local CSharpier formatter.

No stable version has been released. See [release readiness](docs/release-readiness.md)
for verified behaviour and remaining qualification limits.
