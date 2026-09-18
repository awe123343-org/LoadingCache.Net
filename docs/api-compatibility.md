# Public API and behaviour baseline

The opt-in admission change makes `LoadingCacheOptions.MaxConcurrentLoads`
from `int` to `int?` (ADR0015). This is a binary API change and can also require
source changes where consumers read the property. Initializing a positive integer
remains supported. On 2026-09-18 the compiler regenerated the baseline: the only
delta is five getter/property signature lines changing to `int?`; the DI baseline
is unchanged. The rebuilt reference assemblies match in both the isolated candidate
and original checkout. Both sets of consumers passed on .NET 8.0.31 and 10.0.12;
original-checkout verification is recorded separately under
`artifacts/opt-in-load-limits-20260918/integration01/validation/`.

The release candidate's public surface is captured from the compiler-produced
`net8.0` reference assemblies for both core and DI. The two reviewable files are
`tools/LoadingCache.ApiBaseline/baselines/LoadingCache.api.txt` and
`tools/LoadingCache.ApiBaseline/baselines/LoadingCache.Extensions.DependencyInjection.api.txt`.
The initial snapshot and eleven negative controls passed on 2026-09-16; raw records
are `artifacts/v1-acceptance-20260916/api-write-reviewed.json` and `api-self-test-reviewed.log`.
A matching build and `check` are required after each API change; writing a snapshot
is not itself approval of every API or completion of the release gate.

## Mechanical check

`LoadingCache.ApiBaseline` uses Roslyn's metadata importer and symbol formatter
from the installed SDK selected by `global.json`. It does not load the library
for execution, hand-decode signatures, or add a dependency to either shipped
package. The SDK inspected for this implementation is 10.0.100; its root
`LICENSE.txt` is MIT, copyright .NET Foundation and Contributors. No new package
or global tool is installed. The check records the actual compiler version/hash,
reference assembly hashes, input argv, baseline hashes and match result.

The snapshot covers externally visible types, public/protected members,
constructors and accessors, base classes, implemented interfaces, generic
variance/constraints, nullable annotations, parameter names/default values,
constants and contract attributes (including parameter and return attributes).
Compiler metadata for nullable context is represented by decoded signatures;
implementation-only state-machine/compiler-generated attributes are excluded.
Internal/private implementation changes are outside the baseline. Any addition,
removal or alteration fails exact comparison until reviewed; `write` is an
explicit update command, never an automatic part of `check`.

The tool reads already-built reference assemblies; first build the exact candidate
and tool with the repository SDK. Pass the framework references used by that build
and DI abstractions explicitly. The inspected core `obj/project.assets.json`
resolves `Microsoft.NETCore.App.Ref/8.0.22`; the initial metadata import used that
pack and DI abstractions 10.0.12's `net8.0` assembly. This is the compile-time
reference surface, distinct from the separately tested runtime 8.0.31. Reading
8.0.22 reference metadata does not execute or claim runtime testing on 8.0.22,
nor justify silently changing a future build's resolved reference pack. On
another machine or after a restore change, read that build's resolved assets
and supply the corresponding paths; record their hashes with the check:

```sh
dotnet build tools/LoadingCache.ApiBaseline/LoadingCache.ApiBaseline.csproj -c Release
dotnet tools/LoadingCache.ApiBaseline/bin/Release/net10.0/LoadingCache.ApiBaseline.dll self-test \
  --reference-directory "$HOME/.nuget/packages/microsoft.netcore.app.ref/8.0.22/ref/net8.0"
dotnet tools/LoadingCache.ApiBaseline/bin/Release/net10.0/LoadingCache.ApiBaseline.dll check \
  --reference-directory "$HOME/.nuget/packages/microsoft.netcore.app.ref/8.0.22/ref/net8.0" \
  --reference "$HOME/.nuget/packages/microsoft.extensions.dependencyinjection.abstractions/10.0.12/lib/net8.0/Microsoft.Extensions.DependencyInjection.Abstractions.dll" \
  --assembly src/LoadingCache/obj/Release/net8.0/ref/LoadingCache.dll \
  --assembly src/LoadingCache.Extensions.DependencyInjection/obj/Release/net8.0/ref/LoadingCache.Extensions.DependencyInjection.dll \
  --baseline-directory tools/LoadingCache.ApiBaseline/baselines \
  --evidence artifacts/api-baseline/check.json
```

For the initial reviewed snapshot, replace `check` with `write`, then run `check`.
The self-test emits small real assemblies and confirms detection of nullable,
optional-default, generic-constraint, parameter-name, accessor visibility,
flow-annotation, method visibility, sealed/abstract type and init-only changes; it confirms a private method-body
change is ignored. This validates the emitter's advertised boundaries, not the
cache implementation. A baseline mismatch prints removed/added signatures and
exits nonzero. Unavailable direct assembly references fail instead of silently
falling back to the host's framework.

This is a strict API-surface review gate, not proof that every unchanged signature
is behaviorally or binary compatible. After a published stable package exists,
also compare packed assets against that real release with the SDK's package/API
compatibility validation. No fictional previously published baseline is used.

## Behaviour and versioning policy

While experimental, review every intentional API or behaviour change and update
the related documentation, regression and baseline together. Before 1.0,
freeze the candidate surface and record the final-source test results.
Starting with 1.0, breaking documented source/binary contracts or behaviour needs
a major version; compatible API additions use minor versions, and fixes that
preserve those contracts use patch versions. An internal performance optimisation
does not authorize changing expiration, cancellation, identity or ownership.

Existing behaviour acceptance remains authoritative:

| Contract                                                          | Documentation and representative regressions                                                                                                                                                  |
| ----------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Four personalities, options, null/loader behaviour                | `docs/semantics.md`; `LoadingCacheContractTests`, `EnginePersonalityTests`                                                                                                                    |
| Caller-only cancellation, single flight, stale completion fencing | `docs/concurrency.md`; contract/publication/flight-finalization regression fixtures                                                                                                           |
| Expiration, refresh, loader/maintenance bounds, shutdown          | `docs/semantics.md`, `docs/resource-model.md`; fixed/variable expiry, refresh, timeout and maintenance regressions                                                                            |
| Package-facing supported use                                      | `tests/LoadingCache.PackageTests/Program.cs`, linked `LoaderContractSmoke.cs` and `NativePolicySmoke.cs`; four personalities, weak references, bulk, metrics, ownership and policy operations |
| DI and AOT                                                        | DI consumer and `samples/LoadingCache.DiAotSmoke`; final candidate package/trim/native runs on each claimed platform                                                                          |

Passing the API check cannot substitute for these tests or the other
[V1 release gates](v1-performance-gates.md). Runtime and platform evidence must
remain tied to the exact candidate.
