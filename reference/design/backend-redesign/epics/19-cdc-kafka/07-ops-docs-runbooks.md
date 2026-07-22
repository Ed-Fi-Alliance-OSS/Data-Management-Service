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
   disabled; PostgreSQL has no corresponding history topic. Document the provider source-
   history monitor: PostgreSQL slot/publication identity, status, and retained WAL range;
   SQL Server capture instances/jobs, retained LSN ranges, configured cleanup retention,
   and remaining margin. Explain that SQL Server's default cleanup retention is 4,320
   minutes (72 hours), not an indefinite recovery window.
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
   Require incremental consumers to renew durable per-partition end-offset-barrier evidence
   at least every 24 hours. A stale, missing, corrupt, partition-mismatched, or otherwise
   uncertain checkpoint invalidates all local state and requires another complete bounded
   bootstrap; never instruct a consumer to resume incrementally from it.
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
4. Document connector restart, cache rebuild, guarded source replacement, explicit target
   retirement, cache-ahead invariant recovery, source-history diagnosis, and explicit
   destructive cleanup. A
   possibly published higher cache version requires the deferred new-namespace workflow
   rather than an in-place lower-version correction; stop publication and retain the cache
   and latch for diagnosis. Permit E18's full-cache clear/latch-reset operation only with
   positive evidence that the projection was internal-only. Treat SQL Server schema history
   and Connect offsets as one retained lifecycle unit on ordinary stop. Before every post-
   enablement start/resume, require source-history status `healthy`; `unknown` remains
   stopped and fail-closed. Missing or re-created provider artifacts, expired retained
   history, or inconsistent history/offsets durably latch `SourceHistoryContinuityLost`,
   stop the connector, and make that binding unrecoverable in v1. Never advise resetting
   offsets, recreating a slot/capture/history topic, or resnapshotting the existing public
   topic. Destructive retirement may remove those artifacts but does not recover the
   binding. Add a sensitive-data disclosure procedure for bytes that should never have
   appeared in the public topic: mark the target not ready, fence and verify connector
   tasks, revoke consumer ACLs, perform any offline restamp, and destructively retire the
   affected binding generation. Record the restamp/operation identifier, binding
   generation, topic, containment time, deletion request, and broker or managed-platform
   purge confirmation. A corrective upsert, tombstone, compaction request, configuration
   removal, or unverified metadata lookup failure is not purge evidence. If platform purge
   evidence is unavailable, keep the incident open. Never restart or recreate the old
   binding/topic; leave CDC unavailable until the deferred replacement-namespace workflow
   is implemented. Identify independently operated consumer copies as deployment incident
   scope rather than claiming that topic deletion purges them.
5. Document binding-state location, backup, normal-stop retention, fail-closed missing
   state, guarded explicit adoption, cleanup ordering, target/source mismatch diagnosis, and
   guarded new-generation source replacement. Adoption requires an operator-supplied
   complete binding record and live verification of the physical source and every retained
   provider, connector, offset, topic, ACL, configuration, durability, partitioner, and
   record-size artifact; instructions never infer fields, and any incomplete or mismatched
   case remains unchanged and fail-closed. State that adoption is missing-state recovery
   around an already complete governed-artifact set, not a first-time enablement path.
   Explain that a new independent target created from a template, clone, or copied backup
   receives a new
   `dms.DataStoreIdentity.SourceIdentity`
   before binding, while a rollback or restore that replaces an existing source uses the
   guarded identity-rotation and new-binding/topic recovery workflow from 19-00 and 19-04.
   It fences the old connector, creates every artifact under the new generation, retains or
   explicitly retires the old generation, and reports eventual status rather than another
   exact baseline. Never instruct operators to rewrite a binding in place or rotate identity
   during an ordinary setup retry. Do not present guarded source replacement as a way to
   clear or reuse a binding whose source-history loss is already latched.
6. State explicitly that exact same-topic baseline replacement and incompatible-contract
   cutover are deferred until a separately owned deployment capability can fence every DMS
   replica and external writer and drain admitted work. Cross-link the design-only safety
   constraints without presenting them as executable v1 runbooks. Link E18's
   representation-restamp utility as the required explicitly offline path for every byte-
   changing API/cache correction; it publishes higher versions eventually but does not
   restore an exact CDC baseline after first-write admission. State that a safe recovery
   from source-history loss would also require a new binding generation, topics, consumer
   state namespace, and snapshot, and that this is a deferred future workflow rather than a
   v1 procedure.
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
  but remains below its post-audit provider barrier. It distinguishes temporary continuity
  `unknown` from terminal `lost`, shows the last successful proof and remaining retention
  margin, and never clears a loss latch through artifact recreation or offset changes.
- Consumer-bootstrap troubleshooting covers cleaner degradation, retained-log growth,
  partition skew, slow durable-state writes, deadline exhaustion, invalid-state discard,
  expired or uncertain incremental-continuity evidence, and capacity requalification before
  retrying production use.
- Procedures distinguish initial offline readiness from later observational health. They
  require positive setup-controller evidence that a new database has not been published to
  writers, keep first-write admission closed through the initial barrier, and never claim
  that timeout or setup-controller loss completed readiness.
- Destructive or replay-producing operations are clearly marked and never inferred from
  configuration removal.
- Sensitive-data disclosure instructions prove containment precedes restamping, record
  broker or managed-platform evidence that the public topic and platform-governed retained
  copies covered by its deletion guarantee are purged, and keep both the incident and CDC
  target open/not-ready when that evidence is unavailable. They never offer compaction,
  tombstones, corrective republication, or recreation of the old topic as purge.
- Local teardown instructions distinguish ordinary stop from destructive volume removal
  and remove the connector, offsets, public/progress/schema-history topics and ACLs,
  PostgreSQL slot/publication or SQL Server capture instances/jobs, and every other governed
  artifact, then remove terminal incident state immediately before/with the JSON binding
  record. They retain that complete set on ordinary stop and fail closed if any destructive
  removal is incomplete.
- Adoption documentation is verified against the 19-00 state operation and 19-04 live
  validation, never offers inferred state reconstruction, and leaves failed adoption
  unchanged. Guarded source-replacement documentation is verified against the implemented
  new-generation workflow and rejects terminal source-history loss and possibly published
  cache-ahead state as recovery inputs.
- Documentation identifies exact same-topic baseline replacement and incompatible-contract
  cutover as deferred and provides no v1 command sequence that could be mistaken for an
  implemented production write gate. Offline restamp guidance requires higher versions for
  every byte-changing correction and does not claim exact CDC readiness. It identifies
  source-history loss as terminal for the v1 binding and contains no same-topic offset-reset,
  provider-artifact recreation, or resnapshot recovery steps.
- Documentation distinguishes CDC from Change Queries and from response serialization.
- Instructions never present a consumer reconstruction that exceeded 24 hours as valid,
  even when the topic retains tombstones longer than seven days, and never retain
  incremental state whose 24-hour continuity proof is stale or uncertain.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- A production cross-replica/external-writer admission gate or transaction drain.
- Exact baseline-replacing repair or contract cutover after first-write admission.
- Recovery from provider source-history loss or replacement-namespace cutover.
- Possibly published cache-ahead replacement-namespace recovery.
- Service availability or lag SLO commitments beyond the v1 consumer-bootstrap deadline.
- Product-specific consumer implementation guidance beyond the public contract.
