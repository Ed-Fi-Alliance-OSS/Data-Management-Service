# Data Model and DDL

## Design Summary

The feature uses a redesign-aligned update-tracking and tracked-change-authorization model centered on the canonical `dms.Document` row, a required narrow live-change journal, and dedicated tracking tables for deletes and key changes.

Core required artifacts:

- `dms.ChangeVersionSequence`
- `dms.ResourceKey`
- `dms.Document.ResourceKeyId`
- `dms.Document.CreatedByOwnershipTokenId` when not already present through redesign-auth work
- `dms.Document.ChangeVersion`
- `dms.Document.IdentityVersion`
- `dms.DocumentChangeEvent`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- tracked-change authorization-basis columns on delete and key-change rows
- supporting indexes
- change-version, identity-version, and journal-maintenance logic

Deferred retention artifact for a later phase:

- `dms.ChangeQueryRetentionFloor` when purge is enabled

## Backend Scope Note

The canonical DMS-843 design is defined at the contract, artifact-responsibility, and behavioral level rather than as a PostgreSQL-only feature.

Normative rule:

- any relational backend that implements DMS-843 must provide semantically equivalent change-version allocation, live-row stamping, tombstone capture, key-change capture, indexing, and row-level write serialization

Informative note:

- the SQL blocks in this document and in `Appendix-A-Feature-DDL-Sketch.sql` use PostgreSQL syntax as one concrete example of a conforming backend implementation
- equivalent MSSQL DDL, trigger, locking, and backfill behavior is also required for the project's supported SQL Server path
- JSON payload and authorization-projection columns on tracking rows may use engine-appropriate JSON-capable storage, for example PostgreSQL `jsonb` or SQL Server `nvarchar(max)` carrying JSON text, because the normative requirement is faithful JSON-document storage and response materialization rather than a PostgreSQL-specific physical type
- those SQL blocks are examples of one conforming backend implementation, not a statement that the public feature contract is PostgreSQL-only

## Resource key lookup

To align with the backend-redesign journal model, the current backend must add a stable resource-key lookup used to filter live change-journal rows.

One conforming shape is:

```sql
CREATE TABLE dms.ResourceKey (
    ResourceKeyId smallint NOT NULL PRIMARY KEY,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    CONSTRAINT UX_ResourceKey_ProjectName_ResourceName
        UNIQUE (ProjectName, ResourceName)
);
```

Required semantics:

- `ResourceKeyId` is the narrow filter key used by `dms.DocumentChangeEvent`
- the mapping for a deployed effective schema must be stable
- seed rows must be provisioned from the deployed effective schema inventory rather than inferred from the current contents of `dms.Document`
- resources with zero current live rows still require `dms.ResourceKey` rows so the deployed effective schema has a complete stable mapping
- current implementations may also persist `ResourceVersion` on `dms.ResourceKey` as a compatibility or diagnostic copy tied to the deployed effective schema seed
- provisioning or startup validation must fail fast if the effective resource inventory exceeds the chosen key-space bound

## Canonical Live Row

The current source of truth for resources and descriptors is `dms.Document`.

Relevant existing columns include:

- `DocumentPartitionKey`
- `Id`
- `DocumentUuid`
- `ProjectName`
- `ResourceName`
- `ResourceVersion`
- `IsDescriptor`
- `EdfiDoc`
- `SecurityElements`
- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`
- `CreatedAt`
- `LastModifiedAt`
- `LastModifiedTraceId`

The feature extends this table with:

- `ResourceKeyId`
- `CreatedByOwnershipTokenId` if not already available on the live row from redesign-auth work
- `ChangeVersion`
- `IdentityVersion`

## Global Sequence

Create one global sequence:

```sql
CREATE SEQUENCE dms.ChangeVersionSequence AS bigint START WITH 1 INCREMENT BY 1;
```

Required semantics:

- global monotonic ordering across the database
- values may contain gaps
- rollbacks may consume values
- clients treat values as ordering tokens rather than row counts
- value `0` matches the ODS-output-compatible bootstrap watermark exposed by `availableChangeVersions` when no change versions have been allocated; legacy ODS also returns `0` on an empty instance because `MAX(ChangeVersion)` over empty tables is `NULL`, serialized as `0`
- the sequence is both the stamp-allocation mechanism and the public source for the ODS-compatible `newestChangeVersion` ceiling (`next value - 1` on the selected source)
- `oldestChangeVersion` remains replay-floor metadata (or bootstrap `0` when no replay-floor metadata exists)

Dialect-specific ceiling computation:

- PostgreSQL: query sequence state without consuming a value; the standard read is `SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM dms."ChangeVersionSequence"` so the pre-first-allocation state normalizes to `0` instead of exposing the engine's raw start value
- SQL Server: `NEXT VALUE FOR` is DDL-level and cannot be called in a read-only context; use `SELECT current_value FROM sys.sequences WHERE object_id = OBJECT_ID('dms.ChangeVersionSequence')` to read the ceiling without consuming a value; `current_value` is `NULL` before the first allocation, which must be normalized to `0` for the bootstrap response
- both engines must normalize a pre-allocation state to `0` rather than returning a raw engine-specific initial sequence descriptor value

## `dms.Document.ResourceKeyId`

Add the redesign-aligned live-row resource key:

```sql
ALTER TABLE dms.Document
    ADD COLUMN ResourceKeyId smallint;
```

Required semantics:

- `ResourceKeyId` references `dms.ResourceKey`
- backfill derives `ResourceKeyId` from the row's current `(ProjectName, ResourceName)`
- current-backend implementations may retain `ProjectName`, `ResourceName`, `ResourceVersion`, and `IsDescriptor` as compatibility or diagnostic copies, but `ResourceKeyId` becomes the required filter key for the live change journal
- changed-resource candidate selection must key off `ResourceKeyId`, not off wide journal copies of resource identity text

## `dms.Document.CreatedByOwnershipTokenId`

To align tracked changes to redesign ownership-based authorization, the live row must expose `CreatedByOwnershipTokenId`.

If another redesign-auth workstream has not already added this column, DMS-843 must add it as a bridge prerequisite.

One conforming shape is:

```sql
ALTER TABLE dms.Document
    ADD COLUMN CreatedByOwnershipTokenId smallint;
```

Required semantics:

- the live row stores the ownership stamp used by redesign authorization concepts
- tombstones and key-change rows copy the live row's value so tracked-change authorization can evaluate ownership after deletes or later key changes
- if historical rows cannot be retroactively assigned an ownership token, null values remain valid persisted state and carry the same authorization implications they do in the redesign authorization model
- under ownership-based authorization, a tracked row with `CreatedByOwnershipTokenId = null` does not bypass ownership filtering; it remains ownership-uninitialized state and is not returned through that strategy

Implementation note:

- this package treats tracked-change ownership filtering as an accepted DMS-specific authorization exception justified by redesign [`auth.md`](../design/backend-redesign/design-docs/auth.md), [`data-model.md`](../design/backend-redesign/design-docs/data-model.md), [`transactions-and-concurrency.md`](../design/backend-redesign/design-docs/transactions-and-concurrency.md), and [`auth-redesign-subject-edorg-model.md`](../design/auth/auth-redesign-subject-edorg-model.md)
- preserving `CreatedByOwnershipTokenId` on the live row and on tracked rows is therefore part of the approved DMS-843 contract, not an optional extension

## `dms.Document.ChangeVersion`

Add a live-row change token:

```sql
ALTER TABLE dms.Document
    ADD COLUMN ChangeVersion bigint;
```

Final state after backfill:

- non-null for all rows
- defaulted from `dms.ChangeVersionSequence` for new inserts
- unique per committed representation change for a single document row

For redesign alignment, `dms.Document.ChangeVersion` is the current-backend equivalent of redesign `dms.Document.ContentVersion`.

This bridge design adds one physical live-row representation-change stamp column: `dms.Document.ChangeVersion`.

It does not also add `dms.Document.ContentVersion` to the current-backend schema. In this package, references to `ContentVersion` are redesign cross-reference terminology for the same stamp responsibility.

Bridge migration requirement:

- redesign-aligned migrations must map this existing live-row stamp to redesign `ContentVersion` semantics without introducing a second simultaneously active representation-stamp column
- migrations must fail fast when both `ChangeVersion` and `ContentVersion` appear as active live-row representation stamps for the same deployed schema stage

## `dms.Document.IdentityVersion`

Add a distinct live-row identity-change stamp:

```sql
ALTER TABLE dms.Document
    ADD COLUMN IdentityVersion bigint;
```

Final state after backfill:

- non-null for all rows
- defaulted from `dms.ChangeVersionSequence` for new inserts
- updated only when the document's identity tuple changes

For redesign alignment, `dms.Document.IdentityVersion` is the current-backend equivalent of redesign `dms.Document.IdentityVersion`.

This column is not a public Change Query response field, but it is required so the current-backend bridge model matches redesign identity-tracking responsibilities.

Mutual-exclusivity rule for `IdentityVersion` initialization vs. update-time binding:

- database insert trigger or column default is the sole owner of `IdentityVersion` assignment for newly inserted rows
- the application write path is the sole owner of `IdentityVersion` advancement for committed identity-changing updates on already-existing rows
- these two mechanisms apply to disjoint events (insert vs. identity-changing update) and must never compete; no insert trigger may fire an additional `IdentityVersion` allocation during an identity-changing update, and no update write path may skip the mandatory application-side allocation on insert

## Content and Identity Stamping Rules

`ChangeVersion` must change when the served representation changes.

For the current backend, this includes:

- insertion of a new `dms.Document` row
- direct updates to `dms.Document.EdfiDoc`
- representation rewrites caused by identity-update cascades that rewrite `EdfiDoc`

`ChangeVersion` must not change when only authorization-maintenance columns or other non-representation metadata are updated.

`IdentityVersion` must change when the document's natural-key identity tuple changes.

For the current backend, this includes:

- insertion of a new `dms.Document` row
- an identity-changing update where the tuple extracted from `ResourceSchema.IdentityJsonPaths` changes

`IdentityVersion` must not change for:

- non-identity representation rewrites
- representation rewrites on dependent documents whose own identity tuple does not change
- authorization-only maintenance updates

Examples that must not change `ChangeVersion` by themselves:

- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`

Identity-change consequence rule:

- when an update changes the resource identity tuple, that write must allocate both a new `ChangeVersion` and a new `IdentityVersion`

## Downstream identity-propagation rules

An identity-changing update can force representation rewrites on dependent documents whose stored representation embeds the changed identity values.

Required semantics:

- the implementation must realize dependent rewrites through storage-layer propagation metadata, generated update plans, or equivalent database-driven machinery; the current backend's `dms.Reference` may be one input to that storage-layer work rather than the normative mechanism
- each dependent document whose stored representation changes because of the upstream identity change must receive a new `ChangeVersion`
- each such dependent representation change must emit a `dms.DocumentChangeEvent` row through the normal journal-maintenance rules
- a dependent document receives a new `IdentityVersion` only if the dependent document's own identity tuple changes
- a dependent document writes a key-change tracking row only if the dependent document's own identity tuple changes
- propagation must cover all affected downstream documents in the same committed write path without relying on ad hoc API/core-service recursion coverage

This is the current-backend bridge equivalent of the redesign's indirect-update semantics. A conforming implementation may realize the propagation with different internal mechanics, but it must preserve the same committed-state outcomes.

## Trigger Strategy

## Insert trigger

Recommended behavior:

- `BEFORE INSERT`
- if `ChangeVersion` is null, assign `nextval('dms.ChangeVersionSequence')`
- if `IdentityVersion` is null, assign `nextval('dms.ChangeVersionSequence')`

## Representation-change trigger

Recommended behavior:

- `BEFORE UPDATE OF EdfiDoc`
- if `NEW.EdfiDoc IS DISTINCT FROM OLD.EdfiDoc`, assign a new `ChangeVersion`
- otherwise preserve the existing value

Why the trigger is scoped to `EdfiDoc`:

- current authorization triggers update other columns on `dms.Document`
- a generic update trigger would create false changed-resource records

Trigger responsibility note:

- use database triggers to keep the live-row `ChangeVersion` stamp consistent on inserts and representation-changing updates
- keep direct-row `IdentityVersion` changes in the same storage update plan as the identity-changing write; one conforming current-backend bridge is for the write path to bind a newly allocated `IdentityVersion` for the directly changed row when one universal JSON trigger cannot derive resource-specific identity tuples
- use database triggers to emit `dms.DocumentChangeEvent` rows whenever the live `ChangeVersion` stamp changes
- keep downstream dependent restamping and journaling in the storage layer so indirect-change correctness does not depend on API/core-layer traversal coverage
- use the selected source's sequence ceiling as the source of `newestChangeVersion` for ODS parity rather than deriving `newestChangeVersion` from committed-row maxima

Bridge-mechanics note:

- direct-row `IdentityVersion` binding is a current-backend bridge implementation detail when one universal JSON trigger cannot infer identity tuples
- insert-time defaults or triggers own initial `IdentityVersion` assignment for newly inserted rows; explicit write-path binding applies only to committed identity-changing updates on existing rows, so the two mechanisms do not compete for the same update event
- this does not change the redesign-aligned ownership boundary: downstream propagation, restamping, and journaling remain storage-layer responsibilities driven by the committed update plan
- redesign-aligned backends may use metadata-driven write-path logic, database-native mechanisms, or a combination, provided the normative identity-stamping outcomes in this design are preserved

Validation requirement for direct-write `IdentityVersion` binding:

- migration and integration validation must prove that every committed identity-changing write path advances `IdentityVersion` exactly once for the changed document
- downstream propagation paths must also prove that dependent documents advance `IdentityVersion` only when their own identity tuple changes

## Journal trigger

Recommended behavior:

- `AFTER INSERT OR UPDATE OF ChangeVersion`
- insert one `dms.DocumentChangeEvent` row containing the new `ChangeVersion`, `DocumentPartitionKey`, `DocumentId`, and `ResourceKeyId`
- do not emit a journal row for updates that leave `ChangeVersion` unchanged

## Partition note

`dms.Document` is partitioned by `DocumentPartitionKey`.

The migration must validate, for each supported engine, that change-version stamping triggers apply to every physical table or partition that can store `dms.Document` rows in the deployed layout. For PostgreSQL this includes validating parent-table trigger propagation to partitions; MSSQL implementations must validate the engine-equivalent trigger coverage for the deployed layout.

This is a migration validation requirement, not an assumption.

Partitioned-FK compatibility requirement:

- for `dms.DocumentChangeEvent -> dms.Document` cascade behavior, each supported engine/version pair must validate whether declarative FK plus `ON DELETE CASCADE` is available for the deployed partitioning layout
- where declarative support is unavailable, migrations must provide and validate an equivalent delete-time cleanup mechanism

## Delete Tombstones

Create `dms.DocumentDeleteTracking`:

```sql
CREATE TABLE dms.DocumentDeleteTracking (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    KeyValues jsonb NOT NULL,
    SecurityElements jsonb NOT NULL,
    StudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StudentEdOrgResponsibilityAuthorizationIds jsonb NULL,
    ContactStudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StaffEducationOrganizationAuthorizationEdOrgIds jsonb NULL,
    CreatedByOwnershipTokenId smallint NULL,
    AuthorizationBasis jsonb NULL,
    DeletedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);
```

Current-backend key note:

- this primary-key tie-breaker shape is current-backend-specific
- redesign-aligned backends that do not persist `DocumentPartitionKey` must use a semantically equivalent stable document tie-breaker while preserving deterministic ordering semantics

## Why `KeyValues` is stored

Delete responses must return the resource identifier values clients use for synchronization.

Those values are derived from `ResourceSchema.IdentityJsonPaths`. Persisting them at delete time avoids dependence on a deleted live row or on new schema metadata tables that do not exist in the current backend.

ResourceVersion stability invariant:

- `dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` both store `ResourceVersion` alongside `KeyValues`, `OldKeyValues`, and `NewKeyValues`
- the `IdentityJsonPaths` used to materialize key alias fields at `/deletes` and `/keyChanges` query time are derived from the deployed schema for that `ResourceVersion`
- this design requires that `IdentityJsonPaths` for a given `(ProjectName, ResourceName, ResourceVersion)` triple is stable within a deployment; a schema upgrade that changes identity paths must use a new `ResourceVersion` value rather than mutating the existing one in place
- implementations must enforce this invariant at migration time; a deployed version of the schema must not retroactively alter the `IdentityJsonPaths` for a `ResourceVersion` that already has committed tombstone or key-change rows

## Why tracked-change authorization data is stored on tombstones

Delete queries must preserve the documented tracked-change authorization contract, including ODS-style delete-aware relationship visibility, after the live row and relationship rows have been removed.

The tombstone therefore stores:

- the current DMS authorization projection copied from `dms.Document`
- `CreatedByOwnershipTokenId` for redesign ownership-based authorization
- row-local `AuthorizationBasis` data containing the resolved basis-resource DocumentIds and other delete-aware relationship inputs needed to evaluate tracked-change authorization after the live row or relationship rows disappear

A committed tombstone does not guarantee public `/deletes` emission. Read-time delete execution still applies same-window re-add suppression against the selected live surface.

## Key Change Tracking

Create `dms.DocumentKeyChangeTracking`:

```sql
CREATE TABLE dms.DocumentKeyChangeTracking (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    OldKeyValues jsonb NOT NULL,
    NewKeyValues jsonb NOT NULL,
    SecurityElements jsonb NOT NULL,
    StudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StudentEdOrgResponsibilityAuthorizationIds jsonb NULL,
    ContactStudentSchoolAuthorizationEdOrgIds jsonb NULL,
    StaffEducationOrganizationAuthorizationEdOrgIds jsonb NULL,
    CreatedByOwnershipTokenId smallint NULL,
    AuthorizationBasis jsonb NULL,
    ChangedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);
```

Current-backend key note:

- this primary-key tie-breaker shape is current-backend-specific
- redesign-aligned backends that do not persist `DocumentPartitionKey` must use a semantically equivalent stable document tie-breaker while preserving deterministic ordering semantics

Required semantics:

- `dms.DocumentKeyChangeTracking.ChangeVersion` stores a distinct public key-change token allocated for the tracked key-change row from the same `dms.ChangeVersionSequence`
- `dms.DocumentKeyChangeTracking.ChangeVersion` does not store `dms.Document.IdentityVersion`
- this preserves legacy ODS key-change token sequencing while still leaving `IdentityVersion` as an internal bridge artifact

## Why old and new key values are stored

The current live row only contains the latest natural-key state.

`/keyChanges` must return both:

- the earliest key state the client needs to retire
- the latest key state the client needs to recognize

Those values cannot be reconstructed from only the current row after additional updates or deletes occur.

## Why key-change tracked-change authorization data is stored

Key-change results must remain authorization-filtered even if a later delete removes the live row or if later writes move the current resource to a different key state.

The tracking row therefore stores:

- the same authorization projection categories copied from the pre-update `dms.Document` row
- `CreatedByOwnershipTokenId` copied from the pre-update live row
- row-local `AuthorizationBasis` data containing the resolved basis-resource DocumentIds and other pre-update tracked-change authorization inputs needed by DocumentId-based relationship and custom-view authorization

## `AuthorizationBasis` payload semantics

`AuthorizationBasis` is a resource-scoped JSON document captured on tombstone and key-change rows.

One conforming use of this payload is to preserve the tracked-change authorization inputs that cannot be safely reconstructed from surviving live rows after deletes or later mutations.

Required semantics:

- the payload is interpreted only in the context of the row's routed resource
- it preserves the basis-resource `DocumentId` values needed for redesign relationship and custom-view authorization checks
- it preserves any additional delete-aware relationship inputs needed to reproduce ODS tracked-change visibility when the authorizing relationship row has already been deleted
- it is captured from the same pre-delete or pre-update authorization-resolution pass that determines the row's live authorization state
- the payload root is a JSON object
- when `AuthorizationBasis` is present, the payload root must contain `contractVersion`
- `contractVersion` is a positive integer identifying the resource-scoped tracked-change authorization contract version used when the row was captured
- when tracked-change relationship or custom-view authorization applies, the payload must contain `basisDocumentIds`
- `basisDocumentIds` is a deterministic JSON object whose keys are stable basis-resource identifiers for the routed resource and whose values are sorted unique arrays of positive `DocumentId` values resolved during the same pre-change authorization pass
- when delete-aware relationship authorization needs facts beyond basis-resource `DocumentId` values, the payload must also contain `relationshipInputs`, a deterministic resource-scoped object containing only the named pre-change facts required by that resource's tracked-change authorization contract
- each change-query-enabled resource that relies on relationship or custom-view tracked-change authorization must define the expected `basisDocumentIds` keys and any `relationshipInputs` members as part of its resource-scoped contract
- incompatible changes to that resource-scoped contract must bump `contractVersion`; retained rows are interpreted against their stored version rather than against heuristics derived only from the current live contract

## Resource-scoped key payload contract for shared tracking tables

`dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` are shared tables across all resources, but the key payload columns are intentionally resource-scoped rather than globally uniform.

Required rules:

- `KeyValues`, `OldKeyValues`, and `NewKeyValues` are interpreted only in the context of the same row's `ProjectName`, `ResourceName`, and `ResourceVersion`
- query execution must always filter by routed resource before reading or returning those payloads
- key extraction order is the declared `ResourceSchema.IdentityJsonPaths` order for that resource
- key payload aliases are resolved by the canonical key-alias rule below
- the API must not rely on storage-engine JSON property order; when a deterministic response-object field order is desired, materialization must follow `IdentityJsonPaths` order rather than the stored JSON value order

This means heterogeneous natural-key shapes across resources are expected and acceptable. The common tracking tables do not need one universal relational key schema because the public routes and internal readers are already resource-scoped.

## Canonical key-alias rule

The tracking payloads must use deterministic public field aliases derived from the same `ResourceSchema.IdentityJsonPaths` metadata used for identity extraction.

Required algorithm:

- evaluate the routed resource's ordered `IdentityJsonPaths`
- for each path, collect the property segments after `$`
- start with the final property segment as the tentative alias
- if tentative aliases collide within the resource, prepend parent property segments from right to left until each alias is unique within that resource
- emit the chosen suffix in lower camel case without separators
- preserve `IdentityJsonPaths` order during materialization; alias uniqueness is resource-local, not global
- derive aliases from JSONPath/property semantics, not from relational column names, trigger aliases, or table-specific storage details

Validation requirements:

- every Change-Query-enabled resource must have a non-empty `IdentityJsonPaths` definition
- each `identityJsonPath` must resolve to a scalar property path already accepted by schema validation
- exact duplicate canonical identity paths remain invalid
- if a resource cannot be made unique even after considering the full property-segment chain, Change Queries for that resource must fail fast at startup or migration time

This rule makes the common tables safe across heterogeneous `EdfiDoc` identities because the stored payload remains resource-scoped and the alias contract is derived directly from schema metadata rather than inferred from one global relational key schema.

## Concrete examples from authoritative schema fixtures

A proof pass over `src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json` showed that leaf-only naming is not sufficient for full feature coverage.

Representative examples:

- `students` uses `$.studentUniqueId`, which materializes as `studentUniqueId`
- `studentSchoolAssociations` uses `$.entryDate`, `$.schoolReference.schoolId`, and `$.studentReference.studentUniqueId`, which materialize as `entryDate`, `schoolId`, and `studentUniqueId`
- `grades` is identity-update-eligible and reuses both `schoolId` and `schoolYear` across `gradingPeriodReference` and `studentSectionAssociationReference`; canonical aliases therefore expand to `gradingPeriodReferenceSchoolId`, `studentSectionAssociationReferenceSchoolId`, `gradingPeriodReferenceSchoolYear`, and `studentSectionAssociationReferenceSchoolYear`

The same fixture also shows other repeated-leaf cases such as `courseOfferings` and `studentCTEProgramAssociations`. This confirms that the design must use deterministic suffix expansion rather than disabling Change Queries for any resource whose identity contains repeated leaf names.

## Why delete and key-change tracking remain separate tables

The design intentionally does not introduce one generic mixed change table for deletes, key changes, and live changed-resource selection.

Reasons:

- delete rows are terminal tombstones and must preserve `KeyValues` after the live row disappears
- key-change rows are transition records and must preserve both `OldKeyValues` and `NewKeyValues`
- key-change queries expose one row per retained identity-transition event rather than a tombstone-style terminal state
- live changed-resource queries are driven by the current live representation stamp and required live-change journal, not by historical tombstones
- separate tables keep indexes purpose-built, avoid sparse nullable payload columns, and allow retention policies to evolve without conflating distinct artifact lifecycles

## Key-change insert strategy

The key-change row is inserted by application code during the update path, not by a generic database trigger.

Reason:

- the application update path already has the resolved `ResourceSchema`
- the application can derive old and new key values from `ResourceSchema.IdentityJsonPaths`
- only the application can reliably distinguish a natural-key change from a representation rewrite that leaves identity unchanged
- the application already has the pre-update tracked-change authorization data that this design stores on the key-change row

## Key-change eligibility rules

Key-change tracking applies only when all of the following are true:

- the resource supports identity updates (`AllowIdentityUpdates = true`)
- the document existed before the write
- the tuple extracted from the pre-update `EdfiDoc` using `IdentityJsonPaths` differs from the tuple extracted from the post-update `EdfiDoc`

Required consequences:

- no key-change row is written for non-identity updates
- no key-change row is written for authorization-only maintenance updates
- no key-change row is written for dependent documents whose representation changed because another resource's identity changed, unless the dependent document's own extracted identity tuple also changed
- exactly one key-change row is written for the changed document per committed identity-changing update transaction

## Tombstone insert strategy

The tombstone is inserted by application code, not by a database delete trigger.

Reason:

- the application delete path already has the resolved `ResourceSchema`, current `EdfiDoc`, current tracked-change authorization data, and resource identity rules
- the database alone does not currently have generic metadata that maps `(ProjectName, ResourceName)` to `identityJsonPaths`

Delete-time extraction rule:

- `KeyValues` must be extracted from the pre-delete `EdfiDoc` using the same canonical identity-path and key-alias rules used for key-change tracking

## `dms.DocumentChangeEvent`

The feature requires an append-only live-change journal aligned to the backend-redesign data model:

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL,
    CreatedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE,
    CONSTRAINT FK_DocumentChangeEvent_ResourceKey
        FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId)
);
```

Semantics:

- one journal row per committed live representation change
- no delete payloads are stored here
- journal rows are removed automatically when the live row is deleted
- the journal is the required source for `journal + verify` changed-resource candidate selection
- resource filtering happens by `ResourceKeyId`, not by repeated text copies of project and resource names

Why it is not contradictory with tombstones:

- the journal is for live create and update selection
- tombstones are for deletes
- key-change tracking is for old-to-new natural-key transitions
- delete queries need data that survives after the live row and its journal rows are removed
- key-change queries need data that survives later key mutations and later deletes

## Later-phase replay-floor metadata if purge is introduced

Retention and purge are not part of the initial DMS-843 scope. If a later phase introduces purge for any change-query surface, the implementation must persist replay-floor metadata explicitly rather than inferring replay floors from table emptiness.

One conforming relational shape is:

```sql
CREATE TABLE dms.ChangeQueryRetentionFloor (
    SurfaceName varchar(64) NOT NULL PRIMARY KEY,
    ReplayFloorChangeVersion bigint NOT NULL,
    UpdatedAt timestamp NOT NULL DEFAULT now(),
    CONSTRAINT CK_ChangeQueryRetentionFloor_SurfaceName
        CHECK (SurfaceName IN ('live', 'deletes', 'keyChanges')),
    CONSTRAINT CK_ChangeQueryRetentionFloor_ReplayFloor
        CHECK (ReplayFloorChangeVersion >= 0)
);
```

Required semantics:

- `SurfaceName` identifies the participating synchronization surface: `live`, `deletes`, or `keyChanges`
- an absent row means that surface has not had its replay floor advanced by purge and therefore contributes replay floor `0`
- when a row is present, `ReplayFloorChangeVersion` is the lowest inclusive `minChangeVersion` that can still be requested for complete results from that surface
- if a surface becomes empty because of purge, its metadata row remains authoritative and must not be removed
- `availableChangeVersions` computes the instance-wide `oldestChangeVersion` as the greatest participating surface replay floor

Atomic update contract:

- each purge operation must delete obsolete tracking rows and advance that surface's replay floor metadata in the same transaction
- `availableChangeVersions` must never observe purged rows without the corresponding replay-floor advance, or a replay-floor advance without the corresponding purge
- if the purge transaction rolls back, both the deleted rows and the replay-floor advance must roll back together

## Index Design

## Required live-row support index

```sql
CREATE INDEX IX_Document_ResourceKeyId_DocumentId
    ON dms.Document (ResourceKeyId, DocumentPartitionKey, Id);
```

Purpose:

- support redesign-aligned resource-key joins and diagnostics
- keep the bridge schema aligned with the backend-redesign live-row shape

## Required ownership support index

```sql
CREATE INDEX IX_Document_CreatedByOwnershipTokenId
    ON dms.Document (CreatedByOwnershipTokenId);
```

Purpose:

- support redesign ownership-based authorization on live reads
- support efficient capture and validation of tracked-change ownership data

## Required tombstone index

```sql
CREATE INDEX IX_DocumentDeleteTracking_Project_Resource_ChangeVersion
    ON dms.DocumentDeleteTracking
    (
        ProjectName,
        ResourceName,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );
```

Purpose:

- support resource-scoped delete scans
- support deterministic ordering

## Required key-change index

```sql
CREATE INDEX IX_DocumentKeyChangeTracking_Project_Resource_ChangeVersion
    ON dms.DocumentKeyChangeTracking
    (
        ProjectName,
        ResourceName,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );
```

Purpose:

- support resource-scoped key-change scans
- support deterministic ordering over retained key-change events

## Required journal indexes

```sql
CREATE INDEX IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent
    (
        ResourceKeyId,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );

CREATE INDEX IX_DocumentChangeEvent_Document
    ON dms.DocumentChangeEvent (DocumentPartitionKey, DocumentId);
```

Purpose:

- support the required `WHERE ResourceKeyId = @R AND ChangeVersion BETWEEN @min AND @max` candidate selection pattern
- support efficient verification joins back to `dms.Document`

## Available Change Version Computation

Ed-Fi ODS/API exposes one global synchronization surface through `availableChangeVersions` and uses that captured synchronization version across `keyChanges`, changed-resource queries, and `deletes`; see the [platform guide](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/) and [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/). DMS follows that same single-surface model.

The participating sources are:

- live side from `dms.DocumentChangeEvent.ChangeVersion`
- delete side from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side from `dms.DocumentKeyChangeTracking.ChangeVersion`

For the participating sources:

- `newestChangeVersion` is the selected source sequence ceiling (`next value - 1` on that source), not the max retained committed tracking-row value across the participating sources
- for the initial DMS-843 scope, each participating surface contributes replay floor `0`, so `oldestChangeVersion` remains `0`
- if the selected source has never allocated a change-version value and no replay-floor metadata exists, return `0` for both values; this is ODS-output-compatible behavior: legacy ODS also returns `0/0` on an empty instance (NULL MAX serialized as 0), and DMS-843 reaches the same result via sequence-ceiling arithmetic (`next value - 1` = `0` before the first allocation)
- if a later retention phase introduces purge metadata, `oldestChangeVersion` becomes the effective replay floor across the participating sources, and `newestChangeVersion = oldestChangeVersion =` the greatest participating replay floor when all participating sources are empty after purge
- for identity-changing updates, the key-change participation value is the distinct public key-change token allocated from `dms.ChangeVersionSequence` for the tracked row, not the live resource `ChangeVersion` and not the internal `IdentityVersion`

Legacy ODS allocates a distinct sequence value for key-change rows from the same change-version sequence used by other synchronization artifacts.

DMS-843 adopts that same public key-change token model for `/keyChanges`.

Client-compatibility note:

- the public API returns the canonical live-resource `id` on `/deletes` and `/keyChanges` from `DocumentUuid`
- `DocumentUuid` is persisted on both tracking tables specifically so those routes can return the same canonical resource identifier after the live row has changed or been deleted

This computation must read committed replay-floor metadata and normalize engine-specific sequence state to the logical allocation ceiling for the selected source. It must not derive `newestChangeVersion` from max retained tracking rows, and it must not surface an engine's raw initial sequence state as a non-zero bootstrap watermark.

## Backend-redesign artifact mapping

The bridge design is aligned to backend-redesign change tracking by preserving artifact responsibilities and adding the missing bridge artifacts where the current backend differs physically.

| Current-backend artifact | Purpose in this design | Backend-redesign relationship |
| --- | --- | --- |
| `dms.ResourceKey` plus `dms.Document.ResourceKeyId` | stable resource-type lookup and narrow journal filter key | same resource-key concept already defined in redesign |
| `dms.Document.CreatedByOwnershipTokenId` | live ownership-based authorization stamp | semantic equivalent of redesign ownership stamp on `dms.Document` |
| `dms.Document.ChangeVersion` | canonical live-row served token for changed-resource semantics | semantic equivalent of redesign `dms.Document.ContentVersion` |
| `dms.Document.IdentityVersion` | identity-change stamp for alignment of identity-update tracking | semantic equivalent of redesign `dms.Document.IdentityVersion` |
| `dms.DocumentChangeEvent` | append-only live-change journal for required `journal + verify` selection | same live-change-journal concept already defined in redesign |
| `dms.DocumentDeleteTracking` | delete tombstone store with key values and tracked-change authorization data | redesign still needs a semantically equivalent delete artifact because `DocumentChangeEvent` does not survive deletes and does not store delete payload or delete-aware authorization inputs |
| `dms.DocumentKeyChangeTracking` | old/new natural-key transition store with tracked-change authorization data | redesign still needs a semantically equivalent key-change artifact because live-row stamps and `DocumentChangeEvent` do not preserve prior key values or pre-update tracked-change authorization state |
| tracked-row `AuthorizationBasis` | row-local basis-resource `DocumentId` inputs for relationship and custom-view authorization after delete or later mutation | bridge artifact required to apply redesign DocumentId-based authorization concepts to tracked changes |

This means the bridge design extends the backend-redesign update-tracking and tracked-change authorization model where necessary for Change Query parity, but it no longer diverges on whether the live journal, resource key, identity stamp, or ownership/basis authorization inputs are core artifacts.

## Backfill Strategy

The design chooses one-time deterministic backfill for existing live rows.

Reasons:

- every current row receives a valid `ChangeVersion`
- every current row receives a valid `IdentityVersion`
- every current row receives a valid `ResourceKeyId`
- clients can use `availableChangeVersions` immediately after rollout
- no ambiguous future-only tracking mode is introduced

Backfill order:

```text
DocumentPartitionKey ASC, Id ASC
```

Requirements:

- seed `dms.ResourceKey` from the deployed effective schema manifest before live-row backfill; do not derive resource keys from the current contents of `dms.Document`
- resources with zero current live rows still require seeded `dms.ResourceKey` rows
- assign each live row a non-null `ResourceKeyId`
- allocate one `ChangeVersion` value per row
- allocate one `IdentityVersion` value per row
- allocate sequence values in the exact declared backfill order using an engine-specific mechanism that preserves per-row iteration order
- complete the backfill before exposing the feature endpoints
- validate that `ResourceKeyId`, `ChangeVersion`, and `IdentityVersion` are non-null afterward
- execute live-row backfill, journal backfill, and trigger enablement in one controlled deployment window with representation-changing writes paused or drained

Implementation note:

- set-based sequence-assignment updates are not sufficient when the engine cannot guarantee row-update order during backfill
- PostgreSQL `UPDATE ... FROM ... nextval(...)` is one example that is not sufficient for this requirement because it does not guarantee row-update order
- use an ordered procedural loop, cursor, or another engine-specific ordered-assignment mechanism that guarantees the sequence is consumed in `DocumentPartitionKey ASC, Id ASC` order
- when `ChangeVersion` and `IdentityVersion` are backfilled in the same pass, consume them as distinct sequence allocations rather than copying one value into both columns

## Journal Backfill

`dms.DocumentChangeEvent` must also be backfilled after the live-row backfill is complete.

Required rule:

- insert one journal row for each current live row using the row's current `ChangeVersion` and `ResourceKeyId`

## Metadata Notes

The current backend stores `_etag` and `_lastModifiedDate` inside `EdfiDoc`.

This feature does not redesign that behavior. `ChangeVersion` remains additive and serves Change Query ordering, while `IdentityVersion` remains an internal alignment artifact for identity-update tracking.

Current-backend divergence note:

- DMS-843 aligns to redesign change-tracking responsibilities, but it does not yet move `_etag` or `_lastModifiedDate` into redesign-style dedicated `dms.Document` stamp columns on the current backend
- a future backend replacement may externalize those fields to redesign-style `dms.Document` columns without changing the public DMS-843 route or synchronization contract

The design remains aligned to backend-redesign concepts by treating `ChangeVersion` as the current-backend equivalent of redesign `ContentVersion`, `IdentityVersion` as the current-backend equivalent of redesign `IdentityVersion`, and `ResourceKeyId` as the current-backend journal filter key.

To avoid ambiguity, current-backend implementations of DMS-843 should persist only `dms.Document.ChangeVersion` for this live-row stamp. If a future redesign adopts the physical name `ContentVersion`, that would be a rename or replacement of the same responsibility, not an additional concurrent column.

## Consolidated SQL Sketch

See `Appendix-A-Feature-DDL-Sketch.sql` for an implementation-oriented SQL sketch of the required and deferred artifacts.
