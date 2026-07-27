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
- **Schema and query integration**: reference/design/cdc-streaming.md#schema-and-query-integration

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

- Update the derived relational model and both provider DDL emitters for the owned data
  model sections.
- Add `DocumentProjectionWork`, its oldest-work index, constrained
  `Disabled`/`Resetting`/`Rebuilding`/`Tracking` lifecycle, and the orthogonal
  `CacheAheadRecoveryRequired` latch.
- Emit two PostgreSQL statement enqueue triggers/functions and one SQL Server set-based
  enqueue trigger, with provider-equivalent least-privilege execution. SQL Server
  `*_Stamp` triggers do not inspect the server-level nested-trigger setting.
  Reassess/remove the source-scan index.
- Integrate the objects with create-only provisioning, DB-apply manifests, and
  introspection.
- Update unit, snapshot, and provider-apply fixtures.
- Update provisioning documentation to link to the owning design sections.

## Acceptance Evidence

- Provider DDL snapshots and introspection tests cover the complete physical inventory
  assigned to this story.
- PostgreSQL and SQL Server DB-apply tests cover provisioning, rerun, constraint, and
  trigger behavior from the design references.
- Lifecycle constraint tests reject casing variants, leading/trailing whitespace, and
  unknown values under representative SQL Server database collations as well as
  PostgreSQL.
- Multi-row insert/update/stamp/restamp fixtures cover every lifecycle state. Forced
  enqueue errors roll back the complete canonical transaction; disabled lifecycle records
  no work; enqueue-enabled states coalesce current requirements; and unchanged
  `ContentVersion` updates leave the work row and both enqueue timestamps unchanged.
- Missing-singleton fixtures prove the enqueue trigger fails the complete canonical
  transaction rather than treating an absent `StateId = 1` row as `Disabled`.
- Access tests prove canonical writers enqueue through triggers but cannot directly mutate
  work, projector writers can acknowledge, the administrative context can perform only
  the owned lifecycle/baseline/repair DML, CDC principals cannot capture work, and reruns
  preserve lifecycle, latch, cache, and pending work.
- The test and documentation changes identify the design sections they verify rather than
  reproducing their tables or rules here.

## Not Assigned to This Story

- Runtime projection and reads are assigned to later E18 stories.
- Provider capture objects, connectors, topics, and message shaping are assigned to E19.

## Clarifying Questions and Answers

### Questions 1

1. What is the production database-principal model for the canonical writer, projector,
   projection administrator, CDC reader, and PostgreSQL non-login enqueue owner: separate
   credentials, per-connection role switching, or another mechanism? Specify the stable
   role/user names, whether `ddl emit`/`ddl provision` creates them or receives existing
   names, and which later story wires each runtime context.
2. Which SQL Server enqueue-trigger execution model is normative: same-owner ownership
   chaining or a narrowly scoped `EXECUTE AS` principal? What exact owner/user and
   ownership/grant inventory must deterministic snapshots, manifests, and introspection
   assert?
3. Should the final desired inventory remove the existing
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` index and prohibit any
   `dms.Document(ContentVersion, DocumentId)` projector-discovery index, leaving queue
   discovery solely on
   `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`?
4. The general DDL design permits existence-check recovery from partial runs, while the
   CDC integration design says legacy or missing E18 inventory is ineligible and must not
   be repaired in place. For both standalone emitted SQL and `ddl provision`, which
   missing or mismatched E18 states must fail before mutation, and which, if any, may be
   completed as a recoverable initial partial apply?
5. Does this story's requirement that CDC principals cannot capture work mean only that
   ordinary E18 DDL emits no work-table access for such principals, with publication/CDC
   capture exclusion owned and tested by 19-01, or must 18-00 add provider-capture
   prevention and tests despite the stated E19 boundary?
6. Do enqueue failures need stable provider-specific identifiers, such as a PostgreSQL
   SQLSTATE and SQL Server error number, for missing/unreadable lifecycle state and other
   enqueue failures, or is generic statement failure plus rollback sufficient until a
   later diagnostics story?

### Answers 1

1. Use stable, DDL-created non-login authorization roles: `edfi_dms_writer`,
   `edfi_dms_projector`, `edfi_dms_projection_admin`, `edfi_dms_cdc_reader`, and
   PostgreSQL-only `edfi_dms_enqueue_owner`. `ddl emit` and `ddl provision` create these
   roles and grants deterministically but never create login credentials or passwords;
   deployment automation maps its login users to them.

   The existing CMS connection remains the single DMS credential. DMS assumes the writer,
   projector, or administration role for each physical connection/session—PostgreSQL using
   non-inherited role membership and `SET ROLE`/`SET LOCAL ROLE`, and SQL Server using
   equivalent no-login execution users with `EXECUTE AS USER`/`REVERT`. The base login must
   not own the schema or hold `superuser`/`db_owner` privileges, and a pooled connection
   must never be returned while impersonating a role.

   The CDC connector uses a separate deployment-owned credential mapped only to
   `edfi_dms_cdc_reader`. The PostgreSQL enqueue owner is never assumable by either runtime
   credential and is reachable only through the hardened `SECURITY DEFINER` functions.

   Story 18-00 creates and verifies the roles, ownership, and grants. The existing
   relational path uses the writer context; 18-03/18-04 wire the projector context; 18-04
   wires administration; 18-08 reuses it; and 19-01 maps the separate CDC credential.
2. Use SQL Server same-owner ownership chaining. Create the `dms` schema with
   `AUTHORIZATION dbo`; `dms.Document`, `dms.DocumentCacheState`,
   `dms.DocumentProjectionWork`, and `TR_Document_EnqueueProjectionWork` inherit that
   schema owner without object-specific ownership overrides. The trigger omits
   `EXECUTE AS`, retains the caller context, and uses only static, schema-qualified
   references to those same-database objects. Do not create a SQL Server enqueue user;
   `edfi_dms_enqueue_owner` remains PostgreSQL-only.

   Grant `edfi_dms_writer` its ordinary canonical DML but no direct
   `DocumentProjectionWork` DML or `DocumentCacheState` read. Grant `PUBLIC` no access to
   either projection-internal table. The separately assumed `edfi_dms_projector` and
   `edfi_dms_projection_admin` users receive only their Answer 1.1 grants; their
   permissions are not part of the enqueue trigger's execution chain.

   Snapshots, manifests, introspection, and rerun validation assert the `dbo` schema
   owner, inherited object ownership, absence of `EXECUTE AS`, and absence of forbidden
   writer/`PUBLIC` grants. A different schema owner, an object-specific ownership
   override, an `EXECUTE AS` clause, or a forbidden grant is drift and fails without
   repair. Access tests prove an assumed `edfi_dms_writer` can enqueue through ordinary
   `dms.Document` DML but cannot directly read lifecycle state or mutate projection work.
3. Yes. Remove `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` from the desired
   model, emitted DDL, snapshots, manifests, and introspection expectations. Do not emit a
   `dms.Document(ContentVersion, DocumentId)` index. Ordinary discovery and oldest-work
   observation use
   `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`; baseline, rebuild, and scrub
   scan `dms.Document` in primary-key `DocumentId` order.
4. Standalone emitted SQL may complete an exact-compatible initial partial apply through
   its existence checks, including inserting a missing `EffectiveSchema` singleton, and
   must do so in the create-only transaction so failure leaves no durable mutation.
   `ddl provision` is stricter: if `dms.EffectiveSchema` exists without its singleton, it
   fails preflight and directs the operator to drop and recreate the database. It may
   proceed when the table is absent for a new/empty initial apply, or when the singleton
   exists with the expected hash for an exact rerun. Once that singleton exists, either
   path must validate and preserve the complete current E18 inventory; a missing E18
   object is drift, not a recoverable partial apply. A different hash, legacy `Etag`,
   obsolete cache UUID constraint, obsolete
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` index, missing or mismatched
   DDL-owned role/user or role attribute, mismatched schema/function/trigger ownership,
   forbidden `EXECUTE AS`, mismatched object/constraint/grant/index shape, or mismatched
   deterministic seed must fail without a durable mutation rather than be altered or
   repaired. CDC admission remains limited to E19's proven new-physical-database initial
   workflow even if a standalone script was technically able to complete an earlier
   partial apply.
5. Keep the provider-capture boundary in 19-01. Ordinary 18-00 DDL must grant CDC
   principals no `DocumentProjectionWork` access and must not create a publication or SQL
   Server capture instance for the work table. 18-00 access tests assert the absence of
   work-table grants; 19-01 owns publication/capture exclusion, provider-metadata
   validation, and proof that work DML produces no captured record.
6. Do not define dedicated DocumentCache enqueue-error identifiers in v1. Generic
   statement failure plus complete rollback is sufficient. For a missing `StateId = 1`
   row or invalid lifecycle value, emit a clear deterministic error using the existing
   provider conventions: PostgreSQL `RAISE EXCEPTION` with its default `P0001` SQLSTATE
   and SQL Server `THROW 50000, ..., 1`. An unreadable row and other permission,
   constraint, storage, deadlock, serialization, or lock-timeout failures retain their
   native provider errors; do not catch, wrap, or renumber them.

   Tests assert the explicit lifecycle diagnostic and complete canonical rollback for
   forced enqueue failures, but define no new enqueue-error taxonomy. Story 18-06 owns
   target-scoped logging and metrics and may add provider-independent classification if a
   concrete consumer requires it.
