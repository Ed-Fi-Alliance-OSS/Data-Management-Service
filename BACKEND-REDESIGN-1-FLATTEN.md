# Backend Redesign 1 (Flatten/Reconstitute): Metadata-Driven Relational Mapping

This document fills the largest design gap in `BACKEND-REDESIGN-1.md`: **how DMS flattens JSON documents into relational tables on POST/PUT**, and **how it reconstitutes JSON from those tables on GET/query**, **without code-generating per-resource code**.

It also defines the minimal `ApiSchema.json` metadata needed (beyond what already exists) and proposes concrete C# shapes for implementing a high-performance, schema-driven flattener/reconstituter with good batching characteristics (no N+1 insert/query patterns).

---

## 1. Requirements & Constraints (Restated)

- Canonical storage is relational; cached JSON is optional.
- Schema changes are operational events (migration + restart); runtime hot reload is out of scope.
- No code generation of per-resource classes or per-resource SQL artifacts checked into source.
- Must be driven by `ApiSchema.json` metadata; avoid hard-coding resource shapes in C#.
- Must support nested collections and preserve array ordering.
- Must avoid N+1 inserts/queries and minimize DB round-trips/network traffic.
- Must remain implementable for both PostgreSQL and SQL Server (dialect differences isolated).

---

## 2. Why “Derived Mapping” Can Be Enough (vs Full Flattening Metadata)

The “full flattening metadata” design (see `reference/design/flattening-metadata-design.md`) explicitly enumerates every table and column. That is maximally explicit, but it has costs:

- It’s large and duplicative of information already present in `jsonSchemaForInsert` and `documentPathsMapping`.
- It couples DMS behavior to a concrete pre-flattening that is harder to evolve without regenerating metadata.

For Ed-Fi resource documents, DMS can derive a complete relational mapping *deterministically* because:

1. **Shape and types** are in `jsonSchemaForInsert` (properties, arrays, item schemas, required-ness, string lengths, formats, numeric precision rules).
2. **References vs scalars** are in `documentPathsMapping`:
   - document reference identities and their JSON paths (`referenceJsonPaths`)
   - descriptor paths (`path`)
3. **Identity + uniqueness** is in `identityJsonPaths` and `arrayUniquenessConstraints`.

So the only metadata that truly must be added is:
- deterministic naming overrides (collisions/length/reserved words)
- optional naming tweaks for deeply nested collections/objects

Everything else can be derived and compiled once at startup into a resource-specific plan.

---

## 3. Minimal `ApiSchema.json` Additions (Relational Block)

### 3.1 Goals of the block

- Keep `ApiSchema.json` authoritative but not bloated.
- Provide *stable* physical naming and a small escape hatch for “bad cases”.
- Avoid enumerating tables/columns explicitly.

### 3.2 Proposed shape

Add an optional `relational` section to each `resourceSchema`:

```json
{
  "relational": {
    "rootTableNameOverride": "Student",

    "nameOverrides": {
      "$.someVeryLongPropertyName...": "ShortColumnName",
      "$.addresses[*]": "Address",
      "$.addresses[*].periods[*]": "AddressPeriod"
    }
  }
}
```

Semantics:
- `rootTableNameOverride`: optional physical root table name override.
- `nameOverrides`: maps a **JSONPath** (property/array path) to a stable physical base name (column or table suffix).
  - `$.x.y` targets a column base name (before suffixes like `_DocumentId`/`_DescriptorId`).
  - `$.arr[*]` targets a child-table base name.

Schema name is not configurable here: it is always derived from `ProjectNamespace` (e.g., `ed-fi` → `edfi`).

### 3.3 Strict rules for `nameOverrides`

To keep the mapping deterministic, portable, and validateable, `nameOverrides` must follow these rules:

1. **JSONPath grammar is restricted**:
   - Must start at `$`
   - Only property segments (`.propertyName`) and array wildcards (`[*]`) are allowed
   - No numeric indexes (`[0]`), no filters (`[?()]`), no recursive descent (`..`), and no bracket property quoting

2. **Keys must match a derived mapping element**:
   - The key must match a derived column path or collection path for the resource.
   - Unknown keys are an error (migration/startup fails fast) to prevent “silent typos”.

3. **Meaning depends on whether the key ends with `[*]`**:
   - If the key **ends with `[*]`**, it overrides the **collection table base name** for that array path (e.g., `$.addresses[*].periods[*]` → `SchoolAddressPeriod`).
   - Otherwise, it overrides the **column base name** at that JSONPath (before suffixes like `_DocumentId` / `_DescriptorId`).
     - For **document references**, the relevant path is the **reference object path** (e.g., `$.schoolReference`), not the identity field paths inside it (e.g., `$.schoolReference.schoolId`).

4. **Overrides cannot create collisions**:
   - After applying overrides and the standard identifier normalization/truncation rules, table/column names must still be unique.
   - Collisions are a compile-time error (migration/startup fails) and must be resolved by adjusting overrides.

### 3.4 What we intentionally do *not* add

- We do **not** add a list of columns or childTables (that’s the full flattening metadata approach).
- We do **not** add per-resource code hooks.

---

## 4. Derived Relational Resource Model (What We Compile at Startup)

At startup (or at migrator time), DMS builds a fully explicit internal model:

- Root table name + full column list (scalars + FK columns)
- Child tables for each array path (and nested arrays)
- Column types/nullability/constraints
- Reference binding plan: JSON reference paths → FK columns → referenced resource
- Descriptor binding plan: descriptor JSON paths → FK columns (descriptor `DocumentId`, resolved via `dms.ReferentialIdentity`) and expected descriptor resource type
- JSON reconstitution plan: table+column → JSON property path writer instructions
- Identity projection plans (for references in responses)

This is not code generation; it is compiled metadata cached by `(DmsInstanceId, EffectiveSchemaHash, ProjectName, ResourceName)`.

### 4.1 Derivation algorithm (high-level)

Inputs:
- `resourceSchema.jsonSchemaForInsert` (and update variant if needed)
- `resourceSchema.documentPathsMapping` (references/descriptors)
- `resourceSchema.identityJsonPaths`
- `resourceSchema.decimalPropertyValidationInfos`
- `resourceSchema.arrayUniquenessConstraints`
- optional `resourceSchema.relational` overrides

Algorithm sketch:
1. **Resolve `$ref`** in the JSON schema into an in-memory schema graph (or a resolver that can answer “what is the schema at path X?”).
2. Walk the schema tree starting at `$`:
   - Each **array** node creates a **child table** with:
     - parent key columns
     - `Ordinal` (order preservation)
     - derived element columns
   - Each **object** node is **inlined**: its scalar descendant properties become columns on the current table (with a deterministic prefix).
   - Each **scalar** node becomes a typed column on the current table.

Note: this design intentionally does **not** support “split” tables for very wide objects. If MetaEd/ApiSchema ever produced a resource that would exceed engine limits (e.g., SQL Server column/row-size limits), migration should fail fast rather than silently changing storage shape.
3. Overlay `documentPathsMapping` classification:
   - Any JSONPath that is a **document reference** becomes exactly one FK column (`..._DocumentId`) on the table that contains that reference object (root or child).
   - Any JSONPath that is a **descriptor reference** becomes exactly one FK column (`..._DescriptorId`) on the table that contains it.
4. Apply naming rules + `nameOverrides`.
5. Apply constraints:
   - root uniqueness from `identityJsonPaths` → unique constraints (optional but recommended)
   - collection uniqueness from `arrayUniquenessConstraints` → unique constraints on child tables
6. Produce a **compiled read/write plan** (see sections 5–7).

### 4.2 Child-table key strategy (avoid RETURNING/OUTPUT)

To avoid round-trips and identity-capture complexity for nested collections, use **composite parent+ordinal keys** instead of surrogate `Id` keys:

- Level 1 child table key: `(ParentDocumentId, Ordinal)`
- Level 2 child table key: `(ParentDocumentId, ParentOrdinal, Ordinal)`
- …and so on

This design:
- makes inserts purely append/bulk without capturing generated keys
- makes nested deletes cascade naturally
- remains portable across PostgreSQL and SQL Server

Concrete example (PostgreSQL) - School `addresses[*]` and nested `periods[*]`:

```sql
CREATE TABLE IF NOT EXISTS edfi.SchoolAddress (
    School_DocumentId bigint NOT NULL
                      REFERENCES edfi.School(DocumentId) ON DELETE CASCADE,

    Ordinal int NOT NULL,

    AddressTypeDescriptor_DescriptorId bigint NOT NULL
                      REFERENCES dms.Descriptor(DocumentId),

    StreetNumberName varchar(150) NULL,

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
```

If a surrogate key is still desired (for operational/debuggability), it can be added later, but the composite key should remain the one used for parent/child FKs to preserve the batching benefits.

---

## 5. Flattening (POST/PUT) Design

### 5.1 Inputs

For a given request, backend already has:
- `ResourceInfo` (project/resource/version)
- validated/coerced JSON body (`JsonNode` / `JsonElement`)
- `DocumentInfo`:
  - resource referential id
  - descriptor references (with concrete JSON paths, including indices)
  - document references (referential ids + concrete reference-object JSON paths, including indices; grouped by wildcard path)

### 5.2 Reference & descriptor resolution (bulk)

Before writing resource tables:

- Resolve all **document references**:
  - `dms.ReferentialIdentity`: `ReferentialId → DocumentId`
- Resolve all **descriptor references**:
  - `dms.ReferentialIdentity`: `ReferentialId → DocumentId` (descriptor referential ids are computed by Core from descriptor resource name + normalized URI)

Cache these lookups aggressively (L1/L2 optional), but only populate caches after commit.

### 5.2.1 Document references inside nested collections (Option B)

When a document reference appears inside a collection (or nested collection), its FK is stored in a **child table row** whose key includes one or more **ordinals**. To set the correct FK column without per-row JSONPath evaluation and per-row referential id hashing, we need a stable way to answer:

> For this `ReferenceBinding`, and for this row’s **ordinal path**, what is the referenced `DocumentId`?

There are two approaches:

- **Option A**: backend flattener recomputes referential ids per row by reading identity fields from JSON at the row scope.
  - Pros: no change to Core reference extraction shapes.
  - Cons: repeats JSON reads + referential id hashing in hot loops; risks drifting from Core canonicalization; scales poorly for large nested arrays.

- **Option B (preferred)**: Core emits each reference instance *with its concrete JSON location (indices)* so the backend can address it by ordinal path.
  - Pros: referential id computation stays centralized in Core; flattening becomes pure lookup; nested collections work naturally with composite parent+ordinal keys.
  - Cons: requires enhancing Core’s extracted reference model to carry location; backend builds a small per-request index.

**What Core must provide (minimal enhancement)**

For each reference object instance, Core must provide:
- the **wildcard reference-object path** (already present as `DocumentReferenceArray.arrayPath`), e.g. `$.addresses[*].periods[*].calendarReference`
- the **concrete reference-object path including indices**, e.g. `$.addresses[2].periods[0].calendarReference`
- the computed `ReferentialId` (already present on `DocumentReference`)

This is the same pattern already used for descriptor extraction (`DescriptorReference.Path`).

**How backend uses it**

Backend turns extracted references + bulk DB resolution into a per-request index:

`(ReferenceObjectPath, OrdinalPath[]) → ReferencedDocumentId`

Where `OrdinalPath[]` is the vector of array ordinals along the wildcard path:
- Root scope: `[]`
- `$.addresses[*]`: `[addressOrdinal]`
- `$.addresses[*].periods[*]`: `[addressOrdinal, periodOrdinal]`

During row materialization, the flattener already knows the current row’s `OrdinalPath` because it is enumerating the arrays. It performs an O(1) lookup to populate each FK column (no per-row hashing).

### 5.3 Row materialization (in-memory)

Using the compiled `ResourceWritePlan`, materialize:

- one root row buffer
- N child row buffers per child table

Important: materialization traverses each JSON array exactly once and does not perform any DB calls.

Pseudocode sketch:

```text
Flatten(documentJson, resolvedRefs):
  rootRow = plan.Root.CreateRow(documentId)
  rootRow.SetScalarsFromJson(documentJson)
  rootRow.SetFkColumnsFromResolvedRefs(documentJson, resolvedRefs)

  for each childPlan in plan.ChildrenDepthFirst:
    rows = []
    for each element in EnumerateArray(documentJson, childPlan.ArrayPath):
      row = childPlan.CreateRow(parentKey, ordinal)
      row.SetScalarsFromElement(element)
      row.SetFkColumnsFromResolvedRefs(element, resolvedRefs)
      rows.add(row)
    plan.TableRows[childPlan.Table] = rows

  return plan.TableRows
```

### 5.4 Write execution (single transaction, no N+1)

Within a single transaction:

1. Write `dms.Document` and `dms.ReferentialIdentity`
2. Write resource root row (`INSERT` or `UPDATE`)
3. For each child table (depth-first):
   - `DELETE FROM Child WHERE ParentKey = @documentId` (or rely on cascade from the parent resource root row if you do “delete then insert root” in some flows)
   - bulk insert all rows for that child table in batches

Bulk insert options (non-codegen):
- **Multi-row INSERT** with parameters (good default)
- **PostgreSQL**: `COPY` via `NpgsqlBinaryImporter` for large collections
- **SQL Server**: `SqlBulkCopy` for large collections

### 5.5 Update behavior for collections (replace strategy)

Initial implementation should use **replace** semantics for arrays:
- delete existing child rows for that parent
- insert current payload rows

This avoids diff logic and is consistent with how document databases treat “replace document”.
Optimizations (delta updates) can be added later if needed, but require stable element keys and careful semantics.

### 5.6 Parameter limits and batching

Avoid huge single commands:
- SQL Server parameter limit (~2100) means multi-row INSERT must be chunked.
- PostgreSQL also benefits from chunking to reduce packet sizes.

WritePlan should expose a `BatchSizer` that, given the table’s column count, yields safe batch sizes.

---

## 6. Reconstitution (GET by id / GET by query) Design

Reconstitution should be written to support both:
- **single document** (GET by id)
- **page of documents** (query results)

The page case must not become “GET by id repeated N times”.

### 6.1 Fetch strategy (keyset-first, batched hydration)

The API never supplies a “list of `DocumentId`s”. Reconstitution starts from a **page keyset**:

- **GET by id**: resolve `DocumentUuid → DocumentId`, then the keyset is a single `DocumentId`.
- **GET by query**: compile a parameterized SQL that selects the `DocumentId`s in the requested page (filters + stable ordering + paging).

All subsequent reads “hydrate” root + child tables by joining to that keyset.

#### Single round-trip (required): materialize a `page` keyset, then hydrate by join

Build one command that:
1) materializes the page keyset server-side, then
2) returns multiple result sets by joining each table to that keyset.

```sql
-- Materialize the page keyset (engine-specific DDL, same logical outcome):
-- PostgreSQL: CREATE TEMP TABLE page (DocumentId bigint PRIMARY KEY) ON COMMIT DROP;
-- SQL Server: DECLARE @page TABLE (DocumentId bigint PRIMARY KEY);  (or #page temp table, then DROP)

WITH page_ids AS (
  -- Provided by DMS query compilation (ApiSchema-driven):
  -- GET by id:    SELECT @DocumentId AS DocumentId
  -- GET by query: SELECT r.DocumentId FROM ... WHERE ... ORDER BY ... OFFSET/FETCH ...
  <PageDocumentIdSql>
)
INSERT INTO page (DocumentId)
SELECT DocumentId FROM page_ids;

-- Optional (if totalCount=true): add a result set that uses the same filters without paging.
-- SELECT COUNT(*) AS TotalCount FROM ... WHERE ...;

SELECT d.DocumentId, d.DocumentUuid, d.Etag, d.LastModifiedAt
FROM dms.Document d
JOIN page p ON p.DocumentId = d.DocumentId
ORDER BY d.DocumentId;

SELECT r.*
FROM edfi.Student r
JOIN page p ON p.DocumentId = r.DocumentId
ORDER BY r.DocumentId;

SELECT c.*
FROM edfi.StudentAddress c
JOIN page p ON p.DocumentId = c.Student_DocumentId
ORDER BY c.Student_DocumentId, c.Ordinal;

-- ...one SELECT per child table in the compiled plan...
```

Execute with a single `DbCommand` and read with `DbDataReader.NextResult()` (or Dapper `QueryMultiple`).

Notes:
- This avoids a second DB round-trip to “hydrate the page”.
- The reconstituter can still materialize the page `DocumentId`s in memory (page-sized) as it reads the first result set for later descriptor/reference expansion.

### 6.2 Descriptor expansion (batched)

Across all fetched rows:
- collect all distinct `DescriptorId`s
- fetch `SELECT DocumentId, Uri, Discriminator FROM dms.Descriptor WHERE DocumentId IN (...)`
- map descriptor ids to URI strings for JSON writing

### 6.3 Reference expansion (identity projection)

To reconstitute a document reference object, DMS must emit the referenced resource’s identity fields (not the referenced `DocumentId`).

This requires an **IdentityProjectionPlan** per referenced resource type that returns:

`(TargetDocumentId, IdentityField1Value, IdentityField2Value, ...)`

Important: identity fields can themselves include reference identities (join chains). This is handled by generating a join plan derived from the referenced resource’s own schema and its reference FK columns.

Execution strategy:
- For the page, collect all FK `DocumentId`s per target resource type across all results.
- For each target resource type, run one batched identity projection query (chunk if needed).
- Populate a dictionary `(targetType, targetDocumentId) → identity value bag`.

### 6.4 JSON assembly (fast, shape-safe)

Use `Utf8JsonWriter` to avoid building large intermediate `JsonNode` graphs:
- write scalars from root/child rows using the compiled column→jsonPath writers
- write arrays in `Ordinal` order
- write references by looking up identity value bags
- write descriptor strings by looking up `dms.Descriptor.Uri`
- inject DMS envelope fields (`id`, `_etag`, `_lastModifiedDate`) from `dms.Document`

Array presence rule (recommended):
- If the array has rows, write it.
- If the array has no rows:
  - write `[]` only if the JSON schema marks the property as required
  - otherwise omit the property (canonical “minimal JSON”)

---

## 7. Concrete C# Shapes (No Per-Resource Codegen)

This section defines the **concrete model and plan objects** that allow DMS to:

- derive a relational mapping from `ApiSchema.json` once (per schema version)
- compile SQL + bindings once (per schema version)
- execute POST/PUT/GET with **no per-resource code generation** and without N+1 inserts/queries

Think of these objects in **three layers**:

1. **Shape layer** (`RelationalResourceModel`): what tables/columns/keys exist for a resource and how they correspond to JSON paths.
2. **Plan layer** (`ResourceWritePlan`, `ResourceReadPlan`, `IdentityProjectionPlan`): compiled SQL plus precomputed details needed for efficient execution.
3. **Execution layer** (`IResourceFlattener`, `IBulkInserter`, `IResourceReconstituter`): runtime services that consume the plans.

All three layers are cached by `(DmsInstanceId, EffectiveSchemaHash, ProjectName, ResourceName)` so the per-request cost is only: reference resolution + row materialization + SQL execution + JSON writing.

### 7.1 Value and naming primitives

```csharp
/// <summary>
/// Identifies an API resource type in a way that matches ApiSchema.json semantics.
/// This is NOT a physical table name. It is used as the key for caching compiled models and plans.
/// </summary>
public readonly record struct QualifiedResourceName(string ProjectName, string ResourceName);

/// <summary>
/// Physical database schema name (e.g. "edfi", "tpdm", "dms").
/// </summary>
public readonly record struct DbSchemaName(string Value);

/// <summary>
/// Physical database table name (schema + local name).
/// </summary>
public readonly record struct DbTableName(DbSchemaName Schema, string Name)
{
    public override string ToString() => $"{Schema.Value}.{Name}";
}

/// <summary>
/// Physical database column name.
/// </summary>
public readonly record struct DbColumnName(string Value);

/// <summary>
/// The semantic role of a column within the relational mapping.
/// </summary>
public enum ColumnKind
{
    /// <summary>
    /// A scalar value copied from JSON into a typed relational column.
    /// </summary>
    Scalar,

    /// <summary>
    /// A foreign key to a concrete resource row (stored as BIGINT DocumentId).
    /// Column naming convention: ..._DocumentId
    /// </summary>
    DocumentFk,

    /// <summary>
    /// A foreign key to a descriptor document row (stored as BIGINT DocumentId).
    /// The referenced DocumentId is resolved from a descriptor URI via dms.ReferentialIdentity.
    /// Column naming convention: ..._DescriptorId
    /// </summary>
    DescriptorFk,

    /// <summary>
    /// Array order value for the current collection level.
    /// Ordinal is required to preserve array ordering during reconstitution.
    /// </summary>
    Ordinal,

    /// <summary>
    /// A key column that comes from the parent table’s key (e.g. ParentDocumentId and parent ordinals).
    /// With composite parent+ordinal keys, the child table’s key begins with one or more ParentKeyPart columns.
    /// </summary>
    ParentKeyPart
}

/// <summary>
/// Abstract scalar kinds supported by DMS relational mapping. These map to engine-specific SQL types.
/// </summary>
public enum ScalarKind
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Time
}

/// <summary>
/// The relational scalar type, including constraints that affect DDL and parameter binding.
/// </summary>
public sealed record RelationalScalarType(
    ScalarKind Kind,
    int? MaxLength = null,
    (int Precision, int Scale)? Decimal = null
);
```

### 7.2 JSON path compilation (avoid parsing JSONPath per value)

ApiSchema JSONPath strings used by DMS are structurally simple:

- property navigation (e.g. `$.nameOfInstitution`)
- array wildcard selection (e.g. `$.addresses[*].streetNumberName`)

For flatten/reconstitution we should treat JSONPath as a **restricted DSL**, compile it once, and avoid calling a general JSONPath engine for each value.

Rules for the compiled path representation:
- The canonical form is **absolute** (starts with `$`) to match ApiSchema content.
- `[*]` indicates “for each element”; the *table* determines the element context (via `JsonScope`), so per-column access can be compiled to a relative path at plan compile time.

```csharp
/// <summary>
/// A restricted JSON path segment used by the relational mapper.
/// Only property navigation and the "any array element" wildcard are required.
/// </summary>
public abstract record JsonPathSegment
{
    public sealed record Property(string Name) : JsonPathSegment;
    public sealed record AnyArrayElement : JsonPathSegment; // [*]
}

/// <summary>
/// A compiled representation of a JSON path used for:
/// - describing the scope (the JSON object/element represented by a table row)
/// - describing the source or destination location for a scalar/reference/descriptor value
///
/// Invariants:
/// - Canonical always starts with "$"
/// - Segments are normalized (no bracket quoting, no numeric indices)
/// </summary>
public readonly record struct JsonPathExpression(
    string Canonical,                  // canonical/normalized string form (no numeric indices)
    IReadOnlyList<JsonPathSegment> Segments
);
```

### 7.3 Relational resource model (shape layer)

The shape model is the output of the “derive from ApiSchema” step. It is:
- **resource-specific** (one per resource type)
- **fully explicit** (tables/columns/keys enumerated)
- **dialect-neutral** (no SQL strings inside)

```csharp
/// <summary>
/// Fully derived relational mapping for a single API resource type.
/// This is the canonical shape model used by:
/// - the migrator (DDL generation)
/// - runtime plan compilation (SQL generation + bindings)
/// - runtime validation helpers (e.g. expected descriptor resource type)
/// </summary>
/// <param name="Resource">Logical resource identity (ApiSchema project/resource).</param>
/// <param name="PhysicalSchema">Physical DB schema where resource tables live (e.g. "edfi").</param>
/// <param name="Root">The root table model (one row per document; key includes DocumentId).</param>
/// <param name="TablesInReadDependencyOrder">
/// Tables ordered for hydration (root first, then child tables, then nested child tables).
/// This order is used to emit SELECT result sets and to reconstitute efficiently without N+1 queries.
/// </param>
/// <param name="TablesInWriteDependencyOrder">
/// Tables ordered for writing (root first, then child tables in depth-first order).
/// This order is used to delete/insert child rows in a stable way (replace semantics).
/// </param>
/// <param name="ReferenceBindings">
/// The set of document-reference bindings derived from documentPathsMapping.referenceJsonPaths.
/// Each binding declares: which JSON reference object it came from, which table stores the FK, and how to reconstitute the identity object.
/// </param>
/// <param name="DescriptorBindings">
/// The set of descriptor-reference bindings derived from documentPathsMapping (descriptor paths).
/// Each binding declares: which JSON descriptor string path it came from, which table stores the FK, and which descriptor resource type is expected.
/// </param>
public sealed record RelationalResourceModel(
    QualifiedResourceName Resource,
    DbSchemaName PhysicalSchema,
    DbTableModel Root,
    IReadOnlyList<DbTableModel> TablesInReadDependencyOrder,
    IReadOnlyList<DbTableModel> TablesInWriteDependencyOrder,
    IReadOnlyList<ReferenceBinding> ReferenceBindings,
    IReadOnlyList<DescriptorBinding> DescriptorBindings
);

/// <summary>
/// Shape model for a single relational table.
/// For root resources, a row corresponds to the root JSON object.
/// For collection tables, a row corresponds to one array element object at the table’s JsonScope.
/// </summary>
/// <param name="Table">Physical table name.</param>
/// <param name="JsonScope">
/// The JSON scope represented by a single row:
/// - Root: "$"
/// - Child collection: "$.addresses[*]"
/// - Nested collection: "$.addresses[*].periods[*]"
/// The parent table is the table whose JsonScope is the longest proper prefix scope of this scope.
/// </param>
/// <param name="Key">
/// The primary key for the table.
/// Recommended scheme (composite parent+ordinal keys):
/// - Root: (DocumentId)
/// - Child: (ParentKeyPart..., Ordinal)
/// Invariant: for any child table, the sequence of ParentKeyPart columns must match the parent table’s Key columns (by position).
/// </param>
/// <param name="Columns">
/// All columns in the table, including:
/// - key columns (DocumentId / parent key parts / ordinal)
/// - scalar columns (typed)
/// - document reference FK columns (..._DocumentId)
/// - descriptor FK columns (..._DescriptorId)
/// Column order is significant: plan compilation uses it to define parameter ordering and row buffers.
/// </param>
/// <param name="Constraints">
/// Logical constraints derived from ApiSchema, used by DDL generation and for diagnostics:
/// - unique constraints (identityJsonPaths, arrayUniquenessConstraints)
/// - foreign keys (resource refs, descriptor refs, parent-child joins)
/// </param>
public sealed record DbTableModel(
    DbTableName Table,
    JsonPathExpression JsonScope,                 // "$" for root, "$.addresses[*]" for child, etc.
    TableKey Key,
    IReadOnlyList<DbColumnModel> Columns,
    IReadOnlyList<TableConstraint> Constraints
);

/// <summary>
/// A primary key description. Key column kinds should be:
/// - Root: a single ParentKeyPart column holding DocumentId
/// - Child: ParentKeyPart columns followed by Ordinal
/// </summary>
public sealed record TableKey(IReadOnlyList<DbKeyColumn> Columns);

/// <summary>
/// A single key column. The ColumnKind describes its semantic meaning.
/// </summary>
public sealed record DbKeyColumn(DbColumnName ColumnName, ColumnKind Kind);

/// <summary>
/// A table column description used for both DDL and runtime binding.
/// </summary>
/// <param name="ColumnName">Physical column name.</param>
/// <param name="Kind">Semantic kind (Scalar, DocumentFk, DescriptorFk, Ordinal, ParentKeyPart).</param>
/// <param name="ScalarType">Scalar type definition for Kind=Scalar; otherwise null.</param>
/// <param name="IsNullable">Whether the column can be null (DDL + parameter binding).</param>
/// <param name="SourceJsonPath">
/// The absolute JSON path to the value in the API document.
/// For columns in a collection table, this path is still absolute (e.g. "$.addresses[*].streetNumberName").
/// Plan compilation will turn this into a relative accessor for the per-row scope to avoid wildcard evaluation per value.
/// </param>
/// <param name="TargetResource">
/// For Kind=DocumentFk: the referenced resource type.
/// For Kind=DescriptorFk: the descriptor resource type (used to compute/validate descriptor referential identity).
/// Null for scalar/ordinal/parent-key columns.
/// </param>
public sealed record DbColumnModel(
    DbColumnName ColumnName,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    JsonPathExpression? SourceJsonPath,            // null for derived columns (ParentKey/Ordinal)
    QualifiedResourceName? TargetResource          // for DocumentFk / DescriptorFk
);

/// <summary>
/// Logical constraints that the migrator can translate into physical DDL.
/// Names are deterministic so that constraint-violation errors can be mapped back to API concepts.
/// </summary>
public abstract record TableConstraint
{
    public sealed record Unique(string Name, IReadOnlyList<DbColumnName> Columns) : TableConstraint;
    public sealed record ForeignKey(
        string Name,
        IReadOnlyList<DbColumnName> Columns,
        DbTableName TargetTable,
        IReadOnlyList<DbColumnName> TargetColumns
    ) : TableConstraint;
}

/// <summary>
/// Declares how a document reference object is stored and later reconstituted.
/// </summary>
/// <param name="ReferenceObjectPath">
/// Path to the reference object itself in the JSON document (not to the individual identity fields).
/// Examples: "$.schoolReference", "$.students[*].studentReference".
/// This path is used to:
/// - locate the correct table scope (root vs a child table)
/// - reconstitute the nested reference object at the right location
/// </param>
/// <param name="Table">The table where the FK column is stored (root or child).</param>
/// <param name="FkColumn">The FK column holding the referenced DocumentId.</param>
/// <param name="TargetResource">The referenced resource type (project + resource).</param>
/// <param name="FieldMappings">
/// The ordered identity field mappings derived from ApiSchema documentPathsMapping.referenceJsonPaths.
/// These mappings are used for:
/// - write-time identity hash computation (already done by Core)
/// - read-time identity projection: which values must be selected from the referenced resource to reconstitute the reference object
/// </param>
public sealed record ReferenceBinding(
    JsonPathExpression ReferenceObjectPath,        // e.g. "$.schoolReference" or "$.students[*].studentReference"
    DbTableName Table,
    DbColumnName FkColumn,                         // e.g. "School_DocumentId"
    QualifiedResourceName TargetResource,
    IReadOnlyList<ReferenceFieldMapping> FieldMappings
);

/// <summary>
/// Maps one identity field in the referencing document’s reference object to the corresponding identity JSONPath in the referenced resource.
/// </summary>
/// <param name="ReferenceJsonPath">Absolute path to the identity field inside the reference object in the referencing document.</param>
/// <param name="TargetIdentityJsonPath">Absolute path to the corresponding identity field in the referenced document.</param>
public sealed record ReferenceFieldMapping(
    JsonPathExpression ReferenceJsonPath,          // where to write in the referencing document
    JsonPathExpression TargetIdentityJsonPath      // where to read from the referenced document identity
);

/// <summary>
/// Declares how a descriptor string (URI) is stored and later reconstituted.
/// </summary>
/// <param name="DescriptorValuePath">Absolute path to the descriptor URI string in the JSON document.</param>
/// <param name="Table">The table where the FK column is stored (root or child).</param>
/// <param name="FkColumn">The FK column holding the descriptor DocumentId (BIGINT).</param>
/// <param name="DescriptorResource">
/// The descriptor resource type expected at this path (e.g. ("EdFi","GradeLevelDescriptor")).
/// This is used for:
/// - write-time validation (descriptor referential id must exist in dms.ReferentialIdentity)
/// - query-time resolution (descriptor URI → descriptor DocumentId, via referential id)
/// - optional DB-level enforcement via triggers (later)
/// </param>
public sealed record DescriptorBinding(
    JsonPathExpression DescriptorValuePath,        // path of the string descriptor URI in JSON
    DbTableName Table,
    DbColumnName FkColumn,                         // ..._DescriptorId
    QualifiedResourceName DescriptorResource        // e.g. ("EdFi","GradeLevelDescriptor")
);
```

Notes:
- `ReferenceBinding.FieldMappings` is derived from existing `documentPathsMapping.referenceJsonPaths`.
- `TargetIdentityJsonPath` is used to drive identity projection join planning (section 7.5).

### 7.4 Write and read plans (plan layer)

The plan layer is compiled from the shape model + SQL dialect + runtime conventions.

Plans contain:
- SQL strings (parameterized)
- binding rules (column order, batching limits, expected keyset shape)
- *no* per-resource code; only metadata + generic executors

```csharp
/// <summary>
/// Compiled write plan for a single resource type.
/// Used by POST and PUT to perform inserts/updates with replace-semantics for collections.
/// </summary>
public sealed record ResourceWritePlan(
    RelationalResourceModel Model,
    IReadOnlyDictionary<DbTableName, TableWritePlan> TablePlans
);

/// <summary>
/// Compiled write plan for one table.
/// </summary>
/// <param name="TableModel">Shape model for the table.</param>
/// <param name="InsertSql">
/// Parameterized SQL for inserting one batch into this table.
/// Convention: parameters are ordered to match DbTableModel.Columns for each row buffer.
/// </param>
/// <param name="UpdateSql">
/// Parameterized SQL for updating the root table (child tables typically use delete+insert).
/// Null for pure child tables.
/// </param>
/// <param name="DeleteByParentSql">
/// Parameterized SQL to delete existing child rows for a given parent key (replace semantics).
/// Null for the root table.
/// </param>
/// <param name="MaxRowsPerBatch">
/// Maximum rows per batch for multi-row INSERT to stay within engine limits (e.g. SQL Server parameter limits).
/// </param>
public sealed record TableWritePlan(
    DbTableModel TableModel,
    string InsertSql,
    string? UpdateSql,
    string? DeleteByParentSql,
    int MaxRowsPerBatch,
    IReadOnlyList<WriteColumnBinding> ColumnBindings
);

/// <summary>
/// Declares how to produce one column value for a given table row during flattening.
/// This is compiled once per schema version so per-row execution is a tight loop.
/// </summary>
/// <param name="Column">The column being populated.</param>
/// <param name="Source">Where the value comes from (parent key, ordinal, scalar json value, reference resolution, ...).</param>
public sealed record WriteColumnBinding(DbColumnModel Column, WriteValueSource Source);

/// <summary>
/// The source for a write-time column value.
/// Note: the plan compiler is free to further compile these into delegates for maximum performance.
/// </summary>
public abstract record WriteValueSource
{
    /// <summary>
    /// The root DocumentId for the resource being written.
    /// </summary>
    public sealed record DocumentId() : WriteValueSource;

    /// <summary>
    /// One component of the parent table key, by index in the parent key.
    /// For nested collections this includes parent ordinals as well as the parent DocumentId.
    /// </summary>
    public sealed record ParentKeyPart(int Index) : WriteValueSource;

    /// <summary>
    /// The ordinal of the current array element at this table’s JsonScope.
    /// </summary>
    public sealed record Ordinal() : WriteValueSource;

    /// <summary>
    /// A scalar value read from JSON, relative to the table scope node.
    /// The compiled plan should prefer relative paths here to avoid wildcard evaluation.
    /// </summary>
    public sealed record Scalar(JsonPathExpression RelativePath, RelationalScalarType Type) : WriteValueSource;

    /// <summary>
    /// A document reference FK value.
    ///
    /// With Option B (section 5.2.1), the referential id is computed by Core and emitted with concrete JSON location.
    /// The backend uses a per-request index keyed by:
    /// - this binding (which identifies the wildcard reference-object path and the FK column)
    /// - the current row's OrdinalPath (array indices from root to the current scope)
    /// to return the referenced DocumentId without per-row hashing.
    /// </summary>
    public sealed record DocumentReference(ReferenceBinding Binding) : WriteValueSource;

    /// <summary>
    /// A descriptor FK value.
    /// The flattener reads the descriptor URI string, normalizes it, and uses (normalizedUri, descriptor resource type)
    /// to look up the descriptor DocumentId in ResolvedReferenceSet.DescriptorIdByKey (populated via dms.ReferentialIdentity resolution).
    /// </summary>
    public sealed record DescriptorReference(DescriptorBinding Binding, JsonPathExpression RelativePath)
        : WriteValueSource;
}

/// <summary>
/// Compiled read/reconstitution plan for a single resource type.
/// </summary>
public sealed record ResourceReadPlan(
    RelationalResourceModel Model,
    IReadOnlyDictionary<DbTableName, TableReadPlan> TablePlans,
    IReadOnlyDictionary<QualifiedResourceName, IdentityProjectionPlan> IdentityProjectionPlans
);

/// <summary>
/// Compiled hydration SQL for a single table based on a materialized keyset named "page".
/// </summary>
/// <param name="SelectByKeysetSql">
/// A SELECT statement that returns all rows needed for the page, by joining to a keyset named "page"
/// containing a BIGINT column named "DocumentId".
/// The reconstituter is responsible for materializing this keyset (temp table / table variable) before running table SELECTs.
/// </param>
public sealed record TableReadPlan(
    DbTableModel TableModel,
    string SelectByKeysetSql            // expects a keyset named `page` with a `DocumentId` column
);
```

### 7.5 Identity projection (reference reconstitution)

```csharp
/// <summary>
/// A compiled SQL plan that projects the identity fields for a referenced resource type.
/// Given a set of referenced DocumentIds, this returns the values needed to reconstitute reference objects.
/// </summary>
/// <param name="Resource">The referenced resource type being projected.</param>
/// <param name="Sql">
/// Parameterized SQL that returns:
/// - DocumentId
/// - one column per identity field (with stable aliases)
/// The plan compiler may include joins to other resource tables if the identity contains reference identities.
/// </param>
/// <param name="Fields">
/// The identity fields (in ApiSchema identity order) and their SQL aliases.
/// Used to populate JSON reference objects deterministically.
/// </param>
public sealed record IdentityProjectionPlan(
    QualifiedResourceName Resource,
    string Sql,
    IReadOnlyList<IdentityField> Fields
);

/// <summary>
/// One identity field returned by an IdentityProjectionPlan.
/// </summary>
public sealed record IdentityField(
    JsonPathExpression IdentityJsonPath,    // e.g. "$.schoolId" or "$.schoolReference.schoolId"
    string SqlAlias                         // column alias in the projection result set
);
```

The `IdentityProjectionPlan` builder:
- takes a resource’s `identityJsonPaths`
- maps each to either:
  - a scalar column on the resource root table, or
  - a join chain through FK columns to another resource’s identity columns
- emits a single SQL query that returns all identity values for the requested `DocumentId`s

### 7.6 Execution interfaces (execution layer)

These are the runtime services that use the compiled plans.

```csharp
public interface IRelationalModelCache
{
    RelationalResourceModel GetOrAdd(string effectiveSchemaHash, QualifiedResourceName resource, Func<RelationalResourceModel> build);
}

public interface IResourcePlanProvider
{
    ResourceWritePlan GetWritePlan(string effectiveSchemaHash, QualifiedResourceName resource);
    ResourceReadPlan GetReadPlan(string effectiveSchemaHash, QualifiedResourceName resource);
}

public interface IResourceFlattener
{
    FlattenedWriteSet Flatten(
        ResourceWritePlan plan,
        long documentId,
        System.Text.Json.Nodes.JsonNode document,
        IDocumentReferenceInstanceIndex documentReferences,
        ResolvedReferenceSet resolved);
}

/// <summary>
/// Per-request resolved lookups used during flattening to populate FK columns without per-row DB queries.
/// </summary>
/// <param name="DocumentIdByReferentialId">
/// Maps a referenced resource’s referential id (UUIDv5) to its DocumentId.
/// Resolved via dms.ReferentialIdentity and used for document references.
/// </param>
/// <param name="DescriptorIdByKey">
/// Maps (normalized URI, descriptor resource type) to a descriptor DocumentId.
/// This is a convenience map derived from Core-extracted descriptor references after referential-id resolution.
/// Used to populate descriptor FK columns without per-row referential-id hashing.
/// </param>
public sealed record ResolvedReferenceSet(
    IReadOnlyDictionary<Guid, long> DocumentIdByReferentialId,
    IReadOnlyDictionary<DescriptorKey, long> DescriptorIdByKey);

/// <summary>
/// Key used for resolving descriptor URI strings to descriptor DocumentIds without per-row referential-id hashing.
/// The URI must be normalized (lowercase) to match DMS canonicalization behavior.
/// </summary>
public readonly record struct DescriptorKey(string NormalizedUri, QualifiedResourceName DescriptorResource);

/// <summary>
/// Resolves extracted document-reference instances to referenced DocumentIds for a single write request.
///
/// Key idea (Option B):
/// - Core extraction emits each reference instance with a concrete JSONPath including indices.
/// - Backend resolves ReferentialId → DocumentId in bulk once.
/// - This index maps the current row's OrdinalPath to the referenced DocumentId without per-row hashing or DB I/O.
///
/// OrdinalPath definition:
/// - The sequence of array indices from the root to the current table scope.
/// - Root scope: empty.
/// - Child scope "$.addresses[*]": [addressOrdinal]
/// - Nested "$.addresses[*].periods[*]": [addressOrdinal, periodOrdinal]
/// </summary>
public interface IDocumentReferenceInstanceIndex
{
    /// <summary>
    /// Returns the referenced DocumentId for the given binding at the given ordinal path, or null if the reference
    /// object is not present at that location (optional reference).
    /// </summary>
    long? GetReferencedDocumentId(ReferenceBinding binding, ReadOnlySpan<int> ordinalPath);
}

/// <summary>
/// Default implementation of <see cref="IDocumentReferenceInstanceIndex"/> that builds a fast per-request lookup
/// from Core-extracted reference instances.
///
/// Implementation notes:
/// - This is intentionally independent of any ORM and does not require per-resource CLR types.
/// - Lookups must not allocate in the row materialization hot loop, so this uses:
///   hash(ordinalPath) → small bucket → ordinalPath sequence compare.
/// - Build cost is proportional to the number of references in the input document (not the number of rows written).
/// </summary>
public sealed class DocumentReferenceInstanceIndex : IDocumentReferenceInstanceIndex
{
    private readonly IReadOnlyDictionary<ReferenceBinding, OrdinalPathMap<long>> _mapByBinding;

    private DocumentReferenceInstanceIndex(IReadOnlyDictionary<ReferenceBinding, OrdinalPathMap<long>> mapByBinding)
    {
        _mapByBinding = mapByBinding;
    }

    public long? GetReferencedDocumentId(ReferenceBinding binding, ReadOnlySpan<int> ordinalPath)
    {
        if (!_mapByBinding.TryGetValue(binding, out var map))
        {
            return null;
        }

        return map.TryGet(ordinalPath, out var documentId) ? documentId : null;
    }

    /// <summary>
    /// Builds the per-request index by combining:
    /// - the resource model bindings (to know which FK columns exist)
    /// - the Core-extracted reference instances (to know which reference occurs at which location)
    /// - the bulk-resolved mapping ReferentialId → DocumentId (to avoid any DB work here)
    ///
    /// Required Core enhancement for Option B:
    /// - each <c>DocumentReference</c> must carry a concrete JSONPath to the reference object instance (including indices),
    ///   e.g. <c>"$.addresses[2].periods[0].calendarReference"</c>.
    /// </summary>
    public static DocumentReferenceInstanceIndex Build(
        IReadOnlyList<ReferenceBinding> bindings,
        EdFi.DataManagementService.Core.External.Model.DocumentReferenceArray[] extractedReferenceArrays,
        IReadOnlyDictionary<Guid, long> documentIdByReferentialId)
    {
        // Map wildcard reference-object path → binding for fast association.
        // The wildcard path is the ReferenceBinding.ReferenceObjectPath (e.g. "$.addresses[*].periods[*].calendarReference").
        var bindingByPath = bindings.ToDictionary(b => b.ReferenceObjectPath.Canonical, b => b);

        var mapsByBinding = new Dictionary<ReferenceBinding, OrdinalPathMap<long>>();

        foreach (var array in extractedReferenceArrays)
        {
            if (!bindingByPath.TryGetValue(array.arrayPath.Value, out var binding))
            {
                // If this happens, ApiSchema and extraction disagree. Treat as a startup/schema error.
                throw new InvalidOperationException($"No ReferenceBinding found for extracted path '{array.arrayPath.Value}'.");
            }

            if (!mapsByBinding.TryGetValue(binding, out var map))
            {
                map = new OrdinalPathMap<long>();
                mapsByBinding.Add(binding, map);
            }

            foreach (var reference in array.DocumentReferences)
            {
                var ordinalPath = OrdinalPathParser.Parse(reference.Path.Value);
                var documentId = documentIdByReferentialId[reference.ReferentialId.Value];
                map.Add(ordinalPath, documentId);
            }
        }

        return new(mapsByBinding);
    }

    /// <summary>
    /// A small per-binding map from ordinal paths to values that supports allocation-free lookups using ReadOnlySpan.
    /// </summary>
    private sealed class OrdinalPathMap<TValue>
    {
        private readonly Dictionary<int, List<Entry>> _buckets = new();

        public void Add(int[] ordinalPath, TValue value)
        {
            var hash = OrdinalPathHash.Hash(ordinalPath);
            if (!_buckets.TryGetValue(hash, out var bucket))
            {
                bucket = [];
                _buckets.Add(hash, bucket);
            }
            bucket.Add(new Entry(ordinalPath, value));
        }

        public bool TryGet(ReadOnlySpan<int> ordinalPath, out TValue value)
        {
            var hash = OrdinalPathHash.Hash(ordinalPath);
            if (!_buckets.TryGetValue(hash, out var bucket))
            {
                value = default!;
                return false;
            }

            foreach (var entry in bucket)
            {
                if (ordinalPath.SequenceEqual(entry.OrdinalPath))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        private readonly record struct Entry(int[] OrdinalPath, TValue Value);
    }

    private static class OrdinalPathHash
    {
        public static int Hash(ReadOnlySpan<int> ordinalPath)
        {
            var hc = new HashCode();
            hc.Add(ordinalPath.Length);
            for (var i = 0; i < ordinalPath.Length; i++)
            {
                hc.Add(ordinalPath[i]);
            }
            return hc.ToHashCode();
        }

        public static int Hash(int[] ordinalPath) => Hash(ordinalPath.AsSpan());
    }

    /// <summary>
    /// Parses a canonical JSONPath that contains numeric indices (e.g. "$.a[2].b[0].c") into an ordinal path [2,0].
    ///
    /// This is used only during index build (once per reference instance), not during the row materialization hot loop.
    /// </summary>
    private static class OrdinalPathParser
    {
        public static int[] Parse(string jsonPathWithIndices)
        {
            // Minimal parser: scan for [digits] segments.
            // Assumes paths have already been canonicalized by Core (JsonHelpers.ConvertPath → JsonPath.Parse → ToString()).
            var ordinals = new List<int>(capacity: 2);

            for (var i = 0; i < jsonPathWithIndices.Length; i++)
            {
                if (jsonPathWithIndices[i] != '[') continue;

                var value = 0;
                var j = i + 1;
                while (j < jsonPathWithIndices.Length && char.IsDigit(jsonPathWithIndices[j]))
                {
                    value = (value * 10) + (jsonPathWithIndices[j] - '0');
                    j++;
                }

                if (j < jsonPathWithIndices.Length && jsonPathWithIndices[j] == ']')
                {
                    ordinals.Add(value);
                    i = j;
                }
            }

            return ordinals.ToArray();
        }
    }
}

public sealed record FlattenedWriteSet(
    DbTableName RootTable,
    RowBuffer RootRow,
    IReadOnlyDictionary<DbTableName, IReadOnlyList<RowBuffer>> ChildRows
);

public sealed record RowBuffer(IReadOnlyList<object?> Values);

public interface IResourceReconstituter
{
    Task<ReconstitutedPage> ReconstituteAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken cancellationToken);
}

public abstract record PageKeysetSpec
{
    public sealed record Single(long DocumentId) : PageKeysetSpec;

    /// <summary>
    /// A parameterized SQL that selects DocumentIds for the requested page, in stable page order.
    /// The reconstituter will materialize this into a keyset named `page` and hydrate tables by joining to it.
    /// </summary>
    public sealed record Query(
        string PageDocumentIdSql,
        IReadOnlyDictionary<string, object?> Parameters,
        string? TotalCountSql = null)
        : PageKeysetSpec;
}

public sealed record ReconstitutedDocument(long DocumentId, byte[] Json);

public sealed record ReconstitutedPage(IReadOnlyList<ReconstitutedDocument> Items, long? TotalCount = null);
```

Database-specific execution (dialect + bulk):

```csharp
public interface ISqlDialect
{
    string Parameter(string name);               // "@x"
    string QuoteIdent(string ident);             // "\"Foo\"" or "[Foo]"
    string Paging(string orderBy, int offset, int limit);
}

public interface IBulkInserter
{
    Task InsertAsync(DbConnection connection, DbTransaction tx, DbTableModel table, IReadOnlyList<RowBuffer> rows, CancellationToken ct);
}
```

Dapper is optional:
- use it for `QueryMultiple` and basic row reads if desired
- avoid mapping to per-resource CLR types; materialize into `RowBuffer`/`DbDataReader`-backed structures

### 7.7 Example: Pre-compilation (startup or migrator)

This shows how the shape model and plans are compiled and cached. The same builder can be used by:
- the migrator (DDL generation)
- runtime (plan compilation + caching)

```csharp
public sealed class RelationalPlanProvider(
    IRelationalModelCache modelCache,
    ISqlDialect dialect,
    IApiSchemaProvider apiSchemaProvider)
    : IResourcePlanProvider
{
    public ResourceWritePlan GetWritePlan(string effectiveSchemaHash, QualifiedResourceName resource)
    {
        var model = modelCache.GetOrAdd(
            effectiveSchemaHash,
            resource,
            build: () =>
            {
                var resourceSchema = apiSchemaProvider.GetResourceSchema(resource.ProjectName, resource.ResourceName);
                return RelationalResourceModelBuilder.Build(resourceSchema);
            });

        return RelationalPlanCompiler.CompileWritePlan(model, dialect);
    }

    public ResourceReadPlan GetReadPlan(string effectiveSchemaHash, QualifiedResourceName resource)
    {
        var model = modelCache.GetOrAdd(
            effectiveSchemaHash,
            resource,
            build: () =>
            {
                var resourceSchema = apiSchemaProvider.GetResourceSchema(resource.ProjectName, resource.ResourceName);
                return RelationalResourceModelBuilder.Build(resourceSchema);
            });

        return RelationalPlanCompiler.CompileReadPlan(model, dialect, apiSchemaProvider);
    }
}
```

Key points:
- `RelationalResourceModelBuilder.Build(...)` is where `jsonSchemaForInsert` + `documentPathsMapping` + `identityJsonPaths` are turned into explicit tables/columns/keys.
- `RelationalPlanCompiler` is where physical names are quoted, parameter styles are applied, batching limits are chosen, and identity projection SQL is derived.

### 7.8 Example: POST/PUT execution (flatten + write)

```csharp
public async Task UpsertAsync(IUpsertRequest request, CancellationToken ct)
{
    var effectiveSchemaHash = _effectiveSchemaProvider.EffectiveSchemaHash;
    var resource = new QualifiedResourceName(request.ResourceInfo.ProjectName.Value, request.ResourceInfo.ResourceName.Value);

    // 1) Compile-or-get the write plan for this resource.
    var writePlan = _planProvider.GetWritePlan(effectiveSchemaHash, resource);

    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var tx = await connection.BeginTransactionAsync(ct);

    // 2) Resolve identity (insert vs update) and obtain DocumentId.
    //    (dms.ReferentialIdentity and dms.Document writes are not shown here.)
    var documentId = await _documentIdAllocator.GetOrCreateDocumentIdAsync(request, connection, tx, ct);

    // 3) Resolve all FK ids in bulk.
    var resolved = await _referenceResolver.ResolveAsync(request.DocumentInfo, connection, tx, ct);

    // 3b) Build a per-request index that maps (binding + ordinalPath) → referenced DocumentId.
    //     This depends on Core emitting concrete JSON paths (including indices) for reference instances (Option B).
    var documentReferences = DocumentReferenceInstanceIndex.Build(
        writePlan.Model.ReferenceBindings,
        request.DocumentInfo.DocumentReferenceArrays,
        resolved.DocumentIdByReferentialId);

    // 4) Flatten JSON into typed rows (no DB calls here).
    var writeSet = _flattener.Flatten(
        writePlan,
        documentId,
        request.EdfiDoc /* JsonNode */,
        documentReferences,
        resolved);

    // 5) Execute root + child table writes in plan order (set-based).
    await _writer.ExecuteAsync(writePlan, documentId, writeSet, connection, tx, ct);

    await tx.CommitAsync(ct);
}
```

Notes:
- `_referenceResolver.ResolveAsync(...)` resolves both document and descriptor referential ids via `dms.ReferentialIdentity` (and may optionally validate descriptor DocumentIds exist in `dms.Descriptor`).
- `_writer.ExecuteAsync(...)` uses `TableWritePlan.DeleteByParentSql` + `IBulkInserter` to avoid N+1 inserts.

Flattening inner loop sketch (how `TableWritePlan.ColumnBindings` gets used):

```csharp
private static RowBuffer MaterializeRow(
    TableWritePlan tablePlan,
    long documentId,
    JsonNode scopeNode,
    IReadOnlyList<long> parentKeyParts, // e.g. [DocumentId] or [DocumentId, parentOrdinal]
    int ordinal,
    ReadOnlySpan<int> ordinalPath,      // e.g. [] (root), [2] (child), [2,0] (nested)
    IDocumentReferenceInstanceIndex documentReferences,
    ResolvedReferenceSet resolved)
{
    var values = new object?[tablePlan.ColumnBindings.Count];

    for (var i = 0; i < tablePlan.ColumnBindings.Count; i++)
    {
        values[i] = tablePlan.ColumnBindings[i].Source switch
        {
            WriteValueSource.DocumentId => documentId,
            WriteValueSource.ParentKeyPart(var index) => parentKeyParts[index],
            WriteValueSource.Ordinal => ordinal,
            WriteValueSource.Scalar(var relPath, var type) => JsonValueReader.Read(scopeNode, relPath, type),

            WriteValueSource.DescriptorReference(var binding, var relPath)
                => ResolveDescriptorId(scopeNode, binding, relPath, resolved),

            WriteValueSource.DocumentReference(var binding)
                => ResolveReferencedDocumentId(binding, ordinalPath, documentReferences),

            _ => throw new InvalidOperationException("Unsupported write value source")
        };
    }

    return new(values);
}

private static long ResolveDescriptorId(
    JsonNode scopeNode,
    DescriptorBinding binding,
    JsonPathExpression relPath,
    ResolvedReferenceSet resolved)
{
    var normalizedUri = JsonValueReader.ReadString(scopeNode, relPath).ToLowerInvariant();
    return resolved.DescriptorIdByKey[new DescriptorKey(normalizedUri, binding.DescriptorResource)];
}

private static long? ResolveReferencedDocumentId(
    ReferenceBinding binding,
    ReadOnlySpan<int> ordinalPath,
    IDocumentReferenceInstanceIndex documentReferences)
{
    return documentReferences.GetReferencedDocumentId(binding, ordinalPath);
}
```

### 7.9 Example: GET by id execution (keyset spec)

```csharp
public async Task<byte[]?> GetByIdAsync(Guid documentUuid, CancellationToken ct)
{
    var effectiveSchemaHash = _effectiveSchemaProvider.EffectiveSchemaHash;
    var resource = new QualifiedResourceName("EdFi", "Student");
    var readPlan = _planProvider.GetReadPlan(effectiveSchemaHash, resource);

    // Resolve UUID → DocumentId (dms.Document)
    var documentId = await _documentLookup.ResolveDocumentIdAsync(documentUuid, ct);
    if (documentId is null) return null;

    var page = await _reconstituter.ReconstituteAsync(readPlan, new PageKeysetSpec.Single(documentId.Value), ct);
    return page.Items.Single().Json;
}
```

### 7.10 Example: GET by query execution (keyset spec + totalCount)

```csharp
public async Task<ReconstitutedPage> QueryAsync(IQueryRequest request, CancellationToken ct)
{
    var effectiveSchemaHash = _effectiveSchemaProvider.EffectiveSchemaHash;
    var resource = new QualifiedResourceName(request.ResourceInfo.ProjectName.Value, request.ResourceInfo.ResourceName.Value);
    var readPlan = _planProvider.GetReadPlan(effectiveSchemaHash, resource);

    // Compile the page DocumentId SQL using ApiSchema queryFieldMapping → columns.
    var compiled = _querySqlCompiler.CompilePageDocumentIdSql(request);

    var keyset = new PageKeysetSpec.Query(
        PageDocumentIdSql: compiled.PageDocumentIdSql,
        Parameters: compiled.Parameters,
        TotalCountSql: request.PaginationParameters.TotalCount ? compiled.TotalCountSql : null);

    return await _reconstituter.ReconstituteAsync(readPlan, keyset, ct);
}
```

Notes:
- The query compiler is responsible for emitting *stable ordering*.
- The reconstituter is responsible for:
  1) materializing the `page` keyset from `PageDocumentIdSql`
  2) running all `TableReadPlan.SelectByKeysetSql` statements (multi-resultset)
  3) performing descriptor expansion and identity projection in batched follow-ups (or as additional result sets)

Reconstitution inner loop sketch (single round-trip hydration with `NextResult()`):

```csharp
public async Task<ReconstitutedPage> ReconstituteAsync(
    ResourceReadPlan plan,
    PageKeysetSpec keyset,
    CancellationToken ct)
{
    await using var connection = await _dataSource.OpenConnectionAsync(ct);
    await using var cmd = connection.CreateCommand();

    // One command text contains:
    // - "materialize page keyset" SQL (from keyset spec)
    // - a SELECT for dms.Document joined to page
    // - one SELECT per TableReadPlan.SelectByKeysetSql
    cmd.CommandText = SqlBatchBuilder.Build(plan, keyset);
    SqlBatchBuilder.AddParameters(cmd, keyset);

    await using var reader = await cmd.ExecuteReaderAsync(ct);

    var documentMetadata = ReadDocumentRows(reader); // dms.Document JOIN page

    foreach (var table in plan.Model.TablesInReadDependencyOrder)
    {
        await reader.NextResultAsync(ct);
        ReadTableRows(reader, table); // grouped by (parent key parts..., ordinal)
    }

    // Optional: descriptor + identity projection follow-ups can either be:
    // - additional result sets in this same command, or
    // - a second command (still batched, page-sized)
    return AssembleJson(plan, documentMetadata /* + tables + lookups */);
}
```

---

## 8. Performance Notes / Failure Modes

- **N+1 writes**: prevented by per-table batched inserts; nested collections avoid identity capture via composite keys.
- **N+1 reads**: prevented by per-table batched selects; reference identity projection is grouped per target resource type.
- **Network traffic**: minimized by multi-resultset reads and batching, plus optional L1/L2 caches for identity/descriptor lookups.
- **Schema complexity**: the model builder must validate supported JSON schema constructs and fail migration/startup for unsupported patterns (e.g., uncontrolled `additionalProperties`).

---

## 9. Next Steps (Design → Implementation)

1. Use composite parent+ordinal keys for child tables (as described above) and reflect this in the migrator DDL rules.
2. Define exact `relational` block JSON schema and add it to `JsonSchemaForApiSchema.json`.
3. Implement a shared `RelationalResourceModelBuilder` (used by both migrator and runtime).
4. Implement Postgres + SQL Server dialects for paging and bulk insert paths.
5. Prototype end-to-end on one resource with nested collections (e.g., `School` addresses → periods).
