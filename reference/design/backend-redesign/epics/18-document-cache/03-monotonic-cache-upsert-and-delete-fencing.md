---
jira: DMS-1313
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Implement Monotonic Cache Upsert and Post-Delete Fencing

## Design References

- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md

The referenced design sections define cache-write ordering, concurrency, lifecycle, and
publication implications. This story is only the work package for implementing them.

## Outcome

Implement one provider-equivalent atomic cache-write/conditional-acknowledgement component
shared by queue processing and optional direct fill.

## Dependencies

- Depends on 18-00, 18-02, E10 representation stamps, and E11 delete behavior.
- Unblocks the 18-04 projector and 18-05 cache-backed read path.

## Implementation Scope

- Add the provider-specific cache DML and transaction adapters.
- Integrate the writer with the materializer result and projection safety state.
- Classify current source/cache/work in one statement; suppress stale candidates; let
  current durable `S = C = W` acknowledge work regardless of candidate version; and leave
  missing/behind cache work pending when the candidate is not current. Repeat the required
  source/work predicates on cache DML and source/cache/work predicates on acknowledgement;
  the classification result alone does not authorize later DML.
- Latch only current cache-ahead state after reclassification. Leave mismatched work
  pending for explicit conditional scrub/rebuild repair without setting the latch.
- Commit cache write and matching work deletion together. Cover equal-version fast
  acknowledgement, enqueue-versus-ack races, newer-work preservation, delete/post-delete
  fencing, crash windows, and duplicate writers.
- Hold the shared lifecycle-state lock through commit; obey the exclusive `Resetting`
  fence and provider-equivalent lock order. Measure same-document canonical-writer wait
  and retry complete canonical transactions after enqueue-related deadlock/serialization
  failures.
- Route projector and direct-fill writes through the shared component.
- Add sanitized outcome metrics and performance coverage.

## Resolved Cache Writer Scope and Runtime Contract

### Component Boundary

- Add one target-scoped cache-write/conditional-acknowledgement service with
  provider-specific transaction adapters. The exact C# names may follow local conventions,
  but the boundary is one shared component consumed by the asynchronous projector and the
  optional direct-fill path.
- The component consumes the resolved target context from 18-01 and the materialization
  candidate contract from 18-02. It does not resolve `DocumentCache:Targets`, validate
  target inventory, page durable work, hydrate documents, administer lifecycle, perform
  scrub/rebuild seeding, shape API responses, or shape Kafka messages.
- Do not add database stored procedures or generated programmable objects for this story.
  Use application-issued, parameterized provider SQL inside a short transaction. The
  schema, constraints, validation triggers, and enqueue triggers remain owned by 18-00.
- The component must not expose a cache-only write API. A cache insert/update that covers
  pending projection work and the matching work acknowledgement commit together, or both
  roll back.

### Inputs and Outcomes

- The write request carries the resolved target context, `DocumentId`, the optional
  selected `DocumentProjectionWork.RequiredContentVersion` captured by the caller, caller
  purpose (`DurableWorkProjection` or `DirectFill`), and an optional
  `DocumentCacheMaterializationCandidate`.
- A `null` candidate is valid and means "classify current durable state only." This is the
  equal-version acknowledgement fast path used before materialization and after baseline
  restart. It may acknowledge current `S = C = W`; otherwise it returns an outcome that
  tells the caller whether materialization is still useful.
- The selected work version is diagnostic/correlation context only. The writer never uses
  it as durable authorization to write cache or delete work; all decisions come from the
  current source/cache/work classification inside the transaction.
- Candidate `DocumentId` mismatch is a programming error. Candidate version, UUID, and
  resource metadata mismatches against current durable state are stale-candidate or
  invariant outcomes, not permission to repair cache.
- Use a small bounded result model with outcomes equivalent to:
  - already current and work acknowledged;
  - candidate written and work acknowledged;
  - current pending work still needs materialization;
  - lifecycle or latch fenced the attempt;
  - source row missing/deleted;
  - stale candidate suppressed;
  - work mismatch or missing-work anomaly left pending;
  - cache-ahead latch set or cache-ahead disappeared on recheck;
  - duplicate/racing writer lost with no durable change; and
  - retryable provider concurrency failure surfaced to the caller's retry policy.
- When reporting `W` absent with cache absent/behind, distinguish ordinary `Tracking`
  missing-work anomalies from rows that may still be unseeded during an incomplete
  `Rebuilding` baseline. The writer still performs no cache DML, no acknowledgement, and
  no repair; rebuild-page seeding and repair remain owned by 18-04.
- Direct fill uses the same result model but remains best effort. It records bounded
  diagnostics and metrics, then returns the relational response path's result unchanged.

### Main Transaction Shape

Each attempt uses one short provider transaction:

1. Acquire the provider-equivalent shared lock on
   `dms.DocumentCacheState(StateId = 1)` and hold it through commit:
   - PostgreSQL: `FOR SHARE`;
   - SQL Server: exact-key `HOLDLOCK` shared row lock.
2. Verify lifecycle is `Tracking` or `Rebuilding` and
   `CacheAheadRecoveryRequired` is false. `Disabled`, `Resetting`, a set latch, missing
   state, unreadable state, or an unsupported state performs no cache DML and no work
   acknowledgement.
3. Classify current `S`, `C`, and `W` for the requested `DocumentId` in one
   provider-consistent statement. Anchor the statement on the input document id and left
   join `dms.Document`, `dms.DocumentCache`, and `dms.DocumentProjectionWork`; do not rely
   on previously selected work-row values or the candidate.
4. Select the action from that classification:
   - no current source row: no cache DML and no acknowledgement;
   - `C > S`: no cache DML and no acknowledgement; leave the transaction and run the
     cache-ahead latch flow below;
   - `S = C = W`: conditionally delete the matching work row even when the supplied
     candidate is absent or stale;
   - `W = S`, cache absent or behind, and candidate `ContentVersion = S`: conditionally
     write the candidate cache row, then conditionally delete matching work;
   - `W = S`, cache absent or behind, and no candidate: return the
     needs-materialization outcome;
   - `W = S`, cache absent or behind, and candidate version differs from `S`: suppress the
     stale candidate and leave work pending;
   - `C = S` and `W` absent: no work remains, so no action; and
   - `W != S`, or cache absent/behind with `W` absent: leave the anomaly pending for
     explicit scrub or rebuild-page repair and do not set the cache-ahead latch.
5. Treat the final work acknowledgement as the commit gate for a cache write. If this
   attempt inserted or updated `DocumentCache` but the final conditional work delete
   affects zero rows, roll back the whole transaction and return a race-lost or retryable
   outcome. Do not leave a newly written cache row committed without acknowledging the
   matching work requirement.

### Conditional Provider DML

- PostgreSQL should use one application-issued `INSERT ... SELECT ... ON CONFLICT
  (DocumentId) DO UPDATE` cache statement for the healthy pending path. The `SELECT`
  repeats the current-source and current-work predicates, including `DocumentId`,
  canonical `DocumentUuid`, candidate `ContentVersion`, and
  `RequiredContentVersion = candidate.ContentVersion`. The conflict update runs only when
  the existing cache `ContentVersion` is lower than the excluded value and updates
  `ComputedAt` with the provider UTC timestamp.
- SQL Server should use application-issued `UPDATE` then `INSERT` statements for the
  single `DocumentId`, with an exact-key `UPDLOCK, HOLDLOCK` probe on
  `dms.DocumentCache` before the insert path to serialize duplicate absent-row writers.
  Do not use a generalized `MERGE` for the v1 writer. The update and insert `SELECT`
  sources repeat the same source/work/candidate predicates as PostgreSQL and update
  `ComputedAt` with `sysutcdatetime()` only when cache content changes.
- The final acknowledgement is a separate provider statement executed after cache DML:
  delete from `dms.DocumentProjectionWork` only where the row still has
  `RequiredContentVersion = S` and current `dms.Document` plus `dms.DocumentCache` both
  still exist at that same `ContentVersion`. Do not delete work by `DocumentId` alone.
- Equal-version acknowledgement performs no cache update and therefore does not refresh
  `ComputedAt`.
- A direct-fill call without current matching work does not insert or update cache. Missing
  work for a behind or absent cache is a scrub/rebuild repair concern, not a request-path
  repair.

### Cache-Ahead Latch Flow

- Suspected `C > S` exits the shared-lock transaction with no cache write and no
  acknowledgement. Then start a short incident transaction, acquire the same state row
  exclusively, re-run the current source/cache/work classification, and set
  `CacheAheadRecoveryRequired` only if `C > S` remains current.
- Do not set the latch for stale candidates, work ahead/behind, missing work, missing
  source rows, duplicate writers, or lifecycle fences.
- If the recheck no longer sees `C > S`, return a "cache-ahead disappeared" outcome and
  leave lifecycle, latch, cache, and work unchanged.

### Delete, Crash, and Retry Boundaries

- Materialization, backoff, cancellation, and external I/O hold no work-row lock. Queue
  paging passes selected values to the writer but does not pre-lock the work row.
- Post-delete races are fenced by the `DocumentCache` and `DocumentProjectionWork` foreign
  keys to `dms.Document`. A writer whose candidate was materialized before a delete must
  perform no manual cache delete; conditional source predicates and FK enforcement either
  suppress the cache write, roll back on a retryable race, or allow the delete cascade to
  remove obsolete cache/work.
- Close-connection/crash tests should prove the only visible states are "neither cache nor
  acknowledgement committed" or "both committed." Use test hooks around the provider
  transaction boundary rather than process-local durable latches.
- Retry deadlock, serialization, lock-timeout, and equivalent provider transient failures
  by replaying the complete cache-write/acknowledgement transaction. Never retry only the
  final acknowledgement statement.
- Deterministic 18-02 materializer invariant or target-mapping exceptions do not enter the
  writer as candidates. The caller leaves work visible and uses its normal failure/backoff
  handling.

### Metrics and Story-Owned Evidence

- Emit sanitized counters for the bounded outcomes above plus histograms for transaction
  duration, cache DML duration, acknowledgement duration, and same-document
  canonical-writer/projector wait. Labels may include provider, normalized target key,
  caller purpose, lifecycle, and outcome. Do not label metrics or logs with
  `DocumentUuid`, `DocumentJson`, request body content, authorization data, or unbounded
  resource labels.
- Unit tests should cover the pure classification/action mapping, result shaping, and
  direct-fill failure swallowing.
- PostgreSQL and SQL Server provider tests should cover the action table, conditional DML
  predicates, equal-version fast acknowledgement, stale-candidate suppression,
  duplicate-writer races, enqueue-versus-acknowledge races, delete races, lifecycle
  fencing, cache-ahead reclassification, rollback between cache DML and acknowledgement,
  and provider retry of the complete transaction.
- Cross-story tests in 18-04, 18-05, 18-06, and 18-07 may reuse this component, but this
  story owns the first focused evidence that its provider transactions are monotonic,
  atomic, duplicate-safe, and latch only current cache-ahead state.

## Acceptance Evidence

- PostgreSQL and SQL Server concurrency tests cover the writer interleavings and outcomes
  required by the referenced design sections.
- Provider tests cover integration with schema constraints, delete lifecycle, and safety
  state.
- Crash and concurrency tests prove no work-row lock spans materialization/backoff/I/O,
  cache and acknowledgement are atomic, stale candidates never write, and only current
  `C > S` sets the latch. A canonical commit between classification and conditional DML
  cannot erase or acknowledge its newer work.
- Performance evidence compares the required projector and direct-fill workload modes.

## Not Assigned to This Story

- Queue paging and administrative recovery orchestration are assigned to 18-04.
- Consumer ordering behavior is assigned to the Kafka contract and E19 verification.
