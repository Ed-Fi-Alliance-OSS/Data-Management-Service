# DMS-1115 (C3): Request-Side Visibility Classification + Writable Request Shaping

**Jira:** [DMS-1115](https://edfi.atlassian.net/browse/DMS-1115)
**Branch:** `DMS-1115` from `DMS-1112`
**Dependencies:** C1/DMS-1111 (merged to main), C8/DMS-1112 (base branch)

## Overview

Implement request-side visibility classification for all compiled scopes and produce the shaped `WritableRequestBody` plus structured `RequestScopeState` and `VisibleRequestCollectionItem` entries.

This story covers 6 of the 15 Core responsibilities: #2 (readable/writable selection), #3 (recursive member filtering), #4 (collection item value filtering), #7 (writable request shaping), #10 (visibility signaling -- request side), #14 (extension semantics -- request side).

## Architecture: Layered Approach

Two-layer design with a clean dependency boundary:

1. **`ProfileVisibilityClassifier`** -- shared reusable primitive consumed by C3 (request side), C5 (stored-side existence lookup), and C6 (stored-state projection). Instance class with cached profile-to-scope bridging.
2. **`WritableRequestShaper`** -- request-specific shaping that consumes the classifier. Produces shaped JSON, scope states, collection items, and validation failures in a single walk.

A shared **`ProfileTreeNavigator`** is extracted from C2's `SemanticIdentityCompatibilityValidator` to serve both C2 and C3.

## Design Decisions

| Decision | Choice | Rationale |
| --- | --- | --- |
| Shared primitive structure | Instance class with cached bridging | Profile-to-scope navigation is identical for request and stored side; caching avoids redundant tree traversal |
| Value filter violations | Collect all in one pass | Consistent with C2's pattern; gives API consumers complete error picture |
| Profile tree navigation | Extract shared `ProfileTreeNavigator`, refactor C2 | Clean reuse; avoids duplication between C2 and C3 |
| JSON output construction | Build fresh during visibility walk | Single pass combines classification + shaping; avoids mutation semantics |
| Extension visibility | Follows parent's `MemberSelection` | Same rules as all other member types; "extensions follow base-data rules" per design doc |

---

## Component 1: `ProfileTreeNavigator`

**File:** `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileTreeNavigator.cs`

Extracted and generalized from `SemanticIdentityCompatibilityValidator`'s private `ProfileTreeNode` and navigation logic.

### API

```csharp
public sealed class ProfileTreeNavigator
{
    public ProfileTreeNavigator(ContentTypeDefinition writeContentType);

    // Navigate to the profile rules for a given compiled scope.
    // Returns null if the scope path cannot be resolved in the profile tree.
    public ProfileTreeNode? Navigate(string jsonScope);
}

public readonly record struct ProfileTreeNode(
    MemberSelection MemberSelection,
    // For IncludeOnly: the set of explicitly included property names.
    // For ExcludeOnly: the set of explicitly excluded property names.
    // For IncludeAll: empty set (all properties visible, no explicit list needed).
    IReadOnlySet<string> ExplicitPropertyNames,
    IReadOnlyDictionary<string, CollectionRule> CollectionsByName,
    IReadOnlyDictionary<string, ObjectRule> ObjectsByName,
    IReadOnlyDictionary<string, ExtensionRule>? ExtensionsByName
);
```

### Navigation Algorithm

1. Parse `JsonScope` into segments by splitting on `.`
2. Strip `[*]` suffix from collection segments
3. Starting from the root `ContentTypeDefinition`, walk segments:
   - `_ext` -> switch to extension rules
   - Collection name -> look up `CollectionRule`
   - Object name -> look up `ObjectRule`
4. Return the `ProfileTreeNode` at the final segment, or null if unresolvable

### C2 Refactor

`SemanticIdentityCompatibilityValidator` replaces its private `ProfileTreeNode` struct and navigation methods with calls to `ProfileTreeNavigator`. Validation logic (checking identity member visibility) remains unchanged.

---

## Component 2: `ProfileVisibilityClassifier`

**File:** `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileVisibilityClassifier.cs`

The shared reusable primitive. Instance class constructed with profile + scope catalog. Pre-computes visibility lookup cache keyed by `JsonScope`.

### API

```csharp
public sealed class ProfileVisibilityClassifier
{
    public ProfileVisibilityClassifier(
        ContentTypeDefinition writeContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    );

    // Scope-level visibility classification
    public ProfileVisibilityKind ClassifyScope(string jsonScope, JsonNode? scopeData);

    // Collection item value filter evaluation. Returns true if item passes.
    public bool PassesCollectionItemFilter(string jsonScope, JsonNode collectionItem);

    // Member filter for a scope -- tells the shaper what to include/exclude
    public ScopeMemberFilter GetMemberFilter(string jsonScope);
}

public readonly record struct ScopeMemberFilter(
    MemberSelection Mode,
    IReadOnlySet<string> ExplicitNames
);
```

### Pre-computation (Construction Time)

For each `CompiledScopeDescriptor` in the catalog, navigate the profile tree and determine:
- Whether the scope is hidden or potentially visible (walking the ancestor chain)
- The effective `MemberSelection` mode
- The set of visible/excluded member names

At each level of the ancestor chain:
- `IncludeOnly` -> segment must be in explicit names to be visible
- `ExcludeOnly` -> segment must NOT be in explicit names to be visible
- `IncludeAll` -> always visible
- If any ancestor hides the scope, it is hidden
- Extensions follow the same logic via the parent's `MemberSelection`

### `ClassifyScope` Logic

1. Look up pre-computed entry for `jsonScope`
2. If hidden by profile -> `Hidden`
3. If `scopeData` is non-null -> `VisiblePresent`
4. If `scopeData` is null -> `VisibleAbsent`

### `PassesCollectionItemFilter` Logic

1. Look up the `CollectionRule` for this scope via cached navigation
2. If no `CollectionItemFilter` defined -> return `true`
3. Extract the filter property value from the `collectionItem` `JsonNode`
4. Evaluate against `FilterMode` (Include/Exclude) and `Values` list
5. Return whether the item passes

---

## Component 3: `WritableRequestShaper`

**File:** `src/dms/core/EdFi.DataManagementService.Core/Profile/WritableRequestShaper.cs`

Consumes `ProfileVisibilityClassifier` + `AddressDerivationEngine`. Walks the request body once, building the shaped output while emitting scope states and collection items.

### API

```csharp
public sealed class WritableRequestShaper(
    ProfileVisibilityClassifier classifier,
    AddressDerivationEngine addressEngine
)
{
    public WritableRequestShapingResult Shape(JsonNode requestBody);
}

public sealed record WritableRequestShapingResult(
    JsonNode WritableRequestBody,
    ImmutableArray<RequestScopeState> RequestScopeStates,
    ImmutableArray<VisibleRequestCollectionItem> VisibleRequestCollectionItems,
    ImmutableArray<WritableProfileValidationFailure> ValidationFailures
);
```

### Single-Pass Walk Algorithm

1. **Root scope (`$`):** Classify visibility (always `VisiblePresent` for request body). Get member filter. Derive `ScopeInstanceAddress`. Emit `RequestScopeState` with `Creatable = false`. Begin building new root `JsonObject`.

2. **For each member at current scope**, consult the member filter:
   - **Visible scalar/property** -> copy to output `JsonObject`
   - **Non-collection child scope** -> recurse (step 3)
   - **Collection** -> recurse (step 4)
   - **`_ext`** -> recurse into each extension child (step 5)
   - **Not visible** -> skip

3. **Non-collection scope recursion:**
   - `ClassifyScope(childJsonScope, childData)` -> get visibility
   - Derive `ScopeInstanceAddress` via address engine with current ancestor context
   - Emit `RequestScopeState`
   - If `VisiblePresent` -> recurse, building a new `JsonObject`
   - If `VisibleAbsent` or `Hidden` -> do not recurse, no output node

4. **Collection scope processing:**
   - `ClassifyScope(collectionJsonScope, collectionArray)` -> get visibility
   - If `Hidden` -> skip
   - If `VisibleAbsent` -> no items
   - If `VisiblePresent` -> iterate each submitted item:
     - `PassesCollectionItemFilter` -> if fails, add category-3 `WritableProfileValidationFailure` via `ProfileFailures.ForbiddenSubmittedData()`, do not include item in output
     - If passes -> derive `CollectionRowAddress`, emit `VisibleRequestCollectionItem` with `Creatable = false`, apply member filter, build filtered item, add to output array
     - Recurse into nested collections/objects within each visible item

5. **Extension scope (`_ext`) processing:**
   - Check for `_ext` key in the request at current scope
   - For each extension child, treat as a scope following the same visibility rules per parent's `MemberSelection`
   - Build `_ext` output object with only visible extension children
   - Each extension child's inner members follow the same filtering logic

**Ancestor context threading:** The walk maintains a `List<AncestorItemContext>` that grows as it descends into collection items, passed to the address engine for correct address derivation within nested structures.

---

## Contract Types

### New types in `Core.External/Profile/`

```csharp
// ProfileVisibilityKind.cs
public enum ProfileVisibilityKind
{
    VisiblePresent,
    VisibleAbsent,
    Hidden
}

// RequestScopeState.cs
public sealed record RequestScopeState(
    ScopeInstanceAddress Address,
    ProfileVisibilityKind Visibility,
    bool Creatable  // initially false, populated by C4
);

// VisibleRequestCollectionItem.cs
public sealed record VisibleRequestCollectionItem(
    CollectionRowAddress Address,
    bool Creatable  // initially false, populated by C4
);
```

These go in `Core.External` because they are part of the Core-backend contract consumed by C4, C5, C6, and DMS-1106.

### New types in `Core/Profile/` (internal)

- `ProfileTreeNavigator` + `ProfileTreeNode` (Component 1)
- `ProfileVisibilityClassifier` + `ScopeMemberFilter` (Component 2)
- `WritableRequestShaper` + `WritableRequestShapingResult` (Component 3)

### Already exist (from C1/C8)

- `ScopeInstanceAddress`, `CollectionRowAddress`, `SemanticIdentityPart`, `AncestorCollectionInstance` -- `AddressTypes.cs`
- `CompiledScopeDescriptor`, `ScopeKind` -- `CompiledScopeTypes.cs`
- `WritableProfileValidationFailure` (category-3 base) -- `ProfileFailure.cs`
- `ProfileFailures.ForbiddenSubmittedData()` factory -- `ProfileFailure.cs`
- `AncestorItemContext` -- `AncestorItemContext.cs`
- `AddressDerivationEngine` -- `AddressDerivationEngine.cs`

---

## Integration Points

| Consumer | What it uses from C3 |
| --- | --- |
| **C4** (creatability) | `RequestScopeState[]` and `VisibleRequestCollectionItem[]` from `WritableRequestShapingResult`; enriches `Creatable` flags |
| **C5** (orchestrator) | Constructs `ProfileVisibilityClassifier` once, passes to both `WritableRequestShaper` (request side) and C6 (stored side) |
| **C6** (stored projection) | `ProfileVisibilityClassifier` directly for stored-side visibility; adds `HiddenMemberPaths` computation |

### What C3 does NOT produce (deferred)

- `RootResourceCreatable` -- owned by C4
- `StoredScopeState` / `VisibleStoredCollectionRow` / `HiddenMemberPaths` -- owned by C6
- `ProfileAppliedWriteRequest` / `ProfileAppliedWriteContext` assembly -- owned by C5

---

## Test Strategy

**Location:** `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/`

Follows project conventions: `TestFixture` classes with `Given_` prefix, `Setup` for arrange+act, `It_` prefix for assertions.

### `ProfileTreeNavigatorTests.cs`

| Fixture | Expectation |
| --- | --- |
| `Given_root_scope` | Navigates to root content type |
| `Given_non_collection_child_scope` | Navigates `$.calendarReference` to object rule |
| `Given_collection_scope` | Navigates `$.classPeriods[*]` to collection rule |
| `Given_nested_scope` | Navigates multi-segment paths |
| `Given_extension_scope` | Navigates `$._ext.sample` to extension rule |
| `Given_extension_within_collection` | Navigates `$.classPeriods[*]._ext.sample` |
| `Given_scope_not_in_profile` | Returns null |

### `ProfileVisibilityClassifierTests.cs`

**Scope-level visibility:**

| Fixture | Expectation |
| --- | --- |
| `Given_IncludeOnly_profile_and_visible_scope_with_data` | `VisiblePresent` |
| `Given_IncludeOnly_profile_and_visible_scope_without_data` | `VisibleAbsent` |
| `Given_IncludeOnly_profile_and_hidden_scope` | `Hidden` |
| `Given_ExcludeOnly_profile_excluding_a_scope` | `Hidden` |
| `Given_ExcludeOnly_profile_and_non_excluded_scope` | `VisiblePresent` / `VisibleAbsent` |
| `Given_IncludeAll_profile` | All scopes visible |
| `Given_hidden_parent_scope` | Child scopes also `Hidden` |
| `Given_extension_scope_with_IncludeOnly_parent` | Hidden if not listed |
| `Given_extension_scope_with_IncludeAll_parent` | Visible |

**Collection item value filtering:**

| Fixture | Expectation |
| --- | --- |
| `Given_no_item_filter` | Passes |
| `Given_include_filter_and_matching_item` | Passes |
| `Given_include_filter_and_non_matching_item` | Fails |
| `Given_exclude_filter_and_matching_item` | Fails |
| `Given_exclude_filter_and_non_matching_item` | Passes |

**Member filtering:**

| Fixture | Expectation |
| --- | --- |
| `Given_IncludeOnly_scope` | Returns only listed member names |
| `Given_ExcludeOnly_scope` | Returns excluded names with ExcludeOnly mode |
| `Given_IncludeAll_scope` | Returns IncludeAll mode |

### `WritableRequestShaperTests.cs`

**Shaping:**

| Fixture | Expectation |
| --- | --- |
| `Given_root_with_hidden_members` | Output excludes hidden scalars |
| `Given_hidden_non_collection_scope` | Entire scope absent from output |
| `Given_visible_non_collection_scope` | Scope present with filtered members |
| `Given_visible_collection_with_filtered_members` | Items present with only visible members |
| `Given_extension_scope_visibility` | `_ext` contains only visible extensions |

**RequestScopeState emission:**

| Fixture | Expectation |
| --- | --- |
| `Given_request_with_mixed_visibility` | Correct states for root, visible child, hidden child |
| `Given_absent_visible_scope` | Emits `VisibleAbsent` state |
| `Given_all_Creatable_flags_false` | All `Creatable` fields are `false` |

**VisibleRequestCollectionItem emission:**

| Fixture | Expectation |
| --- | --- |
| `Given_collection_with_visible_items` | Items with correct `CollectionRowAddress` |
| `Given_nested_collection` | Correct ancestor chain in addresses |

**Validation failures:**

| Fixture | Expectation |
| --- | --- |
| `Given_collection_item_failing_value_filter` | Category-3 failure collected, item excluded |
| `Given_multiple_failing_items` | All failures collected in one pass |
| `Given_items_passing_value_filter` | No failures, items in output |

**Shared reference fixture:** At least one test per class uses the `StudentSchoolAssociation` + `RestrictedAssociation-Write` profile from the delivery plan's worked example.

---

## File Summary

| File | Project | New/Modified |
| --- | --- | --- |
| `ProfileTreeNavigator.cs` | Core | New |
| `ProfileVisibilityClassifier.cs` | Core | New |
| `WritableRequestShaper.cs` | Core | New |
| `ProfileVisibilityKind.cs` | Core.External | New |
| `RequestScopeState.cs` | Core.External | New |
| `VisibleRequestCollectionItem.cs` | Core.External | New |
| `SemanticIdentityCompatibilityValidator.cs` | Core | Modified (use ProfileTreeNavigator) |
| `ProfileTreeNavigatorTests.cs` | Core.Tests.Unit | New |
| `ProfileVisibilityClassifierTests.cs` | Core.Tests.Unit | New |
| `WritableRequestShaperTests.cs` | Core.Tests.Unit | New |
| `SemanticIdentityCompatibilityValidatorTests.cs` | Core.Tests.Unit | Modified (verify still passes after refactor) |
