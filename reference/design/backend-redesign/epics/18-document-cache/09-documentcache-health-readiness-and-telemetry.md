---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add DocumentCache Health, Readiness, and Telemetry

## Design References

- [Projection health and deployment-owned CDC readiness](../../../cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness)
- [Security, telemetry, and operations](../../../cdc-streaming.md#security-telemetry-and-operations)

## Outcome

Expose per-data-store projection health, audit-backed exact completeness, current-source
observation, and sanitized telemetry from the latest completed full audit and current
process state.

## Dependencies

- Depends on 18-00, 18-03, and 18-07.
- Consumed by CDC stories 17-00, 17-03, and 17-06.

## Deliverables

1. Define only the per-data-store projection health/completeness model, including target
   resolution and provider. Define the provider-specific algorithm that observes an
   opaque current physical-source fingerprint for the active execution context without
   retaining or comparing an expected value; deployment automation consumes the same
   observation contract.
2. Record exact unresolved/age snapshots from completed provider-equivalent full audits,
   with separate missing-row, cache-behind-row, and cache-ahead-invariant counts. Expose
   their observation time and age, and add configurable health thresholds without running
   a full anti-join synchronously on health reads.
3. Expose effective projector settings, next/due/overdue scheduling state, active work, and
   process-wide concurrency-gate waits. Keep health and readiness reads observational:
   they neither enqueue nor wait for audits.
4. Add the canonical structured logs and metrics without retaining an expected source
   binding, drift latch, connector state, or deployment aggregate.

## Acceptance Evidence

- Tests cover unresolved/resolved targets, a new fingerprint observation and health reset
  after connection-context replacement, missing tables, zero/nonzero differences,
  missing and cache-behind gaps, cache-ahead invariants, oldest age, stale audits, known
  unresolved incremental work, nonzero-audit invalidation, persistent bounded failure,
  and mixed targets.
- Tests prove health reads reuse the latest audit snapshot and readiness requires a
  sufficiently recent exact-zero finishing audit with no known unresolved work or
  cache-ahead invariant.
- Tests prove repeated health/readiness polling starts no audit work and accurately reports
  startup, due, overdue, running, coalesced, and concurrency-gated states.
- Tests prove a process-local cache-ahead observation remains unhealthy until a later
  source change or full audit establishes that the row is no longer ahead, and that the
  required restart audit re-establishes any persistent invariant.
- Tests distinguish diagnostic process timestamps from database completeness evidence.
- Provider tests prove equivalent connection aliases for one physical database produce
  the same opaque fingerprint, different databases produce different fingerprints, and
  no credential or unsanitized identifier is exposed.
- A metadata-invariant failure remains visible as projection failure but does not add a
  timestamp-based freshness condition.
- API integration proves per-database projection health remains observational.

## Out of Scope

- Durable progress/failure records.
- Connector status, durable or expected source binding, source comparison/drift latching,
  combined CDC readiness, deployment aggregation, delete capture, or ordering checks.
- External dashboard implementation.
