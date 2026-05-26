# Relationship Authorization AuthObject Refactor

## Purpose

This document describes the full refactor for moving relationship authorization
auth object ownership from strategy/check-spec level to subject level.

The refactor addresses the ambiguity introduced by People relationship
authorization. Before People authorization, a relationship authorization check
spec effectively had one auth object: the EdOrg hierarchy auth object for the
strategy direction. With People authorization, a single configured strategy can
produce subjects that require different auth objects, for example:

- `auth.EducationOrganizationIdToEducationOrganizationId`
- `auth.EducationOrganizationIdToStudentDocumentId`
- `auth.EducationOrganizationIdToContactDocumentId`
- `auth.EducationOrganizationIdToStaffDocumentId`
- `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`

A top-level `RelationshipAuthorizationCheckSpec.AuthObject` cannot represent
that shape correctly.

## Recommendation

Make `RelationshipAuthorizationSubject.AuthObject` the only authoritative auth
object for executable relationship authorization checks.

For external failure metadata, keep
`RelationshipAuthorizationFailedStrategy.AuthObject` as an optional summary only:

- Set it when every failed subject represented by that failed strategy has the
  same auth object.
- Set it to `null` when the failed strategy contains multiple distinct auth
  objects.
- Add/move auth object metadata to each failed subject so no detail is lost.

Do not split one configured failed strategy into multiple failed strategies by
auth object. The configured strategy is the logical authorization unit. Splitting
by auth object would make one configured strategy look like several independent
strategies and would distort strategy order, hints, and failure semantics.

## Goals

- Remove `RelationshipAuthorizationCheckSpec.AuthObject`.
- Remove auth object duplication between check spec and People subject metadata.
- Put the auth object on every `RelationshipAuthorizationSubject`.
- Make SQL/adapters/failure mapping read auth object metadata from the subject
  being evaluated.
- Preserve existing EdOrg behavior.
- Represent mixed EdOrg plus People strategies without lying through a
  single top-level auth object.
- Keep failure payloads useful for both homogeneous and mixed-auth-object
  strategies.

## Non-Goals

- Do not implement People endpoint SQL execution as part of this refactor.
- Do not unstage DMS-1095 or DMS-1158 endpoint behavior.
- Do not change auth view DDL or manifest emission.
- Do not change relationship authorization strategy classification.
- Do not change the public ProblemDetails message text unless required by the
  failure metadata shape.

## Current Problem

Current internal model:

```csharp
public sealed record RelationshipAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    RelationshipAuthorizationHierarchyDirection Direction,
    RelationshipAuthorizationValueSource ValueSource,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    RelationshipAuthorizationCheckTarget CheckTarget
);

public sealed record RelationshipAuthorizationSubject(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName Column,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors,
    RelationshipAuthorizationPersonSubjectMetadata? PersonMetadata = null
);

public sealed record RelationshipAuthorizationPersonSubjectMetadata(
    RelationshipAuthorizationPersonKind PersonKind,
    RelationshipAuthorizationPersonSubjectPath Path,
    RelationshipAuthorizationAuthObject AuthObject,
    RelationshipAuthorizationPersonStoredAnchor StoredAnchor,
    RelationshipAuthorizationPersonProposedAnchor? ProposedAnchor
);
```

This creates two issues:

1. `RelationshipAuthorizationCheckSpec.AuthObject` is ambiguous for mixed
   subject strategies.
2. People subjects already carry their own auth object, so the check spec and
   the subject can disagree or invite the wrong consumer behavior.

The most fragile current helper is `CreateCheckSpecAuthObject`, which chooses
one auth object by convention:

- EdOrg if any EdOrg subject exists.
- Otherwise the first People subject auth object.

That convention is not a valid domain rule. It is only a compatibility shim for
existing EdOrg-only consumers.

## Target Internal Model

Move auth object to the executable subject:

```csharp
public sealed record RelationshipAuthorizationSubject(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors,
    RelationshipAuthorizationPersonSubjectMetadata? PersonMetadata = null
)
{
    public bool IsPersonSubject => PersonMetadata is not null;
}
```

Remove auth object from People metadata:

```csharp
public sealed record RelationshipAuthorizationPersonSubjectMetadata(
    RelationshipAuthorizationPersonKind PersonKind,
    RelationshipAuthorizationPersonSubjectPath Path,
    RelationshipAuthorizationPersonStoredAnchor StoredAnchor,
    RelationshipAuthorizationPersonProposedAnchor? ProposedAnchor
);
```

Remove auth object from check spec:

```csharp
public sealed record RelationshipAuthorizationCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    int RelationshipLocalOrder,
    RelationshipAuthorizationHierarchyDirection Direction,
    RelationshipAuthorizationValueSource ValueSource,
    IReadOnlyList<RelationshipAuthorizationSubject> Subjects,
    RelationshipAuthorizationCheckTarget CheckTarget
)
{
    public IReadOnlyList<RelationshipAuthorizationIneligibleSubject> IneligibleSubjects { get; init; } = [];
}
```

After this change, every executable subject carries the auth object needed to
evaluate that subject. A check spec is only the strategy-level AND group plus
target/value-source metadata.

## Target Failure Metadata Model

The external failure model currently has a nullable strategy-level auth object
and a People-specific subject auth object:

```csharp
public sealed record RelationshipAuthorizationFailedStrategy(
    int ConfiguredStrategyIndex,
    int RelationshipLocalOrder,
    string StrategyName,
    string StrategyKind,
    RelationshipAuthorizationAuthObjectInfo? AuthObject,
    RelationshipAuthorizationFailedSubject[] FailedSubjects,
    string? Hint = null
);

public sealed record RelationshipAuthorizationPersonSubjectInfo(
    string PersonKind,
    RelationshipAuthorizationAuthObjectInfo AuthObject,
    string PathKind,
    ...
);
```

The full refactor should make failed subject auth object metadata generic:

```csharp
public sealed record RelationshipAuthorizationFailedSubject(
    int SubjectIndex,
    RelationshipAuthorizationSubjectFailureKind FailureKind,
    RelationshipAuthorizationRootBinding RootBinding,
    RelationshipAuthorizationAuthObjectInfo AuthObject,
    RelationshipAuthorizationSecurableElement[] SecurableElements,
    string? Hint = null
)
{
    public RelationshipAuthorizationPersonSubjectInfo? PersonSubject { get; init; }
}
```

Then remove `AuthObject` from `RelationshipAuthorizationPersonSubjectInfo`:

```csharp
public sealed record RelationshipAuthorizationPersonSubjectInfo(
    string PersonKind,
    string PathKind,
    RelationshipAuthorizationPersonDocumentIdPathStepInfo[] DocumentIdPath,
    RelationshipAuthorizationPersonStoredAnchorInfo StoredAnchor,
    RelationshipAuthorizationPersonProposedAnchorInfo? ProposedAnchor,
    string? Hint = null
);
```

If minimizing external constructor churn is important, implement this as a
two-step transition:

1. Add `RelationshipAuthorizationFailedSubject.AuthObject` as a required
   non-null field, preferably positional, and keep
   `RelationshipAuthorizationPersonSubjectInfo.AuthObject` populated.
2. Once all consumers read the subject-level auth object, remove the
   People-specific duplicate.

For a single refactor PR, prefer the full target shape and update tests/callers
in the same change. Avoid adding a nullable or optional subject-level auth
object field during transition; that would preserve the ambiguity this refactor
is removing.

## Strategy-Level AuthObject Rule

`RelationshipAuthorizationFailedStrategy.AuthObject` remains nullable and is
only a summary field.

Mapping rule:

```csharp
private static RelationshipAuthorizationAuthObjectInfo? SelectStrategyAuthObjectSummary(
    IReadOnlyList<RelationshipAuthorizationFailedSubject> failedSubjects
)
{
    var distinctAuthObjects = failedSubjects
        .Select(static subject => subject.AuthObject)
        .Distinct()
        .ToArray();

    return distinctAuthObjects.Length == 1 ? distinctAuthObjects[0] : null;
}
```

Use the equivalent internal-object comparison before mapping to external DTOs.

This rule gives accurate output for these cases:

- EdOrg-only strategy: strategy auth object is populated.
- People-only Student strategy: strategy auth object is populated.
- People-only Student plus Contact strategy: strategy auth object is null,
  failed subjects carry their own auth objects.
- Mixed EdOrg plus People strategy: strategy auth object is null, failed
  subjects carry their own auth objects.

## Implementation Plan

### 1. Update RelationshipAuthorizationContracts

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationContracts.cs`

Changes:

- Add `RelationshipAuthorizationAuthObject AuthObject` to
  `RelationshipAuthorizationSubject`.
- Remove `RelationshipAuthorizationAuthObject AuthObject` from
  `RelationshipAuthorizationCheckSpec`.
- Remove `RelationshipAuthorizationAuthObject AuthObject` from
  `RelationshipAuthorizationPersonSubjectMetadata`.
- Update constructors and record positional argument order consistently.

Recommended subject argument order:

```csharp
public sealed record RelationshipAuthorizationSubject(
    QualifiedResourceName Resource,
    DbTableName Table,
    DbColumnName Column,
    RelationshipAuthorizationAuthObject AuthObject,
    IReadOnlyList<RelationshipAuthorizationSubjectContributor> Contributors,
    RelationshipAuthorizationPersonSubjectMetadata? PersonMetadata = null
);
```

The auth object belongs next to the relational value binding because it defines
how that value is authorized.

### 2. Stamp AuthObject During Subject Selection

Files:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationalEdOrgAuthorizationSubjectSelector.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationalPeopleAuthorizationSubjectSelector.cs`

EdOrg selector:

- Change the planner-facing selector API to select for one supported strategy.
- Create the EdOrg auth object from `supportedStrategy.Direction`.
- Pass that auth object into every grouped EdOrg subject.

Recommended shape:

```csharp
internal RelationalEdOrgAuthorizationSubjectSelection Select(
    MappingSet mappingSet,
    QualifiedResourceName resource,
    SupportedRelationshipAuthorizationStrategy supportedStrategy
)
```

Then:

```csharp
var authObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
    supportedStrategy.Direction
);
```

People selector:

- Keep creating the People auth object from eligible subject kind.
- Put that auth object on `RelationshipAuthorizationSubject.AuthObject`.
- Remove the auth object from `RelationshipAuthorizationPersonSubjectMetadata`.
- Keep auth object on skipped contributors and failure metadata where it is
  needed before executable subjects exist.

### 3. Simplify Planner Check Spec Creation

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationPlanner.cs`

Changes:

- Delete `CreateCheckSpecAuthObject`.
- Remove the auth object constructor argument when creating stored/proposed
  check specs.
- Update `ApplyProposedAnchorToPersonSubject` so it preserves
  `subject.AuthObject` and updates only People proposed anchor metadata.
- Update no-claims failure construction to use `subject.AuthObject`.

No-claims failure creation should operate over distinct subject auth objects:

- For EdOrg-only checks, this produces one failure with the EdOrg auth object.
- For People-only or mixed checks, this can produce one failure metadata entry
  per distinct subject auth object, preserving People hints.

Subject-specific failures should always set:

```csharp
AuthObject: subject.AuthObject
```

Failure metadata that is created before a subject exists can continue to use
the auth object derived from the selected eligible subject.

### 4. Update SingleRecordRelationshipAuthorizationSqlCompiler

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`

Changes:

- Replace all `checkSpec.AuthObject` reads with the current subject's auth
  object.
- Change helpers such as `AppendAuthorizationSuccessSql` and
  `AppendAuthorizationExistsSelectSql` to accept
  `RelationshipAuthorizationAuthObject authObject`.
- When emitting each subject predicate, pass `subject.AuthObject`.

Current pattern:

```csharp
if (checkSpec.AuthObject.AllowsDirectClaimMatch)
{
    ...
}

writer.AppendRelation(new SqlRelationRef.PhysicalTable(checkSpec.AuthObject.Name));
```

Target pattern:

```csharp
if (authObject.AllowsDirectClaimMatch)
{
    ...
}

writer.AppendRelation(new SqlRelationRef.PhysicalTable(authObject.Name));
```

This preserves current EdOrg SQL because all EdOrg subjects in a check spec
will have the same direction-specific auth object. It also removes the
strategy-level auth-object assumption from the compiler. People endpoint
execution still requires DMS-1095/DMS-1158 SQL work for DocumentId path
extraction, transitive person joins, and non-EdOrg subject value typing.

### 5. Update PageDocumentIdAuthorizationSpecAdapter

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageDocumentIdAuthorizationSpecAdapter.cs`

Page-document authorization is still EdOrg-only. Keep that boundary, but derive
the strategy-level `AllowsDirectClaimMatch` from subject auth objects.

Recommended helper:

```csharp
private static RelationshipAuthorizationAuthObject SelectSingleEdOrgAuthObject(
    RelationshipAuthorizationCheckSpec checkSpec
)
{
    var authObjects = checkSpec.Subjects
        .Select(static subject => subject.AuthObject)
        .Distinct()
        .ToArray();

    if (authObjects.Length != 1)
    {
        throw new InvalidOperationException(
            "PageDocumentId authorization requires exactly one EdOrg auth object."
        );
    }

    return authObjects[0];
}
```

Call the existing execution boundary before adaptation, then use the selected
auth object for `AllowsDirectClaimMatch`.

### 6. Update RelationshipAuthorizationEndpointExecutionBoundary

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationEndpointExecutionBoundary.cs`

Changes:

- Replace `UsesEdOrgHierarchyAuthObject(checkSpec.AuthObject)` with validation
  over all `checkSpec.Subjects`.
- When building an unsupported auth object message, report the first subject
  auth object that is not supported.

The boundary should continue to reject People strategies for page-document and
single-record SQL execution until their endpoint slices are implemented.

### 7. Update RelationshipAuthorizationFailureMapper

File:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationFailureMapper.cs`

Changes:

- Map each failed subject with `subject.AuthObject`.
- Remove fallback logic that uses `checkSpec.AuthObject`.
- Select strategy-level `AuthObject` only when mapped failed subjects are
  homogeneous.
- Remove `SelectSubjectAuthObject(subject, strategyAuthObject)` because the
  subject now owns the auth object.

AUTH1 runtime mapping rule:

```csharp
var mappedSubjects = subjectFailures
    .Select(failure => MapSubject(
        failure.SubjectOrdinal,
        MapSubjectFailureKind(failure.FailureKind),
        checkSpec.Subjects[failure.SubjectOrdinal],
        BuildSubjectHint(failure.FailureKind)
    ))
    .ToArray();

var strategyAuthObject = SelectHomogeneousStrategyAuthObject(mappedSubjects);
```

Use only the failed subject ordinals represented by the runtime AUTH1 payload
when selecting the strategy-level summary. Do not include successful subjects in
the homogeneity calculation. For example, if a mixed EdOrg plus Student strategy
fails only on the Student subject, the strategy summary should be the Student
auth object, not `null`.

For no-claims failures, choose subject-level metadata by matching
`failure.AuthObject` to `subject.AuthObject`. For People subjects, also match
People kind/path where available.

No-claims mapping is different from AUTH1 runtime mapping because an empty EdOrg
claim list means every executable subject in the selected relationship strategy
fails before SQL is composed. In that path, mapping all `checkSpec.Subjects` is
correct. A mixed EdOrg plus Student no-claims failure should still produce one
failed strategy for the configured strategy:

- `FailedStrategy.AuthObject == null` because the failed subjects use distinct
  auth objects.
- The EdOrg failed subject carries
  `auth.EducationOrganizationIdToEducationOrganizationId`.
- The Student failed subject carries
  `auth.EducationOrganizationIdToStudentDocumentId`.
- The People hint comes from the Student subject auth object.

### 8. Update Core External Failure DTO

File:

- `src/dms/core/EdFi.DataManagementService.Core.External/Backend/RelationshipAuthorizationFailure.cs`

Recommended full target:

- Add `RelationshipAuthorizationAuthObjectInfo AuthObject` to
  `RelationshipAuthorizationFailedSubject`.
- Remove `RelationshipAuthorizationAuthObjectInfo AuthObject` from
  `RelationshipAuthorizationPersonSubjectInfo`.
- Keep `RelationshipAuthorizationFailedStrategy.AuthObject` nullable as a
  summary field.

If the full target causes too much constructor churn in one PR, use the
two-step transition described above. The final state should still remove the
People-specific duplicate.

The new subject-level auth object should be non-null for every failed executable
subject. During a transition, make the field hard to omit. If an init-only
transition property is unavoidable, mark it `required` and keep it non-null so
mapper paths cannot silently omit it.

### 9. Update Repository Staging Messages

File:

- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs`

Current staging helpers inspect:

```csharp
failure.PersonMetadata?.AuthObject.Name.ToString()
    ?? failure.AuthObject?.Name.ToString()
```

After the internal refactor:

- Prefer subject-level auth object when a subject exists.
- Continue to use `RelationshipAuthorizationFailureMetadata.AuthObject` for
  pre-subject failures.
- Do not depend on check-spec auth object.

No endpoint behavior should change as part of this refactor.

`RelationshipAuthorizationFailureMetadata.AuthObject` remains valid for
planning and configuration diagnostics that can be emitted before an executable
subject exists, such as unresolved securable elements, skipped child paths,
missing People auth views, or no-applicable-subject failures. Runtime failures
for executable checks should prefer `RelationshipAuthorizationSubject.AuthObject`.

## Test Plan

Do not rely only on existing EdOrg tests. Add or update tests for the new model
shape.

### Planner Tests

Update existing tests:

- Replace `checkSpec.AuthObject` assertions with subject auth object assertions.
- Verify EdOrg normal and inverted strategies stamp subjects with the expected
  auth object.
- Verify People subjects carry the expected People auth view object directly on
  `RelationshipAuthorizationSubject`.
- Verify `PersonMetadata` no longer carries auth object.

Add focused tests:

- Mixed EdOrg plus Student strategy produces subjects with different auth
  objects.
- People-only Student plus Contact strategy produces subjects with different
  auth objects.
- Proposed-value subject transformations preserve `subject.AuthObject`.
- Self-person create-new ineligible subjects preserve `subject.AuthObject`.

### SQL Compiler Tests

Update existing EdOrg tests:

- Expected SQL should remain unchanged.
- Test setup should place the EdOrg auth object on each subject.

Add focused guard tests:

- A check spec with non-EdOrg subject auth object still fails at the execution
  boundary until People endpoint execution is implemented.
- A check spec with multiple auth objects should not be rejected merely because
  the check spec lacks a top-level auth object; rejection should be based on the
  current endpoint boundary.

### PageDocumentId Adapter Tests

Update setup:

- Place auth objects on subjects.
- Remove check-spec auth object setup.

Add guard test:

- Multiple distinct subject auth objects produce a clear adapter/boundary
  failure.

### Failure Mapper Tests

Update existing tests:

- Strategy-level auth object is populated for homogeneous EdOrg failures.
- Failed subject auth object is populated for every failed subject.
- People subject info no longer duplicates auth object in the final target.

Add tests:

- AUTH1 mixed EdOrg plus People strategy where only the People subject fails:
  `FailedStrategy.AuthObject` is the People auth object, not `null`.
- AUTH1 mixed EdOrg plus People strategy where both EdOrg and People subjects
  fail: `FailedStrategy.AuthObject == null`.
- Mixed EdOrg plus People failed strategy with multiple distinct failed subject
  auth objects does not invent a single strategy-level auth object.
- Each failed subject in that mixed strategy has the correct auth object.
- People-only mixed Student plus Contact failed strategy has
  `FailedStrategy.AuthObject == null`.
- Homogeneous People failed strategy, such as Students-only, has both
  `FailedStrategy.AuthObject` summary and per-subject auth object.
- Mixed EdOrg plus People no-claims strategy has a null
  `FailedStrategy.AuthObject`, per-subject auth objects, and People hints
  preserved.
- Homogeneous People no-claims strategy has a populated strategy-level auth
  object summary.

### Repository/Core Handler Tests

Update tests that construct `RelationshipAuthorizationFailedSubject` or
`RelationshipAuthorizationPersonSubjectInfo`.

Verify:

- Error formatting still includes the same user-facing properties.
- Existing EdOrg-only failure responses remain semantically unchanged.
- Mixed-auth-object strategy payloads do not invent a single strategy auth
  object.

## Migration Notes

This refactor touches positional records. Expect compile errors in tests and
fixtures after the model change. Handle them mechanically:

1. Update subject construction to include `AuthObject`.
2. Remove check-spec auth object construction.
3. Move People auth object assertions from `PersonMetadata` to subject.
4. Update failure DTO construction if the full target external record shape is
   applied.

Use `dotnet csharpier format` on touched files after code changes.

## Risks and Mitigations

### Risk: Strategy-level failure payload loses auth object detail

Mitigation:

- Add subject-level auth object metadata to failed subjects.
- Set strategy-level auth object only for homogeneous failed strategies.

### Risk: EdOrg selector becomes direction-dependent

Mitigation:

- Make the planner-facing EdOrg selector operate on a single supported strategy.
- The planner already selects EdOrg subjects one strategy at a time, so this
  matches current control flow.

### Risk: SQL compiler accidentally keeps strategy-level assumptions

Mitigation:

- Delete `RelationshipAuthorizationCheckSpec.AuthObject` entirely so any stale
  code fails at compile time.
- Change SQL helper signatures to require an auth object argument.

### Risk: External DTO churn is larger than expected

Mitigation:

- If needed, add `RelationshipAuthorizationFailedSubject.AuthObject` as an
  init-only transition property first.
- Keep existing strategy-level nullable summary during the transition.
- Remove the People-specific duplicate after consumers are updated.

## Acceptance Criteria

- No production code reads `RelationshipAuthorizationCheckSpec.AuthObject`
  because the property no longer exists.
- Every `RelationshipAuthorizationSubject` has a non-null auth object.
- `RelationshipAuthorizationPersonSubjectMetadata` has no auth object field.
- Existing EdOrg authorization SQL remains unchanged.
- People authorization planning exposes exact per-subject auth objects.
- Strategy-level failed auth object is populated only for homogeneous failed
  strategies.
- Mixed-auth-object failed strategies use `FailedStrategy.AuthObject == null`
  and subject-level auth objects for detail.
- Endpoint staging behavior remains unchanged.
