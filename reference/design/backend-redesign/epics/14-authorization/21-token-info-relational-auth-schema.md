---
jira: DMS-1207
jira_url: https://edfi.atlassian.net/browse/DMS-1207
---

# Story: Convert token_info Education Organization Lookup to Relational Auth Schema

## Description

Convert the education organization portion of the `/oauth/token_info` endpoint to use the relational authorization schema when DMS is running with `AppSettings__UseRelationalBackend=true`.

The current endpoint path calls `IAuthorizationRepository.GetTokenInfoEducationOrganizations(...)`. In the relational backend Docker environment this is still served by `EdFi.DataManagementService.Old.Postgresql.PostgresqlAuthorizationRepository`, whose SQL reads `dms.EducationOrganizationHierarchy`. The relational primary store provisions `auth.EducationOrganizationIdToEducationOrganizationId` and the people auth views instead, so `/oauth/token_info` can fail with PostgreSQL error `42P01: relation "dms.educationorganizationhierarchy" does not exist` even though normal relational authorization requests use the new auth objects successfully.

This story owns the endpoint-specific conversion. It does not change the semantics of relationship authorization for normal resource GET-many, GET-by-id, POST, PUT, or DELETE operations.

## Acceptance Criteria

- With the relational backend enabled, `POST /oauth/token_info` returns 200 for a valid token whose application has education organization IDs.
- The response includes the expected token context fields, including `claim_set`, `education_organizations`, `namespace_prefixes`, `resources`, and `services`, preserving the existing token_info response contract.
- The relational token_info education organization lookup uses the provisioned `auth.EducationOrganizationIdToEducationOrganizationId` data, plus relational resource/document data needed to return:
  - `education_organization_id`
  - `name_of_institution`
  - `type`
  - ancestor education organization fields
- The relational token_info path does not depend on `dms.EducationOrganizationHierarchy` or any other legacy document-store-only hierarchy table.
- Empty education organization claim lists return an empty `education_organizations` array without a server error.
- PostgreSQL relational backend coverage proves the endpoint no longer returns `42P01` for the missing legacy hierarchy table.
- Add focused automated coverage for the relational path. Prefer a DMS E2E scenario using real CMS application/token wiring, with backend integration or unit coverage for the relational query shape as appropriate.
- Existing non-relational token_info behavior remains unchanged.

## Notes

- This gap was found while running `authorization-relational-demo.http` with httpYac against a freshly provisioned relational E2E database.
- The relational database already contains `auth.EducationOrganizationIdToEducationOrganizationId`, `auth.EducationOrganizationIdToStudentDocumentId`, `auth.EducationOrganizationIdToContactDocumentId`, `auth.EducationOrganizationIdToStaffDocumentId`, and `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`.
- Related completed implementation tickets: DMS-902 implemented the original token_info endpoint, DMS-1049 emitted the relational EdOrg hierarchy auth table, and DMS-1096 verified emitted auth DB objects.
