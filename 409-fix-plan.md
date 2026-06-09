# Relational Write Precedence 409 Fix Plan

## Goal

Change the relational write executor so resolver-produced missing document-reference failures surface as `409 Unresolved Reference` only after higher-precedence write failures have had a chance to win.

The target precedence is the legacy ODS write-path behavior:

1. Resource/data-annotation validation.
2. Descriptor existence validation.
3. Unsupported identity/key-change checks.
4. Authorization.
5. Document-reference/FK unresolved-reference failures.

Resource/data-annotation validation already happens before the relational backend executor receives the request. This plan changes only the relational backend ordering after request validation has succeeded.

## Current Problem

`DefaultRelationalWriteExecutor` currently resolves request references before merge, immutable identity checks, and proposed-value authorization. If the resolver reports any failure, the executor immediately returns `BuildReferenceFailureResult`.

That makes missing document references beat:

- immutable identity/key-change checks,
- proposed namespace authorization,
- proposed relationship authorization.

Those early `409`s are not ODS-shaped. ODS reaches unresolved FK/reference failures only after validation, identity checks, and authorization have passed and the database write is attempted.

## Scope

Keep this narrowly scoped.

In scope:

- Defer only `DocumentReferenceFailureReason.Missing` failures produced by the resolver.
- Keep descriptor-reference failures immediate.
- Keep non-missing document-reference failures, such as incompatible target type, immediate.
- Keep profile writes on the current immediate reference-failure path for this fix.
- Allow only the no-profile merge/proposed-authorization preflight to continue with explicitly deferred missing document references.
- Return the remembered missing document-reference failures as `409` only if no higher-precedence result occurs.
- Preserve database FK mapping as the final race-condition fallback.
- Flip only E2E scenarios whose relational behavior now matches ODS precedence.

Out of scope:

- ProblemDetails wording changes.
- Profile data-policy behavior.
- Numeric/date serialization.
- General reference resolver semantics.
- Core response/status mapping.
- Any fallback that hides unrelated flattener, mapping, or planner bugs.
- Reordering the existing stored-target authorization/locking boundary.

## Executor Flow

The existing stored-target authorization/locking boundary at the start of `DefaultRelationalWriteExecutor` should remain where it is. This fix applies after that boundary, where proposed-value writes currently lose to early missing document-reference checks.

Target flow:

```text
stored relationship/namespace authorization boundary
If-Match checks that must happen before proposed authorization
resolve request references

if descriptor-reference failures exist:
    return current reference failure result

if non-missing document-reference failures exist:
    return current reference failure result

if profile write and missing document-reference failures exist:
    return current reference failure result

remember missing document-reference failures, but do not return yet

resolve execution state/current state
merge selected body with current state
run immutable identity/key-change check
run proposed namespace authorization
run proposed relationship authorization
run deferred If-Match precondition evaluation in its existing location, when applicable

if remembered missing document-reference failures exist:
    return 409 Unresolved Reference

guarded no-op handling
persist
read committed representation
commit

on database FK/reference exception:
    map to unresolved reference as today
```

Descriptor failures still win as validation failures. Missing document references become the last backend-level pre-persist failure among these cases.

## Implementation

### 1. Split Reference Failure Handling

After `ReferenceResolver.ResolveAsync`, replace the current `resolvedReferences.HasFailures` branch with explicit checks.

Suggested helpers:

```csharp
private static bool HasDescriptorReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDescriptorReferences.Count > 0;

private static bool HasNonMissingDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDocumentReferences.Any(static failure =>
        failure.Reason is not DocumentReferenceFailureReason.Missing
    );

private static bool HasMissingDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDocumentReferences.Any(static failure =>
        failure.Reason is DocumentReferenceFailureReason.Missing
    );
```

Immediate return:

```csharp
var hasMissingDocumentReferenceFailures = HasMissingDocumentReferenceFailures(resolvedReferences);

if (
    HasDescriptorReferenceFailures(resolvedReferences)
    || HasNonMissingDocumentReferenceFailures(resolvedReferences)
    || (executionRequest.ProfileWriteContext is not null && hasMissingDocumentReferenceFailures)
)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}

var deferMissingDocumentReferenceFailures =
    executionRequest.ProfileWriteContext is null && hasMissingDocumentReferenceFailures;
```

Do not use `ResolvedReferenceSet.HasFailures` for executor precedence decisions because it conflates descriptor and document-reference failures.

If descriptor and document-reference failures are both present, returning the combined reference result immediately is acceptable for this fix. Core maps mixed descriptor/document reference failures to `400 Bad Request`, preserving descriptor validation precedence by status.

### 2. Let No-Profile Merge Run Only for Deferred Missing Document References

The flattener currently fails when a submitted document reference has no resolved document id. That fail-fast behavior should remain the default.

Add the smallest explicit opt-in:

- Add a boolean such as `AllowMissingDocumentReferencesForPrecedence` to `FlatteningInput`, defaulting to `false`.
- Add an optional boolean parameter to `RelationalWriteMergeOrchestrator.Resolve`, defaulting to `false`.
- Pass `AllowMissingDocumentReferencesForPrecedence: true` only from `DefaultRelationalWriteExecutor` when `deferMissingDocumentReferenceFailures` is true.
- Apply the opt-in only to the no-profile flattening path. Profile writes stay on the immediate reference-failure path.

When the opt-in is true, derive the exact tolerated occurrences from `resolvedReferences.InvalidDocumentReferences` where `Reason` is `Missing`. Do not let callers supply arbitrary tolerated paths, and do not fabricate successful resolved references.

The simplest responsible implementation is for `FlatteningResolvedReferenceLookupSet` to derive this internal lookup from the existing `ResolvedReferenceSet` plus the opt-in flag. It can map resolver failure paths to the compiled document-reference binding index and ordinal path, then expose a narrow method such as:

```csharp
public bool IsDeferredMissingDocumentReference(
    int bindingIndex,
    ReadOnlySpan<int> ordinalPath
)
```

Treat an unresolved failure path that cannot be matched to a compiled document-reference binding as an internal invariant failure.

In `RelationalWriteFlattener`:

- In `ResolveDocumentReferenceValue`, if the reference object exists but no document id is resolved, return `FlattenedWriteValue.Literal(null)` only when that exact binding/ordinal occurrence is marked as a deferred missing document reference. Otherwise keep the current exception.
- In `ResolveReferenceDerivedValue`, after confirming the reference object exists, apply the same exact-occurrence check using `referenceDerived.ReferenceSource.BindingIndex` and the current ordinal path before calling the literal resolver that would throw for a missing backing reference lookup. Return `FlattenedWriteValue.Literal(null)` only for an explicitly deferred missing occurrence.
- Do not catch and remap unrelated `RelationalWriteRequestValidationException` or `InvalidOperationException` failures. If a request-shape or mapping error still occurs, keep the existing failure behavior unless a unit test proves it is directly caused by the allowed deferred missing-reference occurrence.

The executor must not persist or return guarded no-op success while deferred missing document-reference failures are remembered.

### 3. Keep Identity Stability Before Proposed Authorization

Leave the existing immutable identity check after merge and before proposed authorization:

```csharp
var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
    executionRequest,
    mergeResult
);
```

This lets key-change failures beat missing document references.

### 4. Run Proposed Authorization Before Deferred 409

Keep proposed namespace authorization before proposed relationship authorization.

For proposed relationship authorization, add only the smallest hook needed for create-new POSTs:

- Add an optional boolean parameter such as `forceStandaloneAuthorization` to `ProposedRelationshipAuthorizationOrchestrator.ResolveAsync`, defaulting to `false`.
- Pass `forceStandaloneAuthorization: deferMissingDocumentReferenceFailures`.
- In `AuthorizeAsync`, ignore `IsHandledByPostInlineAuth1(request)` only when `forceStandaloneAuthorization` is true.

This is necessary because create-new POSTs normally combine Auth1 with the insert command. When deferred missing document references exist, the executor must return before insert/persist, so inline Auth1 would never run.

Successful create-new POSTs without deferred missing document references should keep the existing inline Auth1 behavior.

### 5. Return Deferred Missing References Before Guarded No-Op or Persist

After proposed authorization and the existing deferred `If-Match` block, emit the remembered reference failure:

```csharp
if (deferMissingDocumentReferenceFailures)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}
```

Do this before guarded no-op handling and before persistence.

### 6. Preserve Database FK Mapping

Do not remove or weaken `RelationalWriteDatabaseFailureResultMapper`.

It remains the final safety net when:

- a document reference resolved successfully,
- validation and authorization passed,
- the target row disappeared before persistence,
- the database raises an FK violation.

Those failures should still map to `409 Unresolved Reference`.

## Tests

Add focused backend unit coverage first. Keep the tests small and explicit.

Minimum coverage:

1. Descriptor-reference failure remains immediate and is not deferred behind missing document references.
2. Non-missing document-reference failure, such as incompatible target type, remains immediate.
3. Profile write with missing document reference keeps the current immediate reference-failure behavior.
4. Immutable identity/key-change failure beats a missing document-reference failure.
5. Proposed relationship authorization failure beats a missing document-reference failure.
6. Create-new POST proposed relationship authorization runs standalone before the deferred reference return.
7. Proposed namespace authorization failure beats a missing document-reference failure.
8. Missing document-reference failure returns reference failure when no higher-precedence failure occurs.
9. Deferred missing-reference tolerance is exact to resolver-produced binding/ordinal occurrences; normal flattener mode still fails fast.
10. `PersistAsync` is not called and guarded no-op success is not returned when deferred missing document-reference failures are remembered.

Core HTTP status mapping already has separate coverage for backend reference result types. Executor tests should assert backend result types, not duplicate Core handler status mapping unless a new handler behavior is introduced.

## E2E Scenarios

After unit tests pass, flip only scenarios whose relational behavior now matches ODS precedence.

Expected to pass after executor implementation:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/References/UpdateReferenceValidation.feature`
  - scenario 04 should return `400 Key Change Not Supported` instead of early `409`.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndContacts.feature`
  - scenario 20 should return `403 Authorization Denied` instead of early `409`.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndPeople.feature`
  - scenario 32 should return the ODS-shaped authorization/precedence result.
  - scenario 39 should return `403 Authorization Denied` instead of early `409`.
  - scenario 40 should return `403 Authorization Denied` instead of early `409`.

Already match ODS precedence relationally and can be flipped with expectation changes:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndStaffs.feature`
  - scenario 03: key-change beats update authorization.
  - scenario 15: key-change beats update authorization.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/References/UpdateReferenceValidation.feature`
  - scenario 03: descriptor validation beats unresolved document reference.

## Validation Commands

Format changed C# files:

```bash
dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit
```

Run backend unit tests around the changed executor/flattener:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~DefaultRelationalWriteExecutor|FullyQualifiedName~RelationalWriteFlattener"
```

Build E2E tests:

```bash
dotnet build src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj -c Release
```

Run relational E2E shards containing the flipped scenarios from the repository root:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-3'
```

Run shard 2 if descriptor/reference scenarios are tagged there:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-2'
```

Also run:

```bash
git diff --check
```

## Acceptance Criteria

- Descriptor-reference failures still produce early validation failure behavior.
- Only missing document-reference failures are deferred.
- Profile writes keep current immediate reference-failure behavior.
- Deferred missing-reference tolerance is enabled only by an explicit no-profile flattening opt-in.
- Deferred missing-reference tolerance is exact to resolver-produced missing document-reference occurrences.
- Missing document-reference failures no longer beat immutable identity/key-change failures.
- Missing document-reference failures no longer beat proposed namespace, proposed relationship authorization, or existing deferred `If-Match` failures.
- Create-new POST proposed relationship authorization runs before the deferred missing-reference return.
- Missing document-reference failures still produce `409 Unresolved Reference` when no higher-precedence failure exists.
- No guarded no-op success or persistence occurs while deferred missing document-reference failures are remembered.
- Database FK failures after authorization still map to `409`.
- Affected E2E scenarios are flipped with explicit expected statuses/bodies, not fallback assertions.
