---
jira: DMS-1317
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design documents define behavior and operator constraints. This story adds
cross-feature evidence and implementation-specific guidance without restating them.

## Outcome

Validate the completed E18 capability across providers, publish DocumentCache operator
guidance, and deliver the performance-qualification harness/runbook contract. Actual
representative performance runs and committed result artifacts are assigned to a follow-up
ticket.

## Dependencies

- Depends on 18-00 through 18-06 and informs E19 operator documentation.

## Implementation Scope

- Add cross-story PostgreSQL and SQL Server fixtures for the completed projection feature.
- Cover transactional set-based enqueue, forced enqueue failure with complete canonical
  rollback, complete-transaction deadlock retry, test-only restricted canonical-writer
  trigger execution and direct-work-DML denial, disabled writes, projector-stopped writes,
  cascades, descriptors, restamp, SQL Server prerequisite validation, and guarded
  new-empty activation including prerequisite failure and racing inserts.
- Cover current source/cache/work classification, stale-candidate suppression,
  candidate-independent `S = C = W` acknowledgement, cache-ahead-only latching, blocked
  work mismatches, conditional scrub/rebuild-page repair, enqueue/ack races, delete,
  direct fill, multiple workers, and crash windows.
- Cover fair poison traversal, restart without source scan, long outage, offline
  activation/deactivation, online rebuild and its fail-closed set-latch rejection,
  including unchanged lifecycle, cache, work, and latch state; exact-identity
  administrative exclusion across aliases and SQL Server caller principals,
  different-database concurrency, session loss, `Resetting` crashes, operation-specific
  bounded clearing, internal-only cache-ahead recovery, rejection and evidence
  preservation when publication is possible or uncertain, rejection of simple toggles
  for active/historical downstream state, clear-latch `Tracking` admission and fail-closed
  rejection for the explicit O(N) scrub, concurrent baseline deletes, and poison failures
  exhausting seeding capacity.
- Add the performance-qualification harness, threshold catalog, validator, and runbooks
  for interrupted baseline/rebuild restart, provider load/log/queue-DML limits,
  no-source-scan status observations, and durable-baseline-cursor escalation.
- Use bounded provider guards and documented representative-run requirements to prevent
  unreviewed scale regressions in this story. Actual PostgreSQL and SQL Server
  representative performance runs, pass/fail decisions, and committed
  `reference/document-cache/qualification-results/<run-id>/` artifacts are not part of
  DMS-1317.
- Prove projection failure/backlog never gates canonical API routing.
- Publish operation and troubleshooting guidance for the shipped commands, configuration,
  status, and telemetry.
- Cross-link E19 procedures where connector or downstream state becomes relevant.

## Acceptance Evidence

- The provider integration matrix covers every E18 `CDC-INV-*` contract assignment not
  already proven in a narrower story suite.
- Runbook steps are exercised against the implemented commands and status output.
- Runbooks explain persistent failure remediation, enqueue-vs-processing availability,
  lifecycle mismatch, activation/deactivation, rebuild, set-latch routing to cache-ahead
  recovery or containment, scrub admission and rejection, reset recovery, and how
  provider-specific performance/maintenance evidence is produced and validated by the
  follow-up performance ticket.
- Runbooks require an explicit scrub after suspected restore or unsupported direct
  mutation before operators rely on queue-empty caught-up status.
- Runbooks limit correction and restart after SQL Server prerequisite initialization
  failure to lifecycle `Disabled`, define any other lifecycle as an unsupported incident
  with no v1 recovery or renewed-readiness guarantee, cover correction and retry after
  activation-preflight failure, and state that changing RCSI or `nested triggers` after
  successful validation is outside the supported v1 contract.
- Runbooks link to the owning design sections for contracts, recovery constraints, and
  deferrals instead of copying them.
- Performance-qualification documentation, script entry points, threshold catalog, and
  result validation schema are present, and no DMS-1317 documentation claims completed
  representative-scale qualification without committed result artifacts.

## Not Assigned to This Story

- Kafka infrastructure, connector, and consumer operation are assigned to E19.
- Running the representative PostgreSQL and SQL Server performance qualification and
  committing validated `reference/document-cache/qualification-results/<run-id>/`
  artifacts are assigned to a follow-up performance ticket.
