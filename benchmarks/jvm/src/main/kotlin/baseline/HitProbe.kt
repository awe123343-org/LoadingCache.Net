package baseline

import com.github.benmanes.caffeine.cache.Cache
import com.github.benmanes.caffeine.cache.Caffeine
import com.google.common.cache.CacheBuilder
import com.sun.management.ThreadMXBean
import java.io.IOException
import java.lang.management.ManagementFactory
import java.net.URISyntaxException
import java.nio.file.Path
import java.security.MessageDigest
import java.security.NoSuchAlgorithmException
import java.util.concurrent.BrokenBarrierException
import java.util.concurrent.CyclicBarrier
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import kotlin.concurrent.thread
import kotlin.io.path.Path
import kotlin.io.path.createDirectories
import kotlin.io.path.inputStream
import kotlin.io.path.isRegularFile
import kotlin.io.path.writeText
import kotlin.system.exitProcess

/**
 * Closed-loop read-hit probe for a size-bounded Caffeine or Guava cache.
 *
 * This intentionally has no JMH dependency. The parent benchmark launches one fresh process for each case, using the
 * contract in docs/benchmarks/caffeine-read-20260913/methodology.md.
 */
object HitProbe {
    private const val SCHEMA_VERSION = 1
    private const val DEFAULT_CAPACITY = 1_024
    private const val DEFAULT_RESIDENTS = 1_024
    private const val DEFAULT_WORKERS = 1
    private const val DEFAULT_WARMUPS = 3
    private const val DEFAULT_RUNS = 5
    private const val DEFAULT_DURATION_MILLIS = 500L
    private const val CHUNK_SIZE = 1_024
    private const val START_STRIDE = 17
    private const val WATCHDOG_SECONDS = 30L
    private const val CAFFEINE_VERSION = "3.2.4"

    enum class Engine {
        CAFFEINE,
        GUAVA,
    }

    fun run(engine: Engine, args: Array<String>) {
        val exitCode = execute(engine, args)
        if (exitCode != 0) exitProcess(exitCode)
    }

    private fun execute(engine: Engine, args: Array<String>): Int {
        val options =
            try {
                Options.parse(engine, args)
            } catch (exception: IllegalArgumentException) {
                System.err.println("error: ${exception.message}")
                System.err.println(Options.usage(engine))
                return 2
            }
        val samples = mutableListOf<Sample>()
        var probe: Probe? = null
        var failure: Throwable? = null
        try {
            probe = Probe(options)
            for (sampleIndex in 0 until options.warmups + options.runs) {
                val sample = probe.runSample(sampleIndex, sampleIndex < options.warmups)
                samples.add(sample)
                check(sample.boundsPassed) {
                    "validation failed for sample $sampleIndex: ${sample.validationFailure}"
                }
            }
        } catch (exception: Throwable) {
            failure = exception
        } finally {
            try {
                probe?.close()
            } catch (exception: Throwable) {
                if (failure == null) failure = exception else failure.addSuppressed(exception)
            }
        }
        val report = ReportWriter.write(options, samples, failure)
        try {
            val output = options.output?.toAbsolutePath()
            if (output == null) {
                println(report)
            } else {
                output.parent?.createDirectories()
                output.writeText(report + System.lineSeparator())
                System.err.println("wrote $output")
            }
        } catch (exception: IOException) {
            System.err.println("error writing report: ${exception.message}")
            return 1
        }
        if (failure != null) {
            failure.printStackTrace(System.err)
            return 1
        }
        return 0
    }

    // Engine branching and adapters stay outside the timed lookup loop.
    private class ResidentCache(options: Options) {
        val caffeine: Cache<Any, Int>?
        val guava: com.google.common.cache.Cache<Any, Int>?

        init {
            if (options.engine == Engine.CAFFEINE) {
                val builder = Caffeine.newBuilder().maximumSize(options.capacity.toLong())
                if (options.statistics) builder.recordStats()
                caffeine = builder.build()
                guava = null
            } else {
                val builder =
                    CacheBuilder.newBuilder()
                        .maximumSize(options.capacity.toLong())
                        .concurrencyLevel(options.concurrencyLevel)
                if (options.statistics) builder.recordStats()
                guava = builder.build()
                caffeine = null
            }
        }

        val size: Int
            get() = caffeine?.asMap()?.size ?: checkNotNull(guava).asMap().size

        fun put(key: Any, value: Int) {
            if (caffeine != null) caffeine.put(key, value) else checkNotNull(guava).put(key, value)
        }

        fun cleanUp() {
            if (caffeine != null) caffeine.cleanUp() else checkNotNull(guava).cleanUp()
        }

        fun stats(): Counts {
            if (caffeine != null) {
                val stats = caffeine.stats()
                return Counts(stats.hitCount(), stats.missCount())
            }
            val stats = checkNotNull(guava).stats()
            return Counts(stats.hitCount(), stats.missCount())
        }

        fun weightedSize(): Long =
            caffeine?.policy()?.eviction()?.orElseThrow()?.weightedSize()?.orElse(size.toLong()) ?: size.toLong()
    }

    private class Counts(val hits: Long, val misses: Long)

    private class Probe(private val options: Options) : AutoCloseable {
        // Keep keys boxed through the timed lookup: Array<Int> would unbox and rebox on access.
        private val cache = ResidentCache(options)
        private val keys = Array<Any>(options.residents) { it }
        private val workers: PersistentWorkers
        private val gcCollectorNames: List<String>

        init {
            for (index in keys.indices) cache.put(keys[index], index + 1)
            cache.cleanUp()
            val residentCount = cache.size.toLong()
            check(residentCount == options.residents.toLong()) {
                "initial resident count mismatch: expected ${options.residents}, actual $residentCount"
            }
            val weightedSize = cache.weightedSize()
            check(weightedSize == options.residents.toLong()) {
                "initial weighted size mismatch: expected ${options.residents}, actual $weightedSize"
            }
            workers = PersistentWorkers(cache, keys, options)
            gcCollectorNames = ManagementFactory.getGarbageCollectorMXBeans().map { it.name }
        }

        fun runSample(sampleIndex: Int, warmup: Boolean): Sample {
            val beforeStats = cache.stats()
            val beforeGc = GcCounts.capture(gcCollectorNames)
            val timed = workers.runIteration(if (options.pattern == "hot") 0 else options.residents - 1)
            val afterStats = cache.stats()
            val cleanupStarted = System.nanoTime()
            cache.cleanUp()
            val cleanupSeconds = elapsedSeconds(cleanupStarted, System.nanoTime())
            val residentCount = cache.size.toLong()
            val weightedSize = cache.weightedSize()
            val gcDelta = GcCounts.capture(gcCollectorNames).minus(beforeGc)
            var operations = 0L
            var checksum = 0L
            var misses = 0L
            var allocatedBytes = 0L
            var allocationMeasured = true
            val workersOperations = LongArray(options.workers)
            for (worker in 0 until options.workers) {
                val result = timed.workerResults[worker]
                operations += result.operations
                checksum += result.checksum
                misses += result.misses
                if (result.allocatedBytes < 0) allocationMeasured = false else allocatedBytes += result.allocatedBytes
                workersOperations[worker] = result.operations
            }
            if (!allocationMeasured) allocatedBytes = -1
            var expectedChecksum = 0L
            var checksumPassed = true
            var operationsPassed = true
            for (worker in 0 until options.workers) {
                val result = timed.workerResults[worker]
                val expected = expectedChecksum(worker, result.operations, timed.mask, options.residents)
                expectedChecksum += expected
                checksumPassed = checksumPassed && result.checksum == expected
                operationsPassed = operationsPassed && result.operations > 0
            }
            val hitsDelta = afterStats.hits - beforeStats.hits
            val missesDelta = afterStats.misses - beforeStats.misses
            val statsPassed =
                if (options.statistics) {
                    hitsDelta == operations && missesDelta == 0L
                } else {
                    hitsDelta == 0L && missesDelta == 0L
                }
            val validationFailure =
                when {
                    !checksumPassed || checksum != expectedChecksum -> "checksum mismatch"

                    !operationsPassed -> "worker completed zero operations"

                    misses != 0L -> "lookup misses=$misses"

                    residentCount != options.residents.toLong() || weightedSize != options.residents.toLong() ->
                        "resident/weighted bound mismatch: residents=$residentCount, weightedSize=$weightedSize"

                    !statsPassed -> "statistics mismatch: hitsDelta=$hitsDelta, missesDelta=$missesDelta"

                    else -> null
                }
            return Sample(
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
                validationFailure == null,
                validationFailure,
            )
        }

        override fun close() {
            workers.close()
            cache.cleanUp()
            val residentCount = cache.size
            check(residentCount == options.residents) { "cache changed during final cleanup: $residentCount" }
        }
    }

    private class PersistentWorkers(
        private val cache: ResidentCache,
        private val keys: Array<Any>,
        private val options: Options,
    ) : AutoCloseable {
        private val readyBarrier = CyclicBarrier(options.workers + 1)
        private val startBarrier = CyclicBarrier(options.workers + 1)
        private val finishBarrier = CyclicBarrier(options.workers + 1)
        private val workerResults = Array(options.workers) { WorkerResult() }
        private val allocationBefore = LongArray(options.workers)
        private val threadBean = allocationBean()

        @Volatile private var deadlineNanos = 0L

        @Volatile private var mask = 0

        @Volatile private var stopping = false

        @Volatile private var failure: Throwable? = null
        private val threads =
            Array(options.workers) { worker ->
                thread(start = false, isDaemon = true, name = "LoadingCache.HitProbe.worker-$worker") {
                    val caffeine = cache.caffeine
                    if (caffeine != null) {
                        runWorker(worker) { caffeine.getIfPresent(it) }
                    } else {
                        val guava = checkNotNull(cache.guava)
                        runWorker(worker) { guava.getIfPresent(it) }
                    }
                }
            }

        init {
            for (thread in threads) thread.start()
            await(readyBarrier, "workers did not become ready")
            if (failure != null) throw IllegalStateException("worker initialization failed", failure)
        }

        fun runIteration(nextMask: Int): TimedResult {
            if (failure != null) throw IllegalStateException("worker failed before iteration", failure)
            mask = nextMask
            if (threadBean != null && threadBean.isThreadAllocatedMemorySupported) {
                for (worker in 0 until options.workers) {
                    allocationBefore[worker] = threadBean.getThreadAllocatedBytes(threads[worker].threadId())
                }
            } else {
                allocationBefore.fill(-1L)
            }
            val started = System.nanoTime()
            deadlineNanos = started + TimeUnit.MILLISECONDS.toNanos(options.durationMillis)
            try {
                await(startBarrier, "workers did not start")
                await(finishBarrier, "workers did not finish")
            } catch (exception: RuntimeException) {
                if (!stopping) signalFailure(exception)
                throw exception
            }
            val wallSeconds = elapsedSeconds(started, System.nanoTime())
            if (failure != null) throw IllegalStateException("worker failed during iteration", failure)
            return TimedResult(wallSeconds, mask, Array(workerResults.size) { workerResults[it].copy() })
        }

        private inline fun runWorker(workerIndex: Int, lookup: (Any) -> Int?) {
            try {
                await(readyBarrier, "worker ready barrier failed")
                while (!stopping) {
                    try {
                        await(startBarrier, "worker start barrier failed")
                    } catch (exception: RuntimeException) {
                        if (!stopping) signalFailure(exception)
                        return
                    }
                    if (stopping) return
                    val workerMask = mask.toLong()
                    var index = workerIndex.toLong() * START_STRIDE
                    var operations = 0L
                    var checksum = 0L
                    var misses = 0L
                    do {
                        repeat(CHUNK_SIZE) {
                            val value = lookup(keys[(index and workerMask).toInt()])
                            if (value == null) misses++ else checksum += value
                            operations++
                            index++
                        }
                    } while (!stopping && System.nanoTime() < deadlineNanos)
                    var allocatedBytes = -1L
                    if (threadBean != null && allocationBefore[workerIndex] >= 0) {
                        val after = threadBean.getThreadAllocatedBytes(Thread.currentThread().threadId())
                        allocatedBytes = maxOf(0, after - allocationBefore[workerIndex])
                    }
                    val result = workerResults[workerIndex]
                    result.operations = operations
                    result.checksum = checksum
                    result.misses = misses
                    result.allocatedBytes = allocatedBytes
                    try {
                        await(finishBarrier, "worker finish barrier failed")
                    } catch (exception: RuntimeException) {
                        if (!stopping) signalFailure(exception)
                        return
                    }
                }
            } catch (exception: Throwable) {
                if (!stopping) signalFailure(exception)
            }
        }

        private fun signalFailure(exception: Throwable) {
            if (failure == null) failure = exception
            stopping = true
            readyBarrier.reset()
            startBarrier.reset()
            finishBarrier.reset()
            for (thread in threads) if (thread != Thread.currentThread()) thread.interrupt()
        }

        override fun close() {
            stopping = true
            readyBarrier.reset()
            startBarrier.reset()
            finishBarrier.reset()
            for (thread in threads) thread.interrupt()
            for (thread in threads) {
                try {
                    thread.join(TimeUnit.SECONDS.toMillis(WATCHDOG_SECONDS))
                } catch (exception: InterruptedException) {
                    Thread.currentThread().interrupt()
                    throw IllegalStateException("interrupted while stopping worker threads", exception)
                }
                if (thread.isAlive) throw IllegalStateException("worker did not stop within watchdog", failure)
            }
            if (failure != null) throw IllegalStateException("persistent worker failed", failure)
        }

        private fun allocationBean(): ThreadMXBean? {
            val bean = ManagementFactory.getThreadMXBean()
            if (bean !is ThreadMXBean || !bean.isThreadAllocatedMemorySupported) return null
            return try {
                if (!bean.isThreadAllocatedMemoryEnabled) bean.isThreadAllocatedMemoryEnabled = true
                bean
            } catch (_: UnsupportedOperationException) {
                null
            } catch (_: SecurityException) {
                null
            }
        }

        private fun await(barrier: CyclicBarrier, description: String) {
            try {
                barrier.await(WATCHDOG_SECONDS, TimeUnit.SECONDS)
            } catch (exception: InterruptedException) {
                Thread.currentThread().interrupt()
                throw IllegalStateException("$description: interrupted", exception)
            } catch (exception: BrokenBarrierException) {
                throw IllegalStateException(description, exception)
            } catch (exception: TimeoutException) {
                throw IllegalStateException(description, exception)
            }
        }
    }

    private data class WorkerResult(
        var operations: Long = 0,
        var checksum: Long = 0,
        var misses: Long = 0,
        var allocatedBytes: Long = 0,
    )

    private class TimedResult(val wallSeconds: Double, val mask: Int, val workerResults: Array<WorkerResult>)

    private class Sample(
        val sampleIndex: Int,
        val warmup: Boolean,
        val wallSeconds: Double,
        val cleanupSeconds: Double,
        val operations: Long,
        val checksum: Long,
        val misses: Long,
        val workersOperations: LongArray,
        val hitsDelta: Long,
        val missesDelta: Long,
        val residentCount: Long,
        val weightedSize: Long,
        val workerAllocatedBytes: Long,
        val gcCollections: GcCounts,
        val boundsPassed: Boolean,
        val validationFailure: String?,
    )

    private data class GcCounts(val values: Map<String, Long>) {
        fun minus(before: GcCounts): GcCounts =
            GcCounts(
                values.mapValues { (name, current) ->
                    val previous = before.values.getOrDefault(name, -1L)
                    if (current < 0 || previous < 0) -1L else maxOf(0L, current - previous)
                },
            )

        companion object {
            fun capture(names: List<String>): GcCounts =
                GcCounts(
                    names.associateWith { name ->
                        ManagementFactory.getGarbageCollectorMXBeans().firstOrNull { it.name == name }?.collectionCount
                            ?: -1L
                    },
                )
        }
    }

    private class Options(val engine: Engine) {
        var concurrencyLevel = 4
        var capacity = DEFAULT_CAPACITY
        var residents = DEFAULT_RESIDENTS
        var pattern = "hot"
        var workers = DEFAULT_WORKERS
        var statistics = false
        var warmups = DEFAULT_WARMUPS
        var runs = DEFAULT_RUNS
        var durationMillis = DEFAULT_DURATION_MILLIS
        var output: Path? = null

        private fun validate() {
            require(concurrencyLevel > 0) { "--concurrency-level must be positive" }
            require(isPowerOfTwo(capacity)) { "--capacity must be a positive power of two" }
            require(isPowerOfTwo(residents)) { "--residents must be a positive power of two" }
            require(residents <= capacity) { "--residents must be <= --capacity" }
            require(pattern == "hot" || pattern == "cycle") { "--pattern must be hot or cycle" }
            require(workers > 0) { "--workers must be positive" }
            require(warmups >= 0 && runs > 0) { "--warmups must be >= 0 and --runs must be positive" }
            require(durationMillis > 0) { "--duration-ms must be positive" }
        }

        companion object {
            fun parse(engine: Engine, args: Array<String>): Options {
                val options = Options(engine)
                var index = 0
                while (index < args.size) {
                    val argument = args[index++]
                    if (argument == "--help") {
                        println(usage(engine))
                        exitProcess(0)
                    }
                    require(argument.startsWith("--") && index < args.size) { "expected flag and value: $argument" }
                    val value = args[index++]
                    when (argument) {
                        "--concurrency-level" -> {
                            require(engine == Engine.GUAVA) { "unknown flag: $argument" }
                            options.concurrencyLevel = parseInt(argument, value)
                        }

                        "--capacity" -> options.capacity = parseInt(argument, value)

                        "--residents" -> options.residents = parseInt(argument, value)

                        "--pattern" -> options.pattern = value

                        "--workers" -> options.workers = parseInt(argument, value)

                        "--statistics" -> {
                            require(value == "on" || value == "off") { "$argument must be on or off" }
                            options.statistics = value == "on"
                        }

                        "--warmups" -> options.warmups = parseInt(argument, value)

                        "--runs" -> options.runs = parseInt(argument, value)

                        "--duration-ms" -> options.durationMillis = parseLong(argument, value)

                        "--output" -> options.output = Path(value)

                        else -> throw IllegalArgumentException("unknown flag: $argument")
                    }
                }
                options.validate()
                return options
            }

            fun usage(engine: Engine): String =
                "usage: " +
                    (if (engine == Engine.GUAVA) "--concurrency-level N " else "") +
                    "--capacity N --residents N --pattern hot|cycle --workers N " +
                    "--statistics on|off --warmups N --runs N --duration-ms N --output PATH"

            private fun parseInt(flag: String, value: String): Int =
                try {
                    value.toInt()
                } catch (exception: NumberFormatException) {
                    throw IllegalArgumentException("$flag must be an integer: $value", exception)
                }

            private fun parseLong(flag: String, value: String): Long =
                try {
                    value.toLong()
                } catch (exception: NumberFormatException) {
                    throw IllegalArgumentException("$flag must be an integer: $value", exception)
                }

            private fun isPowerOfTwo(value: Int): Boolean = value > 0 && (value and (value - 1)) == 0
        }
    }

    private object ReportWriter {
        fun write(options: Options, samples: List<Sample>, failure: Throwable?): String {
            val json = StringBuilder(16_384).append('{')
            field(json, "schemaVersion", SCHEMA_VERSION)
            field(json, "capacity", options.capacity)
            if (options.engine == Engine.GUAVA) field(json, "concurrencyLevel", options.concurrencyLevel)
            field(json, "residents", options.residents)
            field(json, "pattern", options.pattern)
            field(json, "workers", options.workers)
            field(json, "statistics", if (options.statistics) "on" else "off")
            field(json, "warmups", options.warmups)
            field(json, "runs", options.runs)
            field(json, "durationMillis", options.durationMillis)
            field(json, "runtime", System.getProperty("java.runtime.version", "unknown"))
            field(json, "os", System.getProperty("os.name", "unknown"))
            field(json, "architecture", System.getProperty("os.arch", "unknown"))
            field(json, "processors", Runtime.getRuntime().availableProcessors())
            val caffeine = options.engine == Engine.CAFFEINE
            val prefix = if (caffeine) "caffeine" else "guava"
            field(json, "${prefix}Version", if (caffeine) CAFFEINE_VERSION else "33.7.1-jre")
            val artifact = cacheArtifact(if (caffeine) Caffeine::class.java else CacheBuilder::class.java)
            field(json, "${prefix}JarPath", artifact.path)
            field(json, "${prefix}JarSha256", artifact.sha256)
            if (!caffeine) {
                field(json, "weightedSizeSource", "unit-weight resident count; not a Guava policy inspection API")
            }
            field(json, "allocationScope", "live dedicated worker threads")
            field(
                json,
                "allocationCoverage",
                if (caffeine) {
                    "dedicated reader threads only; excludes main thread, cache setup, Caffeine maintenance/executor threads, and terminated threads"
                } else {
                    "dedicated reader threads only; excludes main thread, cache setup, Guava maintenance and other threads, and terminated threads"
                },
            )
            field(json, "allocationSupported", allocationSupported())
            separator(json)
            string(json, "samples")
            json.append(":[")
            samples.forEachIndexed { index, sample ->
                if (index > 0) json.append(',')
                json.append(sample(sample))
            }
            json.append(']')
            field(json, "failure", failure?.toString())
            return json.append('}').toString()
        }

        private fun sample(sample: Sample): String {
            val json = StringBuilder(1_024).append('{')
            field(json, "sampleIndex", sample.sampleIndex)
            field(json, "warmup", sample.warmup)
            field(json, "wallSeconds", sample.wallSeconds)
            field(json, "cleanupSeconds", sample.cleanupSeconds)
            field(json, "operations", sample.operations)
            field(json, "checksum", sample.checksum)
            field(json, "misses", sample.misses)
            separator(json)
            string(json, "workersOperations")
            json.append(":[").append(sample.workersOperations.joinToString(",")).append(']')
            field(json, "hitsDelta", sample.hitsDelta)
            field(json, "missesDelta", sample.missesDelta)
            field(json, "residentCount", sample.residentCount)
            field(json, "weightedSize", sample.weightedSize)
            field(json, "workerAllocatedBytes", sample.workerAllocatedBytes)
            separator(json)
            string(json, "gcCollections")
            json.append(":{")
            for ((name, value) in sample.gcCollections.values) field(json, name, value)
            json.append('}')
            field(json, "boundsPassed", sample.boundsPassed)
            field(json, "validationFailure", sample.validationFailure)
            return json.append('}').toString()
        }

        private fun cacheArtifact(type: Class<*>): Artifact =
            try {
                val path = Path.of(type.protectionDomain.codeSource.location.toURI()).toAbsolutePath()
                Artifact(path.toString(), if (path.isRegularFile()) sha256(path) else null)
            } catch (_: URISyntaxException) {
                Artifact(null, null)
            } catch (_: RuntimeException) {
                Artifact(null, null)
            }

        private fun sha256(path: Path): String? =
            try {
                val digest = MessageDigest.getInstance("SHA-256")
                path.inputStream().use { input ->
                    val buffer = ByteArray(16_384)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        if (read > 0) digest.update(buffer, 0, read)
                    }
                }
                digest.digest().toHexString()
            } catch (_: IOException) {
                null
            } catch (_: NoSuchAlgorithmException) {
                null
            }

        private fun allocationSupported(): Boolean {
            val bean = ManagementFactory.getThreadMXBean()
            return bean is ThreadMXBean && bean.isThreadAllocatedMemorySupported
        }

        private fun field(json: StringBuilder, name: String, value: Any?) {
            separator(json)
            string(json, name)
            json.append(':')
            if (value is String) string(json, value) else json.append(value)
        }

        private fun separator(json: StringBuilder) {
            if (json.length > 1 && json.last() != '{' && json.last() != '[') json.append(',')
        }

        private fun string(json: StringBuilder, value: String) {
            json.append('"')
            for (character in value) {
                when (character) {
                    '"' -> json.append("\\\"")

                    '\\' -> json.append("\\\\")

                    '\b' -> json.append("\\b")

                    '\u000c' -> json.append("\\f")

                    '\n' -> json.append("\\n")

                    '\r' -> json.append("\\r")

                    '\t' -> json.append("\\t")

                    else ->
                        if (character < ' ') {
                            json.append("\\u${character.code.toString(16).padStart(4, '0')}")
                        } else {
                            json.append(character)
                        }
                }
            }
            json.append('"')
        }
    }

    private data class Artifact(val path: String?, val sha256: String?)

    private fun expectedChecksum(worker: Int, operations: Long, mask: Int, residents: Int): Long {
        if (mask == 0) return operations
        val fullCycles = operations / residents
        val remainder = (operations % residents).toInt()
        var checksum = fullCycles * residents * (residents + 1L) / 2L
        val start = (worker * START_STRIDE) and mask
        for (index in 0 until remainder) checksum += ((start + index) and mask) + 1L
        return checksum
    }

    private fun elapsedSeconds(started: Long, finished: Long): Double = (finished - started) / 1_000_000_000.0
}
