---
jira: DMS-1097
jira_url: https://edfi.atlassian.net/browse/DMS-1097
---

# Story: Remove Temporary Startup Validation Bypass After Provisioning Is Ready

> **Status: Complete.** The runtime bypass was already a no-op when this
> story was picked up — instance validation had been moved to
> `ValidateStartupInstancesTask` (gated only by `UseRelationalBackend`). The
> work shipped under this story removed the dead
> `AppSettings.ValidateProvisionedMappingsOnStartup` property, its
> configuration bindings (`appsettings.json`, both Docker Compose files), the
> operator README paragraph, and the dead test paths that referenced it.
> AC #1 and AC #3 below have been rewritten to reflect the shipped multi-
> instance-safe model documented in
> `ValidateStartupInstancesTask.cs:18-27`.

## Description

`DMS-1047` introduced startup-time PostgreSQL runtime mapping validation against
`dms.EffectiveSchema`, but the shared Docker startup paths used by local
development, the OpenAPI-spec workflow, and both E2E suites are not provisioned
yet. We are temporarily defaulting startup validation off so runtime mapping set
compilation still runs while DMS can complete startup.

This story removes that temporary bypass once the provisioning workflow and test
environment setup reliably create the required schema fingerprint metadata before
DMS startup validation executes.

Align with:
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`
  ("Schema Validation (EffectiveSchema)")
- `reference/design/backend-redesign/design-docs/ddl-generation.md`
  (create-only provisioning semantics)
- `reference/design/backend-redesign/design-docs/new-startup-flow.md`
  (startup phase ordering and fail-fast behavior)

## Acceptance Criteria

- When `USE_RELATIONAL_BACKEND=true`, operators must run
  `provision-relational-e2e-database.ps1` before `start-local-dms.ps1` so
  `dms.EffectiveSchema` and related fingerprint metadata exist before DMS
  startup validation executes. Provisioning remains a separate lifecycle task
  and is intentionally not coupled into the local startup script.
- The temporary startup bypass flag
  `AppSettings.ValidateProvisionedMappingsOnStartup` is removed entirely.
- Startup validation executes unconditionally. Missing, malformed, or
  mismatched provisioning metadata is logged with actionable remediation.
  Per-instance failures are cached and surface as 503 at request time while
  other instances continue serving (multi-instance-safe failure model — see
  `ValidateStartupInstancesTask.cs:18-27`).
- Automated coverage proves provisioning-ready environments pass with startup
  validation enabled (`PostgresqlEffectiveSchemaHashMismatchTests`,
  `MssqlEffectiveSchemaHashMismatchTests`) and that environments missing
  provisioning fail safely: per-instance startup handling is covered by
  `ValidateStartupInstancesTaskTests` (e.g.
  `Given_One_Bad_Instance_And_One_Good_Instance`), and the request-time 503
  surface is covered by
  `ValidateDatabaseFingerprintMiddlewareMissingTableTests.Given_Database_Is_Not_Provisioned`.

## Tasks

1. Update the shared startup environment and provisioning path so
   `dms.EffectiveSchema` and related fingerprint metadata are present before the
   backend mapping initialization phase runs.
2. Remove the temporary startup bypass from DMS runtime code and shared startup
   scripts, including local Docker and CI bootstrap paths.
3. Update startup diagnostics and failure-mode coverage so missing or mismatched
   provisioning metadata produces explicit, actionable errors.
4. Add or update automated tests proving the OpenAPI-spec workflow and both E2E
   startup paths succeed with startup validation enabled after provisioning is
   ready.
