# Entry-owned expiration nodes: 1 October 2026

Removing the timer-node dictionary reduced fixed-expiry insertion time in this cohort. At capacity 16,384, patch/base ratios are 0.688–0.774 across TTL/TTI and one/ten writers, with about 84 fewer allocated bytes per insert. At capacity 1,024 under eviction pressure, ratios are 0.903–0.947, but allocation rises by 6.6–7.8 B/insert. No-expiry inserts pay the additional Entry field: about 8 B/insert. Timed entries still allocate a separate node.

The read result is conditional: the original pre-A hot-TTI cohort has a 1.10 ratio with disjoint ranges. The mechanism is not isolated; layout change on the contended CAS path is suspected. A separate A versus A+D1 stack measured 1.019 for ten-reader cycle and 1.011 for hot TTI, with overlapping ranges. The single-reader stack measured 1.098 with overlapping ranges. These results do not trigger the chosen stop criterion but do not establish that every read configuration is unaffected.

## Change and preserved contracts

Both fixed and variable expiration store the current `IdentityTimerNode<Entry>` reference on the existing sealed Entry. The engine no longer creates or consults `_expirationNodes`. Entry remains sealed, its monitor remains the Entry itself, and Ready/Loading factories and hit methods are unchanged. Every Entry gains one reference field (8 bytes on this 64-bit runtime); timed entries still allocate a separate timer node. Full node embedding belongs to the later Entry layout work and is not claimed here.

The wheel algorithm, expiry clocks, exact freshness checks, 128-node advance budget, expiration scheduler and runtime-duration APIs are unchanged. All timer fields remain under the engine gate. Due processing validates both the exact node reference and the current map Entry. Retirement clears the field and permanently retires the node; a surviving refresh or finite-duration transition creates a fresh node when necessary. Stale due work cannot clear a newer reference.

Clear/disposal remove current entries before clearing the map. Teardown-only `RetireAll` unlinks any remaining bucket nodes and clears pending continuation work before wheel reset. Detached due nodes are caller-owned; the engine consumes those batches under the same gate, so teardown does not race with an outstanding batch. The implementation note in [ADR-0003](../../adr/0003-timer-wheel.md) changes storage and teardown, without changing the expiration contract, the ADR-0013 commit/capacity protocol, or the ADR-0014 lossy-read protocol.

## Method

- Both builds start from main [10d767e](https://github.com/awe123343-org/LoadingCache.Net/commit/10d767ebbbe1a79aa17db7afb36decc2d30e35cf), including #38 and excluding the separately reviewed TTI coalescing change in #39. Labels are `d1-base` and `d1-entry-owned`. These results do not reuse the older `baseline-a` measurements or multiply ratios from separate experiments. The supplementary read pair starts from [A/#39, e893911](https://github.com/awe123343-org/LoadingCache.Net/commit/e893911ec6aaa8bd0afd5bab5be2a8f57310cec0), which does not include #38; labels are `d1-a-base` and `d1-a-entry-owned`. Those measurement-only worktrees do not change this PR’s main-based branch or add a dependency on #39. Cohorts remain separate.
- Apple M4 Max, arm64, SDK 10.0.401. Execution explicitly pins .NET 10.0.8 or 8.0.20; MemoryCache package 10.0.12. The existing HitProbe/WriteProbe source and hot loops are unchanged. Each label has isolated build output and source/assembly SHA256 receipts.
- Each cell has six independent processes per library per variant. Variant and LC/MC order reverse each round; the three-backend capacity control uses all six backend permutations. Values are medians of process medians, with every process retained. Concurrent ns/op is elapsed time divided by aggregate operations, not individual request latency.
- Hit processes have three 250-ms warmups and seven 250-ms measured samples. Keys are preboxed objects, with 1,024 residents and capacity 1,024. Fixed expiry lasts one hour. Cycle readers use phase offsets; hot readers share one key. MC uses SizeLimit=1,024 and Size=1, absolute expiry for TTL and sliding expiry for TTI. Net8 runs only single-reader rows. Statistics match across libraries.
- Write processes have at least three warmup samples and at least one second of warmup wall time, then seven samples of at least 150 ms accumulated insertion time. Each batch inserts 10,240 distinct preboxed keys; values reuse those objects. Persistent writer threads divide the keys without overlap. Capacity 16,384 starts empty and must retain all writes; capacity 1,024 starts full. Statistics are off.
- The non-evicting write group uses bounded LC and unbounded MC. The single-writer/no-expiry group also measures MC with SizeLimit=16,384 and Size=1, checking that all 10,240 entries remain. The capacity-1,024 group uses SizeLimit=1,024 and Size=1 on MC. These capacity semantics are explicitly different.
- Process allocation covers the timed batch, including concurrent maintenance; it excludes preboxed keys, cache construction, prefill, subsequent explicit cleanup, validation and disposal. Worker-only B/insert is retained separately. Removing the dictionary can reduce allocation while the extra Entry field increases it; measured total B/insert must include both effects and maintenance variation.
- The runner holds the shared benchmark lock for every timing process, checks AC before/after, and records power mode and battery percentage. Adapter rating is 65 W, not measured draw. Builds/tests also use the lock. A power-state transition stops the cohort. The 98-process preflight is excluded from formal results.
- All 660 formal processes (336 original hit, 72 supplementary hit, 252 write) and 4,620 measured samples passed independent validation. Every power record is AC, 100% battery, power mode 0. Runtime/assembly hashes, alternating orders, medians, hit counts/checksums/statistics, capacity and non-evicting retention were checked. No process or first-cohort result was excluded.

## Interpretation and read limitations

All eight TTL/TTI write cells have lower patch medians and disjoint six-process ranges; their MC median ratios are 0.988–1.020. The allocation saving at capacity 16,384 includes avoiding dictionary growth during insertion. Capacity 1,024 is prefilled outside timing, so its dictionary already has storage to reuse; it does not offer the same timed growth saving. Its measured total allocation increases instead. Writer-only allocation includes any maintenance done on the writing threads and is not a measure of Entry creation alone; the split between writer and background allocation varies.

The no-expiry write ratios are 1.031 with one writer and 0.990 with ten, with overlapping ranges in both. Allocated bytes increase by 8.001 and 7.645 respectively. No-expiry single-reader ratios are 0.982 on net10 and 1.030 on net8. The latter process ranges are disjoint (14.801–15.480 versus 15.563–16.213 ns/op), so a uniform “no read cost” claim would be inaccurate.

Pre-A ten-reader hot TTI is 88.778 -> 97.849 ns/op: ratio 1.102, ranges 83.779–92.665 versus 96.155–101.659, with the patch slower in all six alternating pairs. Its mechanism is not isolated; layout change on the contended access-timestamp CAS path is suspected. It is not attributed to noise. Pre-A TTI cycle is 4.889 -> 5.132 (1.050), with overlapping ranges. The hot-key MC controls are unstable (TTL ratio 1.455, TTI 0.652); they do not justify correcting away the LC observation.

A read-only inspection of the exact frozen libraries under .NET 10.0.8 found identical resolved-token IL for seven selected hit methods: both TryGet methods, both TryReadReady overloads, RecordHit, TouchPublished and ReadStrongValueAtomic. Entry allocation size is 176 -> 184 B; AccessTimestamp offset is 112 -> 120, while PublishedWrite remains at 48. This confirms the field cost and excludes extra work in those IL bodies. It does not compare native JIT code or actual cache-line placement in the timed processes, and does not establish causality.

The A/A+D1 supplement deliberately changes the read baseline because A coalesces the contended CAS path and is planned to land before D. It does not replace the pre-A evidence. After stacking on A, ten-reader cycle measured 2.289 -> 2.332 ns/op (1.019), hot measured 2.594 -> 2.622 (1.011), and single-reader TTI measured 25.620 -> 28.126 (1.098). All three ranges overlap. The agreed stop condition—any ratio above 1.03 with non-overlapping process ranges—is not met. The single-reader difference remains inconclusive; range overlap does not prove equality. The delivery branch remains based on main and contains D1 only.

<!-- results-start -->

## Formal results

Ratios are patch/base; lower is better. Each timing is ns/op, with the range of the six process medians in brackets. MC medians are the matched absolute/sliding/no-expiry control; MC ratio records environmental drift without correcting LC results.

### Writes (.NET 10.0.8)

| Case                                        |         LC base ns [range] |        LC patch ns [range] | LC ratio | MC base ns | MC patch ns | MC ratio | Patch LC/MC |
| ------------------------------------------- | -------------------------: | -------------------------: | -------: | ---------: | ----------: | -------: | ----------: |
| none c16384 1 writer(s)                     | 281.351 [270.425, 304.940] | 289.961 [274.963, 319.566] |    1.031 |    126.700 |     125.392 |    0.990 |       2.312 |
| none c16384 1 writer(s); MC bounded control | 281.351 [270.425, 304.940] | 289.961 [274.963, 319.566] |    1.031 |    124.547 |     126.893 |    1.019 |       2.285 |
| TTL c16384 1 writer(s)                      | 521.465 [508.373, 547.050] | 403.777 [384.271, 413.830] |    0.774 |    127.507 |     127.486 |    1.000 |       3.167 |
| TTI c16384 1 writer(s)                      | 528.608 [517.186, 617.095] | 399.682 [377.990, 428.858] |    0.756 |    126.894 |     125.429 |    0.988 |       3.187 |
| none c16384 10 writer(s)                    | 297.750 [290.190, 339.900] | 294.825 [287.400, 305.284] |    0.990 |    133.378 |     133.097 |    0.998 |       2.215 |
| TTL c16384 10 writer(s)                     | 549.274 [533.048, 574.815] | 387.832 [371.645, 407.823] |    0.706 |    136.685 |     137.879 |    1.009 |       2.813 |
| TTI c16384 10 writer(s)                     | 545.048 [540.610, 589.534] | 374.978 [370.477, 385.344] |    0.688 |    135.757 |     136.394 |    1.005 |       2.749 |
| TTL c1024 1 writer(s)                       | 456.718 [446.222, 510.088] | 422.809 [396.071, 439.221] |    0.926 |     58.024 |      58.852 |    1.014 |       7.184 |
| TTI c1024 1 writer(s)                       | 447.988 [437.058, 478.817] | 404.582 [399.875, 416.450] |    0.903 |     57.381 |      58.518 |    1.020 |       6.914 |
| TTL c1024 10 writer(s)                      | 434.284 [422.474, 501.996] | 411.308 [400.100, 419.077] |    0.947 |     33.107 |      33.377 |    1.008 |      12.323 |
| TTI c1024 10 writer(s)                      | 435.109 [418.917, 453.975] | 407.312 [403.861, 418.583] |    0.936 |     32.927 |      32.768 |    0.995 |      12.430 |

Allocation is measured B/insert, not a sum of estimated object sizes. Process-wide allocation includes concurrent maintenance; worker allocation isolates bytes allocated on the writing threads. The bounded MC control is a separate measurement of the same LC cell.

| Case                                        | LC process B/insert base -> patch |   Delta | LC worker B/insert base -> patch |   Delta | MC process B/insert base -> patch |
| ------------------------------------------- | --------------------------------: | ------: | -------------------------------: | ------: | --------------------------------: |
| none c16384 1 writer(s)                     |                522.077 -> 530.078 |  +8.001 |               378.355 -> 385.357 |  +7.002 |                253.287 -> 253.284 |
| none c16384 1 writer(s); MC bounded control |                522.077 -> 530.078 |  +8.001 |               378.355 -> 385.357 |  +7.002 |                253.286 -> 253.286 |
| TTL c16384 1 writer(s)                      |                705.434 -> 621.280 | -84.154 |               585.695 -> 532.030 | -53.665 |                253.286 -> 253.284 |
| TTI c16384 1 writer(s)                      |                705.446 -> 621.280 | -84.167 |               586.866 -> 529.999 | -56.867 |                253.286 -> 253.284 |
| none c16384 10 writer(s)                    |                495.106 -> 502.751 |  +7.645 |               451.599 -> 458.825 |  +7.226 |                234.298 -> 234.180 |
| TTL c16384 10 writer(s)                     |                678.816 -> 595.161 | -83.654 |               653.189 -> 573.290 | -79.899 |                234.253 -> 234.178 |
| TTI c16384 10 writer(s)                     |                678.844 -> 595.197 | -83.647 |               653.940 -> 572.854 | -81.087 |                233.948 -> 234.084 |
| TTL c1024 1 writer(s)                       |                557.827 -> 564.873 |  +7.046 |               426.403 -> 444.085 | +17.682 |                165.957 -> 165.813 |
| TTI c1024 1 writer(s)                       |                557.232 -> 563.870 |  +6.639 |               427.295 -> 454.653 | +27.359 |                164.454 -> 164.245 |
| TTL c1024 10 writer(s)                      |                556.442 -> 564.162 |  +7.720 |               527.915 -> 537.601 |  +9.686 |                126.831 -> 126.991 |
| TTI c1024 10 writer(s)                      |                556.182 -> 563.972 |  +7.789 |               527.867 -> 536.272 |  +8.405 |                126.694 -> 126.571 |

### Pre-A hits (original batch retained)

| Case (runtime, expiry, readers/pattern, stats) |      LC base ns [range] |      LC patch ns [range] | LC ratio | MC base ns | MC patch ns | MC ratio | Patch LC/MC | LC B/op base -> patch |
| ---------------------------------------------- | ----------------------: | -----------------------: | -------: | ---------: | ----------: | -------: | ----------: | --------------------: |
| net10.0 TTL 1/cycle off                        | 20.436 [19.902, 21.059] |  20.432 [19.902, 20.659] |    1.000 |     17.756 |      17.899 |    1.008 |       1.142 |        0.000 -> 0.000 |
| net10.0 TTI 1/cycle off                        | 25.046 [23.890, 26.128] |  24.550 [23.606, 26.006] |    0.980 |     18.258 |      18.253 |    1.000 |       1.345 |        0.000 -> 0.000 |
| net10.0 none 1/cycle off                       | 13.031 [12.310, 13.659] |  12.801 [12.516, 13.757] |    0.982 |     17.985 |      17.957 |    0.998 |       0.713 |        0.000 -> 0.000 |
| net10.0 TTL 1/cycle on                         | 26.205 [25.594, 30.191] |  26.371 [25.492, 27.255] |    1.006 |     18.864 |      18.931 |    1.004 |       1.393 |        0.000 -> 0.000 |
| net10.0 TTI 1/cycle on                         | 28.828 [27.917, 30.160] |  28.306 [27.905, 28.546] |    0.982 |     18.977 |      18.977 |    1.000 |       1.492 |        0.000 -> 0.000 |
| net10.0 TTL 10/cycle off                       |    2.192 [2.022, 2.499] |     2.107 [1.941, 2.233] |    0.961 |     35.123 |      33.779 |    0.962 |       0.062 |        0.000 -> 0.000 |
| net10.0 TTI 10/cycle off                       |    4.889 [4.252, 5.161] |     5.132 [4.463, 5.308] |    1.050 |     35.327 |      34.813 |    0.985 |       0.147 |        0.000 -> 0.000 |
| net10.0 TTL 10/hot off                         |    2.507 [2.324, 2.910] |     2.579 [2.452, 2.956] |    1.029 |     22.600 |      32.874 |    1.455 |       0.078 |        0.000 -> 0.000 |
| net10.0 TTI 10/hot off                         | 88.778 [83.779, 92.665] | 97.849 [96.155, 101.659] |    1.102 |     37.956 |      24.754 |    0.652 |       3.953 |        0.000 -> 0.000 |
| net8.0 TTL 1/cycle off                         | 21.862 [21.531, 22.872] |  21.848 [21.480, 22.504] |    0.999 |     18.838 |      18.856 |    1.001 |       1.159 |        0.000 -> 0.000 |
| net8.0 TTI 1/cycle off                         | 25.369 [24.922, 26.105] |  26.392 [25.694, 26.639] |    1.040 |     18.833 |      18.989 |    1.008 |       1.390 |        0.000 -> 0.000 |
| net8.0 none 1/cycle off                        | 15.275 [14.801, 15.480] |  15.731 [15.563, 16.213] |    1.030 |     18.778 |      18.799 |    1.001 |       0.837 |        0.000 -> 0.000 |
| net8.0 TTL 1/cycle on                          | 28.166 [27.387, 29.205] |  28.004 [27.566, 29.021] |    0.994 |     20.306 |      20.236 |    0.997 |       1.384 |        0.000 -> 0.000 |
| net8.0 TTI 1/cycle on                          | 29.817 [29.454, 30.299] |  29.810 [29.238, 30.999] |    1.000 |     20.445 |      20.398 |    0.998 |       1.461 |        0.000 -> 0.000 |

### A versus A+D1 (separate measurement-only stack)

Both variants start at A/#39, e893911. These three rows do not replace or pool the original pre-A cohort. The stop criterion is a patch/base median ratio above 1.03 with non-overlapping process ranges, in any row.

| Case                     |            A ns [range] |         A+D1 ns [range] | LC ratio | MC A ns | MC A+D1 ns | MC ratio | A+D1 LC/MC | LC B/op A -> A+D1 |
| ------------------------ | ----------------------: | ----------------------: | -------: | ------: | ---------: | -------: | ---------: | ----------------: |
| net10.0 TTI 1/cycle off  | 25.620 [24.799, 26.914] | 28.126 [24.434, 30.317] |    1.098 |  17.702 |     17.862 |    1.009 |      1.575 |    0.000 -> 0.000 |
| net10.0 TTI 10/cycle off |    2.289 [2.257, 2.354] |    2.332 [2.233, 2.415] |    1.019 |  36.008 |     36.283 |    1.008 |      0.064 |    0.000 -> 0.000 |
| net10.0 TTI 10/hot off   |    2.594 [2.460, 2.899] |    2.622 [2.533, 2.705] |    1.011 |  22.235 |     21.097 |    0.949 |      0.124 |    0.000 -> 0.000 |

### Retention in capacity-pressure writes

Counts are the minimum and maximum observed after cleanup across all measured samples. Visible fractions are medians of final-batch fractions over new keys, not an admission probability. Non-evicting rows retain all 10,240 entries in every batch.

| Case                   | Library      | Base count range | Patch count range | Base visible fraction | Patch visible fraction |
| ---------------------- | ------------ | ---------------: | ----------------: | --------------------: | ---------------------: |
| TTL c1024 1 writer(s)  | loadingcache |        1024–1024 |         1024–1024 |               0.04106 |                0.04053 |
| TTL c1024 1 writer(s)  | memorycache  |         973–1024 |          973–1024 |               0.06973 |                0.06973 |
| TTI c1024 1 writer(s)  | loadingcache |        1024–1024 |         1024–1024 |               0.03862 |                0.03657 |
| TTI c1024 1 writer(s)  | memorycache  |         973–1024 |          973–1024 |               0.06724 |                0.06475 |
| TTL c1024 10 writer(s) | loadingcache |        1024–1024 |         1024–1024 |               0.03511 |                0.04019 |
| TTL c1024 10 writer(s) | memorycache  |         973–1024 |          973–1024 |               0.01494 |                0.01494 |
| TTI c1024 10 writer(s) | loadingcache |        1024–1024 |         1024–1024 |               0.03818 |                0.03755 |
| TTI c1024 10 writer(s) | memorycache  |         973–1024 |          973–1024 |               0.01494 |                0.01494 |

<details>
<summary>All six process medians, in round order</summary>

No process is excluded. The same case and round identify an alternating pair. The raw receipts additionally retain all sample values and power checks.

| Case                             | Library             | Base process ns/op                                   | Patch process ns/op                                  |
| -------------------------------- | ------------------- | ---------------------------------------------------- | ---------------------------------------------------- |
| net10.0 TTL 1/cycle off          | loadingcache        | 19.902, 20.271, 20.187, 20.601, 21.059, 20.903       | 20.037, 20.323, 20.541, 20.618, 20.659, 19.902       |
| net10.0 TTL 1/cycle off          | memorycache         | 17.501, 17.575, 17.698, 17.814, 18.113, 18.042       | 17.492, 17.467, 17.887, 18.332, 18.046, 17.911       |
| net10.0 TTI 1/cycle off          | loadingcache        | 23.890, 26.128, 25.460, 24.631, 25.486, 24.608       | 24.272, 23.606, 24.568, 24.531, 26.006, 24.690       |
| net10.0 TTI 1/cycle off          | memorycache         | 18.276, 18.240, 18.351, 18.381, 18.157, 18.219       | 18.283, 18.223, 18.195, 18.298, 18.328, 18.039       |
| net10.0 none 1/cycle off         | loadingcache        | 12.890, 13.195, 12.310, 13.035, 13.659, 13.026       | 13.329, 13.757, 12.516, 12.675, 12.683, 12.918       |
| net10.0 none 1/cycle off         | memorycache         | 17.820, 18.081, 17.839, 17.985, 18.216, 17.985       | 17.976, 17.964, 17.886, 17.870, 17.955, 17.958       |
| net10.0 TTL 1/cycle on           | loadingcache        | 26.822, 25.970, 26.193, 26.218, 25.594, 30.191       | 26.634, 27.255, 25.492, 26.066, 26.232, 26.510       |
| net10.0 TTL 1/cycle on           | memorycache         | 18.950, 18.811, 18.876, 18.814, 18.853, 18.980       | 18.935, 18.970, 18.865, 18.890, 18.997, 18.927       |
| net10.0 TTI 1/cycle on           | loadingcache        | 30.160, 29.079, 28.475, 27.917, 28.604, 29.051       | 28.499, 28.546, 28.289, 27.905, 28.324, 27.958       |
| net10.0 TTI 1/cycle on           | memorycache         | 19.388, 19.165, 18.925, 18.840, 19.029, 18.891       | 18.907, 19.106, 19.030, 18.923, 19.178, 18.759       |
| net10.0 TTL 10/cycle off         | loadingcache        | 2.156, 2.022, 2.099, 2.229, 2.379, 2.499             | 2.048, 1.941, 1.965, 2.222, 2.167, 2.233             |
| net10.0 TTL 10/cycle off         | memorycache         | 34.258, 34.091, 36.196, 17.857, 36.205, 35.989       | 33.532, 33.936, 29.344, 35.497, 34.268, 33.621       |
| net10.0 TTI 10/cycle off         | loadingcache        | 5.161, 4.431, 5.037, 4.851, 4.252, 4.927             | 5.048, 5.215, 4.463, 4.992, 5.308, 5.257             |
| net10.0 TTI 10/cycle off         | memorycache         | 27.137, 34.215, 34.671, 36.989, 36.044, 35.983       | 35.884, 31.123, 33.824, 32.827, 35.801, 36.503       |
| net10.0 TTL 10/hot off           | loadingcache        | 2.716, 2.910, 2.352, 2.433, 2.324, 2.581             | 2.558, 2.686, 2.452, 2.501, 2.956, 2.600             |
| net10.0 TTL 10/hot off           | memorycache         | 22.545, 16.292, 29.475, 25.347, 20.916, 22.656       | 31.177, 29.035, 33.327, 32.603, 33.245, 33.146       |
| net10.0 TTI 10/hot off           | loadingcache        | 92.665, 88.221, 84.260, 83.779, 90.523, 89.335       | 97.616, 101.659, 97.676, 98.022, 98.183, 96.155      |
| net10.0 TTI 10/hot off           | memorycache         | 37.380, 38.531, 32.888, 39.572, 40.543, 32.900       | 24.413, 27.492, 18.870, 25.331, 20.291, 25.096       |
| net8.0 TTL 1/cycle off           | loadingcache        | 21.531, 21.828, 21.555, 21.896, 22.093, 22.872       | 22.504, 21.964, 21.480, 21.738, 21.772, 21.925       |
| net8.0 TTL 1/cycle off           | memorycache         | 18.834, 18.838, 18.839, 18.822, 18.986, 19.030       | 18.917, 18.814, 18.737, 18.799, 18.897, 18.936       |
| net8.0 TTI 1/cycle off           | loadingcache        | 26.105, 25.380, 24.922, 25.359, 25.492, 25.035       | 26.580, 26.404, 26.379, 25.694, 26.639, 26.069       |
| net8.0 TTI 1/cycle off           | memorycache         | 18.713, 18.861, 18.625, 19.973, 18.944, 18.804       | 19.031, 19.251, 18.922, 18.837, 18.946, 19.097       |
| net8.0 none 1/cycle off          | loadingcache        | 14.801, 15.390, 14.960, 15.363, 15.480, 15.187       | 15.713, 15.739, 15.563, 15.723, 15.887, 16.213       |
| net8.0 none 1/cycle off          | memorycache         | 18.883, 18.779, 18.778, 18.641, 18.649, 18.909       | 18.805, 18.710, 18.793, 18.753, 19.093, 19.039       |
| net8.0 TTL 1/cycle on            | loadingcache        | 29.205, 28.440, 27.387, 28.524, 27.838, 27.891       | 28.066, 28.016, 29.021, 27.566, 27.992, 27.585       |
| net8.0 TTL 1/cycle on            | memorycache         | 20.281, 20.332, 20.227, 20.665, 20.452, 20.258       | 20.209, 20.252, 20.161, 20.343, 20.245, 20.227       |
| net8.0 TTI 1/cycle on            | loadingcache        | 30.299, 29.921, 29.647, 29.712, 29.454, 30.167       | 29.768, 30.998, 30.999, 29.238, 29.548, 29.851       |
| net8.0 TTI 1/cycle on            | memorycache         | 20.466, 20.600, 20.385, 20.424, 20.510, 20.400       | 20.273, 20.488, 20.352, 20.611, 20.444, 20.276       |
| none c16384 1 writer(s)          | loadingcache        | 280.301, 270.875, 270.425, 282.401, 304.940, 302.385 | 310.285, 319.566, 279.342, 274.963, 293.037, 286.886 |
| none c16384 1 writer(s)          | memorycache         | 124.531, 127.780, 127.686, 125.714, 123.311, 133.790 | 129.796, 130.136, 126.619, 124.165, 121.470, 123.050 |
| none c16384 1 writer(s)          | memorycache-bounded | 124.317, 124.777, 123.360, 122.465, 128.579, 125.658 | 133.224, 129.905, 123.340, 126.490, 124.455, 127.296 |
| TTL c16384 1 writer(s)           | loadingcache        | 508.373, 535.663, 521.253, 547.050, 518.520, 521.677 | 408.129, 384.271, 399.425, 413.830, 410.149, 396.477 |
| TTL c16384 1 writer(s)           | memorycache         | 127.925, 126.637, 125.085, 127.606, 127.409, 157.945 | 123.364, 128.580, 126.392, 128.791, 128.596, 126.364 |
| TTI c16384 1 writer(s)           | loadingcache        | 617.095, 534.432, 528.886, 517.186, 527.382, 528.330 | 422.743, 428.858, 384.436, 377.990, 410.525, 388.840 |
| TTI c16384 1 writer(s)           | memorycache         | 126.118, 127.438, 128.826, 128.281, 126.349, 125.405 | 125.957, 130.056, 125.595, 125.264, 124.303, 124.833 |
| none c16384 10 writer(s)         | loadingcache        | 290.190, 297.644, 339.900, 306.258, 294.805, 297.856 | 289.466, 294.438, 295.213, 287.400, 305.284, 297.091 |
| none c16384 10 writer(s)         | memorycache         | 132.107, 133.192, 131.148, 140.921, 133.563, 137.975 | 131.888, 132.258, 132.980, 133.214, 136.099, 135.868 |
| TTL c16384 10 writer(s)          | loadingcache        | 542.445, 553.964, 544.584, 572.568, 574.815, 533.048 | 391.635, 387.139, 379.556, 407.823, 388.526, 371.645 |
| TTL c16384 10 writer(s)          | memorycache         | 134.090, 131.987, 137.122, 137.130, 136.248, 141.001 | 142.291, 143.835, 133.548, 136.621, 139.137, 133.251 |
| TTI c16384 10 writer(s)          | loadingcache        | 559.286, 547.503, 540.676, 542.593, 589.534, 540.610 | 373.008, 376.948, 370.477, 371.697, 385.344, 377.557 |
| TTI c16384 10 writer(s)          | memorycache         | 134.575, 135.221, 141.436, 136.726, 136.292, 133.807 | 137.156, 136.957, 134.127, 134.907, 137.806, 135.831 |
| TTL c1024 1 writer(s)            | loadingcache        | 456.252, 457.184, 446.222, 476.309, 447.520, 510.088 | 439.221, 414.521, 417.588, 436.563, 428.030, 396.071 |
| TTL c1024 1 writer(s)            | memorycache         | 58.382, 59.399, 59.084, 57.608, 57.368, 57.666       | 58.141, 59.407, 60.585, 59.263, 58.416, 58.442       |
| TTI c1024 1 writer(s)            | loadingcache        | 454.491, 443.857, 478.817, 437.058, 442.627, 452.120 | 405.162, 399.875, 401.461, 404.959, 404.205, 416.450 |
| TTI c1024 1 writer(s)            | memorycache         | 57.515, 59.242, 57.247, 56.429, 58.710, 57.039       | 58.424, 58.897, 58.180, 57.096, 58.613, 58.930       |
| TTL c1024 10 writer(s)           | loadingcache        | 438.767, 422.474, 429.802, 501.996, 424.165, 445.287 | 414.711, 412.488, 410.128, 419.077, 400.100, 404.010 |
| TTL c1024 10 writer(s)           | memorycache         | 34.320, 33.163, 33.706, 33.051, 32.312, 32.881       | 34.101, 33.202, 33.192, 32.629, 34.192, 33.552       |
| TTI c1024 10 writer(s)           | loadingcache        | 440.610, 429.609, 453.975, 418.917, 451.214, 422.405 | 405.190, 407.200, 418.583, 407.425, 403.861, 408.416 |
| TTI c1024 10 writer(s)           | memorycache         | 32.930, 33.368, 32.924, 32.309, 32.731, 34.265       | 32.558, 32.418, 32.743, 33.143, 32.793, 33.305       |
| A/A+D1: net10.0 TTI 1/cycle off  | loadingcache        | 25.661, 24.799, 25.491, 26.914, 25.580, 26.301       | 24.832, 29.659, 26.717, 29.534, 24.434, 30.317       |
| A/A+D1: net10.0 TTI 1/cycle off  | memorycache         | 17.208, 17.638, 17.610, 17.830, 17.859, 17.765       | 17.429, 17.655, 17.933, 17.899, 17.830, 17.894       |
| A/A+D1: net10.0 TTI 10/cycle off | loadingcache        | 2.278, 2.288, 2.291, 2.257, 2.318, 2.354             | 2.306, 2.233, 2.359, 2.382, 2.298, 2.415             |
| A/A+D1: net10.0 TTI 10/cycle off | memorycache         | 36.968, 36.577, 36.191, 35.825, 35.489, 35.756       | 36.763, 36.244, 36.209, 36.321, 35.623, 36.431       |
| A/A+D1: net10.0 TTI 10/hot off   | loadingcache        | 2.460, 2.552, 2.671, 2.555, 2.633, 2.899             | 2.545, 2.601, 2.643, 2.705, 2.679, 2.533             |
| A/A+D1: net10.0 TTI 10/hot off   | memorycache         | 21.244, 24.398, 21.619, 22.532, 21.938, 23.140       | 20.988, 21.207, 19.607, 21.316, 20.394, 21.364       |

</details>
<!-- results-end -->

## MemoryCache capacity semantics

Under contention, the measured MemoryCache 10.0.12 binary can reject a new entry after 100 failed capacity-accounting CAS attempts, even below its limit. This is a semantic difference, not a performance advantage attributed to LC. The earlier controlled reproduction retained 10,238 of 10,240 writes at capacity 16,384. Its log is `artifacts/perf-expiry-baseline/results/count-diagnostic-ac/net10.0-write-none-w10-c16384-count-diagnostic-r1-memorycache.log`; the actual DLL decompilation is `MemoryCache.decompiled.cs` in that same harness root. `UpdateCacheSizeExceedsCapacity` at line 483 contains the retry limit and `SetEntry` at line 261 handles rejection as capacity eviction. That mechanism is consistent with the observed missing entries; callbacks were not instrumented.

The pressure-group timings therefore compare insertion attempts under different admission/eviction policies, not equal numbers of retained writes. Counts and final-batch visible-new-key fractions accompany the results. The non-evicting group requires complete retention in every measured batch.

## Correctness

Before the structural change, the Release solution build passed and core tests passed 753/753 on both net8 and net10. The final patch passes 790/790 on each, DI 10/10 on each, stress 10/10 on each, and ConsumerSmoke on both. Build and `dotnet csharpier check src tests benchmarks` exit zero; the formatter checks 146 files. `global.json` is unchanged. No existing assertion was removed or weakened.

The measurement-only A+D1 composition applied without conflicts and additionally passed net10 core 786/786. The 37 additional D1 cases comprise three wheel-teardown tests, 31 engine lifecycle cases, and three extra expiry configurations for the existing retention test. They cover every wheel level, partial advance/frontiers, caller-owned detached dues, irreversible retirement, infinite/finite duration transitions with scheduler on/off, Clear followed by same-key insertion, gated stale reads across Clear/invalidate/Dispose, same-Entry refresh rescheduling after hard expiry, and GC release of unrelated values while an old refresh remains alive. The post-review regression `OrphanedEntryCannotAttachItsNodeToTheWheelAfterLifecycleChange` adds nine deterministic combinations: an Entry absent from the map retains a bucket-linked, detached, or never-scheduled node across Clear, Dispose, or DisposeAsync. Late scheduling retires and clears the orphan node without changing the replacement wheel or the new entry with the same key. An unscheduled, unretired node has no wheel owner and is structurally reusable, but the engine rejects the old Entry before reuse. Existing duration mutation, scheduler race, rollback, publication, variable-expiry, bulk, weak-reference and stress tests also pass.

## Local evidence and reproduction

The reusable harness and raw data remain Git-ignored under `artifacts/perf-expiry-baseline/`; Task D verification logs and the write analyser are under `artifacts/perf-fixed-expiry/`. The source worktrees and binaries are not part of this change.

The three independently validated result directories are `results/d1-hit-formal-ac-20261001` (17:37:45–17:54:01 UTC), `results/d1-a-hit-formal-ac-20261001` (18:33:15–18:36:43 UTC) and `results/d1-write-formal-ac-20261001` (18:37:30–18:50:01 UTC), all on 1 October 2026. Each includes `validation.json`, `comparisons.json`, `process-medians.csv`, plan and per-process receipts. The original cohort files remain unchanged. `artifacts/perf-fixed-expiry/a-hit-gate.json` records the explicit gate; `read-il-layout.json` records the pre-A IL/offset inspection. Correctness receipts are in `validation/results.json`, `a-stack-validation/results.json` and `post-review-validation/results.json`. The final run adds only tests and documentation; product code and the measured binaries are unchanged.

LC assembly SHA256 (shared across the net8/net10 probes):

- Base: `45ced18f2221f12851c24f44e94b7b1647742ee4fd9039b5797b01183829ce6f`.
- Patch: `c528a90b183074285eeb109e6a13c6c75d7dcba63a42e74045aef55c30446938`.
- A-only supplement: `3e8f3ceba3bf65838298497c99d5f2b6ab951a9980dc5a231481df5996b0721d`.
- A+D1 supplement: `013edfb09c14b347ea2488b19a8cfeed174c559e0656517266378c0032365d9e`.

From `artifacts/perf-expiry-baseline/`, the formal commands were:

```sh
uv run --no-project python3 matrix.py run \
  --cohort d1-hit-formal-ac-20261001 \
  --variants d1-base d1-entry-owned --tfms net10.0 net8.0 --suite hit \
  --rounds 6 --warmups 3 --runs 7
uv run --no-project python3 matrix.py run \
  --cohort d1-a-hit-formal-ac-20261001 \
  --variants d1-a-base d1-a-entry-owned --tfms net10.0 --suite hit \
  --case-filter '^net10[.]0-hit-access-w(1|10)-(cycle|hot)-stats-off$' \
  --rounds 6 --warmups 3 --runs 7
uv run --no-project python3 matrix.py run \
  --cohort d1-write-formal-ac-20261001 \
  --variants d1-base d1-entry-owned --tfms net10.0 --suite write \
  --case-filter '^net10[.]0-write-(write|access)-|^net10[.]0-write-none-w(1|10)-c16384$' \
  --rounds 6 --warmups 3 --runs 7
```

`matrix.py build --label <new-label> --lc-root <absolute-worktree>` passes `-p:LcRoot` to both harness projects without replacing measured labels. The wrapper acquires the shared lock internally; do not wrap it again. Independent validation uses `artifacts/perf-tti-touch/analyse_hits.py` and `artifacts/perf-fixed-expiry/analyse_writes.py`, each with the corresponding results directory as its argument.
