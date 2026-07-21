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

- Depends on 18-00 and 18-02; optional direct fill additionally depends on 18-07. The
  read path remains correct while 18-03 is disabled or behind.

## Deliverables

1. Integrate optional cache lookup and the canonical freshness test into the read path.
   Treat both cache-behind and cache-ahead rows as unusable; an ahead row also remains an
   independently reported projection invariant violation.
2. Ignore the cache row's CDC-only `StreamEtag` and reuse existing profile, link, and
   request-specific `_etag` shaping after cache or relational assembly.
3. Add relational fallback and an optional monotonic direct fill. Direct fill uses 18-07's
   conditional upsert without taking a write-conflicting source-row lock; failure or a
   concurrent canonical change does not affect the response.
4. Emit cache hit, miss, stale miss, and fallback telemetry.

## Acceptance Evidence

- GET/query tests cover enabled/disabled, hit, miss, cache-behind, cache-ahead, unhealthy
  projection, profile projection, link stripping, and identical cache/fallback
  validators. Cache-ahead fallback does not overwrite or delete the row.
- Authorization tests prove cached JSON never replaces relational authorization or
  candidate selection.
- Tests prove reads do not enqueue projector work and remain correct if direct fill
  fails.
- Tests prove direct fill takes no write-conflicting source-row lock and cannot delay or
  fail the relational response through projection-held canonical-row contention.

## Out of Scope

- Mandatory cache-backed reads.
- Querying/authorizing from `DocumentJson`.
- Kafka connector behavior.
