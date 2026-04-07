---
jira: DMS-1128
jira_url: https://edfi.atlassian.net/browse/DMS-1128
---

# Story: Implement MSSQL Hybrid Cascade Classification, Trigger Inventory, and DDL Emission

## Description

Implement the SQL Server hybrid cascade revision described in `reference/design/backend-redesign/design-docs/mssql-cascading.md` so MSSQL identity propagation is derived from the final physical safety and coverage result instead of defaulting every propagation-eligible edge to fallback-managed `DocumentId`-only FKs.

This story is the SQL Server-specific follow-on to `03-identity-propagation.md` and lands ahead of `reference/design/backend-redesign/epics/10-update-tracking-change-queries/07-mssql-fallback-update-tracking.md`, which remains the downstream update-tracking verification slice.

Depends on:
- `03-identity-propagation.md` (`DMS-999`) — owns the base identity propagation contract and the initial cascade/trigger implementation

Align with:
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` — reference constraint derivation
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` — trigger inventory derivation
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` — DDL emission
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` — verification harness

## Acceptance Criteria

- The derived model and manifest emit final MSSQL per-edge and per-table metadata including `MssqlPropagationMode`, `MssqlFkShape`, `MssqlTableUpdateStrategy`, and pruning or coverage diagnostics.
- Deterministic pruning, SCC or self-loop handling, and root-scoped aggregation are implemented explicitly for MSSQL. Root-scoped SCC or self-loop edges never end as `NativeCascade` in v1; covered pruned outcomes may reconcile to `NoPropagation`; uncovered cycle-derived states fail derivation with diagnostics.
- Table-level fallback-host versus cascade-target validation and post-validation coverage reconciliation run to a stable fixpoint using explicit fallback-carrier responsibilities rather than table interception alone.
- Fallback trigger inventory is emitted only for `FallbackIntercepted` tables, includes non-root referrers, and emits no fallback trigger for fully covered pruned edges.
- MSSQL DDL emits `ON UPDATE CASCADE` for final `NativeCascade` edges, emits `ON UPDATE NO ACTION` for final `NoPropagation` edges with the correct `FullComposite` or `DocumentIdOnly` FK shape, and leaves trigger management only on true `TriggerFallback` edges.
- Verification covers the core MSSQL matrix from the design doc, including safe native cascades, covered no-propagation variants, true fallback replacement, post-unification repeated-path classification, mixed-mode tables, validation demotions and reconciliation, uncovered cycle failure, non-root fallback referrers, `NoPropagation + DocumentIdOnly` direct-write proof, abstract identity-table safe edges, and PostgreSQL/MSSQL parity.

## Tasks

1. Add final MSSQL propagation metadata and diagnostics to the derived model and manifest: `MssqlPropagationMode`, `MssqlFkShape`, `MssqlTableUpdateStrategy`, pruning reasons, winners, carrier coverage, and validation outcomes.
2. Implement deterministic pruning, SCC or self-loop handling, root-scoped aggregation, explicit fallback-carrier derivation, table-level fallback-host versus cascade-target validation to a fixpoint, and post-validation coverage reconciliation against the final carrier set.
3. Revise `DeriveTriggerInventoryPass` so fallback inventory is sourced from explicit fallback-carrier responsibilities, includes non-root bindings, and emits triggers only for `FallbackIntercepted` tables after storage-column resolution.
4. Revise MSSQL reference constraint and DDL emission so final `NativeCascade` edges emit full composite `ON UPDATE CASCADE`, fully covered pruned edges can become `NoPropagation + FullComposite` or `NoPropagation + DocumentIdOnly`, only final `TriggerFallback` edges use `DocumentId`-only FK shape plus fallback trigger management, and derivation fails deterministically if the final native-cascade union remains MSSQL-invalid in v1.
5. Add the core MSSQL verification matrix from the design doc, including direct-write coverage for `NoPropagation + DocumentIdOnly`, uncovered cycle failure, non-root fallback referrers, mixed-mode table validation, and PostgreSQL/MSSQL parity.
6. Keep `DMS-1127` scoped to the downstream update-tracking verification of fallback-managed propagation after this classification and DDL work lands.
