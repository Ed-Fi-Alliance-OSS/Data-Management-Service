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

- The relational database already contains `auth.EducationOrganizationIdToEducationOrganizationId`, `auth.EducationOrganizationIdToStudentDocumentId`, `auth.EducationOrganizationIdToContactDocumentId`, `auth.EducationOrganizationIdToStaffDocumentId`, and `auth.EducationOrganizationIdToStudentDocumentIdThroughResponsibility`.
- Related completed implementation tickets: DMS-902 implemented the original token_info endpoint, DMS-1049 emitted the relational EdOrg hierarchy auth table, and DMS-1096 verified emitted auth DB objects.

## Implementation Design Notes

### token_info EdOrg Lookup Boundary

Split the `/oauth/token_info` education organization lookup out of `IAuthorizationRepository` for this story.

The existing `IAuthorizationRepository` is a broad legacy authorization repository. In addition to `GetTokenInfoEducationOrganizations(...)`, it exposes legacy relationship lookup methods such as:

- `GetAncestorEducationOrganizationIds(...)`
- `GetEducationOrganizationsForStudent(...)`
- `GetEducationOrganizationsForStudentResponsibility(...)`
- `GetEducationOrganizationsForStaff(...)`
- `GetEducationOrganizationsForContact(...)`

Those methods are consumed by the old Core relationship authorization validators. They should not be converted wholesale or swapped behind the existing interface as part of this story, because doing so would risk changing non-token resource authorization behavior that this story explicitly leaves out of scope.

For the relational backend, normal resource authorization should continue through the relational authorization planner and SQL executor path, using typed request authorization context, mapping-set metadata, and the provisioned `auth.*` companion objects. The relational resource endpoint path should not depend on the old Core `IAuthorizationRepository` methods. If a relational GET-many, GET-by-id, POST, PUT, or DELETE path still reaches those old repository methods, treat that as a relational authorization boundary issue to remove in the owning resource-authorization story, not as a reason to implement a broad relational `IAuthorizationRepository`.

The `/oauth/token_info` endpoint is different: it returns a response projection, not an authorization decision. Its EdOrg portion needs to produce `education_organization_id`, `name_of_institution`, `type`, and ancestor fields for introspection. That makes it a narrow endpoint-specific lookup that can safely be separated from the legacy authorization repository.

Introduce a narrow service contract for this purpose, for example:

```csharp
public interface ITokenInfoEducationOrganizationLookup
{
    Task<IEnumerable<TokenInfoEducationOrganization>> GetEducationOrganizations(
        IReadOnlyCollection<EducationOrganizationId> educationOrganizationIds
    );
}
```

`GetTokenInfoHandler` should depend on this narrow service rather than resolving `IAuthorizationRepository` directly.

### DI and Implementations

Register implementations as follows:

- Non-relational/default path: register an adapter implementation of `ITokenInfoEducationOrganizationLookup` that delegates to the existing `IAuthorizationRepository.GetTokenInfoEducationOrganizations(...)`. This preserves current non-relational token_info behavior.
- Relational PostgreSQL path: when `AppSettings__UseRelationalBackend=true` and the datastore is PostgreSQL, register the PostgreSQL relational implementation of `ITokenInfoEducationOrganizationLookup`.
- Relational SQL Server path: when `AppSettings__UseRelationalBackend=true` and the datastore is SQL Server, register the SQL Server relational implementation of `ITokenInfoEducationOrganizationLookup`.

Do not replace the existing `IAuthorizationRepository` registration for this story. The existing registration remains available for legacy/non-relational authorization validators and for the non-relational token_info adapter.

### Relational Service Responsibilities

The relational `ITokenInfoEducationOrganizationLookup` implementation owns only the token_info EdOrg response projection. It should:

- Build the token_info-specific concrete EducationOrganization projection from the runtime relational mapping set.
- Use the provisioned `auth.EducationOrganizationIdToEducationOrganizationId` table for reachable EdOrg and ancestor relationships.
- Add direct self rows for claimed EducationOrganization IDs that resolve to concrete EdOrg rows, preserving the direct EdOrg claim rule even if the auth hierarchy self tuple is absent.
- Ignore unresolved claimed EdOrg IDs and stale auth hierarchy tuples as described in Answers 2.1 and 2.2.
- Return an empty result for empty or fully unresolved EdOrg claim lists without causing `/oauth/token_info` to fail.
- Avoid any dependency on `dms.EducationOrganizationHierarchy` or legacy document-store-only authorization tables.

### Long-Term Direction

A complete relational backend switch should not require a relational implementation of the full legacy `IAuthorizationRepository` surface. The expected end state is:

- `IAuthorizationRepository` remains a legacy/non-relational authorization repository while the legacy backend exists.
- Resource authorization in relational mode is handled by the relational planner/executor path and typed relational authorization context.
- `ITokenInfoEducationOrganizationLookup` remains a token_info-specific projection service because token introspection has response-shaping needs that are distinct from resource authorization checks.
- When the legacy backend is removed, the remaining `IAuthorizationRepository` methods can be retired or moved with the legacy validators, rather than preserved as a relational abstraction.

## Clarifying Questions and Answers

### Questions 1

1. Is this story scoped to PostgreSQL relational token_info only, matching the observed `42P01` failure and E2E acceptance, or should it also add the SQL Server relational implementation and test coverage now?
2. What is the canonical relational source for token_info EdOrg projection data: a new dialect-specific union over concrete EducationOrganization root tables, an expanded `EducationOrganization_View`, the abstract identity table plus mapping metadata, or another existing runtime model/query path?
3. How should relational discriminator values such as `Ed-Fi:School` be converted into the existing token_info `type` value and ancestor property names, especially for extension EducationOrganization members or non-`Ed-Fi` project names?
4. Should token_info include a claimed EducationOrganization by direct claim match if `auth.EducationOrganizationIdToEducationOrganizationId` does not yet contain its self tuple, or should the endpoint fail/omit it until the auth hierarchy table is populated?
5. When `AppSettings__UseRelationalBackend=true`, should DI replace `IAuthorizationRepository.GetTokenInfoEducationOrganizations` with a relational implementation, or should token_info get a narrower relational service while existing non-token authorization repository behavior remains unchanged?
6. Should the existing `Features/Security/TokenIntrospection.feature` scenario 01 be retagged with `@relational-backend` as the main E2E signal, or should this story add a separate focused relational token_info scenario and leave the legacy scenario untagged?

### Answers 1

1. Implement the relational token_info EdOrg lookup for both PostgreSQL and SQL Server. PostgreSQL E2E coverage is required because it proves the observed `42P01` regression is gone; SQL Server should have backend integration or query-shape coverage for the same relational lookup contract, but does not need a separate DMS E2E scenario for this story.

2. Use a token_info-specific relational EdOrg projection derived from the runtime relational mapping set: union the concrete EducationOrganization root tables, selecting `DocumentId`, `EducationOrganizationId`, `NameOfInstitution`, and the relational discriminator. Join that projection to `auth.EducationOrganizationIdToEducationOrganizationId` for accessible and ancestor rows. Do not expand the general `EducationOrganization_View`; its contract is the abstract identity select list, and `NameOfInstitution` is payload data outside that contract.

3. Parse relational discriminators as `ProjectName:ResourceName`. For the existing standard Ed-Fi response, convert `Ed-Fi:School` to `type: "edfi.School"` and use only `ResourceName` for ancestor property names, e.g. `school_id`, preserving the current token_info contract. For extension or non-Ed-Fi EducationOrganization members, resolve `ProjectName` through the loaded `ApiSchemaDocuments` to its `projectEndpointName`; emit `type` as `{projectEndpointName}.{ResourceName}` and still derive the ancestor id property from `ResourceName`, e.g. `custom_education_organization_id`.

4. Include a claimed EducationOrganization by direct claim match when the concrete EdOrg row exists, even if `auth.EducationOrganizationIdToEducationOrganizationId` is missing the self tuple. This follows the relational authorization design's direct EdOrg claim rule. The relational token_info query should add a direct self row for claimed EdOrg IDs to the hierarchy rows it uses for projection; it should not fail or omit the claimed EdOrg solely because the auth hierarchy self tuple is absent.

5. Add a narrower token_info EdOrg lookup service and register the relational implementation when `AppSettings__UseRelationalBackend=true`. Keep the existing `IAuthorizationRepository` registration and non-token authorization behavior unchanged. For the non-relational path, adapt the new service to the existing `IAuthorizationRepository.GetTokenInfoEducationOrganizations` implementation so current token_info behavior is preserved.

6. Retag `Features/Security/TokenIntrospection.feature` scenario 01 with `@relational-backend` and the appropriate relational shard tag as the main DMS E2E signal. It already uses real CMS application/token wiring and validates the hierarchy-shaped token_info response. Do not add a duplicate relational-only E2E scenario for the same happy path; cover empty EdOrg claims and SQL Server-specific query behavior with focused backend tests.

### Questions 2

1. If a token contains claimed EducationOrganization IDs that do not resolve to concrete relational EducationOrganization rows, should token_info omit only those missing IDs and still return 200 for any resolvable reachable rows, return an empty `education_organizations` array when none resolve, or surface a security/configuration error?
2. If `auth.EducationOrganizationIdToEducationOrganizationId` contains reachable Source/Target tuples whose source or target EducationOrganization row cannot be found in the concrete EdOrg projection, should relational token_info ignore the stale tuple, omit only the affected EdOrg or ancestor field, or fail as a security/configuration error?
3. Should this story add focused automated coverage for extension or non-Ed-Fi EducationOrganization discriminator rendering, where `type` uses `projectEndpointName` and ancestor property names use `ResourceName`, or is implementation support without dedicated coverage acceptable?
4. For SQL Server, is deterministic query-shape and parameter-binding coverage sufficient for acceptance, or is a provider-backed SQL Server integration test expected before this story is considered complete?

### Answers 2

1. Omit unresolved claimed EducationOrganization IDs and still return 200. The relational lookup should first resolve claimed IDs against the concrete EdOrg projection; missing claims do not produce token_info rows and do not become a security/configuration error. If no claimed ID resolves, return an empty `education_organizations` array while preserving the rest of the token_info response.

2. Treat auth hierarchy tuples with missing source or target projection rows as stale data and ignore the stale tuple. A missing target projection omits that reachable EducationOrganization unless it is reachable through another valid tuple or direct claim. A missing source/ancestor projection omits only that ancestor field for otherwise valid returned EdOrg rows. Do not fail `/oauth/token_info` for these stale tuples.

3. Add focused automated coverage for extension or non-Ed-Fi discriminator rendering. This should be unit-level mapper/formatter coverage using a synthetic loaded schema that proves `ProjectName` resolves to `projectEndpointName` for `type`, and `ResourceName` drives snake_case ancestor property names. Do not add a new extension E2E scenario for this story.

4. Add a provider-backed SQL Server backend integration test before considering the story complete. Deterministic SQL shape and parameter-binding tests are still useful, but they are not sufficient by themselves for this cross-dialect relational lookup. The SQL Server integration should execute the relational token_info EdOrg lookup against real generated SQL and prove the same projection contract as PostgreSQL; no SQL Server DMS E2E scenario is required.
