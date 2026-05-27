---
jira: DMS-1190
jira_url: https://edfi.atlassian.net/browse/DMS-1190
---

# Spike: Snapshot and Read-replica Support for Change Queries

## Description

ODS supports a `Use-Snapshot: true` request header on live resource/descriptor GET-many endpoints, `/deletes`, `/keyChanges`, and `/availableChangeVersions`. The header redirects the request to a configured per-instance snapshot connection string in `dbo.OdsInstanceDerivative`. Snapshots isolate extraction from concurrent writes and address two sync-failure scenarios documented in `reference/design/backend-redesign/design-docs/change-queries.md` — "Using limit/offset without using snapshots" and "Unresolved references when not using snapshots".

DMS v1.0 silently ignores the `Use-Snapshot` header (see § "Snapshot support is deferred"). The Ed-Fi API Publisher's `EdFiApiSourceIsolationApplicator` sends the header by default for API major version ≥ 7, so reads from a DMS v1.0 source are not snapshot-isolated; operators are guided to run with `--ignoreIsolation=true` as an explicit acknowledgment. This spike investigates restoring snapshot support in DMS.

Consider that CMS already supports storing derivatives.

## Acceptance Criteria

- Specify the routing change: how the runtime selects the snapshot/read-replica connection at request scope, how it interacts with the existing data-source resolver, and what endpoints support snapshots/read-replicas.
- Specify the deferred ProblemDetails from `change-queries.md` § "Snapshot ProblemDetails Are Deferred":
  - `urn:ed-fi:api:snapshots:method-not-allowed` (405) with `Allow: GET` header for non-`GET` requests carrying `Use-Snapshot: true`.
  - `urn:ed-fi:api:not-found` (404) "Snapshot not found." when no snapshot connection string is configured, or when the configured snapshot database is unreachable.
- Decide whether DMS provides DB-engine-specific snapshot creation tooling or leaves snapshot creation/teardown to the operator (ODS does the latter).
- Once the proposal is reviewed and approved, create the implementation tickets covering CMS/admin-DB shape, runtime routing, ProblemDetails emission, OpenAPI surface updates, and API Publisher interoperability validation. Link those follow-on tickets back to this spike.
