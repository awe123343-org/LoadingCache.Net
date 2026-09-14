package baseline;

import com.github.benmanes.caffeine.cache.Cache;
import com.github.benmanes.caffeine.cache.Caffeine;
import com.github.benmanes.caffeine.cache.stats.CacheStats;
import com.sun.management.ThreadMXBean;
import java.io.IOException;
import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.OptionalLong;
import java.util.concurrent.BrokenBarrierException;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/**
 * Closed-loop read-hit probe for a size-bounded Caffeine cache.
 *
 * <p>This intentionally has no JMH dependency. The parent benchmark launches one fresh process
 * for each case and supplies the exact case flags described in
 * {@code docs/benchmarks/caffeine-read-20260913/methodology.md}.
 */
public final class CaffeineHitProbe {
  private static final int SCHEMA_VERSION = 1;
  private static final int DEFAULT_CAPACITY = 1_024;
  private static final int DEFAULT_RESIDENTS = 1_024;
  private static final int DEFAULT_WORKERS = 1;
  private static final int DEFAULT_WARMUPS = 3;
  private static final int DEFAULT_RUNS = 5;
  private static final long DEFAULT_DURATION_MILLIS = 500;
  private static final int CHUNK_SIZE = 1_024;
  private static final int START_STRIDE = 17;
  private static final long WATCHDOG_SECONDS = 30;
  private static final String CAFFEINE_VERSION = "3.2.4";

  private CaffeineHitProbe() {}

  public static void main(String[] args) {
    int exitCode = run(args);
    if (exitCode != 0) {
      System.exit(exitCode);
    }
  }

  private static int run(String[] args) {
    Options options;
    try {
      options = Options.parse(args);
    } catch (IllegalArgumentException exception) {
      System.err.println("error: " + exception.getMessage());
      System.err.println(Options.usage());
      return 2;
    }

    List<Sample> samples = new ArrayList<>();
    Probe probe = null;
    Throwable failure = null;
    try {
      probe = new Probe(options);
      int sampleCount = options.warmups + options.runs;
      for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++) {
        boolean warmup = sampleIndex < options.warmups;
        Sample sample = probe.runSample(sampleIndex, warmup);
        samples.add(sample);
        if (!sample.boundsPassed) {
          throw new IllegalStateException(
              "validation failed for sample " + sampleIndex + ": " + sample.validationFailure);
        }
      }
    } catch (Throwable exception) {
      failure = exception;
    } finally {
      if (probe != null) {
        try {
          probe.close();
        } catch (Throwable exception) {
          if (failure == null) {
            failure = exception;
          } else {
            failure.addSuppressed(exception);
          }
        }
      }
    }

    String report = ReportWriter.write(options, samples, failure);
    try {
      if (options.output == null) {
        System.out.println(report);
      } else {
        Path output = options.output.toAbsolutePath();
        Path parent = output.getParent();
        if (parent != null) {
          Files.createDirectories(parent);
        }
        Files.writeString(output, report + System.lineSeparator(), StandardCharsets.UTF_8);
        System.err.println("wrote " + output);
      }
    } catch (IOException exception) {
      System.err.println("error writing report: " + exception.getMessage());
      return 1;
    }

    if (failure != null) {
      failure.printStackTrace(System.err);
      return 1;
    }
    return 0;
  }

  private static final class Probe implements AutoCloseable {
    private final Options options;
    private final Cache<Integer, Integer> cache;
    private final Integer[] keys;
    private final PersistentWorkers workers;
    private final List<String> gcCollectorNames;

    Probe(Options options) {
      this.options = options;
      Caffeine<Object, Object> builder = Caffeine.newBuilder();
      builder.maximumSize(options.capacity);
      if (options.statistics) {
        builder.recordStats();
      }
      cache = builder.build();
      keys = new Integer[options.residents];
      for (int index = 0; index < keys.length; index++) {
        keys[index] = index;
        cache.put(keys[index], index + 1);
      }
      cache.cleanUp();
      long residentCount = cache.asMap().size();
      if (residentCount != options.residents) {
        throw new IllegalStateException(
            "initial resident count mismatch: expected "
                + options.residents
                + ", actual "
                + residentCount);
      }
      long weightedSize = weightedSize(cache);
      if (weightedSize != options.residents) {
        throw new IllegalStateException(
            "initial weighted size mismatch: expected "
                + options.residents
                + ", actual "
                + weightedSize);
      }
      workers = new PersistentWorkers(cache, keys, options);
      gcCollectorNames = new ArrayList<>();
      for (GarbageCollectorMXBean collector : ManagementFactory.getGarbageCollectorMXBeans()) {
        gcCollectorNames.add(collector.getName());
      }
    }

    Sample runSample(int sampleIndex, boolean warmup) {
      CacheStats beforeStats = cache.stats();
      GcCounts beforeGc = GcCounts.capture(gcCollectorNames);
      TimedResult timed = workers.runIteration(options.pattern.equals("hot") ? 0 : options.residents - 1);
      CacheStats afterStats = cache.stats();

      long cleanupStarted = System.nanoTime();
      cache.cleanUp();
      double cleanupSeconds = elapsedSeconds(cleanupStarted, System.nanoTime());
      long residentCount = cache.asMap().size();
      long weightedSize = weightedSize(cache);
      GcCounts gcDelta = GcCounts.capture(gcCollectorNames).minus(beforeGc);

      long operations = 0;
      long checksum = 0;
      long misses = 0;
      long allocatedBytes = 0;
      boolean allocationMeasured = true;
      long[] workersOperations = new long[options.workers];
      for (int worker = 0; worker < options.workers; worker++) {
        WorkerResult result = timed.workerResults[worker];
        operations += result.operations;
        checksum += result.checksum;
        misses += result.misses;
        if (result.allocatedBytes < 0) {
          allocationMeasured = false;
        } else {
          allocatedBytes += result.allocatedBytes;
        }
        workersOperations[worker] = result.operations;
      }
      if (!allocationMeasured) {
        allocatedBytes = -1;
      }

      long expectedChecksum = 0;
      boolean checksumPassed = true;
      boolean operationsPassed = true;
      for (int worker = 0; worker < options.workers; worker++) {
        WorkerResult result = timed.workerResults[worker];
        long expected = expectedChecksum(worker, result.operations, timed.mask, options.residents);
        expectedChecksum += expected;
        checksumPassed &= result.checksum == expected;
        operationsPassed &= result.operations > 0;
      }
      long hitsDelta = afterStats.hitCount() - beforeStats.hitCount();
      long missesDelta = afterStats.missCount() - beforeStats.missCount();
      boolean statsPassed = options.statistics
          ? hitsDelta == operations && missesDelta == 0
          : hitsDelta == 0 && missesDelta == 0;
      boolean boundsPassed = checksumPassed
          && checksum == expectedChecksum
          && operationsPassed
          && misses == 0
          && residentCount == options.residents
          && weightedSize == options.residents
          && statsPassed;
      String validationFailure = null;
      if (!checksumPassed || checksum != expectedChecksum) {
        validationFailure = "checksum mismatch";
      } else if (!operationsPassed) {
        validationFailure = "worker completed zero operations";
      } else if (misses != 0) {
        validationFailure = "lookup misses=" + misses;
      } else if (residentCount != options.residents || weightedSize != options.residents) {
        validationFailure =
            "resident/weighted bound mismatch: residents="
                + residentCount
                + ", weightedSize="
                + weightedSize;
      } else if (!statsPassed) {
        validationFailure =
            "statistics mismatch: hitsDelta=" + hitsDelta + ", missesDelta=" + missesDelta;
      }
      return new Sample(
          sampleIndex,
          warmup,
          timed.wallSeconds,
          cleanupSeconds,
          operations,
          checksum,
          misses,
          workersOperations,
          hitsDelta,
          missesDelta,
          residentCount,
          weightedSize,
          allocatedBytes,
          gcDelta,
          boundsPassed,
          validationFailure);
    }

    @Override
    public void close() {
      workers.close();
      cache.cleanUp();
      long residentCount = cache.asMap().size();
      if (residentCount != options.residents) {
        throw new IllegalStateException("cache changed during final cleanup: " + residentCount);
      }
    }

    private static long weightedSize(Cache<Integer, Integer> cache) {
      if (cache.policy().eviction().isEmpty()) {
        return cache.asMap().size();
      }
      OptionalLong weightedSize = cache.policy().eviction().orElseThrow().weightedSize();
      // maximumSize uses the unit weigher, for which Caffeine does not expose weightedSize().
      return weightedSize.orElse(cache.asMap().size());
    }
  }

  private static final class PersistentWorkers implements AutoCloseable {
    private final Cache<Integer, Integer> cache;
    private final Integer[] keys;
    private final Options options;
    private final CyclicBarrier readyBarrier;
    private final CyclicBarrier startBarrier;
    private final CyclicBarrier finishBarrier;
    private final Thread[] threads;
    private final WorkerResult[] workerResults;
    private final long[] allocationBefore;
    private final ThreadMXBean threadBean;
    private volatile long deadlineNanos;
    private volatile int mask;
    private volatile boolean stopping;
    private volatile Throwable failure;

    PersistentWorkers(Cache<Integer, Integer> cache, Integer[] keys, Options options) {
      this.cache = cache;
      this.keys = keys;
      this.options = options;
      readyBarrier = new CyclicBarrier(options.workers + 1);
      startBarrier = new CyclicBarrier(options.workers + 1);
      finishBarrier = new CyclicBarrier(options.workers + 1);
      threads = new Thread[options.workers];
      workerResults = new WorkerResult[options.workers];
      allocationBefore = new long[options.workers];
      threadBean = allocationBean();
      for (int worker = 0; worker < options.workers; worker++) {
        int workerIndex = worker;
        workerResults[worker] = new WorkerResult();
        threads[worker] = Thread.ofPlatform()
            .daemon(true)
            .name("LoadingCache.HitProbe.worker-" + worker)
            .unstarted(() -> runWorker(workerIndex));
        threads[worker].start();
      }
      await(readyBarrier, "workers did not become ready");
      if (failure != null) {
        throw new IllegalStateException("worker initialization failed", failure);
      }
    }

    TimedResult runIteration(int nextMask) {
      if (failure != null) {
        throw new IllegalStateException("worker failed before iteration", failure);
      }
      mask = nextMask;
      if (threadBean != null && threadBean.isThreadAllocatedMemorySupported()) {
        for (int worker = 0; worker < options.workers; worker++) {
          allocationBefore[worker] = threadBean.getThreadAllocatedBytes(threads[worker].threadId());
        }
      } else {
        Arrays.fill(allocationBefore, -1L);
      }
      long started = System.nanoTime();
      deadlineNanos = started + TimeUnit.MILLISECONDS.toNanos(options.durationMillis);
      try {
        await(startBarrier, "workers did not start");
        await(finishBarrier, "workers did not finish");
      } catch (RuntimeException exception) {
        stopAfterFailure(exception);
        throw exception;
      }
      double wallSeconds = elapsedSeconds(started, System.nanoTime());
      if (failure != null) {
        throw new IllegalStateException("worker failed during iteration", failure);
      }
      WorkerResult[] results = new WorkerResult[workerResults.length];
      for (int worker = 0; worker < workerResults.length; worker++) {
        results[worker] = workerResults[worker].copy();
      }
      return new TimedResult(wallSeconds, mask, results);
    }

    private void runWorker(int workerIndex) {
      try {
        await(readyBarrier, "worker ready barrier failed");
        while (!stopping) {
          try {
            await(startBarrier, "worker start barrier failed");
          } catch (RuntimeException exception) {
            if (!stopping) {
              signalFailure(exception);
            }
            return;
          }
          if (stopping) {
            return;
          }

          long workerMask = mask;
          long index = (long) workerIndex * START_STRIDE;
          long operations = 0;
          long checksum = 0;
          long misses = 0;
          do {
            for (int offset = 0; offset < CHUNK_SIZE; offset++) {
              Integer value = cache.getIfPresent(keys[(int) (index & workerMask)]);
              if (value == null) {
                misses++;
              } else {
                checksum += value;
              }
              operations++;
              index++;
            }
          } while (!stopping && System.nanoTime() < deadlineNanos);

          long allocatedBytes = -1;
          if (threadBean != null && allocationBefore[workerIndex] >= 0) {
            long after = threadBean.getThreadAllocatedBytes(Thread.currentThread().threadId());
            allocatedBytes = Math.max(0, after - allocationBefore[workerIndex]);
          }
          WorkerResult result = workerResults[workerIndex];
          result.operations = operations;
          result.checksum = checksum;
          result.misses = misses;
          result.allocatedBytes = allocatedBytes;
          try {
            await(finishBarrier, "worker finish barrier failed");
          } catch (RuntimeException exception) {
            if (!stopping) {
              signalFailure(exception);
            }
            return;
          }
        }
      } catch (Throwable exception) {
        if (!stopping) {
          signalFailure(exception);
        }
      }
    }

    private void signalFailure(Throwable exception) {
      if (failure == null) {
        failure = exception;
      }
      stopping = true;
      readyBarrier.reset();
      startBarrier.reset();
      finishBarrier.reset();
      for (Thread thread : threads) {
        if (thread != Thread.currentThread()) {
          thread.interrupt();
        }
      }
    }

    private void stopAfterFailure(Throwable exception) {
      if (!stopping) {
        signalFailure(exception);
      }
    }

    @Override
    public void close() {
      stopping = true;
      readyBarrier.reset();
      startBarrier.reset();
      finishBarrier.reset();
      for (Thread thread : threads) {
        thread.interrupt();
      }
      for (Thread thread : threads) {
        try {
          thread.join(TimeUnit.SECONDS.toMillis(WATCHDOG_SECONDS));
        } catch (InterruptedException exception) {
          Thread.currentThread().interrupt();
          throw new IllegalStateException("interrupted while stopping worker threads", exception);
        }
        if (thread.isAlive()) {
          throw new IllegalStateException("worker did not stop within watchdog", failure);
        }
      }
      if (failure != null) {
        throw new IllegalStateException("persistent worker failed", failure);
      }
    }

    private static ThreadMXBean allocationBean() {
      java.lang.management.ThreadMXBean bean = ManagementFactory.getThreadMXBean();
      if (!(bean instanceof ThreadMXBean managementBean)
          || !managementBean.isThreadAllocatedMemorySupported()) {
        return null;
      }
      try {
        if (!managementBean.isThreadAllocatedMemoryEnabled()) {
          managementBean.setThreadAllocatedMemoryEnabled(true);
        }
        return managementBean;
      } catch (UnsupportedOperationException | SecurityException exception) {
        return null;
      }
    }

    private static void await(CyclicBarrier barrier, String description) {
      try {
        barrier.await(WATCHDOG_SECONDS, TimeUnit.SECONDS);
      } catch (InterruptedException exception) {
        Thread.currentThread().interrupt();
        throw new IllegalStateException(description + ": interrupted", exception);
      } catch (BrokenBarrierException | TimeoutException exception) {
        throw new IllegalStateException(description, exception);
      }
    }
  }

  private static final class WorkerResult {
    private long operations;
    private long checksum;
    private long misses;
    private long allocatedBytes;

    WorkerResult copy() {
      WorkerResult copy = new WorkerResult();
      copy.operations = operations;
      copy.checksum = checksum;
      copy.misses = misses;
      copy.allocatedBytes = allocatedBytes;
      return copy;
    }
  }

  private record TimedResult(double wallSeconds, int mask, WorkerResult[] workerResults) {}

  private record Sample(
      int sampleIndex,
      boolean warmup,
      double wallSeconds,
      double cleanupSeconds,
      long operations,
      long checksum,
      long misses,
      long[] workersOperations,
      long hitsDelta,
      long missesDelta,
      long residentCount,
      long weightedSize,
      long workerAllocatedBytes,
      GcCounts gcCollections,
      boolean boundsPassed,
      String validationFailure) {}

  private record GcCounts(Map<String, Long> values) {
    static GcCounts capture(List<String> names) {
      Map<String, Long> values = new LinkedHashMap<>();
      for (String name : names) {
        long count = -1L;
        for (GarbageCollectorMXBean collector : ManagementFactory.getGarbageCollectorMXBeans()) {
          if (collector.getName().equals(name)) {
            count = collector.getCollectionCount();
            break;
          }
        }
        values.put(name, count);
      }
      return new GcCounts(values);
    }

    GcCounts minus(GcCounts before) {
      Map<String, Long> delta = new LinkedHashMap<>();
      for (Map.Entry<String, Long> entry : values.entrySet()) {
        long current = entry.getValue();
        long previous = before.values.getOrDefault(entry.getKey(), -1L);
        delta.put(
            entry.getKey(), current < 0 || previous < 0 ? -1L : Math.max(0L, current - previous));
      }
      return new GcCounts(delta);
    }
  }

  private static final class Options {
    private int capacity = DEFAULT_CAPACITY;
    private int residents = DEFAULT_RESIDENTS;
    private String pattern = "hot";
    private int workers = DEFAULT_WORKERS;
    private boolean statistics;
    private int warmups = DEFAULT_WARMUPS;
    private int runs = DEFAULT_RUNS;
    private long durationMillis = DEFAULT_DURATION_MILLIS;
    private Path output;

    static Options parse(String[] args) {
      Options options = new Options();
      for (int index = 0; index < args.length; index++) {
        String argument = args[index];
        if (argument.equals("--help")) {
          System.out.println(usage());
          System.exit(0);
        }
        if (!argument.startsWith("--") || index + 1 >= args.length) {
          throw new IllegalArgumentException("expected flag and value: " + argument);
        }
        String value = args[++index];
        switch (argument) {
          case "--capacity" -> options.capacity = parseInt(argument, value);
          case "--residents" -> options.residents = parseInt(argument, value);
          case "--pattern" -> options.pattern = value;
          case "--workers" -> options.workers = parseInt(argument, value);
          case "--statistics" ->
              options.statistics = parseChoice(argument, value, "on", "off").equals("on");
          case "--warmups" -> options.warmups = parseInt(argument, value);
          case "--runs" -> options.runs = parseInt(argument, value);
          case "--duration-ms" -> options.durationMillis = parseLong(argument, value);
          case "--output" -> options.output = Paths.get(value);
          default -> throw new IllegalArgumentException("unknown flag: " + argument);
        }
      }
      options.validate();
      return options;
    }

    private void validate() {
      if (!isPowerOfTwo(capacity) || capacity <= 0) {
        throw new IllegalArgumentException("--capacity must be a positive power of two");
      }
      if (!isPowerOfTwo(residents) || residents <= 0) {
        throw new IllegalArgumentException("--residents must be a positive power of two");
      }
      if (residents > capacity) {
        throw new IllegalArgumentException("--residents must be <= --capacity");
      }
      if (!pattern.equals("hot") && !pattern.equals("cycle")) {
        throw new IllegalArgumentException("--pattern must be hot or cycle");
      }
      if (workers <= 0) {
        throw new IllegalArgumentException("--workers must be positive");
      }
      if (warmups < 0 || runs <= 0) {
        throw new IllegalArgumentException("--warmups must be >= 0 and --runs must be positive");
      }
      if (durationMillis <= 0) {
        throw new IllegalArgumentException("--duration-ms must be positive");
      }
    }

    static String usage() {
      return "usage: --capacity N --residents N --pattern hot|cycle --workers N "
          + "--statistics on|off --warmups N --runs N --duration-ms N --output PATH";
    }

    private static int parseInt(String flag, String value) {
      try {
        return Integer.parseInt(value);
      } catch (NumberFormatException exception) {
        throw new IllegalArgumentException(flag + " must be an integer: " + value, exception);
      }
    }

    private static long parseLong(String flag, String value) {
      try {
        return Long.parseLong(value);
      } catch (NumberFormatException exception) {
        throw new IllegalArgumentException(flag + " must be an integer: " + value, exception);
      }
    }

    private static String parseChoice(String flag, String value, String first, String second) {
      if (!value.equals(first) && !value.equals(second)) {
        throw new IllegalArgumentException(flag + " must be " + first + " or " + second);
      }
      return value;
    }

    private static boolean isPowerOfTwo(int value) {
      return value > 0 && (value & (value - 1)) == 0;
    }
  }

  private static final class ReportWriter {
    private ReportWriter() {}

    static String write(Options options, List<Sample> samples, Throwable failure) {
      StringBuilder json = new StringBuilder(16_384);
      json.append('{');
      field(json, "schemaVersion", SCHEMA_VERSION);
      field(json, "capacity", options.capacity);
      field(json, "residents", options.residents);
      field(json, "pattern", options.pattern);
      field(json, "workers", options.workers);
      field(json, "statistics", options.statistics ? "on" : "off");
      field(json, "warmups", options.warmups);
      field(json, "runs", options.runs);
      field(json, "durationMillis", options.durationMillis);
      field(json, "runtime", System.getProperty("java.runtime.version", "unknown"));
      field(json, "os", System.getProperty("os.name", "unknown"));
      field(json, "architecture", System.getProperty("os.arch", "unknown"));
      field(json, "processors", Runtime.getRuntime().availableProcessors());
      field(json, "caffeineVersion", CAFFEINE_VERSION);
      Artifact artifact = caffeineArtifact();
      field(json, "caffeineJarPath", artifact.path);
      field(json, "caffeineJarSha256", artifact.sha256);
      field(json, "allocationScope", "live dedicated worker threads");
      field(
          json,
          "allocationCoverage",
          "dedicated reader threads only; excludes main thread, cache setup, Caffeine maintenance/executor threads, and terminated threads");
      field(json, "allocationSupported", allocationSupported());
      fieldArray(json, "samples", samples, ReportWriter::sample);
      if (failure == null) {
        fieldNull(json, "failure");
      } else {
        field(json, "failure", failure.toString());
      }
      json.append('}');
      return json.toString();
    }

    private static String sample(Sample sample) {
      StringBuilder json = new StringBuilder(1_024);
      json.append('{');
      field(json, "sampleIndex", sample.sampleIndex);
      field(json, "warmup", sample.warmup);
      field(json, "wallSeconds", sample.wallSeconds);
      field(json, "cleanupSeconds", sample.cleanupSeconds);
      field(json, "operations", sample.operations);
      field(json, "checksum", sample.checksum);
      field(json, "misses", sample.misses);
      fieldArray(json, "workersOperations", sample.workersOperations);
      field(json, "hitsDelta", sample.hitsDelta);
      field(json, "missesDelta", sample.missesDelta);
      field(json, "residentCount", sample.residentCount);
      field(json, "weightedSize", sample.weightedSize);
      field(json, "workerAllocatedBytes", sample.workerAllocatedBytes);
      fieldObject(json, "gcCollections", sample.gcCollections.values);
      field(json, "boundsPassed", sample.boundsPassed);
      if (sample.validationFailure == null) {
        fieldNull(json, "validationFailure");
      } else {
        field(json, "validationFailure", sample.validationFailure);
      }
      json.append('}');
      return json.toString();
    }

    private static Artifact caffeineArtifact() {
      try {
        Path path = Paths.get(new URI(Caffeine.class.getProtectionDomain().getCodeSource().getLocation().toString()))
            .toAbsolutePath();
        return new Artifact(path.toString(), Files.isRegularFile(path) ? sha256(path) : null);
      } catch (URISyntaxException | RuntimeException exception) {
        return new Artifact(null, null);
      }
    }

    private static String sha256(Path path) {
      try {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (var input = Files.newInputStream(path)) {
          byte[] buffer = new byte[16_384];
          int read;
          while ((read = input.read(buffer)) >= 0) {
            if (read > 0) {
              digest.update(buffer, 0, read);
            }
          }
        }
        StringBuilder hex = new StringBuilder(64);
        for (byte value : digest.digest()) {
          hex.append(String.format("%02x", value & 0xff));
        }
        return hex.toString();
      } catch (IOException | NoSuchAlgorithmException exception) {
        return null;
      }
    }

    private static boolean allocationSupported() {
      java.lang.management.ThreadMXBean bean = ManagementFactory.getThreadMXBean();
      return bean instanceof ThreadMXBean managementBean
          && managementBean.isThreadAllocatedMemorySupported();
    }

    private static void field(StringBuilder json, String name, String value) {
      separator(json);
      string(json, name);
      json.append(':');
      if (value == null) {
        json.append("null");
      } else {
        string(json, value);
      }
    }

    private static void field(StringBuilder json, String name, long value) {
      separator(json);
      string(json, name);
      json.append(':').append(value);
    }

    private static void field(StringBuilder json, String name, double value) {
      separator(json);
      string(json, name);
      json.append(':').append(value);
    }

    private static void field(StringBuilder json, String name, int value) {
      field(json, name, (long) value);
    }

    private static void field(StringBuilder json, String name, boolean value) {
      separator(json);
      string(json, name);
      json.append(':').append(value);
    }

    private static void fieldNull(StringBuilder json, String name) {
      separator(json);
      string(json, name);
      json.append(":null");
    }

    private static void fieldArray(StringBuilder json, String name, long[] values) {
      separator(json);
      string(json, name);
      json.append(':');
      array(json, values);
    }

    private static <T> void fieldArray(
        StringBuilder json, String name, List<T> values, java.util.function.Function<T, String> encoder) {
      separator(json);
      string(json, name);
      json.append(':').append('[');
      for (int index = 0; index < values.size(); index++) {
        if (index > 0) {
          json.append(',');
        }
        json.append(encoder.apply(values.get(index)));
      }
      json.append(']');
    }

    private static void fieldObject(StringBuilder json, String name, Map<String, Long> values) {
      separator(json);
      string(json, name);
      json.append(':').append('{');
      int index = 0;
      for (Map.Entry<String, Long> entry : values.entrySet()) {
        if (index++ > 0) {
          json.append(',');
        }
        string(json, entry.getKey());
        json.append(':').append(entry.getValue());
      }
      json.append('}');
    }

    private static void array(StringBuilder json, long[] values) {
      json.append('[');
      for (int index = 0; index < values.length; index++) {
        if (index > 0) {
          json.append(',');
        }
        json.append(values[index]);
      }
      json.append(']');
    }

    private static void separator(StringBuilder json) {
      if (json.length() > 1 && json.charAt(json.length() - 1) != '{' && json.charAt(json.length() - 1) != '[') {
        json.append(',');
      }
    }

    private static void string(StringBuilder json, String value) {
      json.append('"');
      if (value != null) {
        for (int index = 0; index < value.length(); index++) {
          char character = value.charAt(index);
          switch (character) {
            case '"' -> json.append("\\\"");
            case '\\' -> json.append("\\\\");
            case '\b' -> json.append("\\b");
            case '\f' -> json.append("\\f");
            case '\n' -> json.append("\\n");
            case '\r' -> json.append("\\r");
            case '\t' -> json.append("\\t");
            default -> {
              if (character < 0x20) {
                json.append(String.format("\\u%04x", (int) character));
              } else {
                json.append(character);
              }
            }
          }
        }
      }
      json.append('"');
    }
  }

  private record Artifact(String path, String sha256) {}

  private static long expectedChecksum(int worker, long operations, int mask, int residents) {
    if (mask == 0) {
      return operations;
    }
    long fullCycles = operations / residents;
    int remainder = (int) (operations % residents);
    long checksum = fullCycles * residents * (residents + 1L) / 2L;
    int start = (worker * START_STRIDE) & mask;
    for (int index = 0; index < remainder; index++) {
      checksum += ((start + index) & mask) + 1L;
    }
    return checksum;
  }

  private static double elapsedSeconds(long started, long finished) {
    return (finished - started) / 1_000_000_000.0;
  }

}
