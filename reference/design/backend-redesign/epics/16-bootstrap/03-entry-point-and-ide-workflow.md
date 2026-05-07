---
jira: DMS-1153
jira_url: https://edfi.atlassian.net/browse/DMS-1153
---

# Story: Bootstrap Entry Point and IDE Workflow

## Description

Implement the infrastructure-lifecycle phase command (`start-local-dms.ps1`) and document the IDE
debugging workflow consistent with the phase-oriented bootstrap contract. `start-local-dms.ps1` owns Docker
stack management and service health waiting; schema preparation, claims staging, instance configuration,
and seed delivery belong to their respective phase commands as specified in `command-boundaries.md`. The IDE
path reuses the same staged schema workspace and bootstrap ordering as the Docker-hosted path; it is not a
parallel schema-resolution or auth-initialization design. `configure-local-dms-instance.ps1` is the sole
phase that creates or selects target DMS instance IDs for the run; downstream phases receive those IDs via
the structured configure result and in-memory forwarding within a single wrapper invocation, or via
explicit `-InstanceId`/`-SchoolYear` selectors in a manual phase flow. Optional smoke-test credentials are
CMS-only pre-DMS work anchored to the selected target set, while the DMS-dependent continuation waits for an
IDE-hosted DMS endpoint only after instance configuration and schema provisioning have completed; when seed
loading is requested,
`load-dms-seed-data.ps1` is the next phase to invoke against the healthy endpoint using the resolved
instance IDs.
Within this story, the source story wording about "skip/resume" is satisfied by safe skip behavior and
optional same-invocation continuation only. It does not introduce a stopped-then-resume bootstrap contract
across separate invocations.

## Acceptance Criteria

- `start-local-dms.ps1` is the infrastructure-lifecycle phase command: it owns Docker stack management,
  service health waiting, and the `-InfraOnly`/post-provision `-DmsBaseUrl` behaviors documented in this
  story. It does not own schema preparation, claims staging, instance configuration, or seed delivery.
- The optional thin wrapper (`bootstrap-local-dms.ps1`) sequences the phase commands for the common
  happy-path developer workflow. It is convenience packaging only - not the normative bootstrap contract
  and not required; developers may invoke the phase commands directly. The wrapper forwards the
  developer-facing infrastructure and seed-source flags used by supported common-path runs, including
  `-EnvironmentFile`, `-IdentityProvider`, `-EnableKafkaUI`, `-EnableSwaggerUI`, `-SeedDataPath`, and
  `-AdditionalNamespacePrefix`, without becoming the owner of those concerns. The wrapper may expose a
  phase-owned parameter when the developer-facing happy path benefits from it.
- `-EnvironmentFile` is accepted by every phase command that contacts local services. Those commands use a
  shared local-settings helper to resolve CMS URL, identity provider, tenant scope, Docker-local DMS URL, and
  database defaults from the same env file. The wrapper forwards the same `-EnvironmentFile` value to
  `start-local-dms.ps1`, `configure-local-dms-instance.ps1`, `provision-dms-schema.ps1`, and
  `load-dms-seed-data.ps1` when those phases run.
- The story-aligned bootstrap flow stays consistent:
  - Config Service remains part of the canonical flow,
  - `-EnableConfig` is not treated as a meaningful developer-facing opt-out,
  - every non-teardown bootstrap run starts Config Service, including the default no-argument flow and
    keycloak-backed runs.
- `-InfraOnly` excludes the DMS container from Docker startup. Together with the optional `-DmsBaseUrl`,
  it defines the two wrapper workflow shapes; `start-local-dms.ps1` only consumes `-DmsBaseUrl` at the
  post-provision DMS-start/health-wait point.
- Story 03 documents two wrapper workflow shapes, with the pre-DMS-stop path as the primary recommended IDE
  workflow:
  - **Primary**: `-InfraOnly` without `-DmsBaseUrl` completes the pre-DMS phase only — infrastructure
    startup, instance creation or narrow existing-instance reuse, optional CMS-only smoke-test credentials,
    schema provisioning or validation, and next-step guidance for the IDE-hosted DMS launch. Developers
    manually start DMS in their IDE after this invocation completes.
  - **Optional convenience**: `-InfraOnly` with `-DmsBaseUrl` set completes the same pre-DMS phase first,
    then automatically waits for the IDE-hosted DMS process to become healthy. The wrapper must not forward
    `-DmsBaseUrl` to the initial infrastructure-only invocation; it carries the value until after instance
    selection and schema provisioning. Any later DMS-dependent step is owned by wrapper orchestration or by
    the next explicit phase command. If seed loading is requested, `load-dms-seed-data.ps1` is the next
    phase to invoke; the wrapper forwards the same `-DmsBaseUrl` value to that phase as the BulkLoadClient
    base URL and forwards the selected `-IdentityProvider` so the seed phase resolves the matching token
    endpoint, along with `-AdditionalNamespacePrefix` when provided and the instance set already selected by
    `configure-local-dms-instance.ps1`. This keeps
    `start-local-dms.ps1` limited to infrastructure lifecycle and health waiting while preserving a
    practical convenience handoff for developers who want to seed through an IDE-hosted DMS process.
- `-InfraOnly` without `-DmsBaseUrl` is terminal for that `start-local-dms.ps1` invocation. It is not a
  checkpoint or later resume mechanism for that command's unfinished work.
- Story 03's accepted reading of "resume" is same-invocation wrapper sequencing selected up front with
  `-InfraOnly -DmsBaseUrl`. It does not define a persisted resume mechanism for a later
  `start-local-dms.ps1` run.
- On `start-local-dms.ps1`, `-DmsBaseUrl` selects the external IDE-hosted DMS endpoint used in that
  continuation flow and is valid only with `-InfraOnly`, after the selected instances and target databases
  are ready. The seed phase has its own `-DmsBaseUrl` input for the BulkLoadClient target endpoint.
- `-DmsBaseUrl` implies automatic health wait with a clear timeout failure if the IDE-hosted process never
  becomes healthy; there is no separate health-wait flag in the public contract.
- If the school-year workflow needs DMS-dependent work such as seed loading under the IDE-hosted process,
  that work still belongs to `load-dms-seed-data.ps1`, requires a healthy DMS endpoint, and receives the
  IDE URL through its own `-DmsBaseUrl` input and the auth provider through its own `-IdentityProvider`
  input. Story 03 does not define a later multi-year bootstrap-resume mechanism from the stopped pre-DMS
  shape.
- `-AddSmokeTestCredentials` is a CMS-only operation that runs after CMS readiness and instance selection.
  It is valid with `-InfraOnly` even when `-DmsBaseUrl` is not set.
- `configure-local-dms-instance.ps1` is the only phase that creates or confirms DMS instance
  records. It emits a structured success-pipeline result containing selected instance IDs and, when
  available, `CMSReadOnlyAccess` credential details from local identity setup, with human-readable text on
  separate streams. When the thin wrapper orchestrates a full run, it captures those IDs in memory and
  forwards them as internal
  `-InstanceId` arguments to downstream phases within the same invocation. The wrapper does not expose
  `-InstanceId` as a public parameter and never parses prose to recover IDs or credentials. When phase
  commands are run separately by a developer, they resolve target instances through their own explicit
  selectors (`-InstanceId <long[]>` or `-SchoolYear <int[]>`) via a CMS-backed lookup. No disk artifact is
  written for downstream instance targeting.
- Downstream selector resolution rule: when exactly one DMS instance exists in CMS and no selector is
  supplied, auto-select it; when multiple instances exist without an explicit selector, fail fast with a
  non-zero exit and guidance to supply `-InstanceId` or `-SchoolYear`.
- `-NoDmsInstance` is only a narrow manual reuse escape hatch:
  - valid only when exactly one existing instance is present in the current tenant scope,
  - invalid with `-SchoolYearRange`,
  - ambiguous reuse fails fast with teardown or manual-environment-preparation guidance instead of a second
    target-selection subsystem.
- The narrowed DMS-916 definition of `-NoDmsInstance` is documented as a deliberate breaking change for
  existing fresh-stack scripts; migration guidance tells users to rerun without the flag or pre-create the
  intended single target instance. See [`bootstrap-design.md` Section 15](../../design-docs/bootstrap/bootstrap-design.md#15-breaking-changes-and-migration-notes)
  for the consolidated migration reference.
- Story 03 keeps CMS-only smoke credentials separate from DMS-dependent `SeedLoader` credentials and seed
  loading; no acceptance criterion describes one blended post-start credential-bootstrap phase.
- Developer-facing IDE guidance exists for the localhost configuration values required to run DMS against the
  Docker-managed infrastructure.
- `start-local-dms.ps1` owns provisioning the dev-only `CMSReadOnlyAccess` client through the
  provider-specific local identity setup path. `configure-local-dms-instance.ps1` may validate and report
  the local-development credential used by IDE-hosted DMS to query CMS, but it does not create or scope
  that client.
- Do not implement `CMSReadOnlyAccess` through `/connect/register` unless that endpoint supports read-only
  scope selection; the current registration path creates admin-scoped clients.
- The design documents one localhost contract for that dev-only `CMSReadOnlyAccess` client so the IDE
  workflow is deterministic, while leaving the exact local secret value as identity setup output or guidance
  rather than a story-level invariant. Broader client lifecycle behavior remains outside this story.
- That IDE guidance scopes the starter `appsettings.Development.json.example` to the common single-instance
  flow and documents the self-contained `-SchoolYearRange` variant's
  `/connect/token/{schoolYear}` behavior separately.
- That IDE guidance explicitly uses the staged schema workspace:
  - `AppSettings__UseApiSchemaPath=true`,
  - `AppSettings__ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema`.
- The same staged schema files are used for `dms-schema hash`, Docker-hosted DMS, and IDE-hosted DMS.
- `provision-dms-schema.ps1` remains responsible for validating the selected target databases against the
  exact selected schema set before any DMS-dependent continuation work begins.
- When `start-local-dms.ps1` starts the DMS container in the story-aligned Docker flow, it must ensure the
  legacy `NEED_DATABASE_SETUP` / `EdFi.DataManagementService.Backend.Installer.dll` startup provisioning path
  is disabled or removed. Docker-hosted DMS starts only after Story 01 provisioning succeeds.
- The documented self-contained identity-provider sequence matches repo dependencies:
  - PostgreSQL starts before `setup-openiddict.ps1 -InitDb`,
  - self-contained identity runs `setup-openiddict.ps1 -InsertData` after Config Service reaches the
    DMS-916 readiness gate,
  - Keycloak identity does not run `setup-openiddict.ps1 -InsertData`.
- Story 03 treats Config Service readiness as a claims-ready gate for the selected staged inputs. Instance
  configuration and all later phases begin only after CMS startup claim loading has completed.
- The readiness check is explicit: after `/health` is green, bootstrap proves CMS applied the staged claims
  content, preferably through a CMS load/composition result and otherwise through manifest-driven
  authorization metadata checks for the expected resource claim URIs and actions. Finding only
  `EdFiSandbox` is not enough.
- The IDE workflow remains Docker-first and does not introduce a second non-Docker bootstrap path.
- `eng/docker-compose/.bootstrap/` is treated as scratch bootstrap state and is excluded from source
  control.

## Tasks

1. Implement the story-aligned behaviors on `start-local-dms.ps1` as the infrastructure-lifecycle phase
   command: Docker stack management, service health waiting, idempotent `-InfraOnly` and post-provision
   `-DmsBaseUrl` workflow shapes, and the fail-fast parameter validation rules described in the Acceptance
   Criteria. Preserve `-EnvironmentFile` behavior and infrastructure-owned pass-through controls such as
   `-EnableKafkaUI` and `-EnableSwaggerUI`. Ensure the DMS container startup environment disables the legacy
   `NEED_DATABASE_SETUP` installer branch, or remove that branch from the story-aligned path. Do not add
   parameters owned by schema preparation, claims staging, instance configuration, or seed delivery to this
   command.
2. Put the story-aligned infrastructure-phase parameters on `start-local-dms.ps1` as specified in
   `command-boundaries.md` Section 3.3; parameters owned by other phase commands stay with those commands.
3. Implement the `-InfraOnly` and `-DmsBaseUrl` behaviors on the script-owned bootstrap surface so only two
   workflow shapes remain: pre-DMS stop, or automatic-health-wait continuation against the external DMS
   endpoint after instance selection and schema provisioning have completed. Keep optional smoke-test
   credentials in the CMS-only pre-DMS phase; the health-wait portion owned by `start-local-dms.ps1` ends
   when DMS health is confirmed, and any later DMS-dependent step is owned by wrapper orchestration or by
   the next explicit phase command. `load-dms-seed-data.ps1 -DmsBaseUrl <url>` is the next phase when seed
   loading is requested; pass `-IdentityProvider` to that phase when the running environment uses a
   non-default provider and `-AdditionalNamespacePrefix` when custom seed data requires additional vendor
   namespace authorization. Document that the stopped pre-DMS shape is a terminal
   invocation rather than a later resume checkpoint for `start-local-dms.ps1`. The next-step guidance
   printed at the pre-DMS termination point must give the developer the required IDE-hosted DMS
   configuration needed to start against the prepared environment, and it must not present a second
   `start-local-dms.ps1` invocation as a resume mechanism for skipped post-start work.
4. Keep the documented identity-provider ordering aligned with the real dependency chain for both Keycloak
   and self-contained auth, including the explicit rule that self-contained identity runs
   `setup-openiddict.ps1 -InsertData` after Config Service readiness while Keycloak skips that step, and keep
   the Config Service readiness contract aligned with claims loading: bootstrap does not proceed to
   instance configuration until CMS startup claim loading is complete for the selected staged inputs,
   verified by `/health` plus proof that the staged claims content was applied. Prefer a CMS load/composition result; otherwise use
   manifest-driven authorization metadata checks for the expected resource claim URIs and actions.
5. Keep bootstrap-time provisioning of the dev-only `CMSReadOnlyAccess` client in the provider-specific
   local identity setup path so the IDE-hosted DMS flow has a stable read-only CMS credential contract
   aligned to the documented localhost values, without expanding this story into broader CMS client
   lifecycle design.
6. Implement the instance-ID forwarding contract: `configure-local-dms-instance.ps1` emits a structured
   success-pipeline result containing selected instance IDs and, when available, `CMSReadOnlyAccess`
   credential details from local identity setup; the thin wrapper (`bootstrap-local-dms.ps1`) captures
   those IDs in memory during a single invocation and forwards them as internal `-InstanceId` arguments to
   `provision-dms-schema.ps1` and
   `load-dms-seed-data.ps1`. The wrapper must not expose `-InstanceId` as a public parameter and must not
   parse human-readable output. When phase commands are run separately, those commands resolve target
   instances through their own explicit `-InstanceId <long[]>` or `-SchoolYear <int[]>` selectors using a
   CMS-backed lookup. Selector resolution rule: auto-select when
   exactly one DMS instance exists and no selector is supplied; fail fast with a non-zero exit and
   corrective guidance when zero or multiple instances exist without an explicit selector. No run-context
   file is written.
7. Add a shared local-settings helper used by every phase command that contacts CMS, PostgreSQL, the local
   identity provider, or the Docker-local DMS endpoint. The helper reads the selected `-EnvironmentFile`
   using the same default env-file behavior as `start-local-dms.ps1`, and the wrapper forwards the same
   `-EnvironmentFile` value to all local-service phases.
8. Add or update the developer-facing IDE guidance, including the localhost configuration values and staged
   schema-path settings described in the main bootstrap design, including the local identity setup
   `CMSReadOnlyAccess` read-only OAuth client details and local-development credential value used by that
   flow.
9. Provide the starter configuration artifact for IDE use (`appsettings.Development.json.example`) so the
   documented workflow is actionable once the local-development credential is inserted from printed
   guidance or identity setup output, and remains aligned with the local identity setup
   `CMSReadOnlyAccess` client contract unless the provisioning scripts are intentionally changed.
10. Update the `-NoDmsInstance` rerun behavior to the narrowed DMS-916 escape hatch: valid only when exactly
   one existing instance is present in the current tenant scope and `-SchoolYearRange` is not set. Ambiguity
   or zero matches fail fast with teardown or manual-environment-preparation guidance. Later phases resolve
   their targets via the in-memory instance IDs forwarded by the wrapper (single-invocation path) or via
   explicit `-InstanceId`/`-SchoolYear` selectors (manual phase-flow path); they must not perform their own
   CMS instance creation, broad target-selection policy, or non-selector-driven discovery pass.
11. Carry the repo-local bootstrap-workspace lifecycle hygiene through this slice by keeping
   `eng/docker-compose/.bootstrap/` documented as scratch-only state. The `.gitignore` entry itself is owned
   by Story 00 because that story is the first slice that writes generated staging artifacts.
12. Document the narrowed `-NoDmsInstance` semantics as a deliberate breaking change for existing fresh-stack
   scripts and provide the migration guidance on the local bootstrap surface.

## Invocation Examples

The following examples show the two complementary invocation patterns. Neither is authoritative over the
other; the wrapper is happy-path convenience, the phase commands are the normative contract.

The remaining gaps in this story are implementation-readiness items only. The design contract for the
entry-point and IDE workflow is complete in this document even where script changes are still pending.

```powershell
# Wrapper — happy path (core schema, no seed)
pwsh eng/docker-compose/bootstrap-local-dms.ps1

# Wrapper - extension + seed
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -Extensions sample -LoadSeedData -SeedTemplate Minimal

# Wrapper - keycloak-backed happy path
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -IdentityProvider keycloak

# Wrapper - custom seed data with agency namespace authorization
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -LoadSeedData -SeedDataPath "./my-seeds/" -AdditionalNamespacePrefix "uri://state.example.org"

# Wrapper - multi-year
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -SchoolYearRange "2025-2026" -LoadSeedData -SeedTemplate Minimal

# Wrapper — IDE workflow (stop before DMS, print IDE configuration guidance)
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly

# Wrapper — IDE workflow (configure/provision first, then wait for IDE-hosted DMS)
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"

# Manual phase flow (core schema, single instance)
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
pwsh eng/docker-compose/configure-local-dms-instance.ps1   # emits structured selected-instance output
pwsh eng/docker-compose/provision-dms-schema.ps1           # auto-selects the one existing instance
pwsh eng/docker-compose/start-local-dms.ps1                # Docker-hosted DMS path
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedTemplate Minimal

# Manual phase flow (IDE-hosted DMS): wait for IDE DMS only after provisioning
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
pwsh eng/docker-compose/configure-local-dms-instance.ps1
pwsh eng/docker-compose/provision-dms-schema.ps1
# Start DMS in the IDE using the printed settings, then:
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"

# Optional manual seed phase through the IDE-hosted DMS process:
pwsh eng/docker-compose/load-dms-seed-data.ps1 -DmsBaseUrl "http://localhost:5198" -SeedTemplate Minimal

# Optional manual seed phase for a keycloak-backed IDE-hosted DMS process:
pwsh eng/docker-compose/load-dms-seed-data.ps1 -IdentityProvider keycloak -DmsBaseUrl "http://localhost:5198" -SeedTemplate Minimal

# Manual phase flow with a non-default env file: pass the same file to local-service phases
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/configure-local-dms-instance.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/provision-dms-schema.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/start-local-dms.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/load-dms-seed-data.ps1 -EnvironmentFile "./.env.local" -SeedTemplate Minimal

# Selector resolution: auto-select (one instance), explicit selector (multiple instances)
pwsh eng/docker-compose/provision-dms-schema.ps1                                             # auto-select
pwsh eng/docker-compose/provision-dms-schema.ps1 -InstanceId 1 # explicit
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026                      # year filter
# Multiple instances, no selector: non-zero exit with corrective guidance.
```

## Out of Scope

- Adding smoke, E2E, or integration test runner flags to the bootstrap surface.
- SDK generation as part of bootstrap.
- Creating a second bootstrap implementation that runs DMS and its supporting infrastructure entirely outside
  the Docker-first model.
- Creating a second schema-materialization path for IDE-hosted DMS.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 1, 5, 7, 9, 12, 13, and 14.1
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Section 3.3 (infrastructure-lifecycle phase contract)
