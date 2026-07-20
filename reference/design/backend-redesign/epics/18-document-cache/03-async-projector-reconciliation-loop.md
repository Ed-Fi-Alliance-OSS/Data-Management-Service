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

Implement the hosted, per-data-store reconciler and bounded in-memory retry behavior
defined by the authoritative design.

## Dependencies

- Depends on 18-00, 18-02, 18-07, and core source/cache DDL.
- Supplies projection signals to 18-09 and CDC stories 17-00, 17-05, and 17-06.

## Deliverables

1. Add supervisor lifecycle and isolated non-HTTP execution scopes for startup targets.
2. Implement provider-equivalent bounded candidate queries.
3. Invoke the shared materializer and guarded upsert with fair retry and idle polling.
4. Add graceful cancellation and sanitized scan/retry/failure telemetry.
5. Measure realistic provider plans and add an ordered-scan index only if evidence
   requires it.

## Acceptance Evidence

- Provider and multi-data-store tests cover selection overlap, no targets, colliding local
  ids, population, create/update, restart, rebuild, transient/persistent failure, fairness,
  peer isolation, and multiple replicas.
- Concurrency tests cover source update and deletion during materialization through the
  shared guard.
- Completeness tests prove lower-version gaps remain visible and no timestamp, epoch, or
  high-watermark becomes a second work predicate.

## Out of Scope

- Durable workflow/retry state.
- Connector status/source-position readiness.
- Dynamic CMS target discovery.
