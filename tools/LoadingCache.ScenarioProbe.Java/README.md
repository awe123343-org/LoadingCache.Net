# Caffeine matched feature scenarios

The adapter targets Java 25 and Caffeine 3.2.4. It uses only the JDK and the
Caffeine JAR, with default executors and no added benchmark dependency.

```sh
javac --release 25 -Xlint:all -Werror -cp /path/to/caffeine-3.2.4.jar -d /private/tmp/scenario-classes tools/LoadingCache.ScenarioProbe.Java/ScenarioProbe.java
java -cp /private/tmp/scenario-classes:/path/to/caffeine-3.2.4.jar ScenarioProbe --scenario async-gated-fanin --cycles 1024 --capacity 1024 --fan-in 16 --warmups 2 --runs 3 --statistics on --output /private/tmp/scenario-java.json
```

CLI options and sample JSON field names match the .NET adapter. The JVM report
also captures every generated `ScenarioProbe*.class` hash and a SHA-256 over the
sorted JSON class-hash map, the Caffeine JAR hash and JVM arguments. Compile into
a clean directory to avoid stale nested classes changing that aggregate hash.

`maintenanceBacklog` is null because Caffeine exposes no equivalent public
counter; it is never fabricated as zero. Final `cleanUp`, size and weight checks
still run. Allocation counts cover the driver thread only, excluding background
workers; do not divide these by the .NET whole-process allocation counts.

See [the methodology](../../docs/benchmarks/caffeine-comprehensive-20260913/methodology.md)
for configurations, semantics, assertions and exclusions.
