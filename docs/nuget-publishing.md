# NuGet publishing

One managed core package contains `lib/net8.0/LoadingCache.dll` for .NET 8 and .NET 10 consumers. The optional DI package depends on the same core/version. Managed prerelease publishing is active; that is not stable 1.0 approval. AOT qualification is tracked separately.

## Release triggers and versions

The root `VERSION` file contains the official base version, initially `0.1.0`.

- Push a changed `VERSION` to `main` to publish that official version. It must increase over the previous value and must not precede existing official version tags. Adding the file starts the first release.
- Run [NuGet release](https://github.com/awe123343/LoadingCache.Net/actions/workflows/publish-nuget.yml) manually on `main` to publish the next patch with `-alpha.<run_number>.<run_attempt>`. No version input is required: base `0.1.0` produces `0.1.1-alpha.<run>.<attempt>`.
- Manual dispatch defaults to `alpha`; the explicit `official` choice retries publication of the current VERSION after inspection of any partial uploads. An existing reserved tag is accepted only for the same source SHA and only when no draft or published GitHub Release exists.
- Other pushes run correctness CI without publishing. PRs, other branches, tags and forks cannot publish.

Both paths call the same correctness, pack, package-consumer and publication jobs. The version is selected once, then carried with the immutable artifact ID. Alpha is a development snapshot newer than the base, not a compatibility promise or a release candidate for that base. Publication is serialised; running publication is never cancelled by a newer request.

The correctness matrix covers Windows, Linux and macOS 15 ARM64, running .NET 8/10 tests and smoke checks. Package consumers use an isolated NuGet cache and PackageReference only; DI resolves core transitively. Restored archives must match feed SHA-256. Preserve nupkg/snupkg and validation manifests. These checks do not replace long stress, performance, architecture or provenance qualification.

Only the publish job obtains OIDC permission, in the `nuget.org` environment. It rechecks the original repository/main/push context, downloads the already validated artifact and exchanges a short-lived credential with `NuGet/login`. It does not rebuild or persist credentials in manifests, packages or NuGet.Config.

## GitHub Releases

The same workflow publishes to [GitHub Releases](https://github.com/awe123343/LoadingCache.Net/releases) after both NuGet packages and their adjacent symbols have been submitted successfully. A separate job has `contents: write`; the NuGet job retains read-only repository access and its own OIDC permission.

The release job downloads the same immutable artifact ID and reruns the shared source/version/archive-hash verifier. It does not rebuild. Tag `v<package-version>` targets the full package source commit, never the current branch tip. The selection job reserves a lightweight tag before validation. Existing tags must point directly to the exact source SHA; mismatches and annotated tags fail. Tags are never moved. Before NuGet upload and GitHub Release creation, CI rechecks the tag and rejects any existing draft or published Release. Assets are the two nupkg files, two snupkg files and results.json. GitHub automatically supplies source ZIP/tar.gz links for the tag, so no duplicate source archive is maintained.

CI uploads assets while the release is a draft, then publishes it as a prerelease without marking it Latest for alpha versions, or as an official Latest release for stable versions. If this stage fails, NuGet uploads are not rolled back. Inspect any existing draft/tag before recovery; the workflow does not overwrite assets, move tags or blindly retry a partial publication. Rerun the failed GitHub job only after inspecting any existing draft; a matching reserved tag may remain; rerunning all jobs chooses a new alpha version, but an official version remains unchanged and must not be blindly republished.

This is **GitHub Releases**, not **GitHub Packages**. NuGet.org remains the installation feed. The user-level Packages page does not list release attachments. GitHub-hosted package restore would be a separate registry configuration and is not required to download these assets.

## Release authorisation

Only `awe123343` (GitHub account ID `8992564`) may initiate publication, including VERSION pushes and manual alpha/official dispatch. The version-selection, packaging and both publishing jobs check the original account identity and the current triggering actor. A different user rerunning an owner's workflow is rejected, including reruns of only failed publishing jobs. NuGet also rechecks the identities before requesting its OIDC credential.

These checks control execution, not visibility of GitHub's Run workflow button. They do not prevent an administrator with permission to change the workflow or repository security settings from changing the policy. Existing runs use their original workflow revision.

## Maintainer configuration

Repository: [awe123343/LoadingCache.Net](https://github.com/awe123343/LoadingCache.Net).

| Repository Actions variable | Configured value                                  |
| --------------------------- | ------------------------------------------------- |
| `NUGET_CORE_PACKAGE_ID`     | `LoadingCache.Net`                                |
| `NUGET_DI_PACKAGE_ID`       | `LoadingCache.Net.Extensions.DependencyInjection` |
| `NUGET_AUTHORS`             | `awe123343`                                       |
| `NUGET_USERNAME`            | `awe123343`                                       |

All four must exist at repository scope so pack can validate first. Missing/invalid identity fails explicitly, without invented metadata. Core/DI IDs must differ case-insensitively. Repository URL/revision come from the actual checkout/context. Package IDs do not rename namespaces/assemblies or raise the runtime minimum.

The GitHub environment is `nuget.org`, limited to the `main` deployment branch. NuGet Trusted Publishing is configured for owner `awe123343`, repository `LoadingCache.Net`, workflow filename **`publish-nuget.yml`** (not its full path) and environment **`nuget.org`**. Scope is Push new packages/versions for the exact two package IDs; publishing does not require unlist/relist authority. No long-lived API key is needed. [NuGet configuration](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Artifact checks and failure handling

- Reject missing metadata, invalid versions, ID conflicts and unexpected assets/dependencies before publishing.
- Before OIDC login, revalidate revision/version, non-local-validation mode, both package roles and four archive hashes. Publish only the manifest's artifacts from that run.
- Retain failed pack/consumer logs as diagnostics, excluding restore caches/credentials. Produce the publishable artifact only on successful validation.
- Core has no runtime package dependency. DI depends on the selected core/version and Microsoft DI abstractions. Packages contain README, Apache-2.0 licence, third-party notices and XML; symbols contain portable PDBs.
- Push core then DI. Adjacent snupkg files follow their nupkg; do not push symbols again with a second wildcard.
- Do not hide conflicts with `--skip-duplicate`. Multiple package/symbol uploads are not transactional. On partial success, preserve failure and compare feed/manifest before recovery. Rerunning only publish retains the original pack artifact/version and cannot automatically repair every partial symbol state. Rerunning all jobs creates and verifies a new attempt version, without deleting/repairing the old one. No indefinite retries.

## Local validation

Use a new output directory:

```sh
uv run --no-project python tools/pack-release.py --output artifacts/nuget/<run> --local-validation
uv run --no-project python tools/package-consumers.py \
  --feed artifacts/nuget/<run>/packages \
  --output artifacts/nuget-consumers/<run> \
  --core-id LoadingCache.LocalValidation \
  --di-id LoadingCache.Extensions.DependencyInjection.LocalValidation \
  --version 0.1.0-alpha.localvalidation
```

Optional `--runtime8-host`/`--runtime10-host` select installed hosts. Local helpers do not authenticate or upload. Local fixture identities are not release identities. Source Link URL retrieval remains a separate check.

The first public prerelease, `0.1.0-alpha.1.2`, was published by [run 35413554855, attempt 2](https://github.com/awe123343/LoadingCache.Net/actions/runs/35413554855). Both public package ZIP payloads matched the validated CI artifacts, excluding the server-added signature. Signature presence was observed; cryptographic trust was not independently checked. See [release readiness](release-readiness.md).

Actions are commit-pinned. NuGet/login v1.2.0 uses `8d196754b4036150537f80ac539e15c2f1028841` (Apache-2.0); download-artifact v8.0.1 uses `3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c` (MIT). Existing checkout/setup/upload pins and [action evidence](ci-action-evidence.json) remain authoritative. Recheck metadata/licences when updating pins; selected review is not an entire action source audit.

## Tag reservation and recovery

The selection job reserves the tag while the source is still main HEAD, before long-running verification or any upload. If main has already advanced, it fails without publishing; a race during tag creation can likewise fail safely. Once reserved, subsequent jobs use the exact tag and `gh release create --verify-tag`, omitting `--target`, so later workflow changes do not require the Release API to create an older tag. The built-in GITHUB_TOKEN remains sufficient for the intended path; no App, PAT or additional stored credential is configured.

A failed test leaves a reserved tag but no package or Release. It does not certify a successful release. Retrying the same source may reuse that tag; different source cannot. Do not delete or move published tags. Partial NuGet publication still requires explicit recovery using the original immutable artifact, never a blind full rerun.

Official 0.1.0 was recovered using the original CI artifact from run 35422080541 and source e4621223b5bd768b22cfd7a23752eefdc423f2ed. The exact tag and all five uploaded asset digests were verified before publishing the GitHub Release. NuGet was not republished. The early-reservation flow has local mocked regression coverage; a new hosted publishing run has not yet exercised it.
