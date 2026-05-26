---
jira: DMS-1183
jira_url: https://edfi.atlassian.net/browse/DMS-1183
---

# Story: Extend MetaEd and DMS OpenAPI for Change Queries

## Description

Extend the OpenAPI surface so Change Query routes and query parameters are advertised consistently.

MetaEd owns emission of the OpenAPI definitions. DMS owns consuming the updated OpenAPI metadata for discovery and documentation, while runtime resource Change Query routing is resolved from the effective resource model.

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
- DMS does not treat OpenAPI path presence as the sole source of truth for resource `/deletes` and `/keyChanges` routing; known resources excluded from the OpenAPI Change Query surface still resolve through the effective resource model.
- DMS does not require a separate hard-coded route list for resource and descriptor `/deletes` or `/keyChanges` endpoints.
- Tests cover OpenAPI generation for regular resources, descriptors, and `SchoolYearType` exclusion.
- DMS tests cover startup/loading of the updated OpenAPI, route classification for advertised Change Query endpoints, and model-driven resolution for known resources excluded from the OpenAPI Change Query surface.

## Out of Scope

- Implementing the endpoint runtime behavior.
- Snapshot OpenAPI support.
- Runtime feature-flagging for Change Queries.
