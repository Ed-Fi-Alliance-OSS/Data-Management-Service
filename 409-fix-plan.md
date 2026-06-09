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

- Defer document-reference failures inside `DefaultRelationalWriteExecutor`.
- Keep descriptor-reference failures immediate.
- Allow merge/proposed-auth preflight to proceed when document references are missing.
- Return the remembered document-reference failures as `409` only if no higher-precedence result occurs.
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

if descriptor-reference failures exist:
    return 400 Bad Request validation/reference failure

remember document-reference failures, but do not return yet

resolve execution state/current state
merge selected body with current state
run immutable identity/key-change check
run proposed namespace authorization
run proposed relationship authorization

if remembered document-reference failures exist:
    return 409 Unresolved Reference

guarded no-op handling
persist
read committed representation
commit

on database FK/reference exception:
    map to unresolved reference as today
```

The key behavior change is moving the document-reference `BuildReferenceFailureResult` until after proposed authorization.

## Implementation Details

### 1. Add Reference Failure Helpers

Add small helper methods near the executor or `RelationalWriteExecutorResults` to keep the branch explicit:

```csharp
private static bool HasDescriptorReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDescriptorReferences.Count > 0;

private static bool HasDocumentReferenceFailures(ResolvedReferenceSet resolvedReferences) =>
    resolvedReferences.InvalidDocumentReferences.Count > 0;
```

Do not use `ResolvedReferenceSet.HasFailures` for precedence decisions in the executor because it conflates descriptor and document failures.

### 2. Return Descriptor Failures Immediately

After `ReferenceResolver.ResolveAsync`, replace the current `HasFailures` branch with descriptor-specific behavior:

```csharp
if (resolvedReferences.InvalidDescriptorReferences.Count > 0)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}
```

This preserves the legacy ODS rule that descriptor validation beats unresolved document references.

If both descriptor and document failures are present, returning the combined reference result is acceptable because Core maps mixed descriptor/document reference failures to `400 Bad Request`, preserving validation precedence.

### 3. Defer Document Failures

Keep the resolved reference set and continue:

```csharp
var hasDeferredDocumentReferenceFailures =
    resolvedReferences.InvalidDocumentReferences.Count > 0;
```

Do not return yet. The deferred result should be emitted only after identity stability and proposed authorization complete.

### 4. Let Merge Run With Deferred Missing Document References

This is the only part that needs careful implementation because the flattener currently expects every submitted document reference to have a resolved document id.

Preferred implementation:

- Add an explicit option to merge/flatten for this preflight case, for example:
  - `RelationalWriteMergeOptions.AllowMissingDocumentReferenceValuesForPrecedenceCheck`, or
  - a boolean on `FlatteningInput`, such as `AllowDeferredDocumentReferenceFailures`.
- Use that option only when `hasDeferredDocumentReferenceFailures` is true.
- In `RelationalWriteFlattener.ResolveDocumentReferenceValue`, if the reference object exists but no resolved document id is available:
  - return `FlattenedWriteValue.Literal(null)` only when the explicit option is enabled;
  - keep the existing exception in normal mode.
- Apply the same rule for reference-derived document identity values if they are needed by a root value used for identity/proposed authorization:
  - return `null` only under the explicit deferred-reference option;
  - keep current fail-fast behavior otherwise.

This makes the precedence path explicit and prevents unresolved-reference tolerance from leaking into normal persistence.

Important constraint:

- The executor must not persist when deferred document-reference failures exist.
- After proposed authorization, if the deferred document-reference failure list is still present, return `BuildReferenceFailureResult`.

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

Then add the deferred document-reference return:

```csharp
if (hasDeferredDocumentReferenceFailures)
{
    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
    return RelationalWriteExecutorResults.BuildReferenceFailureResult(
        executionRequest.OperationKind,
        resolvedReferences
    );
}
```

This is what fixes the authorization precedence cases where current relational behavior returns `409` before the ODS-shaped `403`.

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

6. **Normal missing document reference tolerance is not enabled accidentally**
   - Exercise flattener/merge without the explicit deferred-reference option.
   - Assert the existing fail-fast behavior remains.

7. **No persistence occurs when deferred document-reference failures exist**
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

## Acceptance Criteria

- Descriptor-reference failures still produce early `400 Bad Request`.
- Missing document-reference failures no longer beat immutable identity/key-change failures.
- Missing document-reference failures no longer beat proposed namespace or relationship authorization failures.
- Missing document-reference failures still produce `409 Unresolved Reference` when no higher-precedence failure exists.
- Database FK failures after authorization still map to `409`.
- The affected E2E scenarios are flipped with explicit expected bodies/statuses, not fallback assertions.
- Unit tests cover the new precedence ordering and the no-persist guarantee.
