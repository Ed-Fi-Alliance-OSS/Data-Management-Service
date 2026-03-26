# DMS-1111: Compiled-Scope Adapter Contract + Address Derivation Engine

## Summary

Define the shared compiled-scope adapter contract types and implement the normative
`ScopeInstanceAddress` / `CollectionRowAddress` derivation algorithm. This is the Tier 0
foundation for Core profile write support (C1). Stories C2-C6 depend directly on these
types and the derivation engine.

Aligns with:

- `profiles.md` &sect;"Shared Compiled-Scope Adapter"
- `profiles.md` &sect;"Scope and Row Address Derivation"
- `core-profile-delivery-plan.md` C1

## Ownership Boundary

- Core owns: adapter contract types, address types, derivation engine, test-only adapter factory.
- Backend owns: production adapter factory (populating from `TableWritePlan` / `DbTableModel`).
- C1 contract types must NOT reference backend compiled-plan types.

## Contract Types

All types live in `src/dms/core/EdFi.DataManagementService.Core/Profile/`.

### ScopeKind

```csharp
public enum ScopeKind { Root, NonCollection, Collection }
```

Three-way distinction. Maps from backend's `DbTableKind`:

| DbTableKind | ScopeKind |
|---|---|
| Root | Root |
| RootExtension | NonCollection |
| CollectionExtensionScope | NonCollection |
| Collection | Collection |
| ExtensionCollection | Collection |

`ScopeKind` intentionally does not distinguish inlined-vs-separate-table storage topology.
That distinction is backend-only, resolved from `TableWritePlan` metadata at execution time.

### CompiledScopeDescriptor

```csharp
public sealed record CompiledScopeDescriptor(
    string JsonScope,
    ScopeKind ScopeKind,
    string? ImmediateParentJsonScope,
    ImmutableArray<string> CollectionAncestorsInOrder,
    ImmutableArray<string> SemanticIdentityRelativePathsInOrder,
    ImmutableArray<string> CanonicalScopeRelativeMemberPaths
);
```

Six fields per compiled scope, matching `profiles.md` exactly:

| Field | Semantics |
|---|---|
| `JsonScope` | Exact compiled scope identifier as `string`. Matches `DbTableModel.JsonScope.Canonical`. |
| `ScopeKind` | Root, NonCollection, or Collection. |
| `ImmediateParentJsonScope` | Compiled parent scope. Nullable (root has no parent). Collection-aligned `_ext` scopes point at the aligned base scope. |
| `CollectionAncestorsInOrder` | Collection scopes from root-most to immediate parent collection ancestor, as `string` JsonScope values. |
| `SemanticIdentityRelativePathsInOrder` | Non-empty compiled semantic identity member paths for persisted multi-item collection scopes. Empty for non-collection scopes. |
| `CanonicalScopeRelativeMemberPaths` | Canonical vocabulary for `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths`. |

### Address Types

Per `profiles.md` lines 284-300:

```csharp
public sealed record ScopeInstanceAddress(
    string JsonScope,
    ImmutableArray<AncestorCollectionInstance> AncestorCollectionInstances
);

public sealed record AncestorCollectionInstance(
    string JsonScope,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder
);

public sealed record CollectionRowAddress(
    string JsonScope,
    ScopeInstanceAddress ParentAddress,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityInOrder
);

public sealed record SemanticIdentityPart(
    string RelativePath,
    JsonNode? Value,
    bool IsPresent
);
```

### AncestorItemContext (engine input)

```csharp
public sealed record AncestorItemContext(string JsonScope, JsonNode Item);
```

Callers provide this to tell the engine which concrete collection item is on the traversal
path for each ancestor collection scope.

## Derivation Engine

### Class Shape

```csharp
public class AddressDerivationEngine
{
    private readonly IReadOnlyDictionary<string, CompiledScopeDescriptor> _scopesByJsonScope;

    public AddressDerivationEngine(IReadOnlyList<CompiledScopeDescriptor> scopeCatalog)
    {
        _scopesByJsonScope = scopeCatalog.ToDictionary(s => s.JsonScope);
    }

    public ScopeInstanceAddress DeriveScopeInstanceAddress(
        string jsonScope,
        IReadOnlyList<AncestorItemContext> ancestorItems);

    public CollectionRowAddress DeriveCollectionRowAddress(
        string jsonScope,
        JsonNode collectionItem,
        IReadOnlyList<AncestorItemContext> ancestorItems);
}
```

Instance-based with pre-indexed scope catalog. Callers (C3, C5, C6) own JSON traversal
and accumulate `AncestorItemContext` as they walk into nested collections.

### Normative 7-Step Algorithm

**`DeriveScopeInstanceAddress(jsonScope, ancestorItems)`:**

1. **Resolve** compiled scope descriptor from pre-indexed catalog. Throw if not found
   or if `ScopeKind == Collection`.
2. **Derive AncestorCollectionInstances** from `CollectionAncestorsInOrder`:
   - For each ancestor JsonScope, find the matching `AncestorItemContext`.
   - Look up the ancestor's `CompiledScopeDescriptor` to get its
     `SemanticIdentityRelativePathsInOrder`.
   - Read each semantic identity relative path from the ancestor's JSON item.
   - Emit `AncestorCollectionInstance(ancestorJsonScope, identityParts)`.
3. Return `ScopeInstanceAddress(jsonScope, ancestorCollectionInstances)`.

Root scope `$` has empty `CollectionAncestorsInOrder` and requires empty `ancestorItems`.

**`DeriveCollectionRowAddress(jsonScope, collectionItem, ancestorItems)`:**

1. **Resolve** descriptor. Throw if not found or `ScopeKind != Collection`.
2. **Derive ParentAddress** = `DeriveScopeInstanceAddress(descriptor.ImmediateParentJsonScope, ancestorItems)`.
3. **Read SemanticIdentityInOrder** from `collectionItem` using the addressed scope's
   `SemanticIdentityRelativePathsInOrder`.
4. Return `CollectionRowAddress(jsonScope, parentAddress, semanticIdentityInOrder)`.

### SemanticIdentityPart Reading

For each relative path in `SemanticIdentityRelativePathsInOrder`:

- Navigate the JSON item to that path.
- `IsPresent = true` if the property exists (even if its value is null).
- `IsPresent = false` if the property is missing entirely.
- `Value` = the `JsonNode` at that path (null when missing or explicit JSON null).

This preserves missing-vs-explicit-null semantics per step 3 of the normative algorithm.

### `_ext` Handling (Step 6)

Extension scopes participate as literal `JsonScope` segments but do not appear in any
scope's `CollectionAncestorsInOrder`. A collection-aligned `_ext` child collection has:

- `ImmediateParentJsonScope` pointing at the aligned base scope (e.g. `$`), not the
  `_ext` scope itself.
- `CollectionAncestorsInOrder` including the base collection if nested, but never the
  `_ext` scope.

The engine handles this naturally through the adapter data without special-casing.

### Request-Side vs Stored-Side (Step 7)

Both sides use the same `AddressDerivationEngine` instance, the same adapter, and the same
rules. The only difference is the JSON input:

- Request-side: `WritableRequestBody` (after canonicalization and writable-profile shaping).
- Stored-side: full current stored document before readable-profile projection.

Same scope + same JSON data produces identical addresses regardless of which side calls.

## File Layout

```
src/dms/core/EdFi.DataManagementService.Core/Profile/
    CompiledScopeTypes.cs      -- ScopeKind, CompiledScopeDescriptor
    AddressTypes.cs            -- ScopeInstanceAddress, CollectionRowAddress,
                                  AncestorCollectionInstance, SemanticIdentityPart,
                                  AncestorItemContext
    AddressDerivationEngine.cs -- Instance-based engine with pre-indexed catalog

src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/
    AddressDerivationEngineTests.cs -- All test fixtures
```

## Test Strategy

### Test-Only Adapter Factory

Hand-crafted `CompiledScopeDescriptor` lists in the test file, modeled on the delivery
plan's shared reference fixture (StudentSchoolAssociation-like shape).

### Test Fixture Scopes

| JsonScope | ScopeKind | Parent | CollectionAncestors | SemanticIdentity | Members |
|---|---|---|---|---|---|
| `$` | Root | null | [] | [] | [studentReference, schoolReference, entryDate, entryTypeDescriptor] |
| `$.calendarReference` | NonCollection | `$` | [] | [] | [calendarCode, calendarTypeDescriptor] |
| `$.classPeriods[*]` | Collection | `$` | [] | [classPeriodName] | [classPeriodName, officialAttendancePeriod] |
| `$.classPeriods[*].meetingTimes[*]` | Collection | `$.classPeriods[*]` | [$.classPeriods[*]] | [startTime, endTime] | [startTime, endTime] |
| `$._ext.sample` | NonCollection | `$` | [] | [] | [specialNote] |
| `$._ext.sample.extActivities[*]` | Collection | `$` | [] | [activityName] | [activityName, activityDate] |

### Test Cases

1. **Root scope** -- `$` produces `ScopeInstanceAddress("$", [])`.
2. **Root-adjacent 1:1 scope** -- `$.calendarReference` produces
   `ScopeInstanceAddress("$.calendarReference", [])`.
3. **Single-level collection** -- `$.classPeriods[*]` with item
   `{classPeriodName: "First"}` produces `CollectionRowAddress` with parent = root
   address, identity = `[(classPeriodName, "First", true)]`.
4. **Nested collection (two-level ancestor chain)** -- `$.classPeriods[*].meetingTimes[*]`
   produces `CollectionRowAddress` with parent having one ancestor (the classPeriods
   item), and its own semantic identity `[(startTime, ...), (endTime, ...)]`.
5. **`_ext` scope at root level** -- `$._ext.sample` produces
   `ScopeInstanceAddress("$._ext.sample", [])`.
6. **Collection-aligned `_ext` child collection** --
   `$._ext.sample.extActivities[*]` produces `CollectionRowAddress` with parent = root
   address (not `_ext` scope), empty ancestors.
7. **Request/stored alignment** -- same scope + same JSON data yields identical addresses.

### Test Style

Per project conventions: `[TestFixture]` classes with `Given_` prefix, `[SetUp]` for
arrange + act, `[Test]` methods with `It_` prefix.

## ProfileDefinition Compatibility Assessment

### Gap Analysis

The existing `ProfileDefinition` (in `Core/Profile/ProfileContext.cs`) and the
`CompiledScopeDescriptor` catalog operate at different levels of abstraction:

- `ProfileDefinition` uses a recursive tree of rules keyed by **member name** at each
  level (e.g. `CollectionRule.Name = "classPeriods"`).
- The adapter uses flat **compiled JsonScope paths** (e.g. `$.classPeriods[*]`).

### Bridge

Given a `CompiledScopeDescriptor` with `JsonScope = "$.classPeriods[*]"`, the member name
is derivable: strip the `[*]` suffix, take the last dot-separated segment ->
`"classPeriods"`. This maps directly to `CollectionRule.Name`.

Extension scopes follow the same pattern: `$._ext.sample` navigates to `ExtensionRule`
named `"sample"`.

### Assessment Per Downstream Story

| Story | ProfileDefinition Usage | Adaptation Needed |
|---|---|---|
| C2 (semantic identity validation) | Walk tree to collection scope, check if identity member is in include/exclude list. Member name from adapter's `SemanticIdentityRelativePathsInOrder` maps directly to `PropertyRule.Name`. | None |
| C3 (visibility classification) | Walk adapter catalog and `ProfileDefinition` tree in parallel. Adapter provides `JsonScope` vocabulary; profile provides include/exclude rules. | Small utility to correlate `JsonScope` paths with tree positions by member name extraction. |
| C4 (creatability) | Consumes C3 outputs + adapter. | None beyond C3. |
| C5 (orchestration) | Calls C2->C3->C4. | None. |
| C6 (stored-state projection) | Mirrors C3 for stored side. Same bridge. | Same utility as C3. |

### Conclusion

No structural changes to `ProfileDefinition` are needed. C3 will need a small utility to
correlate adapter `JsonScope` paths with `ProfileDefinition` tree positions by member name
extraction. This is a straightforward tree-walk helper, not a schema change. C3 should own
this utility.

## Scope Boundaries

### What C1 delivers

- `ScopeKind` enum
- `CompiledScopeDescriptor` record
- `ScopeInstanceAddress`, `CollectionRowAddress`, `AncestorCollectionInstance`,
  `SemanticIdentityPart`, `AncestorItemContext` records
- `AddressDerivationEngine` class
- Unit tests covering all 7 acceptance criteria test cases
- This design doc (including ProfileDefinition compatibility assessment)

### What C1 does NOT deliver

- Production adapter factory (backend's responsibility, DMS-1103 or prerequisite)
- `ProfileVisibilityKind`, `RequestScopeState`, `VisibleRequestCollectionItem`,
  `StoredScopeState`, `VisibleStoredCollectionRow`, `ProfileAppliedWriteRequest`,
  `ProfileAppliedWriteContext` (owned by C3/C5/C6)
- Profile error types (C8)
- JSON traversal/walking logic for documents (C3/C5/C6)
- `ProfileDefinition`-to-adapter correlation utility (C3)
