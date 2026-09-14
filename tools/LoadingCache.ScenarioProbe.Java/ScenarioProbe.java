import com.github.benmanes.caffeine.cache.*;
import java.lang.management.ManagementFactory;
import java.lang.reflect.RecordComponent;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.time.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.BooleanSupplier;

/** Matched, validated closed-loop scenario driver; see methodology.md. */
public final class ScenarioProbe {
  private static final List<String> NAMES = List.of("size-churn", "weight-churn", "weight-replace", "mixed", "ttl", "tti", "variable", "variable-update", "ttl-cleanup", "tti-cleanup", "variable-cleanup", "runtime-maximum", "runtime-expiry", "runtime-access", "runtime-refresh", "runtime-variable", "variable-put", "eviction-listener", "removal-listener", "strong-lookup", "quiet-lookup", "weak-key-lookup", "weak-value-lookup", "weak-key-cleanup", "weak-value-cleanup", "sync-miss", "manual-sync-miss", "sync-fanin-contract", "manual-sync-fanin-contract", "async-completed-miss", "manual-async-completed-miss", "async-gated-miss", "manual-async-gated-miss", "async-gated-fanin", "manual-async-gated-fanin", "refresh-explicit", "refresh-auto", "bulk-sync", "bulk-async", "prefetch-sync", "prefetch-async");
  private static final Duration DURATION = Duration.ofMillis(10);
  private ScenarioProbe() {}

  public static void main(String[] args) throws Exception {
    Options options = Options.parse(args);
    List<String> selected = options.scenario.equals("all") ? NAMES : List.of(options.scenario);
    if (!options.scenario.equals("trace-policy") && !NAMES.containsAll(selected)) throw new IllegalArgumentException("Unknown scenario");
    List<Sample> samples = new ArrayList<>();
    for (String name : selected) for (int index = 0; index < options.warmups + options.runs; index++) {
      Sample sample = run(name, options, index);
      samples.add(sample);
      System.err.printf(Locale.ROOT, "%s sample=%d operations=%d backend=%d seconds=%.6f%n", name, index, sample.operations, sample.backendCalls, sample.seconds);
    }
    Map<String, Object> report = new LinkedHashMap<>();
    report.put("schemaVersion", 1);
    report.put("runtime", System.getProperty("java.runtime.version"));
    report.put("vm", System.getProperty("java.vm.name"));
    report.put("architecture", System.getProperty("os.arch"));
    report.put("os", System.getProperty("os.name") + " " + System.getProperty("os.version"));
    report.put("processors", Runtime.getRuntime().availableProcessors());
    report.put("stopwatchFrequency", 1_000_000_000L);
    report.put("jvmArguments", ManagementFactory.getRuntimeMXBean().getInputArguments());
    report.put("cacheSha256", hash(Path.of(Caffeine.class.getProtectionDomain().getCodeSource().getLocation().toURI())));
    Path classDirectory = Path.of(ScenarioProbe.class.getProtectionDomain().getCodeSource().getLocation().toURI());
    Map<String, String> classHashes = new TreeMap<>();
    try (var paths = Files.list(classDirectory)) { for (Path path : paths.filter(path -> path.getFileName().toString().startsWith("ScenarioProbe") && path.toString().endsWith(".class")).toList()) classHashes.put(path.getFileName().toString(), hash(path)); }
    report.put("harnessClassHashes", classHashes);
    report.put("harnessSha256", HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(json(classHashes).getBytes(StandardCharsets.UTF_8))));
    report.put("argv", List.of(args)); report.put("options", options);
    report.put("traceSha256", options.traceFile == null ? null : hash(Path.of(options.traceFile)));
    report.put("allocationScope", "driver-thread allocated bytes during timed scenario; excludes background maintenance, loader and callback workers; not directly comparable to .NET whole-process bytes; -1 if unavailable");
    report.put("timingScope", "closed-loop wall time including scenario actions, inline validation and final maintenance; excludes builder, trace and object setup, final snapshot validation and JSON; no per-request latency percentiles");
    report.put("samples", samples);
    String output = json(report);
    if (options.output == null) System.out.println(output);
    else { Path path = Path.of(options.output).toAbsolutePath(); Files.createDirectories(path.getParent()); Files.writeString(path, output + System.lineSeparator(), StandardCharsets.UTF_8); }
  }

  private static Sample run(String name, Options options, int index) throws Exception {
    if (name.equals("sync-fanin-contract") || name.equals("manual-sync-fanin-contract")) return syncFanIn(name, options, index);
    if (name.equals("weak-key-cleanup") || name.equals("weak-value-cleanup")) return referenceCleanup(name, options, index);
    AtomicLong clock = new AtomicLong();
    int[] trace = options.traceFile == null ? trace(options.cycles, options.seed, options.capacity * 4) : readTrace(options.traceFile);
    check(trace.length == options.cycles && Arrays.stream(trace).allMatch(key -> key >= 0 && key <= 1_000_000), "trace length/key bounds");
    Key[] keys = new Key[Math.max(options.capacity * 4 + 32, Arrays.stream(trace).max().orElseThrow() + 1)];
    Value[] values = new Value[keys.length], alternate = new Value[keys.length];
    for (int k = 0; k < keys.length; k++) { keys[k] = new Key(k); values[k] = new Value(k + 1, k % 4 + 1); alternate[k] = new Value(k + 10001, k % 2 + 1); }
    Value[] weightReplacementTrace = name.equals("weight-replace") ? weightReplacementTrace(trace, options.capacity / 8, values) : null;
    Value[] previousWeightValues = name.equals("weight-replace") ? new Value[options.capacity / 8] : null;
    int expectedWeightChanges = name.equals("weight-replace") ? options.cycles - (int) Arrays.stream(trace).map(key -> key % (options.capacity / 8)).distinct().count() : 0;
    AtomicLong callbacks = new AtomicLong();
    Caffeine<Object, Object> builder = Caffeine.newBuilder().weigher((key, value) -> ((Value) value).weight);
    // A fresh builder keeps the size cases unweighted; no weigher on their hit path.
    if (name.startsWith("weight")) builder.maximumWeight(options.capacity);
    else builder = Caffeine.newBuilder().maximumSize(options.capacity);
    builder.ticker(clock::get);
    if (options.statistics) builder.recordStats();
    if (name.equals("ttl") || name.equals("ttl-cleanup") || name.equals("runtime-expiry")) builder.expireAfterWrite(DURATION);
    if (name.equals("tti") || name.equals("tti-cleanup") || name.equals("runtime-access")) builder.expireAfterAccess(DURATION);
    if (name.equals("variable") || name.equals("variable-update") || name.equals("variable-cleanup") || name.equals("runtime-variable") || name.equals("variable-put")) builder.expireAfter(new VariableExpiry());
    if (name.equals("refresh-auto") || name.equals("runtime-refresh")) builder.refreshAfterWrite(DURATION);
    if (name.equals("weak-key-lookup")) builder.weakKeys();
    if (name.equals("weak-value-lookup")) builder.weakValues();
    if (name.equals("eviction-listener")) builder.evictionListener((key, value, cause) -> callbacks.incrementAndGet());
    if (name.equals("removal-listener")) builder.removalListener((key, value, cause) -> callbacks.incrementAndGet());
    boolean asyncMode = name.contains("async");
    Loader loader = new Loader(values, name.startsWith("prefetch"), name.contains("gated"));
    Cache<Key, Value> cache = null;
    AsyncCache<Key, Value> asyncCache = null;
    if (asyncMode) asyncCache = name.startsWith("manual") ? builder.buildAsync() : builder.buildAsync(loader);
    else cache = List.of("sync-miss", "refresh-explicit", "refresh-auto", "runtime-refresh", "bulk-sync", "prefetch-sync").contains(name) ? builder.build(loader) : builder.build();
    if (name.endsWith("lookup")) { for (int k = 0; k < options.capacity / 2; k++) cache.put(keys[k], values[k]); drain(cache); check((cache.getIfPresent(new Key(0)) != null) == !name.equals("weak-key-lookup"), "reference key equality"); }
    Map<Integer, Value> model = new HashMap<>();
    Counters c = new Counters();
    int weightChanges = 0;
    int drains = 0;
    Instant utcStarted = Instant.now();
    long allocationBefore = allocation();
    long started = System.nanoTime();
    for (int cycle = 0; cycle < options.cycles; cycle++) {
      int id = trace[cycle];
      switch (name) {
        case "size-churn", "weight-churn", "eviction-listener" -> { cache.put(keys[id], values[id]); c.operations++; }
        case "trace-policy" -> {
          Value traced = cache.getIfPresent(keys[id]);
          if (traced != null) { check(traced == values[id], "trace value"); c.hits++; c.checksum += traced.id; }
          else { c.misses++; cache.put(keys[id], values[id]); c.operations++; }
          c.operations++; drains += drain(cache);
        }
        case "weight-replace" -> {
          id %= options.capacity / 8;
          Value replacement = weightReplacementTrace[cycle];
          Value previousWeightValue = previousWeightValues[id];
          if (previousWeightValue != null) { check(previousWeightValue != replacement && previousWeightValue.weight != replacement.weight, "replacement must change value identity and weight for the same key"); weightChanges++; }
          cache.put(keys[id], replacement); c.operations++;
          Value replaced = cache.getIfPresent(keys[id]); c.operations++;
          check(replaced == replacement, "replacement lost"); c.checksum += replaced.id; previousWeightValues[id] = replaced; c.hits++;
        }
        case "mixed" -> {
          id %= options.capacity / 2;
          if (cycle % 10 < 2) { cache.put(keys[id], values[id]); model.put(id, values[id]); }
          else if (cycle % 10 == 2) { cache.invalidate(keys[id]); model.remove(id); }
          else { Value mixed = cache.getIfPresent(keys[id]); check((mixed != null) == model.containsKey(id), "mixed presence"); if (mixed != null) { check(mixed == model.get(id), "mixed value"); c.checksum += mixed.id; c.hits++; } else c.misses++; }
          c.operations++;
        }
        case "ttl", "tti", "variable" -> {
          cache.put(keys[0], values[0]); c.operations++;
          clock.addAndGet(6_000_000); read(cache, keys[0], values[0], true, c);
          clock.addAndGet(6_000_000); read(cache, keys[0], values[0], !name.equals("ttl"), c);
          clock.addAndGet(11_000_000); read(cache, keys[0], values[0], false, c);
        }
        case "variable-update" -> {
          cache.put(keys[0], values[0]); c.operations++; clock.addAndGet(6_000_000);
          cache.put(keys[0], alternate[0]); c.operations++; clock.addAndGet(6_000_000);
          read(cache, keys[0], alternate[0], true, c); clock.addAndGet(11_000_000);
          read(cache, keys[0], alternate[0], false, c);
        }
        case "ttl-cleanup", "tti-cleanup", "variable-cleanup" -> {
          for (int k = 0; k < 16; k++) { cache.put(keys[k], values[k]); c.operations++; }
          drains += drain(cache); c.operations++; check(cache.estimatedSize() == 16, "expiry prefill not fully registered");
          clock.addAndGet(2_000_000_000L); drains += drain(cache); c.operations++;
          check(cache.estimatedSize() == 0, "expired entries survived cleanup");
        }
        case "runtime-maximum" -> {
          cache.policy().eviction().orElseThrow().setMaximum((cycle & 1) == 0 ? options.capacity : options.capacity / 2); c.operations++;
          for (int k = 0; k < 16; k++) { cache.put(keys[(id + k) % keys.length], values[(id + k) % keys.length]); c.operations++; }
        }
        case "runtime-expiry", "runtime-access" -> {
          Policy.FixedExpiration<Key, Value> fixedPolicy = name.equals("runtime-access") ? cache.policy().expireAfterAccess().orElseThrow() : cache.policy().expireAfterWrite().orElseThrow();
          fixedPolicy.setExpiresAfter(DURATION); c.operations++;
          cache.put(keys[0], values[0]); c.operations++; clock.addAndGet(6_000_000);
          fixedPolicy.setExpiresAfter(Duration.ofMillis(5)); c.operations++;
          read(cache, keys[0], values[0], false, c);
        }
        case "runtime-variable" -> {
          cache.put(keys[0], values[0]); c.operations++;
          cache.policy().expireVariably().orElseThrow().setExpiresAfter(keys[0], Duration.ofMillis(5)); c.operations++;
          clock.addAndGet(6_000_000); read(cache, keys[0], values[0], false, c);
        }
        case "removal-listener" -> { cache.put(keys[0], (cycle & 1) == 0 ? values[0] : alternate[0]); c.operations++; }
        case "variable-put" -> {
          cache.policy().expireVariably().orElseThrow().put(keys[0], values[0], Duration.ofMillis(5)); c.operations++;
          clock.addAndGet(6_000_000); read(cache, keys[0], values[0], false, c);
        }
        case "quiet-lookup" -> {
          id %= options.capacity / 2; Value quiet = cache.policy().getIfPresentQuietly(keys[id]);
          check(quiet == values[id], "quiet lookup value"); c.operations++; c.hits++; c.checksum += quiet.id;
        }
        case "strong-lookup", "weak-key-lookup", "weak-value-lookup" -> { id %= options.capacity / 2; read(cache, keys[id], values[id], true, c); }
        case "sync-miss", "manual-sync-miss" -> {
          cache.invalidate(keys[0]); c.operations++;
          Value loaded = cache instanceof LoadingCache<Key, Value> loading ? loading.get(keys[0]) : cache.get(keys[0], loader::load);
          check(loaded == values[0], "sync load"); c.checksum += loaded.id; c.operations++;
        }
        case "async-completed-miss", "manual-async-completed-miss", "async-gated-miss", "manual-async-gated-miss", "async-gated-fanin", "manual-async-gated-fanin" -> {
          asyncCache.synchronous().invalidate(keys[0]); c.operations++;
          boolean gated = name.contains("gated");
          if (gated) loader.gate = new CompletableFuture<>();
          int count = gated && name.contains("fanin") ? options.fanIn : 1;
          List<CompletableFuture<Value>> requests = new ArrayList<>(count);
          for (int waiter = 0; waiter < count; waiter++) { requests.add(asyncCache instanceof AsyncLoadingCache<Key, Value> loading ? loading.get(keys[0]) : asyncCache.get(keys[0], loader::asyncLoad)); c.operations++; }
          if (gated) { check(requests.stream().noneMatch(CompletableFuture::isDone), "gated requests completed before release"); loader.gate.complete(values[0]); }
          for (CompletableFuture<Value> request : requests) { Value result = request.get(30, TimeUnit.SECONDS); check(result == values[0], "async value"); c.checksum += result.id; }
        }
        case "refresh-explicit", "refresh-auto", "runtime-refresh" -> {
          if (name.equals("runtime-refresh")) { cache.policy().refreshAfterWrite().orElseThrow().setRefreshesAfter(DURATION); c.operations++; }
          cache.put(keys[0], alternate[0]); c.operations++;
          LoadingCache<Key, Value> refreshing = (LoadingCache<Key, Value>) cache;
          if (!name.equals("refresh-explicit")) {
            clock.addAndGet(name.equals("runtime-refresh") ? 6_000_000 : 11_000_000);
            if (name.equals("runtime-refresh")) { cache.policy().refreshAfterWrite().orElseThrow().setRefreshesAfter(Duration.ofMillis(5)); c.operations++; }
            Value stale = refreshing.get(keys[0]); c.operations++;
            check(stale == alternate[0] || stale == values[0], "refresh read");
            until(() -> refreshing.policy().getIfPresentQuietly(keys[0]) == values[0]);
          } else { check(refreshing.refresh(keys[0]).get(30, TimeUnit.SECONDS) == values[0], "refresh result"); c.operations++; }
          c.checksum += values[0].id;
        }
        case "bulk-sync", "prefetch-sync", "bulk-async", "prefetch-async" -> {
          List<Key> batch = Arrays.asList(Arrays.copyOf(keys, 16));
          if (asyncMode) { asyncCache.synchronous().invalidateAll(); c.operations++; Map<Key, Value> result = ((AsyncLoadingCache<Key, Value>) asyncCache).getAll(batch).get(30, TimeUnit.SECONDS); validateBulk(result, values, c); c.operations++; }
          else { cache.invalidateAll(); c.operations++; Map<Key, Value> result = ((LoadingCache<Key, Value>) cache).getAll(batch); validateBulk(result, values, c); c.operations++; }
          if (name.startsWith("prefetch")) { Value prefetched = (asyncMode ? asyncCache.synchronous() : cache).policy().getIfPresentQuietly(loader.prefetchKey); check(prefetched == values[16], "prefetch not published"); c.operations++; }
        }
        default -> throw new IllegalStateException(name);
      }
    }
    Cache<Key, Value> view = asyncMode ? asyncCache.synchronous() : cache;
    drains += drain(view);
    if (name.equals("removal-listener")) until(() -> callbacks.get() == options.cycles - 1);
    long ended = System.nanoTime();
    long allocationAfter = allocation();
    long allocated = allocationBefore < 0 || allocationAfter < 0 ? -1 : allocationAfter - allocationBefore;
    Instant utcEnded = Instant.now();
    if (name.equals("weight-replace")) check(weightChanges == expectedWeightChanges, "weighted replacement transition count");
    Policy.Eviction<Key, Value> policy = view.policy().eviction().orElseThrow();
    long finalCount = view.estimatedSize();
    long weight = policy.weightedSize().orElse(finalCount);
    if (name.equals("weight-replace")) check(finalCount == options.cycles - expectedWeightChanges && weight == Arrays.stream(previousWeightValues).filter(Objects::nonNull).mapToLong(value -> value.weight).sum(), "weighted replacement final residents/weight metadata");
    if (!options.statistics) check(view.stats().hitCount() == 0 && view.stats().missCount() == 0, "statistics disabled");
    else if (name.endsWith("lookup") && !name.equals("quiet-lookup")) check(view.stats().hitCount() >= options.cycles, "statistics hit recording");
    if (options.statistics && name.equals("quiet-lookup")) check(view.stats().hitCount() == 1 && view.stats().missCount() == 0, "quiet lookup altered statistics");
    check(finalCount <= options.capacity && weight <= policy.getMaximum(), "capacity bound");
    long expectedBackend = name.contains("miss") || name.contains("fanin") || name.startsWith("refresh") || name.equals("runtime-refresh") ? options.cycles : 0;
    check(loader.calls.get() == expectedBackend, "backend invocation count");
    long expectedBulk = name.startsWith("bulk") || name.startsWith("prefetch") ? options.cycles : 0;
    check(loader.bulkCalls.get() == expectedBulk, "true bulk count");
    if (List.of("size-churn", "weight-churn", "eviction-listener", "runtime-maximum").contains(name)) {
      for (Map.Entry<Key, Value> pair : policy.coldest(options.capacity * 4).entrySet()) { check(pair.getValue() == values[pair.getKey().id], "resident value corrupt"); c.checksum += pair.getValue().id; }
    }
    java.lang.ref.Reference.reachabilityFence(keys); java.lang.ref.Reference.reachabilityFence(values); java.lang.ref.Reference.reachabilityFence(alternate);
    return new Sample(name, index, index < options.warmups, c.operations, c.checksum, c.hits, c.misses, loader.calls.get(), loader.bulkCalls.get(), callbacks.get(), finalCount, weight, policy.getMaximum(), null, drains, utcStarted.toString(), utcEnded.toString(), started, ended, (ended - started) / 1e9, allocated, true);
  }

  private static void read(Cache<Key, Value> cache, Key key, Value expected, boolean present, Counters c) {
    Value value = cache.getIfPresent(key); c.operations++; check((value != null) == present, "expiry/lookup presence");
    if (value != null) { check(value == expected, "lookup value"); c.checksum += value.id; c.hits++; } else c.misses++;
  }
  private static Sample referenceCleanup(String name, Options options, int index) {
    Caffeine<Object, Object> builder = Caffeine.newBuilder().maximumSize(options.capacity);
    boolean weakKey = name.equals("weak-key-cleanup");
    if (weakKey) builder.weakKeys(); else builder.weakValues();
    if (options.statistics) builder.recordStats();
    Cache<Key, Value> cache = builder.build();
    ReferenceFixture fixture = populateUnrooted(cache, weakKey);
    Instant utcStart = Instant.now(); long start = System.nanoTime(); int passes = 0;
    do { System.gc(); cache.cleanUp(); passes++; } while ((fixture.weak.refersTo(null) == false || cache.estimatedSize() != 0) && passes < 32);
    check(fixture.weak.refersTo(null) && cache.estimatedSize() == 0, "weak target not collected/cleaned within bounded forced GC passes");
    java.lang.ref.Reference.reachabilityFence(fixture.retained);
    long end = System.nanoTime();
    return new Sample(name, index, index < options.warmups, 0, 0, 0, 0, 0, 0, 0, 0, 0, options.capacity, null, passes, utcStart.toString(), Instant.now().toString(), start, end, (end - start) / 1e9, -1, true);
  }
  private record ReferenceFixture(java.lang.ref.WeakReference<Object> weak, Object retained) {}
  private static Sample syncFanIn(String name, Options options, int index) throws Exception {
    CountDownLatch invoked = new CountDownLatch(options.fanIn), completed = new CountDownLatch(options.fanIn), entered = new CountDownLatch(1), release = new CountDownLatch(1);
    AtomicLong calls = new AtomicLong(); Key key = new Key(0); Value expected = new Value(1, 1);
    java.util.function.Function<Key, Value> load = ignored -> {
      calls.incrementAndGet(); entered.countDown();
      try { check(release.await(30, TimeUnit.SECONDS), "loader gate timeout"); }
      catch (InterruptedException exception) { Thread.currentThread().interrupt(); throw new IllegalStateException(exception); }
      return expected;
    };
    Caffeine<Object, Object> builder = Caffeine.newBuilder().maximumSize(options.capacity);
    if (options.statistics) builder.recordStats();
    Cache<Key, Value> cache = name.equals("sync-fanin-contract") ? builder.build(load::apply) : builder.build();
    Value[] values = new Value[options.fanIn];
    ConcurrentLinkedQueue<Throwable> failures = new ConcurrentLinkedQueue<>();
    Thread[] threads = new Thread[options.fanIn]; Instant utcStart = Instant.now(); long start = System.nanoTime();
    try {
      for (int i = 0; i < threads.length; i++) {
        int worker = i;
        threads[i] = new Thread(() -> {
          invoked.countDown();
          try { values[worker] = cache instanceof LoadingCache<Key, Value> loading ? loading.get(key) : cache.get(key, load); }
          catch (Throwable failure) { failures.add(failure); }
          finally { completed.countDown(); }
        });
        threads[i].setDaemon(true); threads[i].start();
      }
      check(invoked.await(30, TimeUnit.SECONDS) && entered.await(30, TimeUnit.SECONDS), "callers did not enter");
      check(completed.getCount() == options.fanIn, "a gated call completed before release");
      release.countDown(); check(completed.await(30, TimeUnit.SECONDS), "callers failed to finish");
      check(failures.isEmpty() && calls.get() == 1 && Arrays.stream(values).allMatch(value -> value == expected), "sync fan-in result/backend");
      int drains = drain(cache); long end = System.nanoTime();
      return new Sample(name, index, index < options.warmups, 0, options.fanIn, 0, 0, calls.get(), 0, 0, cache.estimatedSize(), cache.estimatedSize(), options.capacity, null, drains, utcStart.toString(), Instant.now().toString(), start, end, (end - start) / 1e9, -1, true);
    } finally { release.countDown(); for (Thread thread : threads) if (thread != null) { thread.join(30_000); check(!thread.isAlive(), "sync caller did not terminate"); } }
  }
  private static ReferenceFixture populateUnrooted(Cache<Key, Value> cache, boolean weakKey) {
    Key key = new Key(1); Value value = new Value(2, 1); cache.put(key, value); drain(cache);
    return new ReferenceFixture(new java.lang.ref.WeakReference<>(weakKey ? key : value), weakKey ? value : key);
  }
  private static void validateBulk(Map<Key, Value> result, Value[] values, Counters c) {
    check(result.size() == 16, "bulk count"); for (Map.Entry<Key, Value> pair : result.entrySet()) { check(pair.getKey().id < 16 && pair.getValue() == values[pair.getKey().id], "bulk value"); c.checksum += pair.getValue().id; }
  }
  private static int drain(Cache<Key, Value> cache) {
    for (int pass = 1; pass <= 256; pass++) {
      cache.cleanUp(); Policy.Eviction<Key, Value> policy = cache.policy().eviction().orElseThrow();
      if (policy.weightedSize().orElse(cache.estimatedSize()) <= policy.getMaximum()) return pass;
    }
    throw new IllegalStateException("maintenance failed to converge");
  }
  private static void until(BooleanSupplier predicate) {
    long start = System.nanoTime(); while (!predicate.getAsBoolean()) { if (System.nanoTime() - start > 30_000_000_000L) throw new IllegalStateException("callback/refresh failed to finish"); Thread.yield(); }
  }
  private static int[] trace(int count, int seed, int bound) {
    int[] trace = new int[count]; int state = seed;
    for (int i = 0; i < count; i++) { state = state * 1664525 + 1013904223; trace[i] = Integer.remainderUnsigned(state, bound); } return trace;
  }
  private static Value[] weightReplacementTrace(int[] trace, int keyCount, Value[] values) {
    int[] versions = new int[keyCount]; Value[] changed = new Value[values.length], replacements = new Value[trace.length];
    for (int i = 0; i < values.length; i++) changed[i] = new Value(values[i].id + 10000, values[i].weight % 4 + 1);
    for (int i = 0; i < trace.length; i++) { int key = trace[i] % keyCount; replacements[i] = (versions[key]++ & 1) == 0 ? values[key] : changed[key]; }
    return replacements;
  }
  private static int[] readTrace(String path) throws Exception {
    String text = Files.readString(Path.of(path), StandardCharsets.UTF_8).trim();
    check(text.startsWith("[") && text.endsWith("]"), "trace must be a JSON integer array");
    String body = text.substring(1, text.length() - 1).trim();
    if (body.isEmpty()) return new int[0];
    return Arrays.stream(body.split(",", -1)).map(String::trim).mapToInt(Integer::parseInt).toArray();
  }
  private static long allocation() {
    var bean = ManagementFactory.getThreadMXBean();
    if (bean instanceof com.sun.management.ThreadMXBean allocation && allocation.isThreadAllocatedMemorySupported()) {
      if (!allocation.isThreadAllocatedMemoryEnabled()) allocation.setThreadAllocatedMemoryEnabled(true);
      return allocation.getThreadAllocatedBytes(Thread.currentThread().threadId());
    }
    return -1;
  }
  private static String hash(Path path) throws Exception { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path))); }
  private static void check(boolean condition, String message) { if (!condition) throw new IllegalStateException(message); }
  private record Key(int id) {}
  private record Value(int id, int weight) {}
  private static final class Counters { long operations, checksum, hits, misses; }
  private static final class VariableExpiry implements Expiry<Key, Value> {
    @Override public long expireAfterCreate(Key key, Value value, long currentTime) { return DURATION.toNanos(); }
    @Override public long expireAfterUpdate(Key key, Value value, long currentTime, long currentDuration) { return DURATION.toNanos(); }
    @Override public long expireAfterRead(Key key, Value value, long currentTime, long currentDuration) { return DURATION.toNanos(); }
  }
  private static final class Loader implements CacheLoader<Key, Value> {
    private final Value[] values;
    private final boolean prefetch, gated;
    private final AtomicLong calls = new AtomicLong(), bulkCalls = new AtomicLong();
    private final Key prefetchKey = new Key(16);
    private CompletableFuture<Value> gate;
    Loader(Value[] values, boolean prefetch, boolean gated) { this.values = values; this.prefetch = prefetch; this.gated = gated; }
    @Override public Value load(Key key) { calls.incrementAndGet(); return values[key.id]; }
    @Override public CompletableFuture<Value> asyncLoad(Key key, Executor executor) { calls.incrementAndGet(); return gated ? gate : CompletableFuture.completedFuture(values[key.id]); }
    @Override public Map<Key, Value> loadAll(Set<? extends Key> keys) { bulkCalls.incrementAndGet(); Map<Key, Value> result = new HashMap<>(); for (Key key : keys) result.put(key, values[key.id]); if (prefetch) result.put(prefetchKey, values[16]); return result; }
    @Override public CompletableFuture<? extends Map<Key, Value>> asyncLoadAll(Set<? extends Key> keys, Executor executor) { return CompletableFuture.completedFuture(loadAll(keys)); }
  }
  private record Sample(String name, int index, boolean warmup, long operations, long checksum, long hits, long misses, long backendCalls, long bulkCalls, long callbacks, long finalCount, long finalWeight, long maximum, Long maintenanceBacklog, int drainPasses, String utcStarted, String utcEnded, long startedTimestamp, long endedTimestamp, double seconds, long allocatedBytes, boolean validated) {}
  private record Options(String scenario, int cycles, int capacity, int fanIn, int seed, int warmups, int runs, boolean statistics, String output, String traceFile) {
    static Options parse(String[] args) {
      if (args.length % 2 != 0) throw new IllegalArgumentException("Options require name/value pairs");
      Map<String, String> map = new HashMap<>(); List<String> allowed = List.of("--scenario", "--cycles", "--capacity", "--fan-in", "--seed", "--warmups", "--runs", "--statistics", "--output", "--trace-file");
      for (int i = 0; i < args.length; i += 2) if (!allowed.contains(args[i]) || map.putIfAbsent(args[i], args[i + 1]) != null) throw new IllegalArgumentException("Unknown/duplicate option");
      String stats = map.getOrDefault("--statistics", "off"); if (!stats.equals("on") && !stats.equals("off")) throw new IllegalArgumentException("--statistics on|off");
      check(map.getOrDefault("--scenario", "all").equals("trace-policy") == map.containsKey("--trace-file"), "trace-policy requires --trace-file exclusively");
      return new Options(map.getOrDefault("--scenario", "all"), number(map, "--cycles", 1024, 2, 1000000), number(map, "--capacity", 1024, 64, 1000000), number(map, "--fan-in", 16, 2, 1024), number(map, "--seed", 419, 0, Integer.MAX_VALUE), number(map, "--warmups", 2, 0, 100), number(map, "--runs", 3, 1, 100), stats.equals("on"), map.get("--output"), map.get("--trace-file"));
    }
    private static int number(Map<String, String> map, String key, int fallback, int min, int max) { int value = Integer.parseInt(map.getOrDefault(key, Integer.toString(fallback))); if (value < min || value > max) throw new IllegalArgumentException(key); return value; }
  }
  private static String json(Object value) throws Exception {
    if (value == null) return "null";
    if (value instanceof Number || value instanceof Boolean) return value.toString();
    if (value instanceof Map<?, ?> map) { List<String> fields = new ArrayList<>(); for (var entry : map.entrySet()) fields.add(json(entry.getKey().toString()) + ":" + json(entry.getValue())); return "{" + String.join(",", fields) + "}"; }
    if (value instanceof Iterable<?> list) { List<String> items = new ArrayList<>(); for (Object item : list) items.add(json(item)); return "[" + String.join(",", items) + "]"; }
    if (value.getClass().isRecord()) { Map<String, Object> fields = new LinkedHashMap<>(); for (RecordComponent component : value.getClass().getRecordComponents()) fields.put(component.getName(), component.getAccessor().invoke(value)); return json(fields); }
    StringBuilder out = new StringBuilder("\""); for (char ch : value.toString().toCharArray()) { switch (ch) { case '"' -> out.append("\\\""); case '\\' -> out.append("\\\\"); case '\n' -> out.append("\\n"); case '\r' -> out.append("\\r"); case '\t' -> out.append("\\t"); default -> { if (ch < 32) out.append(String.format(Locale.ROOT, "\\u%04x", (int) ch)); else out.append(ch); } } } return out.append('"').toString();
  }
}
