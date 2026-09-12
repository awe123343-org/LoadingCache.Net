# Full-scope primary-source evidence

Accessed on 12 September 2026. Conclusions are consolidated in [research](../../research.md). This pass did not execute upstream code/tests or audit entire repositories.

| File | Recorded query or purpose |
| --- | --- |
| release.json | Caffeine latest stable release response: v3.2.4 |
| stable.json | Commit resolved from Caffeine v3.2.4 |
| master.json | Frozen Caffeine master audit commit |
| bitfaster.json | BitFaster latest release response: v2.6.1 |
| runtime8.json | Runtime source resolved from v8.0.31 |
| files.json | Thirty successful source/test/licence downloads: immutable URLs, sizes, hashes and read scopes; also one failed path |
| summary.json | Counts and execution boundaries |

Original downloads were temporary. Retrieve them using files.json and verify their hashes; temporary files are not durable evidence. Downloaded-only files, the complete master WindowClimber probe/anchor controller and BitFaster TimerWheel test bodies were not represented as fully audited.

The first request for `BitFaster.Caching/Policy/IBoundedPolicy.cs` returned 404. The corrected `BitFaster.Caching/IBoundedPolicy.cs` succeeded; both records remain.

Caffeine stable/master is Apache-2.0; BitFaster and runtime are MIT. BitFaster's TimerWheel records Caffeine ancestry, so adaptation requires tracing applicable notices rather than relying on a repository badge. Actual local adaptations are recorded in [the upstream map](../../upstream-map.md).
