---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- [Authoritative relational CDC design](../../../cdc-streaming.md)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Publish operator guidance for the implemented local and production-like relational CDC
capability without redefining its architecture or contracts.

## Deliverables

1. Document local opt-in, connector registration, topic discovery, Kafka UI use, and
   troubleshooting commands.
2. Document PostgreSQL and SQL Server prerequisites, least-privilege access, provider
   artifacts, retention, restart, and cleanup.
3. Document Kafka compact-only topic/ACL/consumer operation, including why time/delete
   retention is prohibited without a separately defined authoritative bootstrap source,
   DMS per-database projection-health observation, and deployment-owned combined
   readiness.
4. Document connector restart, offset reset, resnapshot, topic recreation, cache rebuild,
   target migration/retirement, cache-ahead invariant recovery, and explicit destructive
   cleanup. A possibly published higher cache version requires a new binding generation,
   topic, consumer state namespace, and snapshot rather than an in-place lower-version
   correction. The old connector and cache writers are stopped before the full cache and
   durable latch are cleared together.
5. Document binding-state location, backup, normal-stop retention, fail-closed missing
   state, explicit adoption, cleanup ordering, target/source mismatch diagnosis, and
   new-generation migration. Explain that a new independent target created from a
   template, clone, or copied backup receives a new `dms.DataStoreIdentity.SourceIdentity`
   before binding, while a rollback or restore that replaces an existing source uses the
   guarded identity-rotation and new-binding/topic recovery workflow. Never instruct
   operators to rewrite a binding in place or rotate identity during an ordinary setup
   retry.
6. Document both compatibility paths: for a conforming projection or opaque-ETag
   correction, stop old cache writers including direct fill, clear and rebuild cache
   state, retain the binding/topic/offsets, and verify later equal-version records; for an
   incompatible key/field/type/delete/document-contract change, mark readiness false,
   reserve the new binding/topic/`contractVersion`, stop or fence the old connector and
   verify its tasks cannot capture from the source, stop old-contract cache writers,
   clear and completely reproject the cache with only new-contract writers, snapshot it
   with the new connector, bootstrap the new consumer namespace, and explicitly retain
   or retire the old topic. The old connector must stop before the cache clear and must
   never restart against the rebuilt cache.
7. Cross-link the canonical design and both ADRs instead of repeating their normative
   tables or algorithms.

## Acceptance Evidence

- Instructions are verified against the implemented scripts, templates, and status
  output for both providers.
- Troubleshooting covers persistent projection failure, provider key/filter/order
  failure, cache-ahead invariant diagnosis including later source equality, missing target,
  missing/malformed source identity, source-resolution failure, binding mismatch, and
  governed artifacts without binding state.
- Destructive or replay-producing operations are clearly marked and never inferred from
  configuration removal.
- Local teardown instructions distinguish ordinary stop from destructive volume removal
  and remove governed artifacts before their JSON binding records.
- Neither procedure advances canonical `ContentVersion` or claims simultaneous
  incompatible-contract publication from the single cache row. The compatible repair
  proves that old cache writers are stopped and later equal-version offsets replace
  prior values in the existing topic.
- The incompatible-contract procedure has a verified old-connector stop or source fence
  before cache clearing/reprojection, so no rebuilt row can reach the old topic; only the
  new connector snapshots the rebuilt cache.
- Documentation distinguishes CDC from Change Queries and from response serialization.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- SLA/SLO commitments.
- Consumer implementation guidance beyond the public contract.
