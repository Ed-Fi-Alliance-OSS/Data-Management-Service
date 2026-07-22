---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add the Asynchronous DocumentCache Reconciliation Loop

## Design References

- [Freshness and reconciliation](../../../cdc-streaming.md#freshness-and-reconciliation)
- [Bounded in-process execution policy](../../../cdc-streaming.md#bounded-in-process-execution-policy)
- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement the hosted, per-data-store incremental discovery and full-audit reconciler,
including target-scoped failure backoff and database rediscovery without a document-scoped
retry map.

## Dependencies

- Depends on the 18-00 schema, 18-01 target configuration, 18-02 materializer, and 18-03
  monotonic upsert.
- Unblocks 18-05 and supplies projection signals to 18-06 and CDC stories 19-00, 19-06,
  and 19-07.

## Deliverables

1. Add supervisor lifecycle and isolated non-HTTP execution scopes for explicit targets,
   including bounded re-resolution of configured targets that are not yet available.
2. Implement provider-equivalent bounded incremental keyset scans using a disposable
   process-local `(ContentVersion, DocumentId)` cursor. Each source/cache pair comes from
   one database statement rather than separately read source and cache commands. SQL Server
   pages explicitly use `READ COMMITTED`, use no hint that restores locking reads, and run
   only after 18-01 validates RCSI on the same open target connection.
3. Before a startup or restart audit, capture the maximum current source key as the
   initial incremental boundary. After the audit, start the cursor at exactly that
   pre-audit boundary rather than a later maximum, so commits after the audit remain
   visible to incremental scanning.
4. Implement startup, periodic, and rebuild-triggered full anti-join audits,
   including bounded audit-local paging and an exact finishing aggregate that separates
   missing, cache-behind, and cache-ahead rows. Apply the same one-statement source/cache
   observation and SQL Server RCSI validation to every audit page and finishing aggregate.
5. Invoke the shared materializer and monotonic upsert for missing and cache-behind
   candidates. After a failed incremental page, mark the target repair-required, apply
   target-scoped capped exponential backoff with jitter, and run a coalesced immediate full
   audit to rediscover the database difference. During an audit, advance beyond failed
   candidates and apply the backoff before the next repair pass. Retain no failed document
   or version identity after its bounded page is drained, and clear the repair-required
   observation only after an exact-zero finishing audit. When either lane observes a
   cache-ahead row, atomically set `DocumentCacheState.CacheAheadRecoveryRequired`; do not
   materialize or repair the row. Once classified, do not reclassify it if the source
   advances before the latch commit, and do not report the observation before that commit.
   Pause all projector writes for the latched target. Treat a missing, malformed, or
   unwritable state singleton as fail-closed. If the SQL Server RCSI prerequisite becomes
   false or unreadable, stop before classification, set no cache-ahead latch, discard the
   page observation, and return the target to prerequisite retry.
6. Implement one target-scoped administrative recovery operation for a projection proven
   internal-only. Require explicit caller confirmation of that deployment-owned proof, take
   the exclusive singleton-state lock, require a set latch, clear the entire cache and latch
   in one provider transaction, and request an immediate full audit after commit. Expose no
   latch-only reset. E19 owns containment when downstream observation is possible or
   uncertain; its downstream-state reset is deferred from v1.
7. Add graceful cancellation and sanitized incremental-scan, audit, target-backoff, and
   failure telemetry, and measure realistic plans for both providers.
8. Bind the configurable incremental interval, full-audit interval, page size,
   process-wide concurrent-target limit, and maximum audit age. Supply conservative,
   implementation-tuned defaults in supported appsettings and documentation.
9. Run one serialized loop per target, coalesce duplicate audit requests, bound every page,
   enforce fair process-wide target concurrency, and ensure health/readiness observation
   never starts or waits for an audit.

## Acceptance Evidence

- Provider and multi-data-store tests cover no targets, unresolved targets, late
  resolution, colliding local ids, population, create/update, restart, rebuild,
  transient/persistent failure, fairness, peer isolation, and multiple replicas.
- Concurrency tests cover source update during multi-result-set materialization through the
  final optimistic version check, source update after that check through the shared
  monotonic upsert, and deletion through the foreign-key delete fence.
- Query-plan tests prove ordinary high-version updates use indexed incremental discovery
  without a full relationship scan and that a full audit covers the relationship once
  rather than rescanning each repaired prefix.
- Completeness tests prove late lower-version commits and cache-row loss below the cursor
  are repaired by full audit, advancing past failures retains no document-scoped retry
  state and relies on full-audit database rediscovery, and no timestamp, epoch, `StreamEtag`
  comparison, or cursor/high-watermark becomes a second completeness predicate.
- Classification tests prove missing and cache-behind rows are repaired, while a
  cache-ahead row is not materialized, is not treated as repairable work, remains in the
  exact audit result, atomically latches the target, and pauses further cache writes. After
  the canonical source advances to exactly the cached version, including a synchronized advance
  between classification and latch commit, restart and zero-audit tests prove the latch
  remains set until proven-internal-only recovery; no test clears possibly published or
  uncertain state.
- SQL Server isolation tests prove every incremental page, audit page, and finishing
  aggregate obtains its source/cache pairs from one RCSI-backed `READ COMMITTED` statement.
  With RCSI disabled or unreadable, including after initial resolution, the next comparison
  stops before classification and cannot set the durable latch. A synchronized
  RCSI-enabled source advance followed by cache projection cannot produce a mixed
  cache-ahead observation; relational API work and eligible peer targets continue.
- Recovery tests prove the operation rejects a clear latch or missing internal-only
  confirmation, waits for in-flight shared latch locks, clears all cache rows before
  resetting the latch in the same transaction, rolls both changes back on failure, and
  requests an immediate full audit only after commit. No E18 test clears possibly published
  or uncertain state.
- A synchronized startup test commits a higher-key source update after the finishing
  audit observation but before incremental scanning begins and proves the update remains
  above the pre-audit boundary and is projected without waiting for the next full audit.
- Scheduling tests cover immediate startup/rebuild audits, interval eligibility,
  serialized per-target work, coalescing, bounded pages, fair process-wide concurrency,
  no document-scoped retry queue, capped target backoff, stale-audit readiness, and graceful
  cancellation. A systemic persistent failure affecting more documents than `PageSize`
  proves candidate memory remains bounded by active pages, page scans continue, database
  rediscovery finds every failed candidate, and readiness remains false until an exact-zero
  audit.
- Configuration tests reject invalid values, require maximum audit age to exceed the full
  audit interval, and pin the documented implementation defaults.

## Out of Scope

- Durable workflow state or per-document retry state. The singleton cache-ahead safety latch
  remains the only durable projector incident state.
- Connector status/source-position readiness.
- Discovery of unlisted CMS targets.
