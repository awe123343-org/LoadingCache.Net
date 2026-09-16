# Parallel resident replacement evidence

The fresh matched statistics OFF/OFF and ON/ON comparison **DID NOT PASS the 100-configuration gate**. Default statistics remain OFF.
Of 100 preboxed-read/replacement runtime/configurations, 68 were lower in both rounds,
27 higher in both, and 5 mixed or tied. This records a measured gate, not an adoption or release decision.

| Group | Lower cost in both rounds | Higher cost in both | Mixed / tied |
| --- | ---: | ---: | ---: |
| kind: read | 39 | 26 | 3 |
| kind: replacement | 29 | 1 | 2 |
| statistics: off | 36 | 12 | 2 |
| statistics: on | 32 | 15 | 3 |

The kind rows partition the same 100 configurations as the statistics rows; do not add them together.

[Full comparison](comparison.json), [main-case round ratios](comparison.csv), and [verified evidence](evidence.json)
retain all outcomes. Native-key reads, trace hit rate/end occupancy and fan-in backend calls are reported separately
in the analyzer's category CSVs; they are not substituted for the cost gate. Medians are not tail percentiles.

The [fresh matrix](../../../artifacts/benchmarks/memorycache-goal-20260916/parallel-resident-put/memorycache-full-01) contains 584 processes and 2,920 measured samples with exact commands/raw hashes.
The original-driver paired results remain separate:
- `write-normal-01`: 32 processes / 224 samples; {'lower': 8, 'higher': 0, 'mixed': 0}.
- `controls-normal-01`: 64 processes / 448 samples; {'lower': 11, 'higher': 2, 'mixed': 3}.
- `read-confirmation-01`: 16 processes / 112 samples; {'lower': 0, 'higher': 0, 'mixed': 2}.
Every non-improving paired round is listed in `evidence.json`; confirmations do not overwrite the original controls.

[Final validation](../../../artifacts/validation/memorycache-goal-20260916/parallel-resident-put-final-01) passed 1516 tests and two consumers;
[soak](../../../artifacts/validation/memorycache-goal-20260916/parallel-resident-put-soak) passed twelve five-minute cases, six per actual runtime.
The final source snapshot includes the updated lifecycle fixture. The initial validation is retained as separate evidence.
Validation binary bindings use surviving outputs checked after validation, not per-test-process emitted hashes.
Core SHA256: `d6b99727eb98e3c8a8396e00723ca6473f6e682074a0b9d9bf0070d622fba275`. Runtime/source/harness identities and exact argv are preserved in the linked manifests.

Reproduce offline export with `uv run --offline --no-project python artifacts/benchmarks/memorycache-goal-20260916/parallel-resident-put/export-report.py --execute --archive`.
The script refuses existing output directories. It runs only verification and the existing analyzer, never a cache workload.
When present, `evidence.tar.gz` preserves source/raw/log/TRX evidence without DLL/PDB/build trees;
`evidence-members.json` lists every member and SHA256, and `archive.json` records archive identity/read-back verification.
Extract the archive at the repository root to restore artifact-relative links. Binaries are deliberately excluded;
live binary re-verification requires the original local frozen/build outputs.
