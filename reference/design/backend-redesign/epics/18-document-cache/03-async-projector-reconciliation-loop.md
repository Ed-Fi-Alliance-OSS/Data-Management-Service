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
- [Projection health and CDC readiness](../../../cdc-streaming.md#projection-health-and-cdc-readiness)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement the hosted, per-data-store incremental discovery and full-audit reconciler,
including the bounded in-memory retry behavior defined by the authoritative design.

## Dependencies

- Depends on 18-00, 18-02, 18-07, and core source/cache DDL.
- Supplies projection signals to 18-09 and CDC stories 17-00, 17-05, and 17-06.

## Deliverables

1. Add supervisor lifecycle and isolated non-HTTP execution scopes for startup targets.
2. Implement provider-equivalent bounded incremental keyset scans using a disposable
   process-local `(ContentVersion, DocumentId)` cursor.
3. Implement startup, periodic, rebuild, and readiness-triggered full anti-join audits,
   including bounded audit-local paging and an exact finishing aggregate.
4. Provision the required `dms.Document(ContentVersion, DocumentId)` index whenever
   `dms.DocumentCache` is provisioned.
5. Invoke the shared materializer and guarded upsert with fair retry and idle polling.
6. Add graceful cancellation and sanitized incremental-scan, audit, retry, and failure
   telemetry, and measure realistic plans for both providers.

## Acceptance Evidence

- Provider and multi-data-store tests cover selection overlap, no targets, colliding local
  ids, population, create/update, restart, rebuild, transient/persistent failure, fairness,
  peer isolation, and multiple replicas.
- Concurrency tests cover source update and deletion during materialization through the
  shared guard.
- Query-plan tests prove ordinary high-version updates use indexed incremental discovery
  without a full relationship scan and that a full audit covers the relationship once
  rather than rescanning each repaired prefix.
- Completeness tests prove late lower-version commits and cache-row loss below the cursor
  are repaired by full audit, advancing past failures retains bounded retry, and no
  timestamp, epoch, `StreamEtag` comparison, or cursor/high-watermark becomes a second
  completeness predicate.

## Out of Scope

- Durable workflow/retry state.
- Connector status/source-position readiness.
- Dynamic CMS target discovery.
