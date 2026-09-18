# Current-source stability — 18 September 2026

**Twelve 30-minute soaks, one continuous eight-hour endurance and terminal source/build/runtime checks passed.** This qualifies the explicit-limit macOS ARM64 profile, not every V1/platform/default-admission combination.

The unique sequence ran 07:36:17–16:36:23 UTC: six cases per runtime, sequential runtime processes, then one endurance. No resume, accumulated interruptions, retries or budget changes.

| Check                          | Result                                                                              |
| ------------------------------ | ----------------------------------------------------------------------------------- |
| Soaks                          | 12/12 Passed, zero failed/skipped, each ≥1,800 seconds.                             |
| Actual runtimes/platform       | .NET 8.0.31/10.0.12, macOS ARM64.                                                   |
| Continuous endurance           | 28,800.0301995 seconds; 18,030,661,120 operations.                                  |
| Four-personality recreation    | 96 cycles, alternating statistics.                                                  |
| Retention                      | 92 post-warmup samples, 18 complete five-sample median windows.                     |
| Managed baseline/max growth    | 1,513,744 / 93,424 bytes, below unchanged 16 MiB growth limit.                      |
| Independently recomputed slope | 10,189.524025 bytes/hour, below unchanged 1 MiB/hour.                               |
| Quiescent observations         | 11,904 snapshots: active/in-flight/read-write backlog/notification queues all zero. |
| Capacity/execution             | Residents 0–256, weight=count; loader peak four, matching explicit limit.           |
| Unexpected endurance faults    | Zero maintenance/listener faults; old retired targets zero in all 96 observations.  |

Original batch drain, payload identity, clear and disposal assertions ran. Feature-soak deliberate listener failures/overload remain expected contract outcomes. GC/window sampling is risk evidence, not proof of all interleavings or workloads.

## Linkage

Independent review passed 3,984 checks: 71 mapping paths, 1,057 build inputs, 208 evaluated dependencies, five output groups, runtime inventories, stage receipts and complete runner manifests. Both bridges were terminal Passed. Seventy mapped tracked blobs from original `b8c9203524b58d7fb0081a4c24dad67e33990d93` matched working/isolated inputs; the remaining mapping was the frozen bridge. The isolated checkout's base `69e6e5b` did not identify its actual dirty-snapshot inputs. Executing DLL/MVID evidence was checked, not inferred from HEAD or exit zero.

| Evidence              | SHA256                                                             |
| --------------------- | ------------------------------------------------------------------ |
| Build manifest        | `ca619349bab01d6d1b753b8ea7e3a81a04496febb6b719f5f28e1926d8a049d8` |
| Actual core DLL       | `e9d8d6f24b9cfe2fb88d420fe79b15c22e1348c7bc6d975dbb1052044e30ffc2` |
| Endurance raw         | `36ee43d9fa78ec18b915a543f43fc50892f6e339ead23b0f96f057fdbab5d8f9` |
| Raw qualification     | `13e20249a457659da4e26a5cb206e26dc5445cdf22feed0ede606ea3099cf704` |
| Linkage qualification | `024583a2df27c2a57f27a026be26abecf95f210fa2e975c5ff0bbecd344d1163` |

Local unpublished raw/commands/TRX/audits live under `artifacts/opt-in-load-limits-20260918/stability-run01/formal01/`, `stability-final-raw-review01/` and `stability-final-linkage-review01/`. Each review's `audit.py` was run with `PYTHONDONTWRITEBYTECODE=1 uv run --offline --no-project python`.

C/F (plus bulk K) were explicit. Do not describe this as unlimited-default eight-hour retention evidence; default admission has separate gated and fixed-rate HTTP checks. Old build03 bridge failure, old HTTP failure and resident A/A Inconclusive remain unchanged.

## Linux ARM64 follow-up

Current source in a pinned isolated Linux ARM64 image passed 747 core + 10 DI + 10 short stress cases on each actual runtime: 1,534 total, zero failed/skipped. Two source consumers and four Release builds passed without warnings/errors. All 144 source/config inputs stayed unchanged; runtime/module identities and 1,033 outputs were recorded. No new Linux long run or benchmark was performed.

Local evidence: `artifacts/opt-in-load-limits-20260918/linux-validation01/`; qualification SHA256 `8c75d5c14ae5983d0cfb0565094d71aad8cadc8fb7625a11b9611f5b948ce413`. Its `run-linux.py` used `uv run --offline --no-project` and a new output. Linux has its own build identity, not macOS DLL hashes. Later hosted Windows/Linux x64 managed CI and remaining gaps are recorded in [release readiness](release-readiness.md).
