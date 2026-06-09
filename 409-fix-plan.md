# Relational Write Precedence 409 Fix Plan

## Goal

Change the relational write executor so missing document-reference failures surface as `409 Unresolved Reference` only after validation, immutable identity checks, and proposed-value authorization have had a chance to produce the same higher-precedence result as legacy ODS.

This plan addresses the validation/authorization precedence failures where relational currently returns an early `409` because request references are resolved before merge, identity stability, or proposed authorization.

## Current Behavior

The current relational write executor resolves every request reference early:

- `DefaultRelationalWriteExecutor.cs` resolves references before normal execution-state resolution, merge, identity checks, and proposed authorization.
- If any reference resolution failure exists, the executor immediately returns `BuildReferenceFailureResult`.
- Core maps document-only reference failures to `409 Unresolved Reference`.

That means missing document references can beat:

- immutable identity/key-change checks,
- proposed namespace authorization,
- proposed relationship authorization.

This is not the legacy ODS precedence. In ODS, unresolved FK/reference errors are reached only after validation, identity/key-change checks, and authorization have passed and the database write is attempted.

## Desired Precedence

The relational write path should follow this order:

1. Resource/data-annotation validation.
2. Descriptor existence validation.
3. Unsupported identity/key-change checks.
4. Authorization.
5. Document-reference/FK unresolved-reference failures.

Descriptor failures should remain early `400 Bad Request` validation failures. Missing document references should be deferred until after identity and proposed authorization.

## Scope

This change should be narrowly scoped to missing document-reference precedence. It should not introduce broad fallback behavior or relax E2E assertions.

In scope:

- Defer only missing document-reference failures inside `DefaultRelationalWriteExecutor`.
- Keep descriptor-reference failures immediate.
- Keep non-missing document-reference failures, such as incompatible target type, on the current immediate path.
- Keep profile writes on the current immediate reference-failure path for this change.
- Allow no-profile merge/proposed-auth preflight to proceed when document references are missing.
- Return the remembered missing document-reference failures as `409` only if no higher-precedence result occurs.
- Preserve database FK mapping as a final race-condition fallback.
- Add focused unit coverage.
- Flip only scenarios whose expected relational behavior now matches ODS precedence.

Out of scope:

- Changing ProblemDetails wording.
- Changing profile data-policy behavior.
- Normalizing numeric/date serialization.
- Broadly changing reference resolver semantics.
- Hiding missing references during actual persistence.

## Proposed Executor Flow

Update `DefaultRelationalWriteExecutor.ExecuteAsyncInternal` to distinguish descriptor-reference failures from document-reference failures.

Target flow:

```text
stored relationship/namespace authorization boundary
If-Match checks that must happen before proposed authorization
resolve request references

if descriptor-reference failures or non-missing document-reference failures exist:
    return current reference failure result

if profile write and missing document-reference failures exist:
    return current reference failure result

remember missing document-reference failures, but do not return yet

resolve execution state/current state
merge selected body with current state
run immutable identity/key-change check
run proposed namespace authorization
run proposed relationship authorization
run deferred If-Match precondition evaluation, when applicable

if remembered missing document-reference failures exist:
    return 409 Unresolved Reference

guarded no-op handling
persist
read committed representation
commit

on database FK/reference exception:
    map to unresolved reference as today
```

The key behavior change is moving the missing document-reference `BuildReferenceFailureResult` until after proposed authorization and deferred `If-Match` evaluation.

## Implementation Details

### 1. Add Reference Failure Helpers

Add small helper methods near the executor or `RelationalWriteExecutorResults` to keep the branch explicit:

```csharp
private static bool HasDescriptorReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDescriptorReferences.Count > 0;

private static bool HasImmediateDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDocumentReferences.Any(static failure =>
        failure.Reason is not DocumentReferenceFailureReason.Missing
    );

private static bool HasDeferredMissingDocumentReferenceFailures(
    ResolvedReferenceSet resolvedReferences
) =>
    resolvedReferences.InvalidDocumentReferences.Any(static failure =>
        failure.Reason is DocumentReferenceFailureReason.Missing
    );
```

Do not use `ResolvedReferenceSet.HasFailures` for precedence decisions in the executor because it conflates descriptor and document failures.

### 2. Return Immediate Reference Failures Immediately

After `ReferenceResolver.ResolveAsync`, replace the current `HasFailures` branch with scoped behavior:

```csharp
if (
    HasDescriptorReferenceFailures(resolvedReferences)
    || HasImmediateDocumentReferenceFailures(resolvedReferences)
    || (
        request.ProfileWriteContext is not null
        && HasDeferredMissingDocumentReferenceFailures(resolvedReferences)
    )
)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}
```

This preserves the legacy ODS rule that descriptor validation beats unresolved document references, avoids changing incompatible-target behavior, and keeps profile write behavior out of scope.

If both descriptor and document failures are present, returning the combined reference result is intentional for this change. Core maps mixed descriptor/document reference failures to `400 Bad Request`, preserving validation precedence by status. The body may include both descriptor and document-reference entries; do not flip exact E2E expectations for mixed-failure bodies unless that body difference is explicitly accepted.

### 3. Defer Missing Document Failures

Keep the resolved reference set and continue:

```csharp
var hasDeferredMissingDocumentReferenceFailures =
    request.ProfileWriteContext is null
    && HasDeferredMissingDocumentReferenceFailures(resolvedReferences);
```

Do not return yet. The deferred result should be emitted only after identity stability, proposed authorization, and any deferred `If-Match` precondition evaluation complete.

### 4. Let Merge Run With Deferred Missing Document References

This is the only part that needs careful implementation because the flattener currently expects every submitted document reference to have a resolved document id.

Preferred implementation:

- Add an explicit option to merge/flatten for this preflight case, for example:
  - `RelationalWriteMergeOptions.AllowMissingDocumentReferenceValuesForPrecedenceCheck`, or
  - a boolean on `FlatteningInput`, such as `AllowDeferredDocumentReferenceFailures`.
- Use that option only when `hasDeferredMissingDocumentReferenceFailures` is true.
- In `RelationalWriteFlattener.ResolveDocumentReferenceValue`, if the reference object exists but no resolved document id is available:
  - return `FlattenedWriteValue.Literal(null)` only when the explicit option is enabled;
  - keep the existing exception in normal mode.
- Apply the same rule for reference-derived document identity values if they are needed by a root value used for identity/proposed authorization:
  - return `null` only under the explicit deferred-reference option;
  - keep current fail-fast behavior otherwise.

This makes the precedence path explicit and prevents unresolved-reference tolerance from leaking into normal persistence.

Important constraint:

- The executor must not persist when deferred missing document-reference failures exist.
- After proposed authorization and deferred `If-Match` evaluation, if the deferred missing document-reference failure list is still present, return `BuildReferenceFailureResult`.

### 5. Keep Identity Stability Before Proposed Authorization

Leave the existing identity stability check after merge and before proposed authorization:

```csharp
var identityStabilityFailure = RelationalWriteIdentityStability.TryBuildFailureResult(
    executionRequest,
    mergeResult
);
```

This is what fixes the key-change precedence cases:

- `RelationshipsWithEdOrgsAndStaffs.feature` scenario 03.
- `RelationshipsWithEdOrgsAndStaffs.feature` scenario 15.
- `UpdateReferenceValidation.feature` scenario 04, once the missing `graduationSchoolYearTypeReference` no longer returns early.

### 6. Keep Proposed Authorization Before Deferred 409

Leave proposed namespace authorization before proposed relationship authorization:

```csharp
var namespaceAuthorizationBoundary = await _proposedNamespaceAuthorizationOrchestrator.ResolveAsync(...);
var proposedAuthorizationBoundary = await _proposedRelationshipAuthorizationOrchestrator.ResolveAsync(...);
```

Then run the existing deferred `If-Match` precondition block:

```csharp
if (
    ifMatchPreconditionEvaluation
    is IfMatchPreconditionEvaluation.DeferredUntilAfterProposedAuthorization
)
{
    var deferredPreconditionResult =
        _executionStateResolver.TryBuildDeferredPreconditionFailureResult(
            executionRequest,
            currentState
        );

    if (deferredPreconditionResult is not null)
    {
        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return deferredPreconditionResult;
    }
}
```

Only after that should the executor add the deferred missing document-reference return:

```csharp
if (hasDeferredMissingDocumentReferenceFailures)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}
```

This is what fixes the authorization precedence cases where current relational behavior returns `409` before the ODS-shaped `403`.

The ordering also preserves deferred `If-Match` behavior: a stale deferred precondition must not be hidden by a remembered missing document reference.

### 7. Preserve Database FK Mapping

Do not remove or weaken `RelationalWriteDatabaseFailureResultMapper`.

It should remain the final safety net for cases where:

- a document reference resolved successfully,
- authorization passed,
- the target row was deleted before persistence,
- the database raises an FK violation.

Those failures should still map to `409 Unresolved Reference`.

## Test Plan

### Unit Tests

Add focused tests in `DefaultRelationalWriteExecutorTests`.

Required coverage:

1. **Descriptor validation beats document-reference failure**
   - Arrange descriptor failure and document-reference failure.
   - Assert result is `400`/reference validation shape, not document-only `409`.

2. **Immutable identity beats missing document reference**
   - Existing-target write.
   - Request changes immutable identity.
   - Include missing document reference.
   - Assert `UpdateFailureImmutableIdentity` or corresponding upsert immutable identity result.

3. **Proposed relationship authorization beats missing document reference**
   - Caller has claims so planner returns executable authorization.
   - Proposed values fail authorization.
   - Include missing document reference.
   - Assert relationship authorization failure, not reference failure.

4. **Proposed namespace authorization beats missing document reference**
   - Proposed namespace fails.
   - Include missing document reference.
   - Assert namespace authorization failure, not reference failure.

5. **Missing document reference returns 409 when no higher-precedence failure exists**
   - No descriptor failures.
   - No identity changes.
   - Authorization passes or is not required.
   - Assert document-reference failure is returned after proposed authorization.

6. **Deferred If-Match beats missing document reference**
   - Existing-target write with deferred `If-Match` evaluation.
   - Include a missing document reference.
   - Assert the deferred precondition failure is returned before the remembered reference failure.

7. **Incompatible document-reference target is not deferred**
   - Arrange `DocumentReferenceFailureReason.IncompatibleTargetType`.
   - Assert the executor returns the current reference-failure result immediately.

8. **Profile writes are not changed by this fix**
   - Profile write with a missing document reference.
   - Assert the current immediate reference-failure path is preserved.

9. **Normal missing document reference tolerance is not enabled accidentally**
   - Exercise flattener/merge without the explicit deferred-reference option.
   - Assert the existing fail-fast behavior remains.

10. **No persistence occurs when deferred missing document-reference failures exist**
   - Verify `PersistAsync` is not called when the executor returns the deferred reference result.

### E2E Tests

After unit tests pass, flip the affected E2E scenarios whose expected behavior now matches ODS.

Scenarios expected to pass after implementation:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/References/UpdateReferenceValidation.feature`
  - scenario 04 should return `400 Key Change Not Supported` instead of early `409`.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndContacts.feature`
  - scenario 20 should return `403 Authorization Denied` instead of early `409`.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndPeople.feature`
  - scenario 32 should return the ODS-shaped authorization/precedence result.
  - scenario 39 should return `403 Authorization Denied` instead of early `409`.
  - scenario 40 should return `403 Authorization Denied` instead of early `409`.

Scenarios that already match ODS precedence relationally and can be flipped with expectation changes:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsAndStaffs.feature`
  - scenario 03: key-change beats update authorization.
  - scenario 15: key-change beats update authorization.
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/References/UpdateReferenceValidation.feature`
  - scenario 03: descriptor validation beats unresolved document reference.

## Validation Commands

Build:

```bash
dotnet build src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj -c Release
```

Run backend unit tests around the changed executor:

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj -c Release --filter DefaultRelationalWriteExecutor
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

## Risks

The main risk is making the write flattener too tolerant of missing references. The mitigation is to use an explicit deferred-document-reference option and assert normal mode still fails fast.

A second risk is proposed authorization using `null` values produced by unresolved references and returning `403` in cases where ODS would produce `409`. That is intentional only when authorization genuinely has enough information to deny the proposed values. Tests should include a plain missing-reference case to confirm `409` still wins when no higher-precedence denial exists.

Another risk is accidentally persisting a write with unresolved document references. The executor must return the deferred reference failure before guarded no-op or persistence code, and unit tests should verify `PersistAsync` is not called.

A fourth risk is broadening this fix into profile-write behavior. Keep profile writes on the current immediate reference-failure path unless a separate product decision defines profile/reference precedence.

## Acceptance Criteria

- Descriptor-reference failures still produce early `400 Bad Request`.
- Only missing document-reference failures are deferred; incompatible document-reference target failures keep current behavior.
- Profile writes keep current immediate reference-failure behavior.
- Missing document-reference failures no longer beat immutable identity/key-change failures.
- Missing document-reference failures no longer beat proposed namespace, relationship authorization, or deferred `If-Match` failures.
- Missing document-reference failures still produce `409 Unresolved Reference` when no higher-precedence failure exists.
- Database FK failures after authorization still map to `409`.
- The affected E2E scenarios are flipped with explicit expected bodies/statuses, not fallback assertions.
- Unit tests cover the new precedence ordering and the no-persist guarantee.
