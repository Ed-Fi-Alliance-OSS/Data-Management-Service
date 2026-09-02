---
jira: DMS-1317
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
  - DMS-1318
---

# Story: Add DocumentCache Integration Coverage and Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design documents define behavior and operator constraints. This story adds
cross-feature evidence and implementation-specific guidance without restating them.

## Outcome

Validate the completed E18 capability across providers and publish DocumentCache operator
guidance. Representative DocumentCache performance qualification is deferred to a follow-up
performance ticket.

## Dependencies

- Depends on 18-00 through 18-06 and informs E19 operator documentation.

## Implementation Scope

- Add cross-story PostgreSQL and SQL Server fixtures for the completed projection feature.
- Cover transactional set-based enqueue, forced enqueue failure with complete canonical
  rollback, complete-transaction deadlock retry, test-only restricted canonical-writer
  trigger execution and direct-work-DML denial, disabled writes, projector-stopped writes,
  cascades, descriptors, SQL Server prerequisite validation, and guarded
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
- Keep bounded provider guards that are integration checks, including no-source-scan status
  and oldest-work observations; do not include the executable representative performance
  harness, threshold catalog, result validator, or qualification artifact contract in
  DMS-1317.
- Prove projection failure/backlog never gates canonical API routing.
- Publish operation and troubleshooting guidance for the shipped commands, configuration,
  status, and telemetry.
- Cross-link E19 procedures where connector or downstream state becomes relevant.

## Acceptance Evidence

- The provider integration matrix covers every E18 `CDC-INV-*` contract assignment not
  already proven in a narrower story suite.
- The workflows described by the runbooks are covered by integration tests against the
  implemented commands and status output. Runbook text and command examples are reviewed
  manually.
- Runbooks explain persistent failure remediation, enqueue-vs-processing availability,
  lifecycle mismatch, activation/deactivation, rebuild, set-latch routing to cache-ahead
  recovery or containment, scrub admission and rejection, reset recovery, and where
  production-scale performance qualification remains deferred.
- Runbooks require an explicit scrub after suspected restore or unsupported direct
  mutation before operators rely on queue-empty caught-up status.
- Runbooks limit correction and restart after SQL Server prerequisite initialization
  failure to lifecycle `Disabled`, define any other lifecycle as an unsupported incident
  with no v1 recovery or renewed-readiness guarantee, cover correction and retry after
  activation-preflight failure, and state that changing RCSI or `nested triggers` after
  successful validation is outside the supported v1 contract.
- Runbooks link to the owning design sections for contracts, recovery constraints, and
  deferrals instead of copying them.
- DMS-1317 documentation must not claim completed production-scale performance
  qualification, representative thresholds, executable harness output, result validation
  schema, or committed qualification artifacts.

## Implementation Note: CMS Relational Provider Metadata

Hosted integration through the real Configuration Service exposed a prerequisite gap in the
completed 18-01 target-selection path. The 18-01 design requires resolved data-store metadata
to carry an explicit normalized `postgresql` or `sqlserver` provider token and states that real
CMS provider-mismatch integration is not taskable until CMS exposes that metadata. CMS did not
yet persist or return the token, so DMS-1317 could not validate the production target-resolution
path required by its cross-provider integration scope.

This story therefore retains the narrow CMS contract needed to make that integration path real:

- Add nullable `Provider` metadata to the CMS data-store schema, commands, responses, and both
  provider repositories.
- Preserve `null` for backward compatibility and as the update meaning of "not supplied"; an
  existing provider remains unchanged when an update omits the property.
- When supplied, require the already-normalized token `postgresql` or `sqlserver`. Reject empty,
  whitespace, differently-cased, and unknown values so CMS cannot persist ambiguous metadata
  that silently makes a DocumentCache target ineligible.
- Limit the change to carrying and validating provider identity. Provider compatibility,
  target eligibility, and mismatch diagnostics remain owned by the existing 18-01 DMS logic.

Although this is production integration code rather than test-only code, it directly closes the
explicit 18-01 prerequisite discovered by DMS-1317's hosted coverage. Splitting it would leave
this story unable to exercise the shipped CMS-to-DMS path it is required to validate.

## Implementation Note: Config MSSQL E2E Startup Hardening

The PR's GitHub Actions failure was in the Configuration Service MSSQL E2E
startup path, before any DocumentCache integration test executed. SQL Server
2025 failed during container startup on the newer GitHub-hosted runner image,
leaving `dms-mssql` unhealthy and preventing `ed-fi-api-config-service` from
starting. The failing lane used `eng/docker-compose/start-local-config.ps1`
with `eng/docker-compose/mssql.yml`; it did not use the DMS workflow's
hardened `.github/actions/start-mssql-test-container/action.yml`.

The durable low-scope fix hardens only the config MSSQL E2E compose path:

- `eng/docker-compose/mssql-tmpfs.yml` mirrors the DMS MSSQL action's CI
  resource shape with `MSSQL_AGENT_ENABLED=true`, `MSSQL_MEMORY_LIMIT_MB=4096`,
  `MSSQL_CONTAINER_MEMORY=10g`, `MSSQL_TMPFS_SIZE=4g`, and tmpfs-backed
  `/var/opt/mssql`.
- `.env.config.mssql.e2e` and `.env.config.mssql.multitenant.e2e` opt into
  that override with `MSSQL_USE_TMPFS=true`, so ordinary local MSSQL compose
  starts keep their persistent `dms-mssql-2025` volume.
- `start-local-config.ps1` now starts the MSSQL `db` service first, waits for
  `sqlcmd` readiness, and only then starts `keycloak` and `config`, avoiding a
  full-stack compose abort when SQL Server needs an early restart window.
- `eng/docker-compose/tests/ConfigMssqlComposeStartup.Tests.ps1` guards this
  contract and is registered in both relevant PR Pester lanes.

## Not Assigned to This Story

- Kafka infrastructure, connector, and consumer operation are assigned to E19.
- Representation restamp implementation, integration coverage, and operator guidance are
  assigned independently to DMS-1318 / story 18-08 and are not dependencies of this story.
- The executable representative DocumentCache performance harness, threshold catalog,
  result validation schema, representative runbook, production performance runs,
  pass/fail decisions, durable-baseline-cursor performance escalation evidence, and
  committed qualification artifacts are assigned to follow-up performance work.
