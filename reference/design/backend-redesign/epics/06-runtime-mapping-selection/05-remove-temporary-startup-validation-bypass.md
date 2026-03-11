---
jira: DMS-1097
jira_url: https://edfi.atlassian.net/browse/DMS-1097
---

# Story: Remove Temporary Startup Validation Bypass After Provisioning Is Ready

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

- The provisioning path used by local Docker startup, the OpenAPI-spec workflow,
  the DMS E2E suite, and the Instance Management E2E suite creates
  `dms.EffectiveSchema` and related fingerprint metadata before DMS startup
  validation executes.
- The temporary startup bypass flag
  `AppSettings.ValidateProvisionedMappingsOnStartup` is removed entirely, or its
  default is restored to strict startup validation.
- Startup-time validation fails fast with actionable diagnostics when
  provisioning metadata is missing, malformed, or mismatched.
- Automated coverage proves provisioning-ready environments pass with startup
  validation enabled and no temporary bypass.

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
