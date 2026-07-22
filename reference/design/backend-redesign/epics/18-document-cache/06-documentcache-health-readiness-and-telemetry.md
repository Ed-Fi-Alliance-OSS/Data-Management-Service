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

Expose per-data-store projection health, an exact audit observation rather than continuous
completeness proof, current-source observation, and sanitized telemetry from the latest
completed full audit and current process state.

## Dependencies

- Depends on the 18-00 schema, 18-01 target configuration, 18-04 projector, and 18-05
  cache-backed read integration.
- Consumed by CDC stories 19-00, 19-04, and 19-07.

## Deliverables

1. Define only the per-data-store projection health/completeness model, including target
   resolution and provider. Read the active database's singleton
   `dms.DataStoreIdentity.SourceIdentity` and expose only the opaque current fingerprint
   defined by the authoritative design's exact provider-token/UUID/SHA-256 algorithm.
   Retain neither the source UUID nor an expected value; deployment automation consumes
   the reported fingerprint as an opaque current-source observation.
2. Record exact unresolved/age snapshots from completed provider-equivalent full audits,
   with separate missing-row, cache-behind-row, and cache-ahead-invariant counts. Read and
   expose the durable `DocumentCacheState.CacheAheadRecoveryRequired` latch alongside their
   observation time and age, and add configurable health thresholds without running a full
   anti-join synchronously on health reads. Missing/malformed latch state is unhealthy.
3. Expose effective projector settings, next/due/overdue scheduling state, active work,
   target-scoped repair-required/backoff state and next eligibility time, and process-wide
   concurrency-gate waits. Retain no failed document/version identities for health. Keep
   health and readiness reads observational: they neither enqueue nor wait for audits.
4. Add the canonical structured logs and metrics without retaining an expected source
   binding, source-drift latch, connector state, or deployment aggregate. The database
   cache-ahead safety latch is the only durable incident state in this story.

## Acceptance Evidence

- Tests cover unresolved/resolved targets, a new fingerprint observation and health reset
  after connection-context replacement, missing tables, zero/nonzero differences,
  missing and cache-behind gaps, cache-ahead invariants, oldest age, stale audits, known
  unresolved incremental work, nonzero-audit invalidation, persistent target-scoped
  failure/backoff with database rediscovery, and mixed targets.
- Tests prove health reads reuse the latest audit snapshot and readiness requires a
  sufficiently recent exact-zero finishing audit, a clear durable cache-ahead latch, no
  active unresolved work, and no target-scoped repair-required observation.
- Tests prove repeated health/readiness polling starts no audit work and accurately reports
  startup, due, overdue, running, coalesced, and concurrency-gated states.
- Tests prove a cache-ahead observation atomically sets the durable latch and remains
  unhealthy across later source equality, a zero audit, canonical deletion, health polling,
  and process restart. Only the explicit full-cache recovery transaction clears it.
- Tests distinguish diagnostic process timestamps from database completeness evidence.
- Tests make explicit that a zero audit is exact only at its finishing observation and that
  a late lower-version commit may remain undiscovered until the next full audit; E19 owns
  the initial new/offline-database workflow that uses a fresh startup audit before
  first-write admission. Neither this story nor E19 turns later projection health into
  another exact baseline or implements a production write gate.
- Shared conformance vectors pin the exact fingerprint bytes for both provider tokens.
  Provider tests prove equivalent connection aliases for one database read the same
  singleton and produce the same opaque fingerprint, independently provisioned databases
  produce different fingerprints, missing/malformed singleton state is unhealthy, and no
  source UUID, credential, or unsanitized identifier is exposed.
- A metadata-invariant failure remains visible as projection failure but does not add a
  timestamp-based freshness condition.
- API integration proves per-database projection health remains observational.

## Out of Scope

- Durable progress/failure records beyond the singleton cache-ahead safety latch.
- Connector status, durable or expected source binding, source comparison/drift latching,
  combined CDC readiness, deployment aggregation, delete capture, or ordering checks.
- External dashboard implementation.
