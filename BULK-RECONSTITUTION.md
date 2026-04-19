# Bulk Reconstitution Design

## Context

The current relational query path already does the expensive database work in bulk:

- page `DocumentId` selection is batched,
- root and child table hydration is batched,
- descriptor URI projection is batched.

That matches the intent in:

- `reference/design/backend-redesign/design-docs/summary.md`
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/00-hydrate-multiresult.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md`

The remaining mismatch is in reconstitution. Query reads still do page-wide setup once per document:

- `RelationalDocumentStoreRepository.BuildQuerySuccess()` loops `hydratedPage.DocumentMetadata` and calls `_readMaterializer.Materialize(...)` once per document.
- `RelationalReadMaterializer.Materialize()` rebuilds the descriptor lookup once per document.
- `DocumentReconstituter.Reconstitute()` creates a fresh `ReconstitutionContext` once per document.
- `DocumentReconstituter` also repeatedly:
  - scans root rows to find the requested document,
  - rediscovers immediate child tables from `JsonScope`,
  - filters reference projection plans by table,
  - filters descriptor sources by table,
  - rebuilds the property-order tree,
  - rebuilds child-row indexes per table.

That is functionally correct, but it is not the page-based reconstitution shape described by the redesign docs. Hydration is bulk; reconstitution setup is still effectively N times page setup.

## Goals

- Keep the existing page-keyset and multi-result hydration contracts.
- Build reconstitution state once per hydrated page.
- Reuse compiled plan metadata instead of re-deriving it from model objects during every document walk.
- Fix the query-path regression in `RelationalDocumentStoreRepository.BuildQuerySuccess()` by making query-page materialization page-scoped instead of per-document page setup.
- Preserve current JSON semantics, including `_ext`, descriptor emission, readable-profile projection, `_etag`, and `_lastModifiedDate`.

## Non-Goals

- No query compiler redesign.
- No authorization redesign.
- No descriptor endpoint redesign.
- No switch to streaming JSON in the first implementation.
- No required-array behavior change.
- No GET-by-id or write-path unification as part of this story-sized fix.

## Design Summary

Introduce two reconstitution layers:

1. `CompiledReconstitutionPlan`
   - built once per `ResourceReadPlan`
   - cached alongside the already cached `ResourceReadPlan`
   - contains table-local emission metadata and table relationships that do not depend on hydrated rows

2. `PageReconstitutionContext`
   - built once per `HydratedPage`
   - contains the row graph, descriptor lookup, and page ordering for one hydrated query page

Per-document materialization then becomes a pure emit pass over an already attached row graph.

The key shift is that the page builder attaches child rows once, in hydration result order, instead of asking each document materialization call to rediscover and re-index the same rowsets.

## Why This Fits the Existing Design

This follows the redesign docs directly:

- `flattening-reconstitution.md` says query reconstitution must be page-based, not “GET by id repeated N times”.
- The same document says hydration result sets are ordered by root scope, immediate parent scope, and `Ordinal`.
- The reconstitution sketch in that doc is already a row-graph algorithm:
  - read root rows,
  - attach child rows by stable parent identity,
  - write JSON.

The current implementation already has the metadata needed to do this properly:

- `DbTableIdentityMetadata`
  - `PhysicalRowIdentityColumns`
  - `RootScopeLocatorColumns`
  - `ImmediateParentScopeLocatorColumns`
- `ReferenceIdentityProjectionTablePlan`
- `DescriptorProjectionPlan` / `DescriptorProjectionSource`
- `HydratedPage.DocumentMetadata`
- deterministic table dependency order in `ResourceReadPlan.TablePlansInDependencyOrder`

The design should use those compiled contracts, not rebuild equivalent knowledge from `JsonScope` and column-name scans during each document emit.

## Proposed Types

### Plan-Scoped Cache

```csharp
internal readonly record struct ScopeKey
{
    public ScopeKey(IEnumerable<object?> parts)
    {
        Parts = [.. parts];
    }

    public ImmutableArray<object?> Parts { get; }

    public bool Equals(ScopeKey other) =>
        Parts.SequenceEqual(other.Parts);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var part in Parts)
        {
            hash.Add(part);
        }

        return hash.ToHashCode();
    }
}

internal sealed record CompiledReconstitutionPlan(
    PropertyOrderNode PropertyOrderTree,
    TableReconstitutionPlan RootTable,
    IReadOnlyList<TableReconstitutionPlan> TablesInDependencyOrder,
    IReadOnlyDictionary<DbTableName, TableReconstitutionPlan> TablesByName
);

internal sealed record TableReconstitutionPlan(
    DbTableModel TableModel,
    DbTableKind TableKind,
    DbTableName? ImmediateParentTable,
    IReadOnlyList<int> RootScopeLocatorOrdinals,
    IReadOnlyList<int> ImmediateParentLocatorOrdinals,
    IReadOnlyList<int> PhysicalRowIdentityOrdinals,
    int? OrdinalOrdinal,
    IReadOnlyList<DbTableName> ImmediateChildrenInDependencyOrder,
    IReadOnlyList<ReferenceIdentityProjectionBinding> ReferenceBindingsInOrder,
    IReadOnlyList<DescriptorProjectionSource> DescriptorSourcesInOrder
);
```

### Page-Scoped Context

```csharp
internal sealed record PageReconstitutionContext(
    CompiledReconstitutionPlan Plan,
    IReadOnlyList<DocumentPageNode> DocumentsInOrder,
    IReadOnlyDictionary<long, DocumentPageNode> DocumentsById,
    IReadOnlyDictionary<long, string> DescriptorUriById
);

internal sealed record DocumentPageNode(
    DocumentMetadataRow Metadata,
    RowNode Root
);

internal sealed record RowNode(
    TableReconstitutionPlan TablePlan,
    object?[] Row,
    long DocumentId,
    ScopeKey PhysicalIdentity,
    IReadOnlyDictionary<DbTableName, IReadOnlyList<RowNode>> ChildrenByTable
);
```

`RowNode` holds a reference to the hydrated row buffer; it does not copy row values into a second page-sized structure.

`ScopeKey` is intentionally composite-ready even though the first implementation still validates single-column locators and identities. That keeps the runtime key shape aligned with the list-based identity metadata already present in `DbTableIdentityMetadata`.

`ScopeKey` must use structural equality over its parts because the page graph uses it as a real dictionary key for:

- `rowsByPhysicalIdentityByTable`
- immediate-parent lookup during child attachment

Reference equality on the backing collection is not acceptable here. The page builder should also normalize locator/identity values before constructing a `ScopeKey` so logically equal keys do not diverge because of CLR type differences from hydration.

## Important Design Choices

### 1. Cache Reconstitution Metadata Per Read Plan

The compiled cache should own everything that is currently recomputed from static plan/model data:

- table-local reference bindings, grouped by table
- table-local descriptor bindings, grouped by table
- immediate parent table for each table
- immediate children in dependency order
- root-scope locator ordinal
- immediate-parent locator ordinal
- physical-row-identity ordinal
- ordinal column ordinal
- deterministic property-order tree

That removes repeated work from:

- `BuildPropertyOrderTree(...)`
- `FindImmediateChildTables(...)`
- `FindColumnOrdinalByName(...)` for descriptor FK columns
- per-call scans over all reference/descriptor plans to find the ones for a table

The descriptor part should use `ResourceReadPlan.DescriptorProjectionPlansInOrder` and their `DescriptorProjectionSource.DescriptorIdColumnOrdinal`, not `Model.DescriptorEdgeSources`. The compiled ordinal contract already exists and should be the hot-path source of truth.

Even if the first implementation only supports single-column locators and identities, the compiled cache shape should still preserve the underlying list-based model contracts:

- `DbTableIdentityMetadata.PhysicalRowIdentityColumns`
- `DbTableIdentityMetadata.RootScopeLocatorColumns`
- `DbTableIdentityMetadata.ImmediateParentScopeLocatorColumns`

That keeps the cache forward-compatible with any future composite locator or identity shape. The first implementation can validate `Count == 1` while building the cache and fail fast if a table exceeds that temporary limit, while still emitting normalized `ScopeKey` values from the validated ordinal lists.

### 2. Build Page State Once

The page context should own only page-derived state:

- `DescriptorUriById`
- root rows by `DocumentId`
- all `RowNode`s for the hydrated page
- the attached parent/child row graph
- page output order aligned to `HydratedPage.DocumentMetadata`

This means query pages pay:

- one hydration
- one page-context build
- one emit pass per document

instead of:

- one hydration
- N descriptor lookups
- N reconstitution contexts
- N root-row scans
- N child-row index rebuilds

### 3. Attach Children Directly in Result-Set Order

The hydration contract already guarantees deterministic row ordering by:

- root document scope
- immediate parent scope
- `Ordinal` where applicable

That means the page builder does not need to do the full current per-document regroup-and-sort work. It can:

1. read rows in the hydrated order,
2. create a `RowNode` for each row,
3. resolve its immediate parent once,
4. append the row to the parent’s child list.

However, the design should not rely on SQL ordering as an implicit assumption only. The current implementation still sorts child rows by `Ordinal` inside `ChildRowIndex.Create(...)`, and `TableReadPlan` does not currently expose ordering as a typed contract. To keep correctness robust, the bulk design should do one of these:

- preserve a single page-build-time defensive sort or monotonicity check per `(parent, child table)` list, or
- promote row-order expectations into a compiled/validated read-plan contract and fail fast if hydration violates them.

The key requirement is: ordering validation/sorting must happen once per page, not once per document.

## Page Context Build Algorithm

### Phase 1: Get or Build `CompiledReconstitutionPlan`

Build once per `ResourceReadPlan`:

- resolve ordinals from `DbTableIdentityMetadata`
- validate exactly one root-scope locator column per table
- validate exactly one physical-row-identity column per table
- validate zero-or-one immediate-parent locator column depending on table kind
- derive `ImmediateParentTable`
- derive `ImmediateChildrenInDependencyOrder`
- group `ReferenceIdentityProjectionBinding`s by table
- flatten `DescriptorProjectionPlansInOrder[*].SourcesInOrder` into table-local descriptor bindings
- build the property-order tree once

For the first implementation it is acceptable to require exactly one physical-row identity column and one relevant locator column per table, and fail fast otherwise. That temporary restriction should be implemented as validation during cache construction, not baked into the long-term cache shape. It matches the current read shapes:

- root: `DocumentId`
- collection rows: `CollectionItemId`
- collection-aligned extension scopes: `BaseCollectionItemId`

### Immediate Parent Derivation

`ImmediateParentTable` is the key correctness invariant for page-level child attachment. It should be derived once from the same scope/topology rules the current `DocumentReconstituter.FindImmediateChildTables(...)` logic uses, then validated for every non-root table.

Required rules:

- root collections attach to the root table
- nested collections attach to the nearest ancestor collection/common-type table whose scope is exactly one array level shallower
- root extension scopes attach to the root table
- collection extension scopes attach to the base collection/common-type table at the same array depth
- extension child collections do not attach directly to the base scope when their relative path begins with `_ext.{project}`; they attach to the aligned extension scope table that owns that `_ext` site

This is especially important for `_ext` paths. A base scope like `$.addresses[*]` must not treat `$.addresses[*]._ext.sample.services[*]` as its direct child. The immediate parent is the aligned extension scope row at `$.addresses[*]._ext.sample`, not the base address row.

The cache builder should validate:

- every non-root table resolves to exactly one immediate parent table
- the parent appears earlier in dependency order
- the parent table kind and child table kind form one of the allowed attachment pairs

If any table fails that derivation, cache construction should fail fast.

### Phase 2: Build Descriptor Lookup Once

Build one `Dictionary<long, string>` from `HydratedPage.DescriptorRowsInPlanOrder`.

This replaces the current per-document descriptor lookup rebuild in `RelationalReadMaterializer`.

### Phase 3: Create Root Nodes

For the root table:

- create one `RowNode` per hydrated root row
- resolve `DocumentId` from the validated root-scope locator
- build `PhysicalIdentity` as a normalized `ScopeKey` from `PhysicalRowIdentityOrdinals`
- validate there is exactly one root row per `DocumentId`
- retain both:
  - `rootByDocumentId`
  - `rowsByPhysicalIdentityByTable[rootTable]`, keyed by `ScopeKey`

### Phase 4: Attach Non-Root Rows Once

For each non-root table in dependency order:

1. create a `RowNode` for each hydrated row
2. resolve:
   - `DocumentId`
   - `PhysicalIdentity` as a normalized `ScopeKey`
   - immediate parent key as a normalized `ScopeKey`
3. add the row to `rowsByPhysicalIdentityByTable[currentTable]`
4. resolve the parent row once:
   - look up the table’s `ImmediateParentTable`
   - use the immediate parent `ScopeKey` to find the parent `RowNode`
5. append the child node to the parent’s `ChildrenByTable[childTable]`
6. preserve array order by either:
   - validating monotonic hydration order for each attached sibling list, or
   - doing one page-build-time sort by `Ordinal` when the table has an ordinal column

Attachment rules come from the existing table kinds and identity metadata:

- top-level collections attach to the root row via `DocumentId`
- nested collections attach via `ParentCollectionItemId`
- root extension scopes attach to the root row via `DocumentId`
- collection extension scopes attach to the base collection row via `BaseCollectionItemId`
- extension child collections attach to the aligned extension scope row via `BaseCollectionItemId` or the extension collection parent key, depending on the compiled table metadata

The parent lookup must be table-qualified, not locator-only. `BaseCollectionItemId=42` in one table is not globally unique across all possible parent tables.

In the first implementation, both `PhysicalIdentity` and the immediate parent key will be `ScopeKey` values containing exactly one part because cache construction validates the current single-column restriction. The important point is that the page graph no longer hard-codes a scalar key type.

### Phase 5: Build `DocumentsInOrder`

Iterate `HydratedPage.DocumentMetadata` in order and resolve each metadata row to a root node:

- if a metadata row has no root row, fail fast
- if a root row exists for a document missing from metadata, fail fast

The output page order remains the query page order from hydration metadata.

## Materialization Flow

After page preparation, per-document materialization becomes:

1. resolve `DocumentPageNode` by `DocumentId`
2. emit the root row
3. recurse through attached children in compiled dependency order
4. use `DescriptorUriById` for descriptor strings
5. use table-local reference bindings for reference objects
6. reorder using the cached `PropertyOrderTree`
7. inject API metadata when the read mode is `ExternalResponse`

Nothing in this flow should rebuild page indexes.

## Proposed API Changes

Add a query-page materialization entry point without turning this design into a broader read-path unification project.

```csharp
public sealed record RelationalReadPageMaterializationRequest(
    ResourceReadPlan ReadPlan,
    HydratedPage HydratedPage,
    RelationalGetRequestReadMode ReadMode
);

public sealed record MaterializedDocument(
    DocumentMetadataRow Metadata,
    JsonNode Document
);

public interface IRelationalReadMaterializer
{
    IReadOnlyList<MaterializedDocument> MaterializePage(
        RelationalReadPageMaterializationRequest request
    );

    JsonNode Materialize(RelationalReadMaterializationRequest request);
}
```

This is intentionally not a migration plan. The goal is to fix query-page reconstitution directly while leaving the current single-document materialization API in place for non-query callers that do not have the page-level performance problem under review.

That keeps scope aligned to the story and review comment:

- query uses `MaterializePage(...)`
- existing single-document callers continue using `Materialize(...)`

## Changes to `DocumentReconstituter`

The hot path should stop taking raw page rowsets and rediscovering page structure per document.

Preferred direction:

```csharp
internal static JsonNode ReconstituteDocument(
    DocumentPageNode document,
    PageReconstitutionContext context
);
```

The query path should call this page-based reconstitution flow. The existing single-document entry point can remain for GET-by-id and write-side callers because those paths are out of scope for this design and are not the source of the query-page regression.

## Repository and Caller Changes

### Query

`RelationalDocumentStoreRepository.BuildQuerySuccess()` should:

- call `_readMaterializer.MaterializePage(...)` once per hydrated page
- apply readable-profile projection per returned document
- refresh `_etag` after readable-profile projection

It should stop looping `DocumentMetadata` and calling `_readMaterializer.Materialize(...)` per item.

### Out of Scope Callers

This design does not require changing:

- GET-by-id materialization
- committed write readback
- stored-state projection during profile-aware writes

Those callers can be unified later if that becomes desirable, but that is not required to fix the query-path performance issue described in the review.

## Complexity and Expected Gain

### Current Cost Shape

For a hydrated page with `N` documents and `R` total hydrated rows, the current shape is roughly:

- `O(hydration)`
- `O(N * descriptor lookup build)`
- `O(N * root row scan)`
- `O(N * child-row index build)`
- `O(N * repeated plan-derived filtering work)`
- `O(document emit)`

### Target Cost Shape

The target is:

- `O(hydration)`
- `O(descriptor lookup build once)`
- `O(page row graph build once)`
- `O(page ordering validation/sort once, if retained defensively)`
- `O(document emit)`

The important change is moving setup cost from `O(N * page work)` to `O(page work once)`.

## Validation and Failure Modes

The page builder should fail fast for defects the current per-document path may hide:

- duplicate root rows for one `DocumentId`
- duplicate physical row identity within a table
- missing parent row for a child row
- metadata row without a root row
- table identity metadata that does not match the assumptions required by the page builder

These are compilation or hydration contract problems, not normal runtime variance.

## Testing Plan

### Unit Tests

- compiled reconstitution plan groups reference bindings by table once
- compiled reconstitution plan groups descriptor bindings by table using descriptor projection ordinals
- compiled reconstitution plan preserves list-based locator/identity metadata even when first-pass validation requires single-column use
- page context uses composite-ready `ScopeKey` values for physical identity and parent attachment even when first-pass validation limits those keys to one part
- compiled reconstitution plan derives exactly one immediate parent table for every non-root table
- `_ext` child-parent derivation rejects direct base-scope attachment for paths that must attach through an aligned extension scope
- page context builds one descriptor lookup for the page
- page context attaches:
  - top-level collections
  - nested collections
  - root extension scopes
  - collection extension scopes
  - extension child collections
- child order is preserved either by one page-build-time validation step or one page-build-time sort, never by per-document regrouping
- duplicate root rows fail fast
- duplicate physical row identities fail fast
- orphaned child rows fail fast
- emitted JSON remains equivalent to current output for representative nested-plus-`_ext` fixtures

### Repository Tests

- query path calls `MaterializePage(...)` once per page
- query path still applies readable-profile projection per item and refreshes `_etag`

### Integration Tests

- existing relational query execution tests keep passing
- recorder-based query tests should switch from “materialized document ids” to “page materialization call count + returned ids”
- add at least one multi-document page test large enough to catch accidental per-document page setup

## Recommendation

Implement a page-first reconstitution pipeline with:

- a plan-scoped `CompiledReconstitutionPlan`
- a page-scoped `PageReconstitutionContext`
- a page-first `IRelationalReadMaterializer.MaterializePage(...)`
- direct cutover of the query path to that API in the same change

That is the smallest change that actually fixes the review point:

- hydration stays bulk,
- reconstitution setup becomes bulk too,
- the query path finally matches the redesign’s intended page-based reconstitution model,
- and the change stays scoped to the story and regression actually under review.
