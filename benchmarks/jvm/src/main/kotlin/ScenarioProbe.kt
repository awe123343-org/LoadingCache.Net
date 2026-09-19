import com.github.benmanes.caffeine.cache.AsyncCache
import com.github.benmanes.caffeine.cache.AsyncLoadingCache
import com.github.benmanes.caffeine.cache.Cache
import com.github.benmanes.caffeine.cache.CacheLoader
import com.github.benmanes.caffeine.cache.Caffeine
import com.github.benmanes.caffeine.cache.Expiry
import com.github.benmanes.caffeine.cache.LoadingCache
import java.lang.management.ManagementFactory
import java.lang.ref.Reference
import java.lang.ref.WeakReference
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant
import java.util.HexFormat
import java.util.Locale
import java.util.TreeMap
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.function.BooleanSupplier
import java.util.function.Function
import java.util.jar.JarFile

/** Matched, validated closed-loop scenario driver; see methodology.md. */
object ScenarioProbe {
    private val names =
        listOf(
            "size-churn",
            "weight-churn",
            "weight-replace",
            "mixed",
            "ttl",
            "tti",
            "variable",
            "variable-update",
            "ttl-cleanup",
            "tti-cleanup",
            "variable-cleanup",
            "runtime-maximum",
            "runtime-expiry",
            "runtime-access",
            "runtime-refresh",
            "runtime-variable",
            "variable-put",
            "eviction-listener",
            "removal-listener",
            "strong-lookup",
            "quiet-lookup",
            "weak-key-lookup",
            "weak-value-lookup",
            "weak-key-cleanup",
            "weak-value-cleanup",
            "sync-miss",
            "manual-sync-miss",
            "sync-fanin-contract",
            "manual-sync-fanin-contract",
            "async-completed-miss",
            "manual-async-completed-miss",
            "async-gated-miss",
            "manual-async-gated-miss",
            "async-gated-fanin",
            "manual-async-gated-fanin",
            "refresh-explicit",
            "refresh-auto",
            "bulk-sync",
            "bulk-async",
            "prefetch-sync",
            "prefetch-async",
        )
    private val duration = Duration.ofMillis(10)

    @JvmStatic
    fun main(args: Array<String>) {
        val options = Options.parse(args)
        val selected = if (options.scenario == "all") names else listOf(options.scenario)
        require(options.scenario == "trace-policy" || names.containsAll(selected)) { "Unknown scenario" }
        val samples = mutableListOf<Sample>()
        for (name in selected) {
            for (index in 0 until options.warmups + options.runs) {
                val sample = run(name, options, index)
                samples.add(sample)
                System.err.printf(
                    Locale.ROOT,
                    "%s sample=%d operations=%d backend=%d seconds=%.6f%n",
                    name,
                    index,
                    sample.operations,
                    sample.backendCalls,
                    sample.seconds,
                )
            }
        }
        val report =
            linkedMapOf<String, Any?>(
                "schemaVersion" to 1,
                "runtime" to System.getProperty("java.runtime.version"),
                "vm" to System.getProperty("java.vm.name"),
                "architecture" to System.getProperty("os.arch"),
                "os" to System.getProperty("os.name") + " " + System.getProperty("os.version"),
                "processors" to Runtime.getRuntime().availableProcessors(),
                "stopwatchFrequency" to 1_000_000_000L,
                "jvmArguments" to ManagementFactory.getRuntimeMXBean().inputArguments,
                "cacheSha256" to hash(Path.of(Caffeine::class.java.protectionDomain.codeSource.location.toURI())),
            )
        val classHashes = classHashes(Path.of(ScenarioProbe::class.java.protectionDomain.codeSource.location.toURI()))
        report["harnessClassHashes"] = classHashes
        report["harnessSha256"] = hash(json(classHashes).toByteArray(StandardCharsets.UTF_8))
        report["argv"] = args.toList()
        report["options"] = options
        report["traceSha256"] = options.traceFile?.let { hash(Path.of(it)) }
        report["allocationScope"] =
            "driver-thread allocated bytes during timed scenario; excludes background maintenance, loader and callback workers; not directly comparable to .NET whole-process bytes; -1 if unavailable"
        report["timingScope"] =
            "closed-loop wall time including scenario actions, inline validation and final maintenance; excludes builder, trace and object setup, final snapshot validation and JSON; no per-request latency percentiles"
        report["samples"] = samples
        val output = json(report)
        if (options.output == null) {
            println(output)
        } else {
            val path = Path.of(options.output).toAbsolutePath()
            Files.createDirectories(path.parent)
            Files.writeString(path, output + System.lineSeparator(), StandardCharsets.UTF_8)
        }
    }

    private fun classHashes(location: Path): Map<String, String> {
        val hashes = TreeMap<String, String>()
        fun harnessClass(name: String) = name.startsWith("ScenarioProbe") && name.endsWith(".class")
        if (Files.isDirectory(location)) {
            Files.list(location).use { paths ->
                paths
                    .filter { harnessClass(it.fileName.toString()) }
                    .forEach { hashes[it.fileName.toString()] = hash(it) }
            }
        } else {
            JarFile(location.toFile()).use { jar ->
                for (entry in jar.entries()) {
                    if (harnessClass(entry.name)) {
                        hashes[entry.name] = jar.getInputStream(entry).use { hash(it.readAllBytes()) }
                    }
                }
            }
        }
        check(hashes.isNotEmpty(), "harness classes missing")
        return hashes
    }

    private fun run(name: String, options: Options, index: Int): Sample {
        if (name == "sync-fanin-contract" || name == "manual-sync-fanin-contract") {
            return syncFanIn(name, options, index)
        }
        if (name == "weak-key-cleanup" || name == "weak-value-cleanup") return referenceCleanup(name, options, index)
        val clock = AtomicLong()
        val trace = options.traceFile?.let(::readTrace) ?: trace(options.cycles, options.seed, options.capacity * 4)
        check(trace.size == options.cycles && trace.all { it in 0..1_000_000 }, "trace length/key bounds")
        val keys = Array(maxOf(options.capacity * 4 + 32, trace.max() + 1)) { Key(it) }
        val values = Array(keys.size) { Value(it + 1, it % 4 + 1) }
        val alternate = Array(keys.size) { Value(it + 10001, it % 2 + 1) }
        val weightReplacementTrace =
            if (name == "weight-replace") weightReplacementTrace(trace, options.capacity / 8, values) else null
        val previousWeightValues = if (name == "weight-replace") arrayOfNulls<Value>(options.capacity / 8) else null
        val expectedWeightChanges =
            if (name == "weight-replace") {
                options.cycles - trace.map { it % (options.capacity / 8) }.distinct().size
            } else {
                0
            }
        val callbacks = AtomicLong()
        // A fresh builder keeps the size cases unweighted; no weigher on their hit path.
        val builder =
            if (name.startsWith("weight")) {
                Caffeine.newBuilder()
                    .weigher<Any, Any> { _, value -> (value as Value).weight }
                    .maximumWeight(options.capacity.toLong())
            } else {
                Caffeine.newBuilder().maximumSize(options.capacity.toLong())
            }
        builder.ticker(clock::get)
        if (options.statistics) builder.recordStats()
        if (name in listOf("ttl", "ttl-cleanup", "runtime-expiry")) builder.expireAfterWrite(duration)
        if (name in listOf("tti", "tti-cleanup", "runtime-access")) builder.expireAfterAccess(duration)
        if (name in listOf("variable", "variable-update", "variable-cleanup", "runtime-variable", "variable-put")) {
            builder.expireAfter(VariableExpiry())
        }
        if (name == "refresh-auto" || name == "runtime-refresh") builder.refreshAfterWrite(duration)
        if (name == "weak-key-lookup") builder.weakKeys()
        if (name == "weak-value-lookup") builder.weakValues()
        if (name == "eviction-listener") builder.evictionListener<Key, Value> { _, _, _ -> callbacks.incrementAndGet() }
        if (name == "removal-listener") builder.removalListener<Key, Value> { _, _, _ -> callbacks.incrementAndGet() }
        val asyncMode = name.contains("async")
        val loader = Loader(values, name.startsWith("prefetch"), name.contains("gated"))
        val asyncCache: AsyncCache<Key, Value>? =
            if (asyncMode) {
                if (name.startsWith("manual")) builder.buildAsync() else builder.buildAsync(loader)
            } else {
                null
            }
        val cache: Cache<Key, Value> =
            if (asyncMode) {
                asyncCache!!.synchronous()
            } else {
                if (
                    name in
                    listOf(
                        "sync-miss",
                        "refresh-explicit",
                        "refresh-auto",
                        "runtime-refresh",
                        "bulk-sync",
                        "prefetch-sync",
                    )
                ) {
                    builder.build(loader)
                } else {
                    builder.build()
                }
            }
        if (name.endsWith("lookup")) {
            for (k in 0 until options.capacity / 2) cache.put(keys[k], values[k])
            drain(cache)
            check((cache.getIfPresent(Key(0)) != null) == (name != "weak-key-lookup"), "reference key equality")
        }
        val model = HashMap<Int, Value>()
        val c = Counters()
        var weightChanges = 0
        var drains = 0
        val utcStarted = Instant.now()
        val allocationBefore = allocation()
        val started = System.nanoTime()
        for (cycle in 0 until options.cycles) {
            var id = trace[cycle]
            when (name) {
                "size-churn",
                "weight-churn",
                "eviction-listener",
                -> {
                    cache.put(keys[id], values[id])
                    c.operations++
                }

                "trace-policy" -> {
                    val traced = cache.getIfPresent(keys[id])
                    if (traced != null) {
                        check(traced === values[id], "trace value")
                        c.hits++
                        c.checksum += traced.id
                    } else {
                        c.misses++
                        cache.put(keys[id], values[id])
                        c.operations++
                    }
                    c.operations++
                    drains += drain(cache)
                }

                "weight-replace" -> {
                    id %= options.capacity / 8
                    val replacement = weightReplacementTrace!![cycle]
                    val previousWeightValue = previousWeightValues!![id]
                    if (previousWeightValue != null) {
                        check(
                            previousWeightValue !== replacement && previousWeightValue.weight != replacement.weight,
                            "replacement must change value identity and weight for the same key",
                        )
                        weightChanges++
                    }
                    cache.put(keys[id], replacement)
                    c.operations++
                    val replaced = cache.getIfPresent(keys[id])
                    c.operations++
                    check(replaced === replacement, "replacement lost")
                    c.checksum += replaced!!.id
                    previousWeightValues[id] = replaced
                    c.hits++
                }

                "mixed" -> {
                    id %= options.capacity / 2
                    if (cycle % 10 < 2) {
                        cache.put(keys[id], values[id])
                        model[id] = values[id]
                    } else if (cycle % 10 == 2) {
                        cache.invalidate(keys[id])
                        model.remove(id)
                    } else {
                        val mixed = cache.getIfPresent(keys[id])
                        check((mixed != null) == model.containsKey(id), "mixed presence")
                        if (mixed != null) {
                            check(mixed === model[id], "mixed value")
                            c.checksum += mixed.id
                            c.hits++
                        } else {
                            c.misses++
                        }
                    }
                    c.operations++
                }

                "ttl",
                "tti",
                "variable",
                -> {
                    cache.put(keys[0], values[0])
                    c.operations++
                    clock.addAndGet(6_000_000)
                    read(cache, keys[0], values[0], true, c)
                    clock.addAndGet(6_000_000)
                    read(cache, keys[0], values[0], name != "ttl", c)
                    clock.addAndGet(11_000_000)
                    read(cache, keys[0], values[0], false, c)
                }

                "variable-update" -> {
                    cache.put(keys[0], values[0])
                    c.operations++
                    clock.addAndGet(6_000_000)
                    cache.put(keys[0], alternate[0])
                    c.operations++
                    clock.addAndGet(6_000_000)
                    read(cache, keys[0], alternate[0], true, c)
                    clock.addAndGet(11_000_000)
                    read(cache, keys[0], alternate[0], false, c)
                }

                "ttl-cleanup",
                "tti-cleanup",
                "variable-cleanup",
                -> {
                    for (k in 0 until 16) {
                        cache.put(keys[k], values[k])
                        c.operations++
                    }
                    drains += drain(cache)
                    c.operations++
                    check(cache.estimatedSize() == 16L, "expiry prefill not fully registered")
                    clock.addAndGet(2_000_000_000)
                    drains += drain(cache)
                    c.operations++
                    check(cache.estimatedSize() == 0L, "expired entries survived cleanup")
                }

                "runtime-maximum" -> {
                    cache.policy().eviction().orElseThrow().maximum =
                        (if (cycle and 1 == 0) options.capacity else options.capacity / 2).toLong()
                    c.operations++
                    for (k in 0 until 16) {
                        cache.put(keys[(id + k) % keys.size], values[(id + k) % keys.size])
                        c.operations++
                    }
                }

                "runtime-expiry",
                "runtime-access",
                -> {
                    val fixedPolicy =
                        if (name == "runtime-access") {
                            cache.policy().expireAfterAccess().orElseThrow()
                        } else {
                            cache.policy().expireAfterWrite().orElseThrow()
                        }
                    fixedPolicy.expiresAfter = duration
                    c.operations++
                    cache.put(keys[0], values[0])
                    c.operations++
                    clock.addAndGet(6_000_000)
                    fixedPolicy.expiresAfter = Duration.ofMillis(5)
                    c.operations++
                    read(cache, keys[0], values[0], false, c)
                }

                "runtime-variable" -> {
                    cache.put(keys[0], values[0])
                    c.operations++
                    cache.policy().expireVariably().orElseThrow().setExpiresAfter(keys[0], Duration.ofMillis(5))
                    c.operations++
                    clock.addAndGet(6_000_000)
                    read(cache, keys[0], values[0], false, c)
                }

                "removal-listener" -> {
                    cache.put(keys[0], if (cycle and 1 == 0) values[0] else alternate[0])
                    c.operations++
                }

                "variable-put" -> {
                    cache.policy().expireVariably().orElseThrow().put(keys[0], values[0], Duration.ofMillis(5))
                    c.operations++
                    clock.addAndGet(6_000_000)
                    read(cache, keys[0], values[0], false, c)
                }

                "quiet-lookup" -> {
                    id %= options.capacity / 2
                    val quiet = cache.policy().getIfPresentQuietly(keys[id])
                    check(quiet === values[id], "quiet lookup value")
                    c.operations++
                    c.hits++
                    c.checksum += quiet!!.id
                }

                "strong-lookup",
                "weak-key-lookup",
                "weak-value-lookup",
                -> {
                    id %= options.capacity / 2
                    read(cache, keys[id], values[id], true, c)
                }

                "sync-miss",
                "manual-sync-miss",
                -> {
                    cache.invalidate(keys[0])
                    c.operations++
                    val loaded =
                        if (cache is LoadingCache<Key, Value>) cache.get(keys[0]) else cache.get(keys[0], loader::load)
                    check(loaded === values[0], "sync load")
                    c.checksum += loaded.id
                    c.operations++
                }

                "async-completed-miss",
                "manual-async-completed-miss",
                "async-gated-miss",
                "manual-async-gated-miss",
                "async-gated-fanin",
                "manual-async-gated-fanin",
                -> {
                    asyncCache!!.synchronous().invalidate(keys[0])
                    c.operations++
                    val gated = name.contains("gated")
                    if (gated) loader.gate = CompletableFuture()
                    val count = if (gated && name.contains("fanin")) options.fanIn else 1
                    val requests = ArrayList<CompletableFuture<Value>>(count)
                    repeat(count) {
                        requests.add(
                            if (asyncCache is AsyncLoadingCache<Key, Value>) {
                                asyncCache.get(keys[0])
                            } else {
                                asyncCache.get(keys[0], loader::asyncLoad)
                            },
                        )
                        c.operations++
                    }
                    if (gated) {
                        check(requests.none { it.isDone }, "gated requests completed before release")
                        loader.gate!!.complete(values[0])
                    }
                    for (request in requests) {
                        val result = request.get(30, TimeUnit.SECONDS)
                        check(result === values[0], "async value")
                        c.checksum += result.id
                    }
                }

                "refresh-explicit",
                "refresh-auto",
                "runtime-refresh",
                -> {
                    if (name == "runtime-refresh") {
                        cache.policy().refreshAfterWrite().orElseThrow().refreshesAfter = duration
                        c.operations++
                    }
                    cache.put(keys[0], alternate[0])
                    c.operations++
                    val refreshing = cache as LoadingCache<Key, Value>
                    if (name != "refresh-explicit") {
                        clock.addAndGet(if (name == "runtime-refresh") 6_000_000 else 11_000_000)
                        if (name == "runtime-refresh") {
                            cache.policy().refreshAfterWrite().orElseThrow().refreshesAfter = Duration.ofMillis(5)
                            c.operations++
                        }
                        val stale = refreshing.get(keys[0])
                        c.operations++
                        check(stale === alternate[0] || stale === values[0], "refresh read")
                        until { refreshing.policy().getIfPresentQuietly(keys[0]) === values[0] }
                    } else {
                        check(refreshing.refresh(keys[0]).get(30, TimeUnit.SECONDS) === values[0], "refresh result")
                        c.operations++
                    }
                    c.checksum += values[0].id
                }

                "bulk-sync",
                "prefetch-sync",
                "bulk-async",
                "prefetch-async",
                -> {
                    val batch = keys.copyOfRange(0, 16).asList()
                    if (asyncMode) {
                        asyncCache!!.synchronous().invalidateAll()
                        c.operations++
                        val result =
                            (asyncCache as AsyncLoadingCache<Key, Value>).getAll(batch).get(30, TimeUnit.SECONDS)
                        validateBulk(result, values, c)
                        c.operations++
                    } else {
                        cache.invalidateAll()
                        c.operations++
                        val result = (cache as LoadingCache<Key, Value>).getAll(batch)
                        validateBulk(result, values, c)
                        c.operations++
                    }
                    if (name.startsWith("prefetch")) {
                        val prefetched =
                            (if (asyncMode) asyncCache!!.synchronous() else cache)
                                .policy()
                                .getIfPresentQuietly(loader.prefetchKey)
                        check(prefetched === values[16], "prefetch not published")
                        c.operations++
                    }
                }

                else -> throw IllegalStateException(name)
            }
        }
        val view = if (asyncMode) asyncCache!!.synchronous() else cache
        drains += drain(view)
        if (name == "removal-listener") until { callbacks.get() == options.cycles - 1L }
        val ended = System.nanoTime()
        val allocationAfter = allocation()
        val allocated = if (allocationBefore < 0 || allocationAfter < 0) -1 else allocationAfter - allocationBefore
        val utcEnded = Instant.now()
        if (name == "weight-replace") {
            check(weightChanges == expectedWeightChanges, "weighted replacement transition count")
        }
        val policy = view.policy().eviction().orElseThrow()
        val finalCount = view.estimatedSize()
        val weight = policy.weightedSize().orElse(finalCount)
        if (name == "weight-replace") {
            check(
                finalCount == (options.cycles - expectedWeightChanges).toLong() &&
                    weight == previousWeightValues!!.sumOf { it?.weight?.toLong() ?: 0L },
                "weighted replacement final residents/weight metadata",
            )
        }
        if (!options.statistics) {
            check(view.stats().hitCount() == 0L && view.stats().missCount() == 0L, "statistics disabled")
        } else if (name.endsWith("lookup") && name != "quiet-lookup") {
            check(view.stats().hitCount() >= options.cycles, "statistics hit recording")
        }
        if (options.statistics && name == "quiet-lookup") {
            check(view.stats().hitCount() == 1L && view.stats().missCount() == 0L, "quiet lookup altered statistics")
        }
        check(finalCount <= options.capacity && weight <= policy.maximum, "capacity bound")
        val expectedBackend =
            if (
                name.contains("miss") ||
                name.contains("fanin") ||
                name.startsWith("refresh") ||
                name == "runtime-refresh"
            ) {
                options.cycles.toLong()
            } else {
                0L
            }
        check(loader.calls.get() == expectedBackend, "backend invocation count")
        val expectedBulk = if (name.startsWith("bulk") || name.startsWith("prefetch")) options.cycles.toLong() else 0L
        check(loader.bulkCalls.get() == expectedBulk, "true bulk count")
        if (name in listOf("size-churn", "weight-churn", "eviction-listener", "runtime-maximum")) {
            for ((key, value) in policy.coldest(options.capacity * 4)) {
                check(value === values[key.id], "resident value corrupt")
                c.checksum += value.id
            }
        }
        Reference.reachabilityFence(keys)
        Reference.reachabilityFence(values)
        Reference.reachabilityFence(alternate)
        return Sample(
            name,
            index,
            index < options.warmups,
            c.operations,
            c.checksum,
            c.hits,
            c.misses,
            loader.calls.get(),
            loader.bulkCalls.get(),
            callbacks.get(),
            finalCount,
            weight,
            policy.maximum,
            null,
            drains,
            utcStarted.toString(),
            utcEnded.toString(),
            started,
            ended,
            (ended - started) / 1e9,
            allocated,
            true,
        )
    }

    private fun read(cache: Cache<Key, Value>, key: Key, expected: Value, present: Boolean, c: Counters) {
        val value = cache.getIfPresent(key)
        c.operations++
        check((value != null) == present, "expiry/lookup presence")
        if (value != null) {
            check(value === expected, "lookup value")
            c.checksum += value.id
            c.hits++
        } else {
            c.misses++
        }
    }

    private fun referenceCleanup(name: String, options: Options, index: Int): Sample {
        val builder = Caffeine.newBuilder().maximumSize(options.capacity.toLong())
        val weakKey = name == "weak-key-cleanup"
        if (weakKey) builder.weakKeys() else builder.weakValues()
        if (options.statistics) builder.recordStats()
        val cache: Cache<Key, Value> = builder.build()
        val fixture = populateUnrooted(cache, weakKey)
        val utcStart = Instant.now()
        val start = System.nanoTime()
        var passes = 0
        do {
            System.gc()
            cache.cleanUp()
            passes++
        } while ((!fixture.weak.refersTo(null) || cache.estimatedSize() != 0L) && passes < 32)
        check(
            fixture.weak.refersTo(null) && cache.estimatedSize() == 0L,
            "weak target not collected/cleaned within bounded forced GC passes",
        )
        Reference.reachabilityFence(fixture.retained)
        val end = System.nanoTime()
        return Sample(
            name,
            index,
            index < options.warmups,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            options.capacity.toLong(),
            null,
            passes,
            utcStart.toString(),
            Instant.now().toString(),
            start,
            end,
            (end - start) / 1e9,
            -1,
            true,
        )
    }

    private data class ReferenceFixture(val weak: WeakReference<Any>, val retained: Any)

    private fun syncFanIn(name: String, options: Options, index: Int): Sample {
        val invoked = CountDownLatch(options.fanIn)
        val completed = CountDownLatch(options.fanIn)
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val calls = AtomicLong()
        val key = Key(0)
        val expected = Value(1, 1)
        val load =
            Function<Key, Value> {
                calls.incrementAndGet()
                entered.countDown()
                try {
                    check(release.await(30, TimeUnit.SECONDS), "loader gate timeout")
                } catch (exception: InterruptedException) {
                    Thread.currentThread().interrupt()
                    throw IllegalStateException(exception)
                }
                expected
            }
        val builder = Caffeine.newBuilder().maximumSize(options.capacity.toLong())
        if (options.statistics) builder.recordStats()
        val cache: Cache<Key, Value> =
            if (name == "sync-fanin-contract") builder.build(load::apply) else builder.build()
        val values = arrayOfNulls<Value>(options.fanIn)
        val failures = ConcurrentLinkedQueue<Throwable>()
        val threads = arrayOfNulls<Thread>(options.fanIn)
        val utcStart = Instant.now()
        val start = System.nanoTime()
        try {
            for (worker in threads.indices) {
                val thread =
                    Thread {
                        invoked.countDown()
                        try {
                            values[worker] = if (cache is LoadingCache<Key, Value>) cache.get(key) else cache.get(key, load)
                        } catch (failure: Throwable) {
                            failures.add(failure)
                        } finally {
                            completed.countDown()
                        }
                    }
                threads[worker] = thread
                thread.isDaemon = true
                thread.start()
            }
            check(invoked.await(30, TimeUnit.SECONDS) && entered.await(30, TimeUnit.SECONDS), "callers did not enter")
            check(completed.count == options.fanIn.toLong(), "a gated call completed before release")
            release.countDown()
            check(completed.await(30, TimeUnit.SECONDS), "callers failed to finish")
            check(
                failures.isEmpty() && calls.get() == 1L && values.all { it === expected },
                "sync fan-in result/backend",
            )
            val drains = drain(cache)
            val end = System.nanoTime()
            return Sample(
                name,
                index,
                index < options.warmups,
                0,
                options.fanIn.toLong(),
                0,
                0,
                calls.get(),
                0,
                0,
                cache.estimatedSize(),
                cache.estimatedSize(),
                options.capacity.toLong(),
                null,
                drains,
                utcStart.toString(),
                Instant.now().toString(),
                start,
                end,
                (end - start) / 1e9,
                -1,
                true,
            )
        } finally {
            release.countDown()
            for (thread in threads) {
                if (thread != null) {
                    thread.join(30_000)
                    check(!thread.isAlive, "sync caller did not terminate")
                }
            }
        }
    }

    private fun populateUnrooted(cache: Cache<Key, Value>, weakKey: Boolean): ReferenceFixture {
        val key = Key(1)
        val value = Value(2, 1)
        cache.put(key, value)
        drain(cache)
        return ReferenceFixture(WeakReference(if (weakKey) key else value), if (weakKey) value else key)
    }

    private fun validateBulk(result: Map<Key, Value>, values: Array<Value>, c: Counters) {
        check(result.size == 16, "bulk count")
        for ((key, value) in result) {
            check(key.id < 16 && value === values[key.id], "bulk value")
            c.checksum += value.id
        }
    }

    private fun drain(cache: Cache<Key, Value>): Int {
        for (pass in 1..256) {
            cache.cleanUp()
            val policy = cache.policy().eviction().orElseThrow()
            if (policy.weightedSize().orElse(cache.estimatedSize()) <= policy.maximum) return pass
        }
        throw IllegalStateException("maintenance failed to converge")
    }

    private fun until(predicate: BooleanSupplier) {
        val start = System.nanoTime()
        while (!predicate.asBoolean) {
            if (System.nanoTime() - start > 30_000_000_000L) {
                throw IllegalStateException("callback/refresh failed to finish")
            }
            Thread.yield()
        }
    }

    private fun trace(count: Int, seed: Int, bound: Int): IntArray {
        var state = seed
        return IntArray(count) {
            state = state * 1664525 + 1013904223
            Integer.remainderUnsigned(state, bound)
        }
    }

    private fun weightReplacementTrace(trace: IntArray, keyCount: Int, values: Array<Value>): Array<Value> {
        val versions = IntArray(keyCount)
        val changed = Array(values.size) { Value(values[it].id + 10000, values[it].weight % 4 + 1) }
        return Array(trace.size) {
            val key = trace[it] % keyCount
            if (versions[key]++ and 1 == 0) values[key] else changed[key]
        }
    }

    private fun readTrace(path: String): IntArray {
        val text = Files.readString(Path.of(path), StandardCharsets.UTF_8).trim()
        check(text.startsWith("[") && text.endsWith("]"), "trace must be a JSON integer array")
        val body = text.substring(1, text.length - 1).trim()
        if (body.isEmpty()) return IntArray(0)
        return body.split(',').map { it.trim().toInt() }.toIntArray()
    }

    private fun allocation(): Long {
        val bean = ManagementFactory.getThreadMXBean()
        if (bean is com.sun.management.ThreadMXBean && bean.isThreadAllocatedMemorySupported) {
            if (!bean.isThreadAllocatedMemoryEnabled) bean.isThreadAllocatedMemoryEnabled = true
            return bean.getThreadAllocatedBytes(Thread.currentThread().threadId())
        }
        return -1
    }

    private fun hash(path: Path) = hash(Files.readAllBytes(path))

    private fun hash(bytes: ByteArray) = HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes))

    private fun check(condition: Boolean, message: String) {
        if (!condition) throw IllegalStateException(message)
    }

    private data class Key(val id: Int)

    private data class Value(val id: Int, val weight: Int)

    private class Counters {
        var operations = 0L
        var checksum = 0L
        var hits = 0L
        var misses = 0L
    }

    private class VariableExpiry : Expiry<Key, Value> {
        override fun expireAfterCreate(key: Key, value: Value, currentTime: Long) = duration.toNanos()

        override fun expireAfterUpdate(key: Key, value: Value, currentTime: Long, currentDuration: Long) =
            duration.toNanos()

        override fun expireAfterRead(key: Key, value: Value, currentTime: Long, currentDuration: Long) =
            duration.toNanos()
    }

    private class Loader(private val values: Array<Value>, private val prefetch: Boolean, private val gated: Boolean) :
        CacheLoader<Key, Value> {
        val calls = AtomicLong()
        val bulkCalls = AtomicLong()
        val prefetchKey = Key(16)
        var gate: CompletableFuture<Value>? = null

        override fun load(key: Key): Value {
            calls.incrementAndGet()
            return values[key.id]
        }

        override fun asyncLoad(key: Key, executor: Executor): CompletableFuture<Value> {
            calls.incrementAndGet()
            return if (gated) gate!! else CompletableFuture.completedFuture(values[key.id])
        }

        override fun loadAll(keys: Set<Key>): Map<Key, Value> {
            bulkCalls.incrementAndGet()
            val result = HashMap<Key, Value>()
            for (key in keys) result[key] = values[key.id]
            if (prefetch) result[prefetchKey] = values[16]
            return result
        }

        override fun asyncLoadAll(keys: Set<Key>, executor: Executor): CompletableFuture<out Map<Key, Value>> =
            CompletableFuture.completedFuture(loadAll(keys))
    }

    private data class Sample(
        val name: String,
        val index: Int,
        val warmup: Boolean,
        val operations: Long,
        val checksum: Long,
        val hits: Long,
        val misses: Long,
        val backendCalls: Long,
        val bulkCalls: Long,
        val callbacks: Long,
        val finalCount: Long,
        val finalWeight: Long,
        val maximum: Long,
        val maintenanceBacklog: Long?,
        val drainPasses: Int,
        val utcStarted: String,
        val utcEnded: String,
        val startedTimestamp: Long,
        val endedTimestamp: Long,
        val seconds: Double,
        val allocatedBytes: Long,
        val validated: Boolean,
    )

    private data class Options(
        val scenario: String,
        val cycles: Int,
        val capacity: Int,
        val fanIn: Int,
        val seed: Int,
        val warmups: Int,
        val runs: Int,
        val statistics: Boolean,
        val output: String?,
        val traceFile: String?,
    ) {
        companion object {
            fun parse(args: Array<String>): Options {
                require(args.size % 2 == 0) { "Options require name/value pairs" }
                val map = HashMap<String, String>()
                val allowed =
                    listOf(
                        "--scenario",
                        "--cycles",
                        "--capacity",
                        "--fan-in",
                        "--seed",
                        "--warmups",
                        "--runs",
                        "--statistics",
                        "--output",
                        "--trace-file",
                    )
                for (i in args.indices step 2) {
                    require(
                        args[i] in allowed && map.putIfAbsent(args[i], args[i + 1]) == null,
                    ) {
                        "Unknown/duplicate option"
                    }
                }
                val stats = map.getOrDefault("--statistics", "off")
                require(stats == "on" || stats == "off") { "--statistics on|off" }
                check(
                    (map.getOrDefault("--scenario", "all") == "trace-policy") == map.containsKey("--trace-file"),
                    "trace-policy requires --trace-file exclusively",
                )
                return Options(
                    map.getOrDefault("--scenario", "all"),
                    number(map, "--cycles", 1024, 2, 1000000),
                    number(map, "--capacity", 1024, 64, 1000000),
                    number(map, "--fan-in", 16, 2, 1024),
                    number(map, "--seed", 419, 0, Int.MAX_VALUE),
                    number(map, "--warmups", 2, 0, 100),
                    number(map, "--runs", 3, 1, 100),
                    stats == "on",
                    map["--output"],
                    map["--trace-file"],
                )
            }

            private fun number(map: Map<String, String>, key: String, fallback: Int, min: Int, max: Int): Int {
                val value = map.getOrDefault(key, fallback.toString()).toInt()
                require(value in min..max) { key }
                return value
            }
        }
    }

    private fun json(value: Any?): String {
        if (value == null) return "null"
        if (value is Number || value is Boolean) return value.toString()
        if (value is Map<*, *>) {
            return value.entries.joinToString(",", "{", "}") { json(it.key.toString()) + ":" + json(it.value) }
        }
        if (value is Iterable<*>) return value.joinToString(",", "[", "]") { json(it) }
        if (value is Sample) {
            return json(
                linkedMapOf(
                    "name" to value.name,
                    "index" to value.index,
                    "warmup" to value.warmup,
                    "operations" to value.operations,
                    "checksum" to value.checksum,
                    "hits" to value.hits,
                    "misses" to value.misses,
                    "backendCalls" to value.backendCalls,
                    "bulkCalls" to value.bulkCalls,
                    "callbacks" to value.callbacks,
                    "finalCount" to value.finalCount,
                    "finalWeight" to value.finalWeight,
                    "maximum" to value.maximum,
                    "maintenanceBacklog" to value.maintenanceBacklog,
                    "drainPasses" to value.drainPasses,
                    "utcStarted" to value.utcStarted,
                    "utcEnded" to value.utcEnded,
                    "startedTimestamp" to value.startedTimestamp,
                    "endedTimestamp" to value.endedTimestamp,
                    "seconds" to value.seconds,
                    "allocatedBytes" to value.allocatedBytes,
                    "validated" to value.validated,
                ),
            )
        }
        if (value is Options) {
            return json(
                linkedMapOf(
                    "scenario" to value.scenario,
                    "cycles" to value.cycles,
                    "capacity" to value.capacity,
                    "fanIn" to value.fanIn,
                    "seed" to value.seed,
                    "warmups" to value.warmups,
                    "runs" to value.runs,
                    "statistics" to value.statistics,
                    "output" to value.output,
                    "traceFile" to value.traceFile,
                ),
            )
        }
        val out = StringBuilder("\"")
        for (ch in value.toString()) {
            when (ch) {
                '"' -> out.append("\\\"")
                '\\' -> out.append("\\\\")
                '\n' -> out.append("\\n")
                '\r' -> out.append("\\r")
                '\t' -> out.append("\\t")
                else -> if (ch.code < 32) out.append(String.format(Locale.ROOT, "\\u%04x", ch.code)) else out.append(ch)
            }
        }
        return out.append('"').toString()
    }
}
