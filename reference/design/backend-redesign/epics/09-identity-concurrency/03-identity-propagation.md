---
jira: DMS-999
jira_url: https://edfi.atlassian.net/browse/DMS-999
---

# Story: Identity Propagation via Cascades/Triggers (No Closure Traversal)

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

1. Emit/validate DDL for identity-component propagation:
   - one complete full-composite FK vector per target; PostgreSQL assigns fixed actions mechanically and is never
     classified or pruned for multiple paths, and
   - on SQL Server, deterministic bounded global action selection over physical candidates. Every covered `NO ACTION`
     diamond edge needs origin-aware same-root-row/same-boundary coverage for every source-update flow;
     provider-independent validation rejects identity cycles.
     Every FK keeps the full vector and there is no `DocumentId`-only shape or identity-value propagation trigger (see
     `design-docs/mssql-cascading.md`).
2. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes, recomputing `ReferentialId` using the engine UUIDv5 helper (`E02-S06`).
3. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
4. Add integration tests for a small identity dependency chain scenario.
