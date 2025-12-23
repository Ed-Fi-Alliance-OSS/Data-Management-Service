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
- Descriptor binding plan: descriptor JSON paths → FK columns (to `dms.Descriptor`) and expected discriminator
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
  - descriptor references (with JSON paths)
  - document references (referential ids; array groupings include JSON paths)

### 5.2 Reference & descriptor resolution (bulk)

Before writing resource tables:

- Resolve all **document references**:
  - `dms.ReferentialIdentity`: `ReferentialId → DocumentId`
- Resolve all **descriptor references**:
  - `dms.Descriptor`: `(Uri, Discriminator) → DescriptorDocumentId`

Cache these lookups aggressively (L1/L2 optional), but only populate caches after commit.

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

The shapes below are designed to:
- compile once per resource per schema version
- execute fast with minimal allocation
- isolate SQL dialect and bulk-loading differences

### 7.1 Value and naming primitives

```csharp
public readonly record struct QualifiedResourceName(string ProjectName, string ResourceName);

public readonly record struct DbSchemaName(string Value);
public readonly record struct DbTableName(DbSchemaName Schema, string Name)
{
    public override string ToString() => $"{Schema.Value}.{Name}";
}

public readonly record struct DbColumnName(string Value);

public enum ColumnKind
{
    Scalar,
    DocumentFk,     // ..._DocumentId
    DescriptorFk,   // ..._DescriptorId
    Ordinal,
    ParentKeyPart
}

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

public sealed record RelationalScalarType(
    ScalarKind Kind,
    int? MaxLength = null,
    (int Precision, int Scale)? Decimal = null
);
```

### 7.2 JSON path compilation (avoid parsing JSONPath per value)

ApiSchema paths are structurally simple (property navigation + `[*]` wildcards). Compile them to a light-weight segment list.

```csharp
public abstract record JsonPathSegment
{
    public sealed record Property(string Name) : JsonPathSegment;
    public sealed record AnyArrayElement : JsonPathSegment; // [*]
}

public readonly record struct JsonPathExpression(
    string Canonical,                  // original string for diagnostics
    IReadOnlyList<JsonPathSegment> Segments
);
```

### 7.3 Relational model (explicit, derived)

```csharp
public sealed record RelationalResourceModel(
    QualifiedResourceName Resource,
    DbSchemaName PhysicalSchema,
    DbTableModel Root,
    IReadOnlyList<DbTableModel> TablesInReadDependencyOrder,
    IReadOnlyList<DbTableModel> TablesInWriteDependencyOrder,
    IReadOnlyList<ReferenceBinding> ReferenceBindings,
    IReadOnlyList<DescriptorBinding> DescriptorBindings
);

public sealed record DbTableModel(
    DbTableName Table,
    JsonPathExpression JsonScope,                 // "$" for root, "$.addresses[*]" for child, etc.
    TableKey Key,
    IReadOnlyList<DbColumnModel> Columns,
    IReadOnlyList<TableConstraint> Constraints
);

public sealed record TableKey(IReadOnlyList<DbKeyColumn> Columns);

public sealed record DbKeyColumn(DbColumnName ColumnName, ColumnKind Kind);

public sealed record DbColumnModel(
    DbColumnName ColumnName,
    ColumnKind Kind,
    RelationalScalarType? ScalarType,
    bool IsNullable,
    JsonPathExpression? SourceJsonPath,            // null for derived columns (ParentKey/Ordinal)
    QualifiedResourceName? TargetResource          // for DocumentFk / DescriptorFk
);

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

public sealed record ReferenceBinding(
    JsonPathExpression ReferenceObjectPath,        // e.g. "$.schoolReference" or "$.students[*].studentReference"
    DbTableName Table,
    DbColumnName FkColumn,                         // e.g. "School_DocumentId"
    QualifiedResourceName TargetResource,
    IReadOnlyList<ReferenceFieldMapping> FieldMappings
);

public sealed record ReferenceFieldMapping(
    JsonPathExpression ReferenceJsonPath,          // where to write in the referencing document
    JsonPathExpression TargetIdentityJsonPath      // where to read from the referenced document identity
);

public sealed record DescriptorBinding(
    JsonPathExpression DescriptorValuePath,        // path of the string descriptor URI in JSON
    DbTableName Table,
    DbColumnName FkColumn,                         // ..._DescriptorId
    string ExpectedDiscriminator                   // e.g. "GradeLevelDescriptor"
);
```

Notes:
- `ReferenceBinding.FieldMappings` is derived from existing `documentPathsMapping.referenceJsonPaths`.
- `TargetIdentityJsonPath` is used to drive identity projection join planning (section 7.5).

### 7.4 Write and read plans (compiled for execution)

```csharp
public sealed record ResourceWritePlan(
    RelationalResourceModel Model,
    IReadOnlyDictionary<DbTableName, TableWritePlan> TablePlans
);

public sealed record TableWritePlan(
    DbTableModel TableModel,
    string InsertSql,
    string UpdateSql,                   // null for pure child tables
    string DeleteByParentSql,           // null for root
    int MaxRowsPerBatch
);

public sealed record ResourceReadPlan(
    RelationalResourceModel Model,
    IReadOnlyDictionary<DbTableName, TableReadPlan> TablePlans,
    IReadOnlyDictionary<QualifiedResourceName, IdentityProjectionPlan> IdentityProjectionPlans
);

public sealed record TableReadPlan(
    DbTableModel TableModel,
    string SelectByKeysetSql            // expects a keyset named `page` with a `DocumentId` column
);
```

### 7.5 Identity projection (reference reconstitution)

```csharp
public sealed record IdentityProjectionPlan(
    QualifiedResourceName Resource,
    string Sql,
    IReadOnlyList<IdentityField> Fields
);

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

### 7.6 Execution interfaces

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
    FlattenedWriteSet Flatten(ResourceWritePlan plan, long documentId, System.Text.Json.Nodes.JsonNode document);
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
