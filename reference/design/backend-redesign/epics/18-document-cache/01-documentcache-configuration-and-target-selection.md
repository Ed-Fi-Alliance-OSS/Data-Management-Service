---
jira: DMS-1311
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Configuration and Target Selection

## Design References

- **Configuration and projection target selection**: reference/design/cdc-streaming.md#configuration-and-projection-target-selection
- **Projection administration**: reference/design/cdc-streaming.md#projection-administration
- **Durable lifecycle**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#durable-work-and-lifecycle
- **Projection health and deployment-owned CDC readiness**: reference/design/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness

The referenced design sections define target selection, validation, and lifecycle behavior.
This story is only the work package for implementing them.

## Outcome

Implement the DMS configuration and target-resolution layer used by the projection
workers, lifecycle administration, cache reads, and health reporting.

## Dependencies

- Configuration and target-resolution scaffolding may proceed alongside 18-00. Integrated
  lifecycle, activation-preflight, enqueue-trigger, and provider-prerequisite validation
  depends on the 18-00 schema.
- Supplies target contexts to E18 stories 18-04 through 18-06 and target observations to
  E19.

## Implementation Scope

- Add strongly typed configuration binding and validation.
- Add target normalization, resolution, refresh, and replacement lifecycle services.
- Validate durable lifecycle/work state and fail closed for configuration/database-state
  mismatch. Removing a target pauses processing without deleting work or disabling
  tracking.
- Define the guarded new-empty `Disabled -> Tracking` and repeatable offline
  activation/deactivation command/result contracts, including their eligibility and
  preflight classifications. The new-empty contract requires a one-time writer-blocking
  `dms.Document` lock, an exclusive state-row lock, clear-latch and empty
  canonical/cache/work checks, and racing-insert safety; 18-04 owns the shared
  administrative-mutex adapter and guarded workflow execution. Runtime target
  configuration never changes lifecycle. Require proof that the projection is
  internal-only and reject the simple toggle for an active or historical downstream
  consumer/CDC binding.
- Replace scan/audit tuning with queue poll, page, concurrency, backoff, seeding
  high-water, and direct-fill settings.
- Validate RCSI and server-level `nested triggers` for SQL Server. Runtime validation of a
  disabled or unreadable prerequisite fails only projection/cache eligibility, emits
  explicit diagnostics, and never changes the setting. The DMS-1310 write-side guard
  separately rejects affected indirect stamps in an enqueue-enabled lifecycle so they
  cannot commit without work.
- Add supported appsettings examples that link to the authoritative design.
- Add target-scoped diagnostics needed by health reporting.

## Acceptance Evidence

- Configuration, validation, and command-contract tests cover the states, requests, and
  preflight classifications enumerated by the referenced design sections.
- Provider integration tests cover target prerequisite validation and isolation.
- Preflight and contract tests cover legal/illegal transition requests, `Resetting`
  mismatch, target pause/resume, nonempty activation rejection, and
  active/historical-binding rejection.
- Appsettings and diagnostics are verified against the implementation and defer behavioral
  explanation to the design owner.

## Not Assigned to This Story

- Projector scheduling, cache reads, and health aggregation are assigned to later E18
  stories.
- The administrative-mutex adapter, guarded transition execution, and provider
  concurrency/session-loss tests are assigned to 18-04.
- Durable CDC binding and connector lifecycle are assigned to E19.
