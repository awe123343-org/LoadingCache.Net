package baseline;

import com.github.benmanes.caffeine.cache.Cache;
import com.github.benmanes.caffeine.cache.Caffeine;
import com.github.benmanes.caffeine.cache.stats.CacheStats;
import java.util.concurrent.BrokenBarrierException;
import java.util.concurrent.CyclicBarrier;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import org.openjdk.jmh.annotations.Benchmark;
import org.openjdk.jmh.annotations.BenchmarkMode;
import org.openjdk.jmh.annotations.Fork;
import org.openjdk.jmh.annotations.Level;
import org.openjdk.jmh.annotations.Measurement;
import org.openjdk.jmh.annotations.Mode;
import org.openjdk.jmh.annotations.OutputTimeUnit;
import org.openjdk.jmh.annotations.Param;
import org.openjdk.jmh.annotations.Scope;
import org.openjdk.jmh.annotations.Setup;
import org.openjdk.jmh.annotations.State;
import org.openjdk.jmh.annotations.TearDown;
import org.openjdk.jmh.annotations.Threads;
import org.openjdk.jmh.annotations.Warmup;

/**
 * Compares Caffeine's concurrent put path when the caller also drains maintenance.
 *
 * <p>The worker threads are created once at trial setup. Each timed invocation coordinates the
 * existing workers with barriers, writes one pre-generated trace window, and then calls
 * {@link Cache#cleanUp()} until the bounded state is stable. This intentionally measures the
 * coordination and maintenance path rather than an enqueue-only put loop.
 */
@State(Scope.Benchmark)
@BenchmarkMode(Mode.AverageTime)
@OutputTimeUnit(TimeUnit.NANOSECONDS)
@Warmup(iterations = 3, time = 1)
@Measurement(iterations = 3, time = 1)
@Fork(1)
@Threads(1)
public class CaffeineWriteDrainBenchmark {
  private static final int TRACE_WINDOWS = 4;
  private static final int QUIESCENCE_PASS_LIMIT = 128;

  /** Number of worker threads used by one write batch. */
  @Param({"1", "4", "10"})
  public int workers;

  /** Number of writes performed by each worker in one batch. */
  @Param({"512", "4096"})
  public int writesPerWorker;

  /** Maximum number of resident entries. */
  @Param({"1024", "16384"})
  public int capacity;

  /** Whether Caffeine records statistics during the timed write path. */
  @Param({"off", "on"})
  public String statistics;

  /** Whether a repeated key retains its value or alternates between two preallocated values. */
  @Param({"same", "changed"})
  public String valueMode;

  private Cache<Integer, Integer> cache;
  private Integer[] trace;
  private Integer[] changedValues;
  private PersistentWorkers workerPool;
  private int nextWindow;

  @Setup(Level.Trial)
  public void setupTrial() {
    trace = createTrace(workers * writesPerWorker * TRACE_WINDOWS);
    changedValues = new Integer[trace.length];
    for (int index = 0; index < trace.length; index++) {
      changedValues[index] = index + trace.length;
    }
    workerPool = new PersistentWorkers();
  }

  @Setup(Level.Iteration)
  public void setupIteration() {
    if (cache != null) {
      cache.invalidateAll();
      cache.cleanUp();
    }
    Caffeine<Object, Object> builder = Caffeine.newBuilder().maximumSize(capacity);
    if (statistics.equals("on")) {
      builder.recordStats();
    }
    cache = builder.build();
    if (cache.estimatedSize() != 0) {
      throw new IllegalStateException("cache was not empty at iteration setup");
    }
    nextWindow = 0;
  }

  /**
   * Writes one concurrent trace window and includes all cleanup needed to reach a bounded,
   * stable state in the timed operation.
   */
  @Benchmark
  public long putBatchAndQuiesce() {
    int window = nextWindow % TRACE_WINDOWS;
    Integer[] values = valueMode.equals("changed") && nextWindow >= TRACE_WINDOWS
        ? changedValues : trace;
    nextWindow = (nextWindow + 1) % (TRACE_WINDOWS * 2);
    int start = window * workers * writesPerWorker;
    workerPool.runBatch(start, values);
    quiesce();
    return cache.estimatedSize();
  }

  @TearDown(Level.Iteration)
  public void tearDownIteration() {
    cache.cleanUp();
    if (cache.estimatedSize() > capacity) {
      throw new IllegalStateException("cache exceeded maximum size after cleanup");
    }
  }

  @TearDown(Level.Trial)
  public void tearDownTrial() {
    if (workerPool != null) {
      workerPool.close();
    }
    if (cache != null) {
      cache.invalidateAll();
      cache.cleanUp();
      if (cache.estimatedSize() != 0) {
        throw new IllegalStateException("cache was not empty after trial cleanup");
      }
    }
  }

  private void quiesce() {
    long previousSize = -1;
    long previousActualSize = -1;
    long previousEvictions = -1;
    for (int pass = 0; pass < QUIESCENCE_PASS_LIMIT; pass++) {
      cache.cleanUp();
      long size = cache.estimatedSize();
      long actualSize = cache.asMap().size();
      CacheStats stats = cache.stats();
      long evictions = stats.evictionCount();
      if (
          size <= capacity
              && actualSize <= capacity
              && size == previousSize
              && actualSize == previousActualSize
              && evictions == previousEvictions) {
        return;
      }
      previousSize = size;
      previousActualSize = actualSize;
      previousEvictions = evictions;
    }
    throw new IllegalStateException(
        "cache did not become quiescent after " + QUIESCENCE_PASS_LIMIT + " cleanup passes");
  }

  private Integer[] createTrace(int length) {
    Integer[] values = new Integer[length];
    for (int index = 0; index < values.length; index++) {
      values[index] = index;
    }
    return values;
  }

  private final class PersistentWorkers implements AutoCloseable {
    private final CyclicBarrier startBarrier = new CyclicBarrier(workers + 1);
    private final CyclicBarrier doneBarrier = new CyclicBarrier(workers + 1);
    private final Thread[] threads = new Thread[workers];
    private volatile boolean stopping;
    private volatile Throwable failure;
    private volatile int start;
    private Integer[] values;

    PersistentWorkers() {
      for (int worker = 0; worker < workers; worker++) {
        int workerIndex = worker;
        threads[worker] = new Thread(() -> run(workerIndex), "caffeine-write-worker-" + worker);
        threads[worker].setDaemon(true);
        threads[worker].start();
      }
    }

    void runBatch(int batchStart, Integer[] batchValues) {
      start = batchStart;
      values = batchValues;
      await(startBarrier);
      await(doneBarrier);
      if (failure != null) {
        throw new IllegalStateException("persistent worker failed", failure);
      }
    }

    private void run(int worker) {
      while (true) {
        try {
          startBarrier.await();
        } catch (InterruptedException e) {
          Thread.currentThread().interrupt();
          failure = e;
          return;
        } catch (BrokenBarrierException e) {
          failure = e;
          return;
        }
        if (stopping) {
          return;
        }

        try {
          int workerStart = start + worker * writesPerWorker;
          for (int index = 0; index < writesPerWorker; index++) {
            Integer key = trace[workerStart + index];
            cache.put(key, values[workerStart + index]);
          }
        } catch (Throwable t) {
          failure = t;
        }
        try {
          doneBarrier.await();
        } catch (InterruptedException e) {
          Thread.currentThread().interrupt();
          failure = e;
          return;
        } catch (BrokenBarrierException e) {
          failure = e;
          return;
        }
      }
    }

    @Override
    public void close() {
      stopping = true;
      try {
        startBarrier.await(30, TimeUnit.SECONDS);
      } catch (InterruptedException e) {
        Thread.currentThread().interrupt();
      } catch (BrokenBarrierException | TimeoutException e) {
        failure = e;
      }
      for (Thread thread : threads) {
        try {
          thread.join(TimeUnit.SECONDS.toMillis(5));
        } catch (InterruptedException e) {
          Thread.currentThread().interrupt();
          failure = e;
          return;
        }
        if (thread.isAlive()) {
          throw new IllegalStateException("persistent worker did not stop", failure);
        }
      }
    }

    private void await(CyclicBarrier barrier) {
      try {
        barrier.await(30, TimeUnit.SECONDS);
      } catch (InterruptedException e) {
        Thread.currentThread().interrupt();
        throw new IllegalStateException("benchmark coordination interrupted", e);
      } catch (BrokenBarrierException | TimeoutException e) {
        throw new IllegalStateException("benchmark coordination failed", e);
      }
    }
  }
}
