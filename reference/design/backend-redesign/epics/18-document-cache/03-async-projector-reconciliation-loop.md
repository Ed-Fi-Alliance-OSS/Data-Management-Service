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
- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement the hosted, per-data-store incremental discovery and full-audit reconciler,
including the bounded in-memory retry behavior defined by the authoritative design.

## Dependencies

- Depends on 18-00, 18-02, 18-07, and core source/cache DDL.
- Supplies projection signals to 18-09 and CDC stories 17-00, 17-05, and 17-06.

## Deliverables

1. Add supervisor lifecycle and isolated non-HTTP execution scopes for explicit targets,
   including bounded re-resolution of configured targets that are not yet available.
2. Implement provider-equivalent bounded incremental keyset scans using a disposable
   process-local `(ContentVersion, DocumentId)` cursor.
3. Before a startup or restart audit, capture the maximum current source key as the
   initial incremental boundary. After the audit, start the cursor at exactly that
   pre-audit boundary rather than a later maximum, so commits after the audit remain
   visible to incremental scanning.
4. Implement startup, periodic, rebuild, and readiness-triggered full anti-join audits,
   including bounded audit-local paging and an exact finishing aggregate that separates
   missing, cache-behind, and cache-ahead rows.
5. Provision the required `dms.Document(ContentVersion, DocumentId)` index whenever
   `dms.DocumentCache` is provisioned.
6. Invoke the shared materializer and guarded upsert with fair retry and idle polling for
   missing and cache-behind candidates. Report cache-ahead rows as invariant violations
   without materialization or retry.
7. Add graceful cancellation and sanitized incremental-scan, audit, retry, and failure
   telemetry, and measure realistic plans for both providers.

## Acceptance Evidence

- Provider and multi-data-store tests cover no targets, unresolved targets, late
  resolution, colliding local ids, population, create/update, restart, rebuild,
  transient/persistent failure, fairness, peer isolation, and multiple replicas.
- Concurrency tests cover source update and deletion during materialization through the
  shared guard.
- Query-plan tests prove ordinary high-version updates use indexed incremental discovery
  without a full relationship scan and that a full audit covers the relationship once
  rather than rescanning each repaired prefix.
- Completeness tests prove late lower-version commits and cache-row loss below the cursor
  are repaired by full audit, advancing past failures retains bounded retry, and no
  timestamp, epoch, `StreamEtag` comparison, or cursor/high-watermark becomes a second
  completeness predicate.
- Classification tests prove missing and cache-behind rows are repaired, while a
  cache-ahead row is not materialized, does not enter the retry set, remains in the exact
  audit result, and keeps projection readiness false.
- A synchronized startup test commits a higher-key source update after the finishing
  audit observation but before incremental scanning begins and proves the update remains
  above the pre-audit boundary and is projected without waiting for the next full audit.

## Out of Scope

- Durable workflow/retry state.
- Connector status/source-position readiness.
- Discovery of unlisted CMS targets.
