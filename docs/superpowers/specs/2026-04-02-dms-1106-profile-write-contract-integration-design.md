# DMS-1106: Integrate the Core/Backend Profile Write Contract

## Overview

This story bridges Core's fully-implemented profile write pipeline (C1-C8) with the backend's relational write path. It threads optional `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext` through write-path orchestration boundaries, enforces root creatability for profiled POST requests, validates Core-emitted addresses against compiled metadata, and enables deferred stored-state projection when the repository has a stored document in hand.

**Approach:** Strict story boundary. No behavioral changes to flattening or persistence — downstream stories (DMS-1123 body-source selection, DMS-1124 profile-aware persist executor) own those. This story makes the contracts available and enforceable at orchestration boundaries.

## Dependencies

All predecessors are complete:
- C1 (DMS-1111): Compiled-scope adapter contract + address derivation engine
- C5 (DMS-1117): ProfileAppliedWriteRequest assembly pipeline
- C6 (DMS-1118): Stored-state projection + HiddenMemberPaths computation
- C8 (DMS-1112): Typed profile error classification
- DMS-1108: Stable-identity collection merge plans (SemanticIdentityBindings)
- DMS-983: Relational write flattener

## Architecture Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Profile pipeline invocation point | New middleware (`ProfileWritePipelineMiddleware`) | Follows established pattern (like `ResolveMappingSetMiddleware`); keeps Core/backend boundary clean |
| Stored-state projection timing | Deferred to repository | Repository already loads the stored document; avoids double-load; respects DMS-1105 boundary |
| Adapter factory structure | Dedicated static class in Backend | Pure function, independently testable, doesn't pollute write-plan types |
| Profile contract threading | Single composite type (`BackendProfileWriteContext`) | Groups related data (request contract + scope catalog + projector); cleaner than multiple nullable fields |
| Deferred projection interface | `IStoredStateProjectionInvoker` interface | Explicit contract, mockable in tests, cleaner than captured closures |
| Contract validation | Dedicated `ProfileWriteContractValidator` class | Well-defined responsibility, independently testable against category-5 failure types |

## New Types and Interfaces

### `CompiledScopeAdapterFactory` (Backend)

**File:** `src/dms/backend/EdFi.DataManagementService.Backend/Profile/CompiledScopeAdapterFactory.cs`

Static class with a pure function that builds a `CompiledScopeDescriptor[]` catalog from `ResourceWritePlan` metadata.

```csharp
public static class CompiledScopeAdapterFactory
{
    public static IReadOnlyList<CompiledScopeDescriptor> BuildFromWritePlan(
        ResourceWritePlan writePlan)
    {
        // Walk writePlan.TablesInDependencyOrder
        // For each DbTableModel:
        //   JsonScope         ← DbTableModel.JsonScope.Canonical
        //   ScopeKind         ← TableKind mapping (Root→Root, Collection/ExtensionCollection→Collection, else→NonCollection)
        //   ImmediateParent   ← parent table's JsonScope
        //   CollectionAncestors ← walk up hierarchy collecting collection scopes
        //   SemanticIdentity  ← CollectionMergePlan.SemanticIdentityBindings[*].RelativePath
        //   CanonicalMembers  ← table column metadata
    }
}
```

**TableKind → ScopeKind mapping:**
- `Root` → `ScopeKind.Root`
- `Collection`, `ExtensionCollection` → `ScopeKind.Collection`
- All others (`RootExtension`, `NonCollection`, etc.) → `ScopeKind.NonCollection`

### `BackendProfileWriteContext` (Backend.External)

**File:** `src/dms/backend/EdFi.DataManagementService.Backend.External/BackendProfileWriteContext.cs`

Composite type carried on write requests, bundling all profile data needed by the repository:

```csharp
public sealed record BackendProfileWriteContext(
    ProfileAppliedWriteRequest Request,
    IReadOnlyList<CompiledScopeDescriptor> CompiledScopeCatalog,
    IStoredStateProjectionInvoker StoredStateProjectionInvoker
);
```

### `IStoredStateProjectionInvoker` (Backend.External)

**File:** Same file as `BackendProfileWriteContext` or adjacent.

```csharp
public interface IStoredStateProjectionInvoker
{
    ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog);
}
```

Middleware provides an implementation that captures `ProfileVisibilityClassifier` and `StoredSideExistenceLookupBuilder` from the pipeline's request-side execution. The three parameters on the interface are what only the repository can supply at call time: the stored document (loaded during target context resolution), the request contract (for composing the full context), and the scope catalog (for address derivation). The repository calls this when the stored document is available during update/upsert-to-existing flows.

### `ProfileWriteContractValidator` (Backend)

**File:** `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileWriteContractValidator.cs`

Static class that validates Core-emitted addresses against compiled metadata:

```csharp
public static class ProfileWriteContractValidator
{
    public static ProfileFailure[] ValidateRequestContract(
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog)
    { /* validate JsonScopes, ancestor chains, semantic identity ordering */ }

    public static ProfileFailure[] ValidateWriteContext(
        ProfileAppliedWriteContext context,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog)
    { /* validate stored-side scope/row addresses against compiled metadata */ }
}
```

Emits category-5 `ProfileFailure` types:
- `UnknownJsonScopeCoreBackendContractMismatchFailure`
- `AncestorChainMismatchCoreBackendContractMismatchFailure`
- `CanonicalMemberPathMismatchCoreBackendContractMismatchFailure`
- `UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure`

## Middleware Integration

### `ProfileWritePipelineMiddleware` (Core)

**File:** `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`

Runs after `ResolveMappingSetMiddleware`, before `UpsertHandler`/`UpdateByIdHandler`.

**Flow:**
1. Short-circuit if not POST/PUT, or if `MappingSet` is null (non-relational path)
2. Resolve `ResourceWritePlan` from `MappingSet.WritePlansByResource`
3. Build `CompiledScopeDescriptor[]` via `CompiledScopeAdapterFactory.BuildFromWritePlan()`
4. Invoke `ProfileWritePipeline.Execute()` with scope catalog, request body, profile content type, operation kind
5. If failures → map to `FrontendResponse` (400/403/409 by category) and short-circuit
6. If no-profile → leave `RequestInfo.BackendProfileWriteContext` null, continue
7. If profile results → construct `BackendProfileWriteContext` with:
   - `ProfileAppliedWriteRequest` from pipeline result
   - The scope catalog
   - An `IStoredStateProjectionInvoker` implementation capturing C6 projection dependencies
8. Attach to `RequestInfo.BackendProfileWriteContext`

**Legacy `ProfileWriteValidationMiddleware`** continues operating for non-relational paths. No removal needed.

### `RequestInfo` Changes

Add one property:

```csharp
public BackendProfileWriteContext? BackendProfileWriteContext { get; set; }
```

## Request Threading

### Record Changes

**`UpdateRequest`** — add optional parameter:

```csharp
BackendProfileWriteContext? BackendProfileWriteContext = null
```

`UpsertRequest` inherits it. `UpsertHandler` populates from `RequestInfo.BackendProfileWriteContext`.

### Interface Extension

**`IRelationalWriteRequest`** — add:

```csharp
BackendProfileWriteContext? BackendProfileWriteContext { get; }
```

## Repository Integration

### `RelationalDocumentStoreRepository.ExecuteWriteGuardRails`

After resolving write plan and target context, before flattening:

**Step 1 — Root creatability guard (POST only):**
```
if POST && BackendProfileWriteContext != null && !Request.RootResourceCreatable
  → return category-4 creatability violation failure
```

**Step 2 — Validate request-side contract:**
```
if BackendProfileWriteContext != null
  → ProfileWriteContractValidator.ValidateRequestContract(request, scopeCatalog)
  → if failures, return validation failure
```

**Step 3 — Stored-state projection (update/upsert-to-existing only):**
```
if BackendProfileWriteContext != null && targetContext is ExistingDocument
  → load stored document (DMS-1105 formalizes this; for now use available document)
  → StoredStateProjectionInvoker.ProjectStoredState(storedDoc, request, catalog)
  → ProfileWriteContractValidator.ValidateWriteContext(context, scopeCatalog)
  → if failures, return validation failure
  → store ProfileAppliedWriteContext for downstream
```

**Step 4 — Flattening:**
`SelectedBody` remains the normal `EdfiDoc`. DMS-1123 switches to `WritableRequestBody`.

**Step 5 — Terminal stage threading:**
`RelationalWriteTerminalStageRequest` gains an optional `ProfileAppliedWriteContext?` field so DMS-1124 can access hidden-member metadata. DMS-1106 adds the field; DMS-1124 consumes it.

### No-Profile Path

When `BackendProfileWriteContext` is null, all profile steps are skipped. Write path behaves exactly as today. No fabricated "all visible" contract.

## Testing Strategy

Tests in `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/`, following project convention (`Given_` fixtures, `It_` test methods).

### Repository/Orchestration Tests

**`Given_NoProfileWriteBehavior`**
- `It_should_skip_all_profile_processing` — no creatability check, no contract validation, no projector invocation
- `It_should_pass_normal_request_body_to_flattening` — `EdfiDoc` used as `SelectedBody`

**`Given_ProfileRootCreateRejectedWhenNonCreatable`**
- `It_should_reject_before_persistence` — category-4 creatability violation
- `It_should_not_invoke_flattening_or_terminal_stage`

**`Given_ProfileScopedUpdateWithStoredStateProjection`**
- `It_should_invoke_stored_state_projector_with_stored_document` — projector called with correct args
- `It_should_validate_write_context_contract` — validator runs on returned context

**`Given_ProfileVisibleRowUpdateWithHiddenRowPreservation`**
- `It_should_thread_hidden_member_paths_to_terminal_stage` — `ProfileAppliedWriteContext` with hidden-member metadata reaches terminal stage request

**`Given_ProfileVisibleButAbsentNonCollectionScope`** (with hidden preservation)
- `It_should_carry_visible_absent_scope_state` — scope state threaded through
- `It_should_carry_hidden_member_paths_for_preservation` — `HiddenMemberPaths` available to downstream

**`Given_ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`**
- `It_should_allow_update_of_existing_visible_item` — existing item update not blocked
- `It_should_reject_insert_of_new_noncreatable_item` — verifies contract carries Core's creatability decision, including three-level parent-create-denied/child-denied chain

**`Given_ContractMismatch_UnknownJsonScope`**
- `It_should_emit_category5_unknown_jsonscope_failure`

### Component Tests

**`CompiledScopeAdapterFactoryTests`**
- Verify correct translation from `ResourceWritePlan`/`DbTableModel` to `CompiledScopeDescriptor[]` for root, non-collection, collection, and extension scopes
- Verify `SemanticIdentityRelativePathsInOrder` sourced from `CollectionMergePlan.SemanticIdentityBindings`
- Verify `CanonicalScopeRelativeMemberPaths` derived from table column metadata

**`ProfileWriteContractValidatorTests`**
- `Given_UnknownJsonScope` → `UnknownJsonScopeCoreBackendContractMismatchFailure`
- `Given_AncestorChainMismatch` → `AncestorChainMismatchCoreBackendContractMismatchFailure`
- `Given_CanonicalMemberPathMismatch` → `CanonicalMemberPathMismatchCoreBackendContractMismatchFailure`
- `Given_UnalignableStoredVisibilityMetadata` → `UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure`
- `Given_ValidContract` → no failures

## Ownership Boundaries

### This story owns:
- Adapter factory (backend → scope catalog)
- Middleware invocation of `ProfileWritePipeline`
- Composite type + projector interface
- Contract validation
- Repository threading + root creatability guard
- Terminal stage request field for profile context

### This story does NOT own:
- Body-source selection (`WritableRequestBody` vs `EdfiDoc`) → DMS-1123
- Profile-aware merge/persist execution → DMS-1124
- Stored document loading formalization → DMS-1105
- Profile member filtering, value predicates, readable projection → Core (C1-C8)

## Files Changed/Created

| File | Action | Project |
|---|---|---|
| `Backend/Profile/CompiledScopeAdapterFactory.cs` | Create | Backend |
| `Backend.External/BackendProfileWriteContext.cs` | Create | Backend.External |
| `Backend/Profile/ProfileWriteContractValidator.cs` | Create | Backend |
| `Core/Middleware/ProfileWritePipelineMiddleware.cs` | Create | Core |
| `Core/Pipeline/RequestInfo.cs` | Modify — add `BackendProfileWriteContext?` | Core |
| `Core/Backend/UpdateRequest.cs` | Modify — add `BackendProfileWriteContext?` param | Core |
| `Core/Backend/UpsertRequest.cs` | Modify — add inherited param | Core |
| `Core/Handler/UpsertHandler.cs` | Modify — pass profile context to request | Core |
| `Backend.External/RelationalWriteRequestContracts.cs` | Modify — add property to interface | Backend.External |
| `Backend/RelationalDocumentStoreRepository.cs` | Modify — profile steps in orchestration | Backend |
| `Backend/RelationalWriteContracts.cs` | Modify — add `ProfileAppliedWriteContext?` to terminal stage request | Backend |
| `Backend.Tests.Unit/Profile/*.cs` | Create — all test classes | Backend.Tests.Unit |
