# H1: defer write signals only without expiry

**The final candidate passes the requested TTL upper limits and preserves clear NONE one-writer gains.**

This report covers the final no-expiry-only implementation and all earlier trials. Raw receipts and source snapshots are retained locally under `artifacts/perf-write-threshold/adjustment5/`; relative receipt paths below refer to that directory. The separate A/A bundle is under `artifacts/perf-write-threshold/adjustment4/aa/`.

Base: `1619eeb7ab31abb6df7cb644a6b1c4f06b4139d4`. M4 Max, .NET 10.0.8, AC power/mode 0. This cohort has 216 new processes / 1,512 measured samples: six processes per source/backend for hits and NONE one-worker cases, twelve for both TTL Put cases. All ranges are full observed process minima/maxima, not confidence intervals. Negative changes mean lower cost.

## Implementation and unchanged tests

Only CacheEngine.cs changes in production from adjustment4. PutCore and non-refresh CompleteSuccess use the existing readonly eligibility bool to select acquisition. Eligible no-expiry engines retain TryEnter, the failed-attempt idle snapshot and blocking fallback. Ineligible engines directly call Monitor.Enter with the existing exception-safe finally: the expansion of the original lock. Both publication bodies match base and adjustment4 byte for byte. No helper or duplicate publication body is introduced.

The existing threshold is one when deferral is disabled. Its threshold > 1 condition already skips every capacity-pressure calculation and store; the policy file stays byte-identical. Deferral remains limited to eligible default-scheduler count-bounded configurations without TTL, TTI or variable expiry; expiry configurations request immediately and create no backstop. Weighted/custom policies, listeners and injected schedulers remain immediate. A readonly branch, H1 fields and the existing write-boundary condition remain, so base monitor/publication operations do not imply identical JIT code, layout or cost.

All test source is unchanged from adjustment4. Full PASS on each net8/net10: core 871 (including all 35 backstop cases), DI 10, stress 10, plus both consumer smokes, solution build, CSharpier and actual prek. The existing tests cover TTL/TTI/variable immediate requests with no write timer, no-expiry Put/load contention, and timer/owner lifecycle. See functional-receipt.json, final-validation/, implementation-audit.md/json and base-entry-only.patch.

## Hit gate

| Case | Base ns | Candidate ns | Raw | Paired MC-adjusted | Gate |
|---|---:|---:|---:|---:|---|
| hit-none-w1 | 12.954 [12.550, 13.699] | 12.812 [12.494, 13.209] | -1.10% | -1.83% | PASS |
| hit-write-w1 | 20.314 [19.929, 20.756] | 20.256 [19.759, 21.626] | -0.28% | -1.32% | PASS |
| hit-access-w1 | 21.413 [20.957, 21.884] | 21.588 [21.301, 21.708] | +0.82% | +0.81% | PASS |

The unchanged hit gate stops at >3% raw slowdown with a strictly non-overlapping worse process range. It uses 1,024 preboxed cycling keys, statistics off, precise expiry, three warmups and seven samples of >=250 ms. In case names, write expiry means TTL and access means TTI.

## New Put and synchronous load measurements

| Case | Pairs | Base ns | Candidate ns | Raw | Paired MC-adjusted | Wins |
|---|---:|---:|---:|---:|---:|---:|
| write-none-w1-c16384 | 6 | 286.630 [281.839, 290.462] | 257.274 [249.443, 266.798] | -10.24% | -10.17% | 6/6 |
| write-write-w1-c16384 | 12 | 390.961 [369.555, 399.532] | 393.572 [373.770, 400.406] | +0.67% | -0.23% | 7/12 |
| write-write-w10-c16384 | 12 | 376.234 [370.669, 418.915] | 370.044 [359.276, 405.924] | -1.65% | -0.26% | 7/12 |
| load-none-w1 | 6 | 704.568 [649.230, 744.740] | 546.812 [543.202, 556.395] | -22.39% | -21.98% | 6/6 |

Each fresh-cache batch has 10,240 preboxed operations at capacity 16,384, statistics off and precise expiry, with one or ten permanent workers. At least three warmups and one second of warmup precede seven samples accumulating >=150 ms foreground time. Constructor/prefill/validation/disposal are outside timing/allocation windows. Foreground includes concurrent maintenance; settled adds explicit quiescence. Whole-process allocation includes background work. Ten-worker ns/op is inverse aggregate throughput, not individual latency. MC is unbounded here and has different loading/scheduling semantics.

| Case | Base/candidate settled ns | Base/candidate B | Base/candidate settled B | Requests per 10240 | Passes per 10240 | Base/candidate MC ns |
|---|---:|---:|---:|---:|---:|---:|
| write-none-w1-c16384 | 297.029 / 267.803 | 530.078 / 529.777 | 530.091 / 530.109 | 440.22 / 118.06 | 441.21 / 119.05 | 121.461 / 122.727 |
| write-write-w1-c16384 | 401.901 / 404.097 | 621.280 / 621.280 | 621.295 / 621.295 | 123.55 / 127.92 | 124.55 / 128.91 | 124.957 / 124.645 |
| write-write-w10-c16384 | 389.945 / 382.790 | 594.395 / 595.437 | 594.410 / 595.452 | 23.73 / 18.69 | 24.72 / 19.69 | 131.498 / 130.596 |
| load-none-w1 | 716.108 / 558.878 | 858.090 / 857.724 | 858.103 / 858.120 | 1850.04 / 140.61 | 1851.04 / 141.61 | 140.660 / 141.072 |

Counters are read outside timing. The adjustment is the median of (candidate/MC_candidate)/(base/MC_base)-1 across matching rounds, not a ratio of aggregate medians. Source and backend order reverse on even rounds. All process and paired values remain in summary.json. This control does not identify a causal scheduler cost.

## Explicitly reused NONE ten-worker evidence

By user instruction these cases were not rerun. They use adjustment4 binaries and twelve pairs each. The defer=true acquisition sequence is retained in adjustment5, with a new readonly branch choosing it. Reuse is an evidence boundary: these are not new measurements of adjustment5 machine code.

| Adjustment4 case | Base ns | Candidate ns | Raw | Paired MC-adjusted | Wins |
|---|---:|---:|---:|---:|---:|
| write-none-w10-c16384 | 286.220 [271.084, 301.138] | 278.287 [271.823, 302.025] | -2.77% | -1.81% | 8/12 |
| load-none-w10 | 802.974 [767.545, 983.929] | 803.500 [778.036, 851.146] | +0.07% | +0.80% | 5/12 |

The complete reused rows, including allocations, settled costs, requests, passes and every pair, are preserved under reusedRows in summary.json, with the source summary SHA-256 in shipping-evidence.json.

## Prior same-base A/A check

After adjustment4, the user requested one A/A cohort using the exact same original base DLL paths/hashes for both A1/A2. Six processes per arm/backend, 48 processes / 336 samples total. No samples were removed or repeated.

| A/A case | Paired MC-adjusted median | Full paired range | A2 wins |
|---|---:|---:|---:|
| write-write-w1-c16384 | +1.7114% | [-4.43%, +6.04%] | 2/6 |
| write-none-w10-c16384 | +0.0043% | [-2.61%, +81.03%] | 3/6 |

Neither A/A median reached 2% in magnitude. The +81.03% NONE ten-worker pair remains included: A1 LC/MC medians were 499.080/301.875 ns, A2 472.490/157.867 ns. Unequal controls account for the large adjusted ratio, while both LC processes also slowed; power/functional checks passed and the environmental cause is unknown. This one cohort does not establish a tight noise floor, prove candidate equivalence or justify subtracting an offset. It has six pairs, unlike the new twelve-pair TTL measurements. Full evidence remains in ../adjustment4/aa/.

## All earlier candidate attempts

Separate cohorts against the same base; they are not paired comparisons between candidates. Original H1 and adjustment1 used six ten-worker pairs; adjustments2-4 used twelve. All values below are paired MC-adjusted changes. prior-attempts.json binds every row to its complete archived source-summary hash.

| Attempt | NONE Put w1 | TTL Put w1 | Load w1 | NONE Put w10 | TTL Put w10 | Load w10 |
|---|---:|---:|---:|---:|---:|---:|
| original | -11.97% | -3.64% | -16.85% | +3.90% | +5.13% | +2.28% |
| adjustment | -8.09% | -3.60% | -11.75% | +5.65% | -4.80% | +10.27% |
| adjustment2 | -11.85% | -8.44% | -19.11% | +6.46% | +0.99% | +11.33% |
| adjustment3 | -10.34% | -0.67% | -14.15% | -1.37% | +3.63% | -1.80% |
| Adjustment4: no-expiry-only eligibility | -12.61% | +4.58% | -16.75% | -1.81% | -0.70% | +0.80% |

- Original H1 deferred eligible fixed-expiry/no-expiry tails with a weak one-shot backstop. Ten-worker NONE Put was +4.91% raw/+3.90% adjusted; TTL Put +5.55%/+5.13%; load +0.37%/+2.28%. The measured regressions were not dismissed as noise, and it was not accepted for merging.
- Adjustment1 moved pressure calculation behind the idle check but acquired the gate again. Ten-worker load was +12.22% raw/+10.27% adjusted with separated worse ranges; stopped.
- Adjustment2 retained the original enqueue critical section, used unit-weight WeightedSize and stored pressure only on transitions. NONE Put/load ten-worker medians still failed the tighter rule.
- The original-H1 diagnostic found contention counters +18.83% and dequeued events/pass 1.808x at c16384, both separated; c1024 contention ranges overlapped. These counters supported a contention trial but did not measure gate hold time, individual wakeups or prove a convoy.
- Adjustment3 added TryEnter for Put/shared non-refresh load publication; failure with an idle owner forced the existing immediate boundary. NONE ten-worker cases passed, but TTL ten-worker was +3.63% adjusted with 10/12 slower pairs; stopped.
- Adjustment4 restricted eligibility at construction to no-expiry engines. Its TTL one-worker result remained +4.58% adjusted, 2/6 wins; all ten-worker cases passed. The subsequent A/A results above informed the final scoped trial.
- Adjustment5 restores original blocking monitor entry for ineligible engines and keeps the tested deferred sequence. This report combines its requested new measurements with explicitly reused adjustment4 NONE ten-worker evidence.

Earlier anomalies remain archived: the original NONE-hit repeat did not reproduce its initial drift; the coarse-NONE setup anomaly reduced with a direct-builder control and yielded no isolated library fix. No such retuning or repeat is performed in the current cohort.

## Verdict and provenance

TTL limits passed: True. NONE one-worker gains remain clear: True. Each TTL paired median must be <=+2%; raw range overlap does not waive the limit. The final candidate meets that agreed rule; this does not prove zero cost on every workload. No selective repeat or further tuning was used in the final cohort.

Original baseline assemblies and all three probe sources are reused unchanged. Candidate source/assemblies, runner and method were frozen after validation. Every result/receipt/hash and pre/post power state is checked. See builds/manifest.json, results/plan.json, source-measured/, functional-receipt.json, validation.json and receipt-index.json for the complete audit trail. All prior raw cohorts remain archived.

### Measured source and assembly fingerprints

| Item | SHA-256 |
|---|---|
| base timing source | `9f130c714b1784472bafdf33a73b4edf578eeb7cc677def5502c7aba6d0d005b` |
| base LoadingCache.dll | `9c7143f4ffdb017281c45f70444f20bfa1ee9ce8818cdb9315ece09241b3b962` |
| h1 timing source | `842c36d3e2f5e0eb60e95481cb7c3b4811712abcdd7af7f964bf46cd2b80026a` |
| h1 LoadingCache.dll | `893bfadae156f21bb1c7ac9c9b65288e5db18d8dc3716235d7b43229e71774c5` |
| WriteProbe harness | `30c72ed39d047c6a99d27d5375e986c65597f826a7735c3df12c36d0355ffa61` |

The final production/test/docs source hashes also match the archived functional receipt. Formatting hooks exclude dated benchmark evidence to preserve these bytes.
