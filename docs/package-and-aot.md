# Package and Native AOT validation

The initial distribution route is managed NuGet: one net8.0 asset supports .NET 8/10, verified by actual-runtime consumers. Native AOT qualification is separate and does not block that initial prerelease route; untested RIDs are not implied support. See [publishing](nuget-publishing.md).

## Package-only consumers

```sh
uv run --no-project python tools/pack-release.py --local-validation --output artifacts/package/<unique-run>
uv run --no-project python tools/package-consumers.py \
  --feed artifacts/package/<unique-run>/packages \
  --output artifacts/package/<unique-run>-consumers \
  --core-id LoadingCache.LocalValidation \
  --di-id LoadingCache.Extensions.DependencyInjection.LocalValidation \
  --version 0.1.0-alpha.localvalidation
```

This uses the same pack inspection and core/DI consumer flow as CI, with a new local feed/cache and explicit local-validation IDs/authorship. Nothing is uploaded and shared NuGet configuration is unchanged. Consumers use PackageReference, check actual resolution and archive hashes, including transitive core resolution, and execute on both runtimes. Package inspection checks net8 DLL/XML, README, licence/notices, portable-PDB symbols and absence of core runtime dependencies. Preserve commands, exits, nuspecs and source/archive hashes; changed inputs invalidate fixed-source claims. See the publishing guide for release metadata.

## Trim and native consumers

```sh
uv run --no-project python tools/aot-smoke.py --output artifacts/aot-smoke/<unique-run>
```

The complete runner pins servicing packs and captures core/DI input DLL/PDB and output evidence. Simple direct commands use SDK-selected packs and do not replace that provenance:

```sh
dotnet publish samples/LoadingCache.AotSmoke/LoadingCache.AotSmoke.csproj -c Release -f net8.0 -r osx-arm64 -o artifacts/aot/net8
artifacts/aot/net8/LoadingCache.AotSmoke --expect-native
dotnet publish samples/LoadingCache.AotSmoke/LoadingCache.AotSmoke.csproj -c Release -f net10.0 -r osx-arm64 -o artifacts/aot/net10
artifacts/aot/net10/LoadingCache.AotSmoke --expect-native
```

Native smoke checks dynamic code is unavailable and exercises public personality, generic/comparer, bulk-present, weight, cancellation and publication APIs. A managed trimmed run uses `-p:PublishAot=false -p:PublishTrimmed=true --self-contained true`. Analyzers/`IsAotCompatible` are not substitutes for publish **and run**, nor does a sample verify every feature combination.

On 18 September, current opt-in source passed four core/DI PackageReference consumers and eight core/DI × .NET 8/10 × trim/native jobs on macOS ARM64, actual runtimes 8.0.31/10.0.12. Source/archive/reference/output checks passed. Two net8 `-ld_classic` deprecation notices remain unsuppressed. The sequential runner overwrote six earlier full assets JSON files; captured hashes, parsed pack records and emitted/reference bytes remain, but complete frozen-input replay for those six jobs is not claimed. Local evidence is under `artifacts/opt-in-load-limits-20260918/package-validation01/` and `package-aot-review01/`; these are not public download links.

Hosted Windows/Linux managed package consumers now pass separately. Source Link is configured with deterministic builds/portable symbols, but actual remote source retrieval still requires verification against a published revision. See [AOT method](aot-validation.md), [DI AOT](di-aot-smoke.md) and [release readiness](release-readiness.md) for source-specific evidence and limits.
