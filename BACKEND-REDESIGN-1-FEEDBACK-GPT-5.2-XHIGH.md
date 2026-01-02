# BACKEND-REDESIGN-1 Feedback (GPT-5.2, XHIGH)

## Scope & Sources Reviewed

Primary focus: the current **three-table backend** (`dms.Document` / `dms.Alias` / `dms.Reference`) and the draft redesign at `BACKEND-REDESIGN-1.md`, **excluding authorization** storage (tables/columns/triggers).

Reviewed:
- Draft: `BACKEND-REDESIGN-1.md`
- Current DB scripts (non-auth):  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql`  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0002_Create_Alias_Table.sql`  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql`  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql`  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0015_Create_Reference_Validation_FKs.sql`  
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0023_Create_DataOut_Indexes.sql`
- Design references:
  - `reference/design/flattening-metadata-design.md`
  - `reference/design/document-query-indexing-design.md`
- DMS Core entrypoint/pipelines (for boundary assumptions): `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- API schema semantics and extraction behavior:
  - `docs/API-SCHEMA-DOCUMENTATION.md`
  - `src/dms/core/EdFi.DataManagementService.Core/Extraction/IdentityExtractor.cs`
  - `src/dms/core/EdFi.DataManagementService.Core/Extraction/ReferenceExtractor.cs`
  - `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs`

## Quick recap: current three-table design (non-auth)

What it does well:
- **Shape independence**: one `dms.Document` JSONB store for all resources avoids per-resource DDL churn.
- **Idempotent upsert**: `ReferentialId` (UUIDv5 hash of natural keys) → `dms.Alias` enforces uniqueness and finds existing documents.
- **Referential integrity & delete protection**: `dms.Reference` + optional FK to `dms.Alias` prevents deleting referenced documents.

What drives the redesign:
- **Query cost & indexing pressure**: filtering over `EdfiDoc JSONB` requires wide GIN indexes (`IX_Document_EdfiDoc`) or helper projections (see `reference/design/document-query-indexing-design.md`).
- **Operational complexity from partitioning**: current Postgres partitioning by `PartitionKeyFor(Guid) = lastByte % 16` forces composite keys and more complex joins.
- **Cascade rewrite burden**: identity changes require rewriting referencing documents (and their references) in a document store model.

## What the draft gets right / strong parts

1. **Clear boundary**: Core stays JSON-centric and metadata-driven; backend owns flattening/reconstitution and DB concerns.
2. **Tables-per-resource**: root table per resource + child tables per collection is the right “traditional relational” move for queries, integrity, and integration.
3. **Descriptor strategy**: keeping descriptors as documents but maintaining a unified `dms.Descriptor` table aligns with ODS/API patterns and enables FK enforcement.
4. **Derive mapping from existing ApiSchema**: leaning on `jsonSchemaForInsert` + `documentPathsMapping` + `identityJsonPaths` avoids duplicating a huge “flattening metadata” block.
5. **Batch-oriented execution**: explicit “no N+1” write/read strategies and multi-resultset hydration are correct for performance.
6. **Schema fingerprinting**: `dms.EffectiveSchema` + deterministic hash is a good foundation for “migrate + restart” operation and fail-fast mismatches.

## Highest-priority gaps / design risks

### 1) “Eliminate reference-cascade updates” is only partially solved

Storing references as `..._DocumentId` FKs *does* remove the need to rewrite **reference values** in referencing documents when a referenced natural key changes.

However, there is a second cascade problem the draft does not address:

- **Many Ed-Fi resource identities (especially associations) include reference identity components** (e.g., `$.studentReference.studentUniqueId`, `$.schoolReference.schoolId`, etc.).
- Core computes a resource’s `ReferentialId` directly from the **raw identityJsonPaths values** (`IdentityExtractor.ExtractDocumentIdentity`), which includes those reference identity values when they’re part of identity.
- If a referenced resource’s identity changes (e.g., `StudentUniqueId` changes), then the *conceptual* identity of any resource whose identity includes that value changes too.

With relational storage, the referencing row can keep pointing to the same referenced `DocumentId`, and reconstitution can output the *new* identity values. But then:
- The `dms.ReferentialIdentity` mapping for the referencing resource (used for POST upsert detection and reference resolution) becomes stale unless it is updated.
- If you also enable `dms.DocumentCache`, cached JSON becomes stale for every referencing document whose reference identity values “should” change **unless** you invalidate dependent caches; the updated draft proposes optional `dms.ReferenceEdge` + `BindingKey` to drive this cache-invalidation cascade.

This is the core tension:
- **Eliminate cascades** vs
- **Keep upsert-by-natural-key semantics consistent after identity changes**.

Concrete failure mode if unaddressed:
- StudentUniqueId changes `A → B`
- Existing `StudentSchoolAssociation` row still points to Student `DocumentId`
- Reconstituted JSON now shows `studentUniqueId=B`
- A client POSTs the same SSA with `studentUniqueId=B`; Core computes a new SSA `ReferentialId` based on `B`
- Backend looks up SSA by referential id (draft’s plan) and doesn’t find it → attempts insert → hits unique constraint or creates a logical duplicate depending on constraints → wrong semantics.

Recommended: explicitly choose one strategy (or a bounded combination):

**Option A (strict/no-cascade, most “relational”)**  
Keep references as `DocumentId` and make POST upsert detection for identities that contain references be **DocumentId-based**, not `ReferentialId`-based:
- Resolve referenced natural keys → referenced `DocumentId`s first.
- Detect existing row by the resource’s *relational* unique constraint (e.g., `Student_DocumentId + School_DocumentId + EntryDate`), not by referential-id lookup.
- Still keep `dms.ReferentialIdentity` for resources that are targets of references (entities/descriptors), but treat it as a resolver, not as the universal upsert key.
  - This is compatible with “no rewrites on identity change”.
  - It does require a clear rule: “upsert detection uses `ReferentialId` only when the identity is purely scalar-on-self; otherwise use relational uniqueness after reference resolution.”

**Option B (preserve current semantics, allow identity updates with propagation)**  
Keep the draft’s referential-id-based upsert detection, but when identity changes, perform a **referential-identity propagation** step:
- Find all dependent resources whose identity includes that reference value and recompute/update their `dms.ReferentialIdentity` entries.
- If `dms.DocumentCache` is enabled, invalidate/recompute dependent caches.
This requires a reverse-dependency mechanism (the updated draft adds optional `dms.ReferenceEdge`) or scanning many FK columns.

**Option C (restrict identity updates)**  
Formally restrict `allowIdentityUpdates` to resources whose identities are not used as components of other identities (or disable entirely in relational mode). This is operationally simplest but may be unacceptable if identity updates are a required feature.

The draft currently asserts `UpdateCascadeHandler` becomes unnecessary. That’s only true if you adopt Option A or Option C (or implement Option B explicitly).

### 2) Polymorphic/abstract references need an explicit reconstitution + query plan

The draft proposes:
- For polymorphic references: store FK to `dms.Document(DocumentId)` and validate membership in application code (or via membership tables/triggers).

What’s missing is the **read path** and **identity projection** for abstract targets:
- `ReferenceBinding.TargetResource` can be an abstract resource type (e.g., `EducationOrganization`) that has **no physical root table**.
- Reconstituting a reference object requires the target’s identity fields with the correct names (e.g., `educationOrganizationId`, not `schoolId`).

You need one of:

**Option A (membership table, recommended for generic implementation)**  
For each abstract resource `A`, create `schema.A_Membership`:
- `DocumentId bigint primary key references dms.Document(DocumentId)`
- columns for the abstract identity fields (typed)
- optional discriminator columns
Maintain it via triggers on each concrete resource table or via write-path application logic.
Then identity projection for abstract type is a simple `SELECT ... FROM A_Membership WHERE DocumentId IN (...)`.

**Option B (union view, acceptable for small hierarchies)**  
Create `schema.A_View` as a union over all concrete tables projecting the abstract identity fields (and a discriminator).
This mirrors `reference/design/flattening-metadata-design.md` and is probably fine for EducationOrganization-scale row counts, but may be risky for large polymorphic sets.

**Option C (runtime CASE/JOIN fanout)**  
Compile a dialect-specific identity projection query that joins `dms.Document` to each possible concrete table and selects identity fields via CASE/COALESCE.
This is workable but more complex and may degrade with large hierarchies.

The redesign already inserts superclass/abstract aliases in `dms.ReferentialIdentity`, which is a strong foundation for **write-time resolution** and **query-time filtering** (resolve abstract referential id → DocumentId, then filter on FK).
But you still need one of the options above to output reference identity values efficiently and correctly.

### 3) Query compilation must support child tables (or explicitly constrain `queryFieldMapping`)

`ValidateQueryMiddleware` supports query fields mapped to *arbitrary JSON paths* (including those with `[*]`), and `queryFieldMapping` is the source of truth for what is queryable.

In a relational model:
- A query field whose JSONPath crosses an array boundary must compile to `EXISTS (...)` / JOIN predicates against the corresponding child table(s).
- A query field mapped to multiple JSON paths may require OR within a field (the draft notes this).

If you do not support child-table predicates, you must enforce a schema rule:
- **Constraint**: `queryFieldMapping` must only contain paths that map to root-table columns.
- Enforce at ApiSchema generation time and/or at DMS startup model compilation (fail fast with a clear error listing offending query fields and paths).

Given Ed-Fi query patterns are often root-level, restricting might be acceptable, but it needs to be an explicit contract either way.

### 4) Derived mapping must define a supported JSON Schema subset and a normalization pass

The draft’s derivation algorithm sketches arrays/objects/scalars + `$ref`. It needs to be more explicit about:
- `allOf` composition (common in OpenAPI/JSON Schema outputs)
- `oneOf`/`anyOf` (likely unsupported; must fail fast)
- nullable types / `type: ["null", ...]`
- `additionalProperties` (the draft mentions failing for “uncontrolled additionalProperties” but the supported rule should be explicit)
- how required-ness is computed for columns and arrays

Recommendation:
- Add an explicit “schema normalization” phase in `RelationalResourceModelBuilder`:
  - resolve `$ref`
  - collapse `allOf` into a single object schema (merge properties/required)
  - reject `oneOf`/`anyOf` unless a bounded, deterministic strategy is defined
  - enforce `additionalProperties=false` (or define “bag column” behavior if true)

### 5) Document references inside nested collections: Core change vs backend-only strategy

The draft proposes enhancing Core so each `DocumentReference` carries a concrete JSONPath with indices, enabling the `(binding + ordinalPath) → DocumentId` index without per-row hashing.

Today:
- `DescriptorReference` already includes `Path` (with indices).
- `DocumentReference` does **not** include a path; `DocumentReferenceArray.arrayPath` is wildcard-only and `DocumentReference[]` is position-only.

You have two viable approaches:

**Option A (draft’s approach)**: add `JsonPath Path` to `DocumentReference` and emit concrete paths during extraction.
- Symmetry with `DescriptorReference` is nice.
- Enables O(1) lookup by ordinal path.
- But it breaks the “Core stays intact” claim and requires updating Core.External models and extractors.

**Option B (backend-only, no Core changes)**: build the mapping by walking the JSON during flattening:
- As you enumerate arrays to materialize rows, detect whether the reference object exists at each ordinal path.
- Consume `DocumentReferenceArray.DocumentReferences` in the same traversal order (skipping missing references), and map to ordinal paths.
- This avoids Core changes but requires careful consistency with `ReferenceExtractor` ordering rules.

Either is acceptable; the design doc should pick one, because it impacts API contracts between Core and backends.

Also note a concrete inconsistency in the draft example code:
- `DocumentReferenceInstanceIndex.Build(...)` expects `DocumentReferenceArray[]`, but the example call passes `request.DocumentInfo.DocumentReferences` (which is a flat `DocumentReference[]` today).

## Medium-priority clarifications / improvements

### 6) Define the ordering/paging contract explicitly

Current behavior appears to rely on `CreatedAt` ordering/indexing in places (see existing `IX_Document_ResourceName_CreatedAt` and the query indexing design reference).
The draft sometimes uses `ORDER BY DocumentId`.

You should decide and document:
- default ordering for GET queries (CreatedAt? LastModifiedAt? DocumentUuid? DocumentId?)
- whether ordering must be stable across migrations/restores
- the indexes required to make OFFSET/LIMIT tolerable

If OFFSET/LIMIT must remain (it does per Ed-Fi API), consider:
- using CreatedAt/DocumentId composite ordering with a supporting index
- keeping the “narrow paging helper” idea (DocumentIndex-like) if offsets can get large

### 7) Wide-table pressure: inlining objects can exceed SQL Server limits

Inlining every non-array object into the current table can produce very wide root tables:
- SQL Server max columns/table (1024) and row-size constraints can be hit, especially with large extension objects.

The draft already suggests 1:1 extension tables for `isResourceExtension=true`. But you may also need:
- automatic “vertical split” into 1:1 tables when a table exceeds thresholds (column count/row width)
- optional ApiSchema hints to force splitting for specific object paths

### 8) `IdentityRole` needs a real definition

`dms.ReferentialIdentity.IdentityRole` is required and uniqueness-enforced per `(DocumentId, IdentityRole)`, but:
- the role enum isn’t defined
- multi-level abstract inheritance may require inserting multiple ancestor referential IDs (not just one superclass)

Recommendation:
- define an `IdentityRole` enum in design (Primary, Superclass1, Superclass2, … OR (RoleResource) instead of a smallint)
- specify how to compute the full ancestor chain from ApiSchema (subclass metadata + `abstractResources`)
- explicitly describe how many referential identities are inserted for a document and why

### 9) Don’t accidentally store reference-object internals as scalar columns

Derived mapping from JSON schema will “see” the identity fields inside a reference object (and `link` objects).
But relational storage wants:
- a single FK column for the reference, not duplicated scalar columns for its identity fields.

Recommendation:
- make suppression rules explicit in the derivation algorithm:
  - if `documentPathsMapping` marks a path as a document reference object, suppress scalar columns for all descendants of that object
  - if a path is a descriptor reference, suppress the string scalar column and store only `..._DescriptorId`

### 10) `dms.DocumentCache` correctness depends on identity-change invalidation

If you cache full JSON with expanded reference identities, then identity updates in referenced resources imply cache invalidation across the FK graph.

Update: the draft now introduces optional `dms.ReferenceEdge` (with stable `BindingKey`) and shows a concrete dependent-invalidation mechanism (reverse lookup by `ChildDocumentId` → delete dependent `dms.DocumentCache` rows). This directly addresses the cache-correctness gap *if* `dms.ReferenceEdge` is enabled and maintained reliably.

Remaining considerations to make the solution “production real”:
- Make `dms.ReferenceEdge` truly optional (enable only when `dms.DocumentCache` strictness and/or delete diagnostics require it).
- Use low-churn maintenance (stage+diff) so no-op updates don’t rewrite edges.
- Decide on strict vs best-effort behavior: if edge maintenance fails, does the write fail, or do you accept temporary cache staleness (TTL fallback / async invalidation)?

## Suggested additions/adjustments to ApiSchema (minimal, non-codegen)

The draft’s `resourceSchema.relational` block (name overrides) is a good start. Consider adding/clarifying:

1. **Supported JSON Schema subset contract**
   - Either encode as an `apiSchemaVersion` requirement or as explicit validation rules in DMS.
   - If you add a normalization pass, document what it supports and what fails.

2. **Optional split hints**
   - A small hint to force a 1:1 split for a specific object path can prevent SQL Server limit issues without enumerating all columns:
     - e.g., `relational.splitObjects: { "$._ext.tpdm": "TpdmExtension" }` (exact shape TBD).

3. **Polymorphic reference support**
   - If you adopt membership tables, you likely don’t need new metadata: subclass relationships + `abstractResources` are enough.
   - But you may want an explicit list of which abstract types require membership materialization for performance/diagnostics.

## Design update: `dms.ReferenceEdge` is now in the draft

The updated draft incorporates an optional `dms.ReferenceEdge` table (keyed by `(ParentDocumentId, BindingKey, ChildDocumentId)`) and uses it for:
- `dms.DocumentCache` dependent invalidation (cache “cascade” without rewriting relational data)
- delete conflict diagnostics (“who references me?”) without scanning all resource tables

This is a good direction and is materially different from today’s `dms.Reference` in ways that address the churn concern:
- It stores **resolved `DocumentId`s** (no alias join/partition key routing).
- It’s **optional**, so high-write deployments can disable it unless they need strict cache correctness/diagnostics.
- It can be maintained with a **diff** so unchanged reference sets do not rewrite rows.

Two things to keep explicit in the design (to avoid reintroducing old pain):
- `BindingKey` must be stable and meaningful (distinct reference sites, deterministic across engines, with clear truncation rules).
- Cache invalidation workload can be large; consider async invalidation (outbox/worker) + TTL safety net if strict synchronous invalidation is too expensive.

## Suggested validation plan (to de-risk early)

1. **Round-trip correctness**
   - Nested arrays (2+ levels) with optional elements
   - Descriptor references (including in nested arrays)
   - Reference objects (including optional references in nested arrays)
   - Resource extensions (`_ext`) and descriptor resources

2. **Query correctness**
   - Root-table-only query fields
   - (If supported) query fields that hit child tables (`EXISTS` behavior)
   - Reference-based query fields requiring referential-id resolution
   - Polymorphic reference query fields (abstract referential id resolution)

3. **Identity update scenarios (pick at least one if identity updates remain supported)**
   - Update an entity’s natural key and confirm:
     - GET responses show updated identity in reference objects
     - POST upsert of dependent resources does not create duplicates / does not incorrectly 409
     - (If cache enabled) cached results remain correct or are explicitly invalidated (ideally via `dms.ReferenceEdge`), and no-op updates do not churn the edge table when diff-based maintenance is used

4. **DDL parity checks**
   - SQL Server: table width/column count constraints on “wide” resources and extension-heavy resources
   - Identifier length collisions and deterministic truncation behavior

## Key questions to answer before implementation

1. Do we need to support identity updates in relational mode (and for which resources), or can we restrict/disable?
2. Is `queryFieldMapping` allowed to reference array paths, and if yes, which query semantics are required (exists-only, equals-only)?
3. How should polymorphic reference identity projection be implemented (membership table vs union view vs CASE fanout)?
4. What is the explicit supported JSON schema subset for `jsonSchemaForInsert` in relational mode?
5. If `dms.DocumentCache` is enabled, is `dms.ReferenceEdge` required for correctness (strict invalidation) or is TTL-only staleness acceptable? Should invalidation be synchronous or async?
