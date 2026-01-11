# Backend Redesign: Data Model

## Status

Draft.

This document is the data-model deep dive for `overview.md`.

- Overview: [overview.md](overview.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Table of Contents

- [Proposed Database Model](#proposed-database-model)
- [Naming Rules (Deterministic, Cross-DB Safe)](#naming-rules-deterministic-cross-db-safe)
- [Type Mapping Defaults (Deterministic, Cross-DB Safe)](#type-mapping-defaults-deterministic-cross-db-safe)
- [Other Notes (Design Guardrails)](#other-notes-design-guardrails)

---

## Proposed Database Model

### Core tables (schema: `dms`)

##### 0) `dms.ResourceKey`

Lookup table mapping `(ProjectName, ResourceName)` to a small surrogate id (`ResourceKeyId`) used in high-churn core tables.

This reduces row width and index bloat (especially in `dms.Document`, `dms.ReferentialIdentity`, and change journals) while keeping names available via join when needed for diagnostics.

Population/seeding:
- This table is provisioned and **seeded deterministically** by the DDL generation utility from the effective `ApiSchema.json` set (core + extensions).
- `ResourceKeyId` assignments must be stable for a given `EffectiveSchemaHash` (the mapping is part of the relational mapping contract).
- DMS loads and caches the mapping per database and fails fast if it does not match the expected mapping for that `EffectiveSchemaHash`.
- `ResourceKeyId` uses `smallint` across engines (expected cardinality is far below 32k); schema provisioning should fail fast if the effective schema ever exceeds that bound.

**PostgreSQL**

```sql
CREATE TABLE dms.ResourceKey (
    ResourceKeyId smallint NOT NULL PRIMARY KEY,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(32) NOT NULL,
    CONSTRAINT UX_ResourceKey_ProjectName_ResourceName UNIQUE (ProjectName, ResourceName)
);
```

**SQL Server**

```sql
CREATE TABLE dms.ResourceKey (
    ResourceKeyId smallint NOT NULL
        CONSTRAINT PK_ResourceKey PRIMARY KEY CLUSTERED,
    ProjectName nvarchar(256) NOT NULL,
    ResourceName nvarchar(256) NOT NULL,
    ResourceVersion nvarchar(32) NOT NULL,
    CONSTRAINT UX_ResourceKey_ProjectName_ResourceName UNIQUE (ProjectName, ResourceName)
);
```

##### 1) `dms.Document`

Canonical metadata per persisted resource instance. One row per document, regardless of resource type.

Update tracking note: `reference/design/backend-redesign/update-tracking.md` defines representation-sensitive `_etag/_lastModifiedDate` (and Change Query support) using derived tokens and journals (no write-time representation fan-out). This table stores the required per-document token columns used for read-time derivation.

**PostgreSQL**

```sql
CREATE TABLE dms.Document (
    DocumentId bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    DocumentUuid uuid NOT NULL,
    ResourceKeyId smallint NOT NULL REFERENCES dms.ResourceKey (ResourceKeyId),

    -- Update tracking tokens (see reference/design/backend-redesign/update-tracking.md)
    ContentVersion bigint NOT NULL DEFAULT 1,
    IdentityVersion bigint NOT NULL DEFAULT 1,
    ContentLastModifiedAt timestamp with time zone NOT NULL DEFAULT now(),
    IdentityLastModifiedAt timestamp with time zone NOT NULL DEFAULT now(),

    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT UX_Document_DocumentUuid UNIQUE (DocumentUuid)
);

CREATE INDEX IX_Document_ResourceKeyId_DocumentId
    ON dms.Document (ResourceKeyId, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.Document (
    DocumentId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_Document PRIMARY KEY CLUSTERED,

    DocumentUuid uniqueidentifier NOT NULL,

    ResourceKeyId smallint NOT NULL,

    -- Update tracking tokens (see reference/design/backend-redesign/update-tracking.md)
    ContentVersion bigint NOT NULL CONSTRAINT DF_Document_ContentVersion DEFAULT (1),
    IdentityVersion bigint NOT NULL CONSTRAINT DF_Document_IdentityVersion DEFAULT (1),
    ContentLastModifiedAt datetime2(7) NOT NULL CONSTRAINT DF_Document_ContentLastModifiedAt DEFAULT (sysutcdatetime()),
    IdentityLastModifiedAt datetime2(7) NOT NULL CONSTRAINT DF_Document_IdentityLastModifiedAt DEFAULT (sysutcdatetime()),

    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_Document_CreatedAt DEFAULT (sysutcdatetime()),

    CONSTRAINT UX_Document_DocumentUuid UNIQUE (DocumentUuid),
    CONSTRAINT FK_Document_ResourceKey FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId)
);

CREATE INDEX IX_Document_ResourceKeyId_DocumentId
    ON dms.Document (ResourceKeyId, DocumentId);
```

Notes:
- `DocumentUuid` remains stable across identity updates; identity-based upserts map to it via `dms.ReferentialIdentity` for **all** identities (self-contained, reference-bearing, and abstract/superclass aliases), because `dms.ReferentialIdentity` is maintained transactionally (including cascades) on identity changes.
- `ResourceKeyId` identifies the document’s concrete resource type; use `dms.ResourceKey` for `(ProjectName, ResourceName)` when needed (diagnostics, CDC metadata).
- Update tracking columns (brief semantics; see `reference/design/backend-redesign/update-tracking.md` for the normative rules):
  - `ContentVersion` / `ContentLastModifiedAt`: bump when the document’s own persisted content changes.
  - `IdentityVersion` / `IdentityLastModifiedAt`: bump when the document’s identity/URI projection changes (including strict identity closure recompute impacts).
  - API `_etag`, `_lastModifiedDate`, and per-item `ChangeVersion` are derived at read time from these tokens plus dependency tokens.
- Time semantics: store timestamps as UTC instants. In PostgreSQL, use `timestamp with time zone` and format response values as UTC (e.g., `...Z`). In SQL Server, use `datetime2` with UTC writers (e.g., `sysutcdatetime()`).
- Authorization is intentionally out of scope for this redesign phase.

##### 1a) `dms.ChangeVersionSequence`

Global monotonic `bigint` sequence used to allocate update tracking stamps (for `ContentVersion`, `IdentityVersion`, and journal `ChangeVersion`). See `reference/design/backend-redesign/update-tracking.md` for stamping rules.

**PostgreSQL**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence AS bigint START WITH 1 INCREMENT BY 1;
```

**SQL Server**

```sql
CREATE SEQUENCE dms.ChangeVersionSequence
    AS bigint
    START WITH 1
    INCREMENT BY 1;
```

##### 1b) `dms.DocumentChangeEvent` (append-only journal)

Append-only journal of per-document representation-affecting changes (local content and/or identity/URI projection). Used to support future Change Query APIs. See `reference/design/backend-redesign/update-tracking.md` for the selection algorithm and retention guidance.

Columns:
- `ChangeVersion`: derived per-document “local change” stamp (recommended: `max(ContentVersion, IdentityVersion)`).
- `DocumentId`: changed document.
- `ResourceKeyId`: resource key for filtering change queries.
- `CreatedAt`: journal insert time (operational/auditing).

**PostgreSQL**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ResourceKeyId smallint NOT NULL REFERENCES dms.ResourceKey (ResourceKeyId),
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);

CREATE INDEX IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent (ResourceKeyId, ChangeVersion, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.DocumentChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_DocumentChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_DocumentChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_DocumentChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_DocumentChangeEvent_ResourceKey FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId)
);

CREATE INDEX IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion
    ON dms.DocumentChangeEvent (ResourceKeyId, ChangeVersion, DocumentId);
```

Recommended population: enforce journal insertion with database triggers on `dms.Document` when token columns change (to avoid “forgotten journal write” bugs and to naturally cover bulk/closure updates); see `reference/design/backend-redesign/update-tracking.md`.

##### 1c) `dms.IdentityChangeEvent` (append-only journal)

Append-only journal of identity/URI projection changes for a document. Used to find indirectly impacted parents via `dms.ReferenceEdge` during Change Query selection and for targeted projection rebuilds.

Columns:
- `ChangeVersion`: the new `IdentityVersion` stamp for the changed document.
- `DocumentId`: the document whose identity/URI projection changed.
- `CreatedAt`: journal insert time.

**PostgreSQL**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY (ChangeVersion, DocumentId)
);
```

**SQL Server**

```sql
CREATE TABLE dms.IdentityChangeEvent (
    ChangeVersion bigint NOT NULL,
    DocumentId bigint NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_IdentityChangeEvent_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_IdentityChangeEvent PRIMARY KEY CLUSTERED (ChangeVersion, DocumentId),
    CONSTRAINT FK_IdentityChangeEvent_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);
```

##### 2) `dms.ReferentialIdentity`

Maps `ReferentialId` → `DocumentId` (replaces today’s `dms.Alias`), including superclass aliases used for polymorphic references.

Rationale for retaining `ReferentialId` in this redesign: see [overview.md#Why keep ReferentialId](overview.md#why-keep-referentialid).

**PostgreSQL**

```sql
CREATE TABLE dms.ReferentialIdentity (
    ReferentialId uuid NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL REFERENCES dms.ResourceKey (ResourceKeyId),
    CONSTRAINT PK_ReferentialIdentity PRIMARY KEY (ReferentialId),
    CONSTRAINT FK_ReferentialIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_ReferentialIdentity_DocumentId_ResourceKey UNIQUE (DocumentId, ResourceKeyId)
);

CREATE INDEX IX_ReferentialIdentity_DocumentId ON dms.ReferentialIdentity (DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.ReferentialIdentity (
    ReferentialId uniqueidentifier NOT NULL,
    DocumentId bigint NOT NULL,
    ResourceKeyId smallint NOT NULL,
    CONSTRAINT PK_ReferentialIdentity PRIMARY KEY NONCLUSTERED (ReferentialId),
    CONSTRAINT FK_ReferentialIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_ReferentialIdentity_ResourceKey FOREIGN KEY (ResourceKeyId)
        REFERENCES dms.ResourceKey (ResourceKeyId),
    CONSTRAINT UX_ReferentialIdentity_DocumentId_ResourceKey UNIQUE CLUSTERED (DocumentId, ResourceKeyId)
);
```

Database Specific Differences:
- The logical shape is identical across engines (UUID `ReferentialId` → `DocumentId` + `ResourceKeyId`).
- The physical DDL will differ slightly for performance: SQL Server should not cluster on a randomly-distributed UUID.

Operational considerations:
- `ReferentialId` is a deterministic UUIDv5 and is effectively randomly distributed for index insertion. The primary operational concern is **write amplification** (page splits, fragmentation/bloat), not point-lookup speed.
- **SQL Server**:
  - Keep the UUID key as **NONCLUSTERED** (as shown) and use a sequential clustered key (e.g., `(DocumentId, ResourceKeyId)`).
  - Consider a lower `FILLFACTOR` (e.g., 80–90) on the UUID index to reduce page splits; monitor fragmentation and rebuild/reorganize as needed.
- **PostgreSQL**:
  - B-tree point lookups on UUID are fine; manage bloat under high write rates with index/table `fillfactor` (e.g., 80–90), healthy autovacuum settings/monitoring, and periodic `REINDEX` when warranted.
  - If sustained ingest is extreme, consider hash partitioning `dms.ReferentialIdentity` by `ReferentialId` (e.g., 8–32 partitions) to reduce contention and make maintenance cheaper.


Critical invariants:
- **Uniqueness** of `ReferentialId` enforces “one natural identity maps to one document” for **all** identities (self-contained identities, reference-bearing identities, and descriptor URIs). This requires `dms.ReferentialIdentity` to be maintained transactionally on identity changes, including cascading recompute when upstream identity components change.
- The resource root table’s natural-key unique constraint (FK `..._DocumentId` columns + scalar identity parts) remains a recommended relational guardrail, but identity-based resolution/upsert uses `dms.ReferentialIdentity`.
- A document has **at most 2** referential ids:
  - the **primary** referential id for the document’s concrete `ResourceKeyId` (`(ProjectName, ResourceName)` in `dms.ResourceKey`)
  - an optional **superclass/abstract alias** referential id for polymorphic references (when `isSubclass: true`) using the superclass/abstract `ResourceKeyId`
- Subclass writes insert both:
  - primary `ReferentialId` (subclass resource name)
  - superclass alias `ReferentialId` (superclass resource name)

This preserves current polymorphic reference behavior without a separate Alias table.

##### 3) `dms.Descriptor` (unified)

Descriptors are still documents, but we maintain a unified descriptor table keyed by the descriptor document’s `DocumentId`. This makes descriptor FK enforcement possible without per-descriptor tables.

**PostgreSQL**

```sql
CREATE TABLE dms.Descriptor (
    DocumentId bigint NOT NULL,
    Namespace varchar(255) NOT NULL,
    CodeValue varchar(50) NOT NULL,
    ShortDescription varchar(75) NOT NULL,
    Description varchar(1024) NULL,
    Discriminator varchar(128) NOT NULL,
    Uri varchar(306) NOT NULL,
    CONSTRAINT PK_Descriptor PRIMARY KEY (DocumentId),
    CONSTRAINT FK_Descriptor_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_Descriptor_Uri_Discriminator UNIQUE (Uri, Discriminator)
);

CREATE INDEX IX_Descriptor_Uri_Discriminator ON dms.Descriptor (Uri, Discriminator);
```

Descriptor references (recommended base design):
- Use an FK directly to `dms.Descriptor(DocumentId)` to guarantee “this is a descriptor” at the DB level.
- Resolve descriptor URI strings by computing the descriptor `ReferentialId` (descriptor resource type from `ApiSchema` + normalized URI, per Core) and looking up `DocumentId` via `dms.ReferentialIdentity` (use `dms.Descriptor` for expansion/type diagnostics, not for resolution).

If DB-level enforcement of “descriptor must be of type X” becomes necessary later we can add checks that the referenced `dms.Descriptor.Discriminator` is the expected type for that FK column (derived from `ApiSchema`).

Descriptor immutability (assumption for this redesign):
- Descriptor documents are treated as **immutable reference data** after creation (no identity/URI updates, and no representation-affecting updates).
- Because descriptors cannot change, descriptor FK dependencies do **not** participate in any reverse-index-driven cascade, and are **excluded** from `dms.ReferenceEdge` by design.

##### 4) `dms.EffectiveSchema` + `dms.SchemaComponent`

Tracks which **effective schema** (core `ApiSchema.json` + extension `ApiSchema.json` files) the database schema is provisioned for, and records the **exact project versions** present in that effective schema. On first use of a given database connection string, DMS uses this to validate that it has a matching mapping set for the database’s recorded fingerprint (cached per connection string; see **EffectiveSchemaHash Calculation** below).

Design decision for this redesign:
- `dms.EffectiveSchema` is a **single-row current-state** table (not an append-only history table).
- `dms.SchemaComponent` rows are keyed by `EffectiveSchemaHash` to allow deterministic inserts without needing to look up a surrogate `EffectiveSchemaId`.

**PostgreSQL**

```sql
CREATE TABLE dms.EffectiveSchema (
    EffectiveSchemaSingletonId smallint NOT NULL PRIMARY KEY,
    ApiSchemaFormatVersion varchar(64) NOT NULL,
    EffectiveSchemaHash varchar(64) NOT NULL,
    ResourceKeyCount smallint NOT NULL,
    ResourceKeySeedHash varchar(64) NOT NULL,
    AppliedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT CK_EffectiveSchema_Singleton CHECK (EffectiveSchemaSingletonId = 1),
    CONSTRAINT UX_EffectiveSchema_EffectiveSchemaHash UNIQUE (EffectiveSchemaHash)
);

CREATE TABLE dms.SchemaComponent (
    EffectiveSchemaHash varchar(64) NOT NULL
        REFERENCES dms.EffectiveSchema (EffectiveSchemaHash) ON DELETE CASCADE,
    ProjectEndpointName varchar(128) NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ProjectVersion varchar(32) NOT NULL,
    IsExtensionProject boolean NOT NULL,
    CONSTRAINT PK_SchemaComponent PRIMARY KEY (EffectiveSchemaHash, ProjectEndpointName)
);
```

##### EffectiveSchemaHash Calculation

`EffectiveSchemaHash` is a deterministic fingerprint of the configured schema set (core + extensions) as it affects relational mapping. It must be stable across file ordering, whitespace, and JSON property ordering.

Determinism contract:
- For a fixed effective schema set and relational mapping version, the computed `EffectiveSchemaHash` string (lowercase hex, 64 chars) is **byte-for-byte stable** across runs and environments.

Recommendations:
- Use `SHA-256` and store as lowercase hex (64 chars).
- Require a single `ApiSchemaFormatVersion` across core + extensions (fail fast if they differ).
- Validate that each configured `ApiSchema.json` file contributes exactly one `projectSchema` entry.
- Exclude OpenAPI payloads from hashing to avoid churn and reduce input size:
  - `projectSchema.openApiBaseDocuments`
  - `projectSchema.resourceSchemas[*].openApiFragments`
  - `projectSchema.abstractResources[*].openApiFragment`
- Keep arrays in-order (many arrays are semantically ordered), but sort objects by property name recursively.
- Include a DMS-controlled constant “relational mapping version” so that a breaking change in mapping conventions forces a mismatch even if ApiSchema content is unchanged.

Algorithm (suggested):
1. Parse the configured core + extension ApiSchema files.
2. For each file, extract:
   - `ApiSchemaFormatVersion` (`apiSchemaVersion`)
   - the `projectSchema` entry (after removing OpenAPI payloads)
3. Sort projects by `ProjectEndpointName`.
4. Compute `ProjectHash = SHA-256(canonicalJson(projectSchema))` for each project.
5. Compute `EffectiveSchemaHash = SHA-256(manifestString)` where `manifestString` is:
   - a constant header (e.g., `dms-effective-schema-hash:v1`)
   - a constant mapping version (e.g., `relational-mapping:v3`)
   - `ApiSchemaFormatVersion`
   - one line per project: `ProjectEndpointName|ProjectName|ProjectVersion|IsExtensionProject|ProjectHash`

Pseudocode:

```text
const HashVersion = "dms-effective-schema-hash:v1"
const RelationalMappingVersion = "relational-mapping:v3"

projects = []
apiSchemaFormatVersion = null
for file in configuredApiSchemaFiles:
  json = parse(file)
  apiSchemaFormatVersion = apiSchemaFormatVersion ?? json.apiSchemaVersion
  assert(apiSchemaFormatVersion == json.apiSchemaVersion)
  projectSchema = json.projectSchema
  projectEndpointName = projectSchema.projectEndpointName
  projectSchema = removeOpenApiPayloads(projectSchema)
  projectHash = sha256hex(canonicalizeJson(projectSchema))
  projects.add({
    projectEndpointName,
    projectName: projectSchema.projectName,
    projectVersion: projectSchema.projectVersion,
    isExtensionProject: projectSchema.isExtensionProject,
    projectHash
  })

projects = sortBy(projects, p => p.projectEndpointName)

manifest =
  HashVersion + "\n" +
  RelationalMappingVersion + "\n" +
  "apiSchemaFormatVersion=" + apiSchemaFormatVersion + "\n" +
  join(projects, "\n",
    p.projectEndpointName + "|" +
    p.projectName + "|" +
    p.projectVersion + "|" +
    p.isExtensionProject + "|" +
    p.projectHash)

effectiveSchemaHash = sha256hex(utf8(manifest))
```

##### 5) `dms.ReferenceEdge` (reverse reference index for cascades)

A small, relational reverse index of **“this document references that document”**, maintained on writes.

Important properties:
- **Required for derived artifacts and reverse lookups**:
  - strict `dms.ReferentialIdentity` cascading recompute when upstream identity components change (`IsIdentityComponent=true`)
  - outbound dependency enumeration for derived `_etag/_lastModifiedDate` and Change Query processing (see `reference/design/backend-redesign/update-tracking.md`)
- **Stores resolved `DocumentId`s only**: no `ReferentialId` resolution, no partition keys, no alias joins.
- **Excludes descriptor references**: this table indexes outgoing **document** references only (`..._DocumentId` FKs). Descriptor FKs (`..._DescriptorId`) are excluded because descriptor documents are immutable (see `dms.Descriptor` above).
- **Coverage requirement is correctness-critical**:
  - DMS must record **all** outgoing document references, including those inside nested collections/child tables.
  - If edge maintenance fails, the write must fail (otherwise strict identity maintenance and update tracking can become incorrect).
- **Collapsed edge granularity**:
  - This design stores **one row per `(ParentDocumentId, ChildDocumentId)`** (not “per reference site/path”) to reduce write amplification and index churn.
  - The `IsIdentityComponent` flag is the **OR** of all reference sites in the parent document that reference the same `ChildDocumentId`.

Primary uses:
- **`dms.ReferentialIdentity` identity cascades**: when a referenced document’s identity changes, update `dms.ReferentialIdentity` for documents whose identities depend on it (reverse traversal of `IsIdentityComponent=true` edges).
- **Update tracking / Change Queries**: expand changed dependencies (`ChildDocumentId → ParentDocumentId`) and scan outbound dependencies (`ParentDocumentId → ChildDocumentId`) when computing derived representation metadata and selecting Change Query candidates (see `reference/design/backend-redesign/update-tracking.md`).

##### DDL (PostgreSQL)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    ChildDocumentId  bigint NOT NULL
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    IsIdentityComponent boolean NOT NULL,

    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),

    CONSTRAINT PK_ReferenceEdge PRIMARY KEY (ParentDocumentId, ChildDocumentId)
);

-- Reverse lookup: "who references Child X?" (also supports identity-dependency closure expansion via IsIdentityComponent)
CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId, IsIdentityComponent)
    INCLUDE (ParentDocumentId);
```

Notes on `IsIdentityComponent`:
- Compute it from ApiSchema as “this reference contributes to any stored identity for the parent”, including superclass/abstract alias identity (for `isSubclass=true` resources where DMS stores an alias row in `dms.ReferentialIdentity`).
- When a parent document references the same child document in multiple places, the stored value is `true` if **any** of those reference sites is an identity component.
- Update tracking uses **all** edges as representation dependencies (not just `IsIdentityComponent=true`) when deriving `_etag/_lastModifiedDate` and selecting Change Query candidates; see `reference/design/backend-redesign/update-tracking.md`.

##### DDL (SQL Server)

```sql
CREATE TABLE dms.ReferenceEdge (
    ParentDocumentId bigint NOT NULL,
    ChildDocumentId  bigint NOT NULL,
    IsIdentityComponent bit NOT NULL,
    CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_ReferenceEdge_CreatedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_ReferenceEdge PRIMARY KEY CLUSTERED (ParentDocumentId, ChildDocumentId),
    CONSTRAINT FK_ReferenceEdge_Parent FOREIGN KEY (ParentDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT FK_ReferenceEdge_Child FOREIGN KEY (ChildDocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);

CREATE INDEX IX_ReferenceEdge_ChildDocumentId
    ON dms.ReferenceEdge (ChildDocumentId, IsIdentityComponent)
    INCLUDE (ParentDocumentId);
```

##### ReferenceEdge FAQ

**We’re moving away from the three-table design because `dms.Reference` is slow/high churn. Won’t this be the same?**

Key differences:
- It stores **resolved `DocumentId` pairs**, so there is no `ReferentialId → DocumentId` join (no Alias-equivalent lookup), and no partition keys.
- It can be maintained with a **low-churn diff** so “update that does not change references” performs **0 row writes** to this table.
- It is not used for referential integrity; it exists for reverse lookups and for identity/version cascades (`IsIdentityComponent=true`).

**Doesn’t this require the same InsertReferences pattern?**

We can use a similar *concept* (stage + diff) to avoid churn, but it is far simpler than today’s `InsertReferences` because:
- inputs are already-resolved `DocumentId`s (no Alias joins, no invalid `ReferentialId` reporting),
- there is no partition-routing logic,
- the table is keyed by the parent `DocumentId` (single delete scope), and
- the diff is between two small sets of `ChildDocumentId`s (with associated `IsIdentityComponent` flags, aggregated by OR when a parent references the same child multiple times).

##### 6) `dms.IdentityLock` (lock orchestration for strict identity correctness)

To support **strict synchronous** `dms.ReferentialIdentity` maintenance (including cascading referential-id recompute for reference-bearing and abstract identities), the backend needs a stable per-document row to lock.

This design uses a dedicated lock table:

**PostgreSQL / SQL Server (logical)**

```sql
CREATE TABLE dms.IdentityLock (
    DocumentId bigint NOT NULL PRIMARY KEY
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE
);
```

Rules:
- Every `dms.Document` insert also inserts the same `DocumentId` into `dms.IdentityLock` (same transaction).
- Identity-changing writes and any write that binds to other documents’ identity values must use row locks on `dms.IdentityLock` (shared/update) to prevent “stale-at-birth” referential ids and to make identity cascades phantom-safe (details in Write Path).
- Concurrency is controlled by row locks on the affected documents’ `dms.IdentityLock` rows + lock ordering + deadlock retry.

Notes:
- See [transactions-and-concurrency.md](transactions-and-concurrency.md) for the normative lock ordering and closure-locking algorithms (including recommended lock query shapes).

### Resource tables (schema per project)

Each project gets a schema derived from `ProjectEndpointName` (e.g., `ed-fi` → `edfi`, `tpdm` → `tpdm`). Core `dms.*` tables remain in `dms`.

For each resource `R`:

#### Root table: `{schema}.{R}`

One row per document; PK is `DocumentId` (shared surrogate key).

Typical structure:
- `DocumentId BIGINT` **PK/FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- Natural key columns (from `identityJsonPaths`) → unique constraint. For identity elements that come from a document reference object, the unique constraint uses the corresponding `..._DocumentId` FK column rather than duplicating the referenced natural-key fields.
- Scalar columns for top-level non-collection properties
- Reference FK columns:
  - Non-descriptor references (concrete target): `..._DocumentId BIGINT` FK → `{schema}.{TargetResource}(DocumentId)` to enforce existence *and* resource type
  - Non-descriptor references (polymorphic/abstract target): `..._DocumentId BIGINT` FK → `dms.Document(DocumentId)` plus optional discriminator/membership enforcement (see Reference Validation in [transactions-and-concurrency.md](transactions-and-concurrency.md))
  - Descriptor references: `..._DescriptorId BIGINT` FK → `dms.Descriptor(DocumentId)`

#### Child tables for collections

For each JSON array of objects/scalars under the resource:

`{schema}.{R}_{CollectionPath}` (name derived deterministically; see Naming Rules)
- Parent key columns (for root-level arrays this is typically `<RootResource>_DocumentId BIGINT` FK → `{schema}.{R}(DocumentId)` ON DELETE CASCADE)
- `Ordinal INT NOT NULL` (preserve array order for reconstitution)
- Primary key is **composite** on parent key + ordinal (e.g., `PRIMARY KEY (School_DocumentId, Ordinal)`)
- Scalar columns for the element object
- Reference/descriptor FK columns as above
- Unique constraints derived from `arrayUniquenessConstraints` when available (often separate from the PK)

Nested collections add the parent’s ordinal(s) into the key and FK, avoiding generated IDs and `RETURNING`/`OUTPUT` key-capture round trips.

#### Abstract identity views for polymorphic references (union view approach)

Some Ed-Fi references target **abstract resources** (polymorphic references), notably:
- `EducationOrganization` (e.g., `educationOrganizationReference`)
- `GeneralStudentProgramAssociation` (e.g., `generalStudentProgramAssociationReference`)

Abstract resources have **no physical root table**, but DMS must still:
- validate membership/type for abstract-target references (application-level), and
- **reconstitute reference identity objects** on reads (e.g., output `educationOrganizationId`, not `schoolId`).

This design uses a narrow **union view per abstract resource**:

- View name: `{schema}.{AbstractResource}_View` (deterministic; validated as part of the expected schema shape)
- Columns:
  - `DocumentId` (the referenced document)
  - `Discriminator` (concrete resource name; optional but recommended for diagnostics)
  - one column per abstract identity element (typed), using the abstract identity field names
- Rows:
  - `UNION ALL` over each concrete resource table that participates in the hierarchy, projecting:
    - `DocumentId`
    - abstract identity columns (including identity renames, e.g. `School.SchoolId AS EducationOrganizationId`)
    - discriminator constant (e.g., `'School'`)

**Why a view**
- Works well for small, stable hierarchies.
- Requires no triggers and no additional write-path maintenance.
- Provides a single join target for identity projection during reconstitution.

**How DMS derives the view definition (metadata-driven)**
- Source of truth is `ApiSchema.json`:
  - `projectSchema.abstractResources[A].identityPathOrder` defines the abstract identity field names and order.
  - `resourceSchema.isSubclass` + `superclass*` fields define which concrete resources belong to `A` and how identity renames map.
- DMS chooses a canonical SQL type for each abstract identity field from the participating concrete resources’ derived identity column types and casts each `SELECT` accordingly to keep the union portable across engines. (`abstractResources[A].openApiFragment` is excluded from `EffectiveSchemaHash` and must not affect schema derivation.)
- For each concrete resource `R` in the hierarchy, DMS derives, per abstract identity field:
  - If `R.identityJsonPaths` contains `$.{fieldName}`, use the corresponding root-table column directly.
  - Else, if `R.isSubclass=true` and `R.superclassIdentityJsonPath == $.{fieldName}`, use the concrete identity column(s) from `R.identityJsonPaths` (identity rename case; e.g., `$.schoolId` → `EducationOrganizationId`).
- If a concrete type cannot supply all abstract identity fields, startup schema validation fails fast (schema mismatch).

**PostgreSQL example: `EducationOrganization_View`**

```sql
CREATE OR REPLACE VIEW edfi.EducationOrganization_View AS
SELECT
    s.DocumentId,
    s.SchoolId AS EducationOrganizationId,
    'School'::varchar(256) AS Discriminator
FROM edfi.School s
UNION ALL
SELECT
    lea.DocumentId,
    lea.LocalEducationAgencyId AS EducationOrganizationId,
    'LocalEducationAgency'::varchar(256) AS Discriminator
FROM edfi.LocalEducationAgency lea
UNION ALL
SELECT
    sea.DocumentId,
    sea.StateEducationAgencyId AS EducationOrganizationId,
    'StateEducationAgency'::varchar(256) AS Discriminator
FROM edfi.StateEducationAgency sea;
```

**SQL Server example: `EducationOrganization_View`**

```sql
CREATE OR ALTER VIEW edfi.EducationOrganization_View AS
SELECT
    s.DocumentId,
    s.SchoolId AS EducationOrganizationId,
    CAST('School' AS nvarchar(256)) AS Discriminator
FROM edfi.School s
UNION ALL
SELECT
    lea.DocumentId,
    lea.LocalEducationAgencyId AS EducationOrganizationId,
    CAST('LocalEducationAgency' AS nvarchar(256)) AS Discriminator
FROM edfi.LocalEducationAgency lea
UNION ALL
SELECT
    sea.DocumentId,
    sea.StateEducationAgencyId AS EducationOrganizationId,
    CAST('StateEducationAgency' AS nvarchar(256)) AS Discriminator
FROM edfi.StateEducationAgency sea;
```

**How the view is used**
- **Write-time resolution**: abstract references resolve via `dms.ReferentialIdentity` (superclass aliases) and do not depend on the view.
- **Read-time identity projection**: when reconstituting an abstract-target reference object, join to `{AbstractResource}_View` by `DocumentId` to fetch the abstract identity fields.
- **Membership/type validation (standard)**: to ensure a `..._DocumentId` FK to `dms.Document` actually belongs to the allowed hierarchy, validate `EXISTS (SELECT 1 FROM {AbstractResource}_View WHERE DocumentId=@id)` (batch when possible).

Operational note:
- Adding a new concrete subtype requires updating the view definition in the database (startup validation will fail fast if it does not match).
- If view performance ever becomes a bottleneck, consider materialization (PostgreSQL materialized view; SQL Server indexed view) as a later, measured optimization.

#### PostgreSQL examples (Student, School, StudentSchoolAssociation)

```sql
CREATE SCHEMA IF NOT EXISTS edfi;

CREATE TABLE IF NOT EXISTS edfi.Student (
    DocumentId       bigint PRIMARY KEY
                     REFERENCES dms.Document(DocumentId) ON DELETE CASCADE,

    StudentUniqueId  varchar(32)  NOT NULL,
    FirstName        varchar(75)  NOT NULL,
    LastSurname      varchar(75)  NOT NULL,
    BirthDate        date         NULL,

    CONSTRAINT UX_Student_StudentUniqueId UNIQUE (StudentUniqueId)
);

-- Descriptor references are stored as FKs directly to dms.Descriptor.
-- The expected descriptor type is validated via dms.Descriptor.Discriminator (application-level, or triggers if desired).

CREATE TABLE IF NOT EXISTS edfi.School (
    DocumentId             bigint PRIMARY KEY
                           REFERENCES dms.Document(DocumentId) ON DELETE CASCADE,

    SchoolId               int          NOT NULL,
    NameOfInstitution      varchar(255) NOT NULL,
    ShortNameOfInstitution varchar(75)  NULL,
    SchoolTypeDescriptor_DescriptorId bigint NULL
                           REFERENCES dms.Descriptor(DocumentId),

    CONSTRAINT UX_School_SchoolId UNIQUE (SchoolId)
);

-- Example collection table: School has a collection of GradeLevelDescriptor values
-- (e.g., 1st, 2nd, 3rd, ...) stored as rows. Ordinal preserves client order.
CREATE TABLE IF NOT EXISTS edfi.SchoolGradeLevel (
    School_DocumentId bigint NOT NULL
                      REFERENCES edfi.School(DocumentId) ON DELETE CASCADE,

    Ordinal int NOT NULL,

    GradeLevelDescriptor_DescriptorId bigint NOT NULL
                      REFERENCES dms.Descriptor(DocumentId),

    CONSTRAINT PK_SchoolGradeLevel PRIMARY KEY (School_DocumentId, Ordinal),
    CONSTRAINT UX_SchoolGradeLevel UNIQUE (School_DocumentId, GradeLevelDescriptor_DescriptorId)
);

-- Example nested collection with composite keys:
-- School -> addresses[*] -> periods[*]
CREATE TABLE IF NOT EXISTS edfi.SchoolAddress (
    School_DocumentId bigint NOT NULL
                      REFERENCES edfi.School(DocumentId) ON DELETE CASCADE,

    Ordinal int NOT NULL,

    AddressTypeDescriptor_DescriptorId bigint NOT NULL
                      REFERENCES dms.Descriptor(DocumentId),

    StreetNumberName varchar(150) NULL,
    City varchar(30) NULL,

    CONSTRAINT PK_SchoolAddress PRIMARY KEY (School_DocumentId, Ordinal),
    CONSTRAINT UX_SchoolAddress UNIQUE (School_DocumentId, AddressTypeDescriptor_DescriptorId)
);

CREATE TABLE IF NOT EXISTS edfi.SchoolAddressPeriod (
    School_DocumentId bigint NOT NULL,
    AddressOrdinal int NOT NULL,
    Ordinal int NOT NULL,

    BeginDate date NOT NULL,
    EndDate date NULL,

    CONSTRAINT PK_SchoolAddressPeriod PRIMARY KEY (School_DocumentId, AddressOrdinal, Ordinal),
    CONSTRAINT FK_SchoolAddressPeriod_SchoolAddress FOREIGN KEY (School_DocumentId, AddressOrdinal)
        REFERENCES edfi.SchoolAddress (School_DocumentId, Ordinal) ON DELETE CASCADE,
    CONSTRAINT UX_SchoolAddressPeriod UNIQUE (School_DocumentId, AddressOrdinal, BeginDate)
);

CREATE TABLE IF NOT EXISTS edfi.StudentSchoolAssociation (
    DocumentId         bigint PRIMARY KEY
                       REFERENCES dms.Document(DocumentId) ON DELETE CASCADE,

    Student_DocumentId bigint NOT NULL
                       REFERENCES edfi.Student(DocumentId),

    School_DocumentId  bigint NOT NULL
                       REFERENCES edfi.School(DocumentId),

    EntryDate          date   NOT NULL,
    ExitWithdrawDate   date   NULL,

    CONSTRAINT UX_StudentSchoolAssociation UNIQUE (Student_DocumentId, School_DocumentId, EntryDate)
);

CREATE INDEX IF NOT EXISTS IX_SSA_StudentDocumentId ON edfi.StudentSchoolAssociation(Student_DocumentId);
CREATE INDEX IF NOT EXISTS IX_SSA_SchoolDocumentId  ON edfi.StudentSchoolAssociation(School_DocumentId);
```

### Extensions

Extension projects and resource extensions should follow a consistent “separate schema, separate table hierarchy” pattern without merging extension columns into the core resource table:

- Extension tables live in the **extension project schema** (derived from `ProjectEndpointName`, e.g., `sample`, `tpdm`), not in the core project schema.
- Table-name patterns follow the old flattening design:
  - extension root table: `{ResourceName}Extension` (e.g., `sample.ContactExtension`)
  - extension collection tables: `{ResourceName}Extension{CollectionSuffix}` (e.g., `sample.ContactExtensionAddress`)
- For `isResourceExtension: true` resources (and other `_ext` sites), store extension fields in:
  - a 1:1 extension root row keyed by `DocumentId` (FK to the base resource root table), and
  - extension scope/collection tables keyed to the same composite keys as the base scope they extend (root + ordinals) so extension rows attach deterministically and cascade deletes cleanly.

See [extensions.md](extensions.md) for the normative mapping rules for `_ext` (resource + common-type extensions, nested collections, and multiple extension projects).



## Naming Rules (Deterministic, Cross-DB Safe)

To keep schema management tractable and avoid rename cascades, physical names must be deterministic:

- **PascalCase everywhere**: table/column/view/constraint/index names are PascalCase (with `_` separators where required by suffix conventions) across both PostgreSQL and SQL Server. Table names are **not pluralized**.

### 1) Schema names

- Fixed schemas:
  - `dms` (core tables)
  - `auth` (authorization companion objects; not fully specified in this redesign)
- Project schemas (core + extensions): derived from `projectSchema.projectEndpointName`:
  - `lowercase`
  - remove all non-alphanumerics
  - if the result does not start with a letter, prefix with `p`
  - example: `ed-fi` → `edfi`
- Validation:
  - schema names must be unique after normalization; fail fast if two projects collapse to the same schema name

### 2) Identifier quoting (required for PostgreSQL PascalCase)

- PostgreSQL: always emit quoted identifiers (double quotes) so names are stored exactly as generated:
  - `"edfi"."School"`, `"StudentUniqueId"`, `"IX_SSA_StudentDocumentId"`
- SQL Server: always emit bracketed identifiers:
  - `[edfi].[School]`, `[StudentUniqueId]`, `[IX_SSA_StudentDocumentId]`

Always-quote is the simplest cross-engine rule:
- preserves PascalCase in PostgreSQL
- avoids reserved-word edge cases without maintaining a reserved-word list

Note: SQL examples in this directory may omit quoting for readability. The DDL generator and compiled SQL plans must follow the quoting rules above.

### 3) Table names (no pluralization)

- Root table:
  - base name: `resourceSchema.relational.rootTableNameOverride` when present, otherwise MetaEd `resourceName` (PascalCase)
  - physical: `{ProjectSchema}.{BaseName}`
- Child collection tables:
  - base name: `{RootBaseName}{CollectionSuffix}`
  - suffix derives from the array property path in JSON order (root to leaf), with one segment per collection:
    - default segment base name: PascalCase of the array property name after deterministic singularization:
      - if ends with `ies`, replace with `y` (`categories` → `category`)
      - else if ends with `ches`/`shes`/`xes`/`zes`/`ses`, remove the trailing `es` (`statuses` → `status`)
      - else if ends with `s` (but not `ss`), remove the trailing `s` (`addresses` → `address`)
      - else leave unchanged
      - irregular/ambiguous cases must use `nameOverrides` (below)
    - override: `resourceSchema.relational.nameOverrides["$.path.to.array[*]"]`
  - example: `School` + `addresses[*]` + `periods[*]` → `SchoolAddressPeriod`
- Extensions:
  - tables live in the **extension project schema** derived from that `_ext` key’s resolved `ProjectEndpointName`
  - naming follows `extensions.md`:
    - extension root table: `{ResourceBaseName}Extension`
    - extension collection tables: `{ResourceBaseName}Extension{CollectionSuffix}`
- Abstract union views:
  - `{ProjectSchema}.{AbstractResource}_View`

### 4) Column names (PascalCase + stable suffixes)

**Primary keys**
- Root tables: `DocumentId` (PK + FK → `dms.Document(DocumentId)`)
- Child tables use composite keys (see `flattening-reconstitution.md`):
  - key column order is stable: parent key parts first, then `Ordinal`
  - parent key parts include:
    - the root `DocumentId`, plus
    - any ancestor ordinals (in root-to-leaf order)

**Parent key part columns (child tables)**
- Root document id key part: `{RootBaseName}_DocumentId` (e.g., `School_DocumentId`)
- Ancestor ordinals:
  - `{ParentCollectionBaseName}Ordinal` (e.g., `AddressOrdinal`)

**Reference and descriptor FK columns**
- Resource references: `{ReferenceBaseName}_DocumentId`
  - `ReferenceBaseName` comes from the reference-object JSON path (e.g., `$.studentReference` → `Student`)
  - override: `resourceSchema.relational.nameOverrides["$.studentReference"]`
- Descriptor references: `{DescriptorBaseName}_DescriptorId`
  - `DescriptorBaseName` comes from the descriptor value JSON path (e.g., `$.schoolTypeDescriptor` → `SchoolTypeDescriptor`)
  - override: `resourceSchema.relational.nameOverrides["$.schoolTypeDescriptor"]`

**Scalar columns**
- Scalars are derived from JSON property names under the table’s JSON scope (PascalCase).
- Inlined objects contribute a stable prefix based on the object property path within the scope (PascalCase concatenation).
- `resourceSchema.relational.nameOverrides["$.path.to.property"]` can override the **base name** for a scalar column at that JSON path.

### 5) Constraint, index, and trigger names (stable)

Object names are deterministic and derived from the owning table and column names:
- Primary key constraints: `PK_{TableName}`
- Unique constraints: `UX_{TableName}_{Column1}_{Column2}_...` (columns in key order)
- Foreign keys: `FK_{TableName}_{ColumnName}` (or `FK_{TableName}_{Column1}_{Column2}` for composite FKs)
- Indexes: `IX_{TableName}_{Column1}_{Column2}_...` (columns in index key order)

If a name exceeds the dialect identifier limit, apply truncation + hash as below.

### 6) Max identifier length handling (truncation + hash)

When an identifier exceeds the maximum for the target dialect:
- PostgreSQL: 63 **bytes** (UTF-8)
- SQL Server: 128 **characters**

Apply deterministic shortening:
1. Compute `hash = sha256hex(utf8(fullIdentifier))` (lowercase hex).
2. Replace the identifier with: `prefix + "_" + hash[0..10]`
   - `prefix` is the longest leading portion that allows the final name to fit within the dialect limit.
3. Validate uniqueness after shortening; if a collision still occurs, fail fast and require a `nameOverrides` fix.

## Type Mapping Defaults (Deterministic, Cross-DB Safe)

The DDL generator derives physical scalar column types deterministically from:
- `resourceSchema.jsonSchemaForInsert` (types + formats + `maxLength`), and
- `resourceSchema.decimalPropertyValidationInfos` (precision/scale for `type: "number"`).

Alignment note:
- SQL Server scalar types intentionally match Ed-Fi ODS SQL Server conventions (authoritative DDL uses `NVARCHAR`, `DATETIME2(7)`, `TIME(7)`, and `DATE`).

Rules:
- Scalar strings must have `maxLength`; missing `maxLength` is an error.
- Decimals must have `(totalDigits, decimalPlaces)` from `decimalPropertyValidationInfos`; missing info is an error.
- `date-time` values are treated as UTC instants at the application boundary. SQL Server storage uses `datetime2(7)` (no offset), so any incoming offset is normalized to UTC at write time.

| ApiSchema JSON schema | PostgreSQL type | SQL Server type |
| --- | --- | --- |
| `type: "string"` (no `format`) | `varchar(n)` | `nvarchar(n)` |
| `type: "string", format: "date"` | `date` | `date` |
| `type: "string", format: "time"` | `time` | `time(7)` |
| `type: "string", format: "date-time"` | `timestamp with time zone` | `datetime2(7)` |
| `type: "integer"` | `integer` | `int` |
| `type: "number"` + `decimalPropertyValidationInfos` | `numeric(p,s)` | `decimal(p,s)` |
| `type: "boolean"` | `boolean` | `bit` |


## Other Notes (Design Guardrails)
- **MetaEd validation**: add a rule that caps the number of fields in any Domain Entity/Association (e.g., ~50) so relational root tables do not become unmanageably wide.
