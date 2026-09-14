# Caffeine read-hit probe

This standalone probe implements the matching contract in
`docs/benchmarks/caffeine-read-20260913/methodology.md`. It has no Maven or JMH dependency:
the harness uses Caffeine 3.2.4 from the local Maven cache, pinned at
`836b65c0a83e5d1641ded9c6de578654bc04b2e9`.

Build it with the pinned Java 25 runtime and the existing Caffeine jar:

```sh
HIT_JAVA_HOME=/Library/Java/JavaVirtualMachines/zulu-25.jdk/Contents/Home
CAFFEINE_JAR="$HOME/.m2/repository/com/github/ben-manes/caffeine/caffeine/3.2.4/caffeine-3.2.4.jar"
HIT_CLASSES=$(mktemp -d "${TMPDIR:-/tmp}/loadingcache-hitprobe-java.XXXXXX")
"$HIT_JAVA_HOME/bin/javac" --release 25 -cp "$CAFFEINE_JAR" \
  -d "$HIT_CLASSES" \
  tools/LoadingCache.HitProbe.Java/src/main/java/baseline/CaffeineHitProbe.java
```

Run one smoke case:

```sh
"$HIT_JAVA_HOME/bin/java" -cp \
  "$HIT_CLASSES:$CAFFEINE_JAR" \
  baseline.CaffeineHitProbe \
  --capacity 1024 --residents 1024 --pattern cycle --workers 4 \
  --statistics on --warmups 1 --runs 2 --duration-ms 50 \
  --output /private/tmp/loadingcache-hitprobe-java.json
```

The JSON report includes the exact case flags, runtime/GC metadata, Caffeine JAR path and
SHA-256, raw warmup samples, per-worker operation/checksum data, cleanup time, statistics deltas,
and validation status. Java allocation data is explicitly limited to live dedicated reader
threads; it excludes the main thread and Caffeine maintenance/executor threads, so it is not a
process-wide allocation measurement.
