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
  - `DocumentUuid` replica identity/key setup,
  - connector restart and slot cleanup.
- SQL Server production guidance covers:
  - database/table CDC enablement,
  - least-privilege connector login,
  - capture instance naming,
  - cleanup and retention considerations.
- Kafka guidance covers:
  - topic-per-instance ACLs,
  - compacted topic behavior,
  - tombstone retention,
  - consumer stale-write handling with `contentVersion`.
- Runbooks cover connector health, lag, last error, snapshot completion, restart, offset reset, resnapshot, and
  topic recreation.
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
7. Add troubleshooting commands for connector status, connector logs, topic listing, and sample consumption.
8. Review documentation against the implemented scripts/templates before closing.

## Out of Scope

- Cloud-provider-specific managed Kafka instructions.
- SLA/SLO commitments.
- Consumer application implementation guides beyond the public message contract.
