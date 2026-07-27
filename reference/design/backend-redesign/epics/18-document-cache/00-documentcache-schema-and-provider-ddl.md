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

1. Keep the existing single DMS data-store credential for canonical writes, projection,
   and projection administration. These are capabilities inside one trusted DMS process,
   not separate production database identities in v1; do not add connection strings or
   per-connection role switching. `ddl emit`/`ddl provision` creates only the
   PostgreSQL `NOLOGIN` `edfi_dms_enqueue_owner` required by the hardened
   `SECURITY DEFINER` enqueue functions. Deployment continues to supply the DMS login,
   and 19-01 owns the separate deployment-supplied CDC login and its grants. Later E18
   stories reuse the resolved DMS connection and do not add another principal model.
   Provider fixtures may use restricted test principals to prove trigger encapsulation,
   but this story must not turn those fixtures into production credential provisioning.
2. Use SQL Server's same-owner ownership chain. Keep the trigger and the three referenced
   `dms` tables under the existing `dms` schema owner, use only static schema-qualified
   references, and do not add `EXECUTE AS` or an enqueue user. Snapshots and introspection
   need to prove the trigger definition and absence of `EXECUTE AS`; functional access
   tests prove that a restricted writer can fire it without direct work-table DML.
   Deployment-specific owner names and a general owner/grant catalog do not belong in the
   manifest.
3. Yes. Remove
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` and do not add a
   `dms.Document(ContentVersion, DocumentId)` index. Ordinary discovery uses only
   `IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId`; explicit baseline, rebuild, and
   scrub operations scan `dms.Document` in primary-key order.
4. Follow the existing create-only provisioning contract rather than adding an E18 drift
   reconciler. Do not bump `RelationalMappingVersion` from `v2` to `v3`; `v2` was just
   introduced and is the correct mapping version for the next DMS release. A new apply or
   same-hash rerun may create missing objects and refresh
   replaceable generated functions/triggers using the existing existence-check patterns.
   An incompatible existing table, column, or constraint fails the transaction instead of
   being altered. `ddl provision` retains its existing preflight failures for a different
   schema hash or an existing `EffectiveSchema` table with no singleton; standalone SQL
   retains its insert-if-missing behavior. No broader object-by-object check must run
   before the first statement because the single transaction already guarantees no
   durable partial mutation. E19 separately requires new-database workflow provenance and
   a complete current E18 inventory before CDC setup; partial completion or a successful
   rerun does not by itself make an older database CDC-eligible.
5. The requirement is limited to the E18 boundary: grant no CDC principal access to
   `DocumentProjectionWork` and emit no capture object for it. Story 19-01 owns and tests
   publication/capture exclusion, CDC-reader grants, and provider metadata.
6. Generic provider failure plus complete rollback is sufficient. Give an absent or
   invalid lifecycle singleton a clear deterministic message, but do not make its
   PostgreSQL SQLSTATE or SQL Server error number a public contract. Let permission,
   constraint, deadlock, timeout, and storage errors retain their native identifiers;
   tests assert the useful lifecycle diagnostic and rollback, not a new error taxonomy.

### Questions 2

1. The physical-model and DDL designs require the always-provisioned
   `dms.DataStoreIdentity`, the current core emitter does not create it, and E19 consumes
   it, but this story does not name it explicitly. Should 18-00 add only that table,
   insert-if-missing singleton initialization, and rerun-preservation evidence, leaving
   source-identity rotation and CDC binding behavior to E19?
2. Does “update the derived relational model” require representing these fixed
   `dms` tables, constraints, indexes, and triggers in `DerivedRelationalModelSet`, or may
   18-00 follow the existing architecture and keep fixed core inventory in
   `CoreDdlEmitter`/shared core definitions while leaving the resource-derived model
   unchanged?
3. Answer 1 establishes one production DMS database credential, so the database cannot
   distinguish canonical-writer, projector, and projection-administrator capabilities.
   Should the access evidence therefore use test-only restricted principals solely to
   prove that a canonical writer can fire enqueue triggers without direct work-table DML,
   while later E18 stories enforce projector/administrator operations at application
   component boundaries, with no additional production roles or grant matrix in 18-00?
4. For the cluster-wide PostgreSQL `edfi_dms_enqueue_owner`, may emitted DDL create the
   `NOLOGIN` role when absent, reuse it when already present, grant only the required
   database-local state/work permissions, and fail clearly when the provisioning
   credential cannot create or assign that owner? Which existing-role conditions, beyond
   an unexpected `LOGIN` capability, must make provisioning fail rather than reuse it?
5. May the provisioned-schema manifest remain at its current version and shape, using its
   existing table/column/constraint/index/trigger/function inventory plus focused provider
   catalog assertions for lifecycle collation, PostgreSQL function owner/security/search
   path/grants, and SQL Server's absent `EXECUTE AS`? Or must this story expand the generic
   manifest to model those security details?
6. For same-hash reruns, may compatibility checking stay narrowly scoped to the E18-owned
   tables and known legacy cache artifacts—rejecting incompatible columns/constraints,
   legacy `Etag`, the obsolete cache UUID constraint, and the obsolete source-scan index;
   creating missing compatible objects; and refreshing replaceable functions/triggers—
   without introducing a general schema-drift comparison framework?
7. To avoid duplicating 18-07 and 18-08, should 18-00's provider-apply suite prove enqueue
   DDL behavior through direct multi-row `dms.Document` changes and a representative
   generated stamp path, while deferring full API-path, descriptor, bulk-restamp-utility,
   and cross-story scenarios to their owning later stories?

### Answers 2

1. Yes. Add `dms.DataStoreIdentity` as fixed core inventory, including its singleton
   constraint, database-generated UUID initialization when
   `DataStoreIdentitySingletonId = 1` is absent, and fresh-apply/rerun tests proving that
   the UUID is nonempty and preserved. For this table, 18-00 owns only physical
   provisioning and preservation. E19 owns reading the identity for a source fingerprint
   and every clone, restore, rotation, binding, and CDC recovery rule. Do not add an
   identity-rotation command or CDC binding behavior to 18-00.
2. Keep the existing architecture. Fixed `dms` inventory belongs in `CoreDdlEmitter` and
   focused shared core definitions; `DerivedRelationalModelSet` remains the
   effective-schema-derived resource model. Add a reusable core definition only where it
   prevents emitter, provisioning, and validation code from duplicating names or shape.
   Do not add fixed tables, constraints, indexes, or triggers to the model-set record or
   require every model builder and consumer to carry them.
3. Yes. The production DMS credential necessarily has the union of permissions needed by
   the canonical path and later projection/administration components, so 18-00 must not
   claim database-enforced separation among those uses. Use test-only restricted
   principals to prove the narrower trigger property: ordinary `dms.Document` DML can
   enqueue through the PostgreSQL security-definer functions or SQL Server ownership
   chain without direct work-table permission. Later E18 stories enforce projector and
   administrator capabilities through narrow application interfaces and tests. Do not
   add production roles, role switching, connection strings, or a generated runtime grant
   matrix.
4. Yes. Create `edfi_dms_enqueue_owner` when absent and otherwise reuse it only when it is
   a locked-down ownership role: `NOLOGIN`, `NOINHERIT`, `NOSUPERUSER`, `NOCREATEDB`,
   `NOCREATEROLE`, `NOREPLICATION`, and `NOBYPASSRLS`, with no privilege-bearing role
   memberships. An administrator-only membership used by the provisioning principal is
   acceptable only when it grants neither inherited nor `SET ROLE` access. Fail rather
   than alter an existing role with a different security attribute or effective
   membership. Apply and verify only the required grants in the current database; owning
   enqueue functions and holding the same narrow grants in other DMS databases is expected
   for this cluster-wide role and does not require a cluster-wide privilege auditor.
   Fail with a clear prerequisite diagnostic when the provisioning credential cannot
   create the role, assign function ownership, or apply the local grants.
5. Keep `ProvisionedSchemaManifest` at its current version and shape. Its existing
   structural inventory should naturally include the new tables, columns, constraints,
   index, triggers, and functions. Use focused provider catalog assertions for the SQL
   Server lifecycle collation, PostgreSQL function owner/security/search path and grants,
   SQL Server's absent `EXECUTE AS`, and preservation of the mutable singleton rows.
   These provider-specific security facts do not justify turning the generic structural
   manifest into a principal and permission model.
6. Do not introduce a general schema-drift comparison framework. Add a small preflight
   limited to E18's reserved inventory and known legacy cache artifacts. However, an
   existing expected-hash singleton marks a completed schema: both standalone SQL and
   `ddl provision` must reject any missing or incompatible required E18 object, legacy
   `Etag`, obsolete cache UUID constraint, or obsolete source-scan index before durable
   mutation. Missing compatible objects may be created only during an initial apply under
   the existing new/partial-apply rules; a complete same-hash rerun may refresh present
   replaceable functions and triggers and must preserve all mutable data. This clarifies
   Answer 1.4's permission to create missing objects; it does not permit repairing a
   database that already records the expected hash.
7. Yes. Exercise the complete lifecycle, coalescing, timestamp-preservation, missing-state,
   rollback, and delete-cascade matrix with direct set-based `dms.Document` changes, then
   add one representative generated resource-stamp path per provider to prove that the
   stamp reaches the enqueue mechanism, including SQL Server nested-trigger behavior.
   Defer full HTTP/API paths, descriptor-specific and cross-feature coverage to 18-07, and
   the bulk restamp command and resumability scenarios to 18-08.
