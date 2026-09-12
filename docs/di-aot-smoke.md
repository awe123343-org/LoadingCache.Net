# DI and Native AOT consumer smoke

`samples/LoadingCache.DiAotSmoke` is a small executable consumer for the
public DI integration. It targets both `net8.0` and `net10.0` and exercises
the behaviour that is easy for a host application to get wrong:

- the default and keyed (`"named"`) closed-generic registrations are separate
  DI-owned singletons;
- a different closed generic service (`ICache<int, string>`) is resolved as a
  typed registration;
- every async load creates an `AsyncServiceScope` and resolves a scoped value;
  the smoke waits for that scoped value's `DisposeAsync` before it checks the
  create/dispose counts;
- two callers share one same-key load, while cancellation of one caller only
  cancels that caller's wait;
- disposing the root `ServiceProvider` disposes the cache and rejects a later
  cache operation.

The loader receives the cache-owned cancellation token. It does not capture a
request token or a scoped service. The smoke's `TaskCompletionSource` gate is
only a deterministic ordering aid for the shared-load cancellation check.

## Build and run

The repository's `global.json` selects SDK `10.0.100`. Build each target
without rebuilding unrelated projects:

```sh
dotnet build samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net8.0
dotnet build samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net10.0
```

For a managed trimmed, self-contained consumer, publish and execute the
produced app. `PublishTrimmed` is enabled explicitly and `PublishAot` is
disabled for this check:

```sh
dotnet publish samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net8.0 -r osx-arm64 --self-contained true \
  -p:PublishAot=false -p:PublishTrimmed=true -p:TrimMode=full \
  -p:RuntimeFrameworkVersion=8.0.31 \
  -o artifacts/di-aot-smoke/trimmed-net8
artifacts/di-aot-smoke/trimmed-net8/LoadingCache.DiAotSmoke

dotnet publish samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net10.0 -r osx-arm64 --self-contained true \
  -p:PublishAot=false -p:PublishTrimmed=true -p:TrimMode=full \
  -o artifacts/di-aot-smoke/trimmed-net10
artifacts/di-aot-smoke/trimmed-net10/LoadingCache.DiAotSmoke
```

For Native AOT, publish and run the native executable with
`--expect-native`. The assertion checks
`RuntimeFeature.IsDynamicCodeSupported == false`; the executable also prints
`TargetFramework`, `FrameworkDescription`, `Environment.Version`, runtime ID,
and process architecture. `FrameworkDescription`/`Environment.Version` are
the runtime evidence: do not relabel a binary produced with one native pack as
another runtime version. In particular, an SDK `10.0.100` build using a
`net8.0` native pack must be reported with the version printed by the binary,
not called `8.0.31` unless it actually prints `8.0.31`.

```sh
dotnet publish samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net8.0 -r osx-arm64 --self-contained true \
  -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=full \
  -p:RuntimeFrameworkVersion=8.0.31 \
  -o artifacts/di-aot-smoke/native-net8
artifacts/di-aot-smoke/native-net8/LoadingCache.DiAotSmoke --expect-native

dotnet publish samples/LoadingCache.DiAotSmoke/LoadingCache.DiAotSmoke.csproj \
  -c Release -f net10.0 -r osx-arm64 --self-contained true \
  -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=full \
  -o artifacts/di-aot-smoke/native-net10
artifacts/di-aot-smoke/native-net10/LoadingCache.DiAotSmoke --expect-native
```

These commands validate this consumer, target framework, RID, and source
state only. They do not certify the complete cache engine or all release
features. When a target runtime pack or Native AOT compiler is unavailable,
record the exact SDK/pack error and leave that target `Blocked`; do not infer a
pass from another target or from `IsAotCompatible` alone.

On the current macOS arm64 toolchain, the net8 Native AOT linker can emit the
platform warning that `-ld_classic` is deprecated; the publish still exits
successfully. The warning is recorded with the command result and is not
converted into a claim about the cache engine.

## Checkpoint evidence

The reproducible checkpoint is recorded in
`artifacts/di-aot-smoke/results.json`. The publish and focused-build commands
in that manifest explicitly use `-p:BuildProjectReferences=false`. The
manifest records post-run binary observations under
`observedBinariesAfterRuns`; those hashes were not captured before publish and
are therefore non-authoritative input provenance. A clean source-integrated
rerun must capture the core and DI hashes before the first publish. This
checkpoint does not verify the current M4 engine source. The source-integrated
commands above remain the future rerun path after the engine owner provides a
compiling snapshot.

The manifest keeps compiler and runtime evidence separate. The current SDK is
`10.0.100`. For the net8 runs, the targeting reference pack is
`Microsoft.NETCore.App.Ref 8.0.22`, while the ILCompiler, Native AOT runtime
pack, and runtime pack are all `8.0.31`; the executables actually report
`.NET 8.0.31`. For net10, the corresponding compiler and runtime packs are
`10.0.0`. A `FrameworkDescription` line is runtime evidence only and is not
used to infer the compiler package version.
