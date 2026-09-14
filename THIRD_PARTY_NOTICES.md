# Third-party references and development dependencies

The production core uses the .NET BCL and includes the Caffeine adaptations
listed below. Guava, BitFaster and FusionCache also informed design research.
No upstream endorsement or clean-room claim is made. Immutable source, test and
license references are recorded in [the upstream map](docs/upstream-map.md).

## Caffeine

Copyright 2014, 2015, 2017, 2026 Ben Manes. All Rights Reserved.

Caffeine is licensed under the Apache License, Version 2.0. A full copy is
included in [LICENSE](LICENSE). Applicable copyright and modification notices
are also retained in the adapted files.

- `src/LoadingCache/Policy/FrequencySketch.cs` adapts Caffeine's packed four-way
  frequency sketch, hash constants, saturation and aging. The .NET changes use
  uint/ulong arithmetic, a bounded table, configurable seed and local sample
  handling.
- `src/LoadingCache/Policy/WindowTinyLfuPolicy.cs` adapts sizing, admission,
  hill-climbing and jitter concepts/formulas from `BoundedLocalCache.java` in
  v3.2.4, commit `836b65c0a83e5d1641ded9c6de578654bc04b2e9`.
  Tiny-cache tuning additionally follows selected `WindowClimber.java` formulas
  at master commit `d885a95eee51fdfe13f450fd9cba80f58f7e0def`. The .NET changes
  include exact node ownership, long weights, explicit maintenance budgets and
  local runtime resizing contracts.

These modifications do not provide source or API compatibility with Caffeine.
Upstream tests were read as research; this repository's policy tests are local
acceptance tests and do not establish that the upstream test suite was run.
`src/LoadingCache/Expiration/TimerWheel.cs` adapts the hierarchy and bucket cascade
from the same stable Caffeine revision, with normalized ticks, bounded work,
explicit node ownership and due-node return batches. Local continuation handling
and its regression tests differ from the upstream callback-coupled implementation.
See ADR-0003 for the exact source and modification record.

`src/LoadingCache/Maintenance/StripedReadBuffer.cs` adapts the bounded CAS ring,
lazy dynamic striping and bounded-retry strategy in Caffeine v3.2.4
`BoundedBuffer.java`, `StripedBuffer.java` and `Buffer.java` at the stable commit
above. Copyright 2015 Ben Manes. All Rights Reserved. The upstream striping
design additionally credits Doug Lea with assistance from members of JCP
JSR-166 Expert Group, released to the public domain as described at
<https://creativecommons.org/publicdomain/zero/1.0/>. These attributions are
preserved alongside the Apache-2.0 notice; they do not relicense the whole file
as public domain. Local modifications use CLR publication primitives, explicit
shutdown ownership, local counters, configurable capacity and budgeted batch
draining. The cold-start bypass in the policy adapter is also informed by
`BoundedLocalCache.java`; see ADR-0014 and the upstream map for scope and tests.

## Development dependencies

Direct test dependencies and formatting tool, verified from their published NuGet package metadata
on 2026-09-12:

| Package                | Version | License    | Package metadata                                                      |
| ---------------------- | ------- | ---------- | --------------------------------------------------------------------- |
| NUnit                  | 4.6.1   | MIT        | [NuGet](https://www.nuget.org/packages/NUnit/4.6.1)                   |
| NUnit3TestAdapter      | 6.3.0   | MIT        | [NuGet](https://www.nuget.org/packages/NUnit3TestAdapter/6.3.0)       |
| FluentAssertions       | 7.2.0   | Apache-2.0 | [NuGet](https://www.nuget.org/packages/FluentAssertions/7.2.0)        |
| CSharpier              | 1.3.0   | MIT        | [NuGet](https://www.nuget.org/packages/CSharpier/1.3.0)               |
| Microsoft.NET.Test.Sdk | 18.0.1  | MIT        | [NuGet](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.0.1) |

These are development dependencies, not production package dependencies. The
exact metadata response is recorded in `docs/dependency-evidence.json`. A complete
transitive dependency/license review is a remaining release gate. Downloaded research source is stored outside the repository; immutable retrieval
metadata is retained in the research evidence. Actual adaptations are listed above.

Future adaptations must retain the applicable copyright, license, notices and
modification markers in the actual files and this record; a URL alone is not a
substitute for notices required by the applicable license.

Additional benchmark/test dependencies verified from published NuGet metadata on
2026-09-12 (`docs/full-scope-dependency-evidence.json`):

| Package                                   | Version | License              | Use                           |
| ----------------------------------------- | ------- | -------------------- | ----------------------------- |
| BenchmarkDotNet                           | 0.15.8  | MIT                  | Benchmark executable          |
| Microsoft.Extensions.Caching.Memory       | 10.0.12 | MIT                  | Lookup baseline               |
| BitFaster.Caching                         | 2.6.1   | MIT, package LICENSE | Lookup/policy baseline        |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | MIT                  | Fake time and scheduler tests |

## Optional DI integration and host samples

The DI extension uses Microsoft.Extensions.DependencyInjection.Abstractions
10.0.12 (MIT); integration tests and consumers use
Microsoft.Extensions.DependencyInjection 10.0.12 (MIT). These are separate from
the BCL-only core dependency graph.

The gRPC sample uses Grpc.AspNetCore, Grpc.Net.Client, and Grpc.Tools 2.83.0
(Apache-2.0). Grpc.Tools is a private build dependency. The ASP.NET Core and Worker
samples use the Microsoft.AspNetCore.App shared framework. Published nuspec
license expressions and repository metadata were inspected on 2026-09-12 and
recorded in `docs/host-dependency-evidence.json`. These direct-package records do
not replace the remaining complete transitive dependency and redistribution
review.

## IDE annotations

JetBrains.Annotations 2026.2.0 (MIT), copyright (c) 2016–2025 JetBrains s.r.o.,
is a private compile-only development dependency for recognizing public APIs
and framework-populated members. Its conditional attributes are not emitted
without `JETBRAINS_ANNOTATIONS`, which this repository does not define.
It adds no runtime dependency or annotation source copies. Verified package
metadata and archive hash are recorded in
[annotation dependency evidence](docs/annotation-dependency-evidence.json).
