# Story: Identity Closure Expansion + Transactional Recompute to Fixpoint

## Description

Implement strict identity maintenance for identity updates:

- Expand the impacted identity closure using `dms.ReferenceEdge(IsIdentityComponent=true)` to a fixpoint.
- Acquire update locks for impacted documents’ `dms.IdentityLock` rows.
- Recompute identity projection values and update `dms.ReferentialIdentity` set-based.
- Bump `IdentityVersion/IdentityLastModifiedAt` only when identity projection values actually change.

Align with `reference/design/backend-redesign/transactions-and-concurrency.md`.

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Closure expansion reaches a fixpoint and includes all transitive dependents.
- Locking prevents phantom dependents during closure computation.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction.

## Tasks

1. Implement closure expansion logic using iterative queries on `dms.ReferenceEdge(IsIdentityComponent=true)`.
2. Implement update-lock acquisition for the closure in deterministic order.
3. Implement set-based recompute/replace of `dms.ReferentialIdentity` rows for impacted documents.
4. Integrate `IdentityVersion` stamping with recompute results (only when changed).
5. Add integration tests for a small dependency chain scenario.

