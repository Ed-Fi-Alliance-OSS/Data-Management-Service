# E2E verification blocked by existing GET-many authorization failures

Story: `reference/design/backend-redesign/epics/14-authorization/07-relationship-auth-crud/02-edorg-only-get-by-id-and-delete.md`

Task selected: `e2e`

Implemented focused single-record E2E coverage in `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RelationshipsWithEdOrgsOnlyRelational.feature`:

- GET-by-id on `academicWeeks/{id}` returns 403 for a caller with non-matching EdOrg claims.
- DELETE on `academicWeeks/{id}` returns 403 for a caller with non-matching EdOrg claims, then an authorized GET proves the row remains.

The two new scenarios pass against a clean local stack:

```bash
env -u NODE_OPTIONS dotnet test src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj --filter "FullyQualifiedName~GETByIdReturnsForbidden|FullyQualifiedName~DELETEReturnsForbidden"
```

Result: 2 passed.

The required broader verification is blocked by existing GET-many relationship authorization scenarios in the same feature:

```bash
env -u NODE_OPTIONS dotnet test src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj --no-build --filter "FullyQualifiedName~Authorization|FullyQualifiedName~ReadResources|FullyQualifiedName~DeleteResources"
```

Observed failures before stopping the run:

- `EmptyEducationOrganizationClaimsReturnAnEmptyPageWithTotalCountZero`
  - Expected: 200, `Total-Count: 0`, `[]`
  - Actual: 403 with error that the client has no education organizations assigned.
  - This appears outside the selected GET-by-id/DELETE E2E task and conflicts with the story's note that single-record empty claims should return 403 rather than the GET-many empty-page behavior.
- `InvertedStrategyAllowsSchoolClaimsToQueryParentLocalEducationAgencies`
  - Expected: parent LEA 201 returned for school claim 20101.
  - Actual: `[]`.
- `NormalAndInvertedStrategiesAreORedForGET_ManyAuthorization`
  - Expected: LEAs 201 and 301.
  - Actual: only LEA 201; assertion then throws an index error because the expected array has more items than the actual response.
- `KnownUnsupportedMixedStrategiesReturnNotImplementedForGET_Many`
  - Expected: 501 for mixed `RelationshipsWithEdOrgsOnly` + `NamespaceBased`.
  - Actual: 200 with the academic week resource returned.

Environment notes:

- Initial setup over an existing stack failed while creating the DMS instance. A clean `teardown-local-dms.ps1` followed by `setup-local-dms.ps1` succeeded.
- The inherited local `NODE_OPTIONS=--experimental-strip-types` makes Playwright's bundled Node fail before API requests. Rerunning test commands with `env -u NODE_OPTIONS` resolves that environment issue.

I did not mark the task complete or commit because the requested broad E2E feedback loop is failing for out-of-scope GET-many behavior.
