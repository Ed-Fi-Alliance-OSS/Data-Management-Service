# Story: Identity Propagation via Cascades/Triggers (No Closure Traversal)

## Description

Implement strict identity maintenance for identity updates without application-managed identity closure traversal:

- Persist identity-component referenced identity values as columns and enforce composite FKs with `ON UPDATE CASCADE` (or trigger-based propagation where required).
- Use per-resource triggers to recompute `dms.ReferentialIdentity` row-locally when identity projection values change (including changes caused by cascaded updates to identity-component propagated identity columns).

Align with:
- `reference/design/backend-redesign/transactions-and-concurrency.md`
- `reference/design/backend-redesign/data-model.md`

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Identity updates propagate transitively via cascades (and triggers where required), without application traversal.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction.

## Tasks

1. Emit/validate DDL for identity-component propagation:
   - composite FKs with `ON UPDATE CASCADE` where allowed, and
   - trigger-based propagation fallback where required (SQL Server cascade-path restrictions).
2. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes.
3. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
4. Add integration tests for a small identity dependency chain scenario.
