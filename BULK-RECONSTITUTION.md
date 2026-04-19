# Bulk Reconstitution Design

## Context

The relational query path already hydrates a selected page in bulk:

- page `DocumentId` selection is batched,
- root and child table hydration is batched,
- descriptor projection rows are batched.

That part matches the redesign intent in:

- `reference/design/backend-redesign/design-docs/summary.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/00-hydrate-multiresult.md`
- `reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md`

The remaining problem is reconstitution. In the current implementation, query reads still perform page-wide setup repeatedly for each document in the page:

- `RelationalDocumentStoreRepository.BuildQuerySuccess()` loops documents and calls `IRelationalReadMaterializer.Materialize()` once per document.
- `RelationalReadMaterializer.Materialize()` rebuilds the descriptor URI lookup for every document.
- `DocumentReconstituter.Reconstitute()` creates a fresh `ReconstitutionContext` for every document.
- that context rebuilds child-row indexes per table for every document.
- root row lookup, immediate-child table discovery, reference-plan filtering, descriptor-source filtering, and property-order tree construction are all repeated more than they need to be.

This does not create N extra database calls, but it does recreate page-level in-memory indexes N times, where N is the number of documents in the page. That gives back part of the win the design intended from page-based reconstitution.

## Goals

- Preserve the existing single-batch database hydration model.
- Build page-level reconstitution state once per hydrated page.
- Reuse that state for every document in the page.
- Keep JSON output semantics unchanged.
- Keep GET-by-id and query on the same materialization path.
- Make the common query-page cost approximately:
  - one page hydration,
  - one page reconstitution-context build,
  - one per-document JSON emit pass.

## Non-Goals

- No authorization redesign.
- No query compiler redesign.
- No descriptor endpoint redesign.
- No required-array behavior change.
- No streaming JSON refactor in the first implementation.

## Problem Statement

Today the query read path is effectively:

1. Hydrate one page of rows.
2. For each document in that page:
   - rebuild descriptor lookup from the same descriptor rows,
   - scan root rows to find the document root row,
   - rebuild child-row indexes from the same hydrated child rows,
   - rediscover immediate child tables,
   - filter reference projection plans for the current table,
   - filter descriptor sources for the current table,
   - rebuild property-order data,
   - emit JSON.

The expensive part is not additional SQL. The expensive part is repeated page setup over identical hydrated rowsets.

In rough terms, the current query-page cost is closer to:

- `O(page hydration)`
- plus `O(document count * page row indexing work)`

The target shape is:

- `O(page hydration)`
- plus `O(page row indexing work once)`
- plus `O(sum of rows actually emitted across documents)`

## Design Summary

Introduce an immutable page-scoped reconstitution context that is built once from:

- `ResourceReadPlan`
- `HydratedPage`

and then reused to materialize every document in the page.

The design has two layers:

1. `CompiledReconstitutionPlanCache`
   - immutable, plan-scoped, derived from `ResourceReadPlan`
   - contains indexes that do not depend on hydrated page contents
   - uses compiled read-plan ordinals for hot-path emission data
2. `PageReconstitutionContext`
   - immutable, page-scoped, derived from `CompiledReconstitutionPlanCache + HydratedPage`
   - contains indexes and row graph state for one hydrated page
   - attaches rows by table-qualified physical row identity rather than ad hoc per-document scans

This split matters because some of the current repeated work is not page-specific at all. A proper page design should not keep rebuilding plan-only lookups.

## Proposed Types

### Plan-Scoped Cache

```csharp
internal readonly record struct PhysicalRowIdentity(long Value);

internal sealed record CompiledReconstitutionPlanCache(
    PropertyOrderNode PropertyOrderTree,
    IReadOnlyDictionary<DbTableName, TableEmissionCache> TableCaches,
    DbTableName RootTable
);

internal sealed record TableEmissionCache(
    DbTableModel TableModel,
    DbTableName? ImmediateParentTable,
    int RootLocatorOrdinal,
    int? ImmediateParentLocatorOrdinal,
    int PhysicalRowIdentityOrdinal,
    int? OrdinalColumnOrdinal,
    IReadOnlyList<ReferenceIdentityProjectionBinding> ReferenceBindingsInOrder,
    IReadOnlyList<DescriptorEmissionBinding> DescriptorBindingsInOrder,
    IReadOnlyList<DbTableName> ImmediateChildrenInOrder
);

internal sealed record DescriptorEmissionBinding(
    JsonPathExpression DescriptorValuePath,
    int DescriptorIdColumnOrdinal
);
```

### Page-Scoped Context

```csharp
internal sealed record PageReconstitutionContext(
    CompiledReconstitutionPlanCache PlanCache,
    IReadOnlyList<DocumentPageNode> DocumentsInOrder,
    IReadOnlyDictionary<long, DocumentPageNode> DocumentsById,
    IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<PhysicalRowIdentity, RowNode>> RowsByPhysicalIdentityByTable,
    IReadOnlyDictionary<long, string> DescriptorUriById
);

internal sealed record DocumentPageNode(
    DocumentMetadataRow Metadata,
    RowNode Root
);

internal sealed record RowNode(
    DbTableName Table,
    object?[] Row,
    long DocumentId,
    PhysicalRowIdentity PhysicalRowIdentity,
    IReadOnlyDictionary<DbTableName, IReadOnlyList<RowNode>> ChildrenByTable
);
```

The important point is that `RowNode` references the existing hydrated `object?[]` row buffers. It does not copy row values into a second page-sized structure.

For the first implementation, the page-context builder should validate that each table has exactly one physical row identity column. That matches the current relational read shapes:

- root scope: `DocumentId`
- collections and extension child collections: `CollectionItemId`
- collection-aligned extension scopes: `BaseCollectionItemId`

## Why Split Plan Cache from Page Context

The page context should own only page-derived work:

- descriptor URI lookup,
- root row lookup by `DocumentId`,
- per-table row lookup by physical row identity,
- per-table row grouping by immediate parent locator,
- parent-child attachment,
- document ordering aligned to hydrated metadata.

The plan cache should own work derived only from the read plan:

- property order tree,
- immediate parent/child table relationships,
- reference bindings by table,
- descriptor emission bindings by table derived from compiled descriptor projection plans,
- root locator, immediate parent locator, physical row identity, and ordinal ordinals.

This keeps the page builder cheap and makes the cost model stable as page size grows.

## Page Context Build Algorithm

### Phase 1: Build Plan Cache

This should be created once per `ResourceReadPlan` and reused across requests if convenient. Even if it first ships as a request-local object, its contents are still plan-scoped.

Build once:

- `PropertyOrderTree`
- `ImmediateParentTable` + `ImmediateChildrenInOrder` for each table
- `ReferenceBindingsInOrder` by table
- `DescriptorBindingsInOrder` by table, built from `DescriptorProjectionPlansInOrder` / `DescriptorProjectionSource`
- root locator, immediate parent locator, physical row identity, and ordinal column ordinals by table
- fail-fast validation that each table exposes exactly one physical row identity column and at most one immediate parent locator column

This eliminates repeated table scanning, repeated `BuildPropertyOrderTree()` work, and repeated descriptor FK ordinal lookup by column name during materialization.

### Phase 2: Build Descriptor Lookup Once

Build one `DescriptorUriById` dictionary from `HydratedPage.DescriptorRowsInPlanOrder`.

This replaces per-document descriptor-lookup construction.

### Phase 3: Create Row Nodes

For each hydrated table:

- create one `RowNode` per hydrated row,
- assign `DocumentId`,
- compute `PhysicalRowIdentity` from `TableEmissionCache.PhysicalRowIdentityOrdinal`,
- retain a reference to the original row buffer.

For every table:

- build `rowsByPhysicalIdentityByTable`,
- validate duplicate physical row identities fail fast.

For the root table:

- build `rootNodeByDocumentId`,
- validate that there is at most one root row per `DocumentId`.

For non-root tables:

- build `rowsByParentLocator`,
- apply `Ordinal` sort once while building the grouping,
- retain the grouped lists for parent-child attachment.

### Phase 4: Attach Children Once

Walk tables in dependency order. For each non-root table:

- identify the immediate parent table from the plan cache,
- for each child row node:
  - read its immediate parent locator value,
  - resolve the parent row node from `RowsByPhysicalIdentityByTable[parentTable]`,
  - append the child node into the parent node's `ChildrenByTable[childTable]` list.

Attachment rules:

- top-level collections attach to the root node by `DocumentId`,
- nested collections attach by `ParentCollectionItemId`,
- collection-aligned extension scopes attach by `BaseCollectionItemId`,
- extension scope tables otherwise attach using the same immediate-parent locator rules as their base scope.

The parent lookup must be table-qualified, not locator-only. That preserves collection-aligned extension semantics and avoids accidental collisions between different parent tables that happen to use the same numeric identity value.

This turns the hydrated page into the in-memory row graph described by the redesign docs.

### Phase 5: Produce Documents in Page Order

Create `DocumentsInOrder` by iterating `HydratedPage.DocumentMetadata` in order and resolving each metadata row to its root node.

Validate:

- every metadata row has a root row,
- no extra root rows exist for documents outside metadata,
- duplicate root rows fail fast,
- child rows that cannot attach fail fast.

## Materialization Flow

With `PageReconstitutionContext` built, per-document materialization becomes a pure emit pass:

1. lookup `DocumentPageNode` by `DocumentId`
2. emit JSON from the root `RowNode`
3. recurse through attached children
4. use `DescriptorUriById` for descriptor values
5. use plan-cached reference bindings and compiled descriptor bindings
6. reorder using the cached `PropertyOrderTree`
7. inject API metadata for external responses

Nothing in that list should rebuild page indexes.

## Proposed API Changes

The cleanest shape is to make page materialization a first-class operation.

### Option A: Page-First Materializer

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

    // Temporary compatibility shim for existing single-document callers.
    JsonNode Materialize(RelationalReadMaterializationRequest request);
}
```

This is the preferred design.

Benefits:

- query reads use a naturally page-shaped API,
- GET-by-id becomes a one-document page case,
- page context creation cannot accidentally happen once per document.
- existing write-side callers can stay on the temporary single-document shim while the page-first API rolls out.

### Option B: Explicit Prepare-Then-Materialize

```csharp
public interface IRelationalReadMaterializer
{
    PageReconstitutionContext PreparePage(
        ResourceReadPlan readPlan,
        HydratedPage hydratedPage
    );

    JsonNode MaterializeDocument(
        PageReconstitutionContext context,
        DocumentMetadataRow metadata,
        RelationalGetRequestReadMode readMode
    );
}
```

This also works, but it exposes more internal structure through the materializer boundary.

### Recommendation

Use Option A externally and keep `PageReconstitutionContext` internal to the materializer/reconstituter implementation.

During migration, keep `Materialize(RelationalReadMaterializationRequest)` as a thin compatibility wrapper that:

1. builds a one-document page request,
2. delegates to `MaterializePage()`,
3. asserts exactly one result,
4. returns that document.

## Changes to `RelationalDocumentStoreRepository`

### Query Path

Replace:

- loop over `hydratedPage.DocumentMetadata`
- call `Materialize()` once per document

with:

- call `MaterializePage()` once
- iterate the already materialized page results
- apply readable profile projection per materialized document if needed

### GET-by-Id Path

Replace:

- single-document `Materialize()` call

with:

- call `MaterializePage()` on the one-document hydrated page
- assert a single materialized document

This keeps GET-by-id and query on the same page-first implementation.

### Write-Side Callers During Migration

`RelationalCommittedRepresentationReader` and `DefaultRelationalWriteExecutor` should initially remain on the temporary `Materialize()` compatibility wrapper. That keeps write readback and profile stored-state projection behavior unchanged while still forcing them through the same page-first implementation internally.

## Changes to `DocumentReconstituter`

`DocumentReconstituter` should stop accepting raw page rowsets for the hot path.

Instead of:

```csharp
Reconstitute(
    long documentId,
    IReadOnlyList<HydratedTableRows> tableRowsInDependencyOrder,
    ...
)
```

the primary path should become:

```csharp
ReconstituteDocument(
    DocumentPageNode document,
    PageReconstitutionContext context
)
```

The old signature can temporarily remain as a compatibility wrapper:

1. build a one-off page context,
2. resolve the requested document,
3. delegate to the new implementation.

That keeps existing tests and write-side callers working during transition.

## Emission Details

The emitter should use only context-owned data and row-local data.

For each `RowNode`:

- emit scalar values using `DbColumnModel.SourceJsonPath`,
- emit references from `TableEmissionCache.ReferenceBindingsInOrder`,
- emit descriptors from `TableEmissionCache.DescriptorBindingsInOrder`,
- recurse through `ChildrenByTable` in dependency order from `TableEmissionCache.ImmediateChildrenInOrder`.

This removes repeated filtering work like:

- "find reference plans for this table",
- "find descriptor bindings for this table",
- "find immediate children of this table".

## Complexity and Expected Gain

### Current Shape

For a page with `N` documents and `R` hydrated rows:

- root row scan is repeated up to `N` times,
- child row grouping is repeated up to `N` times per child table,
- descriptor lookup is rebuilt `N` times,
- table relationship discovery is repeated throughout each document walk.

That pushes the effective cost closer to `O(N * R)` for setup work.

### Proposed Shape

For the same page:

- build descriptor lookup once: `O(descriptor rows)`
- build row graph once: `O(R)`
- emit documents once each: proportional to rows actually belonging to each document

That moves setup work to `O(R)` instead of `O(N * R)`.

For larger page sizes and deep resources, that is the main performance win the design intended.

## Memory Trade-Off

The page context adds wrapper and lookup structures:

- one `RowNode` per hydrated row,
- dictionaries/lists for root lookup, table-qualified physical-row lookup, and child attachment,
- one descriptor lookup dictionary.

This is an acceptable trade because:

- query pages are already bounded by request page size,
- the implementation reuses existing hydrated row buffers instead of copying them,
- the added memory replaces repeated CPU-heavy indexing passes.

If allocation pressure becomes a measured problem later, follow-up optimizations can:

- pool child lists,
- use arrays for some table indexes,
- store child table indexes by dependency-order integer instead of `DbTableName`.

Those are optimizations, not prerequisites for the design.

## Rollout Plan

### Phase 1

- introduce `CompiledReconstitutionPlanCache`
- introduce `PageReconstitutionContext`
- implement a context builder from `ResourceReadPlan + HydratedPage`
- validate one physical row identity column per table
- keep current JSON semantics unchanged

### Phase 2

- add `IRelationalReadMaterializer.MaterializePage()`
- keep `IRelationalReadMaterializer.Materialize()` as a temporary wrapper over the new page-first path
- switch query reads to the page-first path

### Phase 3

- switch GET-by-id to the same page-first path
- route write-side callers through the temporary wrapper if they are not migrated yet
- remove repeated per-document setup paths
- reduce or eliminate the old `ReconstitutionContext`
- convert the old `Reconstitute(documentId, tableRows, ...)` entry point into a wrapper

### Phase 4

- migrate remaining write-side callers and test doubles to `MaterializePage()` where that improves clarity
- delete the old single-document wrappers only after no call sites depend on them

### Phase 5

- optional follow-up: move `CompiledReconstitutionPlanCache` creation into read-plan compilation or mapping-set startup so it is truly plan-scoped and shared across requests

## Testing Plan

### Unit Tests

- page context builds one descriptor lookup for the whole page
- root rows map correctly by `DocumentId`
- physical row identities map correctly by table, including collection-aligned extension scopes keyed by `BaseCollectionItemId`
- child rows attach correctly for:
  - top-level collections
  - nested collections
  - extension scopes
- descriptor emission uses compiled ordinals rather than column-name rediscovery
- `Ordinal` ordering is preserved
- missing parent rows fail fast
- duplicate root rows fail fast
- duplicate physical row identities within a table fail fast
- materialized JSON matches existing reconstitution output for representative fixtures

### Repository Tests

- query path calls the materializer once per page, not once per document
- GET-by-id uses the same page-first materialization path
- readable profile projection still applies per item after full reconstitution
- the temporary single-document `Materialize()` wrapper delegates to the page-first path

### Integration Tests

- existing query execution tests continue to pass unchanged
- add a large enough multi-document page case to catch accidental per-document page rebuilds
- existing write readback and profile stored-state projection behavior remains unchanged while the temporary wrapper is in place

### Perf Verification

Add lightweight instrumentation or benchmark coverage around:

- page context build time
- per-document emit time
- total query page materialization time

The target is to show setup work stays roughly flat as documents are added to the page, while emit work grows with actual page contents.

## Compatibility Notes

- Output JSON shape should remain unchanged.
- `_etag`, `id`, and `_lastModifiedDate` stay a post-reconstitution concern.
- readable profile projection remains above full reconstitution and still refreshes `_etag`.
- no additional database roundtrips are introduced.
- the initial implementation may keep a temporary single-document materialization wrapper so existing write-side callers do not need a flag day migration.

## Recommendation

Implement a page-first materialization pipeline centered on `PageReconstitutionContext`, backed by a plan-scoped `CompiledReconstitutionPlanCache`.

That is the smallest design that:

- fixes the performance issue called out in review,
- aligns with the redesign's "page-based reconstitution" requirement,
- avoids mixing DB concerns with JSON-assembly concerns,
- gives GET-by-id and query one consistent read-materialization path.
