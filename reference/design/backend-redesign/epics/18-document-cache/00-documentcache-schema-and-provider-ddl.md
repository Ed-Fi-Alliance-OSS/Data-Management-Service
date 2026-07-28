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
- Access tests use test-only restricted canonical-writer principals to prove ordinary
  `dms.Document` DML enqueues through triggers without direct work-table permission.
  Production DDL creates no separate identity or grant matrix for canonical writes,
  projection, or projection administration, emits no CDC capture object or work-table
  access, and reruns preserve lifecycle, latch, cache, and pending work.
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

### Questions 3

1. On a database whose `dms.EffectiveSchema` singleton already records the expected hash,
   should a missing `dms.DataStoreIdentity` or `dms.DocumentCacheState` singleton row fail
   the E18 preflight rather than be recreated? The simplest safe rule is to limit
   insert-if-absent initialization to an initial apply before the expected hash is
   recorded; recreating either row on a completed database could silently replace source
   identity or projection safety state.
2. The current `ddl emit` artifacts have no explicit transaction wrapper, while
   `ddl provision` executes them in one transaction. Should 18-00 keep that artifact shape
   and satisfy the standalone completed-schema rule with the focused phase-zero E18
   preflight, leaving all-or-nothing execution of emitted SQL to the caller, rather than
   introducing a cross-cutting transaction-wrapper change in this story?
3. Should each provider enqueue trigger capture one database UTC timestamp per triggering
   statement, use that value for both enqueue timestamps on a new work row and for every
   requirement advanced by that statement, and leave both timestamps unchanged when no
   required version advances? This is the smallest deterministic, provider-equivalent
   timestamp contract for the required multi-row tests.

### Answers 3

1. Yes. Treat an `EffectiveSchema` singleton containing the expected hash as the
   completion marker. The focused phase-zero E18 preflight must then require both
   singleton rows and fail before mutation with the existing drop-and-recreate guidance
   when either is absent. It must never generate a replacement `SourceIdentity` or reset
   projection lifecycle or latch state on a completed database. Insert-if-absent
   initialization remains available only after the database has been classified as an
   eligible initial or partial apply under the existing rules, before the expected hash
   is recorded.
2. Yes. Keep emitted SQL free of a built-in transaction wrapper. Put the read-only,
   completed-schema E18 inventory checks in phase zero before any schema, role, function,
   table, trigger, grant, or seed mutation. `ddl provision` continues to execute the
   generated artifact in its existing single transaction; a standalone `psql` or
   `sqlcmd` caller owns any desired all-or-nothing wrapper. Do not broaden 18-00 into a
   generic script transaction-policy change.
3. Yes. Capture the statement timestamp exactly once—PostgreSQL
   `statement_timestamp()` in a local `timestamp with time zone` value and SQL Server
   `SYSUTCDATETIME()` in a local `datetime2(7)` value. Every new work row from that
   statement uses it for both `FirstEnqueuedAt` and `LastEnqueuedAt`; every existing work
   row whose required version advances preserves `FirstEnqueuedAt` and uses it for
   `LastEnqueuedAt`. A row whose requirement does not advance receives no timestamp DML.
   Thus all new or advanced requirements from one triggering statement share one
   provider-generated UTC instant, while a no-op statement preserves both timestamps.

### Questions 4

1. To avoid requiring PostgreSQL superuser provisioning, should the provisioning principal
   alone receive `SET TRUE, INHERIT FALSE` membership in
   `edfi_dms_enqueue_owner`, while runtime principals receive none, with provisioning
   failing before schema mutation when that capability is unavailable?
2. Keeping the existing distinction that `ddl provision` rejects an `EffectiveSchema`
   table without its singleton while standalone SQL may resume an eligible partial apply,
   should phase zero require existing `Document`, `DocumentCache`, and
   `DocumentProjectionWork` tables to be empty, state to be absent or `Disabled` with a
   clear latch, and an existing valid `DataStoreIdentity` to be preserved? This
   classification does not establish CDC eligibility.
3. On a completed same-hash rerun, may only the definitions of already-present generated
   programmable objects be refreshed, without comparing body text, while object identity,
   attachment/events, enabled state, and each object's explicitly defined security
   metadata must already match?

### Answers 4

1. Yes. Replace Answer 2.4's prohibition on `SET ROLE` access for the provisioning
   membership with one direct grant to the authenticated provisioning principal using
   `SET TRUE, INHERIT FALSE, ADMIN FALSE`. This supplies the narrow PostgreSQL capability
   needed to assign and refresh functions owned by the `NOLOGIN` role without conferring
   its privileges ambiently or allowing membership delegation. Generated DDL grants no
   membership to the DMS or CDC runtime principals and adds no runtime role switching.
   On a completed database, phase zero requires the owner role and provisioning
   membership to exact-match before mutation. An eligible initial or partial apply may
   establish them before database-local schema or data mutation. If the credential cannot
   create or exact-match the role and membership, assign ownership, or apply the required
   local grants, fail with a prerequisite diagnostic rather than requiring superuser or
   silently broadening a conflicting role.
2. Yes, for the initial/partial-apply classification only. Every already-present table
   must first have the compatible E18-owned shape; any present `dms.Document`,
   `dms.DocumentCache`, or `dms.DocumentProjectionWork` table must be empty. The
   `DocumentCacheState` table or singleton may be absent, but any present singleton must
   be exactly `StateId = 1`, `Disabled`, with a clear latch. `DataStoreIdentity` may be
   absent; when present it must contain exactly one valid singleton and its
   `SourceIdentity` must be preserved. Reject rather than clear nonempty tables, active or
   transitional lifecycle state, a set latch, or malformed singleton state. This does
   not change `ddl provision`'s rejection of an `EffectiveSchema` table without its
   singleton, does not impose emptiness on a completed same-hash rerun, and supplies no
   CDC new-database or binding evidence.
3. Yes. For a completed same-hash database, phase zero requires the complete structural
   inventory and every generated programmable object to exist with its expected logical
   identity/signature, table attachment, timing/events, enabled state, owner/execution
   mode, and explicitly defined security metadata. A missing object or metadata mismatch
   fails before mutation. When those checks pass, refresh the deterministic generated
   definition through the existing provider replace/alter pattern without comparing
   provider-normalized body text. Do not use the rerun to create objects, repair
   structure or security, or change singleton or table data. This avoids a general
   programmable-object diff framework while retaining the existing idempotent definition
   refresh.

### Additional Clarifications

1. The completed-schema phase-zero inventory check is limited to E18-owned fixed objects,
   the named legacy cache artifacts, and the required `dms.Document` columns, keys, and
   trigger attachment points consumed by E18. Unrelated core, resource-derived,
   authorization, and Change Query objects retain their existing provisioning behavior.
   This story does not add a database-wide drift validator.
2. Within that boundary, phase zero positively validates every required E18 object and its
   contract-relevant properties. A required object with an incompatible definition, an
   object that collides with a required E18 name, legacy `Etag`, the obsolete cache UUID
   constraint, or the obsolete source-scan index fails before mutation. Otherwise,
   additional columns, constraints, triggers, functions, and differently named unique or
   non-unique indexes are outside the E18 compatibility contract and are not classified
   or rejected merely because they exist. This avoids an incomplete policy for arbitrary
   operator customizations while preserving strict validation of the generated contract.
3. PostgreSQL generated DDL identifies `SESSION_USER` as the authenticated provisioning
   principal that receives the direct `edfi_dms_enqueue_owner` membership with
   `SET TRUE, INHERIT FALSE, ADMIN FALSE`. It does not require
   `CURRENT_USER = SESSION_USER`, so a deployment may use an ordinary role-switched
   administration session. Phase zero exact-matches the authenticated principal's direct
   membership on a completed database. E18 does not enumerate or reject other incoming
   administrative memberships in the cluster-wide owner role; managing those memberships
   is a deployment responsibility, and generated DDL grants no membership to DMS or CDC
   runtime principals. This clarifies Answers 2.4 and 4.1 without adding a cluster-wide
   role-graph auditor.
4. PostgreSQL `TF_DocumentCache_ValidateDocumentUuid` remains an ordinary
   `SECURITY INVOKER` function under the existing schema ownership. Only
   `TF_Document_EnqueueProjectionInsert` and
   `TF_Document_EnqueueProjectionUpdate` are hardened `SECURITY DEFINER` functions owned
   by `edfi_dms_enqueue_owner`. Focused catalog assertions prove that distinction.
5. A valid `dms.DataStoreIdentity.SourceIdentity` is nonzero; the all-zero UUID is invalid.
   Fresh-apply tests prove that database-generated values are nonzero, and completed-schema
   phase-zero validation rejects a zero stored value. This story adds no physical nonzero
   check constraint because the data-model contract already generates the value and
   permits only supported CDC rotation workflows to replace it.
