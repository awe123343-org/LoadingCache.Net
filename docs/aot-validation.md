# Trim and Native AOT validation runner

`tools/aot-smoke.py` is a repo-local evidence runner for the two existing
consumer samples:

- `samples/LoadingCache.AotSmoke` exercises the core `LoadingCache` API.
- `samples/LoadingCache.DiAotSmoke` exercises the DI integration API.

The default matrix is eight jobs: both samples, `net8.0` and `net10.0`, and
managed trimmed plus Native AOT publish. Every publish targets `osx-arm64`,
uses `--self-contained true`, and sets the runtime framework explicitly:
`8.0.31` for `net8.0` and `10.0.12` for `net10.0`.
The SDK-bearing `dotnet` remains the build driver. For linker/compiler child
processes, the runner passes `_DotNetHostDirectory` to the matching local
runtime-only host (`artifacts/runtime8` or `artifacts/runtime10`); this keeps
the SDK `NetCoreRoot` and targeting-pack resolution unchanged while selecting
the requested servicing runtime for the child tool.

The runner requires a fresh output directory and writes complete command logs,
source manifests before and after the run, resolved project-reference input
DLL/PDB hashes captured by an MSBuild `AfterTargets="ResolveReferences"` target,
project asset and `downloadDependencies` pack evidence, output file hashes,
and execution verification. The capture target reads
`ReferenceCopyLocalPaths` after `ResolveReferences`; it does not guess a
source project's `bin` path from the consumer TFM. It deliberately does not
pass `BuildProjectReferences=false`, `--no-build`, or `--no-restore`. The
input capture is part of the publish invocation, so the recorded hashes are
not post-run guesses about a previous shared-worktree binary.

Run the complete matrix after the source revision is frozen:

```sh
uv run --no-project python tools/aot-smoke.py \
  --output artifacts/aot-smoke/$(date +%Y%m%d-%H%M%S)
```

Run one managed-trimmed net10 job while iterating on the runner:

```sh
uv run --no-project python tools/aot-smoke.py \
  --quick \
  --sample core \
  --output artifacts/aot-smoke/quick-$(date +%Y%m%d-%H%M%S)
```

The output directory is intentionally never reused. `results.json` reports
`passed` only when the publish and run exit successfully, the input capture has
both DLL and PDB hashes, the expected runtime/compiler pack evidence is found,
the primary output exists, and the consumer reports the expected framework
version, Arm64 architecture, and dynamic-code state. Native jobs pass
`--expect-native`; trimmed jobs expect dynamic code to remain supported.
The verifier accepts both consumer output forms used by the samples:
`...; Arm64; ...` and `ProcessArchitecture: Arm64`. Missing or non-Arm64
architecture, an unexpected runtime version, or a native/dynamic-code mismatch
keeps the job incomplete.

The runner uses the SDK-bearing `dotnet` command from the current environment
and records `--info` for both local runtime-only hosts. It does not install a
global SDK, runtime or tool; normal publish restore can download NuGet build
and runtime packs. Runtime-only hosts under `artifacts/`
are used only for child tools and cannot perform the publish because they
contain no SDK. Runtime pack versions are checked against the explicit
`RuntimeFrameworkVersion`; ILCompiler or ILLink evidence is collected from the
matching TFM/RID asset target and its download dependencies, and the observed
compiler version is recorded without assuming it equals the runtime version.
A publish failure, missing pack evidence, missing input capture, timeout, or
source change is recorded as a failure/incomplete result; it is never
converted into `Passed`.

The source manifest follows the correctness validator's source/build-input
scope (`.cs`, project/build files, JSON, Python and YAML) and excludes `docs/`
so a concurrent documentation ledger update does not alter the code revision
gate. It is still a fixed-revision check: any included source or build-input
change makes the run ineligible for a passing result.

These jobs validate the selected sample, TFM, RID, source revision, and
published artifact. They do not certify the entire engine, every public API,
cross-platform AOT support, package release readiness, or performance.

The completed eight-job evidence for the frozen source revision is retained at
`artifacts/aot-smoke/timeout-reviewed-2/`. The parser-only regression evidence
for the eight real run logs and four rejected synthetic cases is at
`artifacts/aot-smoke/parser-evidence-20260912/`.
