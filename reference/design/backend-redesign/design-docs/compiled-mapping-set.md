# Backend Redesign: Unified Mapping Models (In-Memory)

Status: Draft.

This document defines the **single shared in-memory object graph** used to represent schema-derived mapping artifacts in the backend redesign.

It exists to prevent drift between:
- DDL generation (`ddl-generation.md`)
- runtime compilation and execution (`flattening-reconstitution.md`)
- mapping packs (optional AOT mode: `aot-compilation.md`, wire format: `mpack-format-v1.md`)

The key idea is to centralize “what was derived/compiled” into a single shape that all producers/consumers use.

---

## 1. Layers and where they are used

1. **Effective schema inputs + fingerprint** (dialect-neutral)
   - Loaded from an explicit list of `ApiSchema.json` files.
   - Produces `EffectiveSchemaHash` and the deterministic `dms.ResourceKey` seed mapping.
   - Stories: E00 (`epics/00-effective-schema-hash/*`)

2. **Derived relational model set** (dialect-aware, SQL-free)
   - Fully derived, explicit inventory built from the effective schema for one dialect.
   - Contains physical identifiers and “DDL intent” inventory (indexes/triggers), but no SQL strings.
   - Used by:
     - DDL emission (to generate schemas/tables/constraints/views/etc)
     - plan compilation (to generate dialect-specific SQL plans)
   - Stories: E01 (`epics/01-relational-model/*`)

3. **Mapping set** (dialect-specific)
   - A derived relational model set **plus** compiled SQL plans for one dialect.
   - Selected at runtime per database by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion)`.
   - Produced by:
     - runtime compilation fallback, or
     - mapping pack decode (`MappingSet.FromPayload(...)`)
   - Stories: E15 (plan compilation), E05 (packs), E06 (runtime selection)

---

## 2. Unified object graph (C# shapes)

These are *conceptual* shapes intended to guide the implementation. Names should align with existing types where possible.

### 2.1 Keys and effective schema summary

```csharp
public enum SqlDialect
{
    Pgsql,
    Mssql
}

public readonly record struct MappingSetKey(
    string EffectiveSchemaHash,              // lowercase hex (64 chars)
    SqlDialect Dialect,
    string RelationalMappingVersion
);

public sealed record SchemaComponentInfo(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject
);

public sealed record ResourceKeyEntry(
    short ResourceKeyId,                     // must fit SQL smallint
    QualifiedResourceName Resource,
    string ResourceVersion,
    bool IsAbstractResource
);

public sealed record EffectiveSchemaInfo(
    string ApiSchemaFormatVersion,
    string RelationalMappingVersion,
    string EffectiveSchemaHash,
    int ResourceKeyCount,
    byte[] ResourceKeySeedHash,              // 32 bytes (SHA-256)
    IReadOnlyList<SchemaComponentInfo> SchemaComponentsInEndpointOrder,
    IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder
);
```

### 2.2 Derived relational model set (dialect-aware, SQL-free)

The derived model set is the single “authoritative” schema-derived inventory that both DDL emission and plan compilation consume.

```csharp
public enum ResourceStorageKind
{
    // Default: per-project schema tables (root + child + _ext).
    RelationalTables,

    // Descriptor resources are stored in shared `dms.Descriptor` (+ `dms.Document`), with no per-descriptor tables.
    SharedDescriptorTable
}

public sealed record ProjectSchemaInfo(
    string ProjectEndpointName,
    string ProjectName,
    string ProjectVersion,
    bool IsExtensionProject,
    DbSchemaName PhysicalSchema
);

public sealed record ConcreteResourceModel(
    ResourceKeyEntry ResourceKey,
    ResourceStorageKind StorageKind,
    RelationalResourceModel RelationalModel
);

public sealed record AbstractIdentityTableInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName Table,
    IReadOnlyList<DbColumnModel> ColumnsInIdentityOrder,
    IReadOnlyList<TableConstraint> Constraints
);

public sealed record AbstractUnionViewInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName ViewName,
    IReadOnlyList<DbColumnModel> ColumnsInIdentityOrder
    // Union-arm model omitted here; see `epics/01-relational-model/04-abstract-union-views.md`.
);

public enum DbIndexKind
{
    // Index implied by PK (included in manifest/inventory for determinism checks).
    PrimaryKey,

    // Index implied by a UNIQUE constraint (included in manifest/inventory for determinism checks).
    UniqueConstraint,

    // Non-unique index required by the FK index policy (see `ddl-generation.md`).
    ForeignKeySupport,

    // Explicit non-query indexes called out in the design (rare outside core `dms.*` tables).
    Explicit
}

public readonly record struct DbIndexName(string Value);

public sealed record DbIndexInfo(
    DbIndexName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    bool IsUnique,
    DbIndexKind Kind
);

public enum DbTriggerKind
{
    // Table trigger that stamps `dms.Document` representation/identity versions (see `update-tracking.md`).
    DocumentStamping,

    // Trigger that recomputes/maintains `dms.ReferentialIdentity` for a resource when identity projection changes.
    ReferentialIdentityMaintenance,

    // Trigger that maintains `{schema}.{AbstractResource}Identity` from concrete roots.
    AbstractIdentityMaintenance,

    // Trigger-based identity-component propagation fallback (SQL Server cascade-path restrictions).
    IdentityPropagationFallback
}

public readonly record struct DbTriggerName(string Value);

public sealed record DbTriggerInfo(
    DbTriggerName Name,
    DbTableName Table,
    DbTriggerKind Kind,
    IReadOnlyList<DbColumnName> KeyColumns
);

public sealed record DerivedRelationalModelSet(
    EffectiveSchemaInfo EffectiveSchema,
    SqlDialect Dialect,
    IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder,
    IReadOnlyList<ConcreteResourceModel> ConcreteResourcesInNameOrder,
    IReadOnlyList<AbstractIdentityTableInfo> AbstractIdentityTablesInNameOrder,
    IReadOnlyList<AbstractUnionViewInfo> AbstractUnionViewsInNameOrder,
    IReadOnlyList<DbIndexInfo> IndexesInCreateOrder,
    IReadOnlyList<DbTriggerInfo> TriggersInCreateOrder
);
```

Notes:
- `RelationalResourceModel` and its nested table/column types are defined in `flattening-reconstitution.md` and reused here.
- `TableConstraint` here refers to the model-level constraint inventory used by DDL emission. The mapping-pack/runtime subset may not need to serialize every constraint kind.
- Index/trigger inventories are dialect-aware (“SQL-free DDL intent”), derived deterministically from the derived tables/constraints plus the policies in `ddl-generation.md`.
  - Scope: schema-derived project objects only (resource/extension/abstract-identity tables). Core `dms.*` indexes/triggers are owned by core DDL emission.

### 2.3 Mapping set (dialect-specific)

The mapping set is what runtime code uses after selection. It is also the semantic target of mapping pack decode.

```csharp
public sealed record MappingSet(
    MappingSetKey Key,
    DerivedRelationalModelSet Model,
    IReadOnlyDictionary<QualifiedResourceName, ResourceWritePlan> WritePlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, ResourceReadPlan> ReadPlansByResource,
    IReadOnlyDictionary<QualifiedResourceName, short> ResourceKeyIdByResource,
    IReadOnlyDictionary<short, ResourceKeyEntry> ResourceKeyById
)
{
    // Required for AOT mode. Must validate payload invariants before returning.
    public static MappingSet FromPayload(MappingPackPayload payload) => throw new NotImplementedException();
}
```

Design invariants:
- All ordering-sensitive collections in `DerivedRelationalModelSet` are stored in canonical order (ordinal string ordering), and any lookup dictionaries are derived from those lists.
- A runtime implementation must not depend on dictionary iteration order for determinism.
- For `ConcreteResourcesInNameOrder`, “name order” means ordinal sort by `(project_name, resource_name)`.

---

## 3. Producer/consumer responsibilities

- **Model derivation** (E01) produces `DerivedRelationalModelSet` from the effective schema set.
- **DDL emission** (E02/E03) consumes `DerivedRelationalModelSet` and a dialect to emit deterministic SQL and manifests.
- **Plan compilation** (E15) consumes `DerivedRelationalModelSet` and a dialect to produce the `WritePlansByResource`/`ReadPlansByResource` dictionaries used by `MappingSet`.
- **Pack build** (E05) serializes the `MappingSet` *semantics* into `.mpack` (payload is a subset required for runtime execution).
- **Pack load** (E05-S05) and **runtime mapping selection** (E06-S02) must return the same `MappingSet` shape regardless of whether it came from packs or runtime compilation.

---

## 4. Runtime usage (write + read)

This section explains how runtime DMS uses the objects in this document (and the plan/model types from `flattening-reconstitution.md`) during request processing.

### 4.1 Mapping set selection (per database instance)

At runtime, schema-dependent work starts only after selecting a `MappingSet`:

1. Determine the target database instance (connection string) for the request.
2. Resolve the database’s `EffectiveSchemaHash` (from `dms.EffectiveSchema`, cached per connection string).
3. Construct `MappingSetKey(EffectiveSchemaHash, Dialect, RelationalMappingVersion)` and fetch the `MappingSet` from an in-process cache:
   - AOT mode: load + validate a matching `.mpack` payload, then `MappingSet.FromPayload(...)`.
   - Runtime compilation fallback: derive `DerivedRelationalModelSet`, compile plans, then construct `MappingSet`.

After this point, the request handler uses `MappingSet.WritePlansByResource` / `MappingSet.ReadPlansByResource` plus `MappingSet.Model` metadata to do the work without re-deriving schema information.

### 4.2 Write path usage (POST/PUT)

**Goal:** turn an API JSON document into a set of typed relational rows and execute parameterized SQL without per-resource codegen.

For a write request targeting resource `R`:

1. **Plan lookup**
   - Resolve `QualifiedResourceName` from routing (project + resource).
   - Lookup `ResourceWritePlan` via `MappingSet.WritePlansByResource[R]`.
   - Use `MappingSet.ResourceKeyIdByResource[R]` when writing to shared tables like `dms.Document` / `dms.ReferentialIdentity`.

2. **Document identity and `DocumentId`**
   - Core computes referential ids and extracts reference instances with concrete JSON locations.
   - Backend resolves insert vs update and allocates/loads the root `DocumentId` (details in `flattening-reconstitution.md`).

3. **Bulk reference + descriptor resolution**
   - Compute the full set of referential ids needed for this request:
     - document references (target resource key + extracted identity values), and
     - descriptor references (descriptor resource key + normalized URI).
   - Perform a single batched lookup against `dms.ReferentialIdentity` to resolve `ReferentialId → DocumentId` for *all* of them.
   - Split the resolved rows into the request-scoped maps needed by the flattener:
     - `ResolvedReferenceSet.DocumentIdByReferentialId` for document references, and
     - `ResolvedReferenceSet.DescriptorIdByKey` for descriptor references (keyed by `(normalizedUri, descriptorResource)`).
   - Materialize `ResolvedReferenceSet` for this request.

4. **Build the per-request reference index**
   - Build an `IDocumentReferenceInstanceIndex` for this request using:
     - `ResourceWritePlan.Model.DocumentReferenceBindings` (the “reference sites”: wildcard reference-object path + FK column + target resource), and
     - Core’s extracted `DocumentReferenceArrays` (reference instances with concrete JSON locations that include array indices), and
     - `ResolvedReferenceSet.DocumentIdByReferentialId` (to convert each instance’s referential id → referenced `DocumentId`).
   - The index answers: “for this `DocumentReferenceBinding` and this row’s `ordinalPath` (array indices along the wildcard reference path), what referenced `DocumentId` should be written to the FK column?”
     - `ordinalPath` examples: root reference `[]`; `$.students[*].studentReference` → `[studentOrdinal]`; `$.addresses[*].periods[*].calendarReference` → `[addressOrdinal, periodOrdinal]`.
   - This is what allows the flattener to populate FK columns for nested arrays in O(1) without per-row DB calls.

5. **Flatten to row buffers using `TableWritePlan.ColumnBindings`**
   - For each `DbTableModel` in `ResourceWritePlan.Model.TablesInWriteDependencyOrder`, enumerate JSON scope instances (`JsonScope`) and materialize `RowBuffer` objects.
   - Each `TableWritePlan` contains `ColumnBindings: IReadOnlyList<WriteColumnBinding>`.
   - Runtime produces `RowBuffer.Values[]` by iterating `ColumnBindings` *in order* and sourcing each value from the associated `WriteValueSource`:
     - `DocumentId`, `ParentKeyPart(i)`, `Ordinal`
     - `Scalar(relativeJsonPath, scalarType)`
     - `DocumentReference(binding)` resolved via the per-request `(binding, ordinalPath) → DocumentId` index
     - `DescriptorReference(...)` resolved via `ResolvedReferenceSet`

   **Critical invariant (how “compiled SQL parameter positions” work):**
   - `TableWritePlan.ColumnBindings` defines the authoritative *parameter/value ordering* for writes.
   - The compiled `InsertSql` for the table is emitted such that its parameter placeholders correspond to `ColumnBindings[0..N)` in that same order.
   - Runtime binds parameters by this ordering (not by “guessing” from SQL text), so it always knows which extracted value goes in which SQL parameter position.

6. **Execute (single transaction, replace semantics for collections)**
   - Root table:
     - `InsertSql` for insert and/or `UpdateSql` for update (depending on identity outcome).
   - Child/collection tables:
     - execute `DeleteByParentSql` (for the parent key) then bulk insert the new rows.
   - Bulk insert is used whenever a table has 0..N rows to write (especially child/collection and extension tables): a dialect-aware executor (e.g. `IBulkInserter`) batches `RowBuffer`s into multi-row inserts (or `COPY`/`SqlBulkCopy`-style paths for large batches), using `InsertSql` + ordered `ColumnBindings` and chunking to respect dialect parameter limits.

### 4.3 Read path usage (GET by id / query)

**Goal:** hydrate a page of documents (root + children + `_ext`) without N+1 queries and reconstitute JSON deterministically.

For a read request targeting resource `R`:

1. **Plan lookup**
   - Lookup `ResourceReadPlan` via `MappingSet.ReadPlansByResource[R]`.

2. **Resolve the page keyset (`DocumentId`s)**
   - GET by id: resolve `DocumentUuid → DocumentId` via `dms.Document`.
   - Query: compile and execute “page DocumentId SQL” (a separate root-table query compiler/executor step) to produce a page keyset of `DocumentId`s:
     - translate supported query parameters using the resource’s `ApiSchema.queryFieldMapping` to predicates over **root-table columns only** (no array traversal),
     - include reference-identity query fields by targeting the locally stored propagated identity columns on the root table (no joins),
     - emit parameterized SQL with deterministic paging (`ORDER BY r.DocumentId ASC` + dialect paging clause), and
     - return `DocumentId` only (optionally also compile/execute a `TotalCountSql` for `totalCount=true`).

3. **Hydrate relational rows (page-sized, batched)**
   - Materialize the page keyset in a dialect-appropriate structure (temp table / table variable) with a single `BIGINT` `DocumentId` column:
     - GET by id: insert the single resolved `DocumentId`.
     - Query: insert the output of the compiled “page DocumentId SQL” (which already applies filters + ordering + paging).
   - Execute one batched DB command that yields multiple result sets by concatenating:
     1) **keyset materialization SQL** (create/clear + insert `DocumentId`s),
     2) a `SELECT` of `dms.Document` joined to the keyset (document UUID + stamps like `_etag`/`_lastModifiedDate` + resource key id), and
     3) one `SELECT` per `DbTableModel` in `ResourceReadPlan.Model.TablesInReadDependencyOrder`, using each table’s compiled `TableReadPlan.SelectByKeysetSql` (each `SELECT` joins the table to the keyset to return all rows for the page).
   - Read result sets in order using `DbDataReader.NextResult()` / `QueryMultiple`, mapping each result set to the corresponding table model in the same order used to build the batch.
   - Each `SelectByKeysetSql` must emit rows ordered by the table key (parent key parts..., ordinal) so reconstitution can assemble arrays deterministically.
   - Each `SelectByKeysetSql` must emit its select-list in a stable order consistent with the table model (so readers can consume by ordinal without name-based mapping).

4. **Reconstitute JSON**
   - Use the `RelationalResourceModel` (table scopes + column metadata) to rebuild a document in two phases:
     1) **Assemble an in-memory row graph** for each `DocumentId` in the page keyset:
        - treat the root table as the anchor (one row per `DocumentId`),
        - for each child table, group rows by the *parent key parts* (the prefix of the composite key) and attach them to the parent row,
        - for array scopes, order siblings by the `Ordinal` key column so JSON arrays are stable and deterministic,
        - repeat depth-first using `TablesInReadDependencyOrder` so parents are always available before children.
     2) **Stream JSON output** from that row graph:
        - write scalar columns to their JSON locations using precompiled “column → JSON writer” instructions (e.g., parse canonical paths like `$.studentReference.studentUniqueId` into tokens `["studentReference","studentUniqueId"]`, and store scalar paths relative to the current table scope such as scope `$.addresses[*]` + relative path `$.streetNumberName`) so the per-row/per-column inner loop is simple property/array writes, not a general JSONPath evaluator doing parse/traverse/wildcard work for every value,
        - materialize inlined objects as needed when any child/scalar property exists beneath them,
        - write collections/nested collections from the attached child-row lists (in `Ordinal` order),
        - apply the array presence rule (omit empty arrays unless the schema marks the array property as required).
   - Overlay extension values from `_ext` tables by attaching extension rows using the same composite keys as the base scope they extend, then emitting `_ext` only where at least one extension value exists (per `extensions.md`).

5. **Reference identity projection (no referenced-table joins)**
   - Use `RelationalResourceModel.DocumentReferenceBindings` to drive reference-object projection. Each binding identifies:
     - the reference site (wildcard JSON reference-object path),
     - the referencing table/row that stores the reference,
     - the FK column (`..._DocumentId`) that indicates presence, and
     - the ordered identity-field bindings that map reference JSON paths → local propagated columns on the referencing row.
   - During reconstitution, if the FK column is null, omit the reference object; otherwise emit the reference object by reading the bound propagated identity columns from the *same row* (no joins).
   - This avoids joining to referenced resource tables (including abstract/unions) during reads; polymorphic/abstract references work the same way because the referencing row stores the abstract identity fields (enforced by a composite FK to the abstract identity table).

6. **Descriptor URI projection (batched)**
   - Use `RelationalResourceModel.DescriptorEdgeSources` to identify descriptor FK columns (`..._DescriptorId`) that require URI projection.
   - Include descriptor projection as an additional result set in the same multi-result hydration command (still page-sized and batched, no N+1), returning `(DescriptorId, Uri)` for all descriptor ids referenced by the page.
   - Avoid left-joining `dms.Descriptor` into every per-table hydration SELECT: it requires many joins (one per descriptor FK) and bloats the compiled per-table SQL.
