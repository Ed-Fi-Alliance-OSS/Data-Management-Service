---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Fresh-Cache Reads with Relational Fallback

## Design References

- [Cache-backed reads and domain lifecycle](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-backed-reads-and-domain-lifecycle)
- [Freshness and reconciliation](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation)
- [Configuration and projection target selection](../../../cdc-streaming.md#configuration-and-projection-target-selection)

The linked design sections define cache usability, fallback, response shaping, and direct
fill. This story is only the work package for implementing them.

## Outcome

Add optional DocumentCache use to GET/query body assembly while retaining the existing
relational read path as the correctness path.

## Dependencies

- Depends on 18-00 through 18-04.

## Implementation Scope

- Add the provider cache-lookup adapter to the relational read pipeline.
- Integrate response shaping and authorization with cached and fallback materialization.
- Integrate optional direct fill through the shared materializer and cache writer.
- Add cache-read and fallback metrics.

## Acceptance Evidence

- API and provider integration tests cover the cache states, fallback paths, provider
  prerequisites, and response variants in the referenced design sections.
- Authorization tests cover cached and fallback execution.
- Timeout and concurrency fixtures cover the direct-fill integration boundary.

## Not Assigned to This Story

- Projection scheduling and repair are assigned to 18-04.
- Kafka connector behavior is assigned to E19.
