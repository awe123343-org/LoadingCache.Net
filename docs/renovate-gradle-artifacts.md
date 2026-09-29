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

The local host later received HTTP 200 for the same Kotlin BOM URL. This confirms
availability from that host, not recovery of the failing Mend runner. Its egress
IP and response body were unavailable; the Mend browser log was inaccessible.
[Sonatype distinguishes 403 policy blocks from 429 rate limits](https://central.sonatype.org/faq/403-error-central/),
but the incident-specific reason for the 403 remains unconfirmed. The Gradle
child process performs these downloads, independently of Renovate's Maven
metadata cache. Reordering the same repositories cannot bypass that rejection.

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
