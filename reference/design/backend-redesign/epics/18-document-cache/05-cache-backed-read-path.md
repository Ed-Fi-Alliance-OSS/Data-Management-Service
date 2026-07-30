---
jira: DMS-1315
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add Fresh-Cache Reads with Relational Fallback

## Design References

- **Cache-backed reads and domain lifecycle**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle
- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Configuration and projection target selection**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection

The referenced design sections define cache usability, fallback, response shaping, and direct
fill. This story is only the work package for implementing them.

## Outcome

Add optional DocumentCache use to GET/query body assembly while retaining the existing
relational read path as the correctness path.

## Dependencies

- Depends on 18-00 through 18-04.
- When the DMS-1190 derivative-routing follow-on is present, consumes the effective-target
  contract in
  `../10-update-tracking-change-queries/39-snapshot-read-replica-runtime-routing.md`.
  This cross-epic compatibility rule does not make primary-only cache delivery wait for
  the placeholder story to receive a Jira key.

## Implementation Scope

- Add the provider cache-lookup adapter to the relational read pipeline.
- Require lifecycle `Tracking`, a clear cache-ahead latch, and row-level content-version
  equality. `Disabled`, `Resetting`, `Rebuilding`, latch, missing, and stale states all
  use relational fallback.
- Integrate response shaping and authorization with cached and fallback materialization.
- Integrate optional direct fill through the shared materializer and atomic
  cache-write/conditional-acknowledgement component.
- Bind cache lookup, lifecycle reads, canonical-version comparison, and relational
  fallback to the request's selected physical database. A snapshot or read-replica request
  either uses `DocumentCache` state from that same database or bypasses cache acceleration;
  it never reads the parent primary's cache.
- Bypass direct fill for snapshot and read-replica targets because direct fill writes
  `dms.DocumentCache` and derivative-eligible requests remain read-only.
- Add cache-read and fallback metrics.

## Acceptance Evidence

- API and provider integration tests cover the cache states, fallback paths, provider
  prerequisites, and response variants in the referenced design sections.
- Integration tests use distinguishable primary, snapshot, and read-replica databases and
  prove cache hits and relational fallback stay on the selected target, primary cache JSON
  is never returned for a derivative request, and derivative requests perform no direct
  fill write.
- Authorization tests cover cached and fallback execution.
- Timeout and concurrency fixtures cover the direct-fill integration boundary.

## Not Assigned to This Story

- Projection scheduling and repair are assigned to 18-04.
- Kafka connector behavior is assigned to E19.
