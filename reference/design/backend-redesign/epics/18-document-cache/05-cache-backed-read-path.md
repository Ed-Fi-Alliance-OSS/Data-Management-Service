---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Fresh-Cache Reads with Relational Fallback

## Design References

- [Cache-backed reads and domain lifecycle](../../../cdc-streaming.md#cache-backed-reads-and-domain-lifecycle)
- [Freshness and reconciliation](../../../cdc-streaming.md#freshness-and-reconciliation)

## Outcome

Add optional cache-backed GET/query body assembly while preserving relational
authorization, candidate selection, fallback, and response shaping.

## Dependencies

- Depends on 18-00 and 18-02; it remains correct while 18-03 is disabled or behind.

## Deliverables

1. Integrate optional cache lookup and the canonical freshness test into the read path.
2. Ignore the cache row's CDC-only `StreamEtag` and reuse existing profile, link, and
   request-specific `_etag` shaping after cache or relational assembly.
3. Add relational fallback and an optional guarded direct fill.
4. Emit cache hit, miss, stale miss, and fallback telemetry.

## Acceptance Evidence

- GET/query tests cover enabled/disabled, hit, miss, stale row, unhealthy projection,
  profile projection, link stripping, and identical cache/fallback validators.
- Authorization tests prove cached JSON never replaces relational authorization or
  candidate selection.
- Tests prove reads do not enqueue projector work and remain correct if direct fill
  fails.

## Out of Scope

- Mandatory cache-backed reads.
- Querying/authorizing from `DocumentJson`.
- Kafka connector behavior.
