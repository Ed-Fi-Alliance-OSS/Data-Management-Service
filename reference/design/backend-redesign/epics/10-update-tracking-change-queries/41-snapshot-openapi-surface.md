---
jira: TBD
jira_url: TBD
---

# Story: Add the Snapshot Contract to Served OpenAPI Documents

## Description

Restore the snapshot OpenAPI surface intentionally deferred from DMS v1.0.

The contract has two halves in two different repositories. The `Use-Snapshot` parameter and the snapshot response components are authored in MetaEd and reach DMS only inside published ApiSchema packages; DMS then assembles and serves documents that must define every component they reference. This story owns the DMS half. It cannot be delivered by hand-editing OpenAPI in this repository, and it must not be implemented by changing backend authoritative fixture inputs that do not feed the served documents.

This split follows the precedent in `20-openapi-change-query-surface.md` (DMS-1183), which separated the already-delivered MetaEd contract from the DMS continuation that consumed it.

## Upstream Prerequisite

The following is MetaEd/ApiSchema work in the upstream repository, not DMS implementation work in this story. It is the input contract this story consumes.

- MetaEd defines a reusable boolean `Use-Snapshot` header parameter with default `false`.
- MetaEd references the parameter from resource, descriptor, and profile GET-many and GET-by-id operations, from resource and descriptor `/deletes` and `/keyChanges`, and from `/changeQueries/v1/availableChangeVersions`.
- MetaEd documents Snapshot Not Found `404` on snapshot-eligible GET operations, and the snapshot-specific `405` with its exact ProblemDetails schema, `application/problem+json`, and `Allow: GET` response header on resource and descriptor `POST`, `PUT`, and `DELETE`.
- MetaEd emits the reusable parameter and the snapshot response components into the `components` block of every independently served base document that references them: resources, descriptors, profiles, and the standalone `projectSchema.openApiBaseDocuments.changeQueries` document. The Change Queries document ships today with empty `parameters` and `responses` collections and no `$ref` of any kind, so it must be populated rather than assumed to inherit.
- MetaEd advertises the header on GET-many as well as GET-by-id, deliberately diverging from the older ODS-derived fixture shape that advertises it only for by-id.

### Dependency mechanics

- DMS consumes ApiSchema as NuGet packages pinned in `src/Directory.Packages.props` — `EdFi.DataStandard52.ApiSchema`, `EdFi.DataStandard52.TPDM.ApiSchema`, the Homograph and Sample variants, and the `EdFi.DataStandard61.*` equivalents. There is no in-repository source for the served OpenAPI documents.
- This story is therefore blocked until the upstream change is merged and published, and its first DMS commit is the package version bump in `src/Directory.Packages.props` covering every ApiSchema package whose served documents gain snapshot artifacts.
- The upstream change must be tracked as its own MetaEd ticket, created and linked before this story is scheduled. This story is not "done" on DMS-side assembly code alone; it is done when DMS serves the snapshot contract from published packages.
- If the upstream ticket cannot be scheduled in the same release, this story is split rather than started: the DMS assembly-and-reference-resolution work can land against a test fixture ahead of the package bump, but the served-surface acceptance criteria below cannot be verified without the published packages and must not be marked complete against a hand-edited document.
- The bump is expected to be hash-neutral. DMS-1183 established that `projectSchema.openApiBaseDocuments` is stripped before effective-schema hashing, model derivation, DDL generation, and mapping-pack selection, so an OpenAPI-only package change should not alter the effective-schema hash, require an `apiSchemaVersion` bump, or churn DDL and plan goldens. This story verifies that expectation rather than assuming it; a hash change means the upstream package carried more than the OpenAPI contract and must be investigated before the bump is accepted.

## Acceptance Criteria

### Package intake

- `src/Directory.Packages.props` is bumped to published ApiSchema package versions containing the snapshot OpenAPI contract, for every affected data-standard and extension package.
- The bump does not change the effective-schema hash and requires no `apiSchemaVersion` bump. Existing DDL, plan, and mapping-set goldens are unchanged by it.
- No served OpenAPI content is authored in this repository. The snapshot parameter and response components appear in served documents only because the packages supply them.
- Backend authoritative fixture inputs used for DDL and plan compilation are not edited to affect served OpenAPI.

### Served documents

- Resource, descriptor, and profile GET-many and GET-by-id operations serve the `Use-Snapshot` parameter.
- Resource and descriptor `/deletes` and `/keyChanges` operations serve the parameter.
- `/changeQueries/v1/availableChangeVersions` serves the parameter.
- Snapshot-eligible GET operations serve Snapshot Not Found `404` with the exact runtime ProblemDetails contract from `40-snapshot-problem-details.md`.
- Resource and descriptor `POST`, `PUT`, and `DELETE` operations serve the snapshot-specific `405`, its exact ProblemDetails schema, `application/problem+json`, and the `Allow: GET` response header.
- The reusable parameter and response components are present in every independently served document that references them: resource, descriptor, profile, and standalone Change Queries documents.
- The standalone Change Queries document's own `components.parameters` and `components.responses` collections are populated; it does not rely on components from a sibling document.
- GET-many operations advertise the header consistently with GET-by-id.
- DMS document assembly preserves the snapshot components through the injection it already performs for `servers`, `components.securitySchemes.oauth2_client_credentials`, and the root `security` requirement, so nothing added by DMS drops or overwrites them.
- Read-replica routing adds no OpenAPI parameter or response field.

### Tests

- DMS document-assembly tests cover resource, descriptor, profile, and Change Queries documents.
- Tests resolve every local `$ref` within each independently served document and fail for missing sibling-only components. This check is written so it fails on the pre-bump packages, proving it detects the gap rather than passing vacuously.
- Tests verify the parameter type/default, operation coverage, exact ProblemDetails metadata, response content type, and `Allow` header declaration.
- A test asserts the served snapshot response metadata matches the runtime contract in `40-snapshot-problem-details.md` exactly, so the two cannot drift independently.
- Effective-schema hash and golden-stability coverage confirms the package bump is OpenAPI-only.

## Dependencies

- An upstream MetaEd/ApiSchema ticket delivering the § Upstream Prerequisite contract, and published ApiSchema packages containing it. This is a hard, cross-repository dependency; see § Dependency mechanics.
- The response definitions must remain identical to `40-snapshot-problem-details.md`.

## Out of Scope

- Authoring the MetaEd/ApiSchema source change, which belongs to the upstream ticket.
- Runtime connection routing or response emission.
- Documenting read-replica selection as an API request option.
- Changes to backend DDL/plan fixture inputs or generated relational goldens.
