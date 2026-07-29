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

The two engines must agree on the comparison semantics of `DerivativeType`, which they will not do by default, and case is only one of the two ways they disagree. The column is `NVARCHAR(50)` on SQL Server and `VARCHAR(50)` on PostgreSQL, and neither deploy script specifies a collation or any existing check constraint.

- **Case.** The SQL Server column inherits the case-insensitive server default, under which a naive `IN` check would accept `SNAPSHOT` or `readreplica`. PostgreSQL comparison is case-sensitive by default, so the same naive check would reject those values there while the unique constraint would also fail to treat `Snapshot` and `SNAPSHOT` as the same derivative type.
- **Trailing whitespace.** A case-sensitive collation alone does not make SQL Server equality exact. SQL Server applies SQL-92 string-padding semantics to `=` and `IN` on variable-length string types, padding the shorter operand, so `Snapshot ` compares equal to `Snapshot` and a naive `IN` check accepts it regardless of collation. PostgreSQL `varchar` comparison is exact and rejects the same value. `LIKE` and `DATALENGTH` are not padding-insensitive and are the available tools for an exact SQL Server comparison; `LEN` is not, because it ignores trailing spaces. See [string comparison and assignment](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/string-comparison-assignment).

The check constraint must therefore require ordinal equality *including length* in both engines, so that exactly the values `Snapshot` and `ReadReplica` are accepted and case and whitespace variants are rejected identically. The CMS API validator is already ordinal and case-sensitive, so this aligns the database with the API rather than changing the accepted contract.

The same requirement applies to the preflight, for the same reason and with a worse outcome if omitted. A `NOT IN` preflight scan on SQL Server would not report `Snapshot ` as an invalid type, a padding-insensitive check constraint would then accept the row, and the upgrade would complete successfully while leaving a value the DMS runtime does not recognize. Because DMS matches derivative types ordinally, that row is ignored with an error log: the operator has a `Snapshot` derivative registered in CMS that never routes, the upgrade reported no problem, and nothing identifies the whitespace as the cause. Whitespace variants must therefore be detected and reported alongside case variants, using a padding-exact comparison in the preflight itself.

The upgrade uses a hard-stop preflight rather than warn-and-defer, because deferring the constraints would leave runtime selection ambiguous. The preflight runs before either constraint is added and fails the upgrade if it finds a problem:

- duplicate `(DataStoreId, DerivativeType)` rows, reported as the offending `(DataStoreId, DerivativeType, Id)` tuples;
- rows whose `DerivativeType` is not exactly `Snapshot` or `ReadReplica`, reported as the offending `(Id, DataStoreId, DerivativeType)` tuples, including the case variants that a case-insensitive collation would otherwise hide and the whitespace variants that SQL Server's string padding would otherwise hide.

Remediation is documented for both: correct an invalid type through `PUT /v3/dataStoreDerivatives/{id}`, or remove the unwanted or unrecoverable row through `DELETE /v3/dataStoreDerivatives/{id}`, then retry the upgrade. The migration never deletes a derivative, rewrites a type, or selects a row by order.

Both conditions are reachable today because the column has never had a unique constraint or a value constraint and rows can be written outside the API, so neither preflight check is hypothetical. Reporting invalid types is what keeps the promised actionable diagnostics: without it, a legacy value would surface only as a bare check-constraint failure during deployment.

Conflict handling is new work in both backends, not a mapping adjustment: the insert and update result types have no duplicate case today, and neither the insert nor the update switch in the endpoint module has a duplicate-result arm, so a newly added duplicate result would fall through to the unknown-error response until it is explicitly mapped. Explicit conflict result variants must be added for both insert and update, in both engines, along with frontend mappings to the conflict response CMS already uses elsewhere — `FailureResponse.ForConflict` with HTTP `409`. `DerivativeType` and `DataStoreId` remain updatable so the constraint is reachable from update as well as insert.

The repository result and the HTTP mapping are two separate changes, and repository-level integration tests can prove the first while the second still falls through to the unknown-error response. Coverage must therefore assert the `409` at the frontend for both insert and update, not only the conflict result in the backend.

A missing derivative row and a row with a null, empty, or whitespace connection string have the same runtime meaning: that derivative is not configured. This preserves the existing nullable CMS contract. Connection-string encryption, tenant scoping, and auditing remain unchanged. DMS continues to ignore an unrecognized `DerivativeType` with an error log as defense in depth, even though the check constraint prevents new invalid values.

### Runtime configuration model and cache

The DMS `DataStore` configuration model gains a typed, read-only derivative map populated from the `dataStoreDerivatives` collection CMS already returns. DMS decrypts each derivative connection string through the same service used for the primary connection string.

Decryption of a derivative must be individually fault-isolated, which the current loading shape does not provide. `ConfigurationServiceDataStoreProvider.FetchDataStores` decrypts inline inside the projection that builds each `DataStore`, and `ConnectionStringDecryptionService.DecryptFromBase64` throws `InvalidOperationException` on a value that is not valid Base64, is no longer than the 16-byte IV, or fails AES decryption under the configured key. Decrypting derivatives the same way would let one unreadable optional derivative abort that projection and fail the tenant's entire data-store load, so every data store in the response — including their primaries and their healthy derivatives — would be unavailable because of one bad optional row.

An undecryptable derivative connection string is therefore a configuration defect scoped to that derivative:

- each derivative's decryption is attempted independently, and a failure is caught per derivative rather than per data store or per response;
- a derivative whose connection string cannot be decrypted is unusable and is treated as not configured, joining the missing-row and null/empty/whitespace cases;
- the parent data store still loads with its primary connection and its remaining usable derivatives;
- the failure is logged as an error identifying the tenant, parent `DataStoreId`, and derivative type, and never the ciphertext, the partial plaintext, the encryption key, or a connection string;
- primary connection-string decryption behavior is unchanged, and that existing behavior is tenant-wide rather than per-data-store. Because the primary is decrypted inside the same `FetchDataStores` projection, an undecryptable primary throws out of the enclosing `ToList()` and fails the whole tenant's data-store load, not merely the one data store that owns the bad value. This design does not narrow that blast radius.

The asymmetry is deliberate and is a scope boundary, not an oversight. Per-data-store isolation of primary decryption failures would be a change to existing behavior that no part of the snapshot contract requires: an unreadable primary leaves that data store with no usable target at all, so isolating it would convert a startup-visible tenant-load failure into a set of individually broken data stores discovered one request at a time. Introducing derivative fault isolation does not create the primary case and does not depend on fixing it. Narrowing primary decryption failure to a single data store is therefore explicitly out of scope for this rollout; if it is wanted, it is separate work with its own justification, and it should be raised as its own ticket rather than folded into the derivative change.

This follows the precedent already set for an unrecognized `DerivativeType`, which is ignored with an error log rather than failing the load, and it keeps a `Snapshot` defect safe by default: an unusable snapshot is not configured, so a snapshot-eligible read returns Snapshot Not Found `404` and never silently reads current data.

The `ReadReplica` case is the one that degrades quietly, and the error log is the only signal. An unusable read replica means eligible reads are served by the primary, which is correct data from the wrong target. That is accepted rather than escalated, because failing every eligible read on a corrupt optional replica string is a worse outcome than serving the primary, and because escalating would make an optional derivative able to disable a working data store. The log must therefore be emitted at error level and be distinguishable from the normal no-replica-configured path, which is not an error and is not logged as one.

This is a configuration-time defect and is distinct from the runtime rule that a configured but failing derivative never falls back. That rule governs a derivative DMS could read and select but cannot reach; an undecryptable string is never selectable in the first place.

#### A decrypted but provider-invalid derivative connection string

CMS validates a derivative connection string for length only, so an arbitrary provider-invalid value can be stored through the public API, encrypted, and decrypted cleanly. Such a value is both non-blank and decryptable, so it is none of the not-configured cases above. It is **configured but unavailable**, and it is resolved at the backend connection-acquisition boundary specified under § Snapshot ProblemDetails rather than at configuration load.

That placement follows the selectability line drawn immediately above. A missing, blank, or undecryptable string is never selectable, so DMS treats the derivative as absent. A decrypted, non-blank string is selectable — DMS cannot know it is malformed without asking a provider — so it is selected and then fails on acquisition, under the same runtime rule that governs a derivative whose host is down.

- a selected `Snapshot` returns Snapshot Not Found `404`, with no fallback;
- a selected `ReadReplica` retains the normal database-availability contract, never becomes Snapshot Not Found, and does not fall back to the primary;
- primary behavior is unchanged;
- the failure is not cached, so correcting the CMS row recovers on the next request that selects that derivative;
- the log identifies the tenant, parent `DataStoreId`, and derivative type, and never the connection string.

The `ReadReplica` outcome is deliberately harsher than the undecryptable-replica case, which serves the primary. The difference is selectability rather than severity: a typo that still decrypts produces a replica DMS selects and cannot use, and silently serving the primary from a selected-but-broken replica is exactly the fallback this design rejects everywhere else.

The primary connection and its derivatives are one cached configuration unit:

- they refresh atomically on the existing per-tenant data-store cache schedule;
- an in-flight request keeps the target it selected at request start;
- a later request observes a derivative update or removal after the normal cache refresh, so a multi-request extraction is only as stable as the derivative configuration behind it. Extraction-wide stability is an operator obligation, specified under § Extraction-window stability;
- unknown derivative types are ignored with an error log so one bad CMS row does not prevent all data stores from loading.

Derivative targets require different validation- and pool-cache semantics from the primary. These are stated as required behavior; the cache implementation is left to the implementation ticket.

- Primary-target cache behavior is unchanged.
- Derivative validation results are never cached for the process lifetime.
- Failed, missing, or malformed derivative validation results are evicted immediately and are not cached; there is no retry TTL.
- Successful derivative fingerprint and resource-key validations are time-bounded by an independent derivative validation TTL, specified below.
- Recreating a snapshot at the same connection string recovers without restarting DMS, once that TTL has elapsed.
- Replacing or removing derivative configuration eventually evicts and disposes obsolete pooled data sources without disrupting in-flight requests.
- The scoped PostgreSQL data-source provider keys by the effective target or connection, or drops its redundant scoped dictionary in favor of the singleton data-source cache. The parent `DataStore.Id` is no longer a valid connection identity, because one parent id can now front more than one connection string.

Without these rules, a single request that reaches a snapshot mid-provisioning would latch a `503` for that connection string until the service restarted, and each newly named snapshot would leak a retained connection pool.

Connection strings must never be included in logs. Logs may include the tenant, primary `DataStoreId`, selected target kind, and trace identifier.

No derivative-specific startup health check is added. A derivative is optional and may be intentionally offline between extraction windows. It is validated when selected by a request.

#### Derivative validation TTL

The derivative validation TTL is its own bounded setting and is not derived from the data-store configuration cache interval. Deriving it would reintroduce exactly the process-lifetime caching this design rejects, because the data-store cache is permitted to be non-expiring: `CacheSettings.DataStoreCacheRefreshEnabled` can be `false`, and `DataStoreCacheExpirationSeconds` is documented to keep the cached configuration until the next explicit reload when set to zero or a negative value. A derivative TTL defined as "no longer than the data-store configuration cache interval" is unbounded under either of those settings, so an operator who disables data-store refresh would silently latch a snapshot `503` or a stale successful validation until restart.

The setting is `CacheSettings.DerivativeValidationCacheExpirationSeconds`, named and expressed in seconds to match the existing `CacheSettings` members:

| Property | Value | Rationale |
| --- | --- | --- |
| Default | `600` (10 minutes) | Matches the existing `DataStoreCacheExpirationSeconds` default, so the out-of-the-box behavior is the coupling the reviewers expected, without inheriting its disable-able semantics. |
| Minimum accepted | `1` | Any positive value is honored. Operators running short extraction cycles may set it low; the cost is repeated fingerprint and resource-key reads against the derivative. |
| Maximum accepted | `3600` (1 hour) | Bounds worst-case recovery for a snapshot recreated at the same connection string. An hour is far longer than any expected provisioning gap and far shorter than a process lifetime, which is the property that must hold. |

Out-of-range and absent handling, which must be validated at startup rather than at first use:

- a zero, negative, or absent value resolves to the default. It does **not** mean "no expiration". This inverts the `DataStoreCacheExpirationSeconds` convention, where a non-positive value means "hold until explicit reload", so the inversion is documented on the setting itself and in the operator-facing configuration reference;
- a value above the maximum resolves to the maximum;
- both out-of-range cases log a startup warning naming the configured value and the value actually in effect, so a clamped setting is discoverable without reading code;
- startup does not fail on an out-of-range value. Clamping matches the existing `CacheSettings` members, none of which fail startup, and a cache-tuning value is not worth making a deployment fatal. The bound is what protects the invariant, not the failure.

Interaction with the data-store configuration cache:

- when the data-store configuration cache is enabled and bounded, the effective TTL is the smaller of the two values, so a shorter data-store refresh still shortens derivative validation;
- when the data-store configuration cache is disabled or non-expiring, the derivative TTL stands alone and remains bounded. Derivative routing does not require data-store refresh to be enabled;
- the operator-facing guidance to allow for a cache interval before beginning extraction refers to the data-store configuration cache, which is what governs when a newly registered derivative becomes visible. The derivative validation TTL governs only how long a validation verdict for an already-visible derivative is reused.

Failed, missing, and malformed derivative validation results are evicted immediately and are not cached at all. There is no retry TTL:

- the next request that selects that derivative revalidates from scratch;
- this is the simplest behavior that satisfies the recovery requirement, and it is strictly more responsive than any retry TTL, so recovery after a snapshot is recreated is bounded by the successful-result TTL alone;
- the cost is that a persistently unreachable derivative is retried once per request rather than once per interval. That is accepted: the failure path already opens no usable connection, the request is failing regardless, and a snapshot target's failure is terminal for the request rather than retried internally;
- if load against a hard-down derivative later proves to be a real problem, a short negative TTL can be added without changing any other rule here. It is deliberately not specified now, because an unmeasured retry interval is a second tunable that would have to be reasoned about alongside the first.

Requiring data-store refresh to be enabled was considered and rejected: it would turn an unrelated performance setting into a hard prerequisite for snapshot routing, and it would still leave the TTL coupled to a value an operator may set arbitrarily high.

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

Selection must run before any database connection is opened, which in the current pipelines means before fingerprint validation. It must also run after the non-database endpoint validation in step 3, so an unknown resource still receives its existing `404` rather than a snapshot `405`. On mutation pipelines, route-semantics validation also precedes selection, but later content-type, body, profile, and document validation does not: once the route is a valid resource or descriptor mutation, `Use-Snapshot: true` returns the snapshot `405` before those later validations run.

The current pipelines do not satisfy both, so satisfying them is concrete work rather than an insertion-point choice. `ApiService.GetRoutedResourceInitialSteps` places `ValidateDatabaseFingerprintMiddleware` and `ValidateResourceKeySeedMiddleware` — both of which open a database connection — ahead of the per-operation steps, while `ValidateEndpointMiddleware` is added by each operation pipeline after them. The tracked-changes pipeline has the same ordering. An unknown project namespace or unknown resource is therefore rejected only after the database has already been read, so inserting target selection before fingerprint validation without reordering would emit a snapshot `405` or `404` for a path that must return `404`.

The reordering is bounded, because the steps that produce the endpoint verdict do not use the database:

- `ParsePathMiddleware` is already ahead of fingerprint validation and supplies `PathComponents`, so malformed paths and malformed identifiers are the one part of step 3 that is already correctly ordered.
- `ApiSchemaValidationMiddleware` reads only `IApiSchemaProvider.IsSchemaValid`.
- `ProvideApiSchemaMiddleware` attaches the startup-built effective schema documents.
- `ValidateEndpointMiddleware` resolves the project namespace and resource from those documents and `PathComponents`.

The routed-resource steps must therefore be split into a non-database endpoint-validation phase and a later database-validation phase. Read pipelines insert target selection between those phases. Mutation pipelines append their existing `ValidateRouteSemanticsMiddleware` to the endpoint phase, insert target selection immediately after it, and only then append fingerprint, resource-key, and mapping-set resolution. The tracked-changes pipeline uses the same endpoint phase and keeps `ResolveMappingSetMiddleware` after the validated fingerprint. This factoring establishes the required precedence without granting endpoint validation any database dependency or moving mutation-only route semantics into read pipelines.

Because this reordering changes the response for a request that is both unroutable and against an unprovisioned or unreachable database, from the fingerprint `503` to the endpoint `404`, it is a deliberate, test-visible precedence change rather than a refactor, and must be covered as such.

#### Per-pipeline insertion points

"After endpoint validation" is not a usable rule for every pipeline, because two of the eight do not perform endpoint validation at all and the mutation pipelines perform an additional write-only route-semantics validation that must also precede selection. The insertion point is therefore specified per pipeline rather than as one generic rule. Selection runs after the validations whose existing response precedence this design preserves — common authentication and parent resolution, endpoint validation where present, and route-semantics validation for mutations — and before the first database connection. It does **not** run after every validation that happens not to use the database. For a route-valid mutation, the snapshot `405` intentionally preempts later content-type, body-parsing, profile, coercion, and document-validation failures.

| Pipeline | Snapshot / replica eligibility | Selection inserted | Notes |
| --- | --- | --- | --- |
| `CreateGetByIdPipeline`, `CreateQueryPipeline` | `Allowed` / `Allowed` | After `ValidateEndpointMiddleware`, before `ValidateDatabaseFingerprintMiddleware` | The base case. |
| `CreateUpsertPipeline`, `CreateUpdatePipeline`, `CreateDeleteByIdPipeline` | `RejectedAsMutation` / `NotApplicable` | Immediately after `ValidateRouteSemanticsMiddleware`, before `ValidateDatabaseFingerprintMiddleware` and all later mutation validation | Route semantics keeps precedence; a route-valid mutation carrying `Use-Snapshot: true` returns the snapshot `405` before content-type, body, profile, or document validation. |
| `CreateGetTrackedChangesPipeline` | `Allowed` / `Allowed` | After `ValidateEndpointMiddleware`, before `ValidateDatabaseFingerprintMiddleware` | Requires the same hoist as the routed pipelines. `ResolveMappingSetMiddleware`, currently ahead of the ApiSchema steps here, stays after fingerprint validation. |
| `CreateGetAvailableChangeVersionsPipeline` | `Allowed` / `Allowed` | Immediately after `GetCommonInitialSteps()`, before `ValidateDatabaseFingerprintMiddleware` | No endpoint validation exists on this route and none is added; see below. |
| `CreateGetTokenInfoPipeline` | `NotApplicable` / `NotApplicable` | Immediately after `GetCommonInitialSteps()`, before `ValidateDatabaseFingerprintMiddleware` | Always resolves to `Primary`, but the step still runs; see below. |

**`/availableChangeVersions` is a fixed route with no endpoint validation to follow.** `CreateGetAvailableChangeVersionsPipeline` is intentionally minimal: it is `GetCommonInitialSteps()`, then `ValidateDatabaseFingerprintMiddleware`, then `AvailableChangeVersionsHandler`. It has no `ParsePathMiddleware`, no `ApiSchemaValidationMiddleware`, no `ProvideApiSchemaMiddleware`, no `ValidateEndpointMiddleware`, and no `ValidateResourceKeySeedMiddleware`. That is delivered contract, not an accident: `21-available-change-versions-endpoint.md` specifies the endpoint as a fixed DMS route that is not generated from `ApiSchema.json` and not gated by OpenAPI path presence, and requires that route availability not depend on either. Selection is inserted directly after the common steps resolve tenant, authentication, and the parent data store.

None of those steps may be added to this pipeline in order to satisfy the generic rule. There is no unknown-resource or unknown-namespace case on a fixed route, so there is no `404` for a snapshot response to preempt, and adding ApiSchema or resource-key validation would reintroduce exactly the ApiSchema coupling that route was built to avoid — a regression against delivered contract, in exchange for nothing.

**Token introspection still runs the selection step, but it is never derivative-eligible.** Its eligibility is `NotApplicable` on both axes, so selection is a foregone conclusion: it explicitly assigns the effective target `Primary` and never selects a `Snapshot` or `ReadReplica`. The step is not skipped, because the effective target is a write-once value that repositories read through a distinct accessor, and reading it before assignment is an error. A pipeline that reaches the fingerprint reader or a repository without having assigned the effective target would fail on that contract rather than quietly defaulting to the primary. Running the step and having it resolve `Primary` is what keeps "every database operation in the request uses the selected target" true without a fallback path. The same reasoning applies to any future pipeline that resolves a data store: assigning `Primary` explicitly is the cheap, uniform behavior, and the absence of a silent default is the point. In this proposal, "token introspection uses no derivative routing" means it cannot select a derivative, not that it skips effective-target assignment.

**Route semantics must precede selection on mutation pipelines.** `ValidateRouteSemanticsMiddleware` runs after `ValidateEndpointMiddleware` in the three mutation pipelines and rejects a collection `DELETE`, a collection `PUT`, and an item `POST`. It returns `405` itself, through `FailureResponse.ForMethodNotAllowed` with content type `application/json; charset=utf-8` and no `Allow` header.

That makes the collision a same-status one, which is why it is easy to miss. For `DELETE /ed-fi/students` carrying `Use-Snapshot: true`, inserting selection immediately after endpoint validation would emit the snapshot `405` — type `urn:ed-fi:api:snapshots:method-not-allowed`, `application/problem+json`, `Allow: GET` — in place of the existing generic `405` and its route-semantics detail. The status code is identical, so any test asserting only the status would still pass while the type, title, detail, content type, and headers all changed. Selection is therefore inserted after route-semantics validation, and an invalid mutation route keeps its existing response whether or not it carries the header.

The write-path precedence is fixed, and each edge is intentional:

1. `ValidateEndpointMiddleware` before `ValidateRouteSemanticsMiddleware`, as today, so `DELETE /ed-fi/nonexistentThings` keeps returning the unknown-resource `404` rather than a route-semantics `405`. Route semantics reads only `Method` and `PathComponents.HasDocumentUuidSegment` and could technically run earlier, but moving it ahead of endpoint validation would flip that response and is not permitted.
2. `ValidateRouteSemanticsMiddleware` before selection, so an invalid mutation route keeps its existing `405` shape.
3. Selection immediately after route semantics and before `ValidateContentTypeMiddleware`, `ParseBodyMiddleware`, profile resolution, coercion, and document validation, so a route-valid mutation carrying `Use-Snapshot: true` returns the snapshot `405` even when one of those later validations would otherwise return `415` or `400`.
4. Selection before `ValidateDatabaseFingerprintMiddleware`, so the snapshot `405` and `404` are emitted without opening any database.

Keep `ValidateRouteSemanticsMiddleware` in the three mutation pipelines and insert selection immediately after it. Do not hoist route semantics into shared routed-resource steps merely to obtain one insertion point. Although it is a no-op for current `GET` requests, doing so would couple read pipelines to future additions to write-route validation and could change read behavior accidentally.

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
- resource and descriptor `/deletes`, including their profile-shaped variants;
- resource and descriptor `/keyChanges`, including their profile-shaped variants;
- `/changeQueries/v1/availableChangeVersions`.

Profile `/deletes` and `/keyChanges` are in scope for the same reason the profile-shaped live reads are: they are served paths that flow through the tracked-changes pipeline, so they inherit its eligibility rather than needing their own rule. `20-openapi-change-query-surface.md` establishes that profile OpenAPI documents preserve `/deletes` and `/keyChanges` for readable profiled resources, so these are real endpoints a Publisher extraction can read, not a hypothetical surface. Excluding them would leave a profiled extraction able to read live data from a snapshot while reading its tombstones and key changes from current data — precisely the mixed-point-in-time outcome this feature exists to prevent. Profile filtering does not change the routing decision: those responses carry identity-key payloads rather than profile-filtered bodies, and the target is selected before any body is shaped.

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

- for a selected snapshot, connection-acquisition failures translate to Snapshot Not Found `404`. This includes catalog absence, authentication failure, DNS or network failure, timeout, firewall rejection, and a connection string the provider rejects while constructing its data source or connection, matching the ODS client contract;
- a proven transport-level connection loss after a successful open may use the same translation;
- a reachable snapshot with no `dms.EffectiveSchema`, a malformed fingerprint, or an effective-schema mismatch retains the existing provisioning or compatibility `503`;
- query, mapping, authorization, fingerprint-shape, and unexpected application failures retain their existing contracts;
- read-replica connectivity failures retain the normal database-availability error and are never translated to Snapshot Not Found.

The distinction is structural, not heuristic. The same provider exception types are raised by connection acquisition and by query failures, so classifying exceptions after the fact cannot separate them reliably. The implementation instead wraps connection acquisition at each read-path seam — provider data-source and connection construction, connection-string parsing, and the open call — so a failure anywhere in acquisition raises a distinct backend-neutral `DatabaseConnectionUnavailableException` while anything raised after a successful open keeps its current contract.

The wrap must cover construction and parsing rather than the open call alone, because at every seam the provider parses the connection string on a statement that precedes the open: PostgreSQL parses while the pooled data source is built, and SQL Server parses in the `SqlConnection` constructor. A boundary drawn around the open call alone would let a provider-invalid connection string escape as an unhandled provider argument failure at all seven seams.

There are seven such read-path seams, and all seven must be covered:

1. the database fingerprint reader;
2. the PostgreSQL resource-key row reader;
3. the SQL Server resource-key row reader;
4. the PostgreSQL relational command executor;
5. the SQL Server relational command executor;
6. the PostgreSQL document hydrator;
7. the SQL Server document hydrator.

The two document hydrators are easy to miss and must not be treated as write-path components. Each opens its own connection inside its hydrate call, and both are reached from the GET-many and GET-by-id read paths in the relational document-store repository. A cached fingerprint result, a cached resource-key result, or an already-successful query-plan connection can therefore be followed by a hydrator connection-acquisition failure. If the hydrator seams are omitted, that failure is not guaranteed to produce the required Snapshot Not Found `404`. The session-scoped hydrators used by write sessions receive an existing connection and transaction, open nothing, and are correctly out of scope.

If command-time transport loss is also translated, it must use a narrowly defined provider-specific connectivity classification. Translating every `DbException` is not acceptable.

The selected target kind is carried in request scope so translation applies only to a snapshot target. Every translated failure logs the underlying error and the target kind, and never the connection string.

### Response precedence

Authentication, tenant validation, client data-store authorization, and parent route resolution occur before derivative selection, so an invalid caller or unroutable request receives the existing `401`, `403`, `400`, or `404` rather than learning whether a derivative is configured.

Path-level validation also precedes snapshot policy, matching ODS route precedence: malformed paths, malformed identifiers, unknown namespaces, and unknown resources return their existing `400` or `404` even when the request carries `Use-Snapshot: true`.

Write-path route-semantics validation likewise precedes snapshot policy. A collection `DELETE`, a collection `PUT`, or an item `POST` returns its existing generic `405` with the route-semantics detail and `application/json; charset=utf-8`, not the snapshot `405`. Because both responses are `405`, this precedence is invisible to a status-only assertion and must be asserted on the type, detail, content type, and the absence of `Allow`.

After the parent is resolved and the endpoint and route semantics are known to be valid:

- a resource or descriptor mutation with `Use-Snapshot: true` returns `405` before content-type, body, profile, coercion, document, or fingerprint validation and without opening the primary or derivative database; the snapshot policy intentionally preempts `415` or `400` responses those later mutation validations would otherwise produce;
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
- holding the derivative configuration and the snapshot database itself unchanged while an extraction reads from them, as specified under § Extraction-window stability;
- removing or replacing the CMS derivative when the database is retired.

This matches ODS ownership, avoids granting the DMS runtime database-creation privileges, and avoids pretending that SQL Server snapshots and PostgreSQL backup/restore or replication have one portable lifecycle.

Reusing one connection string across successive snapshots is supported between extraction windows, not during one. Because derivative validation results are time-bounded rather than cached for the process lifetime, a snapshot recreated at the same connection string becomes usable again after the derivative validation TTL without restarting DMS. Within an extraction window that same reuse is a hazard rather than a convenience, because the unchanged connection string leaves DMS nothing to detect; § Extraction-window stability constrains when the recreation may happen. Operators who instead give each snapshot a distinct name should still remove the retired derivative configuration, so obsolete pooled connections can be released.

A read replica may be eventually consistent. DMS does not measure replica lag or guarantee that `/availableChangeVersions` on a read replica has caught up with the primary. Operators that require a fixed extraction boundary should request a prepared `Snapshot` rather than rely on `ReadReplica`.

### Extraction-window stability

DMS binds the effective target once per request, not once per extraction, and it has no extraction, session, or cursor identity to bind to instead: Publisher issues many independent HTTP requests and nothing in the Ed-Fi API contract correlates them into one unit of work. The snapshot isolation guarantee therefore depends on the operator holding the `Snapshot` derivative fixed for the duration of an extraction:

- do not replace or re-point a data store's `Snapshot` derivative in CMS while an extraction against it is in progress;
- do not recreate the underlying snapshot database at an existing derivative connection string while an extraction against it is in progress;
- do not remove the derivative row, and do not drop or make the snapshot database unreachable, until the extractions that read from it have finished.

The three cases do not fail the same way, and only the first two are silent:

| Operator action | Detection | Extraction outcome |
| --- | --- | --- |
| The CMS derivative row is re-pointed to a different connection string | Not detected | Requests issued after the data-store configuration cache refreshes succeed against the replacement image |
| The snapshot database is recreated at the unchanged derivative connection string | Not detectable at all | Requests succeed against the replacement image once a later connection reaches it; the configuration cache is not involved, because the configuration never changed |
| The derivative row is removed, or the snapshot database is dropped or made unreachable | Detected | The extraction is interrupted with Snapshot Not Found `404` and no fallback |

The first two rows are the hazard this section exists for: the extraction completes normally while its pages come from two different points in time, and the pages already returned are not re-read. Nothing in the response distinguishes that outcome from a correctly isolated extraction. The same-connection-string case is the worse of the two, because it is invisible not only to routing but to every cache keyed by connection string, so DMS has no signal to act on even in principle.

The third row is not silent and is not an isolation defect — it is the documented missing-snapshot and unreachable-snapshot contract from § Snapshot ProblemDetails, reached mid-extraction. It still loses the extraction, so it belongs in the same operator constraint.

Because DMS cannot prevent any of the three, the operator workflow and the release notes must state the constraint and distinguish these outcomes.

## OpenAPI Surface

The served DMS v1.0 OpenAPI surface intentionally contains no snapshot artifacts. `20-openapi-change-query-surface.md` (DMS-1183) records "MetaEd does not advertise unsupported snapshot behavior in the DMS v1.0 OpenAPI surface" as delivered contract and lists snapshot advertising as out of scope, and the shipped ApiSchema packages contain no `Use-Snapshot` parameter or snapshot-aware response component. This is therefore new contract work that re-adds an intentionally deferred surface, not normalization of existing fragments.

Snapshot metadata is re-added so that the served documents describe the runtime contract consistently. The following is the required served result; which half of the rollout produces each part is settled immediately below, and the profile entries are not upstream authoring instructions:

- define a reusable boolean `Use-Snapshot` header parameter with default `false`;
- reference it from resource, descriptor, and profile GET-many and GET-by-id operations;
- reference it from `/deletes` and `/keyChanges`, in the profile documents as well as the resource and descriptor documents, and from `/availableChangeVersions`;
- document the Snapshot Not Found `404` on those GET operations;
- document the snapshot `405`, its exact ProblemDetails contract, and its `Allow: GET` response header on resource and descriptor mutation operations;
- use the exact ProblemDetails types, titles, status codes, and details above, served as `application/problem+json`.

Only three of those documents are package-authored. `openApiBaseDocuments` supplies `resources`, `descriptors`, and the standalone `changeQueries` document; there is no profile base document. DMS derives each served profile document by filtering a clone of the assembled resource document through `ProfileOpenApiSpecificationFilter`, so a profile document's snapshot artifacts are produced by DMS from upstream resource content rather than authored upstream. Splitting the work accordingly is what makes both halves actionable: MetaEd owns the three base documents, and DMS owns preserving the result through profile filtering.

Every independently served OpenAPI document that contains a `$ref` must itself define the component that reference resolves to. A document cannot resolve `#/components/parameters/Use-Snapshot` from a sibling document. That means the `Use-Snapshot` parameter and the snapshot response components must be present in each of the resource, descriptor, profile, and standalone Change Queries documents that reference them — not only in the resource and descriptor base documents. The Change Queries document is the case most likely to be missed among the package-authored documents: DMS serves it independently from `projectSchema.openApiBaseDocuments.changeQueries`, and the shipped document today carries its own `components` block with empty `parameters` and `responses` collections and no `$ref` of any kind, so its components must be populated rather than assumed to be inherited.

The profile document is the case most likely to be missed on the DMS side, and it fails in the opposite direction: the filter prunes component parameters no surviving path references, so a correctly authored upstream parameter can still be dropped from a profile document if the profiled operations lose their reference to it. Preserving the operation-level references and the `components.parameters` and `components.responses` entries they resolve to is therefore DMS work, covered by story `41-snapshot-openapi-surface.md`.

Tests must prove that every component reference resolves within its own document, for the resource, descriptor, profile, and Change Queries documents.

GET-many support is required even though the older ODS-derived fixture shape advertises the header only on the by-id GET and documents the mutation `405` as a bare description with no ProblemDetails schema and no `Allow` header. DMS deliberately documents the header and the failure contract consistently across collection and by-id operations.

The ODS-derived authoritative fixture inputs under the backend fixture tree are DDL and plan-compilation inputs, not the served MetaEd contract. They must not be edited in order to affect served OpenAPI; doing so would churn generated DDL and plan goldens without changing any served document.

Read-replica selection is deployment configuration and adds no request parameter or response field.

DMS-side document-assembly tests cover the resource, descriptor, profile, and Change Query documents.

### The OpenAPI work crosses a repository boundary

Because the components are authored upstream and no fixture in this repository can produce them, the OpenAPI surface is not deliverable inside a DMS-only ticket. DMS consumes seven ApiSchema packages: `EdFi.DataStandard52.ApiSchema`, `EdFi.DataStandard52.TPDM.ApiSchema`, `EdFi.DataStandard52.Homograph.ApiSchema`, `EdFi.DataStandard52.Sample.ApiSchema`, `EdFi.DataStandard61.ApiSchema`, `EdFi.DataStandard61.Homograph.ApiSchema`, and `EdFi.DataStandard61.Sample.ApiSchema`. Data Standard 6.1 folds TPDM into core, so there is deliberately no `EdFi.DataStandard61.TPDM.ApiSchema` package to bump. The served resource, descriptor, and standalone Change Queries documents come from those packages; the served profile documents are derived by DMS from the assembled resource document.

Those seven packages do not all arrive by one mechanism, and the difference determines what a version bump must touch. DMS has two intake paths:

- a **bundled path**, where the frontend project takes direct `PackageReference`s on `EdFi.DataStandard52.ApiSchema` and `EdFi.DataStandard52.TPDM.ApiSchema` with `GeneratePathProperty="true"`, their versions resolve from `src/Directory.Packages.props`, and their resolution is recorded in that project's `packages.lock.json`. `src/dms/Directory.Build.targets` declares bundled entries for four Data Standard 5.2 packages but gates each on the generated path property, so only packages that also carry a direct reference are materialized, and it declares no Data Standard 6.1 entries at all;
- a **file-based path**, where `SCHEMA_PACKAGES` and the bootstrap schema catalog select and download packages at deployment time. Its pins live in the tracked `eng/docker-compose/` environment overlays and in the bootstrap catalog's core package identity and fallback version, and they are independent of `src/Directory.Packages.props`. This is how Sample, Homograph, and all of Data Standard 6.1 reach a running DMS, and it can serve the full set rather than only the families the bundled path omits.

Consequently a central version declaration is not by itself a served-package update: five of the seven have no direct `PackageReference` consumer today, so bumping only `src/Directory.Packages.props` would leave the file-based pins on their previous versions and those families would continue serving OpenAPI without the snapshot contract. No new `PackageReference` is added merely to make a single-mechanism description true; the two paths are existing runtime topology, and changing it is outside this proposal.

The rollout therefore has an explicit, ordered dependency:

1. an upstream MetaEd/ApiSchema ticket adds the `Use-Snapshot` parameter and the snapshot response components to the three package-authored base documents — resources, descriptors, and the standalone Change Queries document — and is created and linked before the DMS story is scheduled;
2. the ApiSchema packages are published;
3. the DMS story updates every active version-selection surface needed to serve the published packages — the bundled path's central versions and lock, and the file-based path's `SCHEMA_PACKAGES` overlays and bootstrap catalog fallback — and delivers the document-assembly, profile-filter preservation, and reference-resolution work and its tests, verified under both intake modes and across every supported package family.

`41-snapshot-openapi-surface.md` records the upstream contract as a prerequisite rather than as its own acceptance criteria, matching how `20-openapi-change-query-surface.md` separated the delivered MetaEd contract from the DMS continuation. The DMS story does not start until the upstream change is published in ApiSchema packages, and its first commit is the package bump. DMS assembly and reference-resolution work is not landed against a hand-authored or backend fixture in advance. If preparatory DMS work is needed before package publication, it must be a separate explicitly scoped story that identifies an upstream-produced prerelease artifact and cannot satisfy any served-surface acceptance criterion.

The package bump is expected to be hash-neutral, because DMS-1183 established that `projectSchema.openApiBaseDocuments` is stripped before effective-schema hashing, model derivation, DDL generation, and mapping-pack selection. An OpenAPI-only bump should therefore need no `apiSchemaVersion` change and should not churn DDL, plan, or mapping-set goldens. The DMS story verifies this rather than assuming it: a hash or golden change indicates the package carried more than the OpenAPI contract and must be investigated before the bump is accepted.

## API Publisher Interoperability

Interoperability validation must prove that a Publisher extraction can use DMS without `--ignoreIsolation=true` when a snapshot is configured:

- Publisher's `Use-Snapshot: true` source requests are served only from the configured snapshot;
- a write committed to the primary after snapshot creation does not appear during that extraction;
- live GET-many, GET-by-id, `/deletes`, `/keyChanges`, and `/availableChangeVersions` all carry `Use-Snapshot: true` within one extraction, which follows from Publisher setting the header on the source client's default headers rather than on a single probe. Those requests resolve to the same snapshot database only while the derivative configuration and the underlying snapshot are unchanged: the default header proves every request asks for the snapshot, not that the snapshot is the same database on every request;
- the operator constraint from § Extraction-window stability is documented, distinguishing its two silent outcomes — a re-pointed derivative row, and a database recreated at the unchanged connection string — from removal or unreachability, which instead interrupts the extraction with the `404` covered below;
- a mutation carrying `Use-Snapshot: true` returns the snapshot `405` with `Allow: GET`;
- when no snapshot is configured, a snapshot-eligible read carrying `Use-Snapshot: true` produces the expected `404`;
- retiring or making the configured snapshot unreachable produces the same `404`;
- `Use-Snapshot: false` does not select the snapshot.

Validation records the DMS, Ed-Fi API, and Publisher versions used, along with the configuration required to reproduce the run, so future changes to Publisher's isolation behavior can be evaluated reproducibly. The DMS version is recorded because Publisher keys its isolation probe on the advertised API major version, which does not identify the DMS build that served the request.

## Test Expectations

Implementation coverage should include:

- CMS PostgreSQL and SQL Server integration tests for the unique and check constraints, the new insert and update conflict repository results, preflight diagnostics for both duplicate rows and invalid derivative types, rejection of case variants such as `SNAPSHOT` and whitespace variants such as `Snapshot ` in both engines, tenant isolation, and derivative inclusion in data-store responses.
- CMS frontend unit or E2E coverage asserting that both the insert duplicate and the update duplicate return HTTP `409`. The integration tests above prove the repository conflict result, not the response mapping.
- DMS configuration-provider unit tests for derivative deserialization, decryption, unknown types, null and blank connection strings, tenant caches, and cache refresh.
- DMS configuration-provider tests for an undecryptable derivative connection string — invalid Base64, a payload at or below the IV length, and a valid Base64 payload encrypted under a different key — proving each of the three failure modes leaves the parent data store loaded with its primary and its other usable derivatives, marks only the affected derivative unusable, and logs an error without the ciphertext or any connection string. Include a data store whose sibling derivative is valid and a second data store in the same CMS response, so fault isolation is proven at both the derivative and the response level.
- A characterization test pinning the unchanged primary behavior: an undecryptable primary connection string fails the whole tenant data-store load, including the other data stores in the same response. This records existing behavior so the derivative change is visibly not narrowing it, and so a later decision to isolate it is a deliberate edit to this test rather than a silent drift.
- End-to-end behavior of an unusable derivative: an undecryptable `Snapshot` yields Snapshot Not Found `404` for a snapshot-eligible read, and an undecryptable `ReadReplica` serves the request from the primary with an error log distinguishable from the no-replica-configured path.
- Core routing unit tests for every row in both eligibility matrices, snapshot precedence over read replica, absence of fallback, header parsing, response precedence, and rejection of a second effective-target assignment within one request.
- Frontend or E2E coverage for blank and multi-valued `Use-Snapshot` headers, which core cannot observe because the frontend normalizes them.
- PostgreSQL and SQL Server integration tests using distinct primary, read-replica, and snapshot databases with distinguishable data. The SQL Server path requires the SQL Server 2025 integration environment described in `AGENTS.md`, and a three-database fixture is a meaningful fixture-cost increase for the runtime-routing ticket.
- Snapshot-unavailable tests at every read-path connection-acquisition seam: fingerprint acquisition, resource-key validation, normal repository connection acquisition, and document-hydration connection acquisition. Hydration coverage must be explicit rather than folded into generic repository coverage, and must include the case where fingerprint and resource-key results are already cached so the hydrator acquisition is the first failure in the request.
- PostgreSQL and SQL Server tests for a decrypted, non-blank, provider-invalid derivative connection string, proving the failure is raised inside the acquisition boundary rather than escaping as an unhandled provider argument failure: a selected `Snapshot` returns Snapshot Not Found `404` with no fallback, a selected `ReadReplica` returns the normal database-availability response and is not served from the primary, the verdict is not cached so a corrected CMS row recovers on the next request, an equivalently malformed primary connection string keeps its existing behavior, and no log records the connection string. Cover both the construction-time parse and the open call, since only the former distinguishes this case from an unreachable host.
- Tests proving a reachable but unprovisioned or fingerprint-incompatible snapshot returns the existing `503`, not Snapshot Not Found, and that an ordinary query failure against a snapshot is not translated either.
- Tests proving a snapshot recreated at the same connection string recovers after the derivative validation TTL without a service restart, and that a removed or replaced derivative eventually releases its pooled data source without disrupting in-flight requests.
- Tests proving the derivative validation TTL remains bounded when data-store cache refresh is disabled or its interval is non-positive, and that a shorter data-store interval shortens the effective TTL.
- Settings-resolution tests for `DerivativeValidationCacheExpirationSeconds` at zero, negative, absent, `1`, `3600`, and above `3600`, asserting the effective value and the startup warning on each out-of-range case, and confirming a non-positive value resolves to the default rather than to no expiration.
- Tests proving failed, missing, and malformed derivative validation results are not cached, so the next request revalidates instead of reusing the failure.
- Tests proving authorization and route-context selection are still based on the parent data store and that all authorization SQL for a request uses the selected target.
- Tests proving an unknown resource path with `Use-Snapshot: true` returns its existing `404` rather than the snapshot `405`.
- Tests proving an invalid mutation route with `Use-Snapshot: true` — collection `DELETE`, collection `PUT`, and item `POST` — returns the existing route-semantics `405` rather than the snapshot `405`, asserted on type, title, detail, content type, and absence of `Allow`, since both responses share the status code.
- Tests proving a route-valid resource or descriptor mutation with `Use-Snapshot: true` returns the snapshot `405` before later mutation validation. Cover an invalid or missing content type that would otherwise return `415`, malformed or invalid body input that would otherwise return `400`, and profile/document validation failures; assert the snapshot ProblemDetails body, `application/problem+json`, and `Allow: GET`, and prove no database connection is opened.
- Pipeline-composition tests asserting the selection step's position in all eight pipelines, that `/availableChangeVersions` gains selection but gains no path, ApiSchema, endpoint, or resource-key step, and that token introspection runs selection and resolves `Primary`.
- Tests proving `/availableChangeVersions` honors `Use-Snapshot: true` against a configured snapshot and selects a configured read replica for a normal read, with both fingerprint validation and the handler running against the selected target.
- OpenAPI document-assembly tests for resource, descriptor, profile, and Change Query documents, including a check that every `$ref` resolves within its own served document, and explicit enumeration of the profile document's `/deletes` and `/keyChanges` operations so their snapshot coverage cannot be satisfied by the unprofiled operations alone.
- Profile-filter tests proving `ProfileOpenApiSpecificationFilter` preserves the snapshot parameter and response references on surviving profiled operations and retains the `components.parameters` and `components.responses` entries those references resolve to, for readable and writable profiles and for profile `/deletes` and `/keyChanges`. The pruning of unreferenced component parameters is the specific mechanism that can silently drop `Use-Snapshot` from a profile document even when the upstream packages are correct, so this coverage is required in addition to the reference-resolution check above.
- Runtime coverage that a profile-shaped `/deletes` and `/keyChanges` read honors `Use-Snapshot: true` and read-replica selection identically to its unprofiled counterpart, so a profiled extraction cannot mix snapshot live reads with current-data tombstones.
- DMS E2E coverage for GET-many, GET-by-id, `/deletes`, `/keyChanges`, `/availableChangeVersions`, `405` plus `Allow: GET`, missing snapshot, and snapshot precedence over read replica.
- API Publisher interoperability coverage as described above.

## Compatibility and Rollout

This changes the post-v1.0 behavior of an existing header:

- DMS v1.0 ignores `Use-Snapshot: true` and reads current data.
- After this feature, the same request reads the configured snapshot or returns `404`.

Release notes must call out that operators using API Publisher either configure a snapshot or continue to opt out explicitly with `--ignoreIsolation=true`.

Release notes must also call out that a snapshot must not be replaced, re-pointed, removed, or recreated at the same connection string while an extraction is reading from it, because DMS selects the target per request. Re-pointing the derivative row or recreating the database at the unchanged connection string silently moves later pages to the replacement image; removal or unreachability instead interrupts the extraction with Snapshot Not Found `404`.

Release notes must also call out that read-replica routing becomes active for any valid `ReadReplica` rows already stored in CMS, and that a read replica may be eventually consistent. Creating a derivative row is itself the configuration action that enables routing, matching ODS, so this is the activation of previously inert configuration rather than an implicit default. Operators should verify or remove stale derivative configuration before upgrading.

No database document or Change Query DDL changes are required in the DMS data store. Snapshot databases must already contain the same generated DDL as their primary because they are copies of a provisioned primary.

## Rejected Alternatives

- **DMS creates and drops snapshots.** Rejected because lifecycle and permissions are database-engine-specific and ODS already treats this as an operator concern.
- **An additional feature flag to enable read-replica routing.** Rejected because the derivative row is already the operator's explicit opt-in, and ODS requires no second switch. A flag would add a configuration surface that ODS parity does not need.
- **Excluding GET-by-id from read-replica selection.** Rejected because ODS selects the replica for any read-only access intent, and a DMS-only exclusion would diverge from ODS without a corresponding contract difference.
- **Use the read replica when `Use-Snapshot: true` but no snapshot exists.** Rejected because a continuously changing replica does not provide the requested point-in-time isolation.
- **Fall back to primary when a selected derivative fails.** Rejected because it silently changes the consistency contract.
- **Translating every `DbException` raised under a snapshot target to Snapshot Not Found.** Rejected because it would convert ordinary query, mapping, and provisioning defects into a misleading `404`. DMS preserves the ODS client contract through a structural connection-acquisition boundary instead.
- **Validating derivative connection-string syntax when data-store configuration is loaded, so a provider-invalid string is treated as not configured.** Rejected because the core configuration provider references neither PostgreSQL nor SQL Server client libraries, so it cannot ask a provider whether a string is valid without acquiring an engine dependency it does not otherwise need. Only the backend seams can make that determination, so the case is handled as configured-but-unavailable at acquisition instead.
- **Caching derivative validation results for the process lifetime, as the primary does.** Rejected because a snapshot is recreated between extraction windows, so a latched failure would require a service restart to clear.
- **Bounding derivative validation by the data-store configuration cache interval instead of an independent TTL.** Rejected because that interval is permitted to be disabled or non-expiring through `DataStoreCacheRefreshEnabled` and a non-positive `DataStoreCacheExpirationSeconds`, which would make the derivative TTL unbounded and reintroduce process-lifetime caching under ordinary configuration.
- **Requiring data-store cache refresh to be enabled as a precondition for derivative routing.** Rejected because it makes an unrelated performance setting a hard prerequisite for snapshot support while still leaving the TTL coupled to an operator-chosen value that may be arbitrarily high.
- **Failing the data-store load when a derivative connection string cannot be decrypted.** Rejected because an optional derivative would then be able to disable a working data store and every other data store in the same CMS response. The derivative is marked unusable with an error log instead, matching the existing treatment of an unrecognized `DerivativeType`.
- **Open a transaction with snapshot isolation for each request.** Rejected because Publisher extraction spans many HTTP requests and needs one stable database image across all of them.
- **Pin the snapshot binding to an extraction, so a mid-extraction replacement cannot move later pages.** Rejected because neither the Ed-Fi API contract nor Publisher supplies an extraction, session, or cursor identity for DMS to bind to. DMS would have to invent that identity, decide when to expire it, and keep a superseded snapshot's connection alive for an unbounded period, which conflicts with the bounded derivative caches and with an operator's ability to retire a snapshot at all. The isolation guarantee is preserved instead by the explicit operator obligation in § Extraction-window stability.
- **Route derivatives as independent data stores.** Rejected because authorization, tenant, and route context belong to the parent and duplicating them can create isolation gaps.

## Resolved Decisions

Reviewed and resolved during this spike:

1. **Read-replica routing follows ODS parity.** All resource and descriptor GET-many and GET-by-id requests, profile-shaped reads, `/deletes`, `/keyChanges`, their profile-shaped variants, and `/availableChangeVersions` are replica-eligible. The derivative row is the opt-in; no additional flag is added. Snapshot outranks replica; writes stay on the primary; token introspection and non-data-management surfaces stay on the primary.
2. **Derivative validation and pool caches are time-bounded by an independent TTL.** Primary behavior is unchanged; derivative results are never cached for the process lifetime, and recreating a snapshot at the same connection string recovers without a restart. The TTL is its own bounded `CacheSettings` value rather than the data-store configuration cache interval, because that interval may be disabled or non-expiring; derivative routing does not require data-store refresh to be enabled.
3. **Snapshot-unavailable `404` is broad at the connection-acquisition boundary.** Catalog absence, authentication failure, network failure, timeout, firewall rejection, and a provider-invalid connection string all return Snapshot Not Found for a snapshot target, while ordinary SQL, mapping, authorization, fingerprint-shape, and application errors do not. The boundary spans provider data-source and connection construction, connection-string parsing, and the open call, because every seam parses the connection string before opening it. Coverage is all seven read-path connection-acquisition seams, including both document hydrators. A decrypted but provider-invalid derivative string is configured-but-unavailable rather than not configured: a selected snapshot returns the `404` and a selected read replica keeps the normal database-availability contract, neither falling back.
4. **The snapshot `405` is scoped to resource and descriptor mutations,** and triggers only on a successfully parsed `true`. This intentionally narrows the acceptance criterion's "non-`GET`" wording to the data-management surface, and intentionally diverges from ODS's header-presence filter.
5. **The CMS uniqueness migration hard-stops on duplicates,** reports the offending rows, and documents deletion through the derivative endpoint as remediation.
6. **Path and route-semantics validation precede snapshot policy, while later mutation validation does not.** An unknown resource still returns its existing `400` or `404`, and an invalid mutation route keeps its existing generic `405`; once the route is a valid resource or descriptor mutation, `Use-Snapshot: true` returns the snapshot `405` before content-type, body, profile, coercion, document, fingerprint, or resource-key validation and before any database connection. The insertion point is specified per pipeline rather than as one generic rule, because `/availableChangeVersions` and token introspection perform no endpoint validation to follow. `/availableChangeVersions` does not gain ApiSchema, endpoint, path, or resource-key validation in order to satisfy the rule, and token introspection explicitly assigns `Primary` while remaining ineligible for either derivative type.
7. **The OpenAPI surface is re-added, not normalized,** including a reusable `Use-Snapshot` parameter applied to GET-many as well as GET-by-id, and a new snapshot-specific `405` ProblemDetails factory. The components are authored upstream in MetaEd and reach DMS only through published ApiSchema packages, so the surface is split into an upstream MetaEd ticket covering the three package-authored base documents — resources, descriptors, and the standalone Change Queries document — and a DMS story that updates every active ApiSchema version-selection surface — the bundled path's central versions and lock, and the file-based `SCHEMA_PACKAGES` overlays and bootstrap catalog fallback that serve Sample, Homograph, and Data Standard 6.1 — and serves the result, verified under both intake modes. There is no profile base document: DMS derives each served profile document by filtering the assembled resource document, so preserving the snapshot references and the components they resolve to through that filter is DMS work rather than upstream work. No served OpenAPI content is authored in this repository.
8. **Extraction-wide snapshot stability is an operator obligation, not a DMS binding.** Target selection stays per request because DMS has no extraction identity to pin to. Operators must not replace, re-point, remove, or recreate a `Snapshot` derivative while an extraction against it is in progress. Re-pointing and same-connection-string recreation are silent, and the latter is undetectable by DMS even in principle; removal and unreachability surface as the existing Snapshot Not Found `404`. The obligation and the distinction between those outcomes are stated in the design, the operator workflow, and the release notes.

## Follow-on Ticket Plan

Create and link the following implementation tickets only after this proposal is approved. The story files are created with Jira placeholders so the ticket keys can be inserted after Jira creation, and `EPIC.md` § Follow-on Stories carries the same `TBD` placeholders for the same reason. Filling in the keys is a single step covering both places: each story's `jira` / `jira_url` front matter and the matching `EPIC.md` row. Until then these are approved-design slices, not traceable work items.

| Story | Area | Scope |
| --- | --- | --- |
| `38-cms-data-store-derivative-invariants.md` | CMS/admin database shape | Add the named `(DataStoreId, DerivativeType)` unique constraint and a `DerivativeType` check constraint requiring ordinal equality including length for PostgreSQL and SQL Server, add the padding-exact preflight for duplicate rows and invalid derivative types with its diagnostics, add insert and update conflict result variants plus their frontend `409` mappings and frontend-level coverage, and cover upgrade behavior and CMS tests. |
| `39-snapshot-read-replica-runtime-routing.md` | DMS configuration and runtime routing | Add derivatives to the configuration response model and `DataStore` record, decrypt them in per-derivative fault boundaries so an undecryptable optional derivative cannot fail the data-store load, introduce the two-phase effective request-scoped connection target, apply snapshot and replica eligibility from pipeline construction, implement the independent bounded derivative validation TTL and pooled-data-source eviction, and cover both relational backends. Reorder the routed-resource and tracked-changes pipelines so `ApiSchemaValidationMiddleware`, `ProvideApiSchemaMiddleware`, and `ValidateEndpointMiddleware` precede fingerprint and resource-key validation, then insert target selection at the per-pipeline points tabulated in § Per-pipeline insertion points — immediately after route-semantics validation and before later content/body/profile/document validation on the mutation pipelines, and directly after the common steps on `/availableChangeVersions` and token introspection, which gain no new validation steps. Token introspection assigns `Primary` but is never derivative-eligible. Re-key the scoped PostgreSQL data-source provider by effective target or connection string, or remove the redundant scoped dictionary; never key by parent `DataStore.Id`. Includes updating the integration-test data-store provider double and the configuration-provider unit tests. |
| `40-snapshot-problem-details.md` | Snapshot ProblemDetails | Add the snapshot `405` factory and `Allow: GET`, emit the missing-snapshot `404` from the existing not-found factory, add the backend-neutral connection-unavailable exception at all seven enumerated read-path connection-acquisition seams including both document hydrators, wrapping provider data-source and connection construction and connection-string parsing as well as the open call so a provider-invalid derivative string is classified rather than escaping, keep provisioning and query defects on their existing contracts, and log safely. |
| `41-snapshot-openapi-surface.md` | OpenAPI surface (DMS half) | Adopt the seven ApiSchema packages carrying the upstream snapshot components by updating every active version-selection surface — the bundled path's central versions and lock, and the file-based `SCHEMA_PACKAGES` overlays and bootstrap catalog fallback that serve Sample, Homograph, and Data Standard 6.1 — plus the associated version assertions and documentation, then serve them from resource, descriptor, and Change Query operations with every referenced component defined in each independently served document including the standalone Change Queries document. Own the profile documents outright: prove `ProfileOpenApiSpecificationFilter` preserves the snapshot parameter and response references and the `components.parameters` and `components.responses` entries they resolve to, including for profile `/deletes` and `/keyChanges`, given that the filter prunes unreferenced component parameters. Add DMS document-assembly, operation-coverage, and reference-resolution tests exercising both intake modes and every supported package family, so two bundled Data Standard 5.2 packages cannot satisfy the story alone, and confirm the bump is hash- and golden-neutral. Depends on the upstream MetaEd ticket below and does not touch the backend authoritative fixture inputs. |
| *(upstream, separate repository)* | MetaEd/ApiSchema snapshot components | Author the reusable `Use-Snapshot` parameter and the snapshot `404`/`405` response components and reference them from the three package-authored base documents — resources, descriptors, and the standalone Change Queries document — then publish the ApiSchema packages. There is no profile base document to author; profile documents are DMS-derived. Tracked as its own MetaEd ticket, created and linked before `41-snapshot-openapi-surface.md` is scheduled. Not a DMS ticket. |
| `42-api-publisher-snapshot-interoperability.md` | API Publisher interoperability | Add an environment and automated or repeatable validation for Publisher isolation behavior against DMS, and document the operator workflow. Depends on the four preceding slices; the Publisher validation itself is release-validation work rather than DMS product code. |

## Acceptance Criteria Coverage

- The routing change is specified above: request-scoped selection, its interaction with the existing data-store resolver, the two eligibility axes, the selection matrices, response precedence, and the endpoints that support snapshots and read replicas.
- Both deferred Snapshot ProblemDetails are specified above, including the `Allow: GET` header, the reused `404` factory, and the new `405` factory. The acceptance criterion's "non-`GET`" wording is deliberately scoped to resource and descriptor mutations, as recorded in the resolved decisions.
- Snapshot and read-replica creation, refresh, and teardown are explicitly operator-owned; DMS provides no engine-specific tooling.
- The required implementation-ticket slices are defined above and await approval of this proposal before Jira creation.
