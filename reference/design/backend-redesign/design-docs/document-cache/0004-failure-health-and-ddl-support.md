---
status: proposed
date: 2026-07-07
jira: DMS-1246
related:
  - DMS-1245
---

# Decision Record: DocumentCache Failure, Health, and DDL Support

## Decision

`dms.DocumentCache` remains a lean projected-state table. Projector progress, failure,
and readiness are tracked in companion state rather than by adding lifecycle columns to
every cache row.

Projection failures must be retryable, observable, and actionable. In
`Projector:Mode = Async`, unresolved failures degrade cache/indexing health but do not
break normal API correctness. In `Projector:Mode = CdcRequired`, unresolved current
projection failures, incomplete bounded initial backfill, missing delete-source support,
or lag above the completed backfill target make CDC not ready.

## Required Projector State

The implementation should add provider-equivalent companion objects for projector state
and failures.

Logical `dms.DocumentCacheProjectionState` fields:

| Field | Purpose |
| --- | --- |
| `ProjectionName` | Stable key for the default DocumentCache projection. |
| `Mode` | Last observed projector mode: `Async` or `CdcRequired`. |
| `BackfillEpochId` | Stable identifier for the current/last bounded backfill or rebuild epoch. |
| `BackfillStatus` | `NotStarted`, `Running`, `Complete`, or `Failed`. |
| `BackfillTargetContentVersion` | `max(dms.Document.ContentVersion)` captured when the current backfill epoch started. |
| `BackfillStartedAt` | UTC timestamp for the current/last backfill start. |
| `BackfillCompletedAt` | UTC timestamp for the last completed backfill. |
| `LastScannedContentVersion` | Highest `dms.Document.ContentVersion` scanned by backfill/projector. |
| `LastProjectedContentVersion` | Highest `ContentVersion` successfully materialized by the projector. |
| `LastSuccessfulProjectionAt` | UTC timestamp of the last successful projection write. |
| `LastFailureAt` | UTC timestamp of the last projection failure. |
| `FailureCount` | Count of currently unresolved projection failures. |

Logical `dms.DocumentCacheProjectionFailure` fields:

| Field | Purpose |
| --- | --- |
| `DocumentId` | Internal document key for retry while the document still exists. |
| `DocumentUuid` | Stable diagnostic key; retained even if the document is later deleted. |
| `ResourceKeyId` | Resource type for diagnostics and grouping. |
| `TargetContentVersion` | Projection work item version that failed. |
| `FailureKind` | Stable category such as `Reconstitution`, `MetadataInvariant`, `DatabaseWrite`, or `Unexpected`. |
| `ErrorMessage` | Sanitized operator-facing failure summary. |
| `AttemptCount` | Number of attempts for this failure key. |
| `FirstFailedAt` | UTC timestamp for first failure. |
| `LastFailedAt` | UTC timestamp for most recent failure. |
| `NextRetryAt` | UTC timestamp after which automatic retry may run. |
| `ResolvedAt` | UTC timestamp when retry/manual repair resolved the failure. |

Failure rows should avoid storing full `DocumentJson` or request payloads. Logs may carry
correlation identifiers, but error text must be sanitized because document data can be
sensitive.

## Retry and Dead Letter Behavior

Projection work should retry transient failures with bounded backoff and jitter. The
projector must avoid hot-looping on a permanently failing document.

A failure becomes dead-lettered when it is classified as non-retryable or exceeds the
configured retry budget. Dead-lettered work remains visible until an operator or repair
process marks it resolved or requeues it.

For CDC readiness, the blocking unit is an unresolved current projection failure: a
failure for a document whose current `dms.Document` stamp still requires a fresh
`dms.DocumentCache` row. Dead-lettered failures are always readiness-blocking. A
transient retry failure is readiness-blocking while it leaves the current document
missing or stale, and it stops blocking when a newer successful projection supersedes
or resolves it.

Retry behavior must preserve stale-write fencing:

- retrying an old `TargetContentVersion` must not overwrite a newer cache row,
- retrying after delete must not recreate a cache row,
- successful projection of a newer version may resolve older failures for the same
  `DocumentId`.

## Health and Readiness Signals

DocumentCache health should report:

- projector mode,
- whether the table and companion state exist,
- initial backfill status,
- current backfill epoch id and target content version,
- current projector lag by `ContentVersion` and by age of the oldest missing/stale row,
- unresolved failure count,
- oldest unresolved failure age,
- last successful projection timestamp,
- last failure timestamp and failure kind,
- whether CDC-mode pre-delete materialization support is available for the selected
  provider.

CDC readiness requires all of the following:

- `Projector:Mode = CdcRequired`,
- `dms.DocumentCache` and required companion objects are provisioned,
- the bounded initial backfill epoch is complete,
- stale-write fencing is active,
- pre-delete source-row materialization is supported and provider-verified,
- no unresolved current projection failures exist, including dead-lettered failures,
- projector lag above the completed backfill target is within the configured threshold,
- Kafka connector/database CDC prerequisites from DMS-1245 are satisfied.

Non-CDC cache-backed reads may remain available when CDC readiness is false because they
fall back to relational reconstitution on misses and stale rows.

## DDL and Index Support

The existing `dms.DocumentCache` columns remain the projected row contract:

- `DocumentId`
- `DocumentUuid`
- `ProjectName`
- `ResourceName`
- `ResourceVersion`
- `ContentVersion`
- `LastModifiedAt`
- `DocumentJson`
- `ComputedAt`

Additional DDL support should focus on projector scans and diagnostics:

- add an index on `dms.Document(ContentVersion, DocumentId)` so the projector can scan
  candidate work in representation-version order,
- keep `dms.DocumentCache.DocumentUuid` unique so connectors can key deletes by the
  public document id,
- keep `dms.DocumentCache(DocumentId)` as the primary key and FK with
  `ON DELETE CASCADE`,
- consider an index on `dms.DocumentCache(ContentVersion, DocumentId)` only if health
  queries need to locate stale cache rows without joining from `dms.Document`,
- keep provider-specific JSON object constraints on `DocumentJson`.

PostgreSQL and SQL Server implementations must expose equivalent logical behavior even
when the physical DDL differs.

## Telemetry

The implementation should emit structured logs and metrics for:

- projection attempts, successes, retries, and failures,
- backfill epoch start, target capture, progress, completion, and failure,
- stale-write skips,
- read cache hits, misses, stale misses, and fallback reconstitution,
- CDC pre-delete materialization attempts, successes, and failures,
- projector lag by version and age.

Metrics should be tagged by provider, projector mode, project/resource where safe, and
failure kind. They should not include document bodies or raw student data.

## Consequences

- Operators can distinguish an optional read-cache degradation from an unsupported CDC
  state.
- CDC connector registration can fail with actionable diagnostics instead of publishing a
  stream that later loses tombstones.
- Keeping projector state out of `dms.DocumentCache` prevents operational retry metadata
  from leaking into the CDC row contract.

## Alternatives Considered

### Store retry state on `dms.DocumentCache`

Rejected. Missing and deleted cache rows also need retry/dead-letter visibility, and
operational retry fields should not become part of the captured CDC row contract.

### Fail normal API reads when projection fails

Rejected. Normal API correctness does not depend on `dms.DocumentCache`; reads can fall
back to relational reconstitution.

### Make CDC readiness depend only on connector status

Rejected. A running connector cannot compensate for missing/stale source rows, incomplete
backfill, projector dead letters, or missing pre-delete materialization support.
