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

Publish operator guidance for the implemented local and initially offline production-like
relational CDC capability without redefining its architecture or contracts.

## Dependencies

- Depends on 18-07 for general DocumentCache operation and the completed E19
  setup/readiness behavior documented by this runbook.

## Deliverables

1. Document local opt-in, connector registration, topic discovery, Kafka UI use, and
   troubleshooting commands. State prominently that v1 opt-in is available only while a
   new physical database is initially provisioned with the completed E18 schema, before DMS
   writes are admitted. An unbound existing database cannot be enabled; operators must use a
   new database. Schema upgrade, migration of legacy data for first-time enablement, and
   later CDC retrofit are not provided by v1. Distinguish this from supported exact-match
   restart and guarded source-replacement recovery of a database originally enabled through
   the new-database flow; both expose eventual status rather than another exact baseline.
2. Document PostgreSQL and SQL Server prerequisites, least-privilege access, provider
   artifacts, retention, restart, and cleanup. Identify the pinned Debezium 3.6 base and
   published Ed-Fi image digest, explain why floating tags are not supported, and list
   SQL Server 2025 as the Ed-Fi known-working qualification target without presenting it
   as an upstream-tested Debezium version. For SQL Server, document the binding-derived
   internal schema-history topic, same-cluster bootstrap servers, externalized producer and
   consumer security settings, durable history producer, single-partition infinite-retention
   topic profile, connector-only ACLs, and `include.schema.changes=false`. Explain that the
   internal history is required even though optional public schema-change events are
   disabled; PostgreSQL has no corresponding history topic.
3. Document public Kafka compact-only topic/ACL/consumer operation, including why segment
   time/size deletion through a cleanup policy containing `delete` is prohibited without a
   separately defined authoritative bootstrap source,
   the explicit seven-day `delete.retention.ms` minimum, the fixed 24-hour consumer-
   bootstrap deadline, earliest-offset and end-offset-barrier handling, invalid-state
   discard/restart behavior, and consumer capacity qualification against its largest
   supported retained topic log, including dirty/uncompacted records, partition skew,
   maximum-sized records, durable state writes, and concurrent mutation traffic. Include
   cleaner-health and earliest-to-end scan-volume observation; live-key count alone is not
   sufficient evidence. Distinguish deployment validation of topic retention from the
   independently operated consumer's responsibility to prove its runtime conformance.
   Also document `maxRecordBytes` as a mutable operational ceiling rather than a universal
   schema maximum or immutable binding field; its pre-publication producer enforcement;
   explicit `buffer.memory` and worker-heap requirements; required
   producer/topic/broker/replica/consumer settings; and the downstream-first procedure for
   increasing it without a new topic generation. Cover DMS per-database projection-health
   observation and deployment-owned combined readiness. Explain provider barrier
   capture/comparison, the internal heartbeat's idle-
   source role, and why connector status or lag alone is insufficient. Document the v1
   readiness scope: exact combined readiness exists only during initial provisioning of a
   new database that the setup controller proves has not been published to any writer.
   First-write admission remains closed through a fresh startup audit and the later
   publication barrier. After admission opens, status is observational and eventually
   consistent; it neither gates normal traffic nor certifies another exact baseline. State
   that v1 provides no production cross-replica/external-writer gate or transaction drain.
   Warn that the HTTP request-body limit alone is not the record-size bound.
4. Document connector restart, offset reset, resnapshot, topic recreation, cache rebuild,
   target migration/retirement, cache-ahead invariant recovery, and explicit destructive
   cleanup. A possibly published higher cache version requires a new binding generation,
   topic, consumer state namespace, and snapshot rather than an in-place lower-version
   correction. The old connector and cache writers are stopped before the full cache and
   durable latch are cleared together. Treat SQL Server schema history and Connect offsets
   as one recovery unit: ordinary stop retains both; missing or unreadable history with
   retained offsets fails closed; and an explicit destructive resnapshot stops the
   connector before resetting/removing both. Never advise recreating an empty history topic
   around retained offsets.
5. Document binding-state location, backup, normal-stop retention, fail-closed missing
   state, explicit adoption, cleanup ordering, target/source mismatch diagnosis, and
   new-generation migration. Explain that a new independent target created from a
   template, clone, or copied backup receives a new `dms.DataStoreIdentity.SourceIdentity`
   before binding, while a rollback or restore that replaces an existing source uses the
   guarded identity-rotation and new-binding/topic recovery workflow. Never instruct
   operators to rewrite a binding in place or rotate identity during an ordinary setup
   retry.
6. State explicitly that same-topic baseline-replacing correction and incompatible-contract
   cutover are deferred until a separately owned deployment capability can fence every DMS
   replica and external writer and drain admitted work. Cross-link the design-only safety
   constraints without presenting them as executable v1 runbooks. Link E18's
   representation-restamp utility only as an explicitly offline API/cache repair; do not
   claim that it restores an exact CDC baseline after first-write admission.
7. Cross-link the canonical design and both ADRs instead of repeating their normative
   tables or algorithms.
8. Document Debezium 3.6 P50/P95/P99 `MilliSecondsBehindSource` telemetry, the explicit
   `statistics.metrics.enabled=true` setting, and why those metrics diagnose lag but do
   not replace the source-position readiness barrier. Include diagnosis of the SQL Server
   unavailable-value marker as a fail-closed required-record error.

## Acceptance Evidence

- Instructions are verified against the implemented scripts, templates, and status
  output for both providers, including the pinned image identity and SQL Server 2025.
- Instructions never offer an existing-database enablement path. They show the fail-closed
  eligibility diagnostic and direct operators to provision a new database without
  inventing an undocumented migration or data-copy procedure.
- Troubleshooting covers persistent projection failure, provider key/filter/order
  failure, cache-ahead invariant diagnosis including later source equality, missing target,
  missing/malformed source identity, source-resolution failure, binding mismatch, and
  governed artifacts without binding state. It also covers a missing/stalled heartbeat,
  SQL Server capture-agent delay, missing/misconfigured/unreadable SQL Server schema
  history, malformed or ambiguous Connect source offsets, and a connector that is running
  but remains below its post-audit provider barrier.
- Consumer-bootstrap troubleshooting covers cleaner degradation, retained-log growth,
  partition skew, slow durable-state writes, deadline exhaustion, invalid-state discard,
  and capacity requalification before retrying production use.
- Procedures distinguish initial offline readiness from later observational health. They
  require positive setup-controller evidence that a new database has not been published to
  writers, keep first-write admission closed through the initial barrier, and never claim
  that timeout or setup-controller loss completed readiness.
- Destructive or replay-producing operations are clearly marked and never inferred from
  configuration removal.
- Local teardown instructions distinguish ordinary stop from destructive volume removal
  and remove connector offsets plus the SQL Server schema-history topic and ACLs with the
  other governed artifacts before their JSON binding records.
- Documentation identifies same-topic baseline replacement and incompatible-contract
  cutover as deferred and provides no v1 command sequence that could be mistaken for an
  implemented production write gate. Offline restamp guidance does not claim exact CDC
  readiness.
- Documentation distinguishes CDC from Change Queries and from response serialization.
- Instructions never present a consumer reconstruction that exceeded 24 hours as valid,
  even when the topic retains tombstones longer than seven days.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- A production cross-replica/external-writer admission gate or transaction drain.
- Exact baseline-replacing repair or contract cutover after first-write admission.
- Service availability or lag SLO commitments beyond the v1 consumer-bootstrap deadline.
- Product-specific consumer implementation guidance beyond the public contract.
