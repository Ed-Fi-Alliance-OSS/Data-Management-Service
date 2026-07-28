---
jira: DMS-1190
jira_url: https://edfi.atlassian.net/browse/DMS-1190
---

# Spike: Snapshot and Read-replica Support for Change Queries

## Summary

ODS honors a `Use-Snapshot: true` request header on live resource and descriptor GET endpoints, `/deletes`, `/keyChanges`, and `/availableChangeVersions`, redirecting the request to a configured per-instance snapshot connection string in `dbo.OdsInstanceDerivative`; snapshots isolate extraction from concurrent writes and address the "Using limit/offset without using snapshots" and "Unresolved references when not using snapshots" sync-failure scenarios in `reference/design/backend-redesign/design-docs/change-queries.md`. DMS v1.0 silently ignores the header (see that document's § "Snapshot support is deferred"), so this proposal specifies how DMS restores snapshot support and adds read-replica routing on top of the `DataStoreDerivative` shape CMS already provides.

## Verified ODS and Publisher Behavior

This proposal is anchored to the following behavior of the official implementations rather than to inference from the derivative table's existence.

- ODS selects a configured read replica automatically whenever its database access intent is `ReadOnly`, and the primary otherwise. The presence of the derivative row is the operator's opt-in; ODS has no additional feature flag. See [OdsDatabaseConnectionStringProvider](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Common/Database/OdsDatabaseConnectionStringProvider.cs).
- When snapshot usage is active, an ODS decorator overrides both normal and read-replica selection with the snapshot connection. See [SnapshotOdsDatabaseConnectionStringProviderDecorator](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Features/ChangeQueries/Providers/SnapshotOdsDatabaseConnectionStringProviderDecorator.cs).
- The Ed-Fi API Publisher does not merely probe with the header. For a source whose API major version is at least 7, it adds `Use-Snapshot: true` to the source client's `DefaultRequestHeaders`, so subsequent source requests issued through that client carry it. A single extraction therefore does not mix snapshot and read-replica targets. See [EdFiApiSourceIsolationApplicator](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-API-Publisher/blob/main/src/EdFi.Tools.ApiPublisher.Connections.Api/Processing/Source/Isolation/EdFiApiSourceIsolationApplicator.cs).
- ODS broadly maps a `DbException` raised under snapshot context to Snapshot Not Found. DMS preserves the required client contract while declining to convert ordinary query defects. See [SnapshotNotFoundExceptionTranslator](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Features/ChangeQueries/ExceptionHandling/SnapshotNotFoundExceptionTranslator.cs).

## Decision

DMS supports the existing CMS `DataStoreDerivative` types as follows.

- A `Snapshot` derivative is selected when a snapshot-eligible request carries `Use-Snapshot: true`.
- A `ReadReplica` derivative is selected automatically for replica-eligible requests whose access intent is read-only and that did not request a snapshot.
- A configured snapshot always takes precedence over a configured read replica.
- Writes always use the primary data store.
- A configured but failing derivative never falls back to another target.
- Snapshot and read-replica creation, refresh, promotion, and teardown remain operator responsibilities. DMS and CMS only store connection information and route requests.

Eligibility is expressed on two independent axes, evaluated per pipeline rather than per HTTP method:

- **Snapshot eligibility** — `Allowed`, `RejectedAsMutation`, or `NotApplicable`.
- **Replica eligibility** — `Allowed` or `NotApplicable`.

The axes are independent because the surfaces they exclude differ: token introspection reads the DMS database but is neither snapshot- nor replica-eligible, while resource and descriptor mutations are replica-`NotApplicable` and snapshot-`RejectedAsMutation`. Resource and descriptor GET-by-id is `Allowed` on both axes.

The selection is made once per request, after the authorized primary data store has been resolved from tenant, token, and route context. Every database operation in that request uses the same selected target.

## Current DMS and CMS Shape

CMS already provides most of the required administrative surface:

- `dmscs.DataStoreDerivative` exists in the PostgreSQL and SQL Server CMS schemas.
- CMS accepts only the `ReadReplica` and `Snapshot` derivative types at its API boundary and encrypts their connection strings.
- `GET /v3/dataStores/` and `GET /v3/dataStores/{id}` include `dataStoreDerivatives`, carrying the encrypted connection string in the same base64 form used for the primary.
- Every derivative query joins `dmscs.DataStore` and applies the tenant predicate, so derivative reads are already tenant-scoped.
- CMS provides CRUD endpoints under `/v3/dataStoreDerivatives`.

No CMS contract change is required for the DMS runtime to *read* derivatives. The following gaps must be closed.

1. The CMS schema does not enforce one derivative of each type per data store, and `DerivativeType` has no database-level value constraint. Without those invariants runtime selection would be ambiguous.
2. `ConfigurationServiceDataStoreProvider` declares its own private response model that has no `dataStoreDerivatives` member, so the collection CMS sends is silently ignored during deserialization. The DMS `DataStore` configuration record likewise has no derivative member.
3. `IDataStoreSelection` represents only the primary `DataStore`, not the effective connection target and its kind. Its setter also has no write-once protection, so introducing a second writer would allow a later step to re-point the target mid-request undetected.
4. The database fingerprint cache, the resource-key validation cache, and the PostgreSQL data-source cache are all keyed by connection string, retain entries for the process lifetime, and are documented as requiring a service restart to clear. Those semantics are acceptable for a long-lived primary but not for an ephemeral derivative.

The DMS runtime does have the correct request-scoped seam: PostgreSQL and SQL Server repositories, the fingerprint middleware, and the resource-key middleware all obtain their connection from `IDataStoreSelection`, so routing can be introduced without endpoint-specific repository changes.

## Administrative Model

### Cardinality and type invariants

CMS enforces a unique `(DataStoreId, DerivativeType)` pair, so each data store has at most one `Snapshot` and at most one `ReadReplica`. This matches the ODS constraint on `dbo.OdsInstanceDerivative`.

Both CMS engines add:

- a named unique constraint on `(DataStoreId, DerivativeType)`;
- a named check constraint restricting `DerivativeType` to `Snapshot` and `ReadReplica`.

Both constraints must be named so repository exception handling can filter on the constraint name, matching how the existing foreign-key handling identifies `FK_DataStoreDerivative_DataStore`. The existing foreign key and cascade-delete behavior remain unchanged.

The two engines must agree on the case semantics of `DerivativeType`, which they will not do by default. The CMS SQL Server deploy scripts specify no collation and contain no existing check constraint, so the column inherits the case-insensitive server default, under which a naive `IN` check would accept `SNAPSHOT` or `readreplica`. PostgreSQL comparison is case-sensitive by default, so the same naive check would reject those values there while the unique constraint would also fail to treat `Snapshot` and `SNAPSHOT` as the same derivative type. The check constraint must therefore state its case semantics explicitly in both engines so that exactly the values `Snapshot` and `ReadReplica` are accepted and case variants are rejected identically. The CMS API validator is already ordinal and case-sensitive, so this aligns the database with the API rather than changing the accepted contract.

The upgrade uses a hard-stop preflight rather than warn-and-defer, because deferring the constraints would leave runtime selection ambiguous. The preflight runs before either constraint is added and fails the upgrade if it finds a problem:

- duplicate `(DataStoreId, DerivativeType)` rows, reported as the offending `(DataStoreId, DerivativeType, Id)` tuples;
- rows whose `DerivativeType` is not exactly `Snapshot` or `ReadReplica`, reported as the offending `(Id, DataStoreId, DerivativeType)` tuples, including case variants that a case-insensitive collation would otherwise hide.

Remediation is documented for both: correct an invalid type through `PUT /v3/dataStoreDerivatives/{id}`, or remove the unwanted or unrecoverable row through `DELETE /v3/dataStoreDerivatives/{id}`, then retry the upgrade. The migration never deletes a derivative, rewrites a type, or selects a row by order.

Both conditions are reachable today because the column has never had a unique constraint or a value constraint and rows can be written outside the API, so neither preflight check is hypothetical. Reporting invalid types is what keeps the promised actionable diagnostics: without it, a legacy value would surface only as a bare check-constraint failure during deployment.

Conflict handling is new work in both backends, not a mapping adjustment: the insert and update result types have no duplicate case today, and the endpoint module maps anything other than success or a foreign-key violation to an unknown-error response. Explicit conflict result variants and their frontend mappings must be added for both insert and update, in both engines, and `DerivativeType` and `DataStoreId` remain updatable so the constraint is reachable from update as well as insert.

A missing derivative row and a row with a null, empty, or whitespace connection string have the same runtime meaning: that derivative is not configured. This preserves the existing nullable CMS contract. Connection-string encryption, tenant scoping, and auditing remain unchanged. DMS continues to ignore an unrecognized `DerivativeType` with an error log as defense in depth, even though the check constraint prevents new invalid values.

### Runtime configuration model and cache

The DMS `DataStore` configuration model gains a typed, read-only derivative map populated from the `dataStoreDerivatives` collection CMS already returns. DMS decrypts each derivative connection string through the same service used for the primary connection string.

The primary connection and its derivatives are one cached configuration unit:

- they refresh atomically on the existing per-tenant data-store cache schedule;
- an in-flight request keeps the target it selected at request start;
- a later request observes a derivative update or removal after the normal cache refresh;
- unknown derivative types are ignored with an error log so one bad CMS row does not prevent all data stores from loading.

Derivative targets require different validation- and pool-cache semantics from the primary. These are stated as required behavior; the cache implementation is left to the implementation ticket.

- Primary-target cache behavior is unchanged.
- Derivative validation results are never cached for the process lifetime.
- Failed, missing, or malformed derivative validation results are evicted immediately or retained only under a short retry TTL.
- Successful derivative fingerprint and resource-key validations are time-bounded by a TTL no longer than the data-store configuration cache interval.
- Recreating a snapshot at the same connection string recovers without restarting DMS, once the bounded cache interval has elapsed.
- Replacing or removing derivative configuration eventually evicts and disposes obsolete pooled data sources without disrupting in-flight requests.
- The scoped PostgreSQL data-source provider keys by the effective target or connection, or drops its redundant scoped dictionary in favor of the singleton data-source cache. The parent `DataStore.Id` is no longer a valid connection identity, because one parent id can now front more than one connection string.

Without these rules, a single request that reaches a snapshot mid-provisioning would latch a `503` for that connection string until the service restarted, and each newly named snapshot would leak a retained connection pool.

Connection strings must never be included in logs. Logs may include the tenant, primary `DataStoreId`, selected target kind, and trace identifier.

No derivative-specific startup health check is added. A derivative is optional and may be intentionally offline between extraction windows. It is validated when selected by a request.

## Request-scoped Routing

### Selection sequence

The existing data-store resolver remains authoritative for tenant isolation, client authorization, and route-context matching:

1. Authenticate the caller and resolve the primary `DataStore` exactly as today.
2. Determine the pipeline's database access intent and its snapshot and replica eligibility.
3. Validate the request path far enough to reject malformed paths, malformed identifiers, unknown namespaces, and unknown resources, without opening a database connection.
4. Parse `Use-Snapshot` using case-insensitive boolean semantics. Only a successfully parsed value of `true` requests a snapshot. A missing, `false`, blank, or invalid value does not request one.
5. Select one effective connection target using the matrix below.
6. Record the resolved parent identity, the effective connection string, and the target kind (`Primary`, `ReadReplica`, or `Snapshot`) in the scoped selection.
7. Run fingerprint validation, resource-key validation, authorization queries, and the endpoint handler against that one target.

Step 4's parsing rule intentionally differs from ODS, which acts on the header merely being present. DMS follows the acceptance criteria and the normative table below: only a parsed `true` selects a snapshot or triggers the mutation rejection.

`Use-Snapshot` is not among the headers the ASP.NET frontend forwards verbatim, so the frontend drops an explicitly blank value and reduces a multi-valued header to its first non-blank value before core sees it. Core therefore cannot distinguish a blank header from an absent one, and blank or multi-value behavior must be covered at the frontend or E2E layer rather than by core routing unit tests.

The derivative has no independent tenant, route-context, or client-authorization identity. It inherits those boundaries from its parent data store and must never participate directly in route matching.

The scoped selection becomes a two-phase contract rather than a single setter:

- the parent resolver records the resolved parent;
- the connection-selection step records the effective target kind and connection string exactly once, and a second attempt is an error;
- reading the effective target before it has been set is an error;
- the parent identity remains separately available for logging and authorization, and repositories read the effective connection string through a distinct accessor so no consumer can silently fall back to the primary's connection string while a derivative is selected.

The parent resolver keeps resolving the parent, and a following request-scoped connection-selection step applies the derivative policy. Access intent and eligibility are supplied by pipeline construction, because DMS builds a separate pipeline per operation and the HTTP method alone cannot distinguish GET-by-id, GET-many, tracked changes, available change versions, and token introspection from one another. Repositories and endpoint handlers therefore need no endpoint-policy awareness.

Selection must run before any database connection is opened, which in the current pipelines means before fingerprint validation. It must also run after the non-database endpoint validation in step 3, so an unknown resource still receives its existing `404` rather than a snapshot `405`. Satisfying both may require a lightweight endpoint-validation phase that precedes target selection; the design deliberately does not fix a single insertion point that would force fingerprint access ahead of endpoint validation.

### Selection matrix

Snapshot eligibility:

| Request | Snapshot configured | Selected result |
| --- | --- | --- |
| Snapshot-eligible read with `Use-Snapshot: true` | Yes | `Snapshot`, overriding any read replica |
| Snapshot-eligible read with `Use-Snapshot: true` | Missing or blank | Snapshot Not Found `404`; no fallback |
| Snapshot-`RejectedAsMutation` request with `Use-Snapshot: true` | Any | Snapshot Method Not Allowed `405`; no database access |
| Snapshot-`NotApplicable` request with `Use-Snapshot: true` | Any | Header ignored; continue to replica evaluation |

Replica evaluation, reached only when a snapshot was not selected:

| Request | Read replica configured | Selected result |
| --- | --- | --- |
| Replica-eligible read-only request | Yes | `ReadReplica` |
| Replica-eligible read-only request | Missing or blank | `Primary` |
| Read-write intent, or replica-`NotApplicable` | Any | `Primary` |

If a selected derivative is configured but unreachable, DMS does not retry the request against another target. Falling back from a snapshot would silently discard the isolation guarantee. Falling back from a read replica would make the location and consistency of a read depend on transient infrastructure state. A missing read replica is a normal primary read; a configured but failing read replica is not.

### Endpoint coverage

Snapshot eligibility and automatic read-replica selection both apply to the data-management read surfaces:

- resource and descriptor GET-many;
- resource and descriptor GET-by-id;
- profile-shaped resource and descriptor reads, which flow through the same GET-many and GET-by-id pipelines and inherit the same behavior;
- resource and descriptor `/deletes`;
- resource and descriptor `/keyChanges`;
- `/changeQueries/v1/availableChangeVersions`.

GET-by-id is in snapshot scope on direct evidence rather than by analogy: the ODS-derived ApiSchema documents the `Use-Snapshot` header parameter on the by-id GET operation and omits it from the collection GET, while referencing the snapshot-aware `404` response from both. Including GET-by-id also keeps dependent point reads inside the same source-isolation boundary as the extraction that triggered them.

`Use-Snapshot: true` is rejected on resource and descriptor `POST`, `PUT`, and `DELETE`. `OPTIONS` remains unaffected.

The following surfaces are outside the `Use-Snapshot` header contract and use no derivative routing:

- discovery, dependency metadata, OpenAPI, and profile OpenAPI endpoints;
- health and readiness endpoints;
- OAuth token issuance and CMS or management endpoints;
- token introspection.

Token introspection is excluded because it is not a data-management extraction surface and returns no resource data, not because it avoids the database: its pipeline resolves a data store and validates the database fingerprint and resource-key seed against it. Read replicas are not used for startup provisioning, DDL, or health checks; the health check already reads a separate primary-only connection string.

## Failure Contract

### Snapshot ProblemDetails

DMS implements the deferred ODS-compatible response shapes from `change-queries.md`:

| Scenario | Type | Title | Status | Detail | Headers |
| --- | --- | --- | --- | --- | --- |
| `Use-Snapshot: true` on a resource or descriptor mutation | `urn:ed-fi:api:snapshots:method-not-allowed` | `Method Not Allowed with Snapshots` | `405` | `An attempt was made to modify data in a Snapshot, but this data is read-only.` | `Allow: GET` |
| No usable snapshot connection is configured | `urn:ed-fi:api:not-found` | `Not Found` | `404` | `Snapshot not found.` | none |
| The selected snapshot database cannot be reached | `urn:ed-fi:api:not-found` | `Not Found` | `404` | `Snapshot not found.` | none |

Both responses use the shared DMS ProblemDetails envelope with the request correlation identifier and `application/problem+json`. The existing not-found failure factory already produces the required `404` type, title, status, and empty collections and is reused as-is. The `405` requires a new snapshot-specific factory: the existing generic method-not-allowed factory emits a different type, title, and detail, and the nearest existing `405` in the pipeline also emits a different content type, so it cannot be reused.

The no-configuration `404` is emitted during connection selection. The unreachable-database `404` is emitted only when the selected target kind is `Snapshot`.

Connection acquisition must distinguish an unavailable database from a reachable but invalid one, covering both the initial fingerprint read and later repository and resource-key connection opens:

- for a selected snapshot, connection-open failures translate to Snapshot Not Found `404`. This includes catalog absence, authentication failure, DNS or network failure, timeout, and firewall rejection, matching the ODS client contract;
- a proven transport-level connection loss after a successful open may use the same translation;
- a reachable snapshot with no `dms.EffectiveSchema`, a malformed fingerprint, or an effective-schema mismatch retains the existing provisioning or compatibility `503`;
- query, mapping, authorization, fingerprint-shape, and unexpected application failures retain their existing contracts;
- read-replica connectivity failures retain the normal database-availability error and are never translated to Snapshot Not Found.

The distinction is structural, not heuristic. The same provider exception types are raised by connection opens and by query failures, so classifying exceptions after the fact cannot separate them reliably. The implementation instead wraps only the connection-open call at each read-path seam, so a failure there raises a distinct backend-neutral `DatabaseConnectionUnavailableException` while anything raised after a successful open keeps its current contract.

There are seven such read-path seams, and all seven must be covered:

1. the database fingerprint reader;
2. the PostgreSQL resource-key row reader;
3. the SQL Server resource-key row reader;
4. the PostgreSQL relational command executor;
5. the SQL Server relational command executor;
6. the PostgreSQL document hydrator;
7. the SQL Server document hydrator.

The two document hydrators are easy to miss and must not be treated as write-path components. Each opens its own connection inside its hydrate call, and both are reached from the GET-many and GET-by-id read paths in the relational document-store repository. A cached fingerprint result, a cached resource-key result, or an already-successful query-plan connection can therefore be followed by a hydrator connection-open failure. If the hydrator seams are omitted, that failure is not guaranteed to produce the required Snapshot Not Found `404`. The session-scoped hydrators used by write sessions receive an existing connection and transaction, open nothing, and are correctly out of scope.

If command-time transport loss is also translated, it must use a narrowly defined provider-specific connectivity classification. Translating every `DbException` is not acceptable.

The selected target kind is carried in request scope so translation applies only to a snapshot target. Every translated failure logs the underlying error and the target kind, and never the connection string.

### Response precedence

Authentication, tenant validation, client data-store authorization, and parent route resolution occur before derivative selection, so an invalid caller or unroutable request receives the existing `401`, `403`, `400`, or `404` rather than learning whether a derivative is configured.

Path-level validation also precedes snapshot policy, matching ODS route precedence: malformed paths, malformed identifiers, unknown namespaces, and unknown resources return their existing `400` or `404` even when the request carries `Use-Snapshot: true`.

After the parent is resolved and the endpoint is known to be valid:

- a resource or descriptor mutation with `Use-Snapshot: true` returns `405` before fingerprint validation and without opening the primary or derivative database;
- a snapshot-eligible read with `Use-Snapshot: true` and no usable snapshot returns `404`;
- otherwise normal fingerprint, request validation, authorization, and handler behavior continues against the selected target.

## Snapshot and Read-replica Lifecycle

DMS will not provide database-engine-specific creation or teardown tooling.

Operators are responsible for:

- creating a SQL Server database snapshot, PostgreSQL point-in-time clone, restored backup, or equivalent read-only source;
- ensuring it uses the same database engine and contains the same DMS schema and `dms.EffectiveSchema` fingerprint as the primary;
- using credentials with read-only permissions;
- registering or updating the derivative connection string in CMS;
- allowing for the configured DMS data-store cache interval before beginning extraction;
- removing or replacing the CMS derivative when the database is retired.

This matches ODS ownership, avoids granting the DMS runtime database-creation privileges, and avoids pretending that SQL Server snapshots and PostgreSQL backup/restore or replication have one portable lifecycle.

Reusing one connection string across successive snapshots is supported. Because derivative validation results are time-bounded rather than cached for the process lifetime, a snapshot recreated at the same connection string becomes usable again after the bounded cache interval without restarting DMS. Operators who instead give each snapshot a distinct name should still remove the retired derivative configuration, so obsolete pooled connections can be released.

A read replica may be eventually consistent. DMS does not measure replica lag or guarantee that `/availableChangeVersions` on a read replica has caught up with the primary. Operators that require a fixed extraction boundary should request a prepared `Snapshot` rather than rely on `ReadReplica`.

## OpenAPI Surface

The served DMS v1.0 OpenAPI surface intentionally contains no snapshot artifacts. `20-openapi-change-query-surface.md` (DMS-1183) records "MetaEd does not advertise unsupported snapshot behavior in the DMS v1.0 OpenAPI surface" as delivered contract and lists snapshot advertising as out of scope, and the shipped ApiSchema packages contain no `Use-Snapshot` parameter or snapshot-aware response component. This is therefore new contract work that re-adds an intentionally deferred surface, not normalization of existing fragments.

Snapshot metadata is re-added at the MetaEd/ApiSchema source, and the served documents then describe the runtime contract consistently:

- define a reusable boolean `Use-Snapshot` header parameter with default `false`;
- reference it from resource, descriptor, and profile GET-many and GET-by-id operations;
- reference it from `/deletes`, `/keyChanges`, and `/availableChangeVersions`;
- document the Snapshot Not Found `404` on those GET operations;
- document the snapshot `405`, its exact ProblemDetails contract, and its `Allow: GET` response header on resource and descriptor mutation operations;
- use the exact ProblemDetails types, titles, status codes, and details above, served as `application/problem+json`.

Every independently served OpenAPI document that contains a `$ref` must itself define the component that reference resolves to. A document cannot resolve `#/components/parameters/Use-Snapshot` from a sibling document. That means the `Use-Snapshot` parameter and the snapshot response components must be present in each of the resource, descriptor, profile, and standalone Change Queries documents that reference them — not only in the resource and descriptor base documents. The Change Queries document is the case most likely to be missed: DMS serves it independently from `projectSchema.openApiBaseDocuments.changeQueries`, and the shipped document today carries its own `components` block with empty `parameters` and `responses` collections and no `$ref` of any kind, so its components must be populated rather than assumed to be inherited.

Tests must prove that every component reference resolves within its own document, for the resource, descriptor, profile, and Change Queries documents.

GET-many support is required even though the older ODS-derived fixture shape advertises the header only on the by-id GET and documents the mutation `405` as a bare description with no ProblemDetails schema and no `Allow` header. DMS deliberately documents the header and the failure contract consistently across collection and by-id operations.

The ODS-derived authoritative fixture inputs under the backend fixture tree are DDL and plan-compilation inputs, not the served MetaEd contract. They must not be edited in order to affect served OpenAPI; doing so would churn generated DDL and plan goldens without changing any served document.

Read-replica selection is deployment configuration and adds no request parameter or response field.

DMS-side document-assembly tests cover the resource, descriptor, profile, and Change Query documents.

## API Publisher Interoperability

Interoperability validation must prove that a Publisher extraction can use DMS without `--ignoreIsolation=true` when a snapshot is configured:

- Publisher's `Use-Snapshot: true` source requests are served only from the configured snapshot;
- a write committed to the primary after snapshot creation does not appear during that extraction;
- live GET-many, GET-by-id, `/deletes`, `/keyChanges`, and `/availableChangeVersions` all use the same snapshot target within one extraction, which follows from Publisher setting the header on the source client's default headers rather than on a single probe;
- a mutation carrying `Use-Snapshot: true` returns the snapshot `405` with `Allow: GET`;
- no configured snapshot produces the expected `404`;
- retiring or making the configured snapshot unreachable produces the same `404`;
- `Use-Snapshot: false` does not select the snapshot.

Validation records the API and Publisher versions used so future changes to Publisher's isolation behavior can be evaluated reproducibly.

## Test Expectations

Implementation coverage should include:

- CMS PostgreSQL and SQL Server integration tests for the unique and check constraints, the new insert and update conflict responses, preflight diagnostics for both duplicate rows and invalid derivative types, rejection of case variants such as `SNAPSHOT` in both engines, tenant isolation, and derivative inclusion in data-store responses.
- DMS configuration-provider unit tests for derivative deserialization, decryption, unknown types, null and blank connection strings, tenant caches, and cache refresh.
- Core routing unit tests for every row in both eligibility matrices, snapshot precedence over read replica, absence of fallback, header parsing, response precedence, and rejection of a second effective-target assignment within one request.
- Frontend or E2E coverage for blank and multi-valued `Use-Snapshot` headers, which core cannot observe because the frontend normalizes them.
- PostgreSQL and SQL Server integration tests using distinct primary, read-replica, and snapshot databases with distinguishable data. The SQL Server path requires the SQL Server 2025 integration environment described in `AGENTS.md`, and a three-database fixture is a meaningful fixture-cost increase for the runtime-routing ticket.
- Snapshot-unavailable tests at every read-path connection-open seam: fingerprint acquisition, resource-key validation, normal repository connection acquisition, and document-hydration connection acquisition. Hydration coverage must be explicit rather than folded into generic repository coverage, and must include the case where fingerprint and resource-key results are already cached so the hydrator open is the first failure in the request.
- Tests proving a reachable but unprovisioned or fingerprint-incompatible snapshot returns the existing `503`, not Snapshot Not Found, and that an ordinary query failure against a snapshot is not translated either.
- Tests proving a snapshot recreated at the same connection string recovers after the bounded cache interval without a service restart, and that a removed or replaced derivative eventually releases its pooled data source without disrupting in-flight requests.
- Tests proving authorization and route-context selection are still based on the parent data store and that all authorization SQL for a request uses the selected target.
- Tests proving an unknown resource path with `Use-Snapshot: true` returns its existing `404` rather than the snapshot `405`.
- OpenAPI document-assembly tests for resource, descriptor, profile, and Change Query documents, including a check that every `$ref` resolves within its own served document.
- DMS E2E coverage for GET-many, GET-by-id, `/deletes`, `/keyChanges`, `/availableChangeVersions`, `405` plus `Allow: GET`, missing snapshot, and snapshot precedence over read replica.
- API Publisher interoperability coverage as described above.

## Compatibility and Rollout

This changes the post-v1.0 behavior of an existing header:

- DMS v1.0 ignores `Use-Snapshot: true` and reads current data.
- After this feature, the same request reads the configured snapshot or returns `404`.

Release notes must call out that operators using API Publisher either configure a snapshot or continue to opt out explicitly with `--ignoreIsolation=true`.

Release notes must also call out that read-replica routing becomes active for any valid `ReadReplica` rows already stored in CMS, and that a read replica may be eventually consistent. Creating a derivative row is itself the configuration action that enables routing, matching ODS, so this is the activation of previously inert configuration rather than an implicit default. Operators should verify or remove stale derivative configuration before upgrading.

No database document or Change Query DDL changes are required in the DMS data store. Snapshot databases must already contain the same generated DDL as their primary because they are copies of a provisioned primary.

## Rejected Alternatives

- **DMS creates and drops snapshots.** Rejected because lifecycle and permissions are database-engine-specific and ODS already treats this as an operator concern.
- **An additional feature flag to enable read-replica routing.** Rejected because the derivative row is already the operator's explicit opt-in, and ODS requires no second switch. A flag would add a configuration surface that ODS parity does not need.
- **Excluding GET-by-id from read-replica selection.** Rejected because ODS selects the replica for any read-only access intent, and a DMS-only exclusion would diverge from ODS without a corresponding contract difference.
- **Use the read replica when `Use-Snapshot: true` but no snapshot exists.** Rejected because a continuously changing replica does not provide the requested point-in-time isolation.
- **Fall back to primary when a selected derivative fails.** Rejected because it silently changes the consistency contract.
- **Translating every `DbException` raised under a snapshot target to Snapshot Not Found.** Rejected because it would convert ordinary query, mapping, and provisioning defects into a misleading `404`. DMS preserves the ODS client contract through a structural connection-open boundary instead.
- **Caching derivative validation results for the process lifetime, as the primary does.** Rejected because a snapshot is recreated between extraction windows, so a latched failure would require a service restart to clear.
- **Open a transaction with snapshot isolation for each request.** Rejected because Publisher extraction spans many HTTP requests and needs one stable database image across all of them.
- **Route derivatives as independent data stores.** Rejected because authorization, tenant, and route context belong to the parent and duplicating them can create isolation gaps.

## Resolved Decisions

Reviewed and resolved during this spike:

1. **Read-replica routing follows ODS parity.** All resource and descriptor GET-many and GET-by-id requests, profile-shaped reads, `/deletes`, `/keyChanges`, and `/availableChangeVersions` are replica-eligible. The derivative row is the opt-in; no additional flag is added. Snapshot outranks replica; writes stay on the primary; token introspection and non-data-management surfaces stay on the primary.
2. **Derivative validation and pool caches are time-bounded.** Primary behavior is unchanged; derivative results are never cached for the process lifetime, and recreating a snapshot at the same connection string recovers without a restart.
3. **Snapshot-unavailable `404` is broad at the connection-open boundary.** Catalog absence, authentication failure, network failure, timeout, and firewall rejection all return Snapshot Not Found for a snapshot target, while ordinary SQL, mapping, authorization, fingerprint-shape, and application errors do not. Coverage is all seven read-path connection-open seams, including both document hydrators.
4. **The snapshot `405` is scoped to resource and descriptor mutations,** and triggers only on a successfully parsed `true`. This intentionally narrows the acceptance criterion's "non-`GET`" wording to the data-management surface, and intentionally diverges from ODS's header-presence filter.
5. **The CMS uniqueness migration hard-stops on duplicates,** reports the offending rows, and documents deletion through the derivative endpoint as remediation.
6. **Path validation precedes snapshot policy,** so an unknown resource still returns its existing `400` or `404`; the mutation `405` is still emitted before any database connection is opened.
7. **The OpenAPI surface is re-added, not normalized,** including a reusable `Use-Snapshot` parameter applied to GET-many as well as GET-by-id, and a new snapshot-specific `405` ProblemDetails factory.

## Follow-on Ticket Plan

Create and link the following implementation tickets only after this proposal is approved. The story files are created with Jira placeholders so the ticket keys can be inserted after Jira creation:

| Story | Area | Scope |
| --- | --- | --- |
| `38-cms-data-store-derivative-invariants.md` | CMS/admin database shape | Add the named `(DataStoreId, DerivativeType)` unique constraint and the explicitly case-sensitive `DerivativeType` check constraint for PostgreSQL and SQL Server, add the preflight for duplicate rows and invalid derivative types with its diagnostics, add insert and update conflict result variants and frontend mappings, and cover upgrade behavior and CMS tests. |
| `39-snapshot-read-replica-runtime-routing.md` | DMS configuration and runtime routing | Add derivatives to the configuration response model and `DataStore` record, decrypt them, introduce the two-phase effective request-scoped connection target, apply snapshot and replica eligibility from pipeline construction, implement the bounded derivative validation caches and pooled-data-source eviction, and cover both relational backends. Re-key the scoped PostgreSQL data-source provider by effective target or connection string, or remove the redundant scoped dictionary; never key by parent `DataStore.Id`. Includes updating the integration-test data-store provider double and the configuration-provider unit tests. |
| `40-snapshot-problem-details.md` | Snapshot ProblemDetails | Add the snapshot `405` factory and `Allow: GET`, emit the missing-snapshot `404` from the existing not-found factory, add the backend-neutral connection-unavailable exception at all seven enumerated read-path connection-open seams including both document hydrators, keep provisioning and query defects on their existing contracts, and log safely. |
| `41-snapshot-openapi-surface.md` | OpenAPI surface | Re-add MetaEd/ApiSchema snapshot components and apply them to resource, descriptor, profile, and Change Query operations, defining the referenced components in every independently served document including the standalone Change Queries document; add DMS document-assembly and reference-resolution tests. Does not touch the backend authoritative fixture inputs. |
| `42-api-publisher-snapshot-interoperability.md` | API Publisher interoperability | Add an environment and automated or repeatable validation for Publisher isolation behavior against DMS, and document the operator workflow. |

## Acceptance Criteria Coverage

- The routing change is specified above: request-scoped selection, its interaction with the existing data-store resolver, the two eligibility axes, the selection matrices, response precedence, and the endpoints that support snapshots and read replicas.
- Both deferred Snapshot ProblemDetails are specified above, including the `Allow: GET` header, the reused `404` factory, and the new `405` factory. The acceptance criterion's "non-`GET`" wording is deliberately scoped to resource and descriptor mutations, as recorded in the resolved decisions.
- Snapshot and read-replica creation, refresh, and teardown are explicitly operator-owned; DMS provides no engine-specific tooling.
- The required implementation-ticket slices are defined above and await approval of this proposal before Jira creation.
