---
jira: DMS-1189
jira_url: https://edfi.atlassian.net/browse/DMS-1189
---

# Story: Return `403` for Crafted `SchoolYearType` Change Query Requests

## Description

Preserve ODS behavior for crafted `SchoolYearType` Change Query requests.

MetaEd excludes `SchoolYearType` from emitted `/deletes` and `/keyChanges` OpenAPI definitions, but a request can still be crafted directly against the generic Change Query route shape. When that happens, DMS classifies the `/deletes` or `/keyChanges` suffix, resolves `ed-fi/schoolYearTypes` as a known resource through the effective resource model, and returns `403 Forbidden` because `ReadChanges` is not configured for `SchoolYearType`.

## Acceptance Criteria

- A crafted request to `/data/v3/ed-fi/schoolYearTypes/deletes` returns `403 Forbidden`.
- A crafted request to `/data/v3/ed-fi/schoolYearTypes/keyChanges` returns `403 Forbidden`.
- The failure is caused by missing `ReadChanges` authorization for `SchoolYearType`, not by treating the resource as unknown.
- The generic Change Query route classification recognizes the `/deletes` and `/keyChanges` suffixes even though `SchoolYearType` paths are not emitted in OpenAPI.
- Resource resolution finds `ed-fi/schoolYearTypes` in the effective resource model and reaches `ReadChanges` authorization for that known resource.
- Tests assert the crafted requests do not take the unknown-resource or route-not-found path.
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
