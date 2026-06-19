# DMS-1222 Part 2 Ignored Scenario Audit

This audit covers the 28 ignored scenarios called out in Part 2 of
`reference/design/backend-redesign/fixes/DMS-1222.md`.

I did not run E2E tests or change feature/code files. The recommendations below are based on
the current feature sources, surrounding relational coverage, and implementation behavior visible
in the current worktree.

Summary recommendation:

| Recommendation | Count |
|---|---:|
| Convert to relational backend coverage | 18 |
| Delete original ignored scenario | 10 |
| Total | 28 |

## Authorization: StudentAssessmentAuthorization.feature

File: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RoleNamedSecurity/StudentAssessmentAuthorization.feature`

Recommendation for all 10 scenarios: convert.

The scenarios are still valuable relationship authorization coverage for StudentAssessment CRUD
and query behavior. The current ignore reason is not that the behavior is obsolete; it is that
StudentAssessment is configured as `NamespaceBased` by default, while these scenarios need
`RelationshipsWithEdOrgsOnly`.

Conversion notes:

- Use the existing CMS claimset upload steps and `@ResetClaimsetsAfterScenario`.
- Upload a temporary claimset overriding StudentAssessment actions to
  `RelationshipsWithEdOrgsOnly`.
- Verify the claim resource name during conversion. CMS embedded claims use
  `http://ed-fi.org/identity/claims/ed-fi/studentAssessment`, while the no-further-auth additional
  claimset has an `ed-fi/studentAssessments` entry. Prefer the CMS claims hierarchy name unless a
  run proves the endpoint-name alias is accepted.
- Keep these in the relational authorization shard, likely `@relational-ci-shard-3`.
- Re-run and adjust only response text that has legitimately changed in current relational
  ProblemDetails.

| Scenario | Recommendation | Rationale |
|---|---|---|
| 01 Ensure authorized client can create a StudentAssessment | Convert | Valid create authorization path once StudentAssessment is forced to `RelationshipsWithEdOrgsOnly`. |
| 02.1 Ensure authorized client can get a StudentAssessment | Convert | Valid get-by-id authorization path. |
| 02.2 Ensure authorized client can get a StudentAssessment | Convert | Valid get-many/query authorization path with authorized visibility. |
| 03 Ensure authorized client can update a StudentAssessment | Convert | Valid update authorization path. |
| 04 Ensure authorized client can delete a StudentAssessment | Convert | Valid delete authorization path. |
| 05 Ensure unauthorized client can not create a StudentAssessment | Convert | Valid 403 create denial path. |
| 06.1 Ensure unauthorized client can not get a StudentAssessment by id | Convert | Valid 403 get-by-id denial path. |
| 06.2 Ensure unauthorized client can not get a StudentAssessment by query | Convert | Valid get-many filtering behavior; unauthorized rows should be hidden rather than returned. |
| 07 Ensure unauthorized client can not update a StudentAssessment | Convert | Valid update denial path. |
| 08 Ensure unauthorized client can not delete a StudentAssessment | Convert | Valid delete denial path. |

## General: DataStrictnessValidation.feature

File: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/General/DataStrictnessValidation.feature`

Current implementation evidence:

- `CoerceFromStringsMiddleware` coerces string `"true"`/`"false"` booleans with
  `Boolean.TryParse`.
- It does not coerce JSON numeric tokens into booleans.
- It also does not coerce string `"0"` or `"1"` into booleans.
- `CreateResourcesValidation.feature` already has active relational coverage for POST string
  coercion of numeric and boolean fields, but PUT boolean string coercion and invalid boolean
  values still justify focused DataStrictness coverage if the stale scenarios are cleaned up.

| Scenario | Recommendation | Rationale |
|---|---|---|
| API-236 / 04 Ensure clients can create a resource using numeric values for booleans | Delete | It expects numeric JSON `0` to be accepted as a boolean. That conflicts with the current contract. If numeric booleans should be tested, replace with a 400 negative case. |
| API-237 / 05 Ensure clients can update a resource using numeric values for booleans | Delete | Same obsolete numeric-boolean acceptance expectation as API-236, plus the scenario has no valid PUT setup/id. |
| API-238 / 06 Ensure clients cannot create a resource using incorrect values for booleans | Convert | The negative contract is still valid: numeric JSON `2` is not a boolean. Complete the pending 400 assertion with current ProblemDetails. |
| API-239 / 07 Ensure clients cannot create a resource using incorrect values for booleans | Delete | Duplicate of API-238 with the same payload and only a partial assertion. |
| API-241 / 09 Ensure clients can update a resource using expected booleans | Convert | Literal JSON boolean `false` on PUT is valid coverage, but the scenario needs proper setup, an id route such as `/ed-fi/classPeriods/{id}`, and current expectations. |
| API-242 / 10 Ensure clients can create a resource using expected booleans as string | Convert | String `"true"` coercion is supported by current middleware. This can be converted as focused DataStrictness coverage, though broader POST string coercion is already covered elsewhere. |
| API-243 / 11 Ensure clients can update a resource using expected booleans as strings | Convert | String `"false"` coercion should be valid on PUT, but the current scenario has stale setup and an expected `classPeriodName` mismatch. Fix those before unignoring. |
| API-244 / 12 Ensure clients can create a resource using numeric values as strings | Delete | String `"1"` is not accepted by the current boolean coercion contract. Keeping this would preserve an obsolete coercion expectation. |
| API-245 / 13 Ensure clients can update a resource using numeric values as strings | Delete | It uses POST while expecting 204, asserts the wrong boolean value, and conflicts with the current contract that `"0"` is not a boolean. |
| API-246 / 14 Ensure clients cannot update a resource that is using a different value type than boolean | Convert | Invalid string `"string"` should remain negative strictness coverage. Update the title/verb and stale error text. |
| API-247 / 15 Ensure clients cannot update a resource that is using a different value type than boolean | Convert | String `"0"` should be rejected under the current contract. Convert as negative coverage with corrected ProblemDetails. |

## Query Value Casing: URLValidation.feature and ResourceQueriesQueryString.feature

Files:

- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/General/URLValidation.feature`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/ResourceQueries/ResourceQueriesQueryString.feature`

Recommendation for all three ignored scenarios: delete.

These scenarios assert case-insensitive matching of string query values. Nearby active relational
scenarios already cover case-insensitive query parameter names. The relational read-path design
notes in `reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md`
explicitly call value casing unresolved and state a default bias toward ordinal/case-sensitive
string semantics. Current provider integration coverage also checks that changing a string query
value's case produces no results.

| Scenario | Recommendation | Rationale |
|---|---|---|
| URLValidation API-235 / 12 Ensure client can retrieve information through a case insensitive query | Delete | Obsolete DMS-799 cleanup. Query parameter name casing is covered by API-250; value casing should not be normalized unless the product contract changes. |
| ResourceQueries API-134 / 11 Ensure clients can GET information when querying with mixed case parameter name and value | Delete | The mixed-case parameter name part is already covered by active scenarios. The mixed-case value expectation conflicts with current relational value matching. |
| ResourceQueries API-135 / 12 Ensure clients can GET information when querying with mixed case parameter name and upper case value | Delete | Same as API-134; this is redundant if value matching remains case-sensitive. |

## References: UpdateReferenceValidation.feature

File: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/References/UpdateReferenceValidation.feature`

| Scenario | Recommendation | Rationale |
|---|---|---|
| API-114 / 05 Ensure clients cannot update a resource that is incorrect from a deep reference | Convert | The DMS-80 CourseOffering/Section referential identity defect appears covered by current relational integration tests for both PostgreSQL and MSSQL. The E2E scenario is still useful as PUT negative reference coverage, but clean the payload before conversion so it isolates the Section failure. |

Conversion notes:

- Current integration scenario `SectionReferentialIdentityScenario` proves a
  `StudentSectionAssociation` referencing a deep `Section` chain can be created successfully.
- The ignored E2E scenario currently changes `studentReference` to non-existent `604874` while
  also expecting only a Section unresolved-reference error. Either seed `604874` or keep the
  existing `604834` student so the intended deep Section reference failure is isolated.
- Prefer using all required `sectionReference` identity fields with a wrong value, similar to
  `CreateReferenceValidation.feature` API-082, instead of relying on omitted fields.

## Resources: CreateResourcesValidation.feature

File: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/CreateResourcesValidation.feature`

| Scenario | Recommendation | Rationale |
|---|---|---|
| API-173 / 25 Verify clients cannot POST a resource without permissions | Delete original | This scenario is stale and internally inconsistent. The feature background authenticates the SIS Vendor, the first request does not remove the Authorization header, `Given the user is authenticated` has no active step definition, and both branches expect the old missing-header payload. Current auth middleware returns `urn:ed-fi:api:unauthorized` with `Bearer token required` or `Invalid token`. |

Replacement guidance:

- Do not convert this scenario verbatim.
- If API-convention auth coverage is still desired here, replace it with two separate current-contract scenarios: missing/empty bearer token and invalid/expired token.
- Existing active security coverage already covers unauthenticated POST rejection and manipulated expired-token rejection in `Security/OwaspCriticalPaths.feature`, so deleting API-173 is acceptable unless a distinct API-conventions scenario is explicitly required.

## Security: OwaspCriticalPaths.feature

File: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/OwaspCriticalPaths.feature`

| Scenario | Recommendation | Rationale |
|---|---|---|
| 14 Explicit non JSON content type is rejected | Delete from DMS E2E source until implemented | The generic write path reads the request body without enforcing `application/json`. Profile-specific content type validation exists, but this baseline security behavior is still a real gap. Keep it as a backlog-owned security gap or external TODO, not an ignored source scenario. |


## Final Classification

Convert:

- StudentAssessmentAuthorization scenarios 01, 02.1, 02.2, 03, 04, 05, 06.1, 06.2, 07, 08.
- DataStrictness scenarios API-238, API-241, API-242, API-243, API-246, API-247.
- UpdateReferenceValidation scenario API-114.
- OwaspCriticalPaths scenario 15.

Delete original:

- DataStrictness scenarios API-236, API-237, API-239, API-244, API-245.
- URLValidation scenario API-235.
- ResourceQueriesQueryString scenarios API-134 and API-135.
- CreateResourcesValidation scenario API-173.

