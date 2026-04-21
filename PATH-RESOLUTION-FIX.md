# Reference Identity Query Path Resolution Fix

## Context

DMS-993 implemented relational GET-many query execution with root-table page selection, deterministic `DocumentId` ordering, and batched hydration/reconstitution. The design requires reference identity query fields such as `studentUniqueId`, `schoolId`, and similar aliases to compile to local root-table binding columns, with no join to the referenced resource.

Relevant design requirements:

- `reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md` keeps reference identity query fields in scope for DMS-993.
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` says query compilation should target root-table API-bound binding/path columns, including `UnifiedAlias` columns.
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` says document reference identity query fields compile to local per-site identity binding columns.
- `reference/design/backend-redesign/design-docs/key-unification.md` says API-path consumers bind to path columns, while storage/DML consumers resolve through `ColumnStorage`.

The implemented query path misses reference identity aliases that do not exactly match a root column `SourceJsonPath`.

## Problem

`RelationalQueryCapabilityCompiler` currently compiles query fields by exact-matching `queryFieldMapping` paths against:

1. `DescriptorEdgeSource.DescriptorValuePath` for root descriptor fields.
2. Root scalar `DbColumnModel.SourceJsonPath` values.
3. Non-root scalar `SourceJsonPath` values for diagnostics.

That is sufficient for ordinary scalar and descriptor fields, but not for virtual or target-side reference identity aliases.

Concrete failure:

- `CourseTranscript.studentUniqueId` has an ApiSchema query path of `$.studentReference.studentAcademicRecordUniqueId`.
- The relational root table stores the local reference identity value in `StudentAcademicRecord_StudentUniqueId`, sourced from `$.studentAcademicRecordReference.studentUniqueId`.
- `DocumentReferenceBinding` already records the semantic relation:
  - target identity path: `$.studentReference.studentUniqueId`
  - local reference path: `$.studentAcademicRecordReference.studentUniqueId`
  - local column: `StudentAcademicRecord_StudentUniqueId`
- Because the query path does not exactly match the root column source path, the compiler classifies `studentUniqueId` as `UnmappedPath`, marks the resource query capability omitted, and runtime GET-many returns 501 for the whole resource.

## Goals

- Resolve reference identity query aliases through `RelationalResourceModel.DocumentReferenceBindings`.
- Preserve the original ApiSchema query path in `SupportedRelationalQueryField.Path` so Core `QueryElement.DocumentPaths` validation continues to match the request contract.
- Target the local API-bound binding/path column, not the canonical storage column.
- Collapse same-site duplicate reference identity bindings using the existing logical-field/key-unification rules.
- Keep SQL execution root-table-only and join-free for document reference identity filters.
- Avoid physical column-name conventions when resolving aliases.

## Non-Goals

- Do not add child-table query predicates, `EXISTS`, or reference-resource joins.
- Do not implement multi-path OR query fields.
- Do not broaden query syntax beyond exact-match predicates.
- Do not change `PageDocumentIdSqlCompiler` unless tests expose a missing alias-rewrite behavior.
- Do not repair ApiSchema query paths in fixtures. The compiler must tolerate the existing public schema contract.

## Proposed Design

Add a small reference-identity target resolver to `RelationalQueryCapabilityCompiler`.

The existing exact path lookup should remain for normal scalar and descriptor fields. When exact lookup does not produce a deterministic root target, the compiler should ask this resolver whether the query field is a reference identity alias that can bind to a root-table reference identity column.

### New Helper

Add an internal helper in `EdFi.DataManagementService.Backend.Plans`, for example:

```csharp
internal sealed class ReferenceIdentityQueryTargetResolver
```

Keep this helper narrow. It is not a general JSONPath resolver. Its only job is to inspect root-table `DocumentReferenceBindings`, map one single-path `queryFieldMapping` to one local binding column, and report no match or ambiguity when it cannot do that deterministically.

Inputs:

- `RelationalResourceModel`
- root `DbTableModel`
- the `RelationalQueryFieldMapping` being compiled

Output:

- a deterministic `RelationalQueryFieldTarget`, or
- a failure classification compatible with the existing `RelationalQueryFieldFailureKind`.

The helper should build candidates only from `DocumentReferenceBindings` where `binding.Table == rootTable.Table`.

### Reference Candidate Construction

For each root `DocumentReferenceBinding`:

1. Build candidate records from the raw `ReferenceIdentityBinding` entries so each candidate retains:
   - `IdentityJsonPath`
   - `ReferenceJsonPath`
   - `Column`
   - owning `DocumentReferenceBinding.TargetResource`
   - owning `DocumentReferenceBinding.ReferenceObjectPath`
   - owning `DocumentReferenceBinding.FkColumn`
2. Validate grouped identity bindings with `ReferenceIdentityProjectionLogicalFieldResolver.ResolveOrThrow(...)`:
   - each member column must exist on the root table,
   - each member column `SourceJsonPath` must match the group `ReferenceJsonPath`,
   - if members are unified aliases, their canonical storage column and presence column must converge,
   - if a reference-site member is a unified alias, its presence column must be the owning reference FK column,
   - same-site duplicate groups must not be collapsed by picking a column name convention.
3. Attach each raw candidate to the resolved logical field for its `ReferenceJsonPath` and use that field's representative binding column as the query target.

The projection resolver should provide validation, but the query resolver should not rely only on its output because the projection output groups by `ReferenceJsonPath` and does not preserve every member `IdentityJsonPath`. Query matching needs both.

### Matching Rules

For a single-path query field, a reference candidate matches when one of these is true:

1. **Local reference path match**
   - `queryPath == ReferenceIdentityBinding.ReferenceJsonPath`
   - Example: `$.studentAcademicRecordReference.studentUniqueId`

2. **Target identity path match**
   - `queryPath == ReferenceIdentityBinding.IdentityJsonPath`
   - Example: `$.studentReference.studentUniqueId`

3. **Virtual query alias match**
   - the query path does not cross an array,
   - the query path leaf starts with `lowerCamel(DocumentReferenceBinding.TargetResource.ResourceName)` under ordinal-ignore-case comparison, and
   - the public query field name ends with the candidate target identity leaf name under ordinal-ignore-case comparison.
   - Example: `CourseTranscript.studentUniqueId`
     - query field name: `studentUniqueId`
     - query path: `$.studentReference.studentAcademicRecordUniqueId`
     - candidate target identity path: `$.studentReference.studentUniqueId`
     - candidate target resource: `StudentAcademicRecord`
     - local column: `StudentAcademicRecord_StudentUniqueId`
   - Example: `StudentAssessmentRegistration.scheduledStudentUniqueId`
     - query field name: `scheduledStudentUniqueId`
     - query path: `$.scheduledStudentReference.studentEducationOrganizationAssessmentAccommodationUniqueId`
     - candidate target identity path: `$.studentReference.studentUniqueId`
     - candidate target resource: `StudentEducationOrganizationAssessmentAccommodation`

This virtual rule is a fallback after exact local/target path matching. It keeps the match semantic: it uses ApiSchema query metadata plus `DocumentReferenceBinding.IdentityBindings`, not physical column prefixes.

If more than one candidate matches:

- if all matches resolve to the same same-site logical field group, collapse to that group's representative binding column;
- otherwise classify the query field as `AmbiguousRootTarget`.

### Target Selection

The matched candidate target should be:

- `RelationalQueryFieldTarget.RootColumn(representativeBindingColumn)` for scalar reference identity bindings.
- `RelationalQueryFieldTarget.DescriptorIdColumn(representativeBindingColumn, descriptorResource)` for descriptor-valued reference identity bindings, using `DbColumnModel.TargetResource` or the matching root `DescriptorEdgeSource`.

The target column must be the API-bound binding column. If it is a `UnifiedAlias`, leave it as the target. `RelationalQueryPageKeysetPlanner` already builds `UnifiedAliasMappingsByColumn`, and `PageDocumentIdSqlCompiler` already rewrites alias predicates to canonical storage columns with the required presence gate.

For CourseTranscript, the resulting supported field should keep:

- query field: `studentUniqueId`
- supported path: `$.studentReference.studentAcademicRecordUniqueId`
- target: `RootColumn(StudentAcademicRecord_StudentUniqueId)`

## Compiler Flow

Update `CompileQueryField` in `RelationalQueryCapabilityCompiler`:

1. Reject multi-path mappings as today.
2. Preserve the `$.id` special case.
3. Try exact root descriptor target resolution.
4. Try exact root scalar path resolution when it is unique.
5. Try reference-aware resolution:
   - this handles no exact root match,
   - this handles exact root ambiguity caused by same-site duplicate reference identity bindings.
6. Apply existing unsupported classifications:
   - `ArrayCrossing`
   - `NonRootTable`
   - `UnmappedPath`
   - `AmbiguousRootTarget`

The exact root scalar path should not immediately return `AmbiguousRootTarget` when multiple root columns match. Give the reference-aware resolver a chance to prove the matches are one same-site logical endpoint group.

## Diagnostics

No new manifest contract is required.

Expected manifest changes:

- Reference identity aliases that previously appeared in `unsupported_fields_in_query_field_order` as `unmapped_path` should move to `supported_fields_in_query_field_order`.
- Resources omitted only because of those aliases should become `RelationalQuerySupport.Supported`.
- Real unsupported cases still use existing failure kinds.

If the resolver finds multiple semantic matches, keep `AmbiguousRootTarget`. If a matching reference binding points to missing columns or invalid storage metadata, fail fast as a derived-model/plan-compilation error rather than silently omitting the field.

## Tests

Add focused unit coverage in `MappingSetCompilerTests` or a dedicated `RelationalQueryCapabilityCompilerTests` fixture:

- CourseTranscript-style virtual alias:
  - query field `studentUniqueId`
  - query path `$.studentReference.studentAcademicRecordUniqueId`
  - binding identity path `$.studentReference.studentUniqueId`
  - binding reference path `$.studentAcademicRecordReference.studentUniqueId`
  - expected target `RootColumn(StudentAcademicRecord_StudentUniqueId)`

- StudentAssessmentRegistration virtual alias:
  - query field `studentUniqueId`
  - query path `$.studentReference.studentEducationOrganizationAssociationUniqueId`
  - expected target `RootColumn(StudentEducationOrganizationAssociation_StudentUniqueId)`

- StudentAssessmentRegistration scheduled virtual alias:
  - query field `scheduledStudentUniqueId`
  - query path `$.scheduledStudentReference.studentEducationOrganizationAssessmentAccommodationUniqueId`
  - expected target `RootColumn(ScheduledStudentEducationOrganizationAssessmentAccom_44578471b1)` or the generated binding column in the current manifest.

- Same-site duplicate group:
  - one `DocumentReferenceBinding` has duplicate `ReferenceJsonPath` members that converge through key unification.
  - expected target is the representative API-bound alias column, and the resource remains supported.

- Ambiguous duplicate guard:
  - duplicates across different reference sites or different non-converging storage columns classify as `AmbiguousRootTarget`.

- One general student-program association virtual alias:
  - query path uses `$.studentReference.generalStudentProgramAssociationUniqueId`
  - expected target is the local `GeneralStudentProgramAssociation` student binding column for that resource.

Golden/regression coverage:

- Regenerate authoritative sample mapping-set manifests.
- Verify `CourseTranscript.studentUniqueId` is supported and the resource is no longer omitted for `unmapped_path`.
- Search for resources omitted only because of reference identity aliases and confirm deterministic aliases move to supported.

Provider coverage:

- Add at least one real relational GET-many test on PostgreSQL and SQL Server:
  - seed/write a CourseTranscript-like resource,
  - call `GET /courseTranscripts?studentUniqueId=...`,
  - assert 200 and returned results,
  - assert the previous failure mode is not 501.

## Implementation Notes

- Keep `SupportedRelationalQueryField.Path` equal to the original `queryFieldMapping` path.
- Use `is null` / `is not null` for null checks.
- Avoid string parsing of column names such as `StudentAcademicRecord_StudentUniqueId`.
- JSONPath comparisons remain ordinal on canonical path strings.
- Public query field name comparison can use the existing query field comparer semantics, `StringComparer.OrdinalIgnoreCase`.
- After code changes, run:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
  - focused plan/compiler unit tests
  - provider integration/E2E query tests as available

## Acceptance Criteria

- `CourseTranscript.studentUniqueId` compiles to the local root-table reference binding column.
- Same-site duplicate/key-unified reference identity fields compile deterministically without choosing by physical column-name convention.
- Original query paths remain visible in supported query metadata.
- Page SQL still targets root-table predicates only, with `UnifiedAlias` rewrite and presence gates handled by `PageDocumentIdSqlCompiler`.
- Resources are no longer omitted solely because deterministic reference identity query aliases do not exact-match `DbColumnModel.SourceJsonPath`.
