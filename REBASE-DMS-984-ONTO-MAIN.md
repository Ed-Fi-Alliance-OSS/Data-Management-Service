# Rebase Plan: DMS-984 Onto `main`

## Goal

Rebase branch `DMS-984` onto `main`, preserving:

1. the `DMS-984` relational write-path executor/session architecture,
2. `main`'s `DMS-991` reference-identity projection behavior,
3. `main`'s `DMS-1113` readable-profile projection behavior,
4. `main`'s `DMS-1106` request-level profile plumbing,

while intentionally **fencing profiled relational writes that reach backend repository orchestration with a profile write context** until `DMS-1123`, `DMS-1105`, and `DMS-1124` are implemented.

The temporary fence must return:

- `UnknownFailure`
- HTTP `500`
- clear message:
  `profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124`

## Confirmed Decisions

1. Do not try to make profiled relational writes partially work during this rebase.
2. Add a temporary fence for any relational write carrying a profile write context.
3. Use an explicit `UnknownFailure` / `500` with the exact pending-work message above.
4. Keep this work in a single agent context session because the production conflict resolution is tightly coupled.

## Design Basis

This rebase plan is driven by:

- `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md` (`DMS-984`)
- `reference/design/backend-redesign/epics/07-relational-write-path/01b-profile-write-context.md` (`DMS-1106`)
- `reference/design/backend-redesign/epics/08-relational-read-path/02-reference-identity-projection.md` (`DMS-991`)
- `reference/design/backend-redesign/epics/07-relational-write-path/01a-c7-readable-profile-projection.md` (`DMS-1113`)
- `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor.md` (`DMS-1124`)
- `reference/design/backend-redesign/epics/07-relational-write-path/01c-current-document-for-profile-projection.md` (`DMS-1105`)
- `reference/design/backend-redesign/epics/07-relational-write-path/02b-profile-applied-request-flattening.md` (`DMS-1123`)

## Key Constraints

1. `DMS-984` explicitly deferred profile-aware merge/no-op/hidden-data behavior to `DMS-1124`.
2. `DMS-1106` profile orchestration for update/upsert-to-existing depends on `DMS-1105` stored-document reconstitution, which is not available in the branch executor path.
3. `DMS-1123` request-body source selection is also not available in the branch executor path, so even profiled create-new writes are unsafe to allow through.
4. `main`'s profile tests were written against the pre-`DMS-984` repository seam and cannot be preserved verbatim after the rebase.
5. `main`'s read-path and pure profile-unit coverage for `DMS-991`, `DMS-1113`, and profile pipeline/contract logic should remain intact; the tests that must change are specifically the old relational write seam/orchestration/DI expectations tied to `IRelationalWriteTargetContextResolver` and `IRelationalWriteTerminalStage`.
6. The temporary fence is only correct if `DMS-1106` still constructs and carries the expected profile context through Core and backend boundaries, so validation must preserve the broader profile-contract surface, not just the final fenced `500`.

## Rebase Strategy

### Phase 1: Pre-Rebase Safety

1. Confirm current worktree state and commit stack before starting:
   - branch is `DMS-984`
   - inspect `git status --short`
   - inspect `git log --oneline main..HEAD`
   - make the worktree clean or intentionally stash any local WIP/docs before starting the rebase so they do not block the rebase or get mixed into conflict resolution accidentally
   - normalize the branch so the rebase input is the intended feature history, not local scratch/history noise
   - after normalization, expect one `DMS-984` feature commit plus only any explicitly intended follow-up commits
   - if additional intended follow-up commits must remain separate, list them explicitly before rebasing and replay them intentionally after the feature commit rebase
2. Create a safety branch before rebasing.
   - use a predictable name such as `dms-984-pre-rebase-safety`
   - keep it unchanged so it can be used later for required `git range-diff` and hotspot diff review after conflict resolution
3. If the branch contains local bookkeeping / scratch commits, squash or peel them off before starting the rebase; do not let them ride through conflict resolution accidentally.
4. Start the rebase onto `main` using the normalized branch history.

### Phase 2: Resolve Core Architectural Conflicts in Favor of the `DMS-984` Executor Model

Preserve the `DMS-984` architecture as the post-rebase write-path foundation:

- `src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteCurrentState.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNoProfileMerge.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNonCollectionPersistence.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteSession.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/SessionRelationalCommandExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/WritePlanBatchSqlEmitter.cs`
- dialect-specific write session factories

Conflict-resolution intent:

1. Keep the executor/session-based write orchestration rather than reverting to the old target-context-resolver plus terminal-stage seam.
2. Keep in-session POST target re-evaluation and guarded no-op handling from `DMS-984`.
3. Keep the new DI registrations for:
   - `IRelationalWriteExecutor`
   - `IRelationalWriteSessionFactory`
   - `IRelationalWriteCurrentStateLoader`
   - `IRelationalWriteNoProfileMergeSynthesizer`
   - `IRelationalWriteNonCollectionPersister`
   - `IRelationalWriteTargetLookupResolver`
4. Remove or adapt obsolete references to:
   - `IRelationalWriteTargetContextResolver`
   - `RelationalWriteTargetContextResolver`
   - `IRelationalWriteTerminalStage`
   - `DefaultRelationalWriteTerminalStage`

### Phase 3: Re-Thread `main`'s Profile Request Plumbing, But Not Runtime Profile Writes

Bring forward `main`'s request-level profile plumbing so the system still recognizes when a write is profile-governed:

- `src/dms/backend/EdFi.DataManagementService.Backend.External/BackendProfileWriteContext.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalWriteRequestContracts.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateRequest.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpsertRequest.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Handler/UpdateByIdHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Handler/UpsertHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWriteValidationMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ContentTypeScopeDiscovery.cs`

Conflict-resolution intent:

1. Preserve `main`'s `BackendProfileWriteContext` threading from middleware through request records to the repository boundary.
2. Preserve the middleware/service registration so profiled requests still arrive with the profile context attached.
3. Preserve immutable-identity handling added on the branch while also restoring the missing profile context argument where handlers construct write requests.
4. Preserve `main`'s relational-backend bypass in `ProfileWriteValidationMiddleware` so legacy profile filtering does not preempt the temporary fence with a `400`.
5. Preserve `ContentTypeScopeDiscovery` support used by `ProfileWritePipelineMiddleware` to build scope catalogs for inlined scopes.
6. Preserve profile contract and scope-catalog logic as types/pipeline behavior and pure unit coverage, but do not preserve repository-side profile guard-rail execution as the first observable runtime outcome for relational writes in this rebase.

### Phase 4: Add the Temporary Profile Fence

Implement a deliberate short-circuit for relational writes when `BackendProfileWriteContext` is present.

Placement:

1. Put the fence in repository orchestration immediately after relational request extraction and before any target resolution.
2. The fence should trigger for both:
   - POST / upsert
   - PUT / update
3. The fence should apply to all profiled relational writes, not just existing-document flows.
4. The fence is the first runtime outcome for profiled relational writes that reach relational repository orchestration; do not preserve repository-time profile contract validation, root-creatability enforcement, or stored-state projection as pre-fence behavior on that path.

Reasoning:

1. `DMS-1123` is not implemented, so the executor would flatten the wrong body for profiled writes.
2. `DMS-1105` is not implemented in the executor path, so existing-document profiled projection cannot work correctly.
3. `DMS-1124` is not implemented, so profile-aware merge/no-op/preservation rules do not exist in the executor.

Expected behavior:

1. Return `UnknownFailure` with message:
   `profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124`
2. Ensure the fence happens before:
   - `ResolveTargetContextAsync`,
   - immediate-result handling from target lookup,
   - executor execution,
   - flattening,
   - no-profile merge synthesis,
   - persistence.
3. For valid profiled relational writes that reach repository orchestration with a `BackendProfileWriteContext`, the observable runtime result should be the temporary fence rather than legacy relational-path profile filtering, target-lookup fast-path results, or old creatability timing behavior.
4. Preserve unit coverage for profile contract validation and pipeline construction separately, but align runtime repository and HTTP-path assertions to the fenced `500` behavior above.

Deliberate non-goals:

1. Do not implement `WritableRequestBody` selection.
2. Do not invoke stored-state projection.
3. Do not attempt partial support for profiled create-new writes.
4. Do not attempt profile-aware merge semantics.

### Phase 5: Preserve `DMS-991` Production Behavior

Bring forward and retain `main`'s reference-identity projection implementation:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReferenceIdentityProjector.cs`
- any related read-plan / hydration integration needed by `main`

Conflict-resolution intent:

1. Keep `main`'s production projector implementation.
2. Do not allow hydration or test conflict resolution to erase the feature.
3. Preserve or restore both classes of tests:
   - reference-identity projection coverage from `main`
   - transaction/session-reuse hydration coverage added on the branch

Expected test preservation:

- unit tests under `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- integration tests in `HydrationExecutorTests.cs`

### Phase 6: Preserve `DMS-1113` Readable Profile Projection

Keep `main`'s readable-profile projection behavior and tests intact.

Conflict-resolution intent:

1. Prefer `main` for readable-profile projector code unless a direct conflict requires adaptation.
2. Do not let write-path conflict resolution accidentally remove read-path profile registration or tests.
3. Treat `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs` as a shared `DMS-1106` / `DMS-1113` hotspot and preserve both:
   - `ProfileWritePipelineMiddleware` registration for the write path,
   - `IReadableProfileProjector` registration for the read path.

### Phase 7: Reconcile Test Surfaces With the Post-Rebase Architecture

This is a required cleanup phase, not optional polish.

#### A. Replace Obsolete Old-Seam Tests

`main` includes incoming tests and expectations written against the old pre-`DMS-984` seam, especially:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteOrchestrationTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/DefaultRelationalWriteTerminalStageTests.cs`
- incoming `main`-side old-seam expectations in `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Handler/RelationalWriteSeamTests.cs`
- incoming `main`-side old-seam expectations in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/RelationalWriteSmokeTests.cs`
- incoming DI expectations for target-context-resolver / terminal-stage registrations

Those incoming old-seam expectations must be rewritten or replaced to reflect the executor architecture. The branch's existing executor-era coverage in these same file paths should be preserved and extended rather than rewritten wholesale.

More specifically:

1. Remove or replace terminal-stage-specific tests; the rebased architecture should not preserve `DefaultRelationalWriteTerminalStage` as the execution seam.
2. When a conflicted file already has branch-side executor/session assertions, keep that foundation and port only the missing `main` intent that still matters.
3. Rewrite old orchestration/seam/smoke expectations so they assert the executor/session model rather than:
   - faking `IRelationalWriteTargetContextResolver`,
   - faking `IRelationalWriteTerminalStage`,
   - asserting terminal-stage requests as the final write seam.
4. Update `main`'s profile orchestration expectations to match the temporary fence:
   - do not preserve the old profiled POST creatability rejection / profiled PUT success assertions as runtime behavior for the rebased branch,
   - replace them with fence-focused assertions for profiled relational POST/PUT.
5. Update DI tests that currently expect:
   - `IRelationalWriteTargetContextResolver`,
   - `RelationalWriteTargetContextResolver`,
   - `IRelationalWriteTerminalStage`,
   - `DefaultRelationalWriteTerminalStage`.

Key files likely affected by this reconciliation include:

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteOrchestrationTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/DefaultRelationalWriteTerminalStageTests.cs`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Handler/RelationalWriteSeamTests.cs` for targeted adaptation, not wholesale rewrite
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/RelationalWriteSmokeTests.cs` for targeted adaptation, not wholesale rewrite
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ReferenceResolverServiceCollectionExtensionsTests.cs` for targeted adaptation, not wholesale rewrite
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/MssqlReferenceResolverServiceCollectionExtensionsTests.cs` for targeted adaptation, not wholesale rewrite
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/WebApplicationBuilderExtensionsTests.cs` for targeted adaptation, not wholesale rewrite

#### B. Add Fence-Focused Profile Tests

Add or adapt tests to prove:

1. repository-level profiled POST returns `UnknownFailure` with the exact pending-work message,
2. repository-level profiled PUT returns `UnknownFailure` with the exact pending-work message,
3. profiled relational writes are fenced before target lookup and executor execution,
4. handler or smoke-level profiled POST maps the backend `UnknownFailure` to HTTP `500` with the exact pending-work message,
5. handler or smoke-level profiled PUT maps the backend `UnknownFailure` to HTTP `500` with the exact pending-work message,
6. no-profile writes still route normally through the executor,
7. request plumbing from middleware/handlers still carries `BackendProfileWriteContext`,
8. the relational backend bypass in `ProfileWriteValidationMiddleware` remains intact so legacy profile-write filtering does not preempt the repository fence with a `400`,
9. `ProfileWritePipelineMiddleware` still produces `BackendProfileWriteContext` for valid writable-profile requests, so the fence is exercised through the intended orchestration path.

#### C. Preserve No-Profile Executor Coverage

Keep branch executor coverage intact for:

1. in-session POST re-evaluation,
2. guarded no-op behavior,
3. current-state loading,
4. session-scoped reference resolution,
5. non-collection persistence,
6. identity stability handling,
7. session reuse / write-session DI.

#### D. Preserve `main` Tests That Should Stay Valid

The rebase should preserve `main`'s test intent and coverage for:

1. `DMS-991` reference-identity projection tests:
   - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReferenceIdentityProjectorTests.cs`
   - both dialect `HydrationExecutorTests.cs`
   - shared hydration helpers
2. `DMS-1113` readable-profile projection tests:
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ReadableProfileProjectorTests.cs`
3. pure profile pipeline / contract / adapter / context-construction tests that are not coupled to the old write seam:
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileWritePipelineTests.cs`
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/ProfileWriteValidationMiddlewareTests.cs`
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/WritableRequestShaperTests.cs`
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/CreatabilityAnalyzerTests.cs`
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/StoredStateProjectorTests.cs`
   - `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/ProfileFailureTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/CompiledScopeAdapterFactoryTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteContractValidatorTests.cs`

### Phase 8: Reconcile Service Registration and Frontend/Core Smoke Tests

Update DI expectations across:

- backend service-collection tests,
- frontend service registration tests,
- relational smoke tests,
- core seam tests.

Conflict-resolution intent:

1. Ensure tests expect the executor/session registrations, not the old target-context-resolver + terminal-stage pair.
2. Preserve `main`'s profile middleware registration and ordering where applicable.
3. Keep no-profile routing green after the architecture merge.
4. Treat `DmsCoreServiceExtensions.cs` as the main registration checkpoint for both the temporary profile-write fence path and `DMS-1113` readable-profile projection so neither side is dropped during conflict resolution.

### Phase 9: Formatting and Compilation Hygiene

After conflict resolution:

1. run `dotnet csharpier format` on touched DMS directories/files,
2. fix compile issues caused by:
   - renamed interfaces,
   - restored profile types,
   - removed terminal-stage seam types,
   - test rewrites.

## File Areas Most Likely To Conflict

### Backend Production

- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteContracts.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/ReferenceResolverServiceCollectionExtensions.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/HydrationTestHelper.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/HydrationExecutorTests.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/HydrationExecutorTests.cs`

### Backend External / Profile Contracts

- `src/dms/backend/EdFi.DataManagementService.Backend.External/BackendProfileWriteContext.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalWriteRequestContracts.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.External/Profile/CompiledScopeAdapterFactory.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileWriteContractValidator.cs`

### Core Production

- `src/dms/core/EdFi.DataManagementService.Core/Handler/UpsertHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Handler/UpdateByIdHandler.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpsertRequest.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateRequest.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWriteValidationMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ContentTypeScopeDiscovery.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ReadableProfileProjector.cs`

### Test Surface

- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/*`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/*`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Handler/*`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/*`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/*`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/*`

## Validation Plan

### Minimum Targeted Validation

Run the smallest useful set first:

1. backend unit tests for executor/session/repository:
   - `EdFi.DataManagementService.Backend.Tests.Unit`
2. backend plans unit tests for reference projection:
   - `EdFi.DataManagementService.Backend.Plans.Tests.Unit`
3. core handler tests related to relational seam and write request construction:
   - `EdFi.DataManagementService.Core.Tests.Unit`
4. frontend DI/service registration smoke tests:
   - `EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`

Pay particular attention within those runs to:

- profile middleware and pipeline coverage that protects the temporary fence path
- handler/smoke coverage proving profiled POST and PUT surface the exact fenced `500` response
- profile contract / scope adapter tests from `DMS-1106`
- profile-context construction tests from `DMS-1106`, especially writable request shaping, creatability analysis, stored-state projection, and profile failure classification
- readable-profile projector coverage from `DMS-1113`
- reference-identity projector coverage from `DMS-991`

### Focused Integration Validation

Run targeted PostgreSQL integration tests covering:

1. hydration transaction reuse,
2. hydration reference-identity projection,
3. representative relational write smoke tests from the branch.

Also run targeted MSSQL hydration integration coverage for `DMS-991`, because reference-identity projection was added for both dialects and should not be validated on PostgreSQL alone.

### Compile-Level Validation

At minimum, ensure these projects compile cleanly after the rebase:

1. backend production projects,
2. backend plans projects,
3. core,
4. frontend,
5. impacted unit test projects,
6. impacted integration test projects.

### Semantic Diff Validation

Before calling the rebase complete, compare the rebased branch to the pre-rebase safety branch deliberately rather than trusting build/test signal alone.

1. Run `git range-diff main...dms-984-pre-rebase-safety main...HEAD` or the equivalent against the actual safety-branch name.
2. Review the range-diff for silent drops or rewrites of:
   - `DMS-984` executor/session architecture,
   - `main`'s `DMS-991` reference-identity projector,
   - `main`'s `DMS-1113` readable-profile projector,
   - `main`'s `DMS-1106` request-contract and profile-context plumbing.
3. If the range-diff is noisy around conflict-heavy files, run targeted `git diff` comparisons against the safety branch for the main hotspots:
   - `RelationalDocumentStoreRepository.cs`
   - `DefaultRelationalWriteExecutor.cs`
   - `RelationalWriteContracts.cs`
   - `UpsertHandler.cs`
   - `UpdateByIdHandler.cs`
   - `DmsCoreServiceExtensions.cs`
4. Treat this semantic comparison as required validation, not optional inspection; tests alone are not sufficient for this rebase.

## Success Criteria

The rebase is complete when all of the following are true:

1. `DMS-984` remains the active relational write architecture.
2. `main`'s `DMS-991` production behavior is preserved.
3. `main`'s `DMS-1113` production behavior is preserved.
4. `main`'s profile request plumbing is restored through middleware, request records, and handlers.
5. Any valid profiled relational write that reaches repository orchestration with a `BackendProfileWriteContext` is fenced with:
   - `UnknownFailure`
   - HTTP `500`
   - `profile-aware relational writes pending DMS-1123/DMS-1105/DMS-1124`
6. No-profile relational writes continue to function through the executor path.
7. Incoming `main` old-seam expectations are removed or rewritten to match the executor architecture, while branch executor-era coverage is preserved and extended.
8. Targeted unit and integration validation passes.

## Explicit Non-Goals for This Rebase

1. Implement `DMS-1123` request-body source selection.
2. Implement `DMS-1105` stored-document reconstitution for profile projection.
3. Implement `DMS-1124` profile-aware merge, preservation, or profiled guarded no-op.
4. Make profiled relational writes partially supported.

## Execution Order Summary

1. Rebase branch onto `main`.
2. Resolve production architecture conflicts in favor of the `DMS-984` executor/session model.
3. Restore `main`'s profile request plumbing through core and backend request boundaries.
4. Add the explicit temporary fence for profiled relational writes that reach repository orchestration with a `BackendProfileWriteContext`.
5. Preserve `main`'s `DMS-991` production implementation and restore lost coverage.
6. Preserve `main`'s `DMS-1113` read-path profile behavior.
7. Rewrite outdated old-seam tests and add fence-focused tests.
8. Format touched code.
9. Run targeted validation and fix fallout.
