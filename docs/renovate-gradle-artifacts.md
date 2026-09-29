# Recovering Renovate Gradle artifacts

## Why Guava PR #29 had no lock update

The [Renovate artifact failure](https://github.com/awe123343-org/LoadingCache.Net/pull/29#issuecomment-5898184204)
occurred while `./gradlew ... -q properties` configured Kotlin Gradle plugin
`2.4.20`: Maven Central returned HTTP 403 for the plugin and its BOM. The failure
preceded lock generation. Renovate committed Guava BOM `33.7.2-jre` in the catalog
with the existing strict `33.7.1-jre` lock, so JVM CI failed dependency resolution.

[Renovate 44.112.0](https://github.com/renovatebot/renovate/blob/44.112.0/lib/modules/manager/gradle/artifacts.ts#L230-L290)
runs `properties` before its resolution task. Normal dependency updates use
`--update-locks` for the updated coordinates; lockfile maintenance uses
`--write-locks`. Enabling maintenance would not repair this earlier failure.

The [original Mend job](https://developer.mend.io/github/awe123343-org/LoadingCache.Net/-/job/01a0edf9-b324-7c5d-8111-815ac529b6f7)
provides the server response. At `2026-09-29 20:26:38 UTC`, the Caffeine metadata
GET to Maven Central returned 403 with the message:

> This IP has been blocked for excessive or automated consumption of Maven Central

The response identifies `cf-ray: a42db9589d1fd69d-IAD`, and Renovate logs a fallback
to stale cached metadata. This confirms an IP block on that Mend request. The
same job's Gradle child then received 403 for Kotlin plugin/BOM downloads from
the same host, consistent with that block. The child response body and actual
egress IP are not logged, so the exact traffic or tenant responsible is unknown.
A later local GET returning 200 proves availability from the local host, not that
Mend access recovered. Renovate's metadata cache does not supply Gradle artifacts.
[Sonatype's guidance](https://central.sonatype.org/faq/403-error-central/) calls for
provider-side investigation of the blocked egress; a repository rebase cannot
remove that block.

The job also records no updated lock files and creation with artifact errors.
[Renovate's two-hour gate](https://github.com/renovatebot/renovate/blob/44.112.0/lib/workers/repository/update/branch/index.ts#L709-L728)
uses the dependency `releaseTimestamp`, not the PR creation time. That threshold
had elapsed, so Renovate created the catalog-only PR and set `renovate/artifacts`
red. The inaccurate log wording about the PR being older than two hours does not
mean this PR already existed for that long.

## Recovery policy

The global `rebaseWhen: conflicted` policy can reuse an unconflicted bot-owned
branch after its base advances. With a matching fingerprint, Renovate then
[skips package and artifact updates](https://github.com/renovatebot/renovate/blob/44.112.0/lib/workers/repository/update/branch/index.ts#L625-L629).
The `gradle` and `gradle-wrapper` rule instead selects `behind-base-branch`,
[rebuilding stale bot-owned branches](https://github.com/renovatebot/renovate/blob/44.112.0/lib/workers/repository/update/branch/reuse.ts#L45-L65)
so the normal artifact pipeline gets another attempt. Other managers retain the global policy.

This also rebases healthy Gradle branches. It takes effect after the rule reaches
the base branch and Renovate next runs; it does not schedule a retry when the base
is unchanged, remove the 403, or guarantee successful artifacts. Renovate
preserves branches it identifies as human-modified; manual amendments may trigger that protection.
Hosted execution of this policy has not yet been verified.

## Recover a failed update

1. Select the retry/rebase checkbox in Renovate's PR body, when present, and let
   Renovate rerun. Restarting GitHub CI alone cannot update locks.
2. If manual recovery is needed, check out the dependency PR and regenerate its
   locks with the repository's Java 25 toolchain, then verify ordinary locked resolution:

    ```sh
    ./gradlew :jvm-benchmarks:build :jvm-benchmarks:installDist --write-locks
    git diff -- benchmarks/jvm/gradle.lockfile settings-gradle.lockfile
    ./gradlew build installDist
    ```

3. Review the lock diff and run the JVM smoke commands from
   [the benchmark README](../benchmarks/jvm/README.md) before committing it.
   Keep dependency locking enabled in CI; do not add arbitrary mirrors to mask
   the download failure.

Local incident evidence on 2026-09-29: original PR head `009f8e0` reproduced the
lock conflict. Amended head `a495cef` passed the locked build/install, JMH smoke,
Caffeine/Guava hit probes and all 41 scenarios, and received approval. All six
[hosted CI jobs](https://github.com/awe123343-org/LoadingCache.Net/actions/runs/36629760567)
then passed, including Windows/Linux/macOS correctness and JVM smoke.
