---
jira: DMS-1183
jira_url: https://edfi.atlassian.net/browse/DMS-1183
---

# Story: Extend MetaEd and DMS OpenAPI for Change Queries

## Description

Extend the OpenAPI surface so Change Query routes and query parameters are advertised and routed consistently.

MetaEd owns emission of the OpenAPI definitions. DMS owns consuming the updated OpenAPI metadata so the relational backend can route and serve the new Change Query endpoints and live change-version filters.

This ticket is intentionally cross-project because the API contract is split across MetaEd generation and DMS runtime consumption.

## Acceptance Criteria

- MetaEd emits `/deletes` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/keyChanges` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/changeQueries/v1/availableChangeVersions`.
- MetaEd excludes `SchoolYearType` from emitted `/deletes` and `/keyChanges` OpenAPI definitions.
- MetaEd adds `minChangeVersion` and `maxChangeVersion` query parameters to live resource and descriptor GET-many endpoint definitions.
- MetaEd adds `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` query parameters to `/deletes` and `/keyChanges` definitions.
- Response schemas for `/deletes`, `/keyChanges`, and `/availableChangeVersions` match the ODS-compatible Change Queries contract.
- DMS effective-schema loading accepts and preserves the new OpenAPI paths and query parameters.
- DMS route resolution can distinguish live resource GET-many, `/deletes`, `/keyChanges`, and `/changeQueries/v1/availableChangeVersions`.
- DMS does not require a separate hard-coded route list for resources covered by the updated OpenAPI.
- Tests cover OpenAPI generation for regular resources, descriptors, and `SchoolYearType` exclusion.
- DMS tests cover startup/loading of the updated OpenAPI and route resolution for the new endpoints.

## Dependencies

- Effective schema loading and OpenAPI path normalization.
- Endpoint implementation tickets consume this contract:
  - `21-available-change-versions-endpoint.md`
  - `22-change-query-endpoint-foundation.md`
  - `23-deletes-endpoint.md`
  - `24-keychanges-endpoint.md`
  - `19-live-change-version-filters.md`

## Out of Scope

- Implementing the endpoint runtime behavior.
- Snapshot OpenAPI support.
- Runtime feature-flagging for Change Queries.
