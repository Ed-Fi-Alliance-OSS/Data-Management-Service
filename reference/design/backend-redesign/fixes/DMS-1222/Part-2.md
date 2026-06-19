# DMS-1222 Part 2 Remaining Ignored Scenario Plan

This is a current-state follow-up to Part 2 of
`reference/design/backend-redesign/fixes/DMS-1222.md`.

I did not run E2E tests. This plan is based on the enabled `.feature` sources, generated
`.feature.cs` metadata, nearby converted relational scenarios, and current middleware/test helper
behavior in the worktree. `API-238` and `API-239` were deleted from
`DataStrictnessValidation.feature` after the DMS-1225 review showed numeric boolean coercion belongs
to that ODS parity story rather than this cleanup.

Scope note: ignored scenarios in `.feature.disabled` files are not included. They are outside the
enabled DMS E2E conversion set described by DMS-1222 Part 2.

Current enabled-source inventory:

| Area | Remaining ignored scenarios | Recommendation |
|---|---:|---|
| `Authorization/RoleNamedSecurity/StudentAssessmentAuthorization.feature` | 10 | Convert all |
| `Security/OwaspCriticalPaths.feature` | 1 | Convert |
| Total | 15 | Convert all |

Original Part 2 buckets that no longer have enabled ignored scenarios: URL/query value casing
(`API-235`, `API-134`, `API-135`), deep reference update (`API-114`), stale create auth
(`API-173`), numeric boolean coercion cases (`API-236`, `API-237`, `API-238`, `API-239`,
`API-244`, `API-245`, `API-247`), and the explicit non-JSON security gap scenario.

## Authorization: StudentAssessmentAuthorization.feature

File:
`src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/RoleNamedSecurity/StudentAssessmentAuthorization.feature`

Recommendation: convert all 10 scenarios.

These scenarios are still useful relationship-authorization coverage for StudentAssessment CRUD and
query behavior. The ignore reason is specific to authorization configuration:
`StudentAssessment` defaults to namespace-based authorization, while these scenarios are intended to
exercise `RelationshipsWithEdOrgsOnly`.

Implementation plan:

- Replace the Rule-level `@ignore` tags with relational tags. Use `@relational-backend` and likely
  `@relational-ci-shard-3`, matching nearby role-named security coverage.
- Add `@ResetClaimsetsAfterScenario`, either at the feature level or on both Rules, because the
  conversion needs a temporary claimset upload.
- In each Rule background, before authenticating the scenario actor, upload a temporary claimset
  that grants StudentAssessment access with the `RelationshipsWithEdOrgsOnly` authorization
  strategy.
- Use the singular lower-camel claim resource name `studentAssessment` in the upload step:
  `Given a claim set is uploaded to CMS that grants "studentAssessment" access to
  "E2E-StudentAssessmentRelationshipsClaimSet" using authorization strategy
  "RelationshipsWithEdOrgsOnly"`.
- Do not use the plural endpoint path `studentAssessments` for the CMS upload unless a test run
  proves it is accepted. The authoritative claims composition contains
  `http://ed-fi.org/identity/claims/ed-fi/studentAssessment`, and the upload helper builds the
  claim URI directly from the supplied endpoint text.
- Keep the feature-level background seeding as-is unless a run proves it must change. The Rule
  background can upload/authenticate the temporary claimset after that seed data is created.
- Update the unauthorized response expectations to the current relational authorization
  ProblemDetails. Nearby converted role-named security scenarios use
  `urn:ed-fi:api:security:authorization` without the trailing colon, and the error wording may also
  differ from these stale expectations.
- Regenerate the committed `.feature.cs` file after changing tags. The current generated file
  carries the Rule-level ignore through `TagHelper.CombineTags(...)`, not through an obvious
  NUnit `IgnoreAttribute`.

| Scenario | Recommendation | Notes |
|---|---|---|
| 01 Ensure authorized client can create a StudentAssessment | Convert | Positive create path once StudentAssessment is forced to `RelationshipsWithEdOrgsOnly`. |
| 02.1 Ensure authorized client can get a StudentAssessment | Convert | Positive get-by-id path. |
| 02.2 Ensure authorized client can get a StudentAssessment | Convert | Positive query/get-many path. |
| 03 Ensure authorized client can update a StudentAssessment | Convert | Positive update path. |
| 04 Ensure authorized client can delete a StudentAssessment | Convert | Positive delete path. |
| 05 Ensure unauthorized client can not create a StudentAssessment | Convert | Negative create path; refresh 403 body. |
| 06.1 Ensure unauthorized client can not get a StudentAssessment by id | Convert | Negative get-by-id path; refresh 403 body. |
| 06.2 Ensure unauthorized client can not get a StudentAssessment by query | Convert | Query filtering should still return an empty result for unauthorized rows. |
| 07 Ensure unauthorized client can not update a StudentAssessment | Convert | Negative update path; refresh 403 body. |
| 08 Ensure unauthorized client can not delete a StudentAssessment | Convert | Negative delete path; refresh 403 body. |

