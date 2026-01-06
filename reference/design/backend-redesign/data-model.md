# Backend Redesign: Data Model

## Status

Draft.

This document is the data-model deep dive for `overview.md`.

- Overview: [overview.md](overview.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- Caching & operations: [caching-and-ops.md](caching-and-ops.md)
- Authorization: [auth.md](auth.md)

## Table of Contents

- [Proposed Database Model](#proposed-database-model)
- [Naming Rules (Deterministic, Cross-DB Safe)](#naming-rules-deterministic-cross-db-safe)
- [Other Notes (Design Guardrails)](#other-notes-design-guardrails)

---

## Proposed Database Model

### Core tables (schema: `dms`)

##### 1) `dms.Document`

Canonical metadata per persisted resource instance. One row per document, regardless of resource type.

**PostgreSQL**

```sql
CREATE TABLE dms.Document (
    DocumentId bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    Etag bigint NOT NULL DEFAULT 1,
    CreatedAt timestamp with time zone NOT NULL DEFAULT now(),
    LastModifiedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT UX_Document_DocumentUuid UNIQUE (DocumentUuid)
);

CREATE INDEX IX_Document_ProjectName_ResourceName_DocumentId
    ON dms.Document (ProjectName, ResourceName, DocumentId);
```

Notes:
- `DocumentUuid` remains stable across identity updates; identity-based upserts map to it via `dms.ReferentialIdentity` for **all** identities (self-contained, reference-bearing, and abstract/superclass aliases), because `dms.ReferentialIdentity` is maintained transactionally (including cascades) on identity changes.
- `Etag` and `LastModifiedAt` are **representation** metadata: they change whenever the API representation would change, including when referenced identities/descriptor URIs change values that are embedded in reference objects/descriptor strings.
  - `Etag` is an **opaque “representation version” token** (stored as a monotonically increasing `bigint`), not a hash of JSON.
  - Prefer monotonic `bigint` etags over UUID etags:
    - smaller storage and indexes,
    - cheap equality comparisons (e.g., `dms.DocumentCache.Etag = dms.Document.Etag`),
    - provides ordering/debuggability without affecting correctness (the value is still “opaque” to clients).
  - `Etag` should be incremented (e.g., `Etag = Etag + 1`) at least once per committed transaction for every document whose **representation** changes, including identity/descriptor cascades; dedupe the impacted `DocumentId` set so a document is bumped once per cascade transaction.
  - Concurrency: updates guarded by `If-Match` should use a conditional update (`WHERE DocumentId=@id AND Etag=@expected`) so the bump is atomic and race-safe.
  - Cascades should update `Etag`/`LastModifiedAt` with **set-based writes** over an impacted set (computed using `dms.ReferenceEdge`), rather than reconstituting and hashing large JSON payloads.
  - Strictness: the impacted-set computation must be **phantom-safe** w.r.t. concurrent `dms.ReferenceEdge` writes; see `caching-and-ops.md` (“Set-based representation-version bump (ETag/LastModifiedAt) — strict and phantom-safe (SERIALIZABLE)”).
- Time semantics: store timestamps as UTC instants. In PostgreSQL, use `timestamp with time zone` and format response values as UTC (e.g., `...Z`). In SQL Server, use `datetime2` with UTC writers (e.g., `sysutcdatetime()`).
- Authorization-related columns are intentionally omitted here. Authorization storage and query filtering is described in [auth.md](auth.md).

##### 2) `dms.ReferentialIdentity`

Maps `ReferentialId` → `DocumentId` (replaces today’s `dms.Alias`), including superclass aliases used for polymorphic references.

Rationale for retaining `ReferentialId` in this redesign: see [overview.md#Why keep ReferentialId](overview.md#why-keep-referentialid).

**PostgreSQL**

```sql
CREATE TABLE dms.ReferentialIdentity (
    ReferentialId uuid NOT NULL,
    DocumentId bigint NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    CONSTRAINT PK_ReferentialIdentity PRIMARY KEY (ReferentialId),
    CONSTRAINT FK_ReferentialIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_ReferentialIdentity_DocumentId_Resource UNIQUE (DocumentId, ProjectName, ResourceName)
);

CREATE INDEX IX_ReferentialIdentity_DocumentId ON dms.ReferentialIdentity (DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.ReferentialIdentity (
    ReferentialId uniqueidentifier NOT NULL,
    DocumentId bigint NOT NULL,
    ProjectName nvarchar(256) NOT NULL,
    ResourceName nvarchar(256) NOT NULL,
    CONSTRAINT PK_ReferentialIdentity PRIMARY KEY NONCLUSTERED (ReferentialId),
    CONSTRAINT FK_ReferentialIdentity_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT UX_ReferentialIdentity_DocumentId_Resource UNIQUE CLUSTERED (DocumentId, ProjectName, ResourceName)
);
```

Database Specific Differences:
- The logical shape is identical across engines (UUID `ReferentialId` → `DocumentId` + `{ProjectName, ResourceName}`).
- The physical DDL will differ slightly for performance: SQL Server should not cluster on a randomly-distributed UUID.

Operational considerations:
- `ReferentialId` is a deterministic UUIDv5 and is effectively randomly distributed for index insertion. The primary operational concern is **write amplification** (page splits, fragmentation/bloat), not point-lookup speed.
- **SQL Server**:
  - Keep the UUID key as **NONCLUSTERED** (as shown) and use a sequential clustered key (e.g., `(DocumentId, ProjectName, ResourceName)`).
  - Consider a lower `FILLFACTOR` (e.g., 80–90) on the UUID index to reduce page splits; monitor fragmentation and rebuild/reorganize as needed.
- **PostgreSQL**:
  - B-tree point lookups on UUID are fine; manage bloat under high write rates with index/table `fillfactor` (e.g., 80–90), healthy autovacuum settings/monitoring, and periodic `REINDEX` when warranted.
  - If sustained ingest is extreme, consider hash partitioning `dms.ReferentialIdentity` by `ReferentialId` (e.g., 8–32 partitions) to reduce contention and make maintenance cheaper.


Critical invariants:
- **Uniqueness** of `ReferentialId` enforces “one natural identity maps to one document” for **all** identities (self-contained identities, reference-bearing identities, and descriptor URIs). This requires `dms.ReferentialIdentity` to be maintained transactionally on identity changes, including cascading recompute when upstream identity components change.
- The resource root table’s natural-key unique constraint (FK `..._DocumentId` columns + scalar identity parts) remains a recommended relational guardrail, but identity-based resolution/upsert uses `dms.ReferentialIdentity`.
- A document has **at most 2** referential ids:
  - the **primary** referential id for the document’s concrete `{ProjectName, ResourceName}`
  - an optional **superclass/abstract alias** referential id for polymorphic references (when `isSubclass: true`)
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

##### 4) `dms.EffectiveSchema` + `dms.SchemaComponent`

Tracks which **effective schema** (core `ApiSchema.json` + extension `ApiSchema.json` files) the database is migrated to, and records the **exact project versions** present in that effective schema. At startup, DMS will use this to validate consistency between the loaded ApiSchema.json files and the database (see **EffectiveSchemaHash Calculation** below).

**PostgreSQL**

```sql
CREATE TABLE dms.EffectiveSchema (
    EffectiveSchemaId bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ApiSchemaFormatVersion varchar(64) NOT NULL,
    EffectiveSchemaHash varchar(64) NOT NULL,
    AppliedAt timestamp with time zone NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX UX_EffectiveSchema_EffectiveSchemaHash
    ON dms.EffectiveSchema (EffectiveSchemaHash);

CREATE TABLE dms.SchemaComponent (
    EffectiveSchemaId bigint NOT NULL
        REFERENCES dms.EffectiveSchema (EffectiveSchemaId) ON DELETE CASCADE,
    ProjectNamespace varchar(128) NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ProjectVersion varchar(64) NOT NULL,
    IsExtensionProject boolean NOT NULL,
    CONSTRAINT PK_SchemaComponent PRIMARY KEY (EffectiveSchemaId, ProjectNamespace)
);
```

##### EffectiveSchemaHash Calculation

`EffectiveSchemaHash` is a deterministic fingerprint of the configured schema set (core + extensions) as it affects relational mapping. It must be stable across file ordering, whitespace, and JSON property ordering.

Recommendations:
- Use `SHA-256` and store as lowercase hex (64 chars).
- Require a single `ApiSchemaFormatVersion` across core + extensions (fail fast if they differ).
- Validate that each configured `ApiSchema.json` file contributes exactly one `projectSchemas[ProjectNamespace]` entry.
- Exclude OpenAPI payloads from hashing to avoid churn and reduce input size:
  - `projectSchemas[*].openApiBaseDocuments`
  - `projectSchemas[*].resourceSchemas[*].openApiFragments`
- Keep arrays in-order (many arrays are semantically ordered), but sort objects by property name recursively.
- Include a DMS-controlled constant “relational mapping version” so that a breaking change in mapping conventions forces a mismatch even if ApiSchema content is unchanged.

Algorithm (suggested):
1. Parse the configured core + extension ApiSchema files.
2. For each file, extract:
   - `ApiSchemaFormatVersion` (`apiSchemaVersion`)
   - the single `projectSchemas[ProjectNamespace]` entry (after removing OpenAPI payloads)
3. Sort projects by `ProjectNamespace`.
4. Compute `ProjectHash = SHA-256(canonicalJson(projectSchema))` for each project.
5. Compute `EffectiveSchemaHash = SHA-256(manifestString)` where `manifestString` is:
   - a constant header (e.g., `dms-effective-schema-hash:v1`)
   - a constant mapping version (e.g., `relational-mapping:v1`)
   - `ApiSchemaFormatVersion`
   - one line per project: `ProjectNamespace|ProjectName|ProjectVersion|IsExtensionProject|ProjectHash`

Pseudocode:

```text
const HashVersion = "dms-effective-schema-hash:v1"
const RelationalMappingVersion = "relational-mapping:v1"

projects = []
apiSchemaFormatVersion = null
for file in configuredApiSchemaFiles:
  json = parse(file)
  apiSchemaFormatVersion = apiSchemaFormatVersion ?? json.apiSchemaVersion
  assert(apiSchemaFormatVersion == json.apiSchemaVersion)
  (projectNamespace, projectSchema) = extractSingleProjectSchema(json.projectSchemas)
  projectSchema = removeOpenApiPayloads(projectSchema)
  projectHash = sha256hex(canonicalizeJson(projectSchema))
  projects.add({
    projectNamespace,
    projectName: projectSchema.projectName,
    projectVersion: projectSchema.projectVersion,
    isExtensionProject: projectSchema.isExtensionProject,
    projectHash
  })

projects = sortBy(projects, p => p.projectNamespace)

manifest =
  HashVersion + "\n" +
  RelationalMappingVersion + "\n" +
  "apiSchemaFormatVersion=" + apiSchemaFormatVersion + "\n" +
  join(projects, "\n",
    p.projectNamespace + "|" +
    p.projectName + "|" +
    p.projectVersion + "|" +
    p.isExtensionProject + "|" +
    p.projectHash)

effectiveSchemaHash = sha256hex(utf8(manifest))
```

##### 5) `dms.ReferenceEdge` (reverse reference index for cascades)

A small, relational reverse index of **“this document references that document”**, maintained on writes.

Important properties:
- **Required for derived-artifact cascades**:
  - `dms.ReferentialIdentity` cascading recompute when upstream identity components change (`IsIdentityComponent=true`)
  - `dms.Document` representation-metadata cascades (`Etag`, `LastModifiedAt`) so `_etag`/`_lastModifiedDate` change when the representation changes
  - JSON document cascade when `dms.DocumentCache` is enabled
- **Stores resolved `DocumentId`s only**: no `ReferentialId` resolution, no partition keys, no alias joins.
- **Coverage requirement is correctness-critical**:
  - DMS must record **all** outgoing document references and descriptor references, including those inside nested collections/child tables.
  - If edge maintenance fails, the write must fail (otherwise dependent referential-id and representation-metadata cascades can become incorrect).
- **Collapsed edge granularity**:
  - This design stores **one row per `(ParentDocumentId, ChildDocumentId)`** (not “per reference site/path”) to reduce write amplification and index churn.
  - The `IsIdentityComponent` flag is the **OR** of all reference sites in the parent document that reference the same `ChildDocumentId`.

Primary uses:
- **`dms.DocumentCache` invalidation/refresh** (optional): when referenced identities/descriptor URIs change, enqueue or mark cached documents for rebuild instead of doing an in-transaction “no stale window” cascade.
- **`dms.ReferentialIdentity` identity cascades**: when a referenced document’s identity/descriptor URI changes, update `dms.ReferentialIdentity` for documents whose identities depend on it (reverse traversal of `IsIdentityComponent=true` edges).
- **Delete conflict messaging**: answer “who references me?” without scanning all tables or relying solely on constraint name parsing.

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
- It is not used for referential integrity; it exists for reverse lookups, identity/version cascades (`IsIdentityComponent=true`), and optional cache rebuild/invalidation.

**Doesn’t this require the same InsertReferences pattern?**

We can use a similar *concept* (stage + diff) to avoid churn, but it is far simpler than today’s `InsertReferences` because:
- inputs are already-resolved `DocumentId`s (no Alias joins, no invalid `ReferentialId` reporting),
- there is no partition-routing logic,
- the table is keyed by the parent `DocumentId` (single delete scope), and
- the diff is between two small sets of `ChildDocumentId`s (with associated `IsIdentityComponent` flags, aggregated by OR when a parent references the same child multiple times).

**Why does `dms.DocumentCache` need an update cascade?**

`dms.DocumentCache` stores **fully reconstituted JSON**, including:
- reference objects expanded from the *current* identity values of referenced resources, and
- descriptor URI strings (often derived from current descriptor fields).

If a referenced document’s identity/URI changes, the JSON that referencing documents should return changes as well. This is a *cache/projection refresh cascade*, not a relational data cascade:
- relational FK columns remain stable (`..._DocumentId` still points to the same row),
- only cached JSON requires recomputation/upsert.

In the preferred eventual-cache mode, this is handled as an **async rebuild cascade** (enqueue/mark dependents for rebuild) rather than an in-transaction “no stale window” rewrite. Targeting rules are described in the Caching section.

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
- See [caching-and-ops.md](caching-and-ops.md) for the normative lock ordering and closure-locking algorithms (including recommended lock query shapes).

##### 7) `dms.DocumentCache` (optional, eventually consistent projection)

Optional materialized JSON representation of the document (as returned by GET/query), stored as a convenience **projection**.

This table is intentionally designed to support **CDC streaming** (e.g., Debezium → Kafka) and downstream indexing:
- it is not purely a “cache-aside” optimization
- when enabled, DMS should materialize documents into this table via a write-driven/background projector

Prefer **eventual consistency** (background/write-driven projection) where rows may be rebuilt asynchronously. For rationale and projector/refresh semantics, see [caching-and-ops.md](caching-and-ops.md) (`dms.DocumentCache` section).

**PostgreSQL**

```sql
CREATE TABLE dms.DocumentCache (
    DocumentId bigint PRIMARY KEY
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    DocumentUuid uuid NOT NULL,
    ProjectName varchar(256) NOT NULL,
    ResourceName varchar(256) NOT NULL,
    ResourceVersion varchar(64) NOT NULL,
    Etag bigint NOT NULL,
    LastModifiedAt timestamp with time zone NOT NULL,
    DocumentJson jsonb NOT NULL,
    ComputedAt timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT CK_DocumentCache_JsonObject CHECK (jsonb_typeof(DocumentJson) = 'object'),
    CONSTRAINT UX_DocumentCache_DocumentUuid UNIQUE (DocumentUuid)
);

CREATE INDEX IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt
    ON dms.DocumentCache (ProjectName, ResourceName, LastModifiedAt, DocumentId);
```

**SQL Server**

```sql
CREATE TABLE dms.DocumentCache (
    DocumentId bigint NOT NULL,
    DocumentUuid uniqueidentifier NOT NULL,
    ProjectName nvarchar(256) NOT NULL,
    ResourceName nvarchar(256) NOT NULL,
    ResourceVersion nvarchar(64) NOT NULL,
    Etag bigint NOT NULL,
    LastModifiedAt datetime2(7) NOT NULL,
    DocumentJson nvarchar(max) NOT NULL,
    ComputedAt datetime2(7) NOT NULL CONSTRAINT DF_DocumentCache_ComputedAt DEFAULT (sysutcdatetime()),
    CONSTRAINT PK_DocumentCache PRIMARY KEY CLUSTERED (DocumentId),
    CONSTRAINT FK_DocumentCache_Document FOREIGN KEY (DocumentId)
        REFERENCES dms.Document (DocumentId) ON DELETE CASCADE,
    CONSTRAINT CK_DocumentCache_IsJsonObject CHECK (ISJSON(DocumentJson) = 1 AND LEFT(LTRIM(DocumentJson), 1) = '{'),
    CONSTRAINT UX_DocumentCache_DocumentUuid UNIQUE (DocumentUuid)
);

CREATE INDEX IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt
    ON dms.DocumentCache (ProjectName, ResourceName, LastModifiedAt, DocumentId);
```

Uses:
- Faster GET/query responses (skip reconstitution)
- Easier CDC streaming (Debezium) / OpenSearch indexing / external integrations

### Resource tables (schema per project)

Each project gets a schema derived from `ProjectNamespace` (e.g., `ed-fi` → `edfi`, `tpdm` → `tpdm`). Core `dms.*` tables remain in `dms`.

For each resource `R`:

#### Root table: `{schema}.{R}`

One row per document; PK is `DocumentId` (shared surrogate key).

Typical structure:
- `DocumentId BIGINT` **PK/FK** → `dms.Document(DocumentId)` ON DELETE CASCADE
- Natural key columns (from `identityJsonPaths`) → unique constraint. For identity elements that come from a document reference object, the unique constraint uses the corresponding `..._DocumentId` FK column rather than duplicating the referenced natural-key fields.
- Scalar columns for top-level non-collection properties
- Reference FK columns:
  - Non-descriptor references (concrete target): `..._DocumentId BIGINT` FK → `{schema}.{TargetResource}(DocumentId)` to enforce existence *and* resource type
  - Non-descriptor references (polymorphic/abstract target): `..._DocumentId BIGINT` FK → `dms.Document(DocumentId)` plus optional discriminator/membership enforcement (see Reference Validation in [caching-and-ops.md](caching-and-ops.md))
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

- View name: `{schema}.{AbstractResource}_View` (deterministic; participates in migration like tables)
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
- DMS chooses a canonical SQL type for each abstract identity field (from `abstractResources[A].openApiFragment` when present, otherwise from the participating concrete resources) and casts each `SELECT` accordingly to keep the union portable across engines.
- For each concrete resource `R` in the hierarchy, DMS derives, per abstract identity field:
  - If `R.identityJsonPaths` contains `$.{fieldName}`, use the corresponding root-table column directly.
  - Else, if `R.isSubclass=true` and `R.superclassIdentityJsonPath == $.{fieldName}`, use the concrete identity column(s) from `R.identityJsonPaths` (identity rename case; e.g., `$.schoolId` → `EducationOrganizationId`).
- If a concrete type cannot supply all abstract identity fields, migration/startup fails fast (schema mismatch).

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
- Adding a new concrete subtype requires a migration that updates the view definition (same operational contract as table changes).
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

- Extension tables live in the **extension project schema** (derived from `ProjectNamespace`, e.g., `sample`, `tpdm`), not in the core project schema.
- Table-name patterns follow the old flattening design:
  - extension root table: `{ResourceName}Extension` (e.g., `sample.ContactExtension`)
  - extension collection tables: `{ResourceName}Extension{CollectionSuffix}` (e.g., `sample.ContactExtensionAddress`)
- For `isResourceExtension: true` resources (and other `_ext` sites), store extension fields in:
  - a 1:1 extension root row keyed by `DocumentId` (FK to the base resource root table), and
  - extension scope/collection tables keyed to the same composite keys as the base scope they extend (root + ordinals) so extension rows attach deterministically and cascade deletes cleanly.

See [extensions.md](extensions.md) for the normative mapping rules for `_ext` (resource + common-type extensions, nested collections, and multiple extension projects).



## Naming Rules (Deterministic, Cross-DB Safe)

To keep migrations tractable and avoid rename cascades, physical names must be deterministic:

- Schema name: derived from `ProjectNamespace` (`ed-fi` → `edfi`, non-alphanumerics removed/normalized).
- Table names: PascalCase resource names (MetaEd `resourceName`), plus deterministic suffixes for collections.
- Column names: PascalCase of JSON property names, with suffixes:
  - `..._DocumentId` for non-descriptor references
  - `..._DescriptorId` for descriptor references
  - `Ordinal` for the current collection level, and `<ParentCollectionBaseName>Ordinal` for ancestor ordinals in nested collections (e.g., `AddressOrdinal`)
- Max identifier length handling:
  - PostgreSQL: 63 bytes; SQL Server: 128 chars
  - When exceeding, apply deterministic truncation + short hash suffix.


## Other Notes (Design Guardrails)
  - **MetaEd validation**: add a rule that caps the number of fields in any Domain Entity/Association (e.g., ~50) so relational root tables do not become unmanageably wide.
