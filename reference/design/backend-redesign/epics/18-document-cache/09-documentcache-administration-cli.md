---
jira: DMS-1428
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
  - DMS-1311
  - DMS-1314
  - DMS-1316
  - DMS-1317
  - DMS-1323
---

# Story: Add a DocumentCache Administration CLI

## Design References

- **Administrative serialization and state-row fencing**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#administrative-serialization-and-state-row-fencing
- **Baseline, rebuild, deactivation, and scrub**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#baseline-rebuild-deactivation-and-scrub
- **Projection administration**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration
- **Security, telemetry, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations

The referenced design sections and the E18 implementation stories own the lifecycle
semantics. This story adds the supported command-line operator surface over those existing
administrative services.

## Outcome

Deliver a supported non-interactive DocumentCache administration CLI for PostgreSQL and SQL
Server targets. The CLI must let operators inspect target status and run the existing
DocumentCache administrative commands, including online cache rebuild, without starting an
ad hoc DMS web process or duplicating provider-specific rebuild logic.

## Dependencies

- Depends on 18-01 for stable JSON-facing administrative contracts and target-key
  serialization.
- Depends on 18-04 for the command runner, administrative mutex, lifecycle transitions,
  bounded clearing, baseline seeding, work draining, and failure classifications.
- Depends on 18-06 for status, health, caught-up, queue, and bounded diagnostic
  observations.
- Informs 18-07 and 19-07 operator runbooks. Coordinate naming and bootstrap integration
  with 19-04, but do not make Kafka connector setup part of this story.

## Implementation Scope

- Add a new .NET command-line application for DocumentCache administration, with a stable
  installed command name selected during tasking.
- Reuse the same provider adapters, target resolution, effective settings, command runner,
  administrative mutex, telemetry, and JSON contracts used by DMS runtime services. Do not
  implement separate SQL-only lifecycle or rebuild paths in the CLI.
- Load configuration from the normal DMS configuration sources plus explicit command-line
  overrides needed for non-hosted execution. Resolve configured targets by
  `tenantKey`/`dataStoreId` using the same target registry semantics as DMS.
- Support read-only target inspection for lifecycle, cache-ahead latch, provider
  eligibility, physical source fingerprint, queue presence, oldest work, active command,
  last-ended diagnostics, and bounded document-scoped failure diagnostics.
- Support the existing administrative commands: guarded new-empty activation, offline
  activation, offline deactivation, online cache rebuild, explicit integrity scrub, and
  internal-only cache-ahead recovery.
- Preserve the existing request/result DTOs and lower-camel JSON enum values for `--json`
  input and output. Human-readable output may be added, but automation must be able to
  consume stable JSON without parsing prose.
- Require explicit non-interactive acknowledgement for destructive or writer-fenced
  commands, including exact `offlineWriterAdmission` confirmation tokens where the existing
  command contracts require them.
- Expose `expectedPhysicalSourceFingerprint` as an optional guard on every mutating
  command that supports it, and fail closed on mismatch before mutation.
- Publish a stable exit-code mapping for completed, rejected-no-mutation,
  failed-no-mutation, incomplete-retryable, argument, configuration, and unexpected
  failures.
- Handle cancellation, command timeouts, provider command timeouts, mutex acquisition
  cancellation, session loss, and retryable incomplete results without reconnecting under
  presumed mutex ownership.
- Emit sanitized structured logs and metrics consistent with the runtime projection and
  administration meters. Never write connection strings, secrets, unsanitized target input,
  or unbounded document identifiers to routine output.
- Add command help, examples, and runbook cross-links for safe activation/deactivation,
  online rebuild, reset/rebuild crash retry, cache-ahead recovery routing, and persistent
  poison remediation.

## Acceptance Evidence

- CLI parser and serialization tests pin command names, options, required confirmations,
  `--json` request/result shapes, lower-camel enum values, and exit-code mapping.
- PostgreSQL and SQL Server integration tests execute the CLI against real target
  databases for status, guarded new-empty activation, online cache rebuild, offline
  activation/deactivation, explicit scrub admission/rejection, and internal-only
  cache-ahead recovery rejection/admission paths.
- Online rebuild tests prove the CLI invokes the shared 18-04 coordinator: pending work is
  preserved while cache is cleared, the baseline is bounded/backpressured, work drains
  before returning to `Tracking`, `Rebuilding` resumes without repeating cache clearing, and
  a set cache-ahead latch rejects with no lifecycle, cache, work, or latch mutation.
- Mutex tests prove two CLI invocations targeting aliases of the same physical database
  serialize through the shared provider mutex, while different physical databases can be
  administered concurrently according to the existing design.
- Cancellation, timeout, and session-loss tests prove mutated incomplete states return
  retryable results and that rerunning the same command revalidates durable state before
  resuming.
- Documentation tests exercise help output and shipped runbook commands so examples cannot
  drift from the implemented CLI.

## Not Assigned to This Story

- New lifecycle semantics, table shapes, queue algorithms, baseline cursor persistence, or
  cache writer behavior. Those remain owned by the existing E18 design and implementation
  stories.
- Kafka connector setup, connector teardown, source replacement, binding retirement, topic
  management, or CDC bootstrap orchestration. Those are E19 responsibilities.
- The representation restamp utility, which remains owned by 18-08.
- HTTP administration endpoints, dashboards, or cloud-provider-specific automation.
