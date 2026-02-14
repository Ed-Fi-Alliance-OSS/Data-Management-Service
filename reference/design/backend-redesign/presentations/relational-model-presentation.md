# Derived Relational Model: Technical Presentation

---

## Agenda

1. The big picture: what we build and how
2. `DerivedRelationalModelSet` — the output object
3. Zooming in: resource models, tables, columns, constraints
4. `DerivedRelationalModelSetBuilder` — orchestration
5. Per-resource pipeline: steps
6. Set-level passes: cross-resource derivation
7. Zooming in: pass groups by concern

---

## 1. The Big Picture

The relational model is **derived** from `ApiSchema.json` at build time — no code generation, no handwritten SQL. The derivation produces a single immutable object, `DerivedRelationalModelSet`, consumed by DDL emission, plan compilation, and manifest output.

```mermaid
flowchart LR
    subgraph Input
        ESS["EffectiveSchemaSet<br/>(core + extension ApiSchema.json files)"]
        DIALECT["SqlDialect<br/>(Pgsql / Mssql)"]
        RULES["ISqlDialectRules"]
    end

    subgraph Builder["DerivedRelationalModelSetBuilder"]
        CTX["RelationalModelSetBuilderContext<br/>(shared mutable state)"]
        PASSES["Ordered Set-Level Passes<br/>(IRelationalModelSetPass[])"]
        CTX --- PASSES
    end

    subgraph Output
        DRMS["DerivedRelationalModelSet<br/>(immutable)"]
    end

    Input --> Builder --> Output

    DRMS --> DDL["DDL Emission"]
    DRMS --> PLANS["Plan Compilation"]
    DRMS --> MANIFEST["Manifest Output"]
```

---

## 2. `DerivedRelationalModelSet` — The Output

The top-level immutable record produced by the builder.

```mermaid
classDiagram
    class DerivedRelationalModelSet {
        EffectiveSchemaInfo EffectiveSchema
        SqlDialect Dialect
        IReadOnlyList~ProjectSchemaInfo~ ProjectSchemasInEndpointOrder
        IReadOnlyList~ConcreteResourceModel~ ConcreteResourcesInNameOrder
        IReadOnlyList~AbstractIdentityTableInfo~ AbstractIdentityTablesInNameOrder
        IReadOnlyList~AbstractUnionViewInfo~ AbstractUnionViewsInNameOrder
        IReadOnlyList~DbIndexInfo~ IndexesInCreateOrder
        IReadOnlyList~DbTriggerInfo~ TriggersInCreateOrder
    }

    class EffectiveSchemaInfo {
        string EffectiveSchemaHash
        IReadOnlyList~ResourceKeyEntry~ ResourceKeysInIdOrder
    }

    class ProjectSchemaInfo {
        string ProjectEndpointName
        string ProjectName
        string ProjectVersion
        bool IsExtensionProject
        DbSchemaName PhysicalSchema
    }

    DerivedRelationalModelSet --> EffectiveSchemaInfo
    DerivedRelationalModelSet --> "0..*" ProjectSchemaInfo
    DerivedRelationalModelSet --> "0..*" ConcreteResourceModel
    DerivedRelationalModelSet --> "0..*" AbstractIdentityTableInfo
    DerivedRelationalModelSet --> "0..*" AbstractUnionViewInfo
    DerivedRelationalModelSet --> "0..*" DbIndexInfo
    DerivedRelationalModelSet --> "0..*" DbTriggerInfo
```

### What each collection represents

| Collection | Contents |
|-----------|----------|
| `ProjectSchemasInEndpointOrder` | Physical schema mappings per project (`ed-fi` → `edfi`, `tpdm` → `tpdm`) |
| `ConcreteResourcesInNameOrder` | One entry per concrete resource — the full relational model for that resource |
| `AbstractIdentityTablesInNameOrder` | Trigger-maintained identity tables for polymorphic abstract resources |
| `AbstractUnionViewsInNameOrder` | Diagnostic union views over concrete members of abstract resources |
| `IndexesInCreateOrder` | Complete index inventory (PK, unique, FK-support, explicit) |
| `TriggersInCreateOrder` | Complete trigger inventory (stamping, referential identity, abstract identity, propagation fallback) |

---

## 3. Zooming In: ConcreteResourceModel

Each concrete resource wraps a `RelationalResourceModel` with its storage kind and resource key.

```mermaid
classDiagram
    class ConcreteResourceModel {
        ResourceKeyEntry ResourceKey
        ResourceStorageKind StorageKind
        RelationalResourceModel RelationalModel
        DescriptorMetadata? DescriptorMetadata
    }

    class ResourceStorageKind {
        <<enumeration>>
        RelationalTables
        SharedDescriptorTable
    }

    class RelationalResourceModel {
        QualifiedResourceName Resource
        DbSchemaName PhysicalSchema
        ResourceStorageKind StorageKind
        DbTableModel Root
        IReadOnlyList~DbTableModel~ TablesInDependencyOrder
        IReadOnlyList~DocumentReferenceBinding~ DocumentReferenceBindings
        IReadOnlyList~DescriptorEdgeSource~ DescriptorEdgeSources
    }

    ConcreteResourceModel --> RelationalResourceModel
    ConcreteResourceModel --> ResourceStorageKind
    RelationalResourceModel --> DbTableModel : Root
    RelationalResourceModel --> "0..*" DbTableModel : TablesInDependencyOrder
    RelationalResourceModel --> "0..*" DocumentReferenceBinding
    RelationalResourceModel --> "0..*" DescriptorEdgeSource
```

### Storage kinds

- **`RelationalTables`** — normal resources: root table + child collection tables + extension tables
- **`SharedDescriptorTable`** — descriptors: stored in the shared `dms.Descriptor` table (no per-descriptor tables)

---

## 3a. Zooming In: DbTableModel

Every table in the model — root, collection, or extension — is represented by `DbTableModel`.

```mermaid
classDiagram
    class DbTableModel {
        DbTableName Table
        JsonPathExpression JsonScope
        TableKey Key
        IReadOnlyList~DbColumnModel~ Columns
        IReadOnlyList~TableConstraint~ Constraints
    }

    class TableKey {
        string ConstraintName
        IReadOnlyList~DbKeyColumn~ Columns
    }

    class DbKeyColumn {
        DbColumnName ColumnName
        ColumnKind Kind
    }

    class DbColumnModel {
        DbColumnName ColumnName
        ColumnKind Kind
        RelationalScalarType? ScalarType
        bool IsNullable
        JsonPathExpression? SourceJsonPath
        QualifiedResourceName? TargetResource
    }

    class ColumnKind {
        <<enumeration>>
        Scalar
        DocumentFk
        DescriptorFk
        Ordinal
        ParentKeyPart
    }

    DbTableModel --> TableKey
    DbTableModel --> "0..*" DbColumnModel
    DbTableModel --> "0..*" TableConstraint
    TableKey --> "1..*" DbKeyColumn
    DbColumnModel --> ColumnKind
    DbKeyColumn --> ColumnKind
```

### JsonScope examples

| Table type | JsonScope | Example |
|-----------|-----------|---------|
| Root | `$` | `edfi.Student` |
| Collection | `$.addresses[*]` | `edfi.SchoolAddress` |
| Nested collection | `$.addresses[*].periods[*]` | `edfi.SchoolAddressPeriod` |

### ColumnKind examples

| `ColumnKind` | Purpose | Example column | `ScalarType` | `SourceJsonPath` | `TargetResource` |
|---|---|---|---|---|---|
| `Scalar` | A value projected from the request JSON | `FirstName varchar(75)` | set | set (`$.firstName`) | null |
| `Scalar` | Propagated reference identity binding column (see note below) | `Student_StudentUniqueId varchar(32)` | set | set | set (`Ed-Fi.Student`) |
| `DocumentFk` | FK to another document's `DocumentId` | `Student_DocumentId bigint` | null (always bigint) | null | set (`Ed-Fi.Student`) |
| `DescriptorFk` | FK to `dms.Descriptor(DocumentId)` | `SchoolTypeDescriptor_DescriptorId bigint` | null (always bigint) | null | set (`Ed-Fi.SchoolTypeDescriptor`) |
| `Ordinal` | Array ordering column preserving element order | `Ordinal int` | null (always int) | null | null |
| `ParentKeyPart` | Key column inherited from an ancestor scope | `School_DocumentId bigint` (on a child table) | null (inherited) | null | null |

> **Note on reference identity binding columns**: Columns like `Student_StudentUniqueId` are `ColumnKind.Scalar` — from the table model's perspective they are typed, JSON-sourced scalar columns. Their reference semantics (composite FK participation, `ON UPDATE CASCADE` propagation) are captured separately in `DocumentReferenceBinding` and its `ReferenceIdentityBinding` list, not in the column kind. You can distinguish them from plain scalars by the presence of a non-null `TargetResource`.

---

## 3b. Zooming In: Constraints

Constraints are a discriminated union — three subtypes under `TableConstraint`.

```mermaid
classDiagram
    class TableConstraint {
        <<abstract>>
    }

    class Unique {
        string Name
        IReadOnlyList~DbColumnName~ Columns
    }

    class ForeignKey {
        string Name
        IReadOnlyList~DbColumnName~ Columns
        DbTableName TargetTable
        IReadOnlyList~DbColumnName~ TargetColumns
        ReferentialAction OnDelete
        ReferentialAction OnUpdate
    }

    class AllOrNoneNullability {
        string Name
        DbColumnName FkColumn
        IReadOnlyList~DbColumnName~ DependentColumns
    }

    class ReferentialAction {
        <<enumeration>>
        NoAction
        Cascade
    }

    TableConstraint <|-- Unique
    TableConstraint <|-- ForeignKey
    TableConstraint <|-- AllOrNoneNullability
    ForeignKey --> ReferentialAction : OnDelete
    ForeignKey --> ReferentialAction : OnUpdate
```

| Constraint type | Purpose | Example |
|----------------|---------|---------|
| `Unique` | Natural key (`UX_..._NK`), reference key (`UX_..._RefKey`), array uniqueness | `UX_Student_NK (StudentUniqueId)` |
| `ForeignKey` | Composite ref FKs, parent FKs, descriptor FKs | `FK_StudentSchoolAssociation_Student_RefKey` |
| `AllOrNoneNullability` | CHECK that reference group columns are all-null or all-populated | `CK_StudentSchoolAssociation_Student_AllNone` |

---

## 3c. Zooming In: Reference and Descriptor Bindings

These metadata records bind JSON reference/descriptor paths to their stored FK columns.

```mermaid
classDiagram
    class DocumentReferenceBinding {
        bool IsIdentityComponent
        JsonPathExpression ReferenceObjectPath
        DbTableName Table
        DbColumnName FkColumn
        QualifiedResourceName TargetResource
        IReadOnlyList~ReferenceIdentityBinding~ IdentityBindings
    }

    class ReferenceIdentityBinding {
        JsonPathExpression ReferenceJsonPath
        DbColumnName Column
    }

    class DescriptorEdgeSource {
        bool IsIdentityComponent
        JsonPathExpression DescriptorValuePath
        DbTableName Table
        DbColumnName FkColumn
        QualifiedResourceName DescriptorResource
    }

    DocumentReferenceBinding --> "1..*" ReferenceIdentityBinding
```

**`DocumentReferenceBinding`** — records how a JSON reference object maps to:
- a `..._DocumentId` FK column, plus
- per-identity-part binding columns (e.g., `Student_StudentUniqueId`)

**`ReferenceIdentityBinding`** — maps a single identity scalar within a reference to:
- a `ReferenceJsonPath` locating the scalar in the JSON reference object
- a `Column` storing that value (e.g., `Student_StudentUniqueId`)

**`DescriptorEdgeSource`** — records how a descriptor value path maps to:
- a `..._DescriptorId` FK column (targeting `dms.Descriptor(DocumentId)`)
- a `DescriptorResource` identifying the expected descriptor type (e.g., `Ed-Fi.SchoolTypeDescriptor`)

---

## 3d. Zooming In: Abstract Resources

For polymorphic/abstract reference targets (e.g., `EducationOrganization`).

```mermaid
classDiagram
    class AbstractIdentityTableInfo {
        ResourceKeyEntry AbstractResourceKey
        DbTableModel TableModel
    }

    class AbstractUnionViewInfo {
        ResourceKeyEntry AbstractResourceKey
        DbTableName ViewName
        IReadOnlyList~AbstractUnionViewOutputColumn~ OutputColumnsInSelectOrder
        IReadOnlyList~AbstractUnionViewArm~ UnionArmsInOrder
    }

    class AbstractUnionViewArm {
        ResourceKeyEntry ConcreteMemberResourceKey
        DbTableName FromTable
        IReadOnlyList~AbstractUnionViewProjectionExpression~ ProjectionExpressionsInSelectOrder
    }

    class AbstractUnionViewProjectionExpression {
        <<abstract>>
    }
    class SourceColumn {
        DbColumnName ColumnName
    }
    class StringLiteral {
        string Value
    }

    AbstractUnionViewInfo --> "1..*" AbstractUnionViewArm
    AbstractUnionViewArm --> "1..*" AbstractUnionViewProjectionExpression
    AbstractUnionViewProjectionExpression <|-- SourceColumn
    AbstractUnionViewProjectionExpression <|-- StringLiteral
    AbstractIdentityTableInfo --> DbTableModel
```

**`AbstractIdentityTableInfo`** — a trigger-maintained identity table that serves as a composite FK target for polymorphic references.

**`AbstractUnionViewInfo`** — a `UNION ALL` view across concrete member root tables, for diagnostic/query use:
- an `OutputColumnsInSelectOrder` defining the view's column list
- an `UnionArmsInOrder` list of per-concrete-member SELECT arms

**`AbstractUnionViewArm`** — one arm of the union view for a single concrete member:
- a `FromTable` identifying the concrete member's root table
- a `ProjectionExpressionsInSelectOrder` list of column expressions

**`AbstractUnionViewProjectionExpression`** — either a `SourceColumn` (real column from the member table) or a `StringLiteral` (constant, e.g., the resource type name).

---

## 3e. Zooming In: Index and Trigger Inventory

```mermaid
classDiagram
    class DbIndexInfo {
        DbIndexName Name
        DbTableName Table
        IReadOnlyList~DbColumnName~ KeyColumns
        bool IsUnique
        DbIndexKind Kind
    }

    class DbIndexKind {
        <<enumeration>>
        PrimaryKey
        UniqueConstraint
        ForeignKeySupport
        Explicit
    }

    class DbTriggerInfo {
        DbTriggerName Name
        DbTableName Table
        DbTriggerKind Kind
        IReadOnlyList~DbColumnName~ KeyColumns
    }

    class DbTriggerKind {
        <<enumeration>>
        DocumentStamping
        ReferentialIdentityMaintenance
        AbstractIdentityMaintenance
        IdentityPropagationFallback
    }

    DbIndexInfo --> DbIndexKind
    DbTriggerInfo --> DbTriggerKind
```

---

## 4. `DerivedRelationalModelSetBuilder` — Orchestration

The builder is intentionally simple: it creates a shared mutable context and runs passes in order.

```mermaid
flowchart TD
    subgraph Builder["DerivedRelationalModelSetBuilder.Build()"]
        direction TB
        CREATE["Create RelationalModelSetBuilderContext<br/>(validates EffectiveSchemaSet,<br/>normalizes project schemas,<br/>builds descriptor path maps)"]

        LOOP["For each pass in ordered pass list:<br/>pass.Execute(context)"]

        RESULT["context.BuildResult()<br/>(validates, canonicalizes ordering,<br/>returns immutable DerivedRelationalModelSet)"]

        CREATE --> LOOP --> RESULT
    end

    ESS["EffectiveSchemaSet"] --> CREATE
    DIALECT["SqlDialect + ISqlDialectRules"] --> CREATE
    RESULT --> DRMS["DerivedRelationalModelSet"]
```

### `RelationalModelSetBuilderContext` — the shared mutable state

The context holds:

| State | Purpose |
|-------|---------|
| `EffectiveSchemaSet` | The input schema set |
| `Dialect` / `DialectRules` | Target database engine |
| `ConcreteResourcesInNameOrder` | Mutable list — passes add/mutate resource models |
| `AbstractIdentityTablesInNameOrder` | Mutable list — abstract identity pass populates |
| `AbstractUnionViewsInNameOrder` | Mutable list — abstract union view pass populates |
| `IndexInventory` | Mutable list — passes add indexes |
| `TriggerInventory` | Mutable list — passes add triggers |
| `ProjectSchemasInEndpointOrder` | Mutable list — shortening pass may update schema names |
| Per-resource `RelationalModelBuilderContext` cache | Retains extracted metadata for cross-resource lookups |
| Extension site registry | `_ext` sites per resource, used by extension pass |
| Descriptor path maps | Pre-computed descriptor paths per resource |

---

## 5. Per-Resource Pipeline

The per-resource pipeline runs inside the first set-level pass (`BaseTraversalAndDescriptorBindingPass`). It derives the base relational model for a single resource.

```mermaid
flowchart TD
    subgraph Pipeline["RelationalModelBuilderPipeline.Run()"]
        direction TB
        S1["ExtractInputsStep<br/>Parse ApiSchema metadata:<br/>identity paths, references,<br/>overrides, constraints,<br/>descriptor paths, decimals"]

        S2["ValidateJsonSchemaStep<br/>Validate jsonSchemaForInsert:<br/>no $ref/oneOf/anyOf/allOf,<br/>arrays-are-tables rule,<br/>identity paths exist"]

        S3["DiscoverExtensionSitesStep<br/>Walk schema for _ext properties,<br/>record ExtensionSite metadata<br/>(owning scope, project keys)"]

        S4["DeriveTableScopesAndKeysStep<br/>Create root table and<br/>child tables for arrays,<br/>build composite PKs<br/>(DocumentId + ordinals)"]

        S5["DeriveColumnsAndBindDescriptorEdgesStep<br/>Walk each table scope,<br/>derive scalar columns,<br/>convert descriptor values<br/>to *_DescriptorId FK columns"]

        S6["CanonicalizeOrderingStep<br/>Sort tables, columns,<br/>constraints deterministically"]

        S1 --> S2 --> S3 --> S4 --> S5 --> S6
    end

    INPUT["ApiSchema.json +<br/>RelationalModelBuilderContext"] --> S1
    S6 --> OUTPUT["RelationalModelBuildResult<br/>(ResourceModel + ExtensionSites)"]
```

### `RelationalModelBuilderContext` — per-resource mutable state

Key properties populated by steps:

| Property | Populated by | Purpose |
|----------|-------------|---------|
| `JsonSchemaForInsert` | ExtractInputs | Fully dereferenced JSON schema |
| `IdentityJsonPaths` | ExtractInputs | Natural key paths |
| `DocumentReferenceMappings` | ExtractInputs | Reference metadata from `documentPathsMapping` |
| `ArrayUniquenessConstraints` | ExtractInputs | Collection uniqueness constraints |
| `NameOverridesByPath` | ExtractInputs | `relational.nameOverrides` entries |
| `DescriptorPathsByJsonPath` | Precomputed | Descriptor path → resource mappings |
| `DecimalPropertyValidationInfosByPath` | ExtractInputs | Precision/scale for decimal columns |
| `ExtensionSites` | DiscoverExtensionSites | `_ext` mapping sites discovered |
| `ResourceModel` | DeriveTableScopes + DeriveColumns | The derived relational model |

---

## 6. Set-Level Passes — Overview

After the per-resource pipeline runs for all resources, subsequent set-level passes stitch cross-resource artifacts.

```mermaid
flowchart TD
    subgraph Passes["Ordered Set-Level Passes"]
        direction TB
        P1["1. BaseTraversalAndDescriptorBindingPass<br/><i>Per-resource pipeline for each<br/>concrete non-extension resource</i>"]

        P2["2. DescriptorResourceMappingPass<br/><i>Detect descriptors, apply<br/>SharedDescriptorTable storage</i>"]

        P3["3. ExtensionTableDerivationPass<br/><i>Derive _ext tables aligned<br/>to base table scopes</i>"]

        P4["4. ReferenceBindingPass<br/><i>Add FK + identity columns<br/>for document references</i>"]

        P5["5. AbstractIdentityTableAndUnionViewDerivationPass<br/><i>Derive identity tables and<br/>union views for abstract resources</i>"]

        P6["6. RootIdentityConstraintPass<br/><i>Natural-key UNIQUE constraints<br/>and reference-key UNIQUE constraints</i>"]

        P7["7. ReferenceConstraintPass<br/><i>Composite FKs and<br/>all-or-none CHECKs</i>"]

        P8["8. ArrayUniquenessConstraintPass<br/><i>Collection-table UNIQUE<br/>constraints</i>"]

        P9["9. ApplyConstraintDialectHashingPass<br/><i>Dialect-specific constraint<br/>name hashing</i>"]

        P10["10. ApplyDialectIdentifierShorteningPass<br/><i>Shorten identifiers exceeding<br/>engine limits</i>"]

        P11["11. CanonicalizeOrderingPass<br/><i>Re-sort for deterministic<br/>output after mutation</i>"]

        P1 --> P2 --> P3 --> P4 --> P5 --> P6 --> P7 --> P8 --> P9 --> P10 --> P11
    end
```

---

## 7. Zooming In: Pass Groups by Concern

### Group A: Base Model (Passes 1-3)

Establishes the foundation — tables, columns, storage kinds, extensions.

```mermaid
flowchart LR
    subgraph P1["Pass 1: Base Traversal"]
        direction TB
        P1A["For each concrete resource:"]
        P1B["Run per-resource pipeline<br/>(6 steps)"]
        P1C["Register resource model<br/>+ extension sites"]
        P1A --> P1B --> P1C
    end

    subgraph P2["Pass 2: Descriptor Mapping"]
        direction TB
        P2A["Detect descriptor resources<br/>(isDescriptor flag)"]
        P2B["Validate schema compatibility<br/>with dms.Descriptor contract"]
        P2C["Set StorageKind =<br/>SharedDescriptorTable"]
        P2A --> P2B --> P2C
    end

    subgraph P3["Pass 3: Extension Tables"]
        direction TB
        P3A["For each resource with _ext sites:"]
        P3B["Walk extension JSON schema<br/>aligned to base table scopes"]
        P3C["Create extension tables<br/>(1:1 root + scope-aligned children)"]
        P3D["Add extension tables to<br/>resource's TablesInDependencyOrder"]
        P3A --> P3B --> P3C --> P3D
    end

    P1 --> P2 --> P3
```

### Group B: References (Passes 4-5)

Binds references across resources and handles polymorphism.

```mermaid
flowchart LR
    subgraph P4["Pass 4: Reference Binding"]
        direction TB
        P4A["For each document reference site:"]
        P4B["Add ..._DocumentId FK column"]
        P4C["Add {Ref}_{IdentityPart}<br/>binding columns"]
        P4D["Emit DocumentReferenceBinding"]
        P4E["For each descriptor site:"]
        P4F["Add ..._DescriptorId FK column"]
        P4A --> P4B --> P4C --> P4D
        P4E --> P4F
    end

    subgraph P5["Pass 5: Abstract Artifacts"]
        direction TB
        P5A["For each abstract resource:"]
        P5B["Create identity table<br/>(DocumentId + identity cols<br/>+ Discriminator)"]
        P5C["Resolve canonical column<br/>signatures across members"]
        P5D["Create union view<br/>(UNION ALL of concrete arms)"]
        P5A --> P5B --> P5C --> P5D
    end

    P4 --> P5
```

### Group C: Constraints (Passes 6-8)

Derives all constraint types — uniqueness and referential integrity.

```mermaid
flowchart LR
    subgraph P6["Pass 6: Root Identity Constraints"]
        direction TB
        P6A["Derive UX_..._NK<br/>(natural-key UNIQUE)"]
        P6B["Derive UX_..._RefKey<br/>(reference-key UNIQUE —<br/>FK target for composite refs)"]
        P6A --> P6B
    end

    subgraph P7["Pass 7: Reference Constraints"]
        direction TB
        P7A["Derive composite FKs to<br/>target reference-key UNIQUE"]
        P7B["Set ON UPDATE CASCADE<br/>or NO ACTION based on<br/>allowIdentityUpdates"]
        P7C["Derive all-or-none CHECK<br/>constraints for optional refs"]
        P7A --> P7B --> P7C
    end

    subgraph P8["Pass 8: Array Uniqueness"]
        direction TB
        P8A["Derive collection-table UNIQUE<br/>constraints from<br/>arrayUniquenessConstraints"]
        P8B["Map constraint paths to<br/>child-table scope columns"]
        P8A --> P8B
    end

    P6 --> P7 --> P8
```

### Group D: Naming and Finalization (Passes 9-11)

Makes identifiers safe for the target engine and deterministic.

```mermaid
flowchart LR
    subgraph P9["Pass 9: Constraint Hashing"]
        direction TB
        P9A["Append signature hash to<br/>constraint names that may<br/>exceed engine limits"]
        P9B["Handles PK, UNIQUE, FK,<br/>CHECK constraint names"]
        P9A --> P9B
    end

    subgraph P10["Pass 10: Identifier Shortening"]
        direction TB
        P10A["Shorten all identifiers<br/>exceeding engine limits"]
        P10B["PG: 63 bytes<br/>MSSQL: 128 chars"]
        P10C["prefix + '_' + sha256hex[0:10]"]
        P10D["Validate no collisions"]
        P10A --> P10B --> P10C --> P10D
    end

    subgraph P11["Pass 11: Canonicalize Ordering"]
        direction TB
        P11A["Re-sort tables, columns,<br/>constraints within each<br/>resource model"]
        P11B["Ensures deterministic output<br/>after mutation passes"]
        P11A --> P11B
    end

    P9 --> P10 --> P11
```

---

## Putting It All Together

```mermaid
flowchart TD
    subgraph Inputs["Inputs"]
        API["ApiSchema.json<br/>(core + extensions)"]
        DI["SqlDialect + ISqlDialectRules"]
    end

    subgraph SetBuilder["DerivedRelationalModelSetBuilder"]
        CTX["RelationalModelSetBuilderContext"]

        subgraph GroupA["Group A: Base Model"]
            P1["Pass 1: Base Traversal<br/>(runs per-resource pipeline)"]
            P2["Pass 2: Descriptor Mapping"]
            P3["Pass 3: Extension Tables"]
        end

        subgraph GroupB["Group B: References"]
            P4["Pass 4: Reference Binding"]
            P5["Pass 5: Abstract Artifacts"]
        end

        subgraph GroupC["Group C: Constraints"]
            P6["Pass 6: Root Identity"]
            P7["Pass 7: Reference FKs"]
            P8["Pass 8: Array Uniqueness"]
        end

        subgraph GroupD["Group D: Naming"]
            P9["Pass 9: Constraint Hashing"]
            P10["Pass 10: Shortening"]
            P11["Pass 11: Ordering"]
        end

        CTX --> GroupA --> GroupB --> GroupC --> GroupD
    end

    subgraph PerResourcePipeline["Per-Resource Pipeline (inside Pass 1)"]
        S1["ExtractInputs"]
        S2["ValidateJsonSchema"]
        S3["DiscoverExtensionSites"]
        S4["DeriveTableScopesAndKeys"]
        S5["DeriveColumnsAndBindDescriptorEdges"]
        S6["CanonicalizeOrdering"]
        S1 --> S2 --> S3 --> S4 --> S5 --> S6
    end

    P1 -.->|"runs for each resource"| PerResourcePipeline

    Inputs --> SetBuilder

    GroupD --> RESULT["DerivedRelationalModelSet"]

    RESULT --> DDL["DDL Emission"]
    RESULT --> PLANS["Plan Compilation"]
    RESULT --> MAN["Manifest Output"]
```

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Ordered passes, not visitors** | Dependencies between passes are explicit in ordering; no DAG resolution needed |
| **Mutable context, immutable output** | Passes freely mutate shared state; `BuildResult()` freezes into immutable records |
| **Per-resource pipeline inside Pass 1** | Base traversal is resource-local; cross-resource concerns live in later passes |
| **Constraint passes after reference binding** | FK constraints need reference columns to already exist |
| **Naming passes last** | All semantic derivation completes before identifier shortening and hashing |
| **Canonical ordering at both levels** | Per-resource pipeline orders once; final pass re-orders after mutation |

---

## Epic Story Mapping

| Pass | Story | Description |
|------|-------|-------------|
| 1. Base Traversal | DMS-929 | JSON schema → base tables/columns |
| 2. Descriptor Mapping | DMS-942 | Descriptor → `dms.Descriptor` storage |
| 3. Extension Tables | DMS-932, DMS-1035 | `_ext` relational mapping + common-type extensions |
| 4. Reference Binding | DMS-930 | References/descriptors + identity columns |
| 5. Abstract Artifacts | DMS-933 | Abstract identity tables + union views |
| 6. Root Identity | DMS-930 | Natural-key and reference-key UNIQUE constraints |
| 7. Reference Constraints | DMS-930 | Composite FKs + all-or-none CHECKs |
| 8. Array Uniqueness | DMS-930 | Collection uniqueness constraints |
| 9. Constraint Hashing | DMS-931 | Dialect-specific constraint naming |
| 10. Shortening | DMS-931 | Engine identifier length limits |
| 11. Ordering | DMS-934 | Deterministic manifest output |
| Set Builder | DMS-1033 | Orchestrates all passes |
| Index/Trigger Inventory | DMS-945 | Deterministic index + trigger derivation |
