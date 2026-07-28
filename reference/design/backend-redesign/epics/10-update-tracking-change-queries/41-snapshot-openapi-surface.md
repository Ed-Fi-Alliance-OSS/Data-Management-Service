---
jira: TBD
jira_url: TBD
---

# Story: Add the Snapshot Contract to Served OpenAPI Documents

## Description

Restore the snapshot OpenAPI surface intentionally deferred from DMS v1.0.

Add the contract at the MetaEd/ApiSchema source and ensure every independently served resource, descriptor, profile, and Change Queries document defines all components it references. This is new contract work; it must not be implemented by changing backend authoritative fixture inputs that do not feed the served documents.

## Acceptance Criteria

- MetaEd/ApiSchema defines a reusable boolean `Use-Snapshot` header parameter with default `false`.
- Resource, descriptor, and profile GET-many and GET-by-id operations reference the parameter.
- Resource and descriptor `/deletes` and `/keyChanges` operations reference the parameter.
- `/changeQueries/v1/availableChangeVersions` references the parameter.
- Snapshot-eligible GET operations document Snapshot Not Found `404` with the exact runtime ProblemDetails contract from `40-snapshot-problem-details.md`.
- Resource and descriptor `POST`, `PUT`, and `DELETE` operations document the snapshot-specific `405`, its exact ProblemDetails schema, `application/problem+json`, and the `Allow: GET` response header.
- The reusable parameter and response components are defined in every independently served document that references them: resource, descriptor, profile, and standalone Change Queries documents.
- The standalone Change Queries document's own `components.parameters` and `components.responses` collections are populated; it does not rely on components from a sibling document.
- GET-many operations advertise the header consistently with GET-by-id even though the older ODS-derived fixture shape advertises it only for by-id.
- Read-replica routing adds no OpenAPI parameter or response field.
- Backend authoritative fixture inputs used for DDL and plan compilation are not edited to affect served OpenAPI.
- DMS document-assembly tests cover resource, descriptor, profile, and Change Queries documents.
- Tests resolve every local `$ref` within each independently served document and fail for missing sibling-only components.
- Tests verify the parameter type/default, operation coverage, exact ProblemDetails metadata, response content type, and `Allow` header declaration.

## Dependencies

- The response definitions must remain identical to `40-snapshot-problem-details.md`.

## Out of Scope

- Runtime connection routing or response emission.
- Documenting read-replica selection as an API request option.
- Changes to backend DDL/plan fixture inputs or generated relational goldens.
