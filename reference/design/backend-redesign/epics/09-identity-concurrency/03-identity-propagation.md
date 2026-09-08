---
jira: DMS-999
jira_url: https://edfi.atlassian.net/browse/DMS-999
---

# Story: Identity Propagation via Native Cascades (No Closure Traversal)

## Description

Implement strict identity maintenance for identity updates without application-managed identity closure traversal:

- Persist identity-component referenced identity values as columns and enforce full-composite FKs. PostgreSQL retains
  `ON UPDATE CASCADE` on eligible edges; SQL Server retains native cascades where safe and uses full-composite
  `ON UPDATE NO ACTION` only for safe convergence cuts under [SQL Server foreign-key pruning](../../design-docs/sql-server-pruning.md).
- Use per-resource triggers to recompute `dms.ReferentialIdentity` row-locally when identity projection values change (including changes caused by cascaded updates to identity-component propagated identity columns).

Align with:
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/data-model.md`

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Identity updates propagate transitively via native FK cascades, without application traversal.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction.

## Tasks

1. Emit/validate DDL for identity-component propagation:
   - full-composite FKs with provider actions selected by [SQL Server foreign-key pruning](../../design-docs/sql-server-pruning.md), and
   - no `MssqlIdentityPropagationTrigger` fallback.
2. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes, recomputing `ReferentialId` using the engine UUIDv5 helper (`E02-S06`).
3. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
4. Add integration tests for a small identity dependency chain scenario.
