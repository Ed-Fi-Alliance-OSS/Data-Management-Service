---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Wire CDC Target Registration and Readiness Prerequisites

## Design References

- [Configuration and projection targets](../../../cdc-streaming.md#configuration-and-projection-target-selection)
- [Projection health and CDC readiness](../../../cdc-streaming.md#projection-health-and-cdc-readiness)
- [Physical source binding](../../../cdc-streaming.md#cdc-target-and-physical-source-binding)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Add a target-specific abstraction that distinguishes connector-registration
prerequisites from completed end-to-end readiness and combines projector signals with
E17-owned provider/source checks.

## Dependencies

- Consumes 18-00 target selection, 18-03 reconciliation, 18-07 fencing, and 18-09
  projection health.
- Does not implement the projector or connector registration.

## Deliverables

1. Bind explicit configured CDC targets into the effective projection set.
2. Implement `CanRegisterConnector` and end-to-end readiness evaluations for an explicit
   data-store execution context.
3. Resolve provider-specific physical database identity, detect alias conflicts, and
   retain startup source bindings for drift observation.
4. Validate provider tables, keys, replica/capture setup, and installed source-operation
   shaping before registration.
5. Emit sanitized, condition-specific diagnostics without changing request routing.

## Acceptance Evidence

- Tests cover empty, single, overlapping, duplicate-normalized, and unlisted target
  configurations.
- Provider tests cover equivalent physical aliases, conflicting targets, transient
  identity-resolution failure, missing targets, and latched source drift.
- Readiness tests cover registration-ready versus fully-ready transitions using the
  authoritative readiness sequence.
- API integration tests prove every reported CDC/projector failure remains observational,
  including deletion with unavailable cache state.

## Out of Scope

- Projector implementation.
- Kafka Connect REST registration.
- Publishing Kafka records.
