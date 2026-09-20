# Formatting and unused imports

Use the SDK in `global.json`, Node in `.node-version` (`fnm use`), pnpm from
`package.json`, and `uv`/`uvx`. CSharpier 1.3.0, Ruff 0.14.0 and oxfmt 0.67.0
are pinned; prek is unpinned (`uvx prek` for formatting, `uvx prek@latest` for hook installation).
No global .NET tool installation is needed.

Kotlin files (`.kt` and `.kts`, including Gradle scripts) use the `ktlint-fmt`
hook: ktfmt followed by ktlint auto-fix, matching the playground style in
`.editorconfig`. Versions live in `gradle/libs.versions.toml`; the hook downloads
the tools into ignored `bin` on first use. Use the JDK selected by
`.sdkmanrc`; CI installs Azul Java 25. Run it directly with
`uvx prek@latest run ktlint-fmt --all-files`.

```sh
make install-tools
make install-hooks
make format-pre-commit   # Manual-stage hooks on all tracked files
make format-dotnet-style # Optional semantic import cleanup
make format             # Alias for format-pre-commit
bash pre-commit.sh      # All hooks once; fails on errors or tracked changes vs HEAD
```

The Kotlin script follows playground and stages its input files
with `git add`. Other fixers leave changes unstaged. Review their edits, then rerun the
command; prek reports a nonzero exit status when a hook changes files.
The wrapper shows the initial unstaged diff, then the full diff against HEAD on
failure. Hook-level diff output is disabled to avoid another duplicate.
Existing Git hook settings are not reset by the installer. On Windows, run the shell
entry points through Git Bash with Make available.

## Responsibilities

`dotnet-tool-restore` always runs during `pre-commit`, `pre-push`, `post-checkout`,
`post-rewrite` and `manual`, regardless of changed files. Other hooks retain their
configured stages. This does not install additional Git hooks.
There is no `default_stages` restriction, so general hooks also run in the manual
stage. The expensive `dotnet-format-style` hook remains manual-only.

Ruff uses the standalone `ruff.toml` because Python is used for repository tools,
not a separately managed Python project. A `pyproject.toml` with `[tool.ruff]` would
support the same settings; it is not required for these scripts. Keep one source
of configuration. If Python project metadata or shared Python-tool configuration
becomes necessary, migrate the settings together rather than maintaining both files.
See [Ruff configuration](https://docs.astral.sh/ruff/configuration/).

| Tool                  | Responsibility                                                          | When                                        |
| --------------------- | ----------------------------------------------------------------------- | ------------------------------------------- |
| CSharpier             | C# and .NET XML whitespace/layout                                       | Pre-commit and `format` CI job              |
| Ruff                  | Python formatting, import order and basic lint                          | Pre-commit and `format` CI job              |
| oxfmt                 | JSON, YAML, TOML, Markdown and supported web files                      | Pre-commit and `format` CI job              |
| prek built-ins        | EOF, BOM, line endings, whitespace, merge conflicts, YAML/TOML validity | Pre-commit and `format` CI job              |
| Roslyn IDE0005        | Semantic detection of unnecessary C# imports                            | Existing correctness build on Windows/Linux |
| `dotnet format style` | Automatic removal, restricted to IDE0005                                | Manual only                                 |

Generated output, dependency directories and raw research/benchmark/simulator
evidence are excluded from formatting. Maintained documentation stays included.
Markdown hard line breaks are preserved. No separate Markdown linter or
pyupgrade is needed for this initial setup.

## Why detection runs in the existing build

We intentionally leave automatic unused-usings checks to CI rather than local
Git hooks: full-solution semantic analysis takes about 11 seconds on the measured
macOS ARM64 host, which is too slow for every commit. Local checks and cleanup
remain available through the manual-stage commands below. Normal local builds
also enforce IDE0005 through the shared build settings.

`EnforceCodeStyleInBuild` and an explicit IDE0005 severity let Roslyn report
unused imports during the compilation CI already performs. Warnings-as-errors
makes the check fail. CSharpier remains responsible for whitespace. The `format`
CI job runs all hooks once through the manual stage in `pre-commit.sh`.
There is only one IDE0005 hook: `dotnet-format-style`. It removes unused usings
in both CI and local manual runs; the final `git diff HEAD` check in
`pre-commit.sh` fails if tracked files changed. The correctness build retains its normal
IDE0005 enforcement; there is no second dedicated import-check hook.

IDE0005 requires XML documentation processing. The three projects that previously
disabled it (core tests, DI tests and the gRPC sample) now enable it and suppress
only CS1591, preserving their existing policy of not requiring XML docs for test
and sample APIs. Library documentation and other warnings remain enforced.
See [Microsoft's IDE0005 contract](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0005)
and [SDK build analysis settings](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#enforcecodestyleinbuild).

Manual cleanup restores the solution, loads semantic project information and
selects only IDE0005. It prints the SDK version and verbosity (`normal` by default,
`diagnostic` when `CI=true`). Override with `DOTNET_FORMAT_VERBOSITY` as needed.
No report file is generated. The hook always processes the whole solution:

```sh
make format-dotnet-style
prek run dotnet-format-style --hook-stage manual --all-files
# Limit edits when working on a few files:
dotnet format style LoadingCache.slnx --diagnostics IDE0005 --severity info \
  --include src/LoadingCache/Ownership/ValueOwnership.cs
```

The semantic cleanup hook remains manual-only because of its cost.
Earlier measurements on macOS ARM64 with SDK 10.0.401 took 11.34 s for the direct
check and 11.38 s through the former separate check hook
(2026-09-20, existing restore caches, one run each).
This is too much overhead for each commit; the existing build/CI check remains
enabled. These local measurements are not a cross-platform performance guarantee.

Import usage depends on target framework, preprocessor symbols, referenced
assemblies, extension methods and XML references. Keep the normal multi-target
build after automatic cleanup. The fixture below covers both configured targets;
it does not establish correctness for every possible conditional build.

## Local evidence: 2026-09-13

SDK 10.0.100 on macOS ARM64. Three interleaved runs after warmup, using an
isolated core snapshot and separate build output:

| Command                                                                 | Median wall time |
| ----------------------------------------------------------------------- | ---------------: |
| Core Release rebuild, style analysis off                                |          1.011 s |
| Same rebuild, style analysis on                                         |          1.262 s |
| Separate IDE0005 `dotnet format style --verify-no-changes --no-restore` |          2.946 s |

This small local comparison supports using the existing build: about 0.25 s
additional median build time instead of an extra roughly 3 s formatter process.
It is not a whole-solution or stable-runner performance budget; the host also
had other work running. No claim of zero overhead or universal speedup is made.

A controlled fixture verified detection/removal of unused normal and global
usings and redundant implicit imports. It preserved aliases, `using static`,
XML `cref` imports and target-specific aliases. Clean builds passed for both
net8.0 and net10.0. The isolated full solution also built after IDE0005 cleanup.

Both hook stages and the CI entry script passed on a normalized isolated
repository. A path containing spaces formatted successfully while its staged
blob remained unchanged. Four Python package-tool tests passed. Existing idle
documents and Python tools received their initial formatting normalisation;
documents being edited by the concurrent cache-engine task were left intact.
Run `make format-pre-commit` after those edits settle to normalize them too.

Commands, timings and fixture sources are retained locally under
`artifacts/validation/format-setup/` (`probe.py`, `core-benchmark.py`,
`validate-snapshot.py` and their logs). GitHub-hosted execution remains to be
verified after the workflow is committed and pushed by a maintainer.
