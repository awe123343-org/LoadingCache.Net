# Architecture decision records

Audience: library maintainers and contributors.

Keep durable engineering decisions here: public contracts, ownership and memory-model
invariants, algorithms, dependency choices, alternatives, tradeoffs and reproducible
validation evidence. An ADR belongs here because contributors need its reasoning,
regardless of whether a person or an agent drafted it.

Agent-only prompts, task assignments, session plans, temporary experiments and handoff
notes belong in `docs/local-adr-notes/`, excluded locally through `.git/info/exclude`:

```gitignore
/docs/local-adr-notes/
```

This local exclusion is not distributed with a clone. Configure it before creating
agent notes, and verify it with `git check-ignore -v docs/local-adr-notes/README.md`.
Do not force-add those notes. For mixed documents, keep the durable decision here and
move execution instructions into the local directory. Published documentation must
remain understandable without local notes and must not link to excluded handoff files.

ADRs 0001–0014 are maintainer-facing decisions. Superseded implementation choices
remain useful history; identify their replacement rather than treating them as agent
notes. Current feature and validation status lives in the
[feature matrix](../feature-matrix.md) and [release readiness](../release-readiness.md).
