---
jira: TBD
jira_url: TBD
---

# Story: Implement Snapshot ProblemDetails and Connection-Unavailable Translation

## Description

Implement the deferred snapshot failure contract from DMS-1190.

Map the routing outcomes from `39-snapshot-read-replica-runtime-routing.md` to the ODS-compatible missing-snapshot and mutation responses. Introduce a backend-neutral connection-unavailable exception at each read-path connection-acquisition seam so an unreachable or provider-invalid selected snapshot returns Snapshot Not Found without converting query, schema, authorization, or application defects into false `404` responses.

## Acceptance Criteria

### ProblemDetails contract

- A successfully parsed `Use-Snapshot: true` on any non-`GET` resource or descriptor request, including an invalid route shape such as a collection `DELETE`, a collection `PUT`, or an item `POST`, returns:
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
- The snapshot mutation `405` is emitted after endpoint validation but before route-semantics, content-type, body, profile, coercion, document, resource-key, or fingerprint validation, and without opening the primary, snapshot, or read-replica database. It intentionally preempts the generic route-semantics `405` as well as the `415` or `400` responses those later validations would otherwise produce.
- `Allow: GET` is correct on this response because it states what is permitted in snapshot context, where the target is read-only, rather than what the route would permit on the primary.
- This precedence depends on the pipeline placement delivered by `39-snapshot-read-replica-runtime-routing.md`, which moves endpoint validation ahead of `ValidateDatabaseFingerprintMiddleware` and `ValidateResourceKeySeedMiddleware` and inserts target selection ahead of `ValidateRouteSemanticsMiddleware`. This story asserts the resulting responses; it does not re-implement that placement. If endpoint validation is not hoisted, an unknown resource carrying `Use-Snapshot: true` cannot return its existing `404` before snapshot policy runs.
- The snapshot `405` does displace the generic route-semantics `405` when the header parses `true`. A collection `DELETE`, a collection `PUT`, or an item `POST` carrying a parsed `true` returns the snapshot `405` with `Allow: GET` and `application/problem+json`. The same request without the header, or with `Use-Snapshot: false`, keeps the response `ValidateRouteSemanticsMiddleware` already produces through `FailureResponse.ForMethodNotAllowed`: its own type, title, and detail, content type `application/json; charset=utf-8`, and no `Allow` header.
- Because both are `405`, tests distinguishing them assert type, title, detail, content type, and the presence or absence of `Allow`. A status-code assertion alone cannot tell the two apart and does not satisfy this criterion.

### Connection failure classification

- A backend-neutral `DatabaseConnectionUnavailableException` distinguishes failures raised while **acquiring** a database connection from failures raised after a connection is open. Acquisition spans provider data-source and connection construction, connection-string parsing, and the open call.
- The boundary covers construction and parsing rather than the open call alone. At every baseline seam the provider parses the connection string on a statement that precedes the open — PostgreSQL while the pooled data source is built, SQL Server while `SqlConnectionStringBuilder` constructs the derivative effective string and again in the `SqlConnection` constructor — so a wrap around the open call alone would let a provider-invalid connection string escape as an unhandled provider argument failure at all seven baseline seams. Story 39's derivative-only `PoolBlockingPeriod.NeverBlock` normalization occurs inside this boundary.
- The connection-acquisition boundary is wrapped at all seven baseline read-path seams, each covering that seam's construction, parsing, and open:
  1. database fingerprint reader;
  2. PostgreSQL resource-key row reader;
  3. SQL Server resource-key row reader;
  4. PostgreSQL relational command executor;
  5. SQL Server relational command executor;
  6. PostgreSQL document hydrator;
  7. SQL Server document hydrator.
- Catalog absence, authentication failure, DNS or network failure, timeout, and firewall rejection during connection acquisition are classified as connection unavailable.
- A connection string that decrypts to a non-blank but provider-invalid value is also classified as connection unavailable, because the provider rejects it during construction or parsing. Per `39-snapshot-read-replica-runtime-routing.md` such a derivative is configured-but-unavailable rather than not configured, so a selected `Snapshot` returns Snapshot Not Found `404` and a selected `ReadReplica` retains the normal database-availability contract, neither falling back. An equivalently malformed primary connection string keeps its existing behavior.
- Translation to Snapshot Not Found occurs only when the request-scoped target kind is `Snapshot`.
- Read-replica connection failures retain the normal database-availability contract and never become Snapshot Not Found.
- A reachable snapshot missing `dms.EffectiveSchema`, a malformed fingerprint, or an effective-schema mismatch retains the existing provisioning or compatibility `503`.
- SQL, mapping, authorization, fingerprint-shape, and unexpected application failures retain their existing contracts.
- Provider exceptions are not translated wholesale. Any command-time transport-loss translation uses a narrowly defined provider-specific connectivity classification.
- Cancellation attributable to a supplied caller or request `CancellationToken` propagates unchanged. It is never wrapped in `DatabaseConnectionUnavailableException` and never translated to Snapshot Not Found, even though it is raised inside the acquisition boundary. Six of the seven baseline seams above accept a token and pass it into the open call, so the exclusion is stated rather than left to the wrapper's author: an aborted or timed-out caller is not evidence of a missing snapshot, and translating it would diverge from primary and read-replica cancellation behavior.
- The wrapper classifies only the expected provider failures of connection establishment — data-source and connection construction, connection-string parsing, and the open call. Unexpected and programming exceptions raised inside the boundary retain their existing behavior.
- When E18 cache-backed reads are present, expected connection-establishment failure in the
  provider cache-lookup adapter is treated as an unavailable cache read and falls through
  to relational acquisition on the same selected target. That fallback reaches one of the
  seven wrapped baseline seams: an unavailable selected snapshot therefore produces the
  required Snapshot Not Found `404`, while a read replica retains the normal
  database-availability contract and neither target falls back to the primary. Caller
  cancellation and unexpected or programming exceptions from cache acquisition are not
  cache misses and propagate unchanged.
- Session-scoped write hydrators that receive an existing connection and transaction are not changed.
- Translated failures log the underlying error and selected target kind without logging the connection string.

### Tests

- Unit and integration tests verify the exact `404` and `405` ProblemDetails bodies, content type, correlation identifier, and `Allow: GET`.
- An unknown resource with `Use-Snapshot: true` retains its existing `404` rather than returning the snapshot mutation `405`.
- An invalid mutation route with a parsed `Use-Snapshot: true` — collection `DELETE`, collection `PUT`, and item `POST` — returns the snapshot `405` with `Allow: GET` and `application/problem+json`, asserted field by field rather than by status code.
- Those same three routes without the header, and with `Use-Snapshot: false`, retain the route-semantics `405` body, content type, and lack of `Allow`, also asserted field by field. This pair of tests is what proves the *substitution of the snapshot body* is confined to requests carrying a parsed `true`. It does not cover the pipeline reordering's own precedence changes, which apply with no header at all and are owned by `39-snapshot-read-replica-runtime-routing.md`; these tests run against a healthy database, where the reorder is invisible.
- Any non-`GET` resource or descriptor request with `Use-Snapshot: true` and an invalid or missing content type, malformed or invalid body, invalid profile, or document-validation failure returns the snapshot `405` rather than the later `415` or `400`; tests assert the exact ProblemDetails response and `Allow: GET` and prove no database connection is opened.
- PostgreSQL and SQL Server tests cover snapshot-unavailable behavior at every applicable connection-acquisition seam.
- PostgreSQL and SQL Server tests cover a decrypted, non-blank, provider-invalid derivative connection string at the acquisition boundary, proving the provider's construction-time rejection is classified rather than escaping as an unhandled argument failure: a selected `Snapshot` returns Snapshot Not Found `404` with no fallback, a selected `ReadReplica` returns the normal database-availability response and is not served from the primary, the verdict is not cached so the next request that selects the derivative revalidates instead of reusing the failure, and no log records the connection string. A test that additionally asserts recovery from a corrected CMS row must arrange a data-store configuration refresh, because revalidation on its own re-reads the same cached connection string.
- Seam-level tests prove cancellation through a supplied `CancellationToken` during a snapshot connection acquisition is not translated to Snapshot Not Found and does not fall back, with primary and read-replica behavior unchanged. These are written against the seams rather than end to end, because no request-scoped token reaches them today; wiring one is out of scope for this story.
- Document-hydrator tests include a request whose fingerprint and resource-key validations are already cached so hydration is the first failing connection acquisition.
- When E18 cache-backed reads are present, a cache-enabled integration test makes acquisition
  fail first in the cache adapter and then in relational fallback on the same selected
  snapshot, asserting the exact Snapshot Not Found `404` and no primary fallback. A
  seam-level test proves cancellation from cache acquisition propagates instead of being
  swallowed as a cache miss.
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
