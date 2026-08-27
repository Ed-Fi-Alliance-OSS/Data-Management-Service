---
jira: DMS-1319
source_spike: DMS-1245
epic: DMS-1309
related:
  - DMS-1246
---

# Story: Add Deployment-Owned CDC Binding and Readiness

## Design References

- [Authority and document ownership](../../design-docs/cdc/cdc-streaming.md#authority-and-document-ownership)
- [V1 readiness scope](../../design-docs/cdc/cdc-streaming.md#v1-readiness-scope)
- [Projection health and deployment-owned CDC readiness](../../design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Provider source-position barrier](../../design-docs/cdc/cdc-streaming.md#provider-source-position-barrier)
- [Source-partition hash](../../design-docs/cdc/cdc-streaming.md#source-partition-hash)
- [Source-history continuity](../../design-docs/cdc/cdc-streaming.md#source-history-continuity)
- [Deployment-owned CDC target and physical source binding](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
- [Connector topology and provider setup](../../design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup)
- [Enablement and initial readiness sequence](../../design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence)
- [Security, telemetry, and operations](../../design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations)
- [Projection operational health and CDC admission](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#projection-operational-health-caught-up-status-and-cdc-admission)
- [Kafka topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

The linked design documents exclusively own CDC behavior and recovery requirements. This
story is the implementation work package and evidence index for those requirements.

## Outcome

Add deployment-owned CDC state and status services that combine DMS projection health and
caught-up observations with provider, Kafka, and connector observations.

## Dependencies

- Consume target and projection observations from E18-S01 and E18-S06.
- Use the atomic projection path from E18-S03 and durable queue processing from E18-S04
  for integrated readiness scenarios.
- Consume provider artifacts from E19-S01 and connector configuration and offset
  observations from E19-S02.
- Supply binding, status, admission, retry, and provider-position services to E19-S04.

## Implementation Scope

- Add shared CDC control-plane contracts, validation, deterministic artifact identity,
  normalized observation models, and pure status, admission, retry, and continuity logic
  in Core.
- Add deployment-owned binding and incident state abstractions, guarded lifecycle
  operations, and the local filesystem state-store implementation.
- Add PostgreSQL and SQL Server source-position and source-history adapters in their
  provider backend assemblies.
- Integrate E18 projection observations and E19 provider/connector observations without
  adding CDC orchestration, provider provisioning, or Kafka/Connect mutation behavior.
- Add focused behavioral unit, provider integration, and API integration evidence.

## Acceptance Evidence

The authoritative [contract-to-evidence traceability](../../design-docs/cdc/cdc-streaming.md#contract-to-evidence-traceability)
maps the applicable CDC invariants to this story and its sibling work packages. DMS-1319
is evidenced by these behavioral suites:

- Core CDC contract, validation, state-store, status, admission, retry, continuity,
  artifact-name, source-position, and privacy suites under
  [`DocumentCache/Cdc`](../../../../../src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/DocumentCache/Cdc/).
- [`PostgresqlCdcSourcePositionAdapterTests`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCdcSourcePositionAdapterTests.cs).
- [`MssqlCdcSourcePositionAdapterTests`](../../../../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCdcSourcePositionAdapterTests.cs).
- [`Given_DocumentCacheStatusEndpointProductionService`](../../../../../src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/DocumentCache/Given_DocumentCacheStatusEndpointProductionService.cs)
  and the other DocumentCache API integration suites in that directory.

## Not Assigned to This Story

- DMS projection implementation is assigned to E18.
- Provider object provisioning, connector rendering, Kafka and ACL changes, Connect REST
  orchestration, public message behavior, and end-to-end Kafka scenarios are assigned to
  E19-S01 through E19-S06 as mapped by the authoritative traceability table.
- Operator command and transport wiring, including source-replacement orchestration, is
  assigned to E19-S04.
