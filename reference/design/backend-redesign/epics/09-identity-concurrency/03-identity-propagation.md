---
jira: DMS-999
jira_url: https://edfi.atlassian.net/browse/DMS-999
---

# Story: Identity Propagation via Cascades (No Application Closure Traversal)

## Description

Implement strict identity maintenance for identity updates without application-managed identity closure traversal:

- Persist identity-component referenced identity values as columns and enforce full-composite FKs. Form physical FK
  candidates only after canonical storage mapping and de-duplication; derive statement-scoped value-flow proof
  obligations for both engines; evaluate PostgreSQL's fixed actions and jointly select SQL Server actions that satisfy
  both value-flow safety and error 1785 (see `design-docs/mssql-cascading.md`).
- Use per-resource triggers to recompute `dms.ReferentialIdentity` row-locally when identity projection values change (including changes caused by cascaded updates to identity-component propagated identity columns).

Align with:
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
- `reference/design/backend-redesign/design-docs/data-model.md`
- `reference/design/backend-redesign/design-docs/mssql-cascading.md` (cross-engine value-flow safety + SQL Server
  action selection)

## Acceptance Criteria

- After commit, there is no window where `dms.ReferentialIdentity` is stale for any impacted document.
- Identity updates propagate transitively via native cascades, without application traversal or an identity-value
  propagation trigger.
- Unsafe value flow fails derivation on both engines. SQL Server-specific 1785 infeasibility also fails derivation.
- A failure result is distinct from a successful relational model/manifest.
- Integration tests demonstrate:
  - an upstream identity change causes dependent referential identities to update in the same transaction.

## Tasks

1. Emit/validate DDL for identity-component propagation:
   - map logical references to canonical storage columns and de-duplicate by physical FK identity before assigning
     actions; physical identity excludes action/mode,
   - derive dialect-neutral mutation events and exact changed-component lineage, origin-row correlation, optional-site
     presence, and statement-boundary proof obligations,
   - evaluate PostgreSQL's fixed `CASCADE`/`NO ACTION` actions against those obligations,
   - on SQL Server, jointly select `NativeCascade`, `NoPropagation`, or `ImmutableNoAction` so the final action graph is
     legal under error 1785 and satisfies every value-flow obligation,
   - attach an ordered `CoverageCertificates` list to each success-only `NoPropagation` decision keyed by physical FK id,
     with one certificate per mutation-origin and changed-component case,
   - retain the full composite key on every engine; there is no `DocumentId`-only shape or identity-value propagation
     trigger.
2. Emit per-resource triggers to maintain `dms.ReferentialIdentity` transactionally on identity projection changes, recomputing `ReferentialId` using the engine UUIDv5 helper (`E02-S06`).
3. Integrate identity-stamp behavior (`IdentityVersion/IdentityLastModifiedAt`) with trigger maintenance.
4. Add integration tests for a small identity dependency chain scenario.
5. Share a versioned conformance corpus with MetaEd (METAED-1667), including shared-column independent parents,
   optional co-presence, row-correlation, abstract-identity trigger-boundary, and SQL Server 1785 cases.
6. Keep cross-table/root-to-child equality propagation out of this story; it is separate future work and cannot satisfy a
   reference-FK coverage obligation.
7. Test deterministic solver bounds with an adversarial graph and distinguish
   `CascadeClassificationComplexityExceeded` from `NoSafeSqlServerAssignment`.
