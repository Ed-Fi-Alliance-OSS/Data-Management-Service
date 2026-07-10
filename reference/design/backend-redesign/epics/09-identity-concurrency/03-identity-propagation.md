---
jira: DMS-999
jira_url: https://edfi.atlassian.net/browse/DMS-999
---

# Story: Identity Propagation via Cascades/Triggers (No Closure Traversal)

> **Historical delivery boundary.** DMS-999 is complete and owns the original identity-propagation integration and
> row-local `dms.ReferentialIdentity` maintenance described here. It does not own the revised `v2` complete-vector shape,
> post-key-unification effective identity-dependency derivation/cycle guard, provider action derivation, or full-schema
> qualification. DMS-1274 owns effective dependencies, complete vectors, physical candidates, and the effective guard;
> DMS-1258 owns PostgreSQL fixed actions plus SQL Server physical-cycle
> legality and diamond selection; DMS-1277 owns full-schema qualification. Current-design references below describe the
> integration contract for DMS-999's completed maintenance behavior, not additional work attributed to DMS-999.

## Description

Implement strict identity maintenance for identity updates without application-managed identity closure traversal:

- Persist identity-component referenced identity values as columns and enforce composite FKs with `ON UPDATE CASCADE` where eligible (on SQL Server, foreign-key pruning selects the surviving cascade edge; see `design-docs/mssql-cascading.md`).
- Use per-resource triggers to recompute `dms.ReferentialIdentity` row-locally when identity projection values change (including changes caused by cascaded updates to identity-component propagated identity columns).

Align with:
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` (SQL Server foreign-key pruning + fail-fast)

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Identity updates propagate transitively via cascades (and triggers where required), without application traversal.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction.

## Tasks

1. Integrate identity propagation with the row-local maintenance behavior owned by this completed story. The current
   `v2` DDL contract is delegated rather than delivered by DMS-999:
   - DMS-1274 promotes canonical identity overlaps atomically, rejects effective identity cycles, certifies omitted edges
     as origin-terminal, and derives one complete full-composite FK vector per target plus physical candidates;
   - DMS-1258 assigns PostgreSQL actions mechanically and owns SQL Server physical-cycle legality plus deterministic
     bounded diamond selection; and
   - DMS-1277 qualifies the resulting provider behavior at supported-schema scale.
   Every FK keeps the full vector and there is no `DocumentId`-only shape or identity-value propagation trigger (see
   `design-docs/mssql-cascading.md`).
2. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes, recomputing `ReferentialId` using the engine UUIDv5 helper (`E02-S06`).
3. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
4. Add integration tests for a small identity dependency chain scenario.
