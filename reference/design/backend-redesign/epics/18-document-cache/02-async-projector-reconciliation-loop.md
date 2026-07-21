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
including the bounded in-memory retry behavior defined by the authoritative design.

## Dependencies

- Depends on 18-00, 18-01, 18-04, and core source/cache DDL.
- Supplies projection signals to 18-05 and CDC stories 19-00, 19-06, and 19-07.

## Deliverables

1. Add supervisor lifecycle and isolated non-HTTP execution scopes for explicit targets,
   including bounded re-resolution of configured targets that are not yet available.
2. Implement provider-equivalent bounded incremental keyset scans using a disposable
   process-local `(ContentVersion, DocumentId)` cursor.
3. Before a startup or restart audit, capture the maximum current source key as the
   initial incremental boundary. After the audit, start the cursor at exactly that
   pre-audit boundary rather than a later maximum, so commits after the audit remain
   visible to incremental scanning.
4. Implement startup, periodic, and rebuild-triggered full anti-join audits,
   including bounded audit-local paging and an exact finishing aggregate that separates
   missing, cache-behind, and cache-ahead rows.
5. Update core DDL for both providers. Always provision `dms.DocumentCache`, rename its
   obsolete `Etag` column to the required non-null `StreamEtag`, and always provision the
   `dms.Document(ContentVersion, DocumentId)` discovery/audit index. Remove the obsolete
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt` and
   `UX_DocumentCache_DocumentUuid` indexes, keep compact `DocumentId` as the cache
   primary/foreign key, emit the provider-specific cache insert/update trigger that rejects
   a UUID mismatch with the canonical row, and add the always-provisioned singleton
   `dms.DataStoreIdentity` table with insert-if-absent random UUID initialization. Add the
   always-provisioned singleton `dms.DocumentCacheState` row with its durable cache-ahead
   recovery latch initially clear; provisioning reruns never reset the latch. Update DDL
   emitter, unit, DB-apply, and snapshot fixtures to match the revised column, constraint,
   identity, state, and access-path inventory. Keep provider publication/capture artifacts
   outside this ordinary DDL path.
6. Invoke the shared materializer and monotonic upsert with fair retry and idle polling for
   missing and cache-behind candidates. When either lane observes a cache-ahead row,
   atomically set `DocumentCacheState.CacheAheadRecoveryRequired`; do not materialize or
   retry the row. Once classified, do not reclassify it if the source advances before the
   latch commit, and do not report the observation before that commit. Pause all projector
   writes for the latched target. Treat a missing, malformed, or unwritable state singleton
   as fail-closed.
7. Implement one target-scoped administrative recovery operation that takes the exclusive
   singleton-state lock, requires a set latch, clears the entire cache and latch in one
   provider transaction, and requests an immediate full audit after commit. Expose no
   latch-only reset; downstream publication-path recovery remains E19-owned.
8. Add graceful cancellation and sanitized incremental-scan, audit, retry, and failure
   telemetry, and measure realistic plans for both providers.
9. Bind the configurable incremental interval, full-audit interval, page size,
   process-wide concurrent-target limit, and maximum audit age. Supply conservative,
   implementation-tuned defaults in supported appsettings and documentation.
10. Run one serialized loop per target, coalesce duplicate audit requests, bound every page,
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
- PostgreSQL and SQL Server DDL tests prove every emitted schema includes
  `dms.DataStoreIdentity`, singleton `dms.DocumentCacheState` initialized clear,
  `dms.DocumentCache.StreamEtag`, and
  `dms.Document(ContentVersion, DocumentId)`; preserves the cache `DocumentId` primary/FK;
  emits no obsolete `DocumentCache.Etag`; and excludes
  `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt`,
  `UX_DocumentCache_DocumentUuid`, and any new canonical
  `(DocumentId, DocumentUuid)` index.
- Provisioning rerun tests prove `dms.DataStoreIdentity.SourceIdentity` is generated once
  and retained, independently provisioned databases receive different values, and the
  emitted SQL remains deterministic.
- Provider DB-apply tests prove cache insert/update statements with the matching canonical
  UUID succeed, mismatches fail atomically through the validation trigger, no mismatched CDC
  row can commit, and ordinary canonical writes perform no cache-trigger work.
- Completeness tests prove late lower-version commits and cache-row loss below the cursor
  are repaired by full audit, advancing past failures retains bounded retry, and no
  timestamp, epoch, `StreamEtag` comparison, or cursor/high-watermark becomes a second
  completeness predicate.
- Classification tests prove missing and cache-behind rows are repaired, while a
  cache-ahead row is not materialized, does not enter the retry set, remains in the exact
  audit result, atomically latches the target, and pauses further cache writes. After the
  canonical source advances to exactly the cached version, including a synchronized advance
  between classification and latch commit, restart and zero-audit tests prove the latch
  remains set until explicit recovery.
- Recovery tests prove the operation rejects a clear latch, waits for in-flight shared
  latch locks, clears all cache rows before resetting the latch in the same transaction,
  rolls both changes back on failure, and requests an immediate full audit only after
  commit.
- A synchronized startup test commits a higher-key source update after the finishing
  audit observation but before incremental scanning begins and proves the update remains
  above the pre-audit boundary and is projected without waiting for the next full audit.
- Scheduling tests cover immediate startup/rebuild audits, interval eligibility,
  serialized per-target work, coalescing, bounded pages, fair process-wide concurrency,
  no unbounded candidate queue, stale-audit readiness, and graceful cancellation.
- Configuration tests reject invalid values, require maximum audit age to exceed the full
  audit interval, and pin the documented implementation defaults.

## Out of Scope

- Durable workflow/retry state beyond the singleton cache-ahead safety latch.
- Connector status/source-position readiness.
- Discovery of unlisted CMS targets.
