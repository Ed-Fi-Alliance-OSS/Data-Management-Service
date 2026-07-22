---
jira: DMS-1323
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- **Enablement and initial readiness sequence**: reference/design/cdc-streaming.md#enablement-and-initial-readiness-sequence
- **V1 readiness scope**: reference/design/cdc-streaming.md#v1-readiness-scope
- **Local bootstrap and CI**: reference/design/cdc-streaming.md#local-bootstrap-and-ci
- **Connector topology and provider setup**: reference/design/cdc-streaming.md#connector-topology-and-provider-setup
- **Deployment-owned physical source binding**: reference/design/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding
- **Source-history continuity**: reference/design/cdc-streaming.md#source-history-continuity

The referenced design sections define eligibility, sequencing, topic policy, registration,
readiness, and lifecycle operations. This story is only the work package for implementing
them.

## Outcome

Add the explicit local/bootstrap CDC workflow and the deployment-controller operations
needed to provision, validate, start, stop, and retire a target.

## Dependencies

- Depends on 19-00 through 19-03 and the E18 projection/status inputs consumed by 19-00.

## Implementation Scope

- Add the local/bootstrap command surface and controller orchestration.
- Integrate new-database evidence, provider setup, projection startup, binding lifecycle,
  and connector rendering.
- Add cluster-scoped Kafka Connect offset-store provisioning/validation and binding-scoped
  Kafka topic, durability, record-size, and ACL provisioning/validation.
- Add Kafka Connect registration, live validation, status polling, restart, guarded
  adoption/source replacement, and teardown operations.
- Expose the same workflow to the E2E harness.

## Acceptance Evidence

- Script and integration tests cover the setup, retry, rejection, timeout, restart,
  guarded lifecycle, and teardown cases defined by the integration design.
- Broker-backed tests cover the shared Connect offset store's compaction, durability, and
  worker-only ACLs plus binding-topic policy, record-size, connector, offset, heartbeat, and
  image validation.
- Provider tests cover the initial readiness and post-enablement lifecycle paths for
  PostgreSQL and SQL Server.
- Diagnostics tests cover each implementation boundary without exposing secrets.

## Not Assigned to This Story

- Managed-provider-specific deployment automation is deployment work.
- Projector behavior is assigned to E18; message behavior is owned by the ADR and tested in
  19-05.
