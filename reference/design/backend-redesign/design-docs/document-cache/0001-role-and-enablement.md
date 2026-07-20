---
status: proposed
date: 2026-07-20
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
3. Relational Kafka CDC: one connector captures `dms.DocumentCache` create/update/snapshot
   events as public document upserts and `dms.Document` deletes as authoritative
   tombstones. Cache deletion has no domain meaning and is ignored by the connector.

`dms.DocumentCache` is never the canonical persistence model. Write correctness,
authorization, identity resolution, Change Queries, and normal GET/query correctness must
continue to depend on the relational source tables and `dms.Document`, not on cache rows.

## Configuration Contract

Use separate settings for projection, read acceleration, and Kafka CDC. The projector has
one enabled behavior: asynchronous, eventually consistent projection. CDC readiness may
observe its backfill, lag, and failures, but it does not introduce a distinct projector
mode or request-path delete behavior.

Recommended design-level settings:

```text
DataManagement:DocumentCache:Projector:Mode = Disabled | Async
DataManagement:DocumentCache:ReadAcceleration:Enabled = false | true
DataManagement:KafkaCdc:Enabled = false | true
DataManagement:KafkaCdc:Targets = [{ TenantKey, DataStoreId }, ...]
```

Mode semantics:

| Setting | Meaning |
| --- | --- |
| `Projector:Mode = Disabled` | DMS does not write `dms.DocumentCache`. The table may be absent unless provisioned for another purpose. |
| `Projector:Mode = Async` | DMS maintains `dms.DocumentCache` asynchronously for read acceleration, indexing, or diagnostics. Rows may be missing or stale. |
| `ReadAcceleration:Enabled = true` | GET/query response assembly may read fresh cache rows, but must fall back to relational reconstitution for misses or stale rows. |
| `KafkaCdc:Enabled = true` | Requires `Projector:Mode = Async` and a provisioned `dms.DocumentCache` for upserts. One connector also captures `dms.Document` for deletes. Source prerequisites must pass before registration, and source/connector readiness must pass before CDC is advertised as ready. |
| `KafkaCdc:Targets` | Explicit deployment-owned list of `(tenant key, DataStoreId)` values for which CDC is provisioned. No CMS data store becomes a CDC target merely because it is loaded at startup or discovered later. |

When `KafkaCdc:Enabled = true`, `Targets` must be non-empty and unique after tenant-key
normalization. The target list is bound once at startup; configuration reload does not add
or retire CDC targets inside a running deployment.

Indexing or external integration use cases that do not need Kafka CDC should use
`Projector:Mode = Async`. They may leave `ReadAcceleration:Enabled = false` if the API
should continue to assemble responses directly from relational tables.

Kafka UI, Kafka infrastructure startup, and connector registration are separate concerns.
Starting Kafka UI must not imply `KafkaCdc:Enabled`.

### Scope Across Data Stores

The v1 projector and read-acceleration settings are process-wide defaults, not per-data-store
CMS options. Each data store loaded from the startup configuration with a usable connection
string is therefore a projection target when `Projector:Mode` is `Async`.
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

Target/source binding and drift are connector-deployment concerns owned by DMS-1245; see
[../cdc/0003-debezium-connector-deployment.md](../cdc/0003-debezium-connector-deployment.md).
The DocumentCache projector exposes per-data-store projection health for those targets but
does not resolve physical connector identity, reconcile Kafka Connect, or introduce a
different projector mode.

All CDC readiness remains observational. Missing targets, source drift, projection
failure, and connector failure do not alter `IDataStoreSelection` or block API requests.
In particular, projection failure cannot block API deletion because the connector derives
domain tombstones from `dms.Document` independently of cache health.

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
  readiness report when `dms.DocumentCache` cannot supply the DMS-1245 upsert contract,
  but this never changes API behavior.
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
projection for downstream indexing and CDC upserts. Calling it only a cache obscures the
projector-health and payload responsibilities.

### Use a single enablement flag

Rejected. Projection, read acceleration, and connector registration are independently
useful capabilities. Separate settings preserve those boundaries even though the
projector itself has only one asynchronous enabled mode.
