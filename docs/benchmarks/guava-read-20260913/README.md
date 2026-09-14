# Guava, Caffeine and LoadingCache resident reads

Historical measurement from 13 September 2026. Versions: Guava 33.7.1-jre, Caffeine 3.2.4, actual .NET 8/.NET 10 and Zulu 25.0.4.1+1-LTS. The .NET thin-offer snapshot retained the same 72 source and 36 binary hashes throughout. This measures resident lookup throughput, not loading, writes, hit rate or complete feature equivalence.

## Configuration and results

Twenty-six cases, four runtimes and two reverse-order rounds produced 208 processes and 1,664 samples, of which 1,040 were measured. Each process used three warm-up and five measured 500 ms intervals. Persistent reader threads, barriers, pre-generated keys and 1,024-operation chunks kept setup separate. Checksums, zero misses, statistics and capacity were validated.

Guava's default concurrency level of four partitions capacity into segments. A capacity-1,024 full-residency preflight retained only 1,010 entries, so it was rejected and preserved as a failed control. The 20 main cases use matched half/sparse residency: hot/cyclic reads, 1/4/10/20 readers and statistics OFF/ON, plus sparse and larger-capacity controls. Six full-capacity cases use Guava concurrencyLevel(1), explicitly a non-default configuration, not a one-thread limit.

In the 20 default-configuration cases, each .NET runtime was cheaper than Guava in 16 and dearer in four. The four losses were all single-reader half-capacity cases; .NET 8 was 5.3–51.4% dearer and .NET 10 6.3–47.7% dearer. All 14 multi-reader cases favoured LoadingCache. Caffeine was cheaper than LoadingCache in every case.

Selected capacity-1,024/residency-512/statistics-OFF results, ns/op:

| Access | Readers | .NET 8 | .NET 10 | Caffeine | Guava |
| --- | ---: | ---: | ---: | ---: | ---: |
| Hot | 1 | 39.84 | 40.41 | 16.16 | 33.74 |
| Hot | 10 | 3.82 | 4.88 | 1.53 | 233.37 |
| Cyclic | 1 | 37.20 | 37.54 | 17.21 | 35.33 |
| Cyclic | 10 | 3.38 | 3.30 | 2.11 | 89.73 |

The full-residency concurrency-level-one supplement was about 19–23% slower for LoadingCache with one reader and faster with ten. Segmentation and residency both change, so this is not an isolated segmentation-cost experiment. See [comparison.csv](raw/comparison.csv) for all rows.

## Limits and unresolved variation

ns/op is aggregate elapsed time divided by completed operations, not p99 request latency. Java allocation counts reader threads; .NET counts the process. GC/cleanup boundaries also differ, so allocation and GC values are not directly comparable winners.

The unchanged .NET 10 thin-offer full/hot/one-reader/OFF snapshot measured 29.48 ns/op in the earlier batch and 42.23 here. The cause remains unresolved. It is neither an established code regression nor an explained JIT/noise effect.

Pinned [Guava LocalCache source](https://github.com/google/guava/blob/c5b5a383a1f7f4a84c17de910f61181011c95908/guava/src/com/google/common/cache/LocalCache.java#L2539) records recency and periodically tries segment cleanup on hits. Same-key contention is consistent with this design, but no profiler quantified those individual costs.

## Evidence and reproduction

- [archive.json](archive.json) hashes [evidence.tar.gz](evidence.tar.gz), containing the original report, commands, raw samples, failed preflight, source manifests and referenced thin-offer snapshot. Original historical text is retained unchanged inside the archive.
- [parent-verification.json](parent-verification.json) records independent aggregate and identity checks: 208 processes, 1,664 samples, 72 source files, 36 binaries, 35 inputs, seven downloads and 14 Guava build artifacts.
- [downloads.json](downloads.json) identifies pinned Maven artifacts, source/licence URLs and hashes. Guava/failureaccess are Apache-2.0. error_prone_annotations 2.50.0 was a compile-only dependency from the pinned POM.
- The final `javac --release 25 -Xlint:all -Werror` passed without warnings. Four initial missing-annotation warnings remain archived, not suppressed.
- A separate 16-process/32-sample smoke qualified the harness only.

Use the maintained [Guava adapter](../../../tools/LoadingCache.HitProbe.Guava/README.md) for new measurements. Archived commands identify historical inputs and may require rebuilding dependencies and selecting available paths; they are not a portable one-command replay promise. Always use a new output directory. This experiment did not rerun unchanged runtime NUnit/AOT/package suites and does not clear unrelated historical correctness blockers. Current status is in [release readiness](../../release-readiness.md).
