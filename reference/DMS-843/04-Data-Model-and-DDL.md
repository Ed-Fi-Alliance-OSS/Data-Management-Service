# Data Model and DDL

## Design Summary

The feature uses a DMS-native ChangeVersion column model centered on the canonical `dms.Document` row and a tombstone table for deletes.

Core required artifacts:

- `dms.ChangeVersionSequence`
- `dms.Document.ChangeVersion`
- `dms.DocumentDeleteTracking`
- `dms.DocumentKeyChangeTracking`
- supporting indexes
- change-version stamping triggers

Optional internal alignment artifact:

- `dms.DocumentChangeEvent`

Conditional retention artifact:

- `dms.ChangeQueryRetentionFloor` when purge is enabled

## Backend Scope Note

The canonical DMS-843 design is defined at the contract, artifact-responsibility, and behavioral level rather than as a PostgreSQL-only feature.

Normative rule:

- any relational backend that implements DMS-843 must provide semantically equivalent change-version allocation, live-row stamping, tombstone capture, key-change capture, indexing, and row-level write serialization

Informative note:

- the SQL blocks in this document and in `Appendix-A-Feature-DDL-Sketch.sql` use PostgreSQL syntax because the current DMS storage implementation is PostgreSQL-first
- those SQL blocks are examples of one conforming backend implementation, not a statement that the public feature contract is PostgreSQL-only

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

The feature extends this table with `ChangeVersion`.

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
- value `0` is reserved for the bootstrap watermark sentinel exposed by `availableChangeVersions` when no retained change rows exist
- the sequence is an allocation mechanism for future stamps, not the public source of `oldestChangeVersion` or `newestChangeVersion`

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

## ChangeVersion Stamping Rules

`ChangeVersion` must change when the served representation changes.

For the current backend, this includes:

- insertion of a new `dms.Document` row
- direct updates to `dms.Document.EdfiDoc`
- representation rewrites caused by identity-update cascades that rewrite `EdfiDoc`

`ChangeVersion` must not change when only authorization-maintenance columns or other non-representation metadata are updated.

Examples that must not change `ChangeVersion` by themselves:

- `StudentSchoolAuthorizationEdOrgIds`
- `StudentEdOrgResponsibilityAuthorizationIds`
- `ContactStudentSchoolAuthorizationEdOrgIds`
- `StaffEducationOrganizationAuthorizationEdOrgIds`

## Trigger Strategy

## Insert trigger

Recommended behavior:

- `BEFORE INSERT`
- if `ChangeVersion` is null, assign `nextval('dms.ChangeVersionSequence')`

## Update trigger

Recommended behavior:

- `BEFORE UPDATE OF EdfiDoc`
- if `NEW.EdfiDoc IS DISTINCT FROM OLD.EdfiDoc`, assign a new `ChangeVersion`
- otherwise preserve the existing value

Why the trigger is scoped to `EdfiDoc`:

- current authorization triggers update other columns on `dms.Document`
- a generic update trigger would create false changed-resource records

Trigger responsibility note:

- use database triggers to keep the live-row `ChangeVersion` stamp consistent on inserts and representation-changing updates
- if the optional `dms.DocumentChangeEvent` journal is enabled, use triggers to emit journal rows when the live stamp changes
- do not use trigger-maintained aggregate state or the raw sequence value as the source of `availableChangeVersions`

## Partition note

`dms.Document` is partitioned by `DocumentPartitionKey`.

The migration must validate whether the deployed PostgreSQL version applies parent-table row triggers to all partitions in the current deployment model. If not, the migration must create equivalent triggers on each partition table.

This is a migration validation requirement, not an assumption.

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
    DeletedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);
```

## Why `KeyValues` is stored

Delete responses must return the resource identifier values clients use for synchronization.

Those values are derived from `ResourceSchema.IdentityJsonPaths`. Persisting them at delete time avoids dependence on a deleted live row or on new schema metadata tables that do not exist in the current backend.

## Why authorization projection is stored

Delete queries must preserve the same logical authorization semantics as live collection GET queries after the live row and authorization companion rows have been removed. The tombstone therefore stores the authorization projection copied from `dms.Document`.

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
    ChangedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId)
);
```

## Why old and new key values are stored

The current live row only contains the latest natural-key state.

`/keyChanges` must return both:

- the earliest key state the client needs to retire
- the latest key state the client needs to recognize

Those values cannot be reconstructed from only the current row after additional updates or deletes occur.

## Why key-change authorization projection is stored

Key-change results must remain authorization-filtered even if a later delete removes the live row or if later writes move the current resource to a different key state. The tracking row therefore stores the same authorization projection categories copied from the pre-update `dms.Document` row.

## Resource-scoped key payload contract for shared tracking tables

`dms.DocumentDeleteTracking` and `dms.DocumentKeyChangeTracking` are shared tables across all resources, but the key payload columns are intentionally resource-scoped rather than globally uniform.

Required rules:

- `KeyValues`, `OldKeyValues`, and `NewKeyValues` are interpreted only in the context of the same row's `ProjectName`, `ResourceName`, and `ResourceVersion`
- query execution must always filter by routed resource before reading or returning those payloads
- key extraction order is the declared `ResourceSchema.IdentityJsonPaths` order for that resource
- key payload aliases are resolved by the canonical key-alias rule below
- the API must not rely on PostgreSQL `jsonb` property order; when a deterministic response-object field order is desired, materialization must follow `IdentityJsonPaths` order rather than stored `jsonb` order

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
- key-change queries have route-specific collapse semantics that do not apply to delete rows
- live changed-resource queries are driven by the current live representation stamp and optional live-change journal, not by historical tombstones
- separate tables keep indexes purpose-built, avoid sparse nullable payload columns, and allow retention policies to evolve without conflating distinct artifact lifecycles

## Key-change insert strategy

The key-change row is inserted by application code during the update path, not by a generic database trigger.

Reason:

- the application update path already has the resolved `ResourceSchema`
- the application can derive old and new key values from `ResourceSchema.IdentityJsonPaths`
- only the application can reliably distinguish a natural-key change from a representation rewrite that leaves identity unchanged
- the application already has the pre-update authorization projection that this design stores on the key-change row

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

- the application delete path already has the resolved `ResourceSchema`, current `EdfiDoc`, current authorization projection, and resource identity rules
- the database alone does not currently have generic metadata that maps `(ProjectName, ResourceName)` to `identityJsonPaths`

Delete-time extraction rule:

- `KeyValues` must be extracted from the pre-delete `EdfiDoc` using the same canonical identity-path and key-alias rules used for key-change tracking

## Optional `dms.DocumentChangeEvent`

The feature may also include an internal append-only live-change journal:

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentPartitionKey smallint NOT NULL,
    DocumentId bigint NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    IsDescriptor boolean NOT NULL,
    CreatedAt timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (ChangeVersion, DocumentPartitionKey, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document
        FOREIGN KEY (DocumentPartitionKey, DocumentId)
        REFERENCES dms.Document (DocumentPartitionKey, Id)
        ON DELETE CASCADE
);
```

Semantics:

- one journal row per committed live representation change
- no delete payloads are stored here
- journal rows are removed automatically when the live row is deleted
- the journal can support `journal + verify` selection for live changed-resource queries

Why it is not contradictory with tombstones:

- the journal is for live create and update selection
- tombstones are for deletes
- key-change tracking is for old-to-new natural-key transitions
- delete queries need data that survives after the live row and its journal rows are removed
- key-change queries need data that survives later key mutations and later deletes

## Required replay-floor metadata when purge is enabled

If retention purge is enabled for any change-query surface, the implementation must persist replay-floor metadata explicitly rather than inferring replay floors from table emptiness.

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

## Required live-row index

```sql
CREATE INDEX IX_Document_Project_Resource_ChangeVersion
    ON dms.Document (ProjectName, ResourceName, ChangeVersion, DocumentPartitionKey, Id);
```

Purpose:

- support resource-scoped changed-resource scans in live-row execution mode
- support deterministic ordering

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
- support deterministic ordering before window collapse

## Optional live-row support index

```sql
CREATE INDEX IX_Document_ChangeVersion
    ON dms.Document (ChangeVersion);
```

Use only if validation shows it is needed for `availableChangeVersions` or supporting scans.

## Optional journal indexes

```sql
CREATE INDEX IX_DocumentChangeEvent_Resource_ChangeVersion
    ON dms.DocumentChangeEvent
    (
        ProjectName,
        ResourceName,
        ChangeVersion,
        DocumentPartitionKey,
        DocumentId
    );

CREATE INDEX IX_DocumentChangeEvent_Document
    ON dms.DocumentChangeEvent (DocumentPartitionKey, DocumentId);
```

These indexes are needed only when the journal is enabled.

## Available Change Version Computation

Ed-Fi ODS/API exposes one global synchronization surface through `availableChangeVersions` and uses that captured synchronization version across `keyChanges`, changed-resource queries, and `deletes`; see the [platform guide](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/) and [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/). DMS follows that same single-surface model.

If the journal is not enabled, the participating sources are:

- live side from `dms.Document.ChangeVersion`
- delete side from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side from `dms.DocumentKeyChangeTracking.ChangeVersion`

If the journal is enabled, the participating sources are:

- live side from `dms.DocumentChangeEvent.ChangeVersion`
- delete side from `dms.DocumentDeleteTracking.ChangeVersion`
- key-change side from `dms.DocumentKeyChangeTracking.ChangeVersion`

For the participating sources:

- `newestChangeVersion` is the synchronization ceiling across them
- `oldestChangeVersion` is the effective replay floor across them, meaning the lowest inclusive `minChangeVersion` from which the API can still return complete synchronization results across the participating sources
- without purge metadata, each participating surface contributes replay floor `0`
- when purge is enabled for a surface, that surface's replay floor comes from `dms.ChangeQueryRetentionFloor`
- if retained participating rows exist, `newestChangeVersion` is the maximum retained `ChangeVersion` across them
- if all participating sources are empty but replay-floor metadata exists, `newestChangeVersion = oldestChangeVersion =` the greatest participating replay floor
- if the participating surfaces have different replay floors, `oldestChangeVersion` is the greatest of those floors
- if all participating sources are empty and no replay-floor metadata exists, return `0` for both values; in that bootstrap case `0` is a starting watermark sentinel rather than a retained change row

Client-compatibility note:

- the public API returns the canonical live-resource `id` on `/deletes` and `/keyChanges` from `DocumentUuid`
- `DocumentUuid` is persisted on both tracking tables specifically so those routes can return the same canonical resource identifier after the live row has changed or been deleted

This computation must read committed tracking artifacts and any committed replay-floor metadata. It must not use the current `dms.ChangeVersionSequence` value as a substitute for either bound, because sequence advancement can lead committed state and can contain rollback gaps.

## Backend-redesign artifact mapping

The bridge design is aligned to backend-redesign change tracking by preserving artifact responsibilities rather than forcing identical physical names.

| Current-backend artifact | Purpose in this design | Backend-redesign relationship |
| --- | --- | --- |
| `dms.Document.ChangeVersion` | canonical live-row served token for changed-resource semantics | semantic equivalent of redesign `dms.Document.ContentVersion` |
| optional `dms.DocumentChangeEvent` | append-only live-change journal for `journal + verify` selection | same live-change-journal concept already defined in redesign |
| `dms.DocumentDeleteTracking` | delete tombstone store with key values and authorization projection | redesign still needs a semantically equivalent delete artifact because `DocumentChangeEvent` does not survive deletes and does not store delete payload |
| `dms.DocumentKeyChangeTracking` | old/new natural-key transition store with authorization projection | redesign still needs a semantically equivalent key-change artifact because live-row stamps and `DocumentChangeEvent` do not preserve prior key values |

This means the bridge design extends the backend-redesign update-tracking model where necessary for Change Query parity, but it does not contradict the redesign rules for live representation stamps or live journaling.

## Backfill Strategy

The design chooses one-time deterministic backfill for existing live rows.

Reasons:

- every current row receives a valid `ChangeVersion`
- clients can use `availableChangeVersions` immediately after rollout
- no ambiguous future-only tracking mode is introduced

Backfill order:

```text
DocumentPartitionKey ASC, Id ASC
```

Requirements:

- allocate one sequence value per row
- allocate sequence values in the exact declared backfill order using an engine-specific mechanism that preserves per-row iteration order
- complete the backfill before exposing the feature endpoints
- validate that all rows are non-null afterward

Implementation note:

- PostgreSQL set-based `UPDATE ... FROM ... nextval(...)` is not sufficient for this requirement because it does not guarantee row-update order
- use an ordered procedural loop, cursor, or another engine-specific ordered-assignment mechanism that guarantees the sequence is consumed in `DocumentPartitionKey ASC, Id ASC` order

## Journal Backfill

If the optional journal is enabled, it must also be backfilled after the live-row backfill is complete.

Required rule:

- insert one journal row for each current live row using the row's current `ChangeVersion`

## Metadata Notes

The current backend stores `_etag` and `_lastModifiedDate` inside `EdfiDoc`.

This feature does not redesign that behavior. `ChangeVersion` is additive and serves Change Query ordering only.

The design remains aligned to backend-redesign concepts by treating `ChangeVersion` as the current-backend equivalent of redesign `ContentVersion`.

To avoid ambiguity, current-backend implementations of DMS-843 should persist only `dms.Document.ChangeVersion` for this live-row stamp. If a future redesign adopts the physical name `ContentVersion`, that would be a rename or replacement of the same responsibility, not an additional concurrent column.

## Consolidated SQL Sketch

See `Appendix-A-Feature-DDL-Sketch.sql` for an implementation-oriented SQL sketch of the required and optional artifacts.
