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

## Outcome

Add optional cache-backed GET/query body assembly while preserving relational
authorization, candidate selection, fallback, and response shaping.

## Dependencies

- Depends on 18-00 through 18-04. At runtime, the read path remains correct while the
  18-04 projector is disabled or behind.

## Deliverables

1. Integrate optional cache lookup, the canonical freshness test, and the singleton
   `DocumentCacheState.CacheAheadRecoveryRequired` check into one database read path.
   Treat missing/malformed state, a set latch, cache-behind, and cache-ahead rows as
   unusable; an ahead row also remains an independently reported projection invariant
   violation. Obtain the source/cache version comparison from one database statement. For
   SQL Server, run that statement at `READ COMMITTED` only after the 18-01
   same-open-connection RCSI validation; failed validation bypasses cache lookup and direct
   fill and uses relational fallback without setting the durable latch.
2. Ignore the cache row's CDC-only `StreamEtag` and reuse existing profile, link, and
   request-specific `_etag` shaping after cache or relational assembly.
3. Add relational fallback and an optional monotonic direct fill only while the durable
   latch is clear. Direct fill uses 18-02's
   final optimistic source-version check and 18-03's conditional upsert without requesting
   or retaining an update/write source-row lock as a content-version fence. Apply one short
   end-to-end `ReadAcceleration:DirectFillTimeout` deadline across all source-read,
   cache-row, foreign-key, trigger, and ordinary database contention. Do not renew the
   deadline per statement or change projector target-backoff state; cap each database
   operation by the remaining budget. Timeout, failure, or a concurrent canonical change
   abandons the fill without failing the response or delaying it beyond that small budget.
   Validate the positive duration and supply a conservative implementation-tuned default
   in supported appsettings and operator documentation.
4. Emit cache hit, miss, stale miss, fallback, direct-fill success, abandonment, and timeout
   telemetry.

## Acceptance Evidence

- GET/query tests cover enabled/disabled, hit, miss, cache-behind, cache-ahead, missing
  state, a durable latched state after source/cache versions become equal, unhealthy
  projection, profile projection, link stripping, and identical cache/fallback validators.
  Cache-ahead fallback does not overwrite, delete, or independently clear the row/latch.
- Authorization tests prove cached JSON never replaces relational authorization or
  candidate selection.
- Tests prove reads do not enqueue projector work and remain correct if direct fill
  fails.
- SQL Server tests prove an RCSI-disabled or unreadable target performs relational fallback
  without cache use, direct fill, or latch mutation, while an RCSI-enabled lookup compares
  source/cache state from one statement. The failure remains target-scoped and does not
  affect relational API correctness.
- Tests prove direct fill requests no explicit update/write source-row lock as a
  content-version fence and carries no lock from the optimistic source check into the cache
  transaction. Synchronized source-read, cache-row conflict, concurrent delete/foreign-key,
  trigger, timeout, and ordinary failure cases prove it is abandoned within the direct-fill
  budget and never fails the relational response. Tests preserve ordinary integrity locks
  acquired by foreign-key enforcement and the UUID-validation trigger.
- Deadline tests span multiple statements and prove each operation receives only the
  remaining direct-fill budget, with no per-statement reset or projector backoff effect.
- Configuration tests reject a nonpositive `DirectFillTimeout`, pin its shipped default,
  and prove it remains below the ordinary database command timeout.

## Out of Scope

- Mandatory cache-backed reads.
- Querying/authorizing from `DocumentJson`.
- Kafka connector behavior.
