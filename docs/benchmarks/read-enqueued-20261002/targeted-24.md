> Archived measurement verdict before owner approval. The owner reviewed the failed within-mode requirements on 2 October 2026 and accepted shipping; see [the approval and rebase note](README.md#owner-decision-and-rebase). The measured data and original STOP verdict below are retained.

# Targeted ten-worker statistics-ON result

**Verdict: STOP under the predeclared acceptance rule. No commit pending Claude's reply.**

Main `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4` versus the unchanged candidate, same frozen harness and libraries. 24 LoadingCache processes per arm, each with a fresh MemoryCache control: 96 total / 672 measured samples, all retained. No original six-pair samples are included. .NET 10.0.8, M4 Max, AC mode 0, 10 workers, no expiry, statistics ON, 1,024 cycling preboxed keys, 3 warmups + 7 x 250 ms samples.

Process unit: median of seven wall ns/hit samples. Slow > 6 ns; fast <= 6 ns, fixed before measurement. Within-mode values below are medians of process medians.

| Arm | Slow / 24 | Fast median ns | Slow median ns |
| --- | ---: | ---: | ---: |
| base | 5/24 | 3.397867 | 8.962744 |
| derived | 6/24 | 3.542114 | 9.268523 |

Fisher exact table (candidate/base rows, slow/fast columns): `[[6, 18], [5, 19]]`. One-sided greater p = **0.500000000**; two-sided p = **1.000000000**. Computed by `uv run --no-project python3 targeted-24/report.py`, using exact integer combinations/Fractions; the published SciPy `[[6,2],[1,4]]` example is checked independently. [Fisher definition](https://docs.scipy.org/doc/scipy/reference/generated/scipy.stats.fisher_exact.html).

| Gate | Pass |
| --- | --- |
| pGreaterThanPointOne | True |
| slowCountWithinPlusThree | True |
| fastModeMedianNoWorse | False |
| slowModeMedianNoWorse | False |

Ten-worker statistics ON remains bimodal, as in the pre-existing #38 follow-up. This acceptance test does not establish equal mode probabilities or claim a ten-worker statistics-ON speedup. The original adverse pooled result remains in the earlier report.

## All paired processes

| Round | Base LC ns | Mode | Candidate LC ns | Mode | Base MC ns | Candidate MC ns |
| ---: | ---: | --- | ---: | --- | ---: | ---: |
| 1 | 9.474956 | slow | 8.477150 | slow | 36.264025 | 37.596274 |
| 2 | 3.247260 | fast | 3.615448 | fast | 36.802932 | 35.789349 |
| 3 | 3.397867 | fast | 3.613366 | fast | 38.455003 | 36.708512 |
| 4 | 8.962744 | slow | 3.498532 | fast | 36.866317 | 37.745046 |
| 5 | 8.151479 | slow | 9.523653 | slow | 38.270478 | 36.884036 |
| 6 | 3.168542 | fast | 3.440544 | fast | 38.104893 | 36.662777 |
| 7 | 3.495060 | fast | 9.608313 | slow | 36.683892 | 38.533767 |
| 8 | 3.307306 | fast | 3.609206 | fast | 38.043192 | 37.647402 |
| 9 | 8.765992 | slow | 3.538431 | fast | 38.756724 | 37.502459 |
| 10 | 3.601357 | fast | 3.438446 | fast | 38.062733 | 37.517183 |
| 11 | 3.358165 | fast | 3.471215 | fast | 37.036111 | 34.391450 |
| 12 | 3.285865 | fast | 9.013394 | slow | 37.264615 | 38.445373 |
| 13 | 3.189192 | fast | 9.670355 | slow | 37.852446 | 36.944272 |
| 14 | 3.510712 | fast | 3.545798 | fast | 38.425239 | 38.559378 |
| 15 | 3.237595 | fast | 3.564478 | fast | 37.838206 | 38.367406 |
| 16 | 9.336666 | slow | 3.391111 | fast | 37.467608 | 37.621277 |
| 17 | 3.410197 | fast | 3.639029 | fast | 37.941910 | 36.790592 |
| 18 | 3.494736 | fast | 3.512501 | fast | 36.773052 | 37.334674 |
| 19 | 3.537902 | fast | 3.589054 | fast | 37.468651 | 39.164367 |
| 20 | 3.512852 | fast | 3.620221 | fast | 37.704230 | 38.119653 |
| 21 | 3.469983 | fast | 3.513834 | fast | 36.992326 | 37.178703 |
| 22 | 3.469655 | fast | 3.551225 | fast | 38.315752 | 36.792069 |
| 23 | 3.244366 | fast | 8.316122 | slow | 37.806248 | 36.945236 |
| 24 | 3.346904 | fast | 3.459744 | fast | 38.215332 | 37.651843 |

Every process passed hit/statistics, resident/weight, power, runtime, and assembly checks. Source/tests/docs matched the five-file review receipt before and after timing. No source/harness changes, discarded processes, replacements or selective extensions. Full samples and fingerprints: `samples.csv`, `summary.json`, `results/plan.json` and all result/receipt/log files.
