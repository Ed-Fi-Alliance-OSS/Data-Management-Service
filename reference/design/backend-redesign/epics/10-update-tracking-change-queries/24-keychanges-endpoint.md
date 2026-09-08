---
jira: DMS-1187
jira_url: https://edfi.atlassian.net/browse/DMS-1187
---

# Story: Serve `/keyChanges` from Tracked-Change Rows

## Description

Implement resource and descriptor `/keyChanges` endpoints backed by tracked-change key-change rows.

The endpoint returns old and new identifying values in the requested change-version window. When a resource changes identity multiple times in a window, the endpoint collapses the sequence to the first old values and final new values for that resource ID, matching ODS behavior.

Descriptor identities are immutable in DMS v1, so descriptor `/keyChanges` endpoints exist but return an empty array. Concrete abstract resources (e.g., `School`, `LocalEducationAgency`, `OrganizationDepartment`) likewise return an empty array because their inherited identity (e.g., `EducationOrganizationId`) is immutable in practice; this matches the legacy ODS behavior for derived resources.

This story reuses the shared Change Query endpoint foundation established by
`23-deletes-endpoint.md`.

Runtime resource and descriptor route resolution uses the shared model-driven foundation from `23-deletes-endpoint.md`: classify the trailing `/keyChanges` segment, resolve `{schema}/{resource}` through the effective `ApiSchema.json` endpoint mappings, and resolve the corresponding RelationalBackend `ConcreteResourceModel` or descriptor discriminator from the compiled `MappingSet.Model`. OpenAPI advertises the endpoint surface but is not the runtime source of truth.

## Acceptance Criteria

- Each regular resource with Change Query support can serve `GET /data/v3/{schema}/{resource}/keyChanges`.
- Each descriptor with Change Query support can serve `GET /data/v3/{schema}/{descriptor}/keyChanges`.
- The endpoint reuses the shared Change Query route/resource resolution, paging, totalCount, and response-shaping foundation from `23-deletes-endpoint.md`.
- Route/resource resolution is driven by effective `ApiSchema.json` endpoint mappings and `MappingSet.Model`, not OpenAPI paths.
- Regular resource key changes are selected from tracked-change rows where an appropriate `New_*` identity column is not null.
- The endpoint filters by `minChangeVersion` and `maxChangeVersion`.
- The endpoint supports `limit`, `offset`, and `totalCount`.
- Multiple identity changes for the same `Id` inside one window collapse to one response item using the earliest old values and latest new values.
- The response item `changeVersion` is the final change version in the collapsed window.
- Response `oldKeyValues` and `newKeyValues` use public field names from `queryFieldMapping`.
- Descriptor reference values inside `oldKeyValues` and `newKeyValues` compose the tracked `Namespace` and `CodeValue` values as a single string in `"<namespace>#<codeValue>"` form.
- Descriptor `/keyChanges` endpoints return an empty array and support paging and totalCount behavior consistently.
- Concrete abstract resource `/keyChanges` endpoints (e.g., `School`, `LocalEducationAgency`, `OrganizationDepartment`) return an empty array and support paging and totalCount behavior consistently, matching legacy ODS behavior for derived resources.
- Cascading key-change scenarios are covered.
- A totalCount regression comparable to ODS-5423 is covered.
- Descriptor empty-result behavior comparable to ODS-5422 is covered.
- Concrete-abstract-resource empty-result behavior is covered alongside the descriptor case.
- Tests cover regular resources, descriptors, collapsed windows, paging, and totalCount.
- E2E tests cover common and edge-case scenarios.

## Out of Scope

- `ReadChanges` authorization, split across `25-readchanges-authorization.md` and `27-no-further-and-namespace-readchanges-authorization.md`.
- `/deletes`.
- Snapshot support.
