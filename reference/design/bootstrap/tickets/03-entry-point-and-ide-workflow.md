---
design: DMS-916
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
in-memory forwarding within a single wrapper invocation, or via explicit `-InstanceId`/`-SchoolYear`
selectors in a manual phase flow. Optional smoke-test credentials are CMS-only pre-DMS work anchored to
the selected target set, while the DMS-dependent continuation on `start-local-dms.ps1` covers automatic
health wait only; when seed loading is requested, `load-dms-seed-data.ps1` is the next phase to invoke
against the healthy endpoint using the resolved instance IDs.
Within this story, the source story wording about "skip/resume" is satisfied by safe skip behavior and
optional same-invocation continuation only. It does not introduce a stopped-then-resume bootstrap contract
across separate invocations.

## Acceptance Criteria

- `start-local-dms.ps1` is the infrastructure-lifecycle phase command: it owns Docker stack management,
  service health waiting, and the `-InfraOnly`/`-DmsBaseUrl` behaviors documented in this story. It does
  not own schema preparation, claims staging, instance configuration, or seed delivery.
- The optional thin wrapper (`bootstrap-local-dms.ps1`) sequences the phase commands for the common
  happy-path developer workflow. It is convenience packaging only - not the normative bootstrap contract
  and not required; developers may invoke the phase commands directly. The wrapper forwards the
  developer-facing infrastructure and seed-source flags used by supported common-path runs, including
  `-IdentityProvider` and `-SeedDataPath`, without becoming the owner of those concerns.
- The story-aligned bootstrap flow stays consistent:
  - Config Service remains part of the canonical flow,
  - `-EnableConfig` is not treated as a meaningful developer-facing opt-out,
  - every non-teardown bootstrap run starts Config Service, including the default no-argument flow and
    keycloak-backed runs.
- `-InfraOnly` excludes the DMS container from Docker startup and, together with the optional
  `-DmsBaseUrl`, defines the two workflow shapes supported by the infrastructure-lifecycle phase command.
- Story 03 documents two workflow shapes, with the pre-DMS-stop path as the primary recommended IDE
  workflow:
  - **Primary**: `-InfraOnly` without `-DmsBaseUrl` completes the pre-DMS phase only — infrastructure
    startup, instance creation or narrow existing-instance reuse, optional CMS-only smoke-test credentials,
    schema provisioning or validation, and next-step guidance for the IDE-hosted DMS launch. Developers
    manually start DMS in their IDE after this invocation completes.
  - **Optional convenience**: `-InfraOnly` with `-DmsBaseUrl` set continues from that same pre-DMS phase
    by automatically waiting for the IDE-hosted DMS process to become healthy; `start-local-dms.ps1` stops
    after confirming DMS health. Any later DMS-dependent step is owned by wrapper orchestration or by the
    next explicit phase command. If seed loading is requested, `load-dms-seed-data.ps1` is the next phase
    to invoke, consuming the healthy endpoint and the instance set already selected in step 7. This keeps
    `start-local-dms.ps1` limited to infrastructure lifecycle and health waiting while preserving a practical
    convenience handoff for developers who want to seed through an IDE-hosted DMS process.
- `-InfraOnly` without `-DmsBaseUrl` is terminal for that `start-local-dms.ps1` invocation. It is not a
  checkpoint or later resume mechanism for that command's unfinished work.
- Story 03's accepted reading of "resume" applies only to `start-local-dms.ps1`: same-invocation
  continuation selected up front with `-InfraOnly -DmsBaseUrl`. It does not define a persisted resume
  mechanism for a later `start-local-dms.ps1` run.
- `-DmsBaseUrl` selects the external IDE-hosted DMS endpoint used in that continuation flow and is valid
  only with `-InfraOnly`.
- `-DmsBaseUrl` implies automatic health wait with a clear timeout failure if the IDE-hosted process never
  becomes healthy; there is no separate health-wait flag in the public contract.
- If the school-year workflow needs DMS-dependent work such as seed loading under the IDE-hosted process,
  that work still belongs to `load-dms-seed-data.ps1` and requires a healthy DMS endpoint. Story 03 does
  not define a later multi-year bootstrap-resume mechanism from the stopped pre-DMS shape.
- `-AddSmokeTestCredentials` is a CMS-only operation that runs after CMS readiness and step-7 instance
  selection. It is valid with `-InfraOnly` even when `-DmsBaseUrl` is not set.
- `configure-local-dms-instance.ps1` (step 7) is the only phase that creates or confirms DMS instance
  records. Selected instance IDs are printed to stdout. When the thin wrapper orchestrates a full run, it
  captures those IDs in memory and forwards them as explicit `-InstanceId` arguments to downstream phases
  within the same invocation. When phase commands are run separately by a developer, they resolve target
  instances through their own explicit selectors (`-InstanceId <guid[]>` or `-SchoolYear <int[]>`) via a
  CMS-backed lookup. No disk artifact is written for downstream instance targeting.
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
  intended single target instance. See [`bootstrap-design.md` Section 15](../bootstrap-design.md#15-breaking-changes-and-migration-notes)
  for the consolidated migration reference.
- Story 03 keeps CMS-only smoke credentials separate from DMS-dependent `SeedLoader` credentials and seed
  loading; no acceptance criterion describes one blended post-start credential-bootstrap phase.
- Developer-facing IDE guidance exists for the localhost configuration values required to run DMS against the
  Docker-managed infrastructure.
- Story 03 owns the bootstrap-time provisioning or validation of the dev-only `CMSReadOnlyAccess`
  client and its local-development credential used by IDE-hosted DMS to query CMS; the IDE workflow must
  not rely on pre-existing local seed state for that client to exist.
- The design documents one localhost contract for that dev-only `CMSReadOnlyAccess` client so the IDE
  workflow is deterministic, while leaving the exact local secret value as bootstrap-managed guidance rather
  than a story-level invariant. Broader CMS client lifecycle behavior remains outside this story.
- That IDE guidance scopes the starter `appsettings.Development.json.example` to the common single-instance
  flow and documents the self-contained `-SchoolYearRange` variant's
  `/connect/token/{schoolYear}` behavior separately.
- That IDE guidance explicitly uses the staged schema workspace:
  - `AppSettings__UseApiSchemaPath=true`,
  - `AppSettings__ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema`.
- The same staged schema files are used for `dms-schema hash`, Docker-hosted DMS, and IDE-hosted DMS.
- Step 8 remains responsible for validating the selected target databases against the exact selected schema
  set before any DMS-dependent continuation work begins.
- The documented self-contained identity-provider sequence matches repo dependencies:
  - PostgreSQL starts before `setup-openiddict.ps1 -InitDb`,
  - self-contained identity runs `setup-openiddict.ps1 -InsertData` after Config Service reaches the
    DMS-916 readiness gate,
  - Keycloak identity does not run `setup-openiddict.ps1 -InsertData`.
- Story 03 treats Config Service readiness as a claims-ready gate for the selected staged inputs. Step 7 and
  all later phases begin only after CMS startup claim loading has completed.
- The readiness check is explicit: after `/health` is green, bootstrap calls
  `/authorizationMetadata?claimSetName=<name>` and requires HTTP 200 for `EdFiSandbox`, and for each staged
  additional claim set name when hybrid claims are staged. Proceed only when those probes succeed.
- The IDE workflow remains Docker-first and does not introduce a second non-Docker bootstrap path.
- `eng/docker-compose/.bootstrap/` is treated as scratch bootstrap state and is excluded from source
  control.

## Tasks

1. Implement the story-aligned behaviors on `start-local-dms.ps1` as the infrastructure-lifecycle phase
   command: Docker stack management, service health waiting, idempotent `-InfraOnly` and `-DmsBaseUrl`
   workflow shapes, and the fail-fast parameter validation rules described in the Acceptance Criteria. Do
   not add parameters owned by schema preparation, claims staging, instance configuration, or seed delivery
   to this command.
2. Put the story-aligned infrastructure-phase parameters on `start-local-dms.ps1` as specified in
   `command-boundaries.md` Section 3.3; parameters owned by other phase commands stay with those commands.
3. Implement the `-InfraOnly` and `-DmsBaseUrl` behaviors on the script-owned bootstrap surface so only two
   workflow shapes remain: pre-DMS stop, or automatic-health-wait continuation against the external DMS
   endpoint. Keep optional smoke-test credentials in the CMS-only pre-DMS phase; the health-wait portion
   owned by `start-local-dms.ps1` ends when DMS health is confirmed, and any later DMS-dependent step is
   owned by wrapper orchestration or by the next explicit phase command. `load-dms-seed-data.ps1` is the
   next phase when seed loading is requested. Document that the stopped pre-DMS shape is a terminal
   invocation rather than a later resume checkpoint for `start-local-dms.ps1`. The next-step guidance
   printed at the pre-DMS termination point must give the developer the required IDE-hosted DMS
   configuration needed to start against the prepared environment, and it must not present a second
   `start-local-dms.ps1` invocation as a resume mechanism for skipped post-start work.
4. Keep the documented identity-provider ordering aligned with the real dependency chain for both Keycloak
   and self-contained auth, including the explicit rule that self-contained identity runs
   `setup-openiddict.ps1 -InsertData` after Config Service readiness while Keycloak skips that step, and keep
   the step-6 readiness contract aligned with claims loading: bootstrap does not proceed to step 7 until CMS
   startup claim loading is complete for the selected staged inputs, verified by `/health` plus successful
   `/authorizationMetadata?claimSetName=<name>` probes for `EdFiSandbox` and each staged additional claim
   set name when hybrid claims are in use.
5. Implement bootstrap-time provisioning or validation of the dev-only `CMSReadOnlyAccess` client so
   the IDE-hosted DMS flow has a stable local CMS credential contract aligned to the documented
   localhost values, without expanding this story into broader CMS client lifecycle design.
6. Implement the instance-ID forwarding contract: `configure-local-dms-instance.ps1` prints selected
   instance IDs to stdout; the thin wrapper (`bootstrap-local-dms.ps1`) captures those IDs in memory
   during a single invocation and forwards them as explicit `-InstanceId` arguments to
   `provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. When phase commands are run separately,
   those commands resolve target instances through their own explicit `-InstanceId <guid[]>` or
   `-SchoolYear <int[]>` selectors using a CMS-backed lookup. Selector resolution rule: auto-select when
   exactly one DMS instance exists and no selector is supplied; fail fast with a non-zero exit and
   corrective guidance when zero or multiple instances exist without an explicit selector. No run-context
   file is written.
7. Add or update the developer-facing IDE guidance, including the localhost configuration values and staged
   schema-path settings described in the main bootstrap design, including the bootstrap-provisioned
   `CMSReadOnlyAccess` read-only OAuth client details and the bootstrap-managed local-development credential
   value used by that flow.
8. Provide the starter configuration artifact for IDE use (`appsettings.Development.json.example`) so the
   documented workflow is actionable once the bootstrap-managed local-development credential is inserted from
   printed guidance or provisioning output, and remains aligned with the bootstrap-seeded
   `CMSReadOnlyAccess` client contract unless the provisioning scripts are intentionally changed.
9. Update the `-NoDmsInstance` rerun behavior to the narrowed DMS-916 escape hatch: valid only when exactly
   one existing instance is present in the current tenant scope and `-SchoolYearRange` is not set. Ambiguity
   or zero matches fail fast with teardown or manual-environment-preparation guidance. Later phases resolve
   their targets via the in-memory instance IDs forwarded by the wrapper (single-invocation path) or via
   explicit `-InstanceId`/`-SchoolYear` selectors (manual phase-flow path); they must not perform their own
   CMS instance creation, broad target-selection policy, or non-selector-driven discovery pass.
10. Carry the repo-local bootstrap-workspace hygiene through this slice by keeping
   `eng/docker-compose/.bootstrap/` git-ignored and documented as scratch-only state.
11. Document the narrowed `-NoDmsInstance` semantics as a deliberate breaking change for existing fresh-stack
   scripts and provide the migration guidance on the local bootstrap surface.

## Invocation Examples

The following examples show the two complementary invocation patterns. Neither is authoritative over the
other; the wrapper is happy-path convenience, the phase commands are the normative contract.

```powershell
# Wrapper — happy path (core schema, no seed)
pwsh eng/docker-compose/bootstrap-local-dms.ps1

# Wrapper - extension + seed
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -Extensions sample -LoadSeedData -SeedTemplate Minimal

# Wrapper - keycloak-backed happy path
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -IdentityProvider keycloak

# Wrapper - custom seed data
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -LoadSeedData -SeedDataPath "./my-seeds/"

# Wrapper - multi-year
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -SchoolYearRange "2025-2026" -LoadSeedData -SeedTemplate Minimal

# Wrapper — IDE workflow (stop before DMS, print IDE configuration guidance)
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly

# Manual phase flow (core schema, single instance)
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
pwsh eng/docker-compose/configure-local-dms-instance.ps1   # prints instance GUID to stdout
pwsh eng/docker-compose/provision-dms-schema.ps1           # auto-selects the one existing instance
pwsh eng/docker-compose/start-local-dms.ps1
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData -SeedTemplate Minimal

# Selector resolution: auto-select (one instance), explicit selector (multiple instances)
pwsh eng/docker-compose/provision-dms-schema.ps1                                             # auto-select
pwsh eng/docker-compose/provision-dms-schema.ps1 -InstanceId "a1b2c3d4-1234-5678-abcd-..." # explicit
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

- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 1, 5, 7, 9, 12, 13, and 14.1
- [`../command-boundaries.md`](../command-boundaries.md), Section 3.3 (infrastructure-lifecycle phase contract)
