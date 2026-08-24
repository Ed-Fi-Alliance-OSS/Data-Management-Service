---
jira: DMS-1310
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Finalize DocumentCache Schema and Provider DDL

## Design References

- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Relational data model**: reference/design/backend-redesign/design-docs/data-model.md
- **DDL generation**: reference/design/backend-redesign/design-docs/ddl-generation.md
- **Schema and query integration**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#schema-and-query-integration

The referenced design sections define the physical contract and provisioning behavior. This
story is only the work package for implementing them.

## Outcome

Deliver provider-equivalent cache, durable projection-work, lifecycle/safety state, and
transactional enqueue schema consumed by DocumentCache runtime and CDC work.

## Dependencies

- Depends on E02's DDL/provisioning infrastructure and E10's representation stamps.
- Unblocks integrated durable-state validation in 18-01, E18 stories 18-02 through 18-08,
  and the E19 database-source work.

## Implementation Scope

- Update fixed core definitions and both provider DDL emitters for the owned data-model
  sections. Keep fixed `dms` inventory outside `DerivedRelationalModelSet` and keep
  `RelationalMappingVersion` aligned to the current DMS-owned mapping constant.
- Add always-provisioned `DataStoreIdentity`, `DocumentCache`,
  `DocumentProjectionWork`, and `DocumentCacheState` objects, their owned constraints and
  access paths, and insert-if-absent singleton initialization.
- Emit cache UUID-validation programmable objects, two PostgreSQL statement enqueue
  triggers/functions, and one SQL Server set-based enqueue trigger with
  provider-equivalent least-privilege execution. SQL Server `*_Stamp` triggers do not
  inspect the server-level nested-trigger setting.
- Remove the legacy `DocumentCache.Etag` column, cache UUID constraint/index, obsolete
  source-scan index, and any proposed `dms.Document(ContentVersion, DocumentId)` projector
  discovery index.
- Integrate the objects with create-only provisioning, DB-apply manifests, and
  introspection using the bounded provisioning guards below.
- Update unit, snapshot, provider-apply, and focused trigger-behavior fixtures.
- Update provisioning documentation to link to the owning design sections.

## Resolved Provisioning and Provider Scope

### Create-Only Provisioning

- Provisioning remains create-only. It does not implement an E18 schema-drift comparison,
  upgrade, migration, or object-by-object reconciliation framework.
- Phase-zero validation is limited to:
  - the existing `EffectiveSchema` hash and singleton checks;
  - completed-schema protection for the `DataStoreIdentity` and `DocumentCacheState`
    singleton rows;
  - rejection of the known legacy `DocumentCache.Etag`,
    `UX_DocumentCache_DocumentUuid`, and
    `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` artifacts; and
  - PostgreSQL enqueue-owner role and provisioning-membership safety prerequisites.
- On a completed same-hash database, provisioning fails rather than recreating a missing
  `DataStoreIdentity` or `DocumentCacheState` singleton, replacing `SourceIdentity`, or
  resetting projection lifecycle or latch state. A stored all-zero `SourceIdentity` is
  invalid. No physical nonzero UUID constraint or identity-rotation command is added.
- Otherwise, fresh, partial, and same-hash execution use the existing existence-check and
  replaceable-programmable-object patterns. The story does not classify every possible
  partial database shape or promise stable drift diagnostics. Incompatible existing
  objects may fail through ordinary provider DDL execution and transaction rollback.
- `ddl provision` retains its existing rejection of an `EffectiveSchema` table without
  its singleton and executes the generated batches in one transaction. Standalone emitted
  SQL remains free of a built-in transaction wrapper; its caller owns any desired
  all-or-nothing wrapper.
- Reruns preserve `SourceIdentity`, projection lifecycle, the cache-ahead latch, cache
  rows, pending work, and enqueue timestamps. They may refresh replaceable generated
  functions and triggers through existing provider patterns.

### Provider Execution Security

- Production continues to use one deployment-supplied DMS data-store credential for
  canonical writes, projection, and projection administration. This story adds no runtime
  connection strings, role switching, or production capability grant matrix. Test-only
  restricted principals prove trigger encapsulation.
- PostgreSQL creates or safely reuses the cluster-wide `NOLOGIN`
  `edfi_dms_enqueue_owner`. It must remain a locked-down, non-inheriting,
  non-superuser role with no database/role creation, replication, bypass-RLS, or outgoing
  privilege-bearing membership. Generated DDL gives it only the database-local schema,
  state-read, and work-table privileges needed by the enqueue functions.
- PostgreSQL identifies `SESSION_USER` as the authenticated provisioning principal and
  grants only the direct `SET TRUE, INHERIT FALSE, ADMIN FALSE` membership needed to own
  and refresh the enqueue functions. Generated DDL grants no membership to DMS or CDC
  runtime principals and does not audit unrelated incoming administrative memberships.
- PostgreSQL enqueue functions are `SECURITY DEFINER`, owned by
  `edfi_dms_enqueue_owner`, use a `pg_catalog`-only `search_path`, and schema-qualify DMS
  references. `TF_DocumentCache_ValidateDocumentUuid` remains `SECURITY INVOKER` under
  existing schema ownership.
- SQL Server uses the existing same-owner ownership chain for the enqueue trigger and its
  referenced `dms` tables. It adds no `EXECUTE AS` clause, enqueue user, or enqueue role.
- E19 owns the separate CDC principal, capture objects, capture exclusions, and CDC-reader
  grants. E18 ordinary DDL emits none of them and grants no CDC access to
  `DocumentProjectionWork`.

### Transactional Enqueue Evidence Boundary

- Each provider captures one database UTC timestamp per triggering statement. New work
  uses it for both enqueue timestamps; advancing work preserves `FirstEnqueuedAt` and
  updates `LastEnqueuedAt`; non-advancing work receives no timestamp DML.
- Direct multi-row `dms.Document` insert/update fixtures cover every lifecycle state,
  coalescing, unchanged-version behavior, missing-state failure, rollback, delete cascade,
  and restricted-writer access. One representative generated resource-stamp path per
  provider proves that stamping reaches enqueue, including SQL Server nested-trigger
  behavior.
- Full HTTP/API, descriptor-specific, cross-feature, CDC-capture, and bulk-restamp utility
  scenarios remain assigned to 18-07, 18-08, or E19.
- Enqueue errors need a clear lifecycle diagnostic and complete transaction rollback, but
  provider-specific SQLSTATE or SQL Server error numbers are not a public contract.

## Acceptance Evidence

- Provider DDL snapshots and introspection tests cover the complete physical inventory
  assigned to this story.
- PostgreSQL and SQL Server DB-apply tests cover fresh provisioning, bounded phase-zero
  guards, same-hash rerun preservation, constraints, and trigger behavior.
- Lifecycle constraint tests reject casing variants, leading/trailing whitespace, empty
  values, and unknown values under representative SQL Server database collations as well
  as PostgreSQL.
- Multi-row insert/update and representative generated-stamp fixtures cover every
  lifecycle state. Forced enqueue errors roll back the complete canonical transaction;
  disabled lifecycle records no work; enqueue-enabled states coalesce current
  requirements; and unchanged `ContentVersion` updates leave the work row and both enqueue
  timestamps unchanged.
- Missing-singleton fixtures prove the enqueue trigger fails the complete canonical
  transaction rather than treating an absent `StateId = 1` row as `Disabled`.
- Access tests use test-only restricted canonical-writer principals to prove ordinary
  `dms.Document` DML enqueues through triggers without direct work-table permission.
  Production DDL creates no separate identity or grant matrix for canonical writes,
  projection, or projection administration, and emits no CDC capture object or work-table
  access.
- The test and documentation changes identify the design sections they verify rather than
  reproducing their tables or rules.

## Not Assigned to This Story

- Runtime projection, lifecycle administration, target eligibility, complete runtime
  inventory validation, and reads are assigned to later E18 stories.
- Provider capture objects, connectors, topics, CDC eligibility, and message shaping are
  assigned to E19.
