---
jira: DMS-1317
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/cdc-streaming.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design documents define behavior and operator constraints. This story adds
cross-feature evidence and implementation-specific guidance without restating them.

## Outcome

Validate the completed E18 capability across providers and publish DocumentCache operator
guidance.

## Dependencies

- Depends on 18-00 through 18-06 and informs E19 operator documentation.

## Implementation Scope

- Add cross-story PostgreSQL and SQL Server fixtures for the completed projection feature.
- Cover transactional set-based enqueue, forced enqueue failure with complete canonical
  rollback, complete-transaction deadlock retry, least-privilege trigger execution,
  direct-work-DML denial, disabled writes, projector-stopped writes, cascades, descriptors,
  restamp, SQL Server nested-trigger fail-closed stamping, and guarded new-empty activation
  including racing inserts.
- Cover current source/cache/work classification, stale-candidate suppression,
  candidate-independent `S = C = W` acknowledgement, cache-ahead-only latching, blocked
  work mismatches, conditional scrub/rebuild-page repair, enqueue/ack races, delete,
  direct fill, multiple workers, and crash windows.
- Cover fair poison traversal, restart without source scan, long outage, offline
  activation/deactivation, online rebuild, administrative exclusion/session loss,
  `Resetting` crashes, operation-specific bounded clearing, internal-only cache-ahead
  recovery, rejection and evidence preservation when publication is possible or
  uncertain, rejection of simple toggles for active/historical downstream state, explicit
  scrub, concurrent baseline deletes, and poison failures exhausting seeding capacity.
- Qualify interrupted baseline/rebuild restart from the beginning at representative scale
  against predefined completion-time, database-load, and repeated queue-DML/write-
  amplification limits. If a limit fails, create the durable-baseline-cursor ticket and
  make it a production prerequisite.
- Prove operational-health/caught-up/oldest-work observations use no source scan at scale
  and that projection failure/backlog never gates canonical API routing.
- Publish operation and troubleshooting guidance for the shipped commands, configuration,
  status, and telemetry.
- Cross-link E19 procedures where connector or downstream state becomes relevant.

## Acceptance Evidence

- The provider integration matrix covers every E18 `CDC-INV-*` contract assignment not
  already proven in a narrower story suite.
- Runbook steps are exercised against the implemented commands and status output.
- Runbooks explain persistent failure remediation, enqueue-vs-processing availability,
  lifecycle mismatch, activation/deactivation, rebuild, scrub, reset recovery, and
  provider-specific performance/maintenance evidence.
- Runbooks require an explicit scrub after suspected restore or unsupported direct
  mutation before operators rely on queue-empty caught-up status.
- Runbooks link to the owning design sections for contracts, recovery constraints, and
  deferrals instead of copying them.

## Not Assigned to This Story

- Kafka infrastructure, connector, and consumer operation are assigned to E19.
