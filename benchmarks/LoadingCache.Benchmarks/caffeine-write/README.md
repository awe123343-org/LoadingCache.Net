# Caffeine write benchmark

This JVM-only comparison harness uses Gradle, independently of the .NET build.
The build follows the playground conventions: Kotlin DSL, a version catalogue,
dependency locking, an Azul Java 25 toolchain, and configuration/build caches.
Java bytecode still targets 17, as it did with Maven. Caffeine 3.2.4 and JMH 1.37
remain unchanged so this migration does not silently change the baseline.

From the repository root, SDKMAN users can run `sdk env install` once, then
`sdk env`. The checked-in Gradle wrapper is authoritative; a global Gradle
installation is not required. `.sdkmanrc` selects the same Gradle and JDK family.

```sh
./gradlew :caffeine-write:build :caffeine-write:installDist
./gradlew :caffeine-write:run --args='-l'
./gradlew :caffeine-write:run --args='baseline.CaffeineWriteDrainBenchmark.putBatchAndQuiesce -p workers=1 -p writesPerWorker=512 -p capacity=1024 -p statistics=off -p valueMode=same -wi 0 -i 1 -r 100ms -f 1'
```

The last command is an execution smoke test, not performance evidence. For real
measurements, use the benchmark's normal warmup/measurement settings, record the
actual JDK and dependency versions, and retain raw JMH results. Historical
benchmark reports retain their original source/build provenance.

`installDist` produces `build/install/caffeine-write/bin/caffeine-write` beneath
this directory (and a `.bat` launcher on Windows), with runtime jars alongside
it. This replaces the Maven shaded jar without an additional packaging plugin.
To update dependencies deliberately, change `gradle/libs.versions.toml`, then
run `./gradlew :caffeine-write:build :caffeine-write:installDist --write-locks`
and review the lockfile diff. Renovate detects the catalogue and wrapper.
