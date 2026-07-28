---
jira: TBD
jira_url: TBD
---

# Story: Implement Snapshot ProblemDetails and Connection-Unavailable Translation

## Description

Implement the deferred snapshot failure contract from DMS-1190.

Map the routing outcomes from `39-snapshot-read-replica-runtime-routing.md` to the ODS-compatible missing-snapshot and mutation responses. Introduce a backend-neutral connection-unavailable exception at each read-path connection-open seam so an unreachable selected snapshot returns Snapshot Not Found without converting query, schema, authorization, or application defects into false `404` responses.

## Acceptance Criteria

### ProblemDetails contract

- `Use-Snapshot: true` on a resource or descriptor mutation returns:
  - type `urn:ed-fi:api:snapshots:method-not-allowed`;
  - title `Method Not Allowed with Snapshots`;
  - status `405`;
  - detail `An attempt was made to modify data in a Snapshot, but this data is read-only.`;
  - response header `Allow: GET`.
- A snapshot-eligible read with no configured usable snapshot returns the existing not-found ProblemDetails shape with type `urn:ed-fi:api:not-found`, title `Not Found`, status `404`, and detail `Snapshot not found.`.
- A selected snapshot whose database cannot be reached returns the same Snapshot Not Found `404`.
- Both responses use the shared DMS ProblemDetails envelope, request correlation identifier, and `application/problem+json`.
- The existing not-found failure factory is reused for the `404`; a snapshot-specific factory supplies the `405`.
- Authentication, tenant validation, client data-store authorization, parent route resolution, and non-database path validation retain precedence over snapshot failures.
- The snapshot mutation `405` is emitted after path validation but before fingerprint validation and without opening the primary, snapshot, or read-replica database.

### Connection failure classification

- A backend-neutral `DatabaseConnectionUnavailableException` distinguishes failures raised while opening a database connection from failures raised after a connection is open.
- The connection-open boundary is wrapped at all seven read-path seams:
  1. database fingerprint reader;
  2. PostgreSQL resource-key row reader;
  3. SQL Server resource-key row reader;
  4. PostgreSQL relational command executor;
  5. SQL Server relational command executor;
  6. PostgreSQL document hydrator;
  7. SQL Server document hydrator.
- Catalog absence, authentication failure, DNS or network failure, timeout, and firewall rejection during connection open are classified as connection unavailable.
- Translation to Snapshot Not Found occurs only when the request-scoped target kind is `Snapshot`.
- Read-replica connection failures retain the normal database-availability contract and never become Snapshot Not Found.
- A reachable snapshot missing `dms.EffectiveSchema`, a malformed fingerprint, or an effective-schema mismatch retains the existing provisioning or compatibility `503`.
- SQL, mapping, authorization, fingerprint-shape, and unexpected application failures retain their existing contracts.
- Provider exceptions are not translated wholesale. Any command-time transport-loss translation uses a narrowly defined provider-specific connectivity classification.
- Session-scoped write hydrators that receive an existing connection and transaction are not changed.
- Translated failures log the underlying error and selected target kind without logging the connection string.

### Tests

- Unit and integration tests verify the exact `404` and `405` ProblemDetails bodies, content type, correlation identifier, and `Allow: GET`.
- An unknown resource with `Use-Snapshot: true` retains its existing `404` rather than returning the snapshot mutation `405`.
- PostgreSQL and SQL Server tests cover snapshot-unavailable behavior at every applicable connection-open seam.
- Document-hydrator tests include a request whose fingerprint and resource-key validations are already cached so hydration is the first failing connection open.
- Tests prove a reachable but unprovisioned or fingerprint-incompatible snapshot returns the existing `503`.
- Tests prove ordinary query, mapping, and authorization failures against a snapshot are not translated.
- Tests prove read-replica connectivity failures retain the normal availability response.
- DMS E2E coverage includes mutation `405` plus `Allow: GET`, missing snapshot, and an unreachable configured snapshot.

## Dependencies

- `39-snapshot-read-replica-runtime-routing.md` supplies the effective target kind and typed missing-snapshot and mutation-rejection outcomes.

## Out of Scope

- OpenAPI declaration of the responses.
- Snapshot lifecycle tooling.
- Broad translation of provider `DbException` failures after connection acquisition.
