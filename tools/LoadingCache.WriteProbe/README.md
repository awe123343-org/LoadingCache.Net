# Write-path feedback harness

`LoadingCache.WriteProbe` is a BCL-only, out-of-process-friendly write comparison harness. It
uses the public cache API, pre-generates all keys and values, creates dedicated worker threads
once per sample, synchronises repeated batches with a `Barrier`, and never uses `Thread.Sleep` to
infer maintenance ordering. Barrier phases and worker joins have bounded watchdogs.

The `repro` suite is the small regression loop for the original Q02 workload: one and four
workers, 512 and 4096 writes per worker, repeated until each sample has at least 131,072 writes,
with synchronous `CleanUp` after each batch. The default five measured runs plus one warmup are
longer than the old three-sample BDN ShortRun. `steady` adds capacity 1024/16384, one/four/ten
workers, statistics on/off, and distinct-insert/replacement traces. Its default 16,384 writes per
worker keeps setup and drain cost visible while making the timed write batch long enough to
compare. Replacement cases pre-fill the complete capacity outside the timed region.

The `caffeine` suite matches the Java JMH trace matrix: 1/4/10 workers, 512/4096 writes per
worker, capacity 1024/16384, and statistics off/on. It explicitly clears and quiesces once before
the timed sample, then reuses persistent worker threads. `--target-operations` applies here too:
the trace is rounded up to complete 8-window cycles (two four-window sweeps), with at least one
cycle. The `same` value mode writes key/value `i` on every sweep. The `changed` mode reuses each
key with value `key + sweepLength` on alternating sweeps. Both traces are pre-generated before
timing. The .NET harness retains one pre-generated 8-window cycle and indexes it modulo 8 for
each repeated cycle; its expected checksum still covers every logical write. The Java harness
recycles four key windows and two value arrays through the same modulo-8 sequence, so setup
allocation is now bounded in both harnesses. This suite is a cross-runtime workload comparison;
nanoseconds from JDK 25 and .NET are reported separately.

For a focused caffeine workload, add one or more filters. These filters require `--suite caffeine`
and must match one of the suite's configured dimensions; a valid filter combination with no matching
workload fails clearly instead of producing an empty report:

```sh
dotnet run --project tools/LoadingCache.WriteProbe -c Release -f net10.0 --no-restore -- \
  --suite caffeine --capacity 16384 --workers 1 --writes-per-worker 4096 \
  --statistics on --value-mode changed --runs 3 --warmups 1 --no-progress
```

The available filters are `--capacity`, `--workers`, `--writes-per-worker`, `--statistics on|off`,
and `--value-mode same|changed`. Filtering only selects workloads; it does not change the trace,
batching, cleanup, or operation-count normalisation.

Build and run the repeatable regression loop from a clean source snapshot:

```sh
dotnet run --project tools/LoadingCache.WriteProbe -c Release -f net10.0 --no-restore -- \
  --suite repro --label current --runs 5 --warmups 1 \
  --output artifacts/write-probe/current-repro.json
```

Use `--target-operations N` to shorten a smoke run; leave its default unchanged for comparison.
`--suite all` runs all three suites. Use a fresh output path for each source snapshot. The JSON
keeps runtime/source hashes, setup/write/drain timings, worker and timed throughput, whole-process
and worker-thread allocation counters, GC deltas, pre/post/max maintenance backlog, drain pass
count, final resident/weighted counts, checksums, public-bound status, and a process-wide
`Monitor.LockContentionCount` delta (which includes harness/runtime monitor contention). The
`WriteBufferBacklog` and `WriteBufferPressure` fields are read reflectively so the same harness
compiles against the clean pre-buffer baseline, where those newer public fields do not exist.

The timed result is `WriteSeconds + DrainSeconds`; `SetupSeconds` covers dedicated-thread startup
and readiness separately. Throughput includes each pre-generated `Put` and the quiescent drain.
The seed fixes data order, not OS scheduling. Whole-process allocation covers the complete timed
write/drain loop; worker allocation is limited to the dedicated write threads; neither is retained
memory. `Drain` samples statistics before the first policy inspection because reading an eviction
view may itself synchronously flush maintenance.

For an apples-to-apples before/after run, copy this project and the benchmark sources into two
isolated source snapshots, build each in Release for the same TFM, and run the exact command above
once per snapshot. The historical Q02 pre-buffer baseline was withdrawn because it
was contaminated; it must not be used as an acceptance baseline.
