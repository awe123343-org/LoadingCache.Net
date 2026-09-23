# Changelog

## Unreleased — experimental

- Remove the legacy static `LoadingCache.Create` and `LoadingCacheOptions`; use
  `CacheBuilder.Create<TKey, TValue>().BuildAsyncLoading(...)` with explicit capacity instead.

- Preserve the engineering commission and establish contracts, research,
  provenance, acceptance criteria and an execution plan.
- Implement the first bounded async ownership slice and controlled contract tests.
- Target .NET 8, run TUnit contracts on .NET 8 and .NET 10, and
  use a pinned repository-local CSharpier formatter.

No stable version has been released. See the execution plan for verified behaviour
and remaining implementation work.
