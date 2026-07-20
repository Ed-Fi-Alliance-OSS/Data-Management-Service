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
```

Mode semantics:

| Setting | Meaning |
| --- | --- |
| `Projector:Mode = Disabled` | DMS does not write `dms.DocumentCache`. The table may be absent unless provisioned for another purpose. |
| `Projector:Mode = Async` | DMS maintains `dms.DocumentCache` asynchronously for read acceleration, indexing, or diagnostics. Rows may be missing or stale. |
| `Projector:Mode = CdcRequired` | DMS maintains `dms.DocumentCache` with CDC readiness, stale-write fencing, bounded initial backfill, visible health, and pre-delete source-row guarantees. |
| `ReadAcceleration:Enabled = true` | GET/query response assembly may read fresh cache rows, but must fall back to relational reconstitution for misses or stale rows. |
| `KafkaCdc:Enabled = true` | Requires `Projector:Mode = CdcRequired` and a provisioned `dms.DocumentCache`; source prerequisites must pass before connector registration, and completed source/connector readiness must pass before CDC is advertised as supported. |

Indexing or external integration use cases that do not need Kafka CDC should use
`Projector:Mode = Async`. They may leave `ReadAcceleration:Enabled = false` if the API
should continue to assemble responses directly from relational tables.

Kafka UI, Kafka infrastructure startup, and connector registration are separate concerns.
Starting Kafka UI must not imply `KafkaCdc:Enabled`.

### Scope Across Data Stores

The v1 settings are process-wide defaults, not per-data-store CMS options. Each data store
loaded from the fixed startup configuration with a usable connection string is therefore
a projection target when `Projector:Mode` is `Async` or `CdcRequired`. With
`KafkaCdc:Enabled = true`, every such data store must run in `CdcRequired` mode and expose
its own readiness result; v1 does not silently enable CDC for only the data store selected
by the most recent HTTP request.

Connector registration remains per data store. A failed or incomplete projector makes
that data store's CDC readiness false without making normal API correctness for other data
stores depend on it. Deployment health may also expose an aggregate result that is false
when any configured CDC data store is not ready. A future CMS-backed per-data-store opt-in
may override the process default, but it is not part of v1.

V1 does not discover CDC targets dynamically after startup. Adding, removing, or changing
a target requires an explicit configuration change and deployment/restart.

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
