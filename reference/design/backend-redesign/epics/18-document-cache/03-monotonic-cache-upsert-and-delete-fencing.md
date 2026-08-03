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
- Latch current cache-ahead state after reclassification; `C > S` takes precedence
  over absent or mismatched work because only cache-ahead may represent unsafe
  projected or published state. Leave non-cache-ahead work anomalies pending for
  explicit conditional scrub/rebuild repair without setting the latch.
- Commit cache write and matching work deletion together. Cover equal-version fast
  acknowledgement, enqueue-versus-ack races, newer-work preservation, delete/post-delete
  fencing, crash windows, and duplicate writers.
- Hold the shared lifecycle-state lock through commit; obey the exclusive `Resetting`
  fence and provider-equivalent lock order. Measure same-document canonical-writer wait
  and retry complete canonical transactions after enqueue-related deadlock/serialization
  failures.
- Deliver and register the shared writer component so 18-04 hosted-projector and 18-05
  direct-fill read-path integrations consume it rather than creating separate cache
  writers.
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
  - exhausted retry budget or caller-aborted provider concurrency retry surfaced to the
    caller.
- When reporting `W` absent with cache absent/behind, distinguish ordinary `Tracking`
  missing-work anomalies from rows that may still be unseeded during an incomplete
  `Rebuilding` baseline. The writer still performs no cache DML, no acknowledgement, and
  no repair; rebuild-page seeding and repair remain owned by 18-04.
- Direct fill uses the same writer result model. The later 18-05 read-path wrapper remains
  best effort: it records bounded diagnostics and metrics, handles writer failures, and
  returns the relational response path's result unchanged.

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
   - `W != S` with no cache-ahead relationship, or cache absent/behind with `W`
     absent: leave the anomaly pending for explicit scrub or rebuild-page repair and
     do not set the cache-ahead latch.
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
- Do not set the latch for stale candidates, missing source rows, duplicate writers,
  lifecycle fences, or work ahead/behind/missing-work observations when `C <= S` or
  cache is absent. Absent or mismatched work does not suppress the latch when the
  same current recheck still observes `C > S`.
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
- Reuse `DeadlockRetry` settings and semantics without coupling the writer to API handler
  result types or request-pipeline execution. The retry boundary is the writer's complete
  provider transaction attempt.
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
  direct-fill result propagation.
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
  `C > S`, regardless of `W`, sets the latch. A canonical commit between classification
  and conditional DML cannot erase or acknowledge its newer work.
- Performance evidence compares the required projector and direct-fill workload modes.

## Not Assigned to This Story

- Queue paging and administrative recovery orchestration are assigned to 18-04.
- Consumer ordering behavior is assigned to the Kafka contract and E19 verification.

## Clarifying Questions and Answers

### Questions 1

1. Should the cache writer own the provider retry loop for deadlock, serialization, lock-timeout, and equivalent transient failures, including retry budget/backoff, or should it surface a retryable outcome/exception and rely on the 18-04/18-05 caller to replay the complete cache-write/acknowledgement transaction?
2. When the healthy pending candidate path loses a race because another writer makes the cache current before final acknowledgement, should this attempt still run the final conditional work delete and return an already-current/work-acknowledged outcome if it succeeds, or should it roll back and return a duplicate/racing-writer-lost outcome without acknowledging work?
3. In lifecycle `Rebuilding`, when the writer observes `W` absent with cache absent or behind, should it always report a possible-unseeded-baseline outcome because v1 has no durable baseline cursor, or is the caller expected to pass enough baseline/page context for the writer to distinguish unseeded rows from true missing-work anomalies?
4. What exact production-safe test hook points are expected for the close-connection/crash evidence: after state-lock/classification, after cache DML before acknowledgement, after acknowledgement before commit, before cache-ahead latch commit, or some smaller set?
5. For the same-document canonical-writer/projector wait histogram, should 18-03 measure only writer-side database wait time around conditional cache/work DML, or does the story also require instrumentation in the canonical write/enqueue path to observe writers blocked by acknowledgement transactions?

### Answers 1

1. The shared cache writer should own a bounded provider retry loop for deadlock, serialization, lock-timeout, and equivalent transient failures. Each retry must replay the complete cache-write/conditional-acknowledgement transaction, including lifecycle lock, current `S/C/W` classification, cache DML, acknowledgement, and cache-ahead reclassification when applicable. The caller's retry/backoff policy should see only cancellation, non-transient failures, or an exhausted writer retry budget surfaced as a retryable writer outcome/exception. "Provider concurrency retry surfaced to the caller" means exhausted-budget or caller-aborted cases, not the first transient failure.
2. If the healthy pending path discovers that another writer made the cache current before this attempt's final acknowledgement, this attempt should still run the final conditional work delete. When the acknowledgement predicate observes current durable `S = C = W` and deletes the matching work row, return the already-current/work-acknowledged outcome. Return duplicate/racing-writer-lost only when no cache DML from this attempt remains committed and the final acknowledgement cannot prove and delete matching current work, such as because the work row was already deleted, advanced, or the source/cache relationship changed.
3. In `Rebuilding`, report `W` absent with cache absent or behind as possible unseeded baseline whenever the writer lacks a matching work row. Do not pass baseline/page cursor context into the writer in v1. The writer should perform no cache DML, acknowledgement, latch mutation, or work repair; 18-04 baseline seeding and bounded page repair remain responsible for eventually inserting or repairing work. The same relationship in `Tracking` is a missing-work anomaly for explicit scrub repair.
4. Add a production-safe, test-disabled-by-default transaction fault-injection observer with these named hook points: after main state lock and `S/C/W` classification before cache DML, after successful cache DML before acknowledgement, after successful acknowledgement before commit, and after cache-ahead latch update before the incident transaction commits. Closing the provider connection or forcing rollback at those points should prove no partial cache/acknowledgement commit and no partial latch commit. Do not add after-commit hooks or process-local durable latches as correctness evidence.
5. 18-03 should measure both sides needed for same-document enqueue/acknowledgement contention evidence. The writer component owns histograms around its cache DML and acknowledgement statements, including time spent waiting on a concurrent canonical writer's enqueue transaction. The story should also add or wire minimal canonical write/enqueue-path instrumentation so canonical writers blocked by an acknowledgement transaction contribute to the same sanitized same-document contention metric family. Labels remain bounded provider/target/purpose/outcome labels and must not include document identifiers.

### Questions 2

1. In the cache-ahead incident transaction, should the writer revalidate lifecycle `Tracking` or `Rebuilding` and a clear `CacheAheadRecoveryRequired` latch before setting the latch, returning a lifecycle/latch-fenced outcome if an administrative transition or another incident wins between the main transaction and the incident recheck?
2. For the final acknowledgement predicate, should the work delete bind to the content version proven by this attempt's classification or candidate path, in addition to requiring current source/cache/work equality, so an `N` attempt cannot delete newly advanced `N+1` work if canonical state advances between classification/cache DML and acknowledgement?
3. When conditional cache DML selects a source row but a concurrent canonical delete causes a provider FK or cache-UUID validation failure before commit, should the writer classify that as a retryable delete race that replays the complete writer transaction, as source-missing/deleted after rollback, or as a non-transient provider exception?
4. What is the exact boundary between stale-candidate and invariant outcomes for candidate mismatches: is any `ContentVersion != S` stale suppression, while matching-version mismatches in `DocumentUuid`, `ProjectName`, `ResourceName`, or `ResourceVersion` are deterministic invariant/target failures?
5. Does 18-03 own adding or wiring canonical write/enqueue retry behavior for enqueue-related deadlock, serialization, and lock-timeout cases, or only the cache-writer retry loop plus evidence that existing canonical write retry covers those cases?
6. Should the cache writer's bounded retry budget and backoff be a new `DocumentCache` setting, a reuse of an existing provider transaction retry policy, or a fixed internal policy with tested defaults?

### Answers 2

1. Yes. The cache-ahead incident transaction must acquire the state row exclusively, verify lifecycle is still `Tracking` or `Rebuilding`, verify `CacheAheadRecoveryRequired` is still false, and then re-run the current `S/C/W` classification before setting the latch. If the lifecycle changed, the state row is missing/unreadable, or another incident already set the latch, return the lifecycle/latch-fenced outcome with no cache DML, no acknowledgement, and no additional latch mutation.
2. Yes. The writer should carry an `expectedContentVersion` from the transaction's current classification/action path and bind the final acknowledgement delete to it. The delete must require current source, cache, and work all still equal that expected version; it must not delete merely because the three current rows are equal to each other. If an `N+1` canonical/cache/work state becomes current before acknowledgement, the `N` attempt's delete affects zero rows and the writer handles that as the existing race-lost/retryable path, rolling back any cache DML from the attempt.
3. Treat provider FK and cache-UUID validation failures caused by a concurrent canonical delete as retryable delete races. Roll back and replay the complete cache-write/acknowledgement transaction under the writer retry policy. The replay will normally classify the row as source-missing/deleted or as already handled by cascade; only an exhausted retry budget should surface as a retryable writer failure. Do not classify the first FK/UUID failure as a deterministic invariant failure and do not manually delete cache.
4. Use `ContentVersion` as the stale-candidate boundary after the `DocumentId` programming-error check. If the candidate `ContentVersion` differs from current `S`, suppress it as stale and leave work pending when appropriate. If the candidate version equals current `S` but `DocumentUuid`, `ProjectName`, `ResourceName`, or `ResourceVersion` does not match the current durable source/target context, return a deterministic invariant/target failure, perform no cache DML or acknowledgement, and leave work visible for retry or operator diagnosis.
5. 18-03 owns making enqueue-related canonical write retries true for this feature boundary. Reuse the existing full repository-call deadlock retry pipeline where it already wraps canonical write transactions; add or wire only the missing classification/instrumentation needed so enqueue-related deadlock, serialization, and configured lock-timeout results replay the complete canonical transaction. If the existing pipeline already covers a case, 18-03 should add focused evidence for that case rather than adding a second retry layer. Never retry only the enqueue trigger or work-table upsert after canonical commit.
6. Reuse the existing `DeadlockRetry` configuration and retry semantics: configured budget, exponential backoff, jitter, retry-disabled behavior, transient-provider classification, and sanitized retry logging. Do not route the cache writer through the API handler resilience pipeline or force cache-writer results into HTTP handler result unions just to reuse retry. 18-03 should add or reuse a small retry adapter/policy factory that consumes the same `DeadlockRetry` settings and executes the complete cache-write/conditional-acknowledgement transaction delegate on each retry. The cache writer should expose cache-writer-specific exhausted-budget, caller-aborted, and non-transient outcomes. Tests should prove each retry replays lifecycle lock, `S/C/W` classification, cache DML, acknowledgement, and cache-ahead reclassification when applicable.

### Questions 3

1. Does 18-03 own adding production call sites in projector and direct-fill paths, or should it deliver only the shared writer, adapters, retry/metrics hooks, and focused tests while 18-04 and 18-05 own the actual hosted-projector and read-path integrations?
2. Should the writer's current-state classification and conditional cache DML join and bind current `ResourceKey` metadata (`ProjectName`, `ResourceName`, `ResourceVersion`) as source predicates alongside `DocumentUuid` and `ContentVersion`, or may it trust matching-version candidate metadata produced by 18-02?
3. When the writer returns a deterministic invariant/target failure for matching-version candidate metadata mismatch, should callers treat it as target-fatal and pause projection eligibility, or as a per-document poison/anomaly that leaves work visible under the normal projector backoff path?
4. For direct fill, should the shared writer itself swallow exhausted retry, invariant, and non-transient failures when caller purpose is `DirectFill`, or should it surface the same outcomes/exceptions as durable projection and leave best-effort swallowing plus relational-response preservation to the 18-05 read-path integration?

### Answers 3

1. 18-03 should deliver the shared writer, provider transaction adapters, retry adapter, metrics/test hooks, service registration, and focused unit/provider evidence. It should not add the production hosted-projector or direct-fill read-path call sites. 18-04 owns the projector call site when it implements the reconciliation loop, and 18-05 owns the direct-fill call site when it implements cache-backed reads. The shared-component requirement means those later production integrations must consume this component rather than creating another writer.
2. Yes. The writer's classification should join current `dms.Document` to the durable `dms.ResourceKey` row and carry the current `DocumentUuid`, `ContentVersion`, `ProjectName`, `ResourceName`, and `ResourceVersion` tuple. Conditional cache DML for a candidate write must repeat those source/work/candidate predicates, including the resource metadata, and may write only when the candidate exactly matches that current tuple. Do not trust matching-version candidate metadata by itself, and do not repair a metadata mismatch by substituting source metadata over a mismatched candidate body or `StreamEtag`.
3. Treat matching-version candidate metadata mismatch as a deterministic target/projection invariant failure, not as an ordinary per-document poison item. The writer should perform no cache DML, no acknowledgement, no latch mutation, and leave work visible. The 18-04 projector supervisor should pause projection eligibility for that target execution context and surface bounded sanitized diagnostics until the target is refreshed, restarted, or administratively corrected; it should not continue normal per-document backoff on the same invariant.
4. The shared writer should surface the same typed outcomes and deterministic/non-transient exceptions for `DirectFill` as it does for durable projection. It may emit the shared sanitized writer metrics, but it should not swallow failures based on caller purpose. 18-05 owns the best-effort direct-fill wrapper: catch or translate exhausted retry, invariant, and non-transient writer failures, record bounded diagnostics/metrics, preserve the relational response unchanged, and route any target-fatal diagnostic through the same target-health path used by projection.

### Questions 4

1. After the main transaction detects current `C > S` and exits with no cache DML or acknowledgement, how should caller cancellation, shutdown, or direct-fill timeout be handled before or during the cache-ahead incident transaction: must the writer make a bounded best-effort attempt to set the latch, or may it return caller-aborted with the latch still clear?
2. Since 18-03 does not add the production hosted-projector or direct-fill read-path call sites, what performance evidence does this story own for projector versus direct-fill workload modes: component-level writer evidence using caller purpose and representative contention, or end-to-end workload evidence deferred to 18-04, 18-05, or 18-07?

### Answers 4

1. Once the main transaction has observed current `C > S`, treat the incident recheck/latch flow as a bounded safety continuation of the writer operation. The writer should make one short best-effort incident transaction using its own incident timeout/provider command timeout rather than ordinary work-item cancellation or direct-fill timeout. It still performs no cache DML and no acknowledgement. During graceful shutdown, an incident transaction that has started may commit or roll back within its command timeout; if shutdown or provider cancellation prevents starting or completing that bounded incident transaction, return a caller-aborted cache-ahead-unconfirmed outcome, emit sanitized diagnostics/metrics, and leave the latch state to durable reality. Do not return a normal caller-aborted outcome with the latch clear merely because the original projector item or direct-fill caller timed out after `C > S` was detected.
2. 18-03 owns component-level provider performance evidence for the shared writer in both `DurableWorkProjection` and `DirectFill` caller-purpose modes. The evidence should invoke the writer directly with representative current-state classifications, candidate and no-candidate paths, duplicate writers, canonical enqueue/acknowledgement contention, retries, and cache-ahead incident handling, and report the story-owned transaction, cache DML, acknowledgement, retry, outcome, and same-document wait metrics with bounded labels. End-to-end hosted-projector queue-drain throughput, direct-fill request latency, cache-read fallback behavior, and full operational workload qualification remain deferred to 18-04, 18-05, and 18-07 after their production call sites exist.
