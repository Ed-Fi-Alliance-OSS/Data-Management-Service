# Epic: Operational Guardrails, Repair Tools, and Observability

## Description

Deliver operational safety features and diagnostics for the relational redesign, informed by:

- `reference/design/backend-redesign/strengths-risks.md` (risks + mitigations)
- `reference/design/backend-redesign/transactions-and-concurrency.md` (edge completeness)

Focus areas:
- Prevent silent correctness drift (especially `dms.ReferenceEdge` integrity).
- Provide repair tooling for derived artifacts.
- Add instrumentation for identity closures, lock waits, and edge maintenance churn.
- Add guardrails for extreme fanout/closure sizes.

Authorization remains out of scope.

## Stories

- `00-referenceedge-audit-repair.md` — Offline audit/repair tool for `dms.ReferenceEdge`
- `01-referenceedge-watchdog.md` — Sampling-based online verification (optional self-heal)
- `02-instrumentation.md` — Metrics/logs for locks, closures, edge churn, and retries
- `03-guardrails.md` — Bounds/limits for closure expansion and high-fanout scenarios
- `04-performance-benchmarks.md` — Benchmark harness for read/write hot paths

