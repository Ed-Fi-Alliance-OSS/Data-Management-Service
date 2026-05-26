---
jira: DMS-1184
jira_url: https://edfi.atlassian.net/browse/DMS-1184
---

# Story: Serve `/changeQueries/v1/availableChangeVersions`

## Description

Add the DMS runtime endpoint for `/changeQueries/v1/availableChangeVersions`.

The endpoint returns the same request and response contract as ODS. `oldestChangeVersion` remains hardcoded to `0`, and `newestChangeVersion` is backed by `dms.GetMaxChangeVersion()`.

## Acceptance Criteria

- `GET /changeQueries/v1/availableChangeVersions` is routed when the Change Queries OpenAPI surface is present.
- The endpoint returns `oldestChangeVersion` as `0`.
- The endpoint returns `newestChangeVersion` from `dms.GetMaxChangeVersion()`.
- The response body matches the ODS-compatible contract.
- Query string behavior matches ODS for this endpoint.
- If the route is not present in the effective OpenAPI surface, DMS returns the normal not-found response.
- Tests cover successful routing, response shape, and the database call to `dms.GetMaxChangeVersion()`.
- PostgreSQL and SQL Server coverage proves the endpoint works against both implementations of the function.

## Out of Scope

- `/deletes` and `/keyChanges`.
- Snapshot support.
- Runtime feature flag support for disabling Change Queries.
