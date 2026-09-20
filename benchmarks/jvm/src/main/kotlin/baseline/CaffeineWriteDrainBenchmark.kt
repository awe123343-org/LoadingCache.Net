package baseline

import com.github.benmanes.caffeine.cache.Cache
import com.github.benmanes.caffeine.cache.Caffeine
import java.util.concurrent.BrokenBarrierException
import java.util.concurrent.CyclicBarrier
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import kotlin.concurrent.thread
import org.openjdk.jmh.annotations.Benchmark
import org.openjdk.jmh.annotations.BenchmarkMode
import org.openjdk.jmh.annotations.Fork
import org.openjdk.jmh.annotations.Level
import org.openjdk.jmh.annotations.Measurement
import org.openjdk.jmh.annotations.Mode
import org.openjdk.jmh.annotations.OutputTimeUnit
import org.openjdk.jmh.annotations.Param
import org.openjdk.jmh.annotations.Scope
import org.openjdk.jmh.annotations.Setup
import org.openjdk.jmh.annotations.State
import org.openjdk.jmh.annotations.TearDown
import org.openjdk.jmh.annotations.Threads
import org.openjdk.jmh.annotations.Warmup

/** Includes persistent-worker coordination and bounded maintenance in each timed write batch. */
@State(Scope.Benchmark)
@BenchmarkMode(Mode.AverageTime)
@OutputTimeUnit(TimeUnit.NANOSECONDS)
@Warmup(iterations = 3, time = 1)
@Measurement(iterations = 3, time = 1)
@Fork(1)
@Threads(1)
open class CaffeineWriteDrainBenchmark {
    @JvmField
    @Param("1", "4", "10")
    var workers: Int = 0

    @JvmField
    @Param("512", "4096")
    var writesPerWorker: Int = 0

    @JvmField
    @Param("1024", "16384")
    var capacity: Int = 0

    @JvmField
    @Param("off", "on")
    var statistics: String = ""

    @JvmField
    @Param("same", "changed")
    var valueMode: String = ""

    private lateinit var cache: Cache<Any, Any>

    // Keep boxed values as object references throughout the timed loop: Array<Int> would
    // unbox/rebox accesses in Kotlin and change the original Java benchmark's allocation cost.
    private lateinit var trace: Array<Any>
    private lateinit var changedValues: Array<Any>
    private lateinit var workerPool: PersistentWorkers
    private var nextWindow = 0

    @Setup(Level.Trial)
    fun setupTrial() {
        trace = Array(workers * writesPerWorker * TRACE_WINDOWS) { it }
        changedValues = Array(trace.size) { it + trace.size }
        workerPool = PersistentWorkers()
    }

    @Setup(Level.Iteration)
    fun setupIteration() {
        if (::cache.isInitialized) {
            cache.invalidateAll()
            cache.cleanUp()
        }
        val builder = Caffeine.newBuilder().maximumSize(capacity.toLong())
        if (statistics == "on") builder.recordStats()
        cache = builder.build()
        check(cache.estimatedSize() == 0L) { "cache was not empty at iteration setup" }
        nextWindow = 0
    }

    @Benchmark
    fun putBatchAndQuiesce(): Long {
        val window = nextWindow % TRACE_WINDOWS
        val values = if (valueMode == "changed" && nextWindow >= TRACE_WINDOWS) changedValues else trace
        nextWindow = (nextWindow + 1) % (TRACE_WINDOWS * 2)
        workerPool.runBatch(window * workers * writesPerWorker, values)
        quiesce()
        return cache.estimatedSize()
    }

    @TearDown(Level.Iteration)
    fun tearDownIteration() {
        cache.cleanUp()
        check(cache.estimatedSize() <= capacity) { "cache exceeded maximum size after cleanup" }
    }

    @TearDown(Level.Trial)
    fun tearDownTrial() {
        if (::workerPool.isInitialized) workerPool.close()
        if (::cache.isInitialized) {
            cache.invalidateAll()
            cache.cleanUp()
            check(cache.estimatedSize() == 0L) { "cache was not empty after trial cleanup" }
        }
    }

    private fun quiesce() {
        var previousSize = -1L
        var previousActualSize = -1
        var previousEvictions = -1L
        repeat(QUIESCENCE_PASS_LIMIT) {
            cache.cleanUp()
            val size = cache.estimatedSize()
            val actualSize = cache.asMap().size
            val evictions = cache.stats().evictionCount()
            if (
                size <= capacity &&
                actualSize <= capacity &&
                size == previousSize &&
                actualSize == previousActualSize &&
                evictions == previousEvictions
            ) {
                return
            }
            previousSize = size
            previousActualSize = actualSize
            previousEvictions = evictions
        }
        error("cache did not become quiescent after $QUIESCENCE_PASS_LIMIT cleanup passes")
    }

    private inner class PersistentWorkers : AutoCloseable {
        private val startBarrier = CyclicBarrier(workers + 1)
        private val doneBarrier = CyclicBarrier(workers + 1)

        @Volatile private var stopping = false

        @Volatile private var failure: Throwable? = null

        @Volatile private var start = 0
        private lateinit var values: Array<Any>
        private val threads =
            Array(workers) { worker ->
                thread(isDaemon = true, name = "caffeine-write-worker-$worker") { run(worker) }
            }

        fun runBatch(batchStart: Int, batchValues: Array<Any>) {
            start = batchStart
            values = batchValues
            await(startBarrier)
            await(doneBarrier)
            failure?.let { throw IllegalStateException("persistent worker failed", it) }
        }

        private fun run(worker: Int) {
            while (true) {
                try {
                    startBarrier.await()
                } catch (e: InterruptedException) {
                    Thread.currentThread().interrupt()
                    failure = e
                    return
                } catch (e: BrokenBarrierException) {
                    failure = e
                    return
                }
                if (stopping) return
                try {
                    val workerStart = start + worker * writesPerWorker
                    for (index in 0 until writesPerWorker) {
                        val key = trace[workerStart + index]
                        cache.put(key, values[workerStart + index])
                    }
                } catch (t: Throwable) {
                    failure = t
                }
                try {
                    doneBarrier.await()
                } catch (e: InterruptedException) {
                    Thread.currentThread().interrupt()
                    failure = e
                    return
                } catch (e: BrokenBarrierException) {
                    failure = e
                    return
                }
            }
        }

        override fun close() {
            stopping = true
            try {
                startBarrier.await(30, TimeUnit.SECONDS)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
            } catch (e: BrokenBarrierException) {
                failure = e
            } catch (e: TimeoutException) {
                failure = e
            }
            for (thread in threads) {
                try {
                    thread.join(TimeUnit.SECONDS.toMillis(5))
                } catch (e: InterruptedException) {
                    Thread.currentThread().interrupt()
                    failure = e
                    return
                }
                if (thread.isAlive) throw IllegalStateException("persistent worker did not stop", failure)
            }
        }

        private fun await(barrier: CyclicBarrier) {
            try {
                barrier.await(30, TimeUnit.SECONDS)
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
                throw IllegalStateException("benchmark coordination interrupted", e)
            } catch (e: BrokenBarrierException) {
                throw IllegalStateException("benchmark coordination failed", e)
            } catch (e: TimeoutException) {
                throw IllegalStateException("benchmark coordination failed", e)
            }
        }
    }

    private companion object {
        const val TRACE_WINDOWS = 4
        const val QUIESCENCE_PASS_LIMIT = 128
    }
}
