# BACKEND-REDESIGN-1 Summary (Initial)

This summarizes `BACKEND-REDESIGN-1.md` (Draft): a proposal to replace the current three-table document store (`dms.Document`/`dms.Alias`/`dms.Reference`) with a **relational primary store** using **tables per resource**, while keeping DMS behavior driven by `ApiSchema.json` (no per-resource code generation).

## Goals / Constraints

- **Relational-first**: root table per resource + child tables for collections.
- **Metadata-driven**: DMS Core still validates/extracts identity and references from JSON using `ApiSchema.json`; backend flattens/reconstitutes.
- **Low coupling**: avoid hard-coding shapes; use conventions + small metadata overrides when needed.
- **No cascade updates** for identity changes: store references as `DocumentId` FKs so identity changes do not require rewriting referencing documents.
- **SQL Server parity required**: avoid Postgres-only query/indexing as the only approach.
- **Cached JSON optional**: relational tables are canonical; JSON cache exists only as an optimization/integration aid.
- **Schema reload may require migration**: schema changes are applied via an explicit migrator step.
- **Authorization out of scope**: ignore auth-related tables/columns; those will be redesigned separately.

## Core Database Model (schema: `dms`)

### `dms.Document` (canonical per-instance metadata)

One row per resource instance/document across all resources:
- `DocumentId BIGINT` (PK)
- `DocumentUuid UUID/UNIQUEIDENTIFIER` (unique; API id)
- `ProjectName`, `ResourceName`, `ResourceVersion`
- `CreatedAt`, `LastModifiedAt`, `LastModifiedTraceId`
- `Etag` and `_lastModifiedDate` support

This table replaces “JSON-as-source-of-truth” with a minimal, shared metadata row for every resource.

### `dms.ReferentialIdentity` (Alias replacement)

Maps deterministic `ReferentialId` → `DocumentId`:
- `ReferentialId` unique/PK
- `DocumentId` FK → `dms.Document` (cascade delete)
- `IdentityRole` supports primary identity vs superclass alias identity

This preserves existing polymorphic reference behavior (e.g., EducationOrganization alias ids) without a separate Alias table.

### `dms.Descriptor` (unified descriptor table)

Descriptors remain documents, but descriptor FKs target a single table:
- keyed by descriptor `DocumentId` (PK/FK to `dms.Document`)
- stores `Namespace`, `CodeValue`, `ShortDescription`, optional `Description`, `Discriminator`, `Uri`

Resource tables reference `dms.Descriptor(DocumentId)` to enforce “this FK is a descriptor” in the database.

### `dms.SchemaInfo`

Records which `ApiSchema` reload/version the database is migrated to (ties migration state to `IApiSchemaProvider.ReloadId`).

### Optional: `dms.DocumentCache`

Optional cached full JSON (Postgres `JSONB`, SQL Server `NVARCHAR(MAX)` with JSON validity checks where available):
- used for faster reads and integrations (CDC/OpenSearch)
- not required for correctness

### Optional/Recommended for parity: `dms.QueryIndex`

Cross-DB query support without relying on Postgres JSONB+GIN:
- one row per `(DocumentId, queryField, typedValue)` derived from `queryFieldMapping`
- typed columns (`ValueString`, `ValueNumber`, `ValueDate`, etc.) with indexes for equality filtering

This keeps query semantics metadata-driven and performant on both PostgreSQL and SQL Server.

## Resource Tables (schema per project)

For each project, create a DB schema (e.g., `ed-fi` → `edfi`). For each resource `R`:

### Root table `{projectSchema}.{R}`

- `DocumentId` is the PK and FK to `dms.Document`
- stores flattened scalar columns (selected from JSON schema)
- stores references as FKs:
  - document references: `..._DocumentId BIGINT` FK → `dms.Document`
  - descriptor references: `..._DescriptorId BIGINT` FK → `dms.Descriptor`
- unique constraints for natural keys (from `identityJsonPaths`)

### Collection tables

For each array:
- child table with `ParentDocumentId` FK and `Ordinal INT` for stable reconstitution ordering
- nested collections chain via parent child `Id`
- unique constraints derived from `arrayUniquenessConstraints`

### Extensions

Avoid widening core resource tables:
- extension data goes to separate 1:1 extension tables (keyed by `DocumentId`) + child tables for extension collections.

## How `ApiSchema.json` Drives the Mapping

Use existing metadata:
- `jsonSchemaForInsert` for shape and types
- `identityJsonPaths` for natural keys + uniqueness
- `documentPathsMapping` for scalar vs reference vs descriptor mapping
- `decimalPropertyValidationInfos` for decimal precision/scale
- `arrayUniquenessConstraints` for relational uniqueness in collections
- subclass metadata for superclass alias insertion
- `queryFieldMapping` for query behavior and query index population

Add only minimal optional `relational` overrides to handle:
- name collisions/identifier length
- stable naming for collections
- rare splitting/edge cases

## Runtime Behavior (CRUD + Query)

### Writes (POST upsert / PUT)

Backend responsibilities (within a transaction):
- resolve references in bulk via `dms.ReferentialIdentity` (and `dms.Descriptor` checks)
- insert/update `dms.Document` and `dms.ReferentialIdentity` (including superclass alias rows)
- upsert resource root + child tables (replace strategy for arrays)
- update `dms.Descriptor` for descriptors
- optional: refresh `dms.QueryIndex` and `dms.DocumentCache`

Identity updates are handled by changing `dms.ReferentialIdentity` mapping; no cascade rewrite is required because references are stored as `DocumentId` FKs.

### Reads (GET by id / query)

- GET by id: `DocumentUuid` → `DocumentId` via `dms.Document`, then return cache if enabled; otherwise reconstitute from relational tables using the derived relational mapping.
- Query (v1): filter/paginate using `dms.QueryIndex` + `dms.Document` (resource scoping), then return cache if enabled; otherwise batch reconstitute.

### Deletes

- delete `dms.Document` row by `DocumentUuid`; cascade removes resource rows + identities.
- FK violations enforce “cannot delete referenced resource”; DMS maps constraint names back to resource types for conflict responses.

## Migration Model

Schema upload/reload is allowed to require explicit migration:
- migrator loads `ApiSchema`, derives target model (tables/columns/indexes/FKs), diffs DB, applies DDL, records `dms.SchemaInfo`.
- additive changes are straightforward; renames/removals require explicit operator intent.

## Key Implications vs Three-Table Design

- `dms.Reference` is no longer the integrity mechanism; **DB FKs** enforce integrity.
- `UpdateCascadeHandler` becomes unnecessary for identity changes because referencing rows hold `DocumentId` FKs and reconstitution uses current referenced identities.
- `dms.ReferentialIdentity` becomes the central “natural key → document id” map, including superclass aliases.

## Risks / Open Questions (from the draft)

- Reconstitution cost without cached JSON (mitigate with batching + optional cache).
- Integrations that expect a single JSON document table (may require enabling cache or a JSON materializer pipeline).
- Delete conflict reporting may be less informative than explicit reverse-reference enumeration unless constraint naming/mapping is designed carefully.
- Schema evolution (renames/destructive changes) needs safe migration semantics.

## Suggested Implementation Phases

1. Build `dms.Document`, `dms.ReferentialIdentity`, `dms.Descriptor`, `dms.SchemaInfo`.
2. Implement end-to-end CRUD + reconstitution for one small resource + descriptors.
3. Implement `dms.QueryIndex` population and query filtering/paging.
4. Generalize relational mapping derivation from ApiSchema + conventions; add minimal override support.
5. Build the migrator to diff/apply DDL and track `SchemaInfo`.
6. Add optional `dms.DocumentCache` and any required materialization/integration support.
