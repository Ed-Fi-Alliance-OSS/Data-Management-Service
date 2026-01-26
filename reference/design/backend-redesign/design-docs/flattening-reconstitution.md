# Backend Redesign: Flattening & Reconstitution (Metadata-Driven Relational Mapping)

## Status

Draft.

This document is the flattening/reconstitution deep dive for `overview.md`.

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- AOT compilation (optional mapping pack distribution): [aot-compilation.md](aot-compilation.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

This section describes how DMS flattens JSON documents into relational tables on POST/PUT, and how it reconstitutes JSON from those tables on GET/query without code-generating per-resource code.

It also defines the minimal `ApiSchema.json` metadata needed (beyond what already exists) and proposes concrete C# shapes for implementing a high-performance, schema-driven flattener/reconstituter with good batching characteristics (no N+1 insert/query patterns).

## Table of Contents

- [1. Requirements & Constraints (Restated)](#1-requirements--constraints-restated)
- [2. Why “Derived Mapping” Can Be Enough (vs Full Flattening Metadata)](#2-why-derived-mapping-can-be-enough-vs-full-flattening-metadata)
- [3. Minimal `ApiSchema.json` Additions (Relational Block)](#3-minimal-apischemajson-additions-relational-block)
- [4. Derived Relational Resource Model (Runtime Compilation or AOT Pack)](#4-derived-relational-resource-model-runtime-compilation-or-aot-pack)
- [5. Flattening (POST/PUT) Design](#5-flattening-postput-design)
- [6. Reconstitution (GET by id / GET by query) Design](#6-reconstitution-get-by-id--get-by-query-design)
- [7. Concrete C# Shapes (No Per-Resource Codegen)](#7-concrete-c-shapes-no-per-resource-codegen)
- [8. Performance Notes / Failure Modes](#8-performance-notes--failure-modes)
- [9. Next Steps (Design → Implementation)](#9-next-steps-design--implementation)

---

## 1. Requirements & Constraints

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

Everything else can be derived and compiled once per schema version into a resource-specific plan (either at runtime with caching, or ahead-of-time into a mapping pack; see [aot-compilation.md](aot-compilation.md)).

---

## 3. Minimal `ApiSchema.json` Additions (Relational Block)

### 3.1 Goals of the block

- Keep `ApiSchema.json` authoritative but not bloated.
- Provide stable physical naming and a small escape hatch for “bad cases”.
- Avoid enumerating tables/columns explicitly.

Normative physical identifier rules (schema/table/column naming, quoting, truncation, and constraint/index naming) are defined in:
- `reference/design/backend-redesign/design-docs/data-model.md` (`Naming Rules (Deterministic, Cross-DB Safe)`)

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

### 3.3 Strict rules for `nameOverrides`

To keep the mapping deterministic, portable, and validatable, `nameOverrides` must follow these rules:

1. **JSONPath grammar is restricted**:
   - Must start at `$`
   - Only property segments (`.propertyName`) and array wildcards (`[*]`) are allowed
   - No numeric indexes (`[0]`), no filters (`[?()]`), no recursive descent (`..`), and no bracket property quoting

2. **Keys must match a derived mapping element**:
   - The key must match a derived column path or collection path for the resource.
   - Unknown keys are an error (startup schema validation fails fast) to prevent “silent typos”.

3. **Meaning depends on whether the key ends with `[*]`**:
   - If the key **ends with `[*]`**, it overrides the **collection table base name** for that array path (e.g., `$.addresses[*].periods[*]` → `SchoolAddressPeriod`).
   - Otherwise, it overrides the **column base name** at that JSONPath (before suffixes like `_DocumentId` / `_DescriptorId`).
     - For **document references**, the relevant path is the **reference object path** (e.g., `$.schoolReference`), not the identity field paths inside it (e.g., `$.schoolReference.schoolId`).

4. **Overrides cannot create collisions**:
   - After applying overrides and the standard identifier normalization/truncation rules, table/column names must still be unique.
   - Collisions are a compile-time error (startup schema validation fails) and must be resolved by adjusting overrides.

---

## 4. Derived Relational Resource Model (Runtime Compilation or AOT Pack)

For a given `EffectiveSchemaHash` that DMS serves, DMS builds (or loads from an AOT mapping pack) a fully explicit internal model:

- Root table name + full column list (scalars + FK columns)
- Child tables for each array path (and nested arrays)
- Column types/nullability/constraints
- Document-reference binding plan: JSON reference paths → FK columns + stored reference identity columns → referenced resource
- Descriptor edge source plan: descriptor JSON paths → FK columns (descriptor `DocumentId`, resolved via `dms.ReferentialIdentity`) and expected descriptor resource type
- JSON reconstitution plan: table+column → JSON property path writer instructions
- Reference reconstitution plan (local columns): reference-object writers populated from stored reference identity columns (per `DocumentReferenceBinding`)
- Abstract identity tables for polymorphic reference targets (from `abstractResources`)

This is not code generation; it is compiled (or deserialized) metadata cached by `(DmsInstanceId, EffectiveSchemaHash, ProjectName, ResourceName)`.

The same derived model is also built by the DDL generation utility to generate dialect-specific DDL and provision database schemas (see [ddl-generation.md](ddl-generation.md)).

Determinism requirement:
- The derived model and compiled plans must be reproducible for a given effective schema and mapping version.
- Do not depend on JSON file order, JSON property order, or dictionary iteration order when building `RelationalResourceModel` or compiling SQL/plans.
- Use the deterministic ordering rules defined in [ddl-generation.md](ddl-generation.md) (“Deterministic output ordering (DDL + packs)”) for tables, columns, constraints, indexes, and views.

### 4.0 `dms.ResourceKey` validation and `ResourceKeyId` mapping (AOT pack)

Core tables store resource type as `ResourceKeyId` (see `dms.ResourceKey` in [data-model.md](data-model.md)), while compiled plans are keyed by `QualifiedResourceName(ProjectName, ResourceName)`.

In AOT mode, the mapping pack **embeds** the deterministic `dms.ResourceKey` seed list for that `EffectiveSchemaHash` (ordered `(ResourceKeyId, ProjectName, ResourceName, ResourceVersion)`), and DDL provisioning records a matching `ResourceKeySeedHash` in `dms.EffectiveSchema` for fast runtime validation (`ResourceKeySeedHash` is raw SHA-256 bytes, 32 bytes).

On first use of a database (after reading its recorded `EffectiveSchemaHash` and selecting/loading the mapping pack), DMS must:
1. Read `ResourceKeyCount` and `ResourceKeySeedHash` from the singleton `dms.EffectiveSchema` row.
2. Compare to the expected fingerprint from the mapping set (derived from the embedded seed list), byte-for-byte.
3. If the fingerprint matches, accept without reading `dms.ResourceKey` (fast path).
4. If the fingerprint mismatches (or the columns are missing), fall back to reading `dms.ResourceKey` ordered by `ResourceKeyId` and diffing against the embedded list for diagnostics, then fail fast.
5. Cache bidirectional maps for the lifetime of the mapping set:
   - `QualifiedResourceName -> ResourceKeyId` (writes, change-query parameters)
   - `ResourceKeyId -> (QualifiedResourceName, ResourceVersion)` (background tasks, diagnostics, denormalized metadata materialization)

Fail fast if the table does not match the pack (schema mismatch / mis-provisioned database).

Operational mitigation (recommended):
- Treat `dms.ResourceKey` and the singleton `dms.EffectiveSchema` row as immutable seed artifacts.
- Provision with a privileged role, and run DMS with a runtime database principal that has read-only access to these tables. This prevents post-provisioning drift that the `ResourceKeySeedHash` fast-path check cannot detect without a full `dms.ResourceKey` diff.

### 4.1 Derivation algorithm (high-level)

Inputs:
- `resourceSchema.jsonSchemaForInsert` (and update variant if needed)
- `resourceSchema.documentPathsMapping` (references/descriptors)
- `resourceSchema.identityJsonPaths`
- `resourceSchema.decimalPropertyValidationInfos`
- `resourceSchema.arrayUniquenessConstraints`
- `projectSchema.abstractResources` (abstract identity fields for polymorphic targets)
- extension shape under `_ext` in the effective JSON schema (see [extensions.md](extensions.md))
- optional `resourceSchema.relational` overrides

Important note on `additionalProperties`:
- DMS Core prunes overposted data during request validation (properties reported under JSON Schema `additionalProperties`), then re-validates the pruned body.
- As a result, `jsonSchemaForInsert.additionalProperties=true` is treated as “allow overposting, then ignore it” rather than “persist arbitrary dynamic properties”.
- In relational mode, the derived mapping therefore treats schemas as effectively **closed-world** (persist only known properties); dictionary/map semantics via `additionalProperties` are not supported.

#### Algorithm sketch

Note: C# types referenced below are defined in [7.3 Relational resource model](#73-relational-resource-model)

1. Validate `jsonSchemaForInsert` is fully expanded (no `$ref`). If `$ref` appears, treat it as invalid schema input.
2. Walk the schema tree starting at `$`:
   - Each **array** node creates a **child table** with:
     - parent key columns
     - `Ordinal` (order preservation)
     - derived element columns
   - Each **object** node is **inlined** (except `_ext`): its scalar descendant properties become columns on the current table (with a deterministic prefix).
     - `_ext` handling: treat each `_ext.{project}` subtree as belonging to that extension project’s schema and derive extension tables using the extension naming patterns (e.g., `{Resource}Extension`, `{Resource}Extension{Suffix}`) aligned to the owning scope’s key/ordinals (see [extensions.md](extensions.md)).
   - Each **scalar** node becomes a typed column on the current table.

3. Apply `documentPathsMapping`:
   - For each reference object path (`referenceJsonPaths`):
     - create a `..._DocumentId` FK column at the table scope that owns that path and will represent the entire reference object as a single `..._DocumentId` foreign key
     - derive scalar columns for the reference object’s **identity fields** (one per referenced identity component) using the `{ReferenceBaseName}_{IdentityFieldBaseName}` naming rule
     - suppress scalar-column derivation for the reference object’s `link` internals (if present); link values are not persisted
     - record a `DocumentReferenceBinding` (used for write-time FK + identity column population and for query compilation of reference-identity fields)
       - compute and persist `DocumentReferenceBinding.IsIdentityComponent` as `true` when any `identityJsonPaths` element is sourced from this reference object (i.e., when any `referenceJsonPaths[*].referenceJsonPath` is present in `identityJsonPaths`)
   - For each descriptor path:
     - create a `..._DescriptorId` FK column at the table scope that owns that path
     - suppress the raw descriptor string scalar column at that JSON path; reconstitute the string from `dms.Descriptor` during reads.
     - record a `DescriptorEdgeSource` (expected descriptor resource type, used for resolution/validation and read-time descriptor reconstitution)
       - compute and persist `DescriptorEdgeSource.IsIdentityComponent` as `true` when the descriptor value path is present in `identityJsonPaths`

4. Apply `identityJsonPaths`:
   - derive a deterministic natural-key UNIQUE constraint on the root table:
     - scalar identity elements map to scalar columns
     - identity elements that come from document references map to the corresponding `..._DocumentId` FK columns
   - `dms.ReferentialIdentity` is the primary identity resolver for all resources (including reference-bearing identities), and must be maintained transactionally (including cascades) so `ReferentialId → DocumentId` is never stale after commit.

5. Apply `arrayUniquenessConstraints`:
   - create UNIQUE constraints on child tables based on the specified JSONPaths (mapped to columns)

6. Apply naming rules + `nameOverrides`:
   - resolve all table and column names deterministically
   - validate no collisions and fail fast if any occur

7. Apply `abstractResources` (polymorphic identity tables; optional views):
   - For each abstract resource `A`, create a physical identity table `{schema}.{A}Identity` with:
     - `DocumentId` (PK; FK → `dms.Document(DocumentId)` ON DELETE CASCADE),
     - abstract identity fields (from `abstractResources[A].identityPathOrder`),
     - optional `Discriminator`.
   - Maintain `{schema}.{A}Identity` via triggers on each participating concrete root table (upsert on insert/update of identity columns).
   - Use `{schema}.{A}Identity` as the composite-FK target for abstract reference sites so `ON UPDATE CASCADE` can propagate identity changes and enforce membership/type at the DB level.
   - (Optional) also emit `{schema}.{A}_View` as a narrow `UNION ALL` projection for diagnostics/ad-hoc querying.

### 4.2 Recommended child-table keys (composite parent+ordinal)

To avoid `INSERT ... RETURNING` identity capture and to batch nested collections efficiently, instead of a surrogate key use:

- Root table PK: `(DocumentId)`
- Child table PK: `(ParentKeyPart..., Ordinal)`
- Nested child PK: `(DocumentId, ParentOrdinal(s)..., Ordinal)`

Example:

```sql
CREATE TABLE IF NOT EXISTS edfi.SchoolAddress (
    School_DocumentId bigint NOT NULL,
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

---

## 5. Flattening (POST/PUT) Design

### 5.1 Inputs

For a given request, backend already has:
- `ResourceInfo` (project/resource/version)
- validated/coerced JSON body (`JsonNode` / `JsonElement`) after Core canonicalization:
  - overposted fields (JSON Schema `additionalProperties`) removed
  - null-valued properties removed
  - empty arrays (and arrays of empty objects) removed
  - selected top-level descriptor strings trimmed (`codeValue`, `shortDescription`)
- `DocumentInfo`:
  - resource referential id
  - descriptor references (with concrete JSON paths, including indices)
  - document references (referential ids + concrete reference-object JSON paths, including indices; grouped by wildcard path; requires addition of `DocumentReference.Path` as described below)

### 5.2 Reference & descriptor resolution (bulk)

Before writing resource tables:

- Resolve all **document references**:
  - resolve to `DocumentId` via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`) for all identities (self-contained, reference-bearing, and polymorphic/abstract via superclass alias rows)
- Resolve all **descriptor references**:
  - `dms.ReferentialIdentity`: `ReferentialId → DocumentId` (descriptor referential ids are computed by Core from descriptor resource name + normalized URI)

Cache these lookups aggressively (L1/L2 optional), but only populate caches after commit.

### 5.2.1 Document references inside nested collections

When a document reference appears inside a collection (or nested collection), its FK is stored in a **child table row** whose key includes one or more **ordinals**. To set the correct FK column without per-row JSONPath evaluation and per-row referential id hashing, we need a stable way to answer:

> For this `DocumentReferenceBinding`, and for this row’s **ordinal path**, what is the referenced `DocumentId`?

DMS uses a single required approach: Core emits each reference instance *with its concrete JSON location (indices)* so the backend can address it by ordinal path.

- Keeps referential id computation centralized in Core; flattening becomes pure lookup; nested collections work naturally with composite parent+ordinal keys.
- Requires enhancing Core’s extracted reference model to carry location; backend builds a small per-request index.
- This is the **only** required Core change in this redesign.

**What Core must provide (minimal enhancement)**

Core changes:
- Add `JsonPath Path` to `Core.External.Model.DocumentReference` representing the **concrete reference-object path including indices**, e.g. `$.addresses[2].periods[0].calendarReference`.
- Continue emitting the **wildcard reference-object path** via `DocumentReferenceArray.arrayPath`, e.g. `$.addresses[*].periods[*].calendarReference`.
- Keep the computed `ReferentialId` on `DocumentReference` as today.

This is the same pattern already used for descriptor extraction (`DescriptorReference.Path`).

**ReferenceExtractor implementation guidance (Core)**

Update Core’s `ReferenceExtractor` to populate `DocumentReference.Path` by using Json.Path location information (avoid the current “parallel value slice” approach):

1. For each `DocumentPath` with `isReference=true` and `isDescriptor=false`, and for each `referenceJsonPaths[*]` entry:
   - evaluate the `referenceJsonPath` using `SelectNodesAndLocationFromArrayPathCoerceToStrings(...)` (already available in `JsonHelpers`)
   - for each match, take the match’s concrete scalar path (e.g. `$.addresses[2].periods[0].calendarReference.calendarCode`) and derive the **reference-object path** by stripping the final segment (→ `$.addresses[2].periods[0].calendarReference`)
2. Group extracted identity values by concrete reference-object path.
3. For each concrete reference-object path group:
   - build `DocumentIdentity` using the `referenceJsonPaths[*]` order (identity json paths + values)
   - compute `ReferentialId` using the existing UUIDv5 algorithm
   - emit `DocumentReference(Path=...)`
4. Emit `DocumentReferenceArray` for the reference site using the wildcard reference-object path (as today), but with `DocumentReferences` ordered by concrete path/document order.

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
- M extension row buffers (root + scope tables) for each extension project present in the document (see [extensions.md](extensions.md))

Important: materialization traverses each JSON array exactly once and does not perform any DB calls.

Code sketch:
```C#
public sealed class ResourceFlattener : IResourceFlattener
  {
      public FlattenedWriteSet Flatten(
          ResourceWritePlan plan,
          long documentId,
          JsonNode document,
          IDocumentReferenceInstanceIndex documentReferences,
          ResolvedReferenceSet resolved)
      {
          // pseudocode: plan.TableRows[...] = ...
          var childRows = new Dictionary<DbTableName, IReadOnlyList<RowBuffer>>();

          // pseudocode: rootRow = plan.Root.CreateRow(documentId) + SetScalars + SetFks
          var rootTable = plan.Model.Root.Table;
          var rootTablePlan = plan.TablePlans[rootTable];

          var rootRow = MaterializeRow(
              tablePlan: rootTablePlan,
              documentId: documentId,
              scopeNode: document,                       // root scope "$"
              parentKeyParts: new[] { documentId },      // not important for root; keeps the signature uniform
              ordinal: 0,
              ordinalPath: ReadOnlySpan<int>.Empty,
              documentReferences: documentReferences,
              resolved: resolved);

          // pseudocode: for each childPlan in plan.ChildrenDepthFirst ...
          foreach (var tableModel in plan.Model.TablesInWriteDependencyOrder)
          {
              if (tableModel.Table.Equals(rootTable))
                  continue;

              var tablePlan = plan.TablePlans[tableModel.Table];
              var rows = new List<RowBuffer>();

              // pseudocode: for each element in EnumerateArray(documentJson, childPlan.ArrayPath)
              foreach (var scope in EnumerateTableScopeInstances(document, tableModel.JsonScope))
              {
                  var ordinalPath = scope.OrdinalPath.AsSpan(); // e.g. [2] or [2,0]
                  var ordinal = ordinalPath[^1];                // last index is this table row's Ordinal
                  var parentKeyParts = BuildParentKeyParts(documentId, ordinalPath); // DocumentId + ancestor ordinals

                  // pseudocode: row = childPlan.CreateRow(parentKey, ordinal) + SetScalars + SetFks
                  rows.Add(MaterializeRow(
                      tablePlan,
                      documentId,
                      scopeNode: scope.ScopeNode,               // the object at this table scope
                      parentKeyParts: parentKeyParts,
                      ordinal: ordinal,
                      ordinalPath: ordinalPath,
                      documentReferences: documentReferences,
                      resolved: resolved));
              }

              // pseudocode: plan.TableRows[childPlan.Table] = rows
              childRows[tableModel.Table] = rows;
          }

          return new FlattenedWriteSet(rootTable, rootRow, childRows);
      }

      private static long[] BuildParentKeyParts(long documentId, ReadOnlySpan<int> ordinalPath)
      {
          // PK scheme: (DocumentId, ancestorOrdinal1, ..., ancestorOrdinalN, Ordinal)
          // ParentKeyParts = everything except this row's Ordinal.
          var parentKeyParts = new long[1 + Math.Max(0, ordinalPath.Length - 1)];
          parentKeyParts[0] = documentId;

          for (var i = 0; i < ordinalPath.Length - 1; i++)
              parentKeyParts[1 + i] = ordinalPath[i];

          return parentKeyParts;
      }

      private readonly record struct TableScopeInstance(JsonNode ScopeNode, int[] OrdinalPath);

      private static IEnumerable<TableScopeInstance> EnumerateTableScopeInstances(JsonNode root, JsonPathExpression jsonScope)
      {
          // This is the C# equivalent of the pseudocode's:
          //   EnumerateArray(documentJson, childPlan.ArrayPath)
          //
          // Examples of what it should yield:
          // - scope "$.addresses[*]"              -> (addresses[i], [i])
          // - scope "$.addresses[*].periods[*]"   -> (periods[j],  [i, j])
          //
          // Implementation is intentionally omitted here; later code assumes
          // you avoid JSONPath evaluation in the hot loop and track ordinals as you enumerate.
          throw new NotImplementedException();
      }

      // MaterializeRow(...) is the helper shown later in the document.
  }

```

### 5.4 Write execution (single transaction, no N+1)

Within a single transaction:

1. Write `dms.Document` and `dms.ReferentialIdentity`
2. Write resource root row (`INSERT` or `UPDATE`)
3. For each child table (depth-first):
   - `DELETE FROM Child WHERE ParentKey = @documentId` (or rely on cascade from the parent resource root row if you do “delete then insert root” in some flows)
   - bulk insert all rows for that child table in batches
4. Write extension tables (per extension project schema):
   - 1:1 extension root table rows (keyed by `DocumentId`)
   - extension scope/collection rows keyed to the same composite keys as the base scope they extend (document id + ordinals)
   - use the same baseline “replace” strategy as core collections (delete existing, insert current)
5. No derived reverse-edge maintenance is required:
   - referential-id impacts propagate via `ON UPDATE CASCADE` into stored reference identity columns, and
   - row-local triggers maintain `dms.ReferentialIdentity` and update-tracking stamps in the same transaction.

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

- **GET by id**: resolve `DocumentUuid → DocumentId`, then the keyset is a single `DocumentId`.
- **GET by query**: compile a parameterized SQL over the **resource root table** that selects the `DocumentId`s in the requested page (filters + `ORDER BY r.DocumentId` + paging).

All subsequent reads “hydrate” root + child tables by joining to that keyset.
Extension tables (in extension project schemas) are hydrated the same way: select by the page keyset and attach by the same key/ordinal columns (see [extensions.md](extensions.md)).

#### Query predicates for reference identity fields (local columns)

`ApiSchema.queryFieldMapping` can expose query parameters that correspond to identity fields inside a **reference object** (e.g., `studentUniqueId` mapped from `$.studentReference.studentUniqueId`).

In this redesign, identity fields inside reference objects are persisted as local columns alongside the FK:

- `..._DocumentId` (stable FK), plus
- `{ReferenceBaseName}_{IdentityFieldBaseName}` columns for the referenced identity fields,
  kept consistent via composite FKs with `ON UPDATE CASCADE`.

Therefore the query compiler can translate reference-identity query fields into simple predicates on the querying table, without subqueries:

- Complete or partial referenced identities compile to conjunctions over the provided identity-part columns.

Example (conceptual): query `StudentAssessmentRegistration` by `studentUniqueId`:

```sql
SELECT r.DocumentId
FROM edfi.StudentAssessmentRegistration r
WHERE r.Student_StudentUniqueId = @StudentUniqueId
ORDER BY r.DocumentId
OFFSET @Offset
LIMIT @Limit;
```

#### Single round-trip: materialize a `page` keyset, then hydrate by join

Build one command that:
1) materializes the page keyset server-side, then
2) returns multiple result sets by joining each table to that keyset.

```sql
-- Materialize the page keyset (engine-specific DDL, same logical outcome):
-- PostgreSQL: CREATE TEMP TABLE page (DocumentId bigint PRIMARY KEY) ON COMMIT DROP;
-- SQL Server: CREATE TABLE #page (DocumentId bigint PRIMARY KEY);
-- Note: in SQL Server, use #page in place of page in the statements below.

WITH page_ids AS (
  -- Provided by DMS query compilation (ApiSchema-driven):
  -- GET by id:    SELECT @DocumentId AS DocumentId
  -- GET by query: SELECT r.DocumentId FROM <schema>.<ResourceRoot> r WHERE ... ORDER BY r.DocumentId OFFSET/FETCH ...
  <PageDocumentIdSql>
)
INSERT INTO page (DocumentId)
SELECT DocumentId FROM page_ids;

-- Optional (if totalCount=true): add a result set that uses the same filters without paging.
-- SELECT COUNT(*) AS TotalCount FROM ... WHERE ...;

SELECT
  d.DocumentId,
  d.DocumentUuid,
  d.ContentVersion,
  d.IdentityVersion,
  d.ContentLastModifiedAt,
  d.IdentityLastModifiedAt
FROM dms.Document d
JOIN page p ON p.DocumentId = d.DocumentId;

-- Root table rows
SELECT r.*
FROM <schema>.<ResourceRoot> r
JOIN page p ON p.DocumentId = r.DocumentId
ORDER BY r.DocumentId;

-- Child table rows (one result set per child table)
SELECT c.*
FROM <schema>.<ChildTable> c
JOIN page p ON p.DocumentId = c.<RootDocumentIdColumn>
ORDER BY c.<RootDocumentIdColumn>, c.<ParentOrdinalColumns...>, c.Ordinal;
```

This avoids N+1 queries and keeps network round-trips minimal (one command, multiple result sets).

#### Example: what the multi-result response looks like

Conceptual example for `GET /ed-fi/schools?offset=0&limit=2&totalCount=true`, where the page keyset is `DocumentId IN (2001, 2002)` and the derived model has:
- root table: `edfi.School`
- child table: `edfi.SchoolAddress`
- nested child: `edfi.SchoolAddressPeriod`

The single DB command returns *multiple result sets* (read sequentially via `NextResult()`):

**Result set 1 (optional `totalCount`)**

```text
TotalCount
---------
57
```

**Result set 2 (`dms.Document` joined to the `page` keyset)**

```text
DocumentId | DocumentUuid                           | ContentVersion | IdentityVersion | ContentLastModifiedAt       | IdentityLastModifiedAt
---------- | -------------------------------------- | -------------- | -------------- | --------------------------- | ---------------------------
2001       | 7c5a4c7e-1b2c-4b3d-9f2e-6d0c8c0f8a11   | 14             | 14             | 2026-01-06T18:22:41Z        | 2026-01-06T18:22:41Z
2002       | 1a2b3c4d-5e6f-7081-9201-aabbccddeeff   |  2             |  2             | 2026-01-05T09:10:00Z        | 2026-01-05T09:10:00Z
```

**Result set 3 (root rows: `edfi.School`)**

```text
DocumentId | SchoolId | NameOfInstitution | SchoolTypeDescriptor_DescriptorId
---------- | -------- | ----------------- | --------------------------------
2001       | 255901   | Lincoln HS        | 90012
2002       | 255902   | Roosevelt MS      | 90012
```

**Result set 4 (child rows: `edfi.SchoolAddress`)**

```text
School_DocumentId | Ordinal | AddressTypeDescriptor_DescriptorId | StreetNumberName | City
----------------- | ------- | --------------------------------- | ---------------- | -----------
2001              | 0       | 91001                             | 123 Main St      | Springfield
2001              | 1       | 91002                             | 45 Oak Ave       | Springfield
2002              | 0       | 91001                             | 9 Pine Rd        | Centerville
```

**Result set 5 (nested child rows: `edfi.SchoolAddressPeriod`)**

```text
School_DocumentId | AddressOrdinal | Ordinal | BeginDate   | EndDate
----------------- | ------------- | ------- | ----------- | ----------
2001              | 0             | 0       | 2025-08-15  | NULL
2001              | 0             | 1       | 2026-01-10  | NULL
2002              | 0             | 0       | 2024-08-15  | 2025-06-30
```

### 6.2 Assembly strategy (in-memory)

Reconstitution walks the derived table/tree model:

1. Read the root rows into a dictionary `DocumentId → RootRow`.
2. For each child table in dependency order (including extension scope/collection tables):
   - group child rows by their parent key `(DocumentId, parentOrdinal...)`
   - attach to the parent as a list, ordered by `Ordinal`
3. Once all rows are attached, write JSON using a streaming writer.

### 6.3 Reference expansion (local columns)

To reconstitute reference objects, DMS must output the referenced resource’s identity values (natural keys), not the referenced `DocumentId`.

In this redesign, identity fields inside reference objects are stored as local columns alongside the FK:

- `..._DocumentId`, plus
- `{ReferenceBaseName}_{IdentityFieldBaseName}` columns,
  kept consistent via composite FKs with `ON UPDATE CASCADE`.

Therefore reference expansion during JSON writing is a pure “read local columns and emit the reference object” operation. No batched reference identity projection queries (joins to referenced tables or `{AbstractResource}_View`) are required to populate reference identity fields.

For polymorphic/abstract targets, referrers store the abstract identity fields (e.g., `EducationOrganizationId`) and enforce membership via a composite FK to `{schema}.{AbstractResource}Identity`.

### 6.4 JSON assembly (fast, shape-safe)

Use `Utf8JsonWriter` to avoid building large intermediate `JsonNode` graphs:
- write scalars from root/child rows using the compiled column→jsonPath writers
- write arrays in `Ordinal` order
- write references from stored reference identity columns (per `DocumentReferenceBinding`)
- write descriptor strings by looking up `dms.Descriptor.Uri`
- write `_ext` blocks from extension tables (only when at least one extension value is present at that scope)
- inject `id` from `dms.Document.DocumentUuid`
- serve `_etag` and `_lastModifiedDate` from stored `dms.Document` stamps (`ContentVersion`/`ContentLastModifiedAt`); these values must not be generated as “now” at read/materialization time.

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
2. **Plan layer** (`ResourceWritePlan`, `ResourceReadPlan`): compiled SQL plus precomputed details needed for efficient execution.
3. **Execution layer** (`IResourceFlattener`, `IBulkInserter`, `IResourceReconstituter`): runtime services that consume the plans.

These per-resource shapes are intended to be assembled into a single, shared mapping object graph:
- `DerivedRelationalModelSet` (dialect-aware, SQL-free inventory used by DDL emission and plan compilation; includes DDL intent like indexes/triggers)
- `MappingSet` (dialect-specific derived model set + compiled plans used at runtime and by mapping packs)

See: `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`.

All three layers are cached by `(DmsInstanceId, EffectiveSchemaHash, ProjectName, ResourceName)` so the per-request cost is only: reference resolution + row materialization + SQL execution + JSON writing.

Note: when runtime work starts from `ResourceKeyId` (e.g., Change Query filters or background projection rebuild batches), DMS uses the cached `ResourceKeyId -> (QualifiedResourceName, ResourceVersion)` map (validated above) to locate the correct cached plans (via `QualifiedResourceName`) and to materialize denormalized metadata (via `ResourceVersion`).

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

#### Scalar type mapping (dialect defaults)

These mappings are a determinism contract for both:
- the DDL generation utility (DDL emitted/provisioned), and
- the runtime plan compiler (parameter types, casts, and view definitions).

Rules:
- `ScalarKind.String` requires `maxLength` in `jsonSchemaForInsert`; missing `maxLength` is a schema compilation error.
  - Descriptor URI strings are not stored as strings; they are stored as `..._DescriptorId` FKs, so this rule applies to scalar columns only.
- `ScalarKind.Decimal` requires a matching entry in `decimalPropertyValidationInfos` (`totalDigits`, `decimalPlaces`); missing info is a schema compilation error.
- `ScalarKind.DateTime` uses SQL Server `datetime2(7)` (no timezone) to align with Ed-Fi ODS SQL Server conventions; any incoming offsets are normalized to a UTC instant at write time.

| `ScalarKind` | ApiSchema JSON schema source | PostgreSQL type | SQL Server type |
| --- | --- | --- | --- |
| `String` | `type: "string"` (no `format`) | `varchar(n)` | `nvarchar(n)` |
| `Int32` | `type: "integer"` | `integer` | `int` |
| `Int64` | (not typical in Ed-Fi, reserved) | `bigint` | `bigint` |
| `Decimal` | `type: "number"` + `decimalPropertyValidationInfos` | `numeric(p,s)` | `decimal(p,s)` |
| `Boolean` | `type: "boolean"` | `boolean` | `bit` |
| `Date` | `type: "string", format: "date"` | `date` | `date` |
| `DateTime` | `type: "string", format: "date-time"` | `timestamp with time zone` | `datetime2(7)` |
| `Time` | `type: "string", format: "time"` | `time` | `time(7)` |

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

### 7.3 Relational resource model

The shape model is the output of the “derive from ApiSchema” step. It is:
- **resource-specific** (one per resource type)
- **fully explicit** (tables/columns/keys enumerated)
- **SQL-free** (no SQL strings inside; physical identifiers may be dialect-shortened)

```csharp
/// <summary>
/// Fully derived relational mapping for a single API resource type.
/// This is the canonical shape model used by:
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
/// <param name="DocumentReferenceBindings">
/// The set of document-reference bindings derived from documentPathsMapping.referenceJsonPaths.
/// Each binding declares: which JSON reference object it came from, which table stores the FK and propagated identity columns, and how to populate/reconstitute the reference object from local columns.
/// </param>
/// <param name="DescriptorEdgeSources">
/// The set of descriptor-reference sources derived from documentPathsMapping (descriptor paths).
/// Each source declares: which JSON descriptor string path it came from, which table stores the FK, and which descriptor resource type is expected.
/// </param>
public sealed record RelationalResourceModel(
    QualifiedResourceName Resource,
    DbSchemaName PhysicalSchema,
    DbTableModel Root,
    IReadOnlyList<DbTableModel> TablesInReadDependencyOrder,
    IReadOnlyList<DbTableModel> TablesInWriteDependencyOrder,
    IReadOnlyList<DocumentReferenceBinding> DocumentReferenceBindings,
    IReadOnlyList<DescriptorEdgeSource> DescriptorEdgeSources
);

/// <summary>
/// Shape model for a single relational table (root or child).
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
/// Logical constraints that map to physical database constraints.
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
/// Declares how a document reference object is stored and reconstituted.
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
/// <param name="IdentityBindings">
/// The ordered identity field bindings derived from ApiSchema documentPathsMapping.referenceJsonPaths.
/// These bindings are used for:
/// - write-time column population of stored reference identity fields
/// - query compilation for reference-identity query fields (local predicates)
/// - read-time reference reconstitution from local columns
/// </param>
/// <param name="IsIdentityComponent">
/// True when this reference contributes to the parent document's identity (the referenced identity values are part of the parent's <c>identityJsonPaths</c>).
/// </param>
public sealed record DocumentReferenceBinding(
    bool IsIdentityComponent,
    JsonPathExpression ReferenceObjectPath,        // e.g. "$.schoolReference" or "$.students[*].studentReference"
    DbTableName Table,
    DbColumnName FkColumn,                         // e.g. "School_DocumentId"
    QualifiedResourceName TargetResource,
    IReadOnlyList<ReferenceIdentityBinding> IdentityBindings
);

/// <summary>
/// Maps one identity field inside the reference object to a physical column on the referencing table.
/// </summary>
/// <param name="ReferenceJsonPath">Absolute path to the identity field inside the reference object in the referencing document.</param>
/// <param name="Column">Physical column holding the identity value (e.g. <c>Student_StudentUniqueId</c>).</param>
public sealed record ReferenceIdentityBinding(
    JsonPathExpression ReferenceJsonPath,
    DbColumnName Column
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
/// </param>
/// <param name="IsIdentityComponent">
/// True when this descriptor value participates in the parent document's identity (the descriptor URI is part of the parent's <c>identityJsonPaths</c>).
/// Used when projecting identity values from relational storage for referential-id computation (descriptor rows are treated as immutable in this redesign).
/// </param>
public sealed record DescriptorEdgeSource(
    bool IsIdentityComponent,
    JsonPathExpression DescriptorValuePath,        // path of the string descriptor URI in JSON
    DbTableName Table,
    DbColumnName FkColumn,                         // ..._DescriptorId
    QualifiedResourceName DescriptorResource        // e.g. ("EdFi","GradeLevelDescriptor")
);
```

Notes:
- `DocumentReferenceBinding.IdentityBindings` is derived from existing `documentPathsMapping.referenceJsonPaths`.

### 7.4 Write and read plans (plan layer)

The plan layer is compiled from the shape model + SQL dialect + runtime conventions.

Plans contain:
- SQL strings (parameterized)
- binding rules (column order, batching limits, expected keyset shape)
- *no* per-resource code; only metadata + generic executors

#### SQL text canonicalization (required)

All SQL strings emitted into compiled plans (runtime caches and/or AOT mapping packs) MUST be **byte-for-byte stable** for a fixed `(EffectiveSchemaHash, dialect, relational mapping version)`:

- **Whitespace/formatting**: use `\n` line endings, stable indentation, no trailing whitespace, and stable keyword casing per dialect.
- **Clause ordering**: emit stable select-list order, stable join order, stable predicate order, and stable `IN (...)`/TVP/array patterns for the same logical query.
- **Alias naming**: generate stable table/column aliases deterministically from the plan/model (no randomized suffixes).
- **Parameter naming**: generate parameter names deterministically from the binding model (no GUIDs, no hash-map iteration order). When duplicates are possible, use a deterministic de-duplication scheme.

Implementation guidance:
- Use a single dialect-specific SQL writer/formatter shared by both:
  - the DDL generator (`ddl-generation.md`), and
  - the plan compiler (this document),
  so canonicalization rules cannot drift between “schema provisioning SQL” and “runtime/pack SQL”.
- Tests and manifests should compare **normalized SQL** (or stable hashes of normalized SQL), never “pretty printed” variants.

This is required to support golden-file tests for compiled SQL, stable AOT pack output, and reliable diagnostics.

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
/// <param name="TableModel">The shape model for the table.</param>
/// <param name="InsertSql">Parameterized insert SQL (multi-row insert is handled by IBulkInserter).</param>
/// <param name="UpdateSql">Optional update SQL (for root tables).</param>
/// <param name="DeleteByParentSql">Delete SQL that removes all child rows for a parent key (replace semantics).</param>
/// <param name="ColumnBindings">The ordered list of columns and their write-time value sources.</param>
public sealed record TableWritePlan(
    DbTableModel TableModel,
    string InsertSql,
    string? UpdateSql,
    string? DeleteByParentSql,
    IReadOnlyList<WriteColumnBinding> ColumnBindings
);

/// <summary>
/// Binds a physical column to a write-time value source.
/// </summary>
/// <param name="Column">The column model being written.</param>
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
    /// With the concrete-path approach (section 5.2.1), the referential id is computed by Core and emitted with concrete JSON location.
    /// The backend uses a per-request index keyed by:
    /// - this binding (which identifies the wildcard reference-object path and the FK column)
    /// - the current row's OrdinalPath (array indices from root to the current scope)
    /// to return the referenced DocumentId without per-row hashing.
    /// </summary>
    public sealed record DocumentReference(DocumentReferenceBinding Binding) : WriteValueSource;

    /// <summary>
    /// A descriptor FK value.
    /// The flattener reads the descriptor URI string, normalizes it, and uses (normalizedUri, descriptor resource type)
    /// to look up the descriptor DocumentId in ResolvedReferenceSet.DescriptorIdByKey (populated via dms.ReferentialIdentity resolution).
    /// </summary>
    public sealed record DescriptorReference(DescriptorEdgeSource EdgeSource, JsonPathExpression RelativePath)
        : WriteValueSource;
}

/// <summary>
/// Compiled read/reconstitution plan for a single resource type.
/// </summary>
public sealed record ResourceReadPlan(
    RelationalResourceModel Model,
    IReadOnlyDictionary<DbTableName, TableReadPlan> TablePlans
);

/// <summary>
/// Compiled hydration SQL for a single table based on a materialized keyset table (dialect-specific name).
/// </summary>
/// <param name="SelectByKeysetSql">
/// A SELECT statement that returns all rows needed for the page, by joining to a materialized keyset table
/// containing a BIGINT column named "DocumentId".
/// The reconstituter is responsible for materializing this keyset (temp table / table variable) before running table SELECTs.
/// </param>
public sealed record TableReadPlan(
    DbTableModel TableModel,
    string SelectByKeysetSql            // expects a keyset table with a BIGINT `DocumentId` column
);
```

### 7.5 Reference reconstitution (local columns)

Reference objects are reconstituted from stored reference identity columns on the referencing tables.

The plan compiler uses `RelationalResourceModel.DocumentReferenceBindings` to:
- identify the JSON reference object location, and
- write identity fields from the bound physical columns (`ReferenceIdentityBinding.Column`),
without joining to referenced resource tables.

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
/// Produced by the ApiSchema-derived natural-key resolver (via `dms.ReferentialIdentity`) and used for document references.
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
/// Key idea:
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
    /// Returns the referenced DocumentId for the given reference binding at the given ordinal path, or null if the reference
    /// object is not present at that location (optional reference).
    /// </summary>
    long? GetReferencedDocumentId(DocumentReferenceBinding binding, ReadOnlySpan<int> ordinalPath);
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
    private readonly IReadOnlyDictionary<DocumentReferenceBinding, OrdinalPathMap<long>> _mapByBinding;

    private DocumentReferenceInstanceIndex(
        IReadOnlyDictionary<DocumentReferenceBinding, OrdinalPathMap<long>> mapByBinding
    )
    {
        _mapByBinding = mapByBinding;
    }

    public long? GetReferencedDocumentId(DocumentReferenceBinding binding, ReadOnlySpan<int> ordinalPath)
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
    /// Required Core enhancement:
    /// - each <c>DocumentReference</c> must carry a concrete JSONPath to the reference object instance (including indices),
    ///   e.g. <c>"$.addresses[2].periods[0].calendarReference"</c>.
    /// </summary>
    public static DocumentReferenceInstanceIndex Build(
        IReadOnlyList<DocumentReferenceBinding> bindings,
        EdFi.DataManagementService.Core.External.Model.DocumentReferenceArray[] extractedReferenceArrays,
        IReadOnlyDictionary<Guid, long> documentIdByReferentialId)
    {
        // Map wildcard reference-object path → binding for fast association.
        // The wildcard path is the DocumentReferenceBinding.ReferenceObjectPath (e.g. "$.addresses[*].periods[*].calendarReference").
        var bindingByPath = bindings.ToDictionary(s => s.ReferenceObjectPath.Canonical, s => s);

        var mapsByBinding = new Dictionary<DocumentReferenceBinding, OrdinalPathMap<long>>();

        foreach (var array in extractedReferenceArrays)
        {
            if (!bindingByPath.TryGetValue(array.arrayPath.Value, out var binding))
            {
                // If this happens, ApiSchema and extraction disagree. Treat as a startup/schema error.
                throw new InvalidOperationException(
                    $"No DocumentReferenceBinding found for extracted path '{array.arrayPath.Value}'."
                );
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
    /// A small per-edge-source map from ordinal paths to values that supports allocation-free lookups using ReadOnlySpan.
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
    Task<ReconstitutedPage> ReconstituteAsync(ResourceReadPlan plan, PageKeysetSpec keyset, CancellationToken ct);
}

public abstract record PageKeysetSpec
{
    public sealed record Single(long DocumentId) : PageKeysetSpec;

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

### 7.7 Example: Plan compilation and caching (runtime)

This shows how the shape model and plans are compiled and cached for runtime use when DMS is not loading ahead-of-time mapping packs. In AOT mode, the same compilation work is performed by the pack builder and DMS deserializes the resulting models/plans (see [aot-compilation.md](aot-compilation.md)).

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
- `RelationalPlanCompiler` is where physical names are quoted, parameter styles are applied, batching limits are chosen, and read/write SQL plans are compiled from the derived model.

### 7.8 Example: POST/PUT execution (flatten + write)

Assume `EffectiveSchemaHash` is sourced from the selected database’s `dms.EffectiveSchema` fingerprint (cached per connection string; see `transactions-and-concurrency.md`), and is available via a request-scoped context.

```csharp
public async Task UpsertAsync(IUpsertRequest request, CancellationToken ct)
{
    var effectiveSchemaHash = _effectiveSchemaContext.EffectiveSchemaHash;
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
    //     This depends on Core emitting concrete JSON paths (including indices) for reference instances.
    var documentReferences = DocumentReferenceInstanceIndex.Build(
        writePlan.Model.DocumentReferenceBindings,
        request.DocumentInfo.DocumentReferenceArrays,
        resolved.DocumentIdByReferentialId);

    // 4) Flatten: materialize all table rows in memory with FK columns filled.
    var writeSet = _flattener.Flatten(
        writePlan,
        documentId,
        request.EdfiDoc /* JsonNode */,
        documentReferences,
        resolved);

    // 5) Execute root + child table writes in plan order (set-based).
    await _writer.ExecuteAsync(writePlan, documentId, writeSet, connection, tx, ct);

    // ReferentialId maintenance and update tracking are handled in-transaction by generated database triggers
    // (row-local referential-id recompute + version stamping; identity propagation via ON UPDATE CASCADE).

    await tx.CommitAsync(ct);
}
```

Notes:
- `_referenceResolver.ResolveAsync(...)` resolves document and descriptor references to `DocumentId` via `dms.ReferentialIdentity` (`ReferentialId → DocumentId`) for all identities (self-contained, reference-bearing, and polymorphic/abstract via alias rows), and may validate descriptor existence/type via `dms.Descriptor`.
- `_writer.ExecuteAsync(...)` uses `TableWritePlan.DeleteByParentSql` + `IBulkInserter` to avoid N+1 inserts.

Flattening inner loop sketch (how `TableWritePlan.ColumnBindings` gets used):

```csharp
private static RowBuffer MaterializeRow(
    TableWritePlan tablePlan,
    long documentId,
    JsonNode scopeNode,
    IReadOnlyList<long> parentKeyParts, // e.g. [DocumentId] or [DocumentId, parentOrdinal]
    int ordinal,
    ReadOnlySpan<int> ordinalPath, // e.g. [] (root), [2] (child), [2,0] (nested)
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

            WriteValueSource.DescriptorReference(var edgeSource, var relPath)
                => ResolveDescriptorId(scopeNode, edgeSource, relPath, resolved),

            WriteValueSource.DocumentReference(var binding)
                => ResolveReferencedDocumentId(binding, ordinalPath, documentReferences),

            _ => throw new InvalidOperationException("Unsupported write value source")
        };
    }

    return new(values);
}

private static long ResolveDescriptorId(
    JsonNode scopeNode,
    DescriptorEdgeSource edgeSource,
    JsonPathExpression relPath,
    ResolvedReferenceSet resolved)
{
    var normalizedUri = JsonValueReader.ReadString(scopeNode, relPath).ToLowerInvariant();
    var id = resolved.DescriptorIdByKey[new DescriptorKey(normalizedUri, edgeSource.DescriptorResource)];
    return id;
}

private static long? ResolveReferencedDocumentId(
    DocumentReferenceBinding binding,
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
    var effectiveSchemaHash = _effectiveSchemaContext.EffectiveSchemaHash;
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
    var effectiveSchemaHash = _effectiveSchemaContext.EffectiveSchemaHash;
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
- The query compiler is responsible for emitting stable ordering by the resource root table `DocumentId` (ascending).
- The reconstituter is responsible for:
  1) materializing the `page` keyset from `PageDocumentIdSql`
  2) running all `TableReadPlan.SelectByKeysetSql` statements (multi-resultset)
  3) performing descriptor expansion in batched follow-ups (or as additional result sets)

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

    // descriptor projection follow-ups can either be:
    // - additional result sets in this same command, or
    // - a second command (still batched, page-sized)
    return AssembleJson(plan, documentMetadata /* + tables + lookups */);
}
```
