---
status: proposed
date: 2026-07-07
jira: DMS-1246
related:
  - DMS-1245
  - DMS-1232
  - DMS-1240
---

# Decision Record: `dms.DocumentCache` Role and Enablement

## Decision

`dms.DocumentCache` is an optional materialized document projection for normal DMS
operation, but it is conditionally required when relational Debezium/Kafka CDC is
enabled.

The design separates three concerns:

1. API response materialization: the relational reconstitution/materialization pipeline
   can build the caller-agnostic full resource document without using
   `dms.DocumentCache`.
2. Read acceleration and indexing: `dms.DocumentCache` may be enabled as an eventually
   consistent materialized projection. Reads may use it only when a row is fresh, and
   downstream indexers may consume it as a projected state table.
3. Relational Kafka CDC: CDC captures `dms.DocumentCache` as the public document-state
   source. In this mode, DMS adds stronger readiness, stale-write, and delete guarantees
   around the projector.

`dms.DocumentCache` is never the canonical persistence model. Write correctness,
authorization, identity resolution, Change Queries, and normal GET/query correctness must
continue to depend on the relational source tables and `dms.Document`, not on cache rows.

## Configuration Contract

Use separate settings for projection, read acceleration, and Kafka CDC. A single
`DocumentCacheEnabled` flag is too ambiguous because ordinary cache-backed reads and CDC
have different correctness requirements.

Recommended design-level settings:

```text
DataManagement:DocumentCache:Projector:Mode = Disabled | Async | CdcRequired
DataManagement:DocumentCache:ReadAcceleration:Enabled = false | true
DataManagement:KafkaCdc:Enabled = false | true
DataManagement:KafkaCdc:Targets = [{ TenantKey, DataStoreId }, ...]
DataManagement:KafkaCdc:BlockMutationsWhenNotReady = false | true
```

Mode semantics:

| Setting | Meaning |
| --- | --- |
| `Projector:Mode = Disabled` | DMS does not write `dms.DocumentCache`. The table may be absent unless provisioned for another purpose. |
| `Projector:Mode = Async` | DMS maintains `dms.DocumentCache` asynchronously for read acceleration, indexing, or diagnostics. Rows may be missing or stale. |
| `Projector:Mode = CdcRequired` | DMS maintains `dms.DocumentCache` with CDC readiness, stale-write fencing, bounded initial backfill, visible health, and pre-delete source-row guarantees. |
| `ReadAcceleration:Enabled = true` | GET/query response assembly may read fresh cache rows, but must fall back to relational reconstitution for misses or stale rows. |
| `KafkaCdc:Enabled = true` | Requires `Projector:Mode = CdcRequired` and a provisioned `dms.DocumentCache`; source prerequisites must pass before connector registration, and completed source/connector readiness must pass before CDC is advertised as supported. |
| `KafkaCdc:Targets` | Explicit deployment-owned list of `(tenant key, DataStoreId)` values for which CDC is provisioned. No CMS data store becomes a CDC target merely because it is loaded at startup or discovered later. |
| `KafkaCdc:BlockMutationsWhenNotReady = true` | Optional host availability policy that returns `503` for mutations to a configured CDC target while its CDC readiness is false. The default is `false`, and read-only requests are never blocked by this policy. |

When `KafkaCdc:Enabled = true`, `Targets` must be non-empty and unique after tenant-key
normalization. `BlockMutationsWhenNotReady = true` is invalid unless Kafka CDC is enabled.
The target list is bound once at startup; configuration reload does not add or retire CDC
targets inside a running deployment.

Indexing or external integration use cases that do not need Kafka CDC should use
`Projector:Mode = Async`. They may leave `ReadAcceleration:Enabled = false` if the API
should continue to assemble responses directly from relational tables.

Kafka UI, Kafka infrastructure startup, and connector registration are separate concerns.
Starting Kafka UI must not imply `KafkaCdc:Enabled`.

### Scope Across Data Stores

The v1 projector and read-acceleration settings are process-wide defaults, not per-data-store
CMS options. Each data store loaded from the startup configuration with a usable connection
string is therefore a projection target when `Projector:Mode` is `Async` or `CdcRequired`.
CDC target selection is deliberately narrower: `KafkaCdc:Targets` is an explicit
deployment-owned list, and only listed data stores receive connector registration and CDC
readiness. V1 does not infer CDC target membership from the most recent HTTP request, the
set returned by CMS, or JWT `DataStoreIds`.

Connector registration remains per configured CDC target. A failed or incomplete projector
makes that target's CDC readiness false without making normal API correctness or routing
depend on it. Deployment health may also expose an aggregate result that is false when any
configured CDC target is not ready. A future CMS-backed per-data-store opt-in may replace
the deployment list, but it is not part of v1.

V1 does not discover CDC targets dynamically. Adding or removing a target requires an
explicit deployment-configuration change and the coordinated provisioning, connector, and
DMS deployment workflow.

### CDC Target Binding and Source Drift

When `KafkaCdc:Enabled = true`, startup resolves each entry in `KafkaCdc:Targets` and
captures an immutable CDC source binding. Tenant keys use the same case-insensitive
normalization as `IDataStoreProvider`. Each binding contains `(tenant key, DataStoreId)` and
a provider-resolved physical database identity. DMS must not fingerprint the complete
connection configuration: passwords, application names, pool settings, timeouts, and
equivalent server aliases do not define the CDC source. Physical identity comparison uses
the same provider-specific resolution required to prevent two targets from capturing the
same database. Connection strings, credentials, and unsanitized physical identifiers must
not be logged, persisted, or exposed through health responses.

The ordinary `IDataStoreProvider` may still refresh or reload CMS data for request routing.
A successful reload does not change the configured target list or reconcile Kafka Connect.
It updates normal request routing as usual. CDC source readiness compares only configured
targets with their startup source bindings:

- a CMS data store not present in `KafkaCdc:Targets` is ordinary routing data and is ignored
  by CDC readiness,
- a configured target that is absent from CMS is not CDC-ready,
- a credential rotation or other connection-setting change that resolves to the same
  provider and physical database is not source drift,
- a configured target that resolves to a different provider or physical database has
  source drift,
- a data-store name or route-qualifier-only change does not affect CDC source identity.

When a configured target's provider or connection settings change, its CDC readiness is
`SourceIdentityVerificationPending` until provider-specific resolution completes. A
same-source result clears that transient state; a different source latches drift. This
readiness transition does not delay or roll back the CMS routing refresh. It matters to API
availability only when the host has explicitly enabled mutation blocking.

Confirmed source drift is latched for the lifetime of the process. Restoring the CMS entry
to its previous value does not clear the failure. A coordinated deployment that reruns
physical-source validation, provisioning, connector registration, and DMS startup is the
explicit point that accepts a new binding. Inability to resolve physical identity because
of a transient connection failure reports a retryable not-ready reason; it is not evidence
of drift and is not latched as a source change.

CDC readiness is observational by default. A missing target, source drift, projector
failure, or connector failure does not alter `IDataStoreSelection` and does not block normal
API requests. In particular, GET/query, Change Queries, discovery, and other read-only
requests continue to use the current CMS routing configuration.

Deployments that choose API availability coupling for zero-loss CDC may explicitly set
`KafkaCdc:BlockMutationsWhenNotReady = true`. After ordinary routing selects a configured
CDC target, this policy returns HTTP `503` for mutation requests (`POST`, `PUT`, `DELETE`,
and any future write method) while that target's end-to-end CDC readiness is false. It does
not apply to non-targets, defaults to `false`, and never blocks `GET`, `HEAD`, or `OPTIONS`.
The readiness check and optional mutation gate never call Kafka Connect or perform cleanup.

The stable confirmed-drift reason is `CdcSourceDriftRequiresDeployment`. Health and optional
mutation error responses may expose that reason plus the opaque tenant/data-store key and a
sanitized drift kind, but not the physical identity or connection details.

## Cached Shape

`DocumentJson` contains the caller-agnostic, pre-profile, full API resource body emitted
by the same reconstitution/materialization path used for GET/query responses. It
includes stable top-level server-generated fields `id` and `_lastModifiedDate`. It does
not store one reusable `_etag`; `_etag` is composed from `ContentVersion` and the active
`variantKey` at the serving or stream-shaping boundary. When link injection is compiled
into the read plan, the cached document also includes reference `link` subtrees.

Readable-profile projection and `DataManagement:ResourceLinks:Enabled` stripping happen
after cache retrieval. They do not create separate cache rows.

The cache row also stores `DocumentUuid`, `ContentVersion`, and `LastModifiedAt` as
relational columns for freshness checks, diagnostics, indexing, and CDC metadata.
`DocumentUuid` and `LastModifiedAt` must match the corresponding values embedded in
`DocumentJson`.

DMS should expose a dedicated cache-projection materialization contract instead of
letting the projector reuse an arbitrary GET/query response object. The contract returns
one coherent projection result: `DocumentUuid`, project/resource identifiers,
`ContentVersion`, `LastModifiedAt`, and `DocumentJson`. `DocumentJson` is the full
external API-shaped document after stable server metadata injection, but before `_etag`
composition, readable-profile projection, and response-only link stripping. The
relational columns are derived from the same source metadata and formatted values as the
embedded JSON fields.

Before writing `dms.DocumentCache`, DMS must validate the metadata invariant:

- `DocumentJson.id == DocumentUuid`,
- `DocumentJson._lastModifiedDate == formatted LastModifiedAt`.

If the invariant fails, the projector treats the attempt as a projection failure and
must not write a cache row. This prevents Kafka envelope fields, cache columns, and the
embedded API document from drifting apart.

The cache row stores the metadata needed by reads, diagnostics, indexing, and CDC:

- `DocumentUuid`
- `ProjectName`
- `ResourceName`
- `ResourceVersion`
- `ContentVersion`
- `LastModifiedAt`
- `DocumentJson`
- `ComputedAt`

`DocumentId` remains the internal primary key and foreign key to `dms.Document`; it is not
part of the public Kafka contract.

## Consequences

- DMS can run without `dms.DocumentCache` when neither cache-backed reads nor Kafka CDC
  are enabled.
- Cache-backed reads are opportunistic. A missing, stale, unhealthy, or disabled cache
  must not break GET/query behavior.
- CDC enablement is stricter than cache-backed reads. Connector registration and CDC
  readiness must fail when `dms.DocumentCache` cannot supply the DMS-1245 source
  contract.
- Authorization must run against relational authorization sources. Cached JSON must not
  contain authorization arrays, EdOrg hierarchy JSON, API client identity, or
  readable-profile-specific projections.
- There is one cached full-resource projection per document. DMS does not maintain
  separate link-free, profile-specific, or consumer-specific `DocumentCache` rows.

## Alternatives Considered

### Make `dms.DocumentCache` mandatory for all DMS deployments

Rejected. It would make ordinary API correctness depend on an eventually consistent
projection and increase operational requirements for deployments that do not use CDC,
indexing, or cache-backed reads.

### Treat `dms.DocumentCache` as only a read cache

Rejected. Existing backend-redesign documents intentionally use it as a materialized
projection for downstream indexing and relational CDC. Calling it only a cache obscures
the CDC source-row and projector-health responsibilities.

### Use a single enablement flag

Rejected. Read acceleration can tolerate misses and stale rows because it falls back to
relational reconstitution. CDC cannot tolerate a missing delete source row because that
would silently lose the Kafka tombstone. Separate settings make those guarantees explicit.
