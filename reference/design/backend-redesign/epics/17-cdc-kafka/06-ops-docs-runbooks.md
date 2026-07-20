---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Description

Document how to operate relational DMS CDC/Kafka streaming in local and production-like deployments.

The runbooks should make clear that Kafka CDC is optional, `dms.DocumentCache` is conditionally required when
CDC is enabled, and the stream is a compacted document-state topic rather than a complete event history.
They should also document the source split: cache upserts carry payload, authoritative
`dms.Document` deletes carry lifecycle, and cache deletion/rebuild never means domain
deletion.

## Acceptance Criteria

- Documentation explains the relational CDC architecture and links to the three DMS-1245 decision records.
- Local setup documentation covers:
  - starting Kafka/Kafka Connect,
  - enabling CDC explicitly,
  - connector registration,
  - finding the instance topic,
  - using Kafka UI to inspect records.
- PostgreSQL production guidance covers:
  - logical replication requirements,
  - least-privilege replication user,
  - publication and replication slot naming,
  - two-table publication, `DocumentUuid` key setup, and `dms.Document`
    `REPLICA IDENTITY FULL`,
  - connector restart and slot cleanup.
- SQL Server production guidance covers:
  - database/table CDC enablement,
  - least-privilege connector login,
  - capture instance naming,
  - cleanup and retention considerations.
- Kafka guidance covers:
  - topic-per-instance ACLs,
  - deployment-unique production topic prefixes and the local-only `edfi.dms` default,
  - compacted topic behavior,
  - tombstone retention,
  - `etag` as the opaque DMS API `_etag` derived from `contentVersion` and the Kafka
    document-state `variantKey`,
  - consumer stale-write handling with `contentVersion`.
- Documentation explains the CDC-mode `dms.DocumentCache` guarantees:
  - exact zero-mismatch projection completeness before CDC readiness,
  - mismatch-count and oldest-mismatch-age health,
  - stale-write fencing by `ContentVersion`,
  - bounded in-memory retry and observational projection failures that never block API
    mutations.
- Documentation explains source-operation filtering and proves that cache deletion,
  truncation, and rebuild publish no domain tombstones.
- Documentation explains that canonical deletes come from `dms.Document`, including a
  delete with no prior cache row, and that both provider runbooks verify same-key ordering
  through the routed public topic.
- Runbooks cover connector health, lag, last error, snapshot completion, restart, offset reset, resnapshot, and
  topic recreation.
- Runbooks cover the explicit deployment-configured target list, repeating one-shot provisioning for each target,
  configuration/deployment changes for additions or removals, same-database credential/alias changes, connector
  retirement, and shared-physical-database conflict diagnostics.
- Runbooks explain that missing configured targets and confirmed physical-source drift set CDC readiness false and
  require coordinated deployment, while non-target additions and same-source connection-setting changes are not
  drift. None of these conditions alter normal request routing.
- Runbooks distinguish observing source drift from reconciliation: DMS does not move a projector/connector to a
  different physical source, call Kafka Connect, or clean up artifacts in response to a CMS refresh.
- Runbooks state that moving a `DataStoreId` to a different physical document set requires an explicit migration
  with a new topic/source generation or a deliberate topic/connector reset before resnapshotting.
- Runbooks state that removing a configured target never automatically deletes topics, offsets, ACLs, replication
  slots, or SQL Server capture state; each destructive cleanup action is explicit.
- Runbooks clearly mark offset reset, resnapshot, and topic deletion as destructive or replay-producing
  operations.
- Documentation explains that `-EnableKafkaUI` does not enable CDC.
- Documentation distinguishes CDC/Kafka from Change Queries and from API response streaming/writer
  implementation details.

## Tasks

1. Identify final documentation locations for local developer setup and production guidance.
2. Add architecture overview and decision-record links.
3. Add local setup instructions using the bootstrap CDC flag.
4. Add PostgreSQL setup and recovery runbook.
5. Add SQL Server setup and recovery runbook.
6. Add Kafka topic/ACL/consumer guidance.
7. Add troubleshooting guidance for persistent projection mismatches, bounded retry,
   two-table key/filter/order failures, missing targets, source identity resolution, and
   `CdcSourceDriftRequiresDeployment` remediation.
8. Add troubleshooting commands for connector status, connector logs, topic listing, and sample consumption.
9. Review documentation against the implemented scripts/templates before closing.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- SLA/SLO commitments.
- Consumer application implementation guides beyond the public message contract.
