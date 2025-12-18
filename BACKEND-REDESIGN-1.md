# Backend Redesign 1: Relational Primary Store (Tables per Resource)

## Status

Draft. This is an initial design proposal for replacing the current three-table document store (`Document`/`Alias`/`Reference`) with a relational model using tables per resource, while keeping DMS behavior metadata-driven via `ApiSchema.json`.

## Goals and Constraints

### Goals

1. **Relational-first storage**: Store resources in traditional relational tables (one root table per resource, plus child tables for collections).
2. **Metadata-driven behavior**: Continue to drive validation, identity/reference extraction, and query semantics using `ApiSchema.json` (no handwritten per-resource code).
3. **Low coupling to document shape**: Avoid hard-coding resource shapes in C#; schema awareness comes from metadata + conventions.
4. **Eliminate reference-cascade updates**: References should not require rewriting referencing documents when a natural key changes.
5. **SQL Server + PostgreSQL parity**: The design must be implementable (DDL + CRUD + query) on both engines.

### Constraints / Explicit Decisions

- **Cached JSON is optional**: The relational representation is the canonical source of truth. A JSON cache may exist only as an optimization/integration aid.
- **Schema upload/reload may require migration**: DMS does not need to be able to adapt online to a new schema without a migration step.
- **Authorization is out of scope**: Ignore existing authorization-related tables/columns/scripts; authorization storage will be redesigned separately.
- **No code generation**: No generated per-resource C# or “checked-in generated SQL per resource”. SQL may still be *produced and executed* by a migrator from metadata, but should not require generated source artifacts to compile/run DMS.

## Glossary (Current DMS Terms)

- **DocumentUuid**: The API “id” (UUID) exposed in URLs and stored as `id` in documents.
- **DocumentId**: A database surrogate key (BIGINT) for internal relationships and FKs.
- **DocumentIdentity**: Ordered natural-key elements extracted from the document (from `identityJsonPaths`).
- **ReferentialId**: Deterministic UUIDv5 hash of `(ProjectName, ResourceName, DocumentIdentity)` used for identity-based lookups and references.
- **ProjectName / ResourceName**: As used in `ApiSchema.json` and current DMS.

## High-Level Architecture

Keep DMS Core (pipeline, validators, extractors) intact:

- Core continues to produce `DocumentInfo` (identity + `ReferentialId` + extracted references/descriptors) and operates on JSON bodies.
- Backend repositories (`IDocumentStoreRepository`, `IQueryHandler`) become responsible for:
  1. **Flattening** incoming JSON into relational tables
  2. **Reference resolution** (natural keys → `DocumentId`)
  3. **Reconstitution** (relational → JSON) for GET/query responses

This preserves the Core/Backend boundary and avoids leaking relational concerns into Core.

## Proposed Database Model

### Core tables (schema: `dms`)

#### 1) `dms.Document`

Canonical metadata per persisted resource instance. One row per document, regardless of resource type.

Key fields (common across PostgreSQL and SQL Server):

- `DocumentId BIGINT` (identity/sequence) **PK**
- `DocumentUuid UUID/UNIQUEIDENTIFIER` **unique** (API id)
- `ProjectName VARCHAR(256)` (MetaEd project name)
- `ResourceName VARCHAR(256)` (MetaEd resource name, e.g., `Student`)
- `ResourceVersion VARCHAR(64)`
- `CreatedAt` (`timestamp` / `datetime2`)
- `LastModifiedAt` (`timestamp` / `datetime2`)
- `LastModifiedTraceId VARCHAR(128)`
- `Etag VARCHAR(128)` (value returned in response header and embedded as `_etag`)
- `LastModifiedDateUtc VARCHAR(32)` (the `_lastModifiedDate` string, or derive from `LastModifiedAt`)

Notes:
- `DocumentUuid` remains stable across identity updates; identity-based upserts map to it via `dms.Identity`.
- Authorization-related columns are intentionally omitted here.

#### 2) `dms.Identity`

Maps `ReferentialId` → `DocumentId` (replaces/absorbs today’s `dms.Alias`), including superclass aliases used for polymorphic references.

- `ReferentialId UUID/UNIQUEIDENTIFIER` **PK** (or unique constraint)
- `DocumentId BIGINT` **FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- `IdentityRole SMALLINT` (e.g., `0=Primary`, `1=SuperclassAlias`, reserved for future)
- Optional debug columns: `ProjectName`, `ResourceName`

Critical invariants:
- **Uniqueness** of `ReferentialId` enforces “one natural identity maps to one document”.
- Subclass writes insert both:
  - primary `ReferentialId` (subclass resource name)
  - superclass alias `ReferentialId` (superclass resource name)

This preserves current polymorphic reference behavior without a separate Alias table.

#### 3) `dms.Descriptor` (unified)

Descriptors are still documents, but we maintain a unified descriptor table keyed by the descriptor document’s `DocumentId`. This makes descriptor FK enforcement possible without per-descriptor tables.

- `DocumentId BIGINT` **PK** and **FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- `Namespace VARCHAR(255)`
- `CodeValue VARCHAR(50)`
- `ShortDescription VARCHAR(75)`
- `Description VARCHAR(1024)` NULL
- `Discriminator VARCHAR(128)` (e.g., `GradeLevelDescriptor`)
- `Uri VARCHAR(306)` (computed or stored)

Descriptor references can be enforced two ways:
- **Preferred (type-safe)**: FK to the specific descriptor resource root table (e.g., `edfi.GradeLevelDescriptor(DocumentId)`), which is itself keyed to `dms.Descriptor(DocumentId)`.
- **Simpler (descriptor-only)**: FK directly to `dms.Descriptor(DocumentId)` to guarantee “this is a descriptor”, but without enforcing the specific descriptor type at the DB level.

#### 4) `dms.SchemaInfo`

Tracks which `ApiSchema` reload/version the database is migrated to.

- `SchemaInfoId BIGINT` PK
- `ApiSchemaReloadId` (string/guid) – matches `IApiSchemaProvider.ReloadId`
- `AppliedAt` timestamp/datetime2
- Optional: `ApiSchemaVersion` and/or hash for auditing

#### 5) Optional: `dms.DocumentCache`

Full JSON cache of the reconstituted document. **Not required for correctness**.

- `DocumentId BIGINT` **PK/FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- `DocumentJson JSONB` (PostgreSQL) / `NVARCHAR(MAX)` (SQL Server) with JSON validity constraint where possible
- `ComputedAt` timestamp/datetime2

Uses:
- Faster GET/query responses (skip reconstitution)
- Easier CDC / OpenSearch indexing / external integrations

When disabled, DMS must reconstitute JSON from relational tables.

#### 6) Optional/Recommended: `dms.QueryIndex`

A cross-resource, typed index of `queryFieldMapping` values to keep query execution metadata-driven without generating complex per-resource join SQL.

Row model (EAV-like, but only for declared query fields):
- `DocumentId BIGINT` FK → `dms.Document` ON DELETE CASCADE
- `FieldName VARCHAR(128)` (query parameter name)
- `ValueType SMALLINT` (enum)
- `ValueString NVARCHAR(450)` NULL
- `ValueNumber DECIMAL(38, 10)` NULL
- `ValueDate DATE` NULL
- `ValueDateTime DATETIME2`/`timestamp` NULL
- `ValueBoolean BIT`/`boolean` NULL

Indexes:
- For each `Value*` column: `(FieldName, Value*, DocumentId)` (engine-appropriate)
- Unique constraint to prevent duplicates: `(DocumentId, FieldName, ValueType, Value*)`

This provides:
- SQL Server parity (no reliance on Postgres GIN/jsonb operators)
- Stable performance for common equality-based Ed-Fi queries
- Minimal coupling (only to declared query fields, not full document shape)

### Resource tables (schema per project)

Each project gets a schema derived from `ProjectNamespace` (e.g., `ed-fi` → `edfi`, `tpdm` → `tpdm`). Core `dms.*` tables remain in `dms`.

For each resource `R`:

#### Root table: `{schema}.{R}`

One row per document; PK is `DocumentId` (shared surrogate key).

Typical structure:
- `DocumentId BIGINT` **PK/FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- Natural key columns (from `identityJsonPaths`) → unique constraint
- Scalar columns for top-level non-collection properties
- Reference FK columns:
  - Non-descriptor references (concrete target): `..._DocumentId BIGINT` FK → `{schema}.{TargetResource}(DocumentId)` to enforce existence *and* resource type
  - Non-descriptor references (polymorphic/abstract target): `..._DocumentId BIGINT` FK → `dms.Document(DocumentId)` plus optional discriminator/membership enforcement (see Reference Validation)
  - Descriptor references: `..._DescriptorId BIGINT` FK → `{schema}.{DescriptorResource}(DocumentId)` (preferred) or `dms.Descriptor(DocumentId)`

#### Child tables for collections

For each JSON array of objects/scalars under the resource:

`{schema}.{R}_{CollectionPath}` (name derived deterministically; see Naming Rules)
- `Id BIGINT` identity **PK** (needed for deeper nesting)
- `ParentDocumentId BIGINT` FK → `{schema}.{R}(DocumentId)` ON DELETE CASCADE
- `Ordinal INT NOT NULL` (preserve array order for reconstitution)
- Scalar columns for the element object
- Reference/descriptor FK columns as above
- Unique constraints derived from `arrayUniquenessConstraints` when available

Nested collections create additional tables referencing the parent child table’s `Id`.

### Extensions

Extension projects and resource extensions should follow a consistent 1:1 and 1:N pattern without merging extension columns into the core resource table:

- For `isResourceExtension: true` resources, create `{schema}.{BaseResource}Extension_{Project}` tables keyed by `DocumentId` (1:1) and child tables for extension collections.
- This prevents core schema churn and keeps SQL Server row width/column count manageable.

## Reference Validation

Reference validation is provided by **two layers** (mirroring what the current `Reference` table + FK does, but in a relational way):

### 1) Write-time validation (application-level)

During POST/PUT processing, the backend:
- Resolves each extracted reference (`DocumentReference` / `DescriptorReference`) from `ReferentialId` → `DocumentId` using `dms.Identity`.
- Fails the request if any referenced identity does not exist (same semantics as today: descriptor failures vs resource reference failures).

This is required because the relational tables store **`DocumentId` foreign keys**, and we cannot write those without resolving them.

### 2) Database enforcement (FKs)

Relational tables store references as FKs so the database enforces referential integrity:

- **Concrete non-descriptor references**: FK to the **target resource table**, e.g. `FK(StudentSchoolAssociation.School_DocumentId → edfi.School.DocumentId)`. This validates both existence and type.
- **Descriptor references**: FK to the **target descriptor resource table** (preferred) or to `dms.Descriptor`. This validates existence, and optionally the descriptor type.
- **Polymorphic references** (e.g., `EducationOrganization`): a single FK to a concrete table is not possible. Baseline enforcement is:
  - FK to `dms.Document(DocumentId)` (existence)
  - Application validation ensures the referenced `DocumentId` is one of the allowed concrete types

If DB-level enforcement of polymorphic membership is required, add one of:
- A `...Discriminator` column + trigger that checks `dms.Document.ResourceName` for the referenced `DocumentId`
- A small membership table per abstract type (e.g., `edfi.EducationOrganizationMembership(DocumentId)`), maintained by triggers on the concrete resource tables, and FK to the membership table

### Delete conflicts

Deletes rely on the same FK graph:
- If a document is referenced, `DELETE` fails with an FK violation; DMS maps that to a `409` conflict.
- Because FK names are deterministic, DMS can map the violated constraint back to a resource/table to report “what is referencing me” without a separate `Reference` edge table.

## Naming Rules (Deterministic, Cross-DB Safe)

To keep migrations tractable and avoid rename cascades, physical names must be deterministic:

- Schema name: derived from `ProjectNamespace` (`ed-fi` → `edfi`, non-alphanumerics removed/normalized).
- Table names: PascalCase resource names (MetaEd `resourceName`), plus deterministic suffixes for collections.
- Column names: PascalCase of JSON property names, with suffixes:
  - `..._DocumentId` for non-descriptor references
  - `..._DescriptorId` for descriptor references
- Max identifier length handling:
  - PostgreSQL: 63 bytes; SQL Server: 128 chars
  - When exceeding, apply deterministic truncation + short hash suffix.

## How `ApiSchema.json` Drives This Design

The design uses existing metadata and adds minimal new hints to avoid embedding full “flattening metadata” if we can derive it.

### Existing ApiSchema inputs (already present)

- `jsonSchemaForInsert`: authoritative shape, types, formats, maxLength, required
- `identityJsonPaths`: natural key extraction and uniqueness
- `documentPathsMapping`: identifies references vs scalars vs descriptor paths, plus reference identity mapping
- `decimalPropertyValidationInfos`: precision/scale for `decimal`
- `arrayUniquenessConstraints`: relational unique constraints for collection tables
- `isSubclass` + superclass metadata: drives alias identity insertion
- `queryFieldMapping`: defines queryable fields and their JSON paths/types

### Proposed ApiSchema additions (small, optional)

Add an optional `relational` section to each `resourceSchema` to keep the mapping stable without enumerating every column:

```json
{
  "relational": {
    "mappingVersion": "1",
    "schemaNameOverride": "edfi",
    "nameOverrides": {
      "$.someVeryLongPropertyName...": "ShortColumnName"
    },
    "collectionNaming": {
      "$.addresses[*]": "Address",
      "$.addresses[*].periods[*]": "AddressPeriod"
    },
    "splitObjects": [
      "$.someLargeNestedObject"
    ]
  }
}
```

Intent:
- Default behavior is convention-based derivation from JSON Schema.
- Overrides are used only to handle name collisions/length limits and rare modeling edge cases.
- This avoids “flattening metadata” duplication while staying metadata-driven and non-generated.

## Write Path (POST Upsert / PUT by id)

### Common steps

1. Core validates JSON and extracts:
   - `DocumentIdentity` + `ReferentialId`
   - Document references (with `ReferentialId`s)
   - Descriptor references (with `ReferentialId`s, normalized URI)
2. Backend resolves references in bulk:
   - `dms.Identity` lookup for document refs → `DocumentId[]`
   - `dms.Identity` + `dms.Descriptor` existence check for descriptor refs
3. Backend writes within a single transaction:
   - `dms.Document` insert/update (sets `Etag`, `LastModifiedAt`, etc.)
   - `dms.Identity` upsert (primary + superclass aliases)
   - Resource root + child tables (replace strategy for collections)
   - `dms.Descriptor` upsert if the resource is a descriptor
   - Optional: recompute/refresh `dms.QueryIndex`
   - Optional: refresh `dms.DocumentCache`

### Insert vs update detection

- **Upsert (POST)**: detect by `ReferentialId`:
  - Find `DocumentId` from `dms.Identity` where `IdentityRole=Primary` for that referential id.
- **Update by id (PUT)**: detect by `DocumentUuid`:
  - Find `DocumentId` from `dms.Document` by `DocumentUuid`.

### Identity updates (AllowIdentityUpdates)

If identity changes on update:
- Update `dms.Identity` by removing/replacing the primary `ReferentialId` mapping.
- References stored as FKs (`DocumentId`) remain valid; no cascading rewrite needed.

### Concurrency (ETag)

- Compare `If-Match` header to `dms.Document.Etag`.
- Implement row-level locking per engine (UPDLOCK/HOLDLOCK in SQL Server, `FOR UPDATE` in PostgreSQL) when needed.

## Read Path (GET by id / GET query)

### GET by id

1. Resolve `DocumentUuid` → `DocumentId` via `dms.Document`.
2. If `dms.DocumentCache` enabled and present, return cached JSON (with correct `_etag`, `_lastModifiedDate`, and `id`).
3. Otherwise, reconstitute:
   - Load resource root row + child rows (batched) and assemble JSON in C# using the relational mapping derived from `ApiSchema`.
   - For descriptor properties: join to `dms.Descriptor` to output `namespace#codeValue` URI (or stored `Uri`).
   - For document references: join to referenced resource root tables to output reference identity objects (natural key values).

Reconstitution must preserve:
- Array order (via `Ordinal`)
- Required vs optional properties
- The API surface properties (`id`, `_etag`, `_lastModifiedDate`)

### Query

For v1, prefer `dms.QueryIndex` for filtering:

1. Translate query parameters to typed filters using Core’s canonicalization rules.
2. Find matching `DocumentId`s:
   - Filter `dms.Document` by `ProjectName` + `ResourceName`
   - Apply `QueryIndex` filters (intersection across fields)
   - Order by `CreatedAt` (or `DocumentId`) for stable paging; apply OFFSET/LIMIT
3. Fetch documents:
   - If cache enabled, return cached JSON for the page
   - Else, reconstitute for each `DocumentId` (batching table reads per resource)

This keeps query logic engine-agnostic and avoids Postgres-only JSON indexes.

## Delete Path (DELETE by id)

1. Resolve `DocumentUuid` → `DocumentId`.
2. Attempt delete from `dms.Document` (which cascades to resource tables and identities).
3. Rely on FK constraints from referencing resource tables to `dms.Document` / `dms.Descriptor` to prevent deleting referenced records.

Error reporting:
- SQL Server and PostgreSQL will report FK constraint violations. DMS should map the violated constraint name back to the referencing resource (deterministic FK naming) to produce a conflict response comparable to today’s `DeleteFailureReference`.

## Migration Strategy (Schema Upload/Reload)

Because schema reload can require migration, the contract becomes:

- Schema upload/reload updates the `ApiSchema` available to DMS, but the database must be migrated before that schema is “active” for a given instance.
- A migrator tool (or admin endpoint) performs:
  1. Load ApiSchema
  2. Derive required relational model (tables/columns/indexes/FKs)
  3. Diff against current DB
  4. Apply DDL changes (or emit a script to be applied)
  5. Record success in `dms.SchemaInfo`

Migration scope:
- Additive changes (new properties/collections) are straightforward.
- Renames/removals should require explicit operator intent (drop/rename hints) to avoid destructive surprises.

## SQL Server Parity Notes (Design Guardrails)

- Avoid PostgreSQL-only features in the **core** design (e.g., jsonb GIN as the only query strategy).
- Use standard types:
  - `BIGINT`, `INT`, `BIT/BOOLEAN`, `DATE`, `DATETIME2/TIMESTAMP`, `DECIMAL(p,s)`, `NVARCHAR/VARCHAR`
- Preserve array order with explicit `Ordinal INT`, not implicit ordering.
- Plan for identifier length limits and reserved words.
- Watch SQL Server limits:
  - max columns per table (1024)
  - row size constraints; prefer extension tables and 1:1 split tables for very wide objects

## Key Implications vs the Current Three-Table Design

- `dms.Reference` is no longer required for referential integrity; the database enforces integrity via FKs.
- `UpdateCascadeHandler` becomes unnecessary for identity updates because references are stored as `DocumentId` FKs and reconstituted from current referenced identities.
- Identity uniqueness is enforced in `dms.Identity` (and optionally in resource root unique constraints for direct human-debuggable keys).

## Risks / Open Questions

1. **Reconstitution cost** without JSON cache, especially for query responses returning many documents.
   - Mitigation: aggressive batching, optional cache, and/or DB-side JSON assembly in later iterations.
2. **CDC / OpenSearch / downstream integrations** that currently depend on a single “document table with JSON”.
   - Mitigation: require cache for those deployments, or introduce a materializer that emits JSON from relational changes.
3. **Delete conflict details**: replacing reverse-reference enumeration (`dms.Reference`) with FK-based detection may change which referencing resources can be reported.
4. **Schema evolution**: handling renames and destructive changes safely and predictably.

## Suggested Implementation Phases

1. **Foundational tables**: `dms.Document`, `dms.Identity`, `dms.Descriptor`, `dms.SchemaInfo`.
2. **One resource end-to-end**: implement relational mapping + CRUD + reconstitution for a small resource (and descriptors).
3. **QueryIndex**: implement `dms.QueryIndex` population and query execution for that resource.
4. **Generalize mapping**: make mapping derivation generic from ApiSchema + conventions; add minimal `relational` overrides.
5. **Migration tool**: derive/apply DDL and record `SchemaInfo`.
6. **Optional cache + integration**: `dms.DocumentCache` and any required event/materialization paths.
