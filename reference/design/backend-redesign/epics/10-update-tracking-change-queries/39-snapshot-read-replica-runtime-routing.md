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
- A derivative connection string that cannot be decrypted is treated as not configured for that derivative only. Each derivative is decrypted in its own fault boundary, so a failure does not abort the enclosing data store's construction or the rest of the CMS response. `ConfigurationServiceDataStoreProvider` currently decrypts inline in the projection that builds each `DataStore`, and `ConnectionStringDecryptionService.DecryptFromBase64` throws on invalid Base64, an undersized payload, and a wrong key, so this requires restructuring that projection rather than reusing it as-is.
- An undecryptable derivative logs an error identifying tenant, parent `DataStoreId`, and derivative type, and never the ciphertext, partial plaintext, encryption key, or any connection string. The log is distinguishable from the normal not-configured path, which is not an error.
- An undecryptable primary connection string retains its existing fatal behavior for that data store.
- An unusable `Snapshot` is treated as though no snapshot were configured, so a snapshot-eligible read produces the missing-snapshot outcome rather than reading current data. An unusable `ReadReplica` is treated as though no replica were configured, and the request is served by the primary.
- The primary and its derivatives refresh atomically through the existing per-tenant data-store cache. In-flight requests retain their selected target while later requests observe refreshed configuration.
- `IDataStoreSelection` becomes a two-phase contract: the resolver records the parent data store, then the target-selection step records the effective target kind and connection string exactly once.
- Reading the effective target before assignment and assigning it a second time are errors.
- Parent identity remains separately available for authorization and safe logging; repositories consume a distinct effective-connection accessor and cannot silently fall back to the primary.
- The derivative inherits its parent's tenant, route-context, and client-authorization identity and never participates directly in route matching.
- Pipeline construction supplies database access intent, snapshot eligibility (`Allowed`, `RejectedAsMutation`, or `NotApplicable`), and replica eligibility (`Allowed` or `NotApplicable`). Endpoint handlers and repositories do not contain endpoint-policy checks.

### Selection behavior

- Path validation that does not require a database connection rejects malformed paths, malformed identifiers, unknown namespaces, and unknown resources before derivative policy is evaluated.
- The routed-resource and tracked-changes pipelines are reordered so endpoint validation precedes database access. `ApiService.GetRoutedResourceInitialSteps` currently places `ValidateDatabaseFingerprintMiddleware` and `ValidateResourceKeySeedMiddleware` ahead of the `ValidateEndpointMiddleware` that each operation pipeline adds, and `CreateGetTrackedChangesPipeline` has the same ordering. This story delivers that reordering; it is a prerequisite for target selection, not an optional cleanup.
- `ApiSchemaValidationMiddleware`, `ProvideApiSchemaMiddleware`, and `ValidateEndpointMiddleware` run before `ValidateDatabaseFingerprintMiddleware` and `ValidateResourceKeySeedMiddleware` in both pipelines. Either hoist those three steps or extract a shared endpoint-validation phase both pipelines invoke; endpoint validation must gain no database dependency either way.
- `ResolveMappingSetMiddleware` continues to run after fingerprint validation, because it depends on the validated fingerprint.
- Target selection is inserted after endpoint validation and before `ValidateDatabaseFingerprintMiddleware`.
- The reordering's own behavior change is covered: a request that is both unroutable and against an unprovisioned or unreachable database now returns the endpoint `404` instead of the fingerprint `503`. Existing pipeline and integration tests that assert the previous ordering are updated deliberately rather than adjusted to whatever the new code emits.
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
- Successful derivative fingerprint and resource-key validations are bounded by a new independent `CacheSettings` derivative validation TTL, not by the data-store configuration cache interval. `DataStoreCacheRefreshEnabled` may be `false` and a non-positive `DataStoreCacheExpirationSeconds` means "hold until explicit reload", so a derived TTL would be unbounded.
- The new setting is always positive: a zero, negative, or absent value falls back to a positive default rather than meaning "no expiration". This inverts the existing `DataStoreCacheExpirationSeconds` convention, so it is documented on the setting and validated at startup.
- An enforced upper bound prevents any configuration from making derivative validation effectively process-lifetime.
- When the data-store configuration cache is enabled and bounded, the effective TTL is the smaller of the two values. When it is disabled or non-expiring, the derivative TTL applies on its own and stays bounded. Derivative routing works with data-store refresh disabled.
- Recreating a snapshot at the same connection string recovers after the derivative validation TTL without restarting DMS, including when data-store cache refresh is disabled.
- Replaced or removed derivatives eventually evict and dispose obsolete pooled data sources without interrupting in-flight requests.
- The scoped PostgreSQL data-source provider is keyed by effective target or connection string, or its redundant scoped dictionary is removed. It is never keyed only by parent `DataStore.Id`.
- No derivative-specific startup health check is added.
- Connection strings never appear in logs. Logs may identify tenant, parent `DataStoreId`, target kind, and trace identifier.

### Tests

- Configuration-provider tests cover deserialization, decryption, unknown types, null and blank connection strings, tenant cache isolation, and atomic refresh.
- Configuration-provider tests cover undecryptable derivative connection strings for all three failure modes — invalid Base64, a payload at or below the IV length, and a valid payload encrypted under a different key — and prove the parent data store, its sibling derivatives, and other data stores in the same response still load. A separate test proves an undecryptable primary remains fatal.
- Routing unit tests cover every row of both eligibility matrices, snapshot precedence, no-fallback behavior, boolean parsing, path precedence, and write-once target assignment.
- Frontend or E2E tests cover blank and multi-valued `Use-Snapshot` headers because frontend normalization prevents core from observing those values verbatim.
- PostgreSQL and SQL Server integration tests use distinguishable primary, read-replica, and snapshot databases and prove the complete request remains on one selected target, including authorization and document hydration.
- DMS E2E coverage includes GET-many, GET-by-id, `/deletes`, `/keyChanges`, `/availableChangeVersions`, and snapshot precedence over a configured read replica.
- Tests prove route-context and client authorization remain based on the parent data store.
- Tests cover bounded validation-cache recovery, derivative replacement/removal, pool disposal, and uninterrupted in-flight requests.
- Tests prove the derivative validation TTL stays bounded with `DataStoreCacheRefreshEnabled` set to `false` and with a zero or negative `DataStoreCacheExpirationSeconds`, that a zero, negative, or absent derivative TTL falls back to the positive default, that the configured upper bound is enforced, and that a shorter data-store cache interval shortens the effective TTL.
- The integration-test data-store provider double and configuration-provider unit tests are updated for the derivative-aware model.

## Dependencies

- `38-cms-data-store-derivative-invariants.md` establishes the database invariants that make derivative selection unambiguous.

## Out of Scope

- The HTTP ProblemDetails factories and connection-open failure translation, owned by `40-snapshot-problem-details.md`.
- Served OpenAPI changes.
- Database-engine-specific snapshot or read-replica lifecycle tooling.
