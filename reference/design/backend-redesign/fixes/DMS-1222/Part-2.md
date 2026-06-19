# DMS-1222 Part 2 Ignored Scenario Audit


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

