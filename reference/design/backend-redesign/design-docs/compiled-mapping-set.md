# Backend Redesign: Unified Mapping Models (In-Memory)

Status: Draft.

This document defines the **single shared in-memory object graph** used to represent schema-derived mapping artifacts in the backend redesign.

It exists to prevent drift between:
- DDL generation (`ddl-generation.md`)
- runtime compilation and execution (`flattening-reconstitution.md`)
- mapping packs (optional AOT mode: `aot-compilation.md`, wire format: `mpack-format-v1.md`)
- authorization companion objects, required indexes, and schema-derived authorization metadata used to build SQL authorization checks (`auth.md`)

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
    short ResourceKeyCount,
    byte[] ResourceKeySeedHash,              // 32 bytes (SHA-256)
    IReadOnlyList<SchemaComponentInfo> SchemaComponentsInEndpointOrder,
    IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder
);
```

`ResourceKeyCount` intentionally uses `short` here to match the authoritative `dms.EffectiveSchema.ResourceKeyCount smallint` contract. The design already caps `dms.ResourceKey` cardinality because every `ResourceKeyId` must fit `smallint`; using the same bound in the shared runtime model makes the 32,767-entry ceiling explicit and avoids late narrowing/cast failures in provisioning or validation code.

#### Dialect rules vs. SQL emission (shared, no drift)

This redesign treats `SqlDialect` as a **selection key** and an input to deterministic derivation; it is not
only a DDL-emission concern.

To prevent drift between model derivation (E01) and SQL generation/plan emission (E02/E15), implement
engine-specific “dialect rules” as a reusable component (e.g., `SqlDialectRules` / `ISqlDialectRules`) that
is consumed by both:

- **E01 (relational model derivation)**: identifier length limits and deterministic shortening (truncate + hash),
  and any dialect-conditional default type decisions that affect the SQL-free model inventories (e.g., decisions
  that must be stable to support collision detection and manifest output).
- **E02 (DDL emission) / E15 (plan compilation)**: identifier quoting, DDL capability patterns, SQL formatting, and
  mapping from the model’s dialect-neutral type categories (e.g., `RelationalScalarType`) to concrete SQL types.

The SQL writer/dialect abstraction in E02 should compose over the shared dialect rules rather than duplicating
identifier-limit/shortening/type-default logic.

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
    DbTableModel TableModel
);

public sealed record AbstractUnionViewInfo(
    ResourceKeyEntry AbstractResourceKey,
    DbTableName ViewName,
    IReadOnlyList<AbstractUnionViewOutputColumn> OutputColumnsInSelectOrder,
    IReadOnlyList<AbstractUnionViewArm> UnionArmsInOrder
);

public sealed record AbstractUnionViewOutputColumn(
    DbColumnName ColumnName,
    RelationalScalarType ScalarType,
    JsonPathExpression? SourceJsonPath,
    QualifiedResourceName? TargetResource
);

public sealed record AbstractUnionViewArm(
    ResourceKeyEntry ConcreteMemberResourceKey,
    DbTableName FromTable,
    IReadOnlyList<AbstractUnionViewProjectionExpression> ProjectionExpressionsInSelectOrder
);

public abstract record AbstractUnionViewProjectionExpression
{
    public sealed record SourceColumn(DbColumnName ColumnName) : AbstractUnionViewProjectionExpression;
    public sealed record StringLiteral(string Value) : AbstractUnionViewProjectionExpression;
}

public enum TrackedChangeTableKind
{
    // Per-resource tracked-change table in `tracked_changes_{ProjectEndpointName}`.
    Resource,

    // Shared descriptor tracked-change table, conventionally `tracked_changes_edfi.Descriptor`.
    SharedDescriptor
}

public enum TrackedChangeColumnRole
{
    // Value comes from an identityJsonPaths binding or a securableElements scalar binding.
    SourceValue,

    // Value is projected from `dms.Descriptor.Namespace` for a descriptor reference binding.
    DescriptorNamespace,

    // Value is projected from `dms.Descriptor.CodeValue` for a descriptor reference binding.
    DescriptorCodeValue,

    // Value is projected as a Student/Contact/Staff resource `DocumentId` for ReadChanges authorization.
    PersonDocumentId
}

public enum PersonSecurableElementKind
{
    Student,
    Contact,
    Staff
}

public sealed record TrackedChangeDescriptorJoinInfo(
    string JoinName,
    QualifiedResourceName DescriptorResource,
    DbColumnName SourceDescriptorIdColumn,
    DbColumnName NamespaceOutputColumn,
    DbColumnName CodeValueOutputColumn
);

public sealed record TrackedChangeJoinStep(
    DbTableName FromTable,
    DbColumnName FromDocumentIdColumn,
    DbTableName ToTable,
    DbColumnName ToDocumentIdColumn
);

public sealed record TrackedChangePersonJoinInfo(
    string JoinName,
    PersonSecurableElementKind PersonKind,
    JsonPathExpression SourceJsonPath,
    IReadOnlyList<TrackedChangeJoinStep> JoinStepsInOrder,
    DbColumnName PersonDocumentIdOutputColumn
);

public sealed record TrackedChangeColumnInfo(
    DbColumnName BaseColumnName,
    DbColumnName OldColumnName,
    DbColumnName NewColumnName,
    RelationalScalarType ScalarType,
    bool IsOldColumnNullable,
    bool IsNewColumnNullable,
    TrackedChangeColumnRole Role,
    JsonPathExpression? SourceJsonPath,
    DbColumnName? SourceStorageColumn = null,
    string? DescriptorJoinName = null,
    string? PersonJoinName = null
);

public sealed record TrackedChangeTableInfo(
    DbTableName TableName,
    TrackedChangeTableKind Kind,
    ResourceKeyEntry? ResourceKey,
    DbTableName SourceTable,
    // System columns use fixed role-based SQL type mappings documented below; they are not value columns.
    DbColumnName IdColumn,
    DbColumnName ChangeVersionColumn,
    DbColumnName CreatedAtColumn,
    DbColumnName? DiscriminatorColumn,
    IReadOnlyList<TrackedChangeColumnInfo> ValueColumnsInTableOrder,
    IReadOnlyList<TrackedChangeDescriptorJoinInfo> DescriptorJoinsInNameOrder,
    IReadOnlyList<TrackedChangePersonJoinInfo> PersonJoinsInNameOrder
);

// The four ReadChanges authorization views (see `change-queries.md` §"Authorization views")
// are a static structural inventory, not a per-schema derived inventory: like the
// people auth views they extend, their shape never varies with the effective schema. They are
// owned by `AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions` in `Backend.External`,
// where `AuthViewDefinition` (view name, set operator, union arms) is the shared structural shape
// rendered by both the DDL emitter and the manifest emitter. The fourth kind is named
// `StudentDeletedResponsibility` (view `...StudentDocumentIdDeletedResponsibility`, exactly 63
// characters) because the ODS-era `ThroughDeletedResponsibility` name exceeds PostgreSQL's
// 63-character identifier limit; see `change-queries.md` for the rename rationale.
public enum ReadChangesAuthViewKind
{
    Student,
    Contact,
    Staff,
    StudentDeletedResponsibility,
}

public sealed record ReadChangesAuthorizationViewInfo(
    ReadChangesAuthViewKind Kind,
    AuthViewDefinition ViewDefinition,
    DbColumnName PersonDocumentIdOutputColumn,
    DbColumnName ClaimEducationOrganizationIdColumn
);

public enum DbIndexKind
{
    // Index implied by PK (included in manifest/inventory for determinism checks).
    PrimaryKey,

    // Index implied by a UNIQUE constraint (included in manifest/inventory for determinism checks).
    UniqueConstraint,

    // Non-unique index required by the FK index policy (see `ddl-generation.md`).
    ForeignKeySupport,

    // Index required for authorization query performance (see `auth.md`).
    Authorization,

    // Explicit non-query indexes called out in the design (rare outside core `dms.*` tables).
    Explicit
}

public readonly record struct DbIndexName(string Value);

public sealed record DbIndexInfo(
    DbIndexName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    bool IsUnique,
    DbIndexKind Kind,
    // Optional non-key columns to include in the index leaf pages (SQL `INCLUDE` clause).
    // Null and empty are treated identically by emitters: no `INCLUDE` clause.
    // Non-null for the five `PrimaryAssociation` authorization indexes (see `auth.md`) AND for
    // person-join authorization indexes (DMS-1094), which always INCLUDE the source table's
    // `DocumentId` so the runtime auth-filter join can be served by an index-only scan.
    // An emitted authorization index's `IncludeColumns` may be *widened* when a later step in
    // `DeriveAuthorizationIndexInventoryPass` collides on the same `(table, leading key column)` —
    // e.g. a person-join hop landing on `StudentContactAssociation.Student_DocumentId` (PA-covered,
    // INCLUDE `[Contact_DocumentId]`) widens that PA index to INCLUDE `[Contact_DocumentId, DocumentId]`.
    // EdOrg/Namespace auth indexes (originally `IncludeColumns: null`) widen to `[DocumentId]` under
    // the same collision rule. The merged list is sorted ordinal-ascending by `DbColumnName.Value`
    // and deduped, so widening is deterministic and independent of pass execution order.
    // Manifest serialization: omitted when null/empty, otherwise emitted as `"include_columns": [...]`.
    IReadOnlyList<DbColumnName>? IncludeColumns = null
);

public readonly record struct DbTriggerName(string Value);

public abstract record TriggerKindParameters
{
    public sealed record DocumentStamping(TrackedChangeAttachment? ChangeTracking = null) : TriggerKindParameters;

    public sealed record ReferentialIdentityMaintenance(
        short ResourceKeyId,
        string ProjectName,
        string ResourceName,
        IReadOnlyList<IdentityElementMapping> IdentityElements,
        SuperclassAliasInfo? SuperclassAlias = null
    ) : TriggerKindParameters;

    public sealed record AbstractIdentityMaintenance(
        DbTableName TargetTable,
        IReadOnlyList<TriggerColumnMapping> TargetColumnMappings,
        string DiscriminatorValue
    ) : TriggerKindParameters;

    public sealed record AuthHierarchyMaintenance(
        AuthEdOrgEntity Entity,
        AuthHierarchyTriggerEvent TriggerEvent
    ) : TriggerKindParameters;
}

public sealed record TriggerColumnMapping(DbColumnName SourceColumn, DbColumnName TargetColumn);

public sealed record DbTriggerInfo(
    DbTriggerName Name,
    DbTableName Table,
    IReadOnlyList<DbColumnName> KeyColumns,
    IReadOnlyList<DbColumnName> IdentityProjectionColumns,
    TriggerKindParameters Parameters,
    // The concrete root table (or `dms.Descriptor`) whose mirrored `ContentVersion` /
    // `ContentLastModifiedAt` columns this trigger updates after stamping `dms.Document`.
    // Required (non-null) for every `Parameters is DocumentStamping` entry; null for other kinds.
    // See `change-queries.md` §"Concrete-resource ContentVersion / ContentLastModifiedAt mirror".
    DbTableName? MirrorStampTargetTable = null
);

public sealed record DerivedRelationalModelSet(
    EffectiveSchemaInfo EffectiveSchema,
    SqlDialect Dialect,
    IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder,
    IReadOnlyList<ConcreteResourceModel> ConcreteResourcesInNameOrder,
    IReadOnlyList<AbstractIdentityTableInfo> AbstractIdentityTablesInNameOrder,
    IReadOnlyList<AbstractUnionViewInfo> AbstractUnionViewsInNameOrder,
    IReadOnlyList<TrackedChangeTableInfo> TrackedChangeTablesInNameOrder,
    IReadOnlyList<DbIndexInfo> IndexesInCreateOrder,
    IReadOnlyList<DbTriggerInfo> TriggersInCreateOrder
);
```

Notes:
- `RelationalResourceModel` and its nested table/column types are defined in `flattening-reconstitution.md` and reused here.
- `TableConstraint` here refers to the model-level constraint inventory used by DDL emission. The mapping-pack/runtime subset may not need to serialize every constraint kind.
- Index/trigger/tracked-change inventories are dialect-aware (“SQL-free DDL intent”), derived deterministically from the derived tables/constraints plus the policies in `ddl-generation.md` and `change-queries.md`.
  - `IdentityProjectionColumns` is a null-safe value-diff compare set, not an `UPDATE(column)` gate list.
  - Emitters must not use SQL Server `UPDATE(column)`, PostgreSQL `UPDATE OF`, or equivalent target-list checks to decide whether a Change Queries key-change row should be emitted.
  - `DocumentStamping.ChangeTracking` is valid only on `TriggerKindParameters.DocumentStamping` entries and is attached when Change Queries requires that trigger to also write key-change and tombstone rows. The tracked-change table metadata tells emitters where and how to write tracked-change rows; the owning `DbTriggerInfo.IdentityProjectionColumns` remains the single key-change predicate source.
  - `MirrorStampTargetTable` is required (non-null) for every `TriggerKindParameters.DocumentStamping` entry and null for all other trigger kinds. The derivation pass assigns it by rule: the same table as `Table` for root-table stamping triggers, the resource's root table for child / `_ext` stamping triggers, and `dms.Descriptor` for the descriptor stamping trigger. Dialect emitters render the mirror UPDATE (the second UPDATE in the trigger body, after the `dms.Document` stamp UPDATE) against `MirrorStampTargetTable` and MUST NOT re-derive the target from `Table`. See `change-queries.md` §"Concrete-resource ContentVersion / ContentLastModifiedAt mirror".
  - For key-change rows, dialect emitters use the same null-safe old/new value-diff workset already required for identity stamping. Under key unification, this includes the presence-gated canonical expressions defined in `key-unification.md`.
  - Scope: schema-derived project objects only (resource/extension/abstract-identity tables). This includes authorization-required indexes on resource tables derived from `securableElements` (see `auth.md`) and tracked-change tables/views derived from Change Query metadata (see `change-queries.md`). Core `dms.*` / `auth.*` objects (and their indexes/triggers) are owned by core DDL emission, with two carve-outs:
    - authorization indexes for descriptor `Namespace` securable elements land on `dms.Descriptor` (e.g. `IX_Descriptor_Namespace_Auth`) because all descriptor resources share that base table; these are emitted by `DeriveAuthorizationIndexInventoryPass` alongside resource-table auth indexes rather than by core DDL, since their existence is driven by per-resource `securableElements` metadata.
    - the shared descriptor tracked-change table (`tracked_changes_edfi.Descriptor`) and its `TriggerKindParameters.DocumentStamping`/`DocumentStamping.ChangeTracking` trigger are represented in tracked-change inventory because their columns and discriminator coverage are driven by descriptor resources in the effective schema.
- `IndexesInCreateOrder` / `TriggersInCreateOrder` are stored in canonical deterministic order (schema, table, name), not a dependency-aware DDL execution order; DDL emission chooses any required creation sequence.
- `TrackedChangeTablesInNameOrder` is stored in canonical deterministic order by physical object name. Dialect emitters, runtime Change Query SQL planning, manifests, and tests must consume this inventory rather than re-deriving table columns, descriptor joins, or person joins from emitted SQL strings.
- The ReadChanges authorization view inventory is not part of `DerivedRelationalModelSet`: it is the static `AuthObjectDefinitions.ReadChangesAuthorizationViewDefinitions` list in `Backend.External` (see the `ReadChangesAuthViewKind` note above). Emission of these views is gated per model set by people-auth availability plus the presence of the five required `tracked_changes_edfi` association tables in `TrackedChangeTablesInNameOrder`; the DDL emitter and the manifest emitter apply the same guard so the manifest never advertises views the DDL does not create.
- `TrackedChangeTableInfo` separates system columns from `ValueColumnsInTableOrder`:
  - When `Kind = Resource`, `ResourceKey` is required and identifies the single resource represented by the tracked-change table.
  - When `Kind = SharedDescriptor`, `ResourceKey` is `null`; the table covers every `ConcreteResourceModel` whose `StorageKind = SharedDescriptorTable` in the same `DerivedRelationalModelSet`. Consumers must not duplicate descriptor coverage lists inside `TrackedChangeTableInfo`.
  - `ValueColumnsInTableOrder` entries carry `RelationalScalarType` because they are schema-derived old/new values.
  - `IsOldColumnNullable` and `IsNewColumnNullable` describe the physical old/new tracked-change columns separately. They are required booleans, never tri-state values.
  - `IsOldColumnNullable` follows the nullability of the tracked source value. Required identity and required securable-element values are `false`; optional securable-element values, such as override-driven nullable paths, are `true`.
  - `IsNewColumnNullable` is normally `true` because delete tombstones leave `NewX` values `NULL`. If a future tracked-change table records only key-change rows and never tombstones, it may set `IsNewColumnNullable` from the source value nullability instead.
  - `DescriptorJoinName` and `PersonJoinName` reference entries in `DescriptorJoinsInNameOrder` and `PersonJoinsInNameOrder`; join definitions are owned once at the table level and are not duplicated per value column.
  - `TrackedChangeColumnRole.DescriptorNamespace` and `TrackedChangeColumnRole.DescriptorCodeValue` require `DescriptorJoinName` and require `PersonJoinName = null`.
  - `TrackedChangeColumnRole.PersonDocumentId` requires `PersonJoinName` and requires `DescriptorJoinName = null`.
  - `TrackedChangeColumnRole.SourceValue` requires both join-name fields to be `null`.
  - `IdColumn`, `ChangeVersionColumn`, `CreatedAtColumn`, and `DiscriminatorColumn` are fixed tracked-change system columns whose SQL types are inferred from their roles, following the same convention used by non-scalar `DbColumnModel` roles (`DocumentFk`, `CollectionKey`, `Ordinal`, etc.).
  - `IdColumn`: `NOT NULL`, copied from `dms.Document.DocumentUuid`; PostgreSQL `uuid`, SQL Server `uniqueidentifier`.
  - `ChangeVersionColumn`: `NOT NULL`, copied from the bumped `dms.Document.ContentVersion`; PostgreSQL/SQL Server `bigint`; primary tracked-change window/sort column.
  - `CreatedAtColumn`: `NOT NULL`, tracked row insert timestamp; PostgreSQL `timestamp with time zone DEFAULT now()`, SQL Server `datetime2(7) DEFAULT sysutcdatetime()`.
  - `DiscriminatorColumn`: present only when `Kind = SharedDescriptor`; `NOT NULL`; PostgreSQL `varchar(128)`, SQL Server `nvarchar(128)`; omitted (`null`) for per-resource tracked-change tables.

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
   - Note: under key unification, this same `ResolvedReferenceSet.DescriptorIdByKey` map is also consumed by `KeyUnificationWritePlan`
     when coalescing unified descriptor endpoints into canonical storage columns (see `key-unification.md`).

4. **Build the per-request reference index**
   - Build an `IDocumentReferenceInstanceIndex` for this request using:
     - `ResourceWritePlan.Model.DocumentReferenceBindings` (the “reference sites”: wildcard reference-object path + FK column + target resource), and
     - Core’s extracted `DocumentReferenceArrays` (reference instances with concrete JSON locations that include array indices), and
     - `ResolvedReferenceSet.DocumentIdByReferentialId` (to convert each instance’s referential id → referenced `DocumentId`).
   - The index answers: “for this `DocumentReferenceBinding` and this row’s `ordinalPath` (array indices along the wildcard reference path), what referenced `DocumentId` should be written to the FK column?”
     - `ordinalPath` examples: root reference `[]`; `$.students[*].studentReference` → `[studentOrdinal]`; `$.addresses[*].periods[*].calendarReference` → `[addressOrdinal, periodOrdinal]`.
   - This is what allows the flattener to populate FK columns for nested arrays in O(1) without per-row DB calls.

5. **Flatten to row buffers using `TableWritePlan.ColumnBindings`**
   - For each `TableWritePlan` in `ResourceWritePlan.TablePlansInDependencyOrder`, enumerate JSON scope instances (`JsonScope`) for `TableWritePlan.TableModel` and materialize `RowBuffer` objects.
   - Each `TableWritePlan` contains:
     - `BulkInsertBatching: BulkInsertBatchingInfo` (`MaxRowsPerBatch`, `ParametersPerRow`, `MaxParametersPerCommand`),
     - `ColumnBindings: IReadOnlyList<WriteColumnBinding>` (stored/writable columns only, in parameter order; each binding includes its authoritative `ParameterName`),
     - `KeyUnificationPlans: IReadOnlyList<KeyUnificationWritePlan>` (empty when no key unification applies),
     - `CollectionMergePlan: CollectionMergePlan?` for persisted collection tables only; it points back into `ColumnBindings` via ordered semantic-identity bindings plus `StableRowIdentityBindingIndex`, `OrdinalBindingIndex`, and `CompareBindingIndexesInOrder`, and carries the stable-row `UPDATE` / `DELETE` SQL used during merge execution, and
     - `CollectionKeyPreallocationPlan: CollectionKeyPreallocationPlan?` when the table must reserve new `CollectionItemId` values and bind them into a specific `ColumnBindings` slot before insert DML.
   - Runtime produces `RowBuffer.Values[]` by iterating `ColumnBindings` *in order* and sourcing each value from the associated `WriteValueSource`:
     - `DocumentId`, `ParentKeyPart(i)`, `Ordinal`
     - `Scalar(relativeJsonPath, scalarType)`
     - `DocumentReference(binding)` resolved via the per-request `(binding, ordinalPath) → DocumentId` index
     - `DescriptorReference(...)` resolved via `ResolvedReferenceSet`
     - `WriteValueSource.Precomputed` populated by executing `KeyUnificationWritePlan` (canonical storage columns + any synthetic `..._Present` presence flags) or by `CollectionKeyPreallocationPlan` for reserved collection-row identities

   Key unification notes:
   - `TableWritePlan.ColumnBindings` excludes any column whose `DbColumnModel.Storage` is `UnifiedAlias(...)` (API-bound binding/path aliases are generated/read-only and are never written).
   - Canonical storage columns and synthetic `..._Present` columns are included in `ColumnBindings` as `WriteValueSource.Precomputed` and are populated per-row by `KeyUnificationWritePlan` after normal extraction.

   **Critical invariant (how “compiled SQL parameter positions” work):**
   - `TableWritePlan.ColumnBindings` defines the authoritative *parameter/value ordering* for writes.
   - The compiled `InsertSql` for the table is emitted such that its parameter placeholders correspond to `ColumnBindings[0..N)` in that same order and use `WriteColumnBinding.ParameterName`.
   - Runtime binds parameters from `WriteColumnBinding.ParameterName` (not by “guessing” from SQL text), so it always knows which extracted value goes in which SQL parameter position.

6. **Whole-document no-op detection for existing documents**
   - Applies to `PUT` and to `POST` requests that resolved to an existing `DocumentId`.
   - Use the current-document rows already materialized earlier in the request (for auth/reconstitution) and project
     them into comparable rowsets using the same table ordering and stored/writable column ordering as
     `TableWritePlan.ColumnBindings`, after applying the same profile-aware merge rules the normal executor would use.
   - Guarded no-op comparison MUST reuse the same merge-ordering and post-merge rowset-synthesis logic as execution, either directly or through a shared helper built from the same `TableWritePlan` / `CollectionMergePlan` metadata; do not introduce a compare-only profile merge path that can drift from execution behavior.
   - Compare table-by-table, including:
     - non-collection scope state using `ProfileAppliedWriteContext.StoredScopeStates` (`VisiblePresent`, `VisibleAbsent`, `Hidden`) when profile filtering applies,
     - collection sibling ordering and membership after merge using `ProfileAppliedWriteContext.VisibleStoredCollectionRows`, `CollectionMergePlan.CompareBindingIndexesInOrder`, and the same deterministic post-merge sibling-order rule as the normal executor, and
     - ordered stored/writable values (resolved FK ids, canonical storage columns, synthetic presence flags, etc.).
   - If all comparable rowsets are equal, mark the request as a **no-op candidate** and proceed to guarded execution.

7. **Execute (single transaction, merge semantics for scoped child data)**
   - If the request is a no-op candidate, the write batch must first verify that the observed `ContentVersion` is still
     current for that `DocumentId`. If it is still current, commit without issuing DML for the resource tables or
     `dms.Document`.
   - If the observed `ContentVersion` is no longer current, abandon the no-op fast path and follow the retry /
     precondition rules in `transactions-and-concurrency.md`.
   - Root table:
     - `InsertSql` for insert and/or `UpdateSql` for update (depending on identity outcome), and profile-constrained creates consult `ProfileAppliedWriteRequest.RootResourceCreatable` before root insert DML.
   - Non-root 1:1 tables (including root-scope `_ext` tables):
     - `InsertSql` when `RequestScopeStates` / `StoredScopeStates` show the scope is newly `VisiblePresent` and that create-of-new-visible-data is creatable,
     - `UpdateSql` when the scoped row already exists and remains `VisiblePresent`, preserving hidden members via compiled-binding overlay plus `HiddenMemberPaths`, and
     - `DeleteByParentSql` only when a separate-table scope is `VisibleAbsent`; inlined `VisibleAbsent` scopes clear only their visible compiled bindings, while `Hidden` scopes are preserved.
   - Collection tables:
     - load the current sibling sets for the document,
     - determine the visible stored rows for each scope instance from `ProfileAppliedWriteContext.VisibleStoredCollectionRows` (or treat all rows as visible when no profile filtering applies),
     - match incoming rows by the compiled semantic identity,
     - assume at most one incoming row per `(scope instance, compiled semantic identity)`; duplicate request candidates are upstream data-validation failures and must not be left to database unique-constraint handling,
     - reserve new `CollectionItemId` values in batch using `CollectionKeyPreallocationPlan` when unmatched inserts are needed,
     - update matched rows in place via `CollectionMergePlan.UpdateByStableRowIdentitySql`, preserving bindings governed by `HiddenMemberPaths`,
     - delete omitted visible rows via `CollectionMergePlan.DeleteByStableRowIdentitySql`, and
     - bulk insert only the newly created rows when the corresponding `ProfileAppliedWriteRequest.VisibleRequestCollectionItems` entry is creatable, then recompute `Ordinal` using the deterministic post-merge sibling-order rule described in `flattening-reconstitution.md`.
   - Bulk insert is used whenever a table has 0..N rows to write (especially child/collection and extension tables): a dialect-aware executor (e.g. `IBulkInserter`) batches `RowBuffer`s into multi-row inserts (or `COPY`/`SqlBulkCopy`-style paths for large batches), using `InsertSql` + ordered `ColumnBindings` and chunking by `TableWritePlan.BulkInsertBatching.MaxRowsPerBatch` to respect dialect parameter limits.

### 4.3 Read path usage (GET by id / query)

**Goal:** hydrate a page of documents (root + children + `_ext`) without N+1 queries and reconstitute JSON deterministically.

For a read request targeting resource `R`:

1. **Plan lookup**
   - Lookup `ResourceReadPlan` via `MappingSet.ReadPlansByResource[R]`.

2. **Resolve the page keyset (`DocumentId`s)**
   - GET by id: resolve `DocumentUuid → DocumentId` via `dms.Document`.
   - Query: compile and execute “page DocumentId SQL” (a separate root-table query compiler/executor step) to produce a page keyset of `DocumentId`s:
     - translate supported query parameters using the resource’s `ApiSchema.queryFieldMapping` to predicates over **root-table columns only** (no array traversal),
     - include reference identity query fields by targeting the root table’s API-bound binding/path columns (including `UnifiedAlias` columns) rather than canonical storage-only columns, preserving optional-path presence semantics without joins,
     - emit parameterized SQL with deterministic paging (`ORDER BY r.DocumentId ASC` + dialect paging clause), and
     - return `DocumentId` only (optionally also compile/execute a `TotalCountSql` for `totalCount=true`).

3. **Hydrate relational rows (page-sized, batched)**
   - Materialize the page keyset in a dialect-appropriate structure (temp table / table variable) with a single `BIGINT` `DocumentId` column:
     - GET by id: insert the single resolved `DocumentId`.
     - Query: insert the output of the compiled “page DocumentId SQL” (which already applies filters + ordering + paging).
   - Execute one batched DB command that yields multiple result sets by concatenating:
     1) **keyset materialization SQL** (create/clear + insert `DocumentId`s),
     2) a `SELECT` of `dms.Document` joined to the keyset (document UUID + `ContentVersion` / `ContentLastModifiedAt` stamps used to compose `_etag` / `_lastModifiedDate` + resource key id), and
     3) one `SELECT` per `DbTableModel` in `ResourceReadPlan.Model.TablesInDependencyOrder`, using each table’s compiled `TableReadPlan.SelectByKeysetSql` (each `SELECT` joins the table to the keyset to return all rows for the page).
   - Read result sets in order using `DbDataReader.NextResult()` / `QueryMultiple`, mapping each result set to the corresponding table model in the same order used to build the batch.
   - Each `SelectByKeysetSql` must emit rows ordered by root document scope, then immediate parent scope where applicable, then `Ordinal`, so reconstitution can assemble arrays deterministically.
   - Each `SelectByKeysetSql` must emit its select-list in a stable order consistent with the table model (so readers can consume by ordinal without name-based mapping).

4. **Reconstitute JSON**
   - Key unification note: API JsonPath binding continues to target the per-path binding columns (`DbColumnModel.SourceJsonPath`), including `UnifiedAlias` columns; canonical storage columns are storage-only (`SourceJsonPath = null`) and are not emitted to JSON directly.
   - Use the `RelationalResourceModel` (table scopes + column metadata) to rebuild a document in two phases:
     1) **Assemble an in-memory row graph** for each `DocumentId` in the page keyset:
        - treat the root table as the anchor (one row per `DocumentId`),
        - for each child table, group rows by the immediate parent scope locator (`..._DocumentId` for top-level collections, `ParentCollectionItemId` for nested collections) and attach them to the parent row,
        - for array scopes, order siblings by the `Ordinal` key column so JSON arrays are stable and deterministic,
        - repeat depth-first using `TablesInDependencyOrder` so parents are always available before children.
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
     - the ordered identity-field bindings that map reference JSON paths → local binding/path columns on the referencing row (these columns may be `UnifiedAlias` columns generated from canonical storage columns).
   - During reconstitution, if the FK column is null, omit the reference object; otherwise emit the reference object by reading the bound binding/path columns from the *same row* (no joins).
   - This avoids joining to referenced resource tables (including abstract/unions) during reads; under key unification, composite reference FKs are derived via storage-column mapping (`DbColumnModel.Storage`) even though read-time projection continues to bind to per-path columns.

6. **Descriptor URI projection (batched)**
   - Use `RelationalResourceModel.DescriptorEdgeSources` to identify descriptor FK columns (`..._DescriptorId`) that require URI projection.
   - Include descriptor projection as an additional result set in the same multi-result hydration command (still page-sized and batched, no N+1), returning `(DescriptorId, Uri)` for all descriptor ids referenced by the page.
   - Avoid left-joining `dms.Descriptor` into every per-table hydration SELECT: it requires many joins (one per descriptor FK) and bloats the compiled per-table SQL.
   - Key unification note: descriptor FK emission uses storage-column mapping and may de-duplicate multiple descriptor binding columns into one FK per `(table, storage_column)`, reporting deterministic `descriptor_fk_deduplications[]` diagnostics in manifests (see `key-unification.md`).

### 4.4 Authorization usage (read + write)

Authorization is applied using token-derived authorization context and ODS-style strategy semantics, but adapted for `DocumentId`-centric relational storage; see [auth.md](auth.md).

Mapping-set integration points:
- **Physical column resolution**: authorization checks need to reference the correct physical columns for `Namespace`, EdOrg ids, and person/document relationships. These column names come from the same derived relational model used for reads/writes.
- **Join-path precomputation**: some securable elements (e.g., `Student` for `CourseTranscript`) may only be reachable transitively via references. A schema-derived “securable element column path” resolver can be built once per `(EffectiveSchemaHash, resource, securableElement)` and cached, using the `RelationalResourceModel` tables/columns plus ApiSchema `securableElements`.
- **Authorization-required index inventory**: the derived index inventory includes `DbIndexKind.Authorization` entries for:
  - per-resource `Namespace` indexes (for namespace-based checks),
  - per-resource EdOrg-id indexes (for relationship-based checks), and
  - join-column indexes used to reach person `DocumentId`s (for people-related relationship checks).

Example (sketch): resolving the `Student` securable element for `CourseTranscript`
- ApiSchema marks `$.studentAcademicRecordReference.studentUniqueId` as a `Student` securable element.
- The derived relational model maps this to a join chain:
  - `edfi.CourseTranscript.StudentAcademicRecord_DocumentId` → `edfi.StudentAcademicRecord.DocumentId`
  - `edfi.StudentAcademicRecord.Student_DocumentId` → `edfi.Student.DocumentId`
- Relationship-based authorization can then be expressed against the terminal `Student.DocumentId` using `auth.EducationOrganizationIdToStudentDocumentId` (which outputs `StudentDocumentId`), without joining on natural keys.
