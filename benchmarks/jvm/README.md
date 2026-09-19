# JVM comparison harnesses

One Kotlin module provides Caffeine and Guava comparisons independently of the
.NET build. `Main.kt` dispatches the first argument with `when` and forwards the
remaining arguments unchanged. Hit probes share coordination and reporting; their\ncache lookups are inlined into engine-specific worker loops.

The Gradle wrapper, version catalogue and dependency lockfile pin the build.
All harnesses target Azul Java 25. Kapt generates the JMH benchmark classes.
SDKMAN users can run `sdk env install`, then `sdk env`, from the repository root.

```sh
./gradlew :jvm-benchmarks:build :jvm-benchmarks:installDist
./gradlew :jvm-benchmarks:run --args='--help'
```

The distribution launcher is
`benchmarks/jvm/build/install/jvm-benchmarks/bin/jvm-benchmarks` (`.bat` on Windows).
Run these examples from the repository root:

```sh
./gradlew :jvm-benchmarks:run --args='caffeine-hit --capacity 1024 --residents 512 --pattern cycle --workers 4 --statistics on --warmups 1 --runs 2 --duration-ms 50'
./gradlew :jvm-benchmarks:run --args='guava-hit --capacity 1024 --residents 512 --pattern cycle --workers 4 --statistics on --warmups 1 --runs 2 --duration-ms 50'
./gradlew :jvm-benchmarks:run --args='caffeine-scenarios --scenario all --cycles 8 --capacity 64 --fan-in 4 --warmups 0 --runs 1 --statistics on'
./gradlew :jvm-benchmarks:run --args='caffeine-write baseline.CaffeineWriteDrainBenchmark.putBatchAndQuiesce -p workers=1 -p writesPerWorker=512 -p capacity=1024 -p statistics=off -p valueMode=same -wi 0 -i 1 -r 100ms -f 1'
```

These are execution checks, not performance evidence. Historical benchmark
archives retain their original Java source/build provenance. Kotlin and the shared
classpath require fresh measurements; do not relabel historical results.

Hit probes retain preboxed keys, persistent workers, checksum/count/statistics
checks and JSON output. Guava defaults to concurrency level 4; use
`--concurrency-level 1` when testing full residency without segment imbalance.
Hit allocation counts cover dedicated reader threads only. Scenario allocation
counts cover the driver thread only. Neither is a whole-process allocation measure.

Scenario reports include generated `ScenarioProbe*.class` hashes and the Caffeine
JAR hash. Record all distribution JAR hashes, JVM arguments, actual JDK and flags
with fresh measurements. `maintenanceBacklog` remains null for Caffeine because
it has no equivalent public counter. Write batches include worker coordination
and cleanup until quiescent, rather than only enqueue time.

See the [read methodology](../../docs/benchmarks/caffeine-read-20260913/methodology.md)
and [scenario methodology](../../docs/benchmarks/caffeine-comprehensive-20260913/methodology.md).
To update dependencies, edit `gradle/libs.versions.toml`, run
`./gradlew :jvm-benchmarks:build :jvm-benchmarks:installDist --write-locks`, and review
the lockfile diff. Formatting uses the existing ktfmt/ktlint hook.
