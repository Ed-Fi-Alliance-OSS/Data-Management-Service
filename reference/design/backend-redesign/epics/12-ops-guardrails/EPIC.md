# Epic: Operational Guardrails, Repair Tools, and Observability

## Description

Deliver operational safety features and diagnostics for the relational redesign, informed by:

- `reference/design/backend-redesign/strengths-risks.md` (risks + mitigations)
- `reference/design/backend-redesign/transactions-and-concurrency.md` (cascade fan-out and retry guidance)

Focus areas:
- Prevent silent correctness drift (especially trigger/stamp coverage and `dms.ReferentialIdentity` integrity).
- Provide repair tooling for derived artifacts (`dms.ReferentialIdentity`, journals, cache projections).
- Add instrumentation for cascades, stamp/journal rates, and deadlock retries.
- Add guardrails for extreme identity-update fan-out scenarios.

Authorization remains out of scope.

## Stories

- `00-referentialidentity-audit-repair.md` — Offline audit/repair tool for `dms.ReferentialIdentity` and propagation integrity
- `01-referentialidentity-watchdog.md` — Sampling-based online verification (optional self-heal)
- `02-instrumentation.md` — Metrics/logs for cascades, stamps/journals, and retries
- `03-guardrails.md` — Bounds/limits for identity-update fan-out and retry behavior
- `04-performance-benchmarks.md` — Benchmark harness for read/write hot paths
