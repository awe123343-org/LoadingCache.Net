# Guava resident-read probe

This benchmark-only adapter retains the persistent platform workers, three barriers,
1,024-operation chunks, preallocated keys, checksum, zero-miss checks, stats checks,
watchdog, and raw sample schema of `../LoadingCache.HitProbe.Java`. Only the cache
API/configuration and artifact provenance differ. It adds no production dependency.

The pinned artifact is **Guava 33.7.1-jre**, source revision
`c5b5a383a1f7f4a84c17de910f61181011c95908`. Record its JAR SHA-256, JDK,
compiler command, dependency hashes, and class-file hashes with each benchmark run.
The pinned runtime dependency is `failureaccess:1.0.3`; compilation additionally
uses `error_prone_annotations:2.50.0`, as specified by Guava's parent POM.
`GUAVA_CLASSPATH` below includes Guava and failureaccess; `GUAVA_ANNOTATIONS` is the
annotation JAR path, needed only at compile time.

```sh
javac --release 25 -Xlint:all -Werror -cp "$GUAVA_CLASSPATH:$GUAVA_ANNOTATIONS" -d "$GUAVA_CLASSES" \
  tools/LoadingCache.HitProbe.Guava/src/main/java/baseline/GuavaHitProbe.java
java -cp "$GUAVA_CLASSES:$GUAVA_CLASSPATH" baseline.GuavaHitProbe \
  --capacity 1024 --residents 1024 --concurrency-level 1 \
  --pattern cycle --workers 10 --statistics off \
  --warmups 3 --runs 5 --duration-ms 500 --output result.json
```

`--concurrency-level` defaults to **4** and is always recorded. This is a Guava
configuration hint, not the reader thread count. Guava distributes the maximum
across segments: with multiple segments, populating exactly `capacity` consecutive
integer keys can evict values before the probe starts. The probe deliberately
fails if resident count differs from the requested count; it never enlarges the
capacity, changes keys, or accepts misses to make a case pass.

Compare level 1 with identical fully resident cases as an explicitly configured
variant. For the default-level comparison, run all engines with the same capacity
and a shared resident set that fits, such as half capacity, retaining setup and
zero-miss assertions. Never label level-1 results as Guava defaults or compare
different resident sets without stating the difference.

`weightedSize` is the resident count under this unit-weight `maximumSize` setup;
Guava does not supply Caffeine's policy inspection API. Allocation counts cover
reader threads only. The .NET harness's process-wide allocation count has a wider
scope, so these values are not equivalent measurements.

This is a closed-loop throughput probe. Wall nanoseconds per aggregate operation
are reciprocal throughput, not request latency or p99. Timed processes run serially
without concurrent task-owned builds, tests, or profilers.
