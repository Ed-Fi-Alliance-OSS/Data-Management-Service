---
jira: DMS-1189
jira_url: https://edfi.atlassian.net/browse/DMS-1189
---

# Story: Return `403` for Crafted `SchoolYearType` Change Query Requests

## Description

Preserve ODS behavior for crafted `SchoolYearType` Change Query requests.

MetaEd excludes `SchoolYearType` from emitted `/deletes` and `/keyChanges` OpenAPI definitions, but a request can still be crafted directly against the generic controller route. When that happens, DMS returns `403 Forbidden` because `ReadChanges` is not configured for `SchoolYearType`.

## Acceptance Criteria

- A crafted request to `/data/v3/ed-fi/schoolYearTypes/deletes` returns `403 Forbidden`.
- A crafted request to `/data/v3/ed-fi/schoolYearTypes/keyChanges` returns `403 Forbidden`.
- The failure is caused by missing `ReadChanges` authorization for `SchoolYearType`, not by treating the resource as unknown.
- The ProblemDetails response matches the authorization-denied shape defined in `auth.md`.
- Normal OpenAPI emission still excludes `SchoolYearType` `/deletes` and `/keyChanges` paths.
- Tests cover both `/deletes` and `/keyChanges`.

## Dependencies

- `20-openapi-change-query-surface.md`.
- `22-change-query-endpoint-foundation.md`.
- `25-readchanges-authorization.md`.

## Out of Scope

- Adding `SchoolYearType` to OpenAPI Change Query paths.
- Implementing `ReadChanges` for `SchoolYearType`.
