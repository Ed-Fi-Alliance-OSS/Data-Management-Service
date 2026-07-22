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
   artifacts, retention, restart, and cleanup. Identify the pinned Debezium 3.6 base and
   published Ed-Fi image digest, explain why floating tags are not supported, and list
   SQL Server 2025 as the Ed-Fi known-working qualification target without presenting it
   as an upstream-tested Debezium version.
3. Document Kafka compact-only topic/ACL/consumer operation, including why time/delete
   retention is prohibited without a separately defined authoritative bootstrap source,
   how immutable `maxRecordBytes` is established from the maximum fully materialized
   link-bearing envelope, required producer/topic/broker/replica/consumer size settings,
   DMS per-database projection-health observation, and deployment-owned combined
   readiness. Explain provider barrier capture/comparison, the internal heartbeat's idle-
   source role, and why connector status or lag alone is insufficient. Document the v1
   maintenance-window assumption for initial readiness and explicit baseline replacement:
   deployment automation blocks all canonical mutation sources, drains in-flight requests
   and transactions, forces a fresh startup/restart audit, and keeps the gate closed through
   the later publication barrier. State that the window has no design maximum and that
   timeout or unverifiable drain state remains fail-closed. Warn that the HTTP request-body
   limit alone is not the record-size bound.
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
   correction, enter the maintenance window, drain canonical mutations, stop old cache
   writers including direct fill, clear and rebuild cache state, retain the
   binding/topic/offsets, and verify later equal-version records before reopening writes;
   for an incompatible key/field/type/delete/document-contract change, mark readiness false,
   reserve the new binding/topic/`contractVersion`, stop or fence the old connector and
   verify its tasks cannot capture from the source, stop old-contract cache writers,
   clear and completely reproject the cache with only new-contract writers, snapshot it
   with the new connector, bootstrap the new consumer namespace, and explicitly retain
   or retire the old topic. The old connector must stop before the cache clear and must
   never restart against the rebuilt cache.
7. Cross-link the canonical design and both ADRs instead of repeating their normative
   tables or algorithms.
8. Document Debezium 3.6 P50/P95/P99 `MilliSecondsBehindSource` telemetry, the explicit
   `statistics.metrics.enabled=true` setting, and why those metrics diagnose lag but do
   not replace the source-position readiness barrier. Include diagnosis of the SQL Server
   unavailable-value marker as a fail-closed required-record error.

## Acceptance Evidence

- Instructions are verified against the implemented scripts, templates, and status
  output for both providers, including the pinned image identity and SQL Server 2025.
- Troubleshooting covers persistent projection failure, provider key/filter/order
  failure, cache-ahead invariant diagnosis including later source equality, missing target,
  missing/malformed source identity, source-resolution failure, binding mismatch, and
  governed artifacts without binding state. It also covers a missing/stalled heartbeat,
  SQL Server capture-agent delay, malformed or ambiguous Connect source offsets, and a
  connector that is running but remains below its post-audit provider barrier.
- Procedures distinguish an automatic health failure from an explicit maintenance window,
  list every mutation source that must be gated, and require evidence that admitted
  requests and transactions drained. They allow an indefinitely extended window, retry, or
  explicit abort with CDC still not ready, but never reopen writes as ready after timeout or
  setup-controller loss before completion.
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
