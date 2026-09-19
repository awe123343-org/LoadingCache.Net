# Package and Native AOT validation

The initial distribution route is managed NuGet: one net8.0 asset supports .NET 8/10, verified by actual-runtime consumers. Native AOT qualification is separate and does not block that initial prerelease route; untested RIDs are not implied support. See [publishing](nuget-publishing.md).

## Package-only consumers

```sh
uv run --no-project python tools/package-smoke.py --output artifacts/package/<unique-run>
```

This creates a new local feed/cache and explicit local-validation IDs/authorship, without uploading or changing shared NuGet configuration. Consumers use PackageReference, check actual resolution and execute on both runtimes. Inspect net8 DLL/XML, README, licence/notices, portable-PDB symbols and absence of core runtime dependencies. Preserve commands, exits, nuspecs and source/archive hashes; changed inputs invalidate fixed-source claims. The release helper additionally validates core/DI together, including transitive core resolution; see the publishing guide.

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
