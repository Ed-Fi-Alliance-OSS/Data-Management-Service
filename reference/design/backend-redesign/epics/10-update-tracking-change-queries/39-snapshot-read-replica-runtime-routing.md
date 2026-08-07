---
jira: DMS-1367
jira_url: https://edfi.atlassian.net/browse/DMS-1367
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
- An undecryptable primary connection string retains its existing behavior unchanged, which is tenant-wide: it is decrypted in the same projection, so it fails the entire tenant data-store load rather than only its own data store. Narrowing that to per-data-store isolation is out of scope for this story.
- A missing `Snapshot`, or one whose connection string is null, empty, whitespace, or undecryptable, is treated as not configured, so a snapshot-eligible read produces the missing-snapshot outcome rather than reading current data. A `ReadReplica` in one of those same not-configured states is not selected, and the request is served by the primary.
- "Not configured" covers missing rows, null, empty, or whitespace connection strings, and undecryptable connection strings only. A connection string that decrypts to a non-blank but provider-invalid value is **not** in that set: DMS cannot recognize it as malformed without asking a provider, so it is selectable, is selected normally, and fails at the backend connection-acquisition boundary owned by `40-snapshot-problem-details.md`. A selected `Snapshot` in this state yields the unreachable-snapshot outcome rather than the missing-snapshot outcome, and a selected `ReadReplica` retains the normal database-availability contract and is not served from the primary. Neither falls back, matching the rule that a configured but failing derivative never falls back.
- The primary and its derivatives refresh atomically through the existing per-tenant data-store cache. In-flight requests retain their selected target while later requests observe refreshed configuration.
- `IDataStoreSelection` becomes a two-phase contract: the resolver records the parent data store, then the target-selection step records the effective target kind and connection string exactly once.
- Reading the effective target before assignment and assigning it a second time are errors.
- Parent identity remains separately available for authorization and safe logging; repositories consume a distinct effective-connection accessor and cannot silently fall back to the primary.
- The derivative inherits its parent's tenant, route-context, and client-authorization identity and never participates directly in route matching.
- Pipeline construction supplies database access intent, snapshot eligibility (`Allowed`, `RejectedAsMutation`, or `NotApplicable`), and replica eligibility (`Allowed` or `NotApplicable`). Endpoint handlers and repositories do not contain endpoint-policy checks.

### Selection behavior

- Path validation that does not require a database connection rejects malformed paths, malformed identifiers, unknown namespaces, and unknown resources before derivative policy is evaluated.
- The routed-resource and tracked-changes pipelines are reordered so endpoint validation precedes database access. `ApiService.GetRoutedResourceInitialSteps` currently places `ValidateDatabaseFingerprintMiddleware` and `ValidateResourceKeySeedMiddleware` ahead of the `ValidateEndpointMiddleware` that each operation pipeline adds, and `CreateGetTrackedChangesPipeline` has the same ordering. This story delivers that reordering; it is a prerequisite for target selection, not an optional cleanup.
- Extract a shared non-database endpoint-validation phase containing `ApiSchemaValidationMiddleware`, `ProvideApiSchemaMiddleware`, and `ValidateEndpointMiddleware`, separate from the later fingerprint, resource-key, and mapping-set phase. Read pipelines insert target selection between the phases. Mutation pipelines insert target selection immediately after the endpoint phase, then append their existing mutation-local `ValidateRouteSemanticsMiddleware`, then the database-validation phase. The tracked-changes pipeline uses the same endpoint phase. Endpoint validation gains no database dependency.
- `ResolveMappingSetMiddleware` continues to run after fingerprint validation, because it depends on the validated fingerprint.
- Target selection's insertion point is specified per pipeline, because two pipelines perform no endpoint validation. Selection runs after the validations whose existing response precedence must be preserved — common authentication and parent resolution, and endpoint validation where present — and before the first database connection. It does not wait for every validation that happens not to use the database, and on mutation pipelines it deliberately precedes route-semantics validation:
  - `CreateGetByIdPipeline` and `CreateQueryPipeline`: after `ValidateEndpointMiddleware`, before `ValidateDatabaseFingerprintMiddleware`.
  - `CreateUpsertPipeline`, `CreateUpdatePipeline`, and `CreateDeleteByIdPipeline`: after `ValidateEndpointMiddleware`, before `ValidateRouteSemanticsMiddleware`, before `ValidateDatabaseFingerprintMiddleware`, and before later content-type, body, profile, coercion, and document validation.
  - `CreateGetTrackedChangesPipeline`: after `ValidateEndpointMiddleware`, before `ValidateDatabaseFingerprintMiddleware`, with `ResolveMappingSetMiddleware` remaining after fingerprint validation.
  - `CreateGetAvailableChangeVersionsPipeline`: immediately after `GetCommonInitialSteps()`, before `ValidateDatabaseFingerprintMiddleware`.
  - `CreateGetTokenInfoPipeline`: immediately after `GetCommonInitialSteps()`, before `ValidateDatabaseFingerprintMiddleware`.
- `/changeQueries/v1/availableChangeVersions` is snapshot- and replica-eligible and routes as a read-only request, with selection running directly after tenant, authentication, and parent data-store resolution, then `ValidateDatabaseFingerprintMiddleware` and `AvailableChangeVersionsHandler` against the selected target.
- `ParsePathMiddleware`, `ApiSchemaValidationMiddleware`, `ProvideApiSchemaMiddleware`, `ValidateEndpointMiddleware`, and `ValidateResourceKeySeedMiddleware` are **not** added to `CreateGetAvailableChangeVersionsPipeline`. That route is deliberately independent of `ApiSchema.json` and OpenAPI path presence per `21-available-change-versions-endpoint.md`, it has no unknown-resource case for a snapshot response to preempt, and adding them to satisfy a generic insertion rule would regress delivered contract.
- `CreateGetTokenInfoPipeline` runs the selection step even though both axes are `NotApplicable` and explicitly assigns the effective target `Primary`. It is never snapshot- or read-replica-eligible. The step is not skipped: the effective target is write-once and reading it before assignment is an error, so an explicit `Primary` assignment is what keeps every database operation in the request on the selected target with no silent fallback.
- On the three mutation pipelines, target selection runs before `ValidateRouteSemanticsMiddleware`, so a collection `DELETE`, a collection `PUT`, or an item `POST` carrying a parsed `Use-Snapshot: true` yields the typed mutation-rejection outcome instead of reaching route semantics. The same request without the header, or with `Use-Snapshot: false`, still reaches `ValidateRouteSemanticsMiddleware` and keeps its existing `405` from `FailureResponse.ForMethodNotAllowed`, including content type `application/json; charset=utf-8` and no `Allow` header.
- `ValidateEndpointMiddleware` continues to run before target selection, so an unknown resource with a mutation method keeps returning `404` rather than any `405`. Endpoint validation is the only mutation-path validation that snapshot policy does not preempt.
- For any non-`GET` resource or descriptor request, target selection runs before `ValidateRouteSemanticsMiddleware`, `ValidateContentTypeMiddleware`, `ParseBodyMiddleware`, profile resolution, coercion, and document validation. `Use-Snapshot: true` therefore yields the typed mutation-rejection outcome even if route semantics or one of those later validations would otherwise return `405`, `415`, or `400`; this precedence is intentional and must be tested here. The resulting snapshot `405` response itself is asserted by `40-snapshot-problem-details.md`.
- Keep `ValidateRouteSemanticsMiddleware` in the mutation pipelines, positioned after selection, so its response *body* is unchanged for every request that does not carry a parsed `true`. Its position relative to fingerprint validation does change: it now runs before `ValidateDatabaseFingerprintMiddleware` rather than after, which is what produces the second reordering consequence above. Do not hoist route semantics into shared routed-resource steps; doing so would couple read pipelines to future additions to write-route validation.
- The reordering's own behavior changes are covered as a precedence matrix. The reorder moves the endpoint phase — and, on the mutation pipelines, `ValidateRouteSemanticsMiddleware` — ahead of the whole later database-validation phase: `ValidateDatabaseFingerprintMiddleware`, `ValidateResourceKeySeedMiddleware`, and `ResolveMappingSetMiddleware`. An unroutable request therefore returns the endpoint `404`, an invalid mutation route shape returns the route-semantics `405`, and a request arriving while `IApiSchemaProvider.IsSchemaValid` is false returns the ApiSchema failure when any one of those later stages would also fail. Under the existing order the first failing later stage returned `503`. An unprovisioned or unreachable database exercises the fingerprint edge; a valid fingerprint followed by a resource-key mismatch or mapping-set failure exercises the other edges. **None of these changes depends on `Use-Snapshot` being present**, so they are distinct from the header-driven precedence change below and need their own coverage. Existing pipeline and integration tests that assert the previous ordering are updated deliberately rather than adjusted to whatever the new code emits.
- `Use-Snapshot` uses case-insensitive boolean parsing. Only a successfully parsed `true` requests a snapshot; missing, `false`, blank, and invalid values do not.
- A snapshot-eligible read with `Use-Snapshot: true` selects a configured snapshot and overrides any configured read replica.
- A snapshot-eligible read with `Use-Snapshot: true` and no usable snapshot produces a typed missing-snapshot outcome for the ProblemDetails story; it does not fall back.
- A snapshot-rejected mutation with `Use-Snapshot: true` produces a typed mutation-rejection outcome for the ProblemDetails story before any database connection is opened.
- Snapshot-`NotApplicable` pipelines ignore the header and continue to replica evaluation.
- A replica-eligible read-only request selects a configured read replica, or the primary when no usable read replica is configured.
- Read-write and replica-`NotApplicable` pipelines select the primary.
- A configured but failing derivative never falls back to another target.
- Target selection runs once per request before fingerprint validation, and fingerprint validation, resource-key validation, authorization SQL, repository queries, and document hydration all use that selected target.
- When the E18 `DocumentCache` read path is present in the target release, it obeys the same
  request-scoped selection. Cache lookup, lifecycle checks, canonical `ContentVersion`
  comparison, and relational fallback use the selected physical database. A derivative
  request never reads cache state through the parent primary connection; if the cache
  adapter cannot bind every one of those reads to the selected target, cache acceleration
  is bypassed for that request. Expected connection-establishment failure during cache
  acquisition is treated as an unavailable cache read and falls through to relational
  acquisition on the same selected target; caller cancellation and unexpected or
  programming exceptions propagate unchanged. Optional direct fill is also bypassed for
  `Snapshot` and `ReadReplica` targets because it writes `dms.DocumentCache`; a
  derivative-eligible GET remains read-only. The E18 configuration gate enables a use path
  and never overrides the target already selected by this story.

### Endpoint coverage

- Snapshot and read-replica eligibility apply to resource and descriptor GET-many and GET-by-id, their profile-shaped variants, resource and descriptor `/deletes` and `/keyChanges` including their profile-shaped variants, and `/changeQueries/v1/availableChangeVersions`.
- Profile `/deletes` and `/keyChanges` inherit eligibility from the tracked-changes pipeline they flow through; no separate profile rule is added. A profiled extraction must not read live data from a snapshot while reading tombstones and key changes from current data.
- Resource and descriptor `POST`, `PUT`, and `DELETE` are snapshot-`RejectedAsMutation`, whether or not the route shape is valid, so a collection `DELETE`, a collection `PUT`, and an item `POST` are rejected by snapshot policy rather than by route semantics when the header parses `true`. `OPTIONS` is unaffected.
- Discovery, dependency metadata, OpenAPI, profile OpenAPI, health and readiness, OAuth token issuance, CMS and management endpoints, startup provisioning, and DDL use no derivative routing.
- Token introspection is also never derivative-eligible, but unlike the other surfaces in the preceding list its DMS pipeline resolves a data store and therefore runs target selection to assign `Primary` explicitly.

### Cache and pool lifecycle

- Primary fingerprint and resource-key validation-cache behavior remains unchanged and permanent.
- Primary and derivative fingerprint/resource-key validation verdicts never share a cache entry merely because their connection-string text matches. The implementation may enforce this through separate cache namespaces, a composite key that includes the target's policy class, or an equivalent mechanism, but a Primary always retains the permanent policy while derivatives retain bounded successful verdicts and immediate eviction of failed, missing, or malformed verdicts.
- Failed, missing, and malformed derivative validation results are evicted immediately and are not cached at all; there is no retry TTL. The next request selecting that derivative revalidates from scratch.
- Successful derivative fingerprint and resource-key validations are bounded by `CacheSettings.DerivativeValidationCacheExpirationSeconds`, a new independent setting, not by the data-store configuration cache interval. `DataStoreCacheRefreshEnabled` may be `false` and a non-positive `DataStoreCacheExpirationSeconds` means "hold until explicit reload", so a derived TTL would be unbounded.
- `DerivativeValidationCacheExpirationSeconds` defaults to `600` seconds, accepts `1` through `3600`, and is resolved at startup: a zero, negative, or absent value resolves to `600`, and a value above `3600` resolves to `3600`. It never means "no expiration", inverting the `DataStoreCacheExpirationSeconds` convention, and that inversion is documented on the setting and in the configuration reference.
- Both out-of-range cases log a startup warning naming the configured value and the effective value. Startup does not fail, matching the other `CacheSettings` members; the enforced `3600` ceiling is what protects the never-process-lifetime invariant.
- The implementation adds the default to
  `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json`,
  exposes `CacheSettings__DerivativeValidationCacheExpirationSeconds` through both
  `eng/docker-compose/local-dms.yml` and `eng/docker-compose/published-dms.yml` using the
  operator variable `DMS_DERIVATIVE_VALIDATION_CACHE_EXPIRATION_SECONDS`, and documents
  the default, range, clamping, and non-expiring-convention inversion in both
  `docs/CONFIGURATION.md` and `docs/CACHING-STRATEGY.md`.
- When the data-store configuration cache is enabled and bounded, the effective TTL is the smaller of the two values. When it is disabled or non-expiring, the derivative TTL applies on its own and stays bounded. Derivative routing works with data-store refresh disabled.
- Recreating a snapshot at the same connection string recovers after the derivative validation TTL without restarting DMS, including when data-store cache refresh is disabled.
- Replaced or removed derivatives evict and dispose obsolete pooled data sources without interrupting in-flight requests, once the data-store configuration cache has refreshed to reflect the change. Obsolescence is evaluated across every effective target that can share a pooled object. If a primary, another derivative, or a target under another data store still owns the same connection-string-keyed pool, removing or replacing one derivative does not evict or dispose it. Effective-target ownership, reference or lease tracking, independently owned target pools, or an equivalent mechanism ensures disposal occurs only after the last configured owner is gone and in-flight users have completed. The validation TTL does not supply configuration visibility: it bounds reuse of a verdict about an already-visible connection string, while a corrected, re-pointed, or removed CMS row is observed only when the configuration cache reloads. `ConfigurationServiceDataStoreProvider.RefreshInstancesIfExpiredAsync` performs no periodic refresh when `DataStoreCacheRefreshEnabled` is `false` or `DataStoreCacheExpirationSeconds` is non-positive, and no operator-facing data-store reload endpoint exists, so under those settings a CMS derivative edit is not guaranteed to be observed and restart is the deterministic operator action. Other existing `LoadDataStores` triggers can reload the tenant cache incidentally and expose a changed derivative without a restart; as `29-snapshot-support.md` § Derivative validation TTL records, that is not the supported observation boundary and is not a recovery workflow. This story adds neither a reload endpoint nor a new trigger.
- PostgreSQL satisfies that lifecycle through its owned `NpgsqlDataSource` objects. SQL
  Server does not introduce an application-owned data-source cache. DMS constructs each
  derivative effective connection string through `SqlConnectionStringBuilder` inside the
  connection-acquisition boundary and sets its `PoolBlockingPeriod` to
  `PoolBlockingPeriod.NeverBlock`, overriding
  any operator-supplied derivative value while passing primary connection strings through
  unchanged. This prevents SqlClient's login/timeout blocking period from replaying a
  failed open on the immediately following derivative request. The effective provider
  string, including the forced derivative setting, is the SQL Server pool and ownership
  identity: a primary and derivative with otherwise identical stored text use distinct
  pools, while derivatives with the same effective string may share one. After the final
  configured owner and in-flight lease for a retired effective string are gone, DMS calls
  `SqlConnection.ClearPool` for that exact SqlClient pool. It never uses
  `SqlConnection.ClearAllPools`, because clearing unrelated primary or derivative pools
  would violate the ownership and uninterrupted-request rules above. Leaving a retired
  SQL Server derivative pool solely to driver idle cleanup does not satisfy this story.
- The scoped PostgreSQL data-source provider is keyed by effective target or connection string, or its redundant scoped dictionary is removed. It is never keyed only by parent `DataStore.Id`.
- Startup instance validation and health/readiness connection selection remain primary-only. `ValidateStartupInstancesTask` never enumerates derivatives, backend mapping initialization remains connection-independent, no derivative fingerprint/resource-key verdict or pooled data source is created eagerly, and a derivative is first validated and pooled only when a request selects it.
- Connection strings never appear in logs. Logs may identify tenant, parent `DataStoreId`, target kind, and trace identifier.

### Tests

- Configuration-provider tests cover deserialization, decryption, unknown types, null and blank connection strings, tenant cache isolation, and atomic refresh.
- Configuration-provider tests cover undecryptable derivative connection strings for all three failure modes — invalid Base64, a payload at or below the IV length, and a valid payload encrypted under a different key — and prove the parent data store, its sibling derivatives, and other data stores in the same response still load.
- A characterization test pins the unchanged primary behavior: an undecryptable primary fails the whole tenant data-store load, including sibling data stores in the same response.
- Configuration-provider tests prove a derivative whose connection string decrypts to a non-blank but provider-invalid value is loaded as a configured derivative rather than dropped as not configured, so target selection reaches it and the failure surfaces at the acquisition boundary instead of being silently reinterpreted as an absent derivative.
- Routing unit tests cover every row of both eligibility matrices, snapshot precedence, no-fallback behavior, boolean parsing, path precedence, and write-once target assignment.
- Pipeline-composition tests assert the selection step's position in all eight pipelines, including that `CreateGetAvailableChangeVersionsPipeline` gains selection but gains no ApiSchema, endpoint, path, or resource-key step, and that `CreateGetTokenInfoPipeline` runs selection and resolves `Primary`.
- Tests prove `/availableChangeVersions` honors `Use-Snapshot: true` against a configured snapshot and selects a configured read replica for a normal read, with fingerprint validation and the handler both running against the selected target.
- Tests prove an invalid mutation route — collection `DELETE`, collection `PUT`, and item `POST` — carrying a parsed `Use-Snapshot: true` produces the typed mutation-rejection outcome and never reaches `ValidateRouteSemanticsMiddleware`, so selection precedes route semantics.
- Tests prove those same three routes without the header, and with `Use-Snapshot: false`, still reach `ValidateRouteSemanticsMiddleware` and keep its existing `405`, asserting the type, title, detail, `application/json; charset=utf-8` content type, and absence of `Allow`. This is what confines the *substitution of the snapshot body* to requests carrying a parsed `true`; asserting the status code alone is insufficient because both responses are `405`. These run against a provisioned, reachable database, so they prove nothing about the reordering's precedence changes, which the next bullet covers.
- Tests cover the reordering precedence matrix with no `Use-Snapshot` header. Exercise each hoisted verdict — endpoint `404` for an unroutable request, route-semantics `405` for each invalid mutation route shape (collection `DELETE`, collection `PUT`, and item `POST`), and the ApiSchema failure for an invalid ApiSchema — against each independently failing later stage: fingerprint validation, resource-key validation after a successful fingerprint, and mapping-set resolution after successful fingerprint and resource-key validation. In every case the hoisted verdict replaces the `503` that the existing order would have returned. Without this matrix the reorder is tested only against fingerprint failure and leaves its resource-key and mapping-set precedence changes unpinned.
- Tests prove an unknown resource with a mutation method and `Use-Snapshot: true` returns `404`, not either `405`.
- Tests prove any non-`GET` resource or descriptor request carrying `Use-Snapshot: true` produces the typed mutation-rejection outcome before route-semantics and later mutation validation. Cover an invalid or missing content type that would otherwise return `415`, malformed or invalid body input that would otherwise return `400`, and profile/document validation failures; assert that the pipeline stops at selection with that outcome and that no database connection is opened. The exact snapshot `405` ProblemDetails body, content type, correlation envelope, and `Allow: GET` are asserted by `40-snapshot-problem-details.md`, which owns the factory, so this story stays closeable without it.
- Frontend or E2E tests cover blank and multi-valued `Use-Snapshot` headers because frontend normalization prevents core from observing those values verbatim.
- PostgreSQL and SQL Server integration tests use distinguishable primary, read-replica, and snapshot databases and prove the complete request remains on one selected target, including authorization and document hydration.
- DMS E2E coverage includes GET-many, GET-by-id, `/deletes`, `/keyChanges`, `/availableChangeVersions`, and snapshot precedence over a configured read replica.
- Tests prove route-context and client authorization remain based on the parent data store.
- Tests cover bounded validation-cache recovery at an unchanged derivative connection string, which needs no configuration reload because nothing in the configuration changed.
- Tests cover derivative replacement and removal, obsolete pool disposal, and uninterrupted in-flight requests, arranging a data-store configuration refresh so the changed rows are observable. A test that expects replacement to take effect without that refresh is asserting behavior this design does not provide.
- Tests prove the derivative validation TTL stays bounded with `DataStoreCacheRefreshEnabled` set to `false` and with a zero or negative `DataStoreCacheExpirationSeconds`, and that a shorter data-store cache interval shortens the effective TTL.
- Settings-resolution tests cover `DerivativeValidationCacheExpirationSeconds` at zero, negative, absent, `1`, `3600`, and above `3600`, asserting the resolved effective value and the startup warning for each out-of-range case.
- Tests prove failed, missing, and malformed derivative validations are not cached, so the immediately following request revalidates rather than reusing the failure. SQL Server coverage makes a derivative open fail with a login or timeout error and restores availability at the same effective connection string before that next request, proving the second request reaches the server rather than receiving a SqlClient pool-blocking replay.
- Tests use a Primary and a derivative with identical connection-string text and populate their fingerprint and resource-key validation verdicts in both orders, proving each target retains its own cache policy: the Primary verdict remains permanent, while the derivative success remains bounded and its failed, missing, or malformed verdict is evicted immediately.
- A startup/readiness regression test attaches offline or provider-invalid snapshot and read-replica derivatives to a valid primary and proves startup instance validation and health/readiness connection selection use only the primary, create no derivative validation-cache or pooled-data-source entry, and leave each derivative to be validated on the first request that selects it.
- Pool-lifecycle tests cover targets that share one effective connection-string-keyed pool, including derivatives under different parent data stores. Removing or replacing one owner after configuration refresh leaves the pool available to every remaining configured owner and in-flight request, and disposal occurs only after the final owner is gone and its in-flight users complete. SQL Server separately proves that a primary and derivative with otherwise identical stored text use distinct effective pools because only the derivative forces `PoolBlockingPeriod.NeverBlock`.
- Provider-specific pool-lifecycle coverage proves PostgreSQL disposes only the retired
  `NpgsqlDataSource`; SQL Server forces `PoolBlockingPeriod.NeverBlock` only for derivative
  effective strings, leaves primary strings unchanged, and calls `SqlConnection.ClearPool`
  only for the exact retired effective string after its final owner and in-flight lease are
  gone. An unrelated SqlClient pool remains usable.
- When E18 cache-backed reads are already present, integration tests use distinguishable
  primary, snapshot, and read-replica databases and prove cache hits and relational
  fallback stay on the selected target, primary cache JSON is never returned for a
  derivative request, and derivative requests perform no direct fill write. With cache
  reads enabled, an expected cache-adapter acquisition failure falls through to relational
  acquisition on the same selected snapshot without attempting the primary; a supplied
  cancellation from cache acquisition is not swallowed as a cache miss. Story 40 owns the
  exact HTTP failure response when relational acquisition also fails.
- The integration-test data-store provider double and configuration-provider unit tests are updated for the derivative-aware model.

## Dependencies

- `38-cms-data-store-derivative-invariants.md` establishes the database invariants that make derivative selection unambiguous.
- E18 `05-cache-backed-read-path.md` is not a prerequisite for this story to close. If E18
  cache-backed reads are already present when this story lands, this story owns their
  selected-target integration and the conditional tests above. If this story lands first,
  E18 Story 05 owns that work when it adds cache-backed reads. The two features cannot ship
  together until the integration is complete.

## Out of Scope

- The HTTP ProblemDetails factories and connection-acquisition failure translation, owned by `40-snapshot-problem-details.md`.
- Served OpenAPI changes.
- Database-engine-specific snapshot or read-replica lifecycle tooling.
