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

Expose per-data-store projection health, audit-backed exact completeness,
registration-prerequisite inputs, and sanitized telemetry from the latest completed full
audit and current process state.

## Dependencies

- Depends on 18-00, 18-03, and 18-07.
- Consumed by CDC stories 17-00, 17-03, and 17-06.

## Deliverables

1. Define per-data-store health/completeness and deployment aggregate models.
2. Record exact mismatch/age snapshots from completed provider-equivalent full audits,
   expose their observation time and age, and add configurable health thresholds without
   running a full anti-join synchronously on health reads.
3. Expose projector-side registration prerequisites without taking ownership of
   connector/source readiness.
4. Add the canonical structured logs and metrics.

## Acceptance Evidence

- Tests cover every selection reason, none/overlap, missing tables, zero/nonzero
  mismatches, lower-version gaps, oldest age, stale audits, known unresolved incremental
  work, nonzero-audit invalidation, persistent bounded failure, and mixed targets.
- Tests prove health reads reuse the latest audit snapshot and readiness requires a
  sufficiently recent exact-zero finishing audit with no known unresolved work.
- Tests distinguish diagnostic process timestamps from database completeness evidence.
- A metadata-invariant failure remains visible as projection failure but does not add a
  timestamp-based freshness condition.
- API integration proves individual and aggregate health remain observational.

## Out of Scope

- Durable progress/failure records.
- Connector status, source binding, delete capture, or ordering checks.
- External dashboard implementation.
