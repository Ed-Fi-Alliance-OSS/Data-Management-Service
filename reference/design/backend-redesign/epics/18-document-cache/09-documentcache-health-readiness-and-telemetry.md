---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Design References

- [Projection health and CDC readiness](../../../cdc-streaming.md#projection-health-and-cdc-readiness)
- [Security, telemetry, and operations](../../../cdc-streaming.md#security-telemetry-and-operations)

## Outcome

Expose per-data-store projection health, exact completeness, registration-prerequisite
inputs, and sanitized telemetry from current database and process state.

## Dependencies

- Depends on 18-00, 18-03, and 18-07.
- Consumed by CDC stories 17-00, 17-03, and 17-06.

## Deliverables

1. Define per-data-store health/completeness and deployment aggregate models.
2. Implement provider-equivalent mismatch/age queries and configurable health thresholds.
3. Expose projector-side registration prerequisites without taking ownership of
   connector/source readiness.
4. Add the canonical structured logs and metrics.

## Acceptance Evidence

- Tests cover every selection reason, none/overlap, missing tables, zero/nonzero
  mismatches, lower-version gaps, oldest age, persistent bounded failure, and mixed
  targets.
- Tests distinguish diagnostic process timestamps from database completeness evidence.
- A metadata-invariant failure remains visible as projection failure but does not add a
  timestamp-based freshness condition.
- API integration proves individual and aggregate health remain observational.

## Out of Scope

- Durable progress/failure records.
- Connector status, source binding, delete capture, or ordering checks.
- External dashboard implementation.
