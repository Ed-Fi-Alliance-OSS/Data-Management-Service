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

### Questions 2

1. Specify the exact provider-specific grant matrix for `edfi_dms_writer`,
   `edfi_dms_projector`, `edfi_dms_projection_admin`, and `edfi_dms_cdc_reader`, including
   principal type/attributes, schema usage, canonical/resource table and sequence access,
   cache/state/work permissions, function execution, and `SET ROLE`/`IMPERSONATE`
   capability. Does 18-00 leave `edfi_dms_cdc_reader` without source/cache grants for
   19-01 to add, or grant its non-work access now?
2. What is the complete PostgreSQL ownership and rerun contract: which stable principal
   owns the `dms` schema, tables, triggers, and non-enqueue functions; which objects are
   owned by `edfi_dms_enqueue_owner`; and how must provisioning handle cluster-wide roles
   that already exist because another database created them? State the required
   provisioning privileges and the role attributes/ownership differences that count as
   drift.
3. Answer 1.4 requires an existing current-schema database with any E18 drift to fail
   without repair, while the general emitters use `CREATE OR REPLACE`, `CREATE OR ALTER`,
   and drop/recreate trigger patterns. What exact preflight/apply ordering and comparison
   mechanism must standalone emitted SQL and `ddl provision` use so programmable-object,
   owner, role, and grant drift cannot be overwritten before it is detected?
4. What exact additions and versioning are required for the provisioned-schema manifest
   and introspection model to prove the E18 inventory? Specify whether it must record
   role attributes, grants, schema/object/function owners, function security/search path,
   trigger enabled state and execution context, column collation, check expressions, and
   FK delete actions, and whether mutable `DocumentCacheState` values belong in seed data
   or are verified only by fresh-apply/rerun tests.

### Answers 2

1. Use the following generated permission sets. On PostgreSQL the four runtime principals
   are `NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
   NOBYPASSRLS` roles. On SQL Server they are database users `WITHOUT LOGIN`, with
   `DEFAULT_SCHEMA = dms`, no database-role membership other than `public`, and no
   ownership. SQL Server has no schema `USAGE` permission, so emit the corresponding
   object/column grants rather than schema-wide `CONTROL`, `ALTER`, or ownership.

   - `edfi_dms_writer` receives PostgreSQL `USAGE` on `dms`, `auth`, every generated
     project schema, and every generated tracked-change schema. On both providers it may
     `SELECT` `ResourceKey`, `DataStoreIdentity`, `EffectiveSchema`, `SchemaComponent`,
     `Document`, `ReferentialIdentity`, and `Descriptor`; every generated concrete root,
     collection/nested-collection, common-type extension, and `_ext` table; every
     generated abstract-identity table and union view; the generated authorization
     hierarchy table and baseline authorization views; and every generated tracked-change
     table and ReadChanges authorization view. It receives `INSERT`/`UPDATE`/`DELETE` on
     `Document`, `ReferentialIdentity`, `Descriptor`, all concrete/collection/extension
     tables, and the generated abstract-identity, authorization-hierarchy, and
     tracked-change tables maintained by supported write and trigger paths. It receives
     PostgreSQL `USAGE, SELECT` or SQL Server `UPDATE` on
     `ChangeVersionSequence` and `CollectionItemIdSequence`. It receives PostgreSQL
     `EXECUTE` or SQL Server scalar-function `SELECT` only for the application-callable
     `GetMaxChangeVersion` and `uuidv5` helpers, plus PostgreSQL `throw_error`. On SQL
     Server it receives `EXECUTE, REFERENCES` on `BigIntTable` and
     `UniqueIdentifierTable`.
     It receives no permission on `DocumentCache`, `DocumentCacheState`, or
     `DocumentProjectionWork`; enqueue occurs only through canonical `Document` DML.
   - `edfi_dms_projector` receives PostgreSQL `USAGE` on `dms` and every project schema
     and provider-equivalent `SELECT` on `ResourceKey`, `DataStoreIdentity`,
     `EffectiveSchema`, `SchemaComponent`, `Document`, `Descriptor`, and every generated
     concrete root, collection/nested-collection, common-type extension, and `_ext` table
     used by compiled hydration. It receives no access to `ReferentialIdentity`,
     abstract-identity tables or union views, authorization tables/views, tracked-change
     tables/views, or canonical sequences. It receives `SELECT`, `INSERT`, and `UPDATE`
     on `DocumentCache`; `SELECT` and `DELETE` on `DocumentProjectionWork`; `SELECT` on
     `DocumentCacheState`; and column-level `UPDATE` only on
     `DocumentCacheState.CacheAheadRecoveryRequired`. It receives no canonical-table DML,
     sequence permission, lifecycle-column update, work insert/update, cache delete, or
     ordinary- or trigger-function execution grant. On SQL Server it receives
     `EXECUTE, REFERENCES` only on `BigIntTable`.
   - `edfi_dms_projection_admin` receives the projector's source-read and state-read
     grants explicitly, not through role membership; `SELECT`/`INSERT`/`UPDATE`/`DELETE`
     on `DocumentCache` and `DocumentProjectionWork`; and column-level `UPDATE` on both
     `DocumentCacheState.ProjectionLifecycleState` and
     `CacheAheadRecoveryRequired`. For 18-08 restamping it additionally receives
     column-level `UPDATE` on `Document.ContentVersion` and
     `Document.ContentLastModifiedAt` and on the mirrored `ContentVersion` and
     `ContentLastModifiedAt` columns of every concrete resource root and
     `dms.Descriptor`, plus PostgreSQL `USAGE, SELECT` or SQL Server `UPDATE` on
     `ChangeVersionSequence`. PostgreSQL guarded new-empty activation is exposed through
     one generated `dms.ActivateDocumentCacheOnEmptyDataStore()` `SECURITY DEFINER`
     function owned by `edfi_dms_owner`, with an exact configured `search_path` of
     `pg_catalog` and schema-qualified references. The function performs the complete
     Document lock, empty-table checks, singleton lock, and `Disabled -> Tracking`
     transition; grant `EXECUTE` only to `edfi_dms_projection_admin`. This avoids granting
     table-level `UPDATE` on `Document` merely to satisfy PostgreSQL `LOCK TABLE`. SQL
     Server uses the administrator's existing `SELECT` permission to take the
     provider-equivalent table lock in the guarded transaction. The administrator
     receives `EXECUTE, REFERENCES` on SQL Server `BigIntTable`, but no insert/delete on
     the state singleton, unrestricted canonical/resource DML, collection-id sequence
     permission, or `TRUNCATE`, `ALTER`, ownership, or trigger-function execution
     permission. Administrative clearing therefore uses the supported bounded `DELETE`
     paths.
   - 18-00 creates `edfi_dms_cdc_reader` but grants it no schema, table, sequence, or
     function access. Story 19-01 adds the provider-specific non-work access only when CDC
     is selected: source reads for `Document`, `DocumentCache`, and `CdcHeartbeat`,
     heartbeat update, and the provider capture/replication rights. It never grants
     `DocumentProjectionWork` access.

   PostgreSQL explicitly revokes `ALL` from `PUBLIC` on `dms`, `auth`, every generated
   project schema, and every generated tracked-change schema, and revokes `CREATE` from
   `PUBLIC` on the database's `public` schema. Revoke `PUBLIC` execution from every
   generated DMS function after creation, grant back only the calls above, and set
   `ALTER DEFAULT PRIVILEGES` for `edfi_dms_owner` and
   `edfi_dms_enqueue_owner` in their managed schemas to revoke `PUBLIC` function
   execution for later generated functions. No runtime principal receives direct
   execution on an enqueue, cache-UUID-validation, or other trigger function. SQL Server
   relies on the Answer 1.2 ownership chain and grants no `EXECUTE AS` target permission
   between managed users.

   No PostgreSQL managed role is a member of any other role; SQL Server managed users
   belong only to the implicit `public` role. The ordinary DMS deployment maps its login
   to the writer, projector, and administrator principals with non-inherited `SET`
   capability on PostgreSQL, or deployment-scoped `IMPERSONATE` on SQL Server. The
   separate connector login maps only to the CDC-reader principal. Those deployment-named
   mappings are not emitted by 18-00, and neither runtime login may assume an owner
   principal.
2. Add PostgreSQL-only `edfi_dms_owner` to the story's owned principal inventory; the
   story and Answer 1.1 should be updated accordingly before task creation. It has the
   same locked-down attributes as the runtime roles and is never granted to a DMS or CDC
   login. It owns `dms`, `auth`, generated project/tracked-change schemas, all generated
   tables, sequences, views, and ordinary functions, including
   `TF_DocumentCache_ValidateDocumentUuid`. PostgreSQL triggers have no independent owner;
   they are attached to tables owned by `edfi_dms_owner`.

   `edfi_dms_enqueue_owner` has the same locked-down attributes and owns only
   `TF_Document_EnqueueProjectionInsert` and
   `TF_Document_EnqueueProjectionUpdate`. Both functions are `SECURITY DEFINER` with an
   exact configured `search_path` of `pg_catalog` and schema-qualify every DMS reference.
   The owner receives `USAGE` on `dms`, `SELECT` on `DocumentCacheState`, and the
   `SELECT`/`INSERT`/`UPDATE` permissions on `DocumentProjectionWork` required by the
   coalescing upsert; it receives no delete, cache, canonical-source, schema-create, or
   runtime-role permission. `PUBLIC` and all runtime roles have no direct execution grant
   on those functions.

   PostgreSQL provisioning therefore requires a bootstrap principal with `CREATEROLE`,
   `CREATE` on the target database, and `ADMIN`/`SET` capability for all managed roles so
   it can create or reuse them and assign the required owners. A superuser satisfies this
   contract, but is not required. If cluster-wide roles already exist, provisioning
   accepts them only when their names and all security attributes above match; it neither
   drops nor alters them and then applies or validates only database-local ownership and
   grants.

   Drift validation is deliberately limited to the managed roles and the generated
   objects owned by this DDL in the target database. It fails on a managed-role attribute
   mismatch; membership of a managed role in any other role; the wrong owner for a
   generated schema, table, sequence, view, or function; an enqueue function owned by
   anyone other than `edfi_dms_enqueue_owner`; an ordinary generated object owned by the
   enqueue owner; or an effective managed-principal/`PUBLIC` permission that violates the
   generated matrix. Existing deployment-login mappings and unrelated roles, objects,
   owners, memberships, and grants are outside this comparison. If an existing managed
   role is compatible but the provisioning principal lacks the required `ADMIN`/`SET`
   capability, fail before database mutation with instructions to grant that capability
   or run with the deployment bootstrap principal.
3. Do not introduce a universal desired-inventory reconciler, and do not use the
   provisioned-schema manifest as provisioning input. Generate provider-specific E18
   preflight and postflight checks directly into the emitted SQL so standalone execution
   and `ddl provision` run the same authoritative checks inside the existing single
   create-only transaction. The command's current separate hash/seed preflight may remain
   as an earlier diagnostic, but it is not the E18 drift authority.

   The in-transaction preflight runs before any `CREATE OR REPLACE`, `CREATE OR ALTER`,
   trigger drop/create, owner change, `GRANT`, or `REVOKE`. It:

   1. classifies the database using `EffectiveSchema` and the expected hash/version under
      Answer 1.4's new/partial/rerun rules;
   2. rejects the legacy cache shape (`Etag`, the obsolete cache UUID constraint, and the
      obsolete source-scan index);
   3. validates any present E18-owned reserved object by qualified name, provider shape,
      programmable-object definition, and required owner;
   4. validates managed-principal attributes and memberships plus their effective
      permissions on generated objects; and
   5. rejects reserved security violations, including writer or `PUBLIC` access to
      cache/state/work, CDC-reader access to work, direct trigger-function execution, and
      any managed principal's ability to assume an owner principal.

   An initial apply may create missing E18 inventory after all present reserved items pass.
   An expected-hash rerun requires the complete E18 inventory to match and performs no
   repair of drift. After ordinary emission, a postflight block repeats the targeted
   completeness, owner, definition, and effective-permission checks before the completion
   records are written and the transaction commits. Any failure rolls back the complete
   transaction.

   These checks own only E18's reserved names, generated definitions, managed principals,
   required grants, and prohibitions. They ignore unrelated objects, custom authorization
   views, E19-owned CDC objects and non-work grants, operator-added indexes, deployment
   login mappings, and grants to unrelated principals unless an item collides with an E18
   reserved name or violates a reserved prohibition. This preserves the create-only
   contract without creating an extension-registration architecture.
4. Bump `ProvisionedSchemaManifest.ManifestVersion` from `"1"` to `"2"` and make version 2
   the post-apply verification shape used by this story. It remains snapshot/introspection
   evidence and is not a provisioning comparison engine. Update both provider goldens
   atomically; do not treat version 1 as sufficient E18 evidence. The introspector and
   deterministic emitter must add:

   - principals with provider type, login/inherit/superuser/create-role/create-database/
     replication/bypass-RLS attributes on PostgreSQL and authentication type/default
     schema on SQL Server;
   - normalized managed-role membership and generated SQL Server permission entries,
     while excluding deployment-named login mappings and their `IMPERSONATE` edges from
     golden equality;
   - owner on every schema, table, sequence, view, and function, plus whether SQL Server
     object ownership is inherited from the schema;
   - generated allow/deny grants for managed principals and `PUBLIC`, by grantee, object
     kind and qualified signature, optional column, permission, and grant option,
     including schema, table, sequence, function, type, and relevant impersonation
     permissions; unrelated principals and operator/deployment grants are not golden
     inventory;
   - column collation, including the effective binary collation on the SQL Server
     lifecycle column;
   - normalized check expressions, constraint enabled/validated/trusted state, and
     foreign-key update/delete actions;
   - trigger enabled state, events/timing, normalized definition, linked PostgreSQL
     function, and SQL Server execution context (`NULL` for the required caller context);
   - function signature, owner, language, normalized definition, security-invoker/
     definer flag, and normalized configured `search_path`.

   Existing table/column/index/function/trigger names and counts remain in the manifest,
   so these additions prove the full E18 inventory rather than only its security fields.
   Add the design-required `DataStoreIdentity` table and singleton initialization to the
   18-00 core emitter, manifest, introspection, snapshots, and provider-apply tests; it is
   part of this story's always-provisioned metadata and is not left as a grant to an
   otherwise absent table.

   Do not add the mutable `DocumentCacheState` lifecycle or latch values to
   `SeedData`: only deterministic `EffectiveSchema`, `SchemaComponent`, and `ResourceKey`
   data belongs there. Fresh-apply tests assert the initial `Disabled`/clear row, current
   preflight asserts that exactly `StateId = 1` exists with values admitted by the
   constraints, and rerun tests mutate lifecycle/latch/cache/work first and prove they are
   preserved.

   The manifest-format bump does not itself change `EffectiveSchemaHash`, but E18's
   replacement of the legacy cache contract and addition of correctness-critical tables
   and triggers is a breaking relational mapping change. Bump
   `SchemaHashConstants.RelationalMappingVersion` from `"v2"` to `"v3"` and update the
   corresponding packaged-schema metadata so a pre-E18 database fails the ordinary hash
   preflight. The targeted E18 checks above remain necessary for same-version tampering
   and partial-apply detection.
