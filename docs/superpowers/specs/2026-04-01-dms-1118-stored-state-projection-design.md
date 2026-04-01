# DMS-1118: Stored-State Projection + HiddenMemberPaths Computation

**Story:** [DMS-1118](https://edfi.atlassian.net/browse/DMS-1118) (C6, Tier 3)
**Branch:** `DMS-1118` based on `DMS-1116-1117`
**Date:** 2026-04-01

## Context

DMS-1118 is the C6 component of the Core Profile Support Delivery Plan. It implements the stored-state projector callback: given the current stored JSON and the compiled-scope adapter, produce `VisibleStoredBody`, `StoredScopeStates`, `VisibleStoredCollectionRows`, and `HiddenMemberPaths`, and assemble the complete `ProfileAppliedWriteContext`.

C6 depends on:
- C1 (DMS-1111) — compiled-scope adapter and address derivation engine
- C3 (DMS-1115) — shared visibility classification primitive (`ProfileVisibilityClassifier`)
- C5 (DMS-1117) — pipeline orchestration and `StoredSideExistenceLookupResult`

### Key Insight: C5 Already Computes Most of C6's Outputs

C5's `StoredSideExistenceLookupBuilder.Build()` already walks the stored document, classifies each scope's visibility, derives addresses, computes `HiddenMemberPaths` for every scope and collection row, and filters collection items by value filter. The `StoredSideExistenceLookupResult` contains fully-populated `ImmutableArray<StoredScopeState>` (with Address, Visibility, and HiddenMemberPaths) and `ImmutableArray<VisibleStoredCollectionRow>` (with Address and HiddenMemberPaths).

C6's primary new work is:
1. Producing `VisibleStoredBody` — a filtered JSON of the stored document
2. Assembling `ProfileAppliedWriteContext` from the request + visible stored body + pre-computed stored-side results

## Architecture

### New Components

#### `StoredBodyShaper` (`Core/Profile/StoredBodyShaper.cs`)

A recursive JSON walker that filters a stored document through writable-profile visibility rules. Parallel structure to `WritableRequestShaper` but with no validation failures, no scope-state emission, and no address derivation.

**Constructor:** `ProfileVisibilityClassifier classifier`

**Public API:** `JsonNode Shape(JsonNode storedDocument)`

**Walk structure:**
- `ShapeScope(jsonScope, source)` — classify visibility via `classifier.ClassifyScope()`, recurse into members
- `ShapeScopeMembers(jsonScope, source)` — iterate JSON properties, dispatch to child scope / collection / extension / scalar handlers
- `ShapeNonCollectionChild(jsonScope, scopeData)` — classify visibility; if `VisiblePresent` and data present, recurse into members and include in output; otherwise omit
- `ShapeCollection(jsonScope, scopeData)` — if not `VisiblePresent` or null, skip (VisibleAbsent emits empty array, Hidden omits entirely); for each item, silently skip if `PassesCollectionItemFilter` returns false; for passing items, filter members and recurse into nested scopes
- `ShapeExtensions(parentScope, extNode)` — iterate extension scopes, classify each; if `VisiblePresent`, recurse; otherwise omit
- Scalar members: apply `GetMemberFilter()` for the containing scope (`IncludeOnly` / `ExcludeOnly` / `IncludeAll`); hidden scalars are silently dropped; visible scalars are `DeepClone()`d into output

**Key differences from `WritableRequestShaper`:**
- No validation failures (stored data is trusted)
- No `RequestScopeState` or `VisibleRequestCollectionItem` emission
- No address derivation (no `AddressDerivationEngine` dependency)
- Stored collection items that fail the value filter are silently excluded, not rejected
- No `requestJsonPath` tracking (no error paths needed)

**Scope dispatch mechanism:** Uses the same `classifier.ContainsScope()` and `classifier.GetScopeKind()` probe pattern as `WritableRequestShaper` to distinguish non-collection child scopes (`$"{jsonScope}.{memberName}"`), collection child scopes (`$"{jsonScope}.{memberName}[*]"`), and scalar members.

#### `StoredStateProjector` (`Core/Profile/StoredStateProjector.cs`)

Concrete class — no interface. Constructed per pipeline run with all required context.

**Constructor:** `JsonNode storedDocument, ProfileVisibilityClassifier classifier`

**Method:** `ProfileAppliedWriteContext ProjectStoredState(ProfileAppliedWriteRequest request, StoredSideExistenceLookupResult existenceLookupResult)`

Implementation:
1. Instantiate `StoredBodyShaper(classifier)` and call `Shape(storedDocument)` to produce `VisibleStoredBody`
2. Return `new ProfileAppliedWriteContext(request, visibleStoredBody, existenceLookupResult.ClassifiedStoredScopes, existenceLookupResult.ClassifiedStoredCollectionRows)`

The pre-computed `ClassifiedStoredScopes` and `ClassifiedStoredCollectionRows` from the existence lookup result are passed through unchanged. C6 does not reclassify stored scopes or recompute hidden member paths.

### Modified Components

#### `ProfileWritePipeline` (`Core/Profile/ProfileWritePipeline.cs`)

Where C5 currently receives/uses an `IStoredStateProjector` parameter:
- Remove the `IStoredStateProjector` parameter
- Construct a `StoredStateProjector` directly using pipeline-local context (stored document, classifier)
- Call `projector.ProjectStoredState(request, existenceLookupResult)` in the update/upsert branch

#### `IStoredStateProjector.cs` — Delete

The interface was a useful stub boundary during incremental C5/C6 development but adds no value once C6 is concrete. The pipeline constructs and calls the concrete class directly. C5's create-flow and request-assembly tests remain unaffected (the `!isCreate` guard makes C6 invocation conditional).

The `StoredSideExistenceLookupResult` record defined in the same file must be relocated to `Core.External/Profile/ProfileAppliedWriteContracts.cs` alongside the other contract types it references (`StoredScopeState`, `VisibleStoredCollectionRow`).

## Testing Strategy

### `StoredBodyShaperTests.cs`

Unit tests for JSON filtering, following the `Given_` / `It_` naming convention:

- Visible scalars at root are included, hidden scalars are stripped
- `IncludeOnly`, `ExcludeOnly`, `IncludeAll` filter modes produce correct output
- Non-collection child scope: VisiblePresent includes nested members, VisibleAbsent omits, Hidden omits
- Collection scope: VisiblePresent iterates items, Hidden omits entire array
- Collection item value filter: passing items included, failing items silently excluded
- Nested scopes within collection items are recursively filtered
- Extension scopes (`_ext` at root and within collection items) follow same visibility rules
- Empty/null collections and scopes handled gracefully
- `DeepClone` correctness: output nodes are not aliased to input nodes

### `StoredStateProjectorTests.cs`

Unit tests for context assembly:

- Produces correct `ProfileAppliedWriteContext` with all four members populated
- `VisibleStoredBody` matches expected filtered JSON
- `StoredScopeStates` and `VisibleStoredCollectionRows` are passed through from `StoredSideExistenceLookupResult` unchanged
- `Request` is passed through unchanged

### C5 Pipeline Integration Tests (update existing)

Replace mock/stub `IStoredStateProjector` with real `StoredStateProjector`:

- End-to-end: writable profile + stored document through full pipeline produces correct `ProfileAppliedWriteContext`
- Visible stored body, scope states, and collection rows are all correct in the assembled context
- Per C5 story: "End-to-end update-flow testing (full pipeline including C6 invocation producing correct ProfileAppliedWriteContext) is completed when C6 lands"

### Test Fixtures

Follow existing patterns — build `CompiledScopeDescriptor` catalogs and `ProfileDefinition` objects that exercise key combinations: root + 1:1 + collection + nested + extension, various filter modes.

## File Inventory

### New Files
| File | Purpose |
|------|---------|
| `src/dms/core/EdFi.DataManagementService.Core/Profile/StoredBodyShaper.cs` | Stored document JSON filtering |
| `src/dms/core/EdFi.DataManagementService.Core/Profile/StoredStateProjector.cs` | Context assembly orchestrator |
| `src/dms/core/EdFi.DataManagementService.Core.Tests/Unit/Profile/StoredBodyShaperTests.cs` | StoredBodyShaper unit tests |
| `src/dms/core/EdFi.DataManagementService.Core.Tests/Unit/Profile/StoredStateProjectorTests.cs` | StoredStateProjector unit tests |

### Modified Files
| File | Change |
|------|--------|
| `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileWritePipeline.cs` | Remove `IStoredStateProjector` parameter, construct `StoredStateProjector` directly |
| C5 pipeline test files | Replace mock C6 with real `StoredStateProjector` for update-flow tests |

### Deleted Files
| File | Reason |
|------|--------|
| `src/dms/core/EdFi.DataManagementService.Core.External/Profile/IStoredStateProjector.cs` | Interface no longer needed; `StoredSideExistenceLookupResult` record relocated |

### Relocated Types
| Type | From | To |
|------|------|----|
| `StoredSideExistenceLookupResult` | `IStoredStateProjector.cs` | `Core.External/Profile/ProfileAppliedWriteContracts.cs` |

## Design Decisions

1. **Constructor injection for StoredStateProjector** — mirrors `WritableRequestShaper`'s established pattern; keeps the method signature focused on per-invocation inputs
2. **Separate `StoredBodyShaper`** — parallel to `WritableRequestShaper` with divergent concerns (no validation vs. validation + state emission); avoids coupling the two shapers through a shared abstraction
3. **Delete `IStoredStateProjector` interface** — YAGNI; the pipeline owns construction and the update-flow guard already isolates C6 invocation
4. **Pass-through of pre-computed stored-side results** — C5's `StoredSideExistenceLookupBuilder` already computes `StoredScopeStates` and `VisibleStoredCollectionRows` with full `HiddenMemberPaths`; C6 does not reclassify or recompute
