---
jira: TBD
jira_url: TBD
---

# Story: Route DMS Reads to Snapshots and Read Replicas

## Description

Implement the DMS configuration, request-scoped target selection, and derivative cache and connection-pool behavior specified by DMS-1190.

The existing data-store resolver remains authoritative for tenant, client, and route-context selection. A following request-scoped step selects exactly one effective `Primary`, `ReadReplica`, or `Snapshot` connection for the whole request. Snapshot selection is explicit through `Use-Snapshot: true`; read-replica selection is automatic for eligible read-only pipelines.

## Acceptance Criteria

### Configuration and request-scoped selection

- `ConfigurationServiceDataStoreProvider` deserializes the `dataStoreDerivatives` collection CMS already returns.
- The DMS `DataStore` model exposes a typed, read-only derivative map, and derivative connection strings are decrypted through the same service as the primary connection string.
- Missing derivative rows and null, empty, or whitespace derivative connection strings are treated as not configured.
- Unknown derivative types are ignored with an error log and do not prevent the parent data store from loading.
- The primary and its derivatives refresh atomically through the existing per-tenant data-store cache. In-flight requests retain their selected target while later requests observe refreshed configuration.
- `IDataStoreSelection` becomes a two-phase contract: the resolver records the parent data store, then the target-selection step records the effective target kind and connection string exactly once.
- Reading the effective target before assignment and assigning it a second time are errors.
- Parent identity remains separately available for authorization and safe logging; repositories consume a distinct effective-connection accessor and cannot silently fall back to the primary.
- The derivative inherits its parent's tenant, route-context, and client-authorization identity and never participates directly in route matching.
- Pipeline construction supplies database access intent, snapshot eligibility (`Allowed`, `RejectedAsMutation`, or `NotApplicable`), and replica eligibility (`Allowed` or `NotApplicable`). Endpoint handlers and repositories do not contain endpoint-policy checks.

### Selection behavior

- Path validation that does not require a database connection rejects malformed paths, malformed identifiers, unknown namespaces, and unknown resources before derivative policy is evaluated.
- `Use-Snapshot` uses case-insensitive boolean parsing. Only a successfully parsed `true` requests a snapshot; missing, `false`, blank, and invalid values do not.
- A snapshot-eligible read with `Use-Snapshot: true` selects a configured snapshot and overrides any configured read replica.
- A snapshot-eligible read with `Use-Snapshot: true` and no usable snapshot produces a typed missing-snapshot outcome for the ProblemDetails story; it does not fall back.
- A snapshot-rejected mutation with `Use-Snapshot: true` produces a typed mutation-rejection outcome for the ProblemDetails story before any database connection is opened.
- Snapshot-`NotApplicable` pipelines ignore the header and continue to replica evaluation.
- A replica-eligible read-only request selects a configured read replica, or the primary when no usable read replica is configured.
- Read-write and replica-`NotApplicable` pipelines select the primary.
- A configured but failing derivative never falls back to another target.
- Target selection runs once per request before fingerprint validation, and fingerprint validation, resource-key validation, authorization SQL, repository queries, and document hydration all use that selected target.

### Endpoint coverage

- Snapshot and read-replica eligibility apply to resource and descriptor GET-many and GET-by-id, their profile-shaped variants, resource and descriptor `/deletes` and `/keyChanges`, and `/changeQueries/v1/availableChangeVersions`.
- Resource and descriptor `POST`, `PUT`, and `DELETE` are snapshot-`RejectedAsMutation`; `OPTIONS` is unaffected.
- Discovery, dependency metadata, OpenAPI, profile OpenAPI, health and readiness, OAuth token issuance, CMS and management endpoints, token introspection, startup provisioning, and DDL use no derivative routing.

### Cache and pool lifecycle

- Primary validation-cache behavior remains unchanged.
- Failed, missing, or malformed derivative validation results are evicted immediately or use a short retry TTL; they are never cached for the process lifetime.
- Successful derivative fingerprint and resource-key validations have a TTL no longer than the data-store configuration cache interval.
- Recreating a snapshot at the same connection string recovers after the bounded interval without restarting DMS.
- Replaced or removed derivatives eventually evict and dispose obsolete pooled data sources without interrupting in-flight requests.
- The scoped PostgreSQL data-source provider is keyed by effective target or connection string, or its redundant scoped dictionary is removed. It is never keyed only by parent `DataStore.Id`.
- No derivative-specific startup health check is added.
- Connection strings never appear in logs. Logs may identify tenant, parent `DataStoreId`, target kind, and trace identifier.

### Tests

- Configuration-provider tests cover deserialization, decryption, unknown types, null and blank connection strings, tenant cache isolation, and atomic refresh.
- Routing unit tests cover every row of both eligibility matrices, snapshot precedence, no-fallback behavior, boolean parsing, path precedence, and write-once target assignment.
- Frontend or E2E tests cover blank and multi-valued `Use-Snapshot` headers because frontend normalization prevents core from observing those values verbatim.
- PostgreSQL and SQL Server integration tests use distinguishable primary, read-replica, and snapshot databases and prove the complete request remains on one selected target, including authorization and document hydration.
- DMS E2E coverage includes GET-many, GET-by-id, `/deletes`, `/keyChanges`, `/availableChangeVersions`, and snapshot precedence over a configured read replica.
- Tests prove route-context and client authorization remain based on the parent data store.
- Tests cover bounded validation-cache recovery, derivative replacement/removal, pool disposal, and uninterrupted in-flight requests.
- The integration-test data-store provider double and configuration-provider unit tests are updated for the derivative-aware model.

## Dependencies

- `38-cms-data-store-derivative-invariants.md` establishes the database invariants that make derivative selection unambiguous.

## Out of Scope

- The HTTP ProblemDetails factories and connection-open failure translation, owned by `40-snapshot-problem-details.md`.
- Served OpenAPI changes.
- Database-engine-specific snapshot or read-replica lifecycle tooling.
