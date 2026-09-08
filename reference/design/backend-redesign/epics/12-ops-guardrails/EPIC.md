---
jira: DMS-1014
jira_url: https://edfi.atlassian.net/browse/DMS-1014
---


# Epic: Operational Guardrails, Repair Tools, and Observability

## Description

Deliver operational safety features and diagnostics for the relational redesign, informed by:

- `reference/design/backend-redesign/design-docs/strengths-risks.md` (risks + mitigations)
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (cascade fan-out and retry guidance)

Focus areas:
- Prevent silent correctness drift (especially trigger/stamp coverage and `dms.ReferentialIdentity` integrity).
- Prevent post-provisioning drift in deterministic seed/fingerprint artifacts by running DMS with read-only access to `dms.ResourceKey` and `dms.EffectiveSchema` (provision with a separate privileged role).
- Provide repair tooling for derived artifacts (`dms.ReferentialIdentity`, journals, cache projections).
- Add instrumentation for cascades, stamp/journal rates, and deadlock retries.
- Add guardrails for extreme identity-update fan-out scenarios.

Authorization remains out of scope.

## Stories

- `DMS-1015` — `00-referentialidentity-audit-repair.md` — Offline audit/repair tool for `dms.ReferentialIdentity` and propagation integrity
- `DMS-1016` — `01-referentialidentity-watchdog.md` — Sampling-based online verification (optional self-heal)
- `DMS-1017` — `02-instrumentation.md` — Metrics/logs for cascades, stamps/journals, and retries
- `DMS-1018` — `03-guardrails.md` — Bounds/limits for identity-update fan-out and retry behavior
- `DMS-1019` — `04-performance-benchmarks.md` — Benchmark harness for read/write hot paths
