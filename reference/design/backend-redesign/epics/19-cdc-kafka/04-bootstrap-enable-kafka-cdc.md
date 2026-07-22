---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- [Enablement and initial readiness sequence](../../../cdc-streaming.md#enablement-and-initial-readiness-sequence)
- [V1 readiness scope](../../../cdc-streaming.md#v1-readiness-scope)
- [Local bootstrap and CI](../../../cdc-streaming.md#local-bootstrap-and-ci)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Source-history continuity](../../../cdc-streaming.md#source-history-continuity)
- [Deployment-owned physical source binding](../../../cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)

## Outcome

Add explicit, idempotent local topic/ACL provisioning and connector registration while a
selected new physical database is being initially provisioned, using deployment-owned
binding state. Expose the same workflow to production-like provisioning only while its
controller can prove that the new database has not been published to any writer.

## Dependencies

- Depends on 19-00 through 19-02, the published transform from 19-03, and the
  projection/readiness inputs named by 19-00.

## Deliverables

1. Add `-EnableKafkaCdc` and optional `-CdcBindingStatePath` to the appropriate
   local/bootstrap entry points while retaining Kafka UI as an independent option. For
   first-time enablement, consume bootstrap's explicit result that it created the selected
   database and initial write admission has not opened; reject an unbound path that selects
   or reuses an already-provisioned data store before creating binding state or external
   artifacts. Permit later exact-match validation/restart only when the immutable binding
   proves the database was already enabled through this supported path.
2. Reuse bootstrap new-data-store creation and generated provider connector templates.
   Provision and validate the complete current E18 schema before binding/provider setup;
   do not attempt to upgrade a legacy cache schema.
3. Require the selected deployment target to be present in DMS
   `DocumentCache:Targets`, and reserve or exact-match its immutable binding before
   creating governed artifacts.
4. Create or validate the public topic with exactly `cleanup.policy=compact`, an explicit
   per-topic `delete.retention.ms` of at least `604800000` (seven days), the binding's fixed
   partition count, and `max.message.bytes=<maxRecordBytes>` from the current operational
   policy. Reject any cleanup policy that includes `delete`, a missing topic-level
   tombstone-retention override even
   when the broker default is high enough, a value below the minimum, or any
   missing/conflicting size. Provision and idempotently validate literal, binding-scoped
   topic ACLs for the deployment-supplied connector and instance consumer principals, plus
   their required consumer-group ACLs; do not emit shared-topic, wildcard-topic, or
   cross-instance consumer grants.
5. For SQL Server, create or validate the binding-derived
   `<public-topic>.schema-history` topic before connector registration with exactly one
   partition, `cleanup.policy=delete`, `retention.ms=-1`, `retention.bytes=-1`, and the
   active local or production replication-factor/`min.insync.replicas` profile. Reject
   compaction, finite time/size retention, another name, or another partition count.
   Provision literal ACLs granting only the connector principal the `READ`, `WRITE`,
   `DESCRIBE`, and `DESCRIBE_CONFIGS` permissions required by the history producer,
   consumer, and validation client. The deployment principal owns topic creation/deletion;
   grant no instance-consumer access. PostgreSQL creates no schema-history topic.
6. Before connector registration, validate that the effective broker request,
   record-batch, and replica-fetch limits accept `maxRecordBytes`. Configure the local
   broker accordingly; require equivalent verifiable capability from a production-like
   broker and fail closed when it is smaller or cannot be verified. Document that each
   consumer must set `max.partition.fetch.bytes` and `fetch.max.bytes` to at least the
   operational ceiling and provision memory for one record.
7. Implement idempotent Kafka Connect create/update, external combined-status polling,
   timeout, and condition-specific diagnostics. Require the setup controller's positive
   evidence that it created the selected database and has not published it to any DMS
   replica or other writer. Establish capture and start or roll out the selected DMS
   projector contexts so their immediate startup audit runs before first-write admission.
   Keep first-write admission closed through the post-audit barrier and report initial
   combined ready only afterward. Do not implement or delegate to an unspecified
   cross-replica mutation gate or transaction drain. Fail before registration if the worker
   does not run the deployment-pinned Ed-Fi image digest built from the required Debezium
   3.6 base or permit the required source-producer overrides. After registration, read
   back the connector configuration and reject drift from the required idempotence,
   acknowledgement, retry, maximum-in-flight, operational maximum-request and producer
   buffer-memory values, no-compression, binding `partitionerAlgorithm`,
   `errors.tolerance=none`, or provider heartbeat and `statistics.metrics.enabled=true`
   values. For SQL Server, also reject a missing or conflicting schema-history topic,
   same-cluster bootstrap-server value, history-producer durability value, schema-history
   client security configuration required by the deployment, or `include.schema.changes`
   value other than the required `false`. Reject a
   missing/unknown algorithm token or live partitioner configuration that does not
   implement `kafka-murmur2-v1`. Use the 19-00 provider adapter and connector-offset
   REST response for the post-audit barrier; do not infer catch-up from task status or lag.
   Treat a failed connector task as not ready regardless of offset or lag observations.
   After initial enablement establishes continuity, stop or keep the connector stopped until
   19-00 proves retained offsets and `healthy` provider source history before every start or
   resume. Missing offsets, a terminal continuity latch, or `unknown` evidence never
   authorizes the fixed `snapshot.mode=initial` connector to resnapshot the existing public
   topic. ACL and record-size verification must complete before connector registration and
   before combined readiness can pass. A timeout leaves the target not ready and the
   database offline. Setup may retry or explicitly abandon CDC and open first-write
   admission with the target not ready; that database then becomes ineligible for later v1
   first-time enablement.
8. Implement a coordinated in-place `maxRecordBytes` increase without reserving a new
   binding generation or topic: mark the target not ready, confirm consumer fetch and
   deserialization capacity, raise broker/replica and topic limits, then raise producer
   `buffer.memory` to at least the new ceiling and `max.request.size` last and restart or
   resume the connector. Validate every effective value before restoring readiness; a
   partial or unverifiable rollout remains not ready.
9. Print sanitized binding-generation/connector/source/topic identity, including the
   derived SQL Server schema-history topic. Retain binding, connector offsets, ACLs, and
   every governed topic on normal stop. During explicit destructive volume teardown, stop
   the connector; remove its SQL Server history topic, ACLs, and offsets as one lifecycle
   unit; remove the remaining governed artifacts; and delete any terminal incident state
   immediately before/with the binding record last. Never delete or recreate an empty
   history topic automatically while retained offsets exist.
   Also print the effective public-topic tombstone retention and the fixed 24-hour
   consumer-bootstrap deadline without claiming to certify an independently operated
   consumer.
10. Expose the same workflow to E2E setup before observed test traffic begins.

## Acceptance Evidence

- Script/integration tests cover disabled, UI-only, invalid or unprojected target,
  successful fresh-database setup, repeated exact-binding retry, rejection of an unbound
  already-provisioned or legacy-schema database, timeout, normal-stop retention, missing
  binding around existing artifacts, missing offline-provisioning evidence, and destructive
  teardown flows including incident-before-binding cleanup. Initial rejection occurs before
  binding, provider, topic, ACL, or connector creation; a successfully enabled database
  remains restartable.
- Sequence tests prove binding reservation precedes artifact creation and that external
  initial combined readiness follows the authoritative new/offline-database algorithm
  without a backfill epoch. They reject a previous zero audit, keep first-write admission
  closed through a fresh startup audit, and open it only after readiness passes. An
  idle-provider case proves the generated heartbeat advances the committed source offset
  through a barrier captured after that audit before readiness passes.
- Production-like validation rejects unsafe topic-prefix use, immutable binding rewrite,
  in-place topic partition-count or `partitionerAlgorithm` changes, segment time/size
  deletion through a cleanup policy containing `delete`, a missing topic-level
  `delete.retention.ms` override or a value below `604800000`, and source/topic-generation
  reuse. It accepts a coordinated in-place `maxRecordBytes` increase only when the binding,
  topic name, partition count, partitioner, and contract remain unchanged and every
  consumer, broker/replica, topic, producer-buffer, and producer-request prerequisite is
  validated in downstream-first order.
- Registration tests reject a worker policy that disallows the required producer
  overrides and any live connector configuration with conflicting ordering settings or
  partitioner behavior, or with missing/conflicting `errors.tolerance=none`,
  `max.request.size`, `buffer.memory`, compression, heartbeat interval/action query, or SQL
  Server poll relationship. Status tests reject an unsupported connector-offset endpoint
  and malformed, snapshot, wrong-source, or ambiguous provider offsets. They also reject a
  floating or unexpected connector image and missing/conflicting
  `statistics.metrics.enabled=true`.
- Restart tests prove an established connector remains stopped while source-history status
  is `unknown`, starts only after affirmative `healthy` evidence, and remains permanently
  stopped for a binding latched `SourceHistoryContinuityLost`. Missing offsets or recreated
  provider artifacts never trigger an initial snapshot into the existing topic.
- SQL Server provisioning tests require the exact derived history topic, one partition,
  infinite time/size retention without compaction, the active durability profile, and
  connector-only ACLs. Registration tests reject wrong-cluster bootstrap servers,
  conflicting schema-history client settings, history-producer drift, and public schema
  events. A broker-backed restart test recovers retained history and offsets; missing,
  unreadable, or empty-when-offsets-exist history remains not ready instead of being
  recreated silently.
- A coordinated-increase test first proves an over-budget record publishes nothing, fails
  the connector task, and makes readiness false. It then raises the operational policy in
  downstream-first order, restarts the same connector, and proves the record reaches the
  same topic under the unchanged binding generation before readiness recovers.
- Broker-backed integration tests enable Kafka authorization and prove ACL provisioning
  is repeatable, a configured instance consumer can read its own literal topic, and that
  principal is denied when it attempts to read a peer instance topic or the SQL Server
  schema-history topic. They prove the SQL Server connector principal can write and recover
  its own history but not another binding's history.
- Diagnostics identify infrastructure, REST, provider setup, projector completeness,
  connector catch-up, source-history `unknown`/`lost`, terminal latch, target/source binding,
  ACL authorization, and mismatch failures without secrets.

## Out of Scope

- Managed-Kafka-provider deployment automation.
- A production cross-replica/external-writer admission gate or transaction drain.
- Exact baseline-replacing repair or contract cutover after first-write admission.
- Recovery from provider source-history loss, including offset reset, same-topic resnapshot,
  or replacement-namespace cutover.
- Migration, upgrade, data movement, or initial CDC enablement for an already-provisioned
  database.
- Runtime target discovery, retirement, or source replacement.
- Projector health semantics.
- In-place source rebinding or topic-generation reuse.
