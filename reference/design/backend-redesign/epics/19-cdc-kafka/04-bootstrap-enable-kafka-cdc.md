---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- [Enablement and initial readiness sequence](../../../cdc-streaming.md#enablement-and-initial-readiness-sequence)
- [V1 maintenance-window assumptions](../../../cdc-streaming.md#v1-maintenance-window-assumptions)
- [Local bootstrap and CI](../../../cdc-streaming.md#local-bootstrap-and-ci)
- [Provider source-position barrier](../../../cdc-streaming.md#provider-source-position-barrier)
- [Deployment-owned physical source binding](../../../cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)

## Outcome

Add explicit, idempotent local topic/ACL provisioning and connector registration for a
selected configured target, using deployment-owned binding state, and document how
production-like deployment repeats the same readiness workflow with its durable state
backend.

## Dependencies

- Depends on 19-00 through 19-02, the published transform from 19-03, and the
  projection/readiness inputs named by 19-00.

## Deliverables

1. Add `-EnableKafkaCdc` and optional `-CdcBindingStatePath` to the appropriate
   local/bootstrap entry points while retaining Kafka UI as an independent option.
2. Reuse bootstrap data-store selection and generated provider connector templates.
3. Require the selected deployment target to be present in DMS
   `DocumentCache:Targets`, and reserve or exact-match its immutable binding before
   creating governed artifacts.
4. Create or validate the topic with exactly `cleanup.policy=compact`, an explicit
   per-topic `delete.retention.ms` of at least `604800000` (seven days), the binding's fixed
   partition count, and `max.message.bytes=<maxRecordBytes>` from the current operational
   policy. Reject any cleanup policy that includes `delete`, a missing topic-level
   tombstone-retention override even
   when the broker default is high enough, a value below the minimum, or any
   missing/conflicting size. Provision and idempotently validate literal, binding-scoped
   topic ACLs for the deployment-supplied connector and instance consumer principals, plus
   their required consumer-group ACLs; do not emit shared-topic, wildcard-topic, or
   cross-instance consumer grants.
5. Before connector registration, validate that the effective broker request,
   record-batch, and replica-fetch limits accept `maxRecordBytes`. Configure the local
   broker accordingly; require equivalent verifiable capability from a production-like
   broker and fail closed when it is smaller or cannot be verified. Document that each
   consumer must set `max.partition.fetch.bytes` and `fetch.max.bytes` to at least the
   operational ceiling and provision memory for one record.
6. Implement idempotent Kafka Connect create/update, external combined-status polling,
   timeout, and condition-specific diagnostics. Integrate the deployment-owned maintenance
   contract: block new canonical mutations, drain admitted requests and transactions,
   establish capture, and restart or roll out the selected DMS projector contexts so their
   immediate audit begins after the drain. Keep the gate closed through the post-audit
   barrier and open it only after combined readiness passes. Local and E2E bootstrap satisfy
   the gate by running before observed writes. Production-like automation uses its own
   mutation-admission/drain integration. Fail before registration if the worker
   does not run the deployment-pinned Ed-Fi image digest built from the required Debezium
   3.6 base or permit the required source-producer overrides. After registration, read
   back the connector configuration and reject drift from the required idempotence,
   acknowledgement, retry, maximum-in-flight, operational maximum-request and producer
   buffer-memory values, no-compression, binding `partitionerAlgorithm`,
   `errors.tolerance=none`, or provider heartbeat and `statistics.metrics.enabled=true`
   values. Reject a missing/unknown algorithm token or live partitioner configuration that
   does not implement `kafka-murmur2-v1`. Use the 19-00 provider adapter and connector-offset
   REST response for the post-audit barrier; do not infer catch-up from task status or lag.
   Treat a failed connector task as not ready regardless of offset or lag observations. ACL
   and record-size verification must complete before connector registration and before
   combined readiness can pass. A timeout leaves the target not ready and cannot open writes
   as ready; the window may remain open for retry or be explicitly aborted.
7. Implement a coordinated in-place `maxRecordBytes` increase without reserving a new
   binding generation or topic: mark the target not ready, confirm consumer fetch and
   deserialization capacity, raise broker/replica and topic limits, then raise producer
   `buffer.memory` to at least the new ceiling and `max.request.size` last and restart or
   resume the connector. Validate every effective value before restoring readiness; a
   partial or unverifiable rollout remains not ready.
8. Print sanitized binding-generation/connector/source/topic identity. Retain binding
   and artifacts on normal stop; remove artifacts before binding state during explicit
   destructive volume teardown. Also print the effective public-topic tombstone retention
   and the fixed 24-hour consumer-bootstrap deadline without claiming to certify an
   independently operated consumer.
9. Expose the same workflow to E2E setup before observed test traffic begins.

## Acceptance Evidence

- Script/integration tests cover disabled, UI-only, invalid or unprojected target,
  successful, repeated, timeout, normal-stop retention, missing binding around existing
  artifacts, maintenance/drain failure, and destructive teardown flows.
- Sequence tests prove binding reservation precedes artifact creation and that external
  combined readiness follows the authoritative maintenance-window algorithm without a
  backfill epoch. They reject a previous zero audit, keep mutation admission closed through
  a fresh post-drain startup/restart audit, and reopen it only after readiness passes. An
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
- A coordinated-increase test first proves an over-budget record publishes nothing, fails
  the connector task, and makes readiness false. It then raises the operational policy in
  downstream-first order, restarts the same connector, and proves the record reaches the
  same topic under the unchanged binding generation before readiness recovers.
- Broker-backed integration tests enable Kafka authorization and prove ACL provisioning
  is repeatable, a configured instance consumer can read its own literal topic, and that
  principal is denied when it attempts to read a peer instance topic.
- Diagnostics identify infrastructure, REST, provider setup, projector completeness,
  connector catch-up, target/source binding, ACL authorization, and mismatch failures
  without secrets.

## Out of Scope

- Managed-Kafka-provider deployment automation.
- Runtime target discovery, retirement, or source replacement.
- Projector health semantics.
- In-place source rebinding or topic-generation reuse.
