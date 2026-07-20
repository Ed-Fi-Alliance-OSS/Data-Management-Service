---
status: proposed
date: 2026-07-20
jira: DMS-1246
related:
  - DMS-1245
---

# Decision Record: DocumentCache Reconciliation Health and DDL Support

## Decision

`dms.DocumentCache` remains a lean projected-state table. V1 does not add
`dms.DocumentCacheProjectionState`, `dms.DocumentCacheProjectionFailure`, or any other
durable projector workflow table.

The current difference between `dms.Document` and `dms.DocumentCache` is both the work
inventory and the source of projection health. Failures remain observable as current
missing/stale rows plus structured process telemetry. In `Projector:Mode = Async`,
projection degradation does not break normal API correctness. When Kafka CDC is enabled,
DMS-1245 combines the current projection-completeness observation with connector and
source readiness without requiring a persisted backfill epoch.

## Health and Completeness

For each `(tenant key, DataStoreId)` execution context, use a provider-equivalent query
over the current source and cache rows to report at least:

- total mismatch count,
- missing-cache-row count,
- version-mismatched-row count,
- oldest mismatch source timestamp and age, derived from
  `dms.Document.ContentLastModifiedAt`,
- optional counts by project/resource where operationally safe.

Freshness and mismatch counts compare `ContentVersion` alone. `LastModifiedAt` remains
payload metadata and may supply the source timestamp used for mismatch-age diagnostics;
it is not a second completeness condition. Health must also report projector mode,
whether `dms.DocumentCache` exists, and whether the in-process loop is running.
Process-local telemetry may report the last scan time and duration, last successful
upsert, and last observed error, but those values are diagnostic and do not prove
database completeness.

The mismatch query is authoritative. `LastScannedContentVersion`,
`LastProjectedContentVersion`, and a last-success timestamp must not be used as
completeness signals because a higher version can succeed while a lower version remains
missing.

Projection health uses configurable mismatch-count and oldest-mismatch-age thresholds.
This lets a brief asynchronous lag remain healthy while sustained or growing lag becomes
visible. The exact thresholds are operational policy, not stored workflow state.

Projection completeness, when required for initial CDC readiness, is stricter: the
current mismatch count must be zero. DMS-1245 owns the additional connector
snapshot/catch-up and source-position checks needed before end-to-end CDC is advertised
as ready.

Registration prerequisites are the subset available before the cache is complete:

- `Projector:Mode = Async`,
- `dms.Document` and `dms.DocumentCache` are provisioned,
- the reconciliation loop can be started for the explicit data-store context,
- the guarded cache upsert is available.

CDC readiness and projection health are observational. They never gate normal routing,
reads, or mutations. Cache-backed reads fall back to relational reconstitution, and
authoritative deletes come independently from `dms.Document`.

## Failure and Retry Observability

Projection attempts use bounded in-memory exponential backoff with jitter. Backoff is
keyed by opaque data-store identity, `DocumentId`, and current `ContentVersion`; entries
are removed when the document changes version, is deleted, or becomes fresh. Skipping a
candidate whose retry time has not arrived prevents one persistent error from hot-looping
or starving the rest of the batch.

Each failed attempt emits a structured, sanitized log and metrics tagged with a stable
failure category such as reconstitution, metadata invariant, database write, or
unexpected. Logs do not include `DocumentJson`, request payloads, connection strings, or
tenant display names.

There is no retry budget, dead-letter transition, persisted attempt count, requeue API,
or manual resolution state in v1. A current missing/stale row remains visible in mismatch
count and age for as long as it needs work. After the underlying data, mapping, or service
problem is fixed, the ordinary reconciliation loop retries and clears the mismatch.

## DDL and Index Support

The projected row contract remains:

- `DocumentId`
- `DocumentUuid`
- `ProjectName`
- `ResourceName`
- `ResourceVersion`
- `ContentVersion`
- `LastModifiedAt`
- `DocumentJson`
- `ComputedAt`

No projector state or failure tables are provisioned. Supporting DDL is limited to the
projection and measured access paths:

- keep `dms.DocumentCache(DocumentId)` as the primary key and foreign key with
  `ON DELETE CASCADE`,
- keep `dms.DocumentCache.DocumentUuid` unique for connector upsert keys,
- keep provider-specific JSON object constraints on `DocumentJson`,
- add `dms.Document(ContentVersion, DocumentId)` when needed for ordered bounded scans,
  with provider query-plan coverage,
- add no additional diagnostic index until mismatch-query measurements on realistic data
  demonstrate one is necessary.

PostgreSQL and SQL Server implementations expose equivalent logical behavior even when
the reconciliation and health query plans differ.

## Telemetry

Emit structured logs and metrics for:

- reconciliation scans, candidate counts, attempts, successes, failures, and durations,
- in-memory retry deferrals and backoff duration,
- guarded stale-write skips,
- mismatch count and oldest mismatch age,
- read cache hits, misses, stale misses, and fallback reconstitution.

Metrics are tagged by provider, projector mode, project/resource where safe, and failure
kind, plus an opaque data-store identity where cardinality policy permits. They do not
include document bodies or raw student data.

## Consequences

- Operators see actual incomplete current projections rather than inferred progress.
- Restart loses only backoff timing, not work; the mismatch query rediscovers every
  outstanding item.
- The CDC row contract and DDL remain small, with no operational workflow tables to
  provision, migrate, capture, repair, or clean up.
- A persistent error stays visible through mismatch age and repeated bounded failure
  telemetry until its cause is fixed.

## Alternatives Considered

### Persist projection progress and failure tables

Rejected for v1. They duplicate current mismatch state, require lifecycle and repair
semantics, and still cannot establish completeness from a maximum projected version.
Persistent workflow state may be added later only for a measured operational need.

### Store retry state on `dms.DocumentCache`

Rejected. Missing rows have nowhere to store it, and operational fields would become part
of the captured CDC row contract.

### Fail normal API reads when projection fails

Rejected. Normal API correctness does not depend on `dms.DocumentCache`; reads can fall
back to relational reconstitution.

### Make CDC readiness depend only on connector status

Rejected. A running connector cannot compensate for current documents whose cache rows
are missing or stale. The exact mismatch count supplies that projection signal.
