---
jira: DMS-1183
jira_url: https://edfi.atlassian.net/browse/DMS-1183
---

# Story: Extend MetaEd and DMS OpenAPI for Change Queries

## Description

Extend the OpenAPI surface so Change Query routes and query parameters are advertised consistently.

MetaEd owns emission of the OpenAPI definitions. DMS owns consuming the updated OpenAPI metadata for discovery and documentation, while runtime resource Change Query routing is resolved from DMS's effective resource model rather than from OpenAPI paths.

`/changeQueries/v1/availableChangeVersions` is a fixed DMS runtime route. It is advertised in OpenAPI, but it is not generated from `ApiSchema.json` resource metadata.

This ticket is intentionally cross-project because the API contract is split across MetaEd generation and DMS runtime consumption.

## Acceptance Criteria

- MetaEd emits `/deletes` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/keyChanges` endpoint definitions for each resource and descriptor that supports `ReadChanges`.
- MetaEd emits `/changeQueries/v1/availableChangeVersions`.
- MetaEd adds `minChangeVersion` and `maxChangeVersion` query parameters to live resource and descriptor GET-many endpoint definitions.
- MetaEd adds `minChangeVersion`, `maxChangeVersion`, `limit`, `offset`, and `totalCount` query parameters to `/deletes` and `/keyChanges` definitions.
- Response schemas for `/deletes`, `/keyChanges`, and `/availableChangeVersions` match the ODS-compatible Change Queries contract.
- DMS effective-schema loading accepts and preserves the new OpenAPI paths and query parameters.
- DMS metadata endpoints can serve the updated OpenAPI definitions for discovery and documentation.
- DMS runtime route resolution for resource and descriptor `/deletes` and `/keyChanges` remains model-driven from the effective `ApiSchema.json` endpoint mappings and RelationalBackend `MappingSet.Model` / `ConcreteResourceModel` inventory.
- DMS runtime route resolution for `/changeQueries/v1/availableChangeVersions` is hardcoded and does not depend on `ApiSchema.json` or OpenAPI path presence.
- DMS does not require a separate hard-coded route list for resource and descriptor `/deletes` or `/keyChanges` endpoints.
- Tests cover OpenAPI generation for regular resources and descriptors.
- DMS tests cover startup/loading of the updated OpenAPI and verify that advertised Change Query resource and descriptor paths align with model-driven route classification.

## Out of Scope

- Implementing the endpoint runtime behavior.
- Snapshot OpenAPI support.
- Runtime feature-flagging for Change Queries.
