---
jira: DMS-1323
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add Explicit Local/Bootstrap Connector Registration

## Design References

- **Enablement and initial readiness sequence**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence
- **V1 readiness scope**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope
- **Local bootstrap and CI**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci
- **Connector topology and provider setup**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup
- **Deployment-owned physical source binding**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding
- **Source-history continuity**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity

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
- While canonical write admission is closed, integrate new-database evidence, reject a
  nonempty canonical/cache/work database rather than attempting CDC retrofit, atomically
  create or exact-match the immutable binding, and then invoke or recognize the completed
  guarded `Disabled -> Tracking` transition before the first seed/API write according to
  the retry classification.
- Configure and validate the matching DMS target, start queue processing, wait for
  projection caught-up status, cross the provider heartbeat barrier, and require a second
  caught-up observation before opening admission.
- Integrate provider setup, binding lifecycle, and connector rendering. DMS startup itself
  never enables tracking, and mutable projection/CDC state stays outside the bootstrap
  manifest.
- Wire CDC-owned downstream-publication-history evidence into the E18 DocumentCache
  administrative command gate by providing and registering the production
  `IDocumentCacheDownstreamPublicationHistoryProvider` bridge. The bridge must report
  `internalOnly` only when durable CDC binding/source-history evidence proves the same
  normalized target key and physical-source fingerprint were internal-only; `active`,
  `historical`, `possible`, `unknown`, missing, or mismatched evidence must keep the E18
  commands rejected with no mutation.
- Add cluster-scoped Kafka Connect offset-store provisioning/validation and binding-scoped
  Kafka topic, durability, record-size, and ACL provisioning/validation.
- Add Kafka Connect registration, live validation, status polling, restart, guarded
  adoption/source replacement, and teardown operations.
- Expose the same workflow to the E2E harness.

## Acceptance Evidence

- Script and integration tests cover the setup, retry, rejection, timeout, restart,
  guarded lifecycle, and teardown cases defined by the integration design.
- Partial/retry tests prove the binding is durable before guarded activation: an exact
  binding with lifecycle `Disabled` and a clear latch retries activation; an exact binding
  with lifecycle `Tracking`, a clear latch, and empty tables resumes setup; and a set
  cache-ahead latch, unbound `Tracking`, any other lifecycle, a binding mismatch, or
  unexpected pre-capture rows fail closed and require cleanup/reprovisioning as applicable.
  They also cover queue drain, provider barrier, and second caught-up observation
  interruptions.
- Broker-backed tests cover the shared Connect offset store's compaction, durability, and
  worker-only ACLs plus binding-topic policy, record-size, connector, offset, heartbeat, and
  image validation.
- Provider tests cover the initial readiness and post-enablement lifecycle paths for
  PostgreSQL and SQL Server.
- Production-path tests prove the E18 `activate-offline`, `deactivate-offline`, and
  `recover-cache-ahead` commands no longer receive the default `unknown` downstream
  history when trusted CDC evidence proves `internalOnly`, and still reject active,
  historical, possible, unknown, missing, or mismatched evidence without mutation.
- Diagnostics tests cover each implementation boundary without exposing secrets.

## Not Assigned to This Story

- Managed-provider-specific deployment automation is deployment work.
- Projector behavior is assigned to E18; message behavior is owned by the ADR and tested in
  19-05.
