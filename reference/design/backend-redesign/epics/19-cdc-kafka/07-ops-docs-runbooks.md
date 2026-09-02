---
jira: DMS-1326
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced documents own the architecture, contracts, constraints, and deferrals. This
story documents the shipped implementation and must link to those owners rather than
restate them.

## Outcome

Publish verified operator guidance for the implemented relational CDC capability.

## Dependencies

- Depends on 18-07 and the completed E19 setup, status, and lifecycle tooling.
- Depends on the shipped downstream-publication-history provider behavior that either
  unlocks or intentionally keeps locked the E18 internal-only DocumentCache administrative
  commands.

## Implementation Scope

- Document local opt-in, production-like prerequisites, setup, observation, and
  troubleshooting for both providers.
- Document the shipped topic, connector, consumer, binding-state, security, retention,
  sizing, and telemetry operations.
- Document queue backlog/oldest work, poison failure remediation, lifecycle/configuration
  mismatch, activation/deactivation, `Resetting`, bounded rebuild, clear-latch `Tracking`
  admission for the explicit O(N) scrub, enqueue-failure diagnosis, and provider-specific
  per-write/queue-drain overhead.
- Document that current or historical CDC binding/consumer state disqualifies the simple
  read-acceleration activation/deactivation toggle in v1, and that stopping a connector or
  removing a runtime target is not clearing authority.
- Document the shipped availability of `activate-offline`, `deactivate-offline`, and
  `recover-cache-ahead`. These commands are usable only when the production
  downstream-history provider reports `internalOnly` for the same target and
  physical-source fingerprint; current or historical CDC binding/consumer state,
  `unknown`, missing, or mismatched evidence routes operators to CDC containment/recovery
  instead of simple read-acceleration toggles.
- State that projector downtime permits canonical writes to queue work, while enqueue
  failure rejects the complete canonical transaction. Projection status never gates
  ordinary API routing.
- Document only the implemented restart, recovery, containment, source-replacement, and
  destructive-retirement commands.
- Cross-link E18 projection/restamp guidance and the design-owned deferred workflows.
- Add documentation checks against command help, templates, status output, and test
  fixtures.

## Acceptance Evidence

- Runbook commands are exercised against the supported PostgreSQL and SQL Server workflows.
- Provider exercises cover RCSI/nested-trigger target and activation validation, guidance
  that post-validation changes to either setting are outside the supported v1 contract,
  `Disabled`-only initialization correction-and-restart, unsupported-incident
  classification without a renewed-readiness guarantee for any other lifecycle,
  activation correction-and-retry, restart without source scan, reset/rebuild crash
  recovery, and work-table capture exclusion.
- Documentation tests detect drift from the shipped configuration, status, and lifecycle
  surfaces.
- Documentation checks or exercised runbook scenarios cover both admitted `internalOnly`
  paths and rejected active, historical, possible, unknown, missing, or mismatched
  downstream-history evidence for the E18 command gate.
- Every behavioral, security, recovery, or compatibility statement links to its owning
  design section instead of reproducing its normative algorithm or value table.
- Destructive procedures are verified against the implemented guarded operations.

## Not Assigned to This Story

- Cloud-provider-specific instructions and consumer product implementation guidance are
  separate work.
- Design changes must be made in the owning documents, not in the runbook.
