---
jira: DMS-1187
jira_url: https://edfi.atlassian.net/browse/DMS-1187
---

# Story: Serve `/keyChanges` from Tracked-Change Rows

## Description

Implement resource and descriptor `/keyChanges` endpoints backed by tracked-change key-change rows.

The endpoint returns old and new identifying values in the requested change-version window. When a resource changes identity multiple times in a window, the endpoint collapses the sequence to the first old values and final new values for that resource ID, matching ODS behavior.

Descriptor identities are immutable in DMS v1, so descriptor `/keyChanges` endpoints exist but return an empty array. Concrete abstract resources (e.g., `School`, `LocalEducationAgency`, `OrganizationDepartment`) likewise return an empty array because their inherited identity (e.g., `EducationOrganizationId`) is immutable in practice; this matches the legacy ODS behavior for derived resources.

## Acceptance Criteria

- Each regular resource with Change Query support can serve `GET /data/v3/{schema}/{resource}/keyChanges`.
- Each descriptor with Change Query support can serve `GET /data/v3/{schema}/{descriptor}/keyChanges`.
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
- PostgreSQL and SQL Server tests cover regular resources, descriptors, collapsed windows, paging, and totalCount.

## Dependencies

- `16-tracked-change-trigger-rendering.md`.
- `22-change-query-endpoint-foundation.md`.

## Out of Scope

- `ReadChanges` authorization, handled by `25-readchanges-authorization.md`.
- `/deletes`.
- Snapshot support.
