---
design: DMS-916
---

# Story: Bootstrap Entry Point and IDE Workflow

## Description

Implement the primary bootstrap surface and the IDE-hosted DMS workflow without creating a second bootstrap
architecture. This slice keeps `start-local-dms.ps1` as the single developer-facing and automation surface
for the local workflow, and supports the Docker-for-infrastructure plus IDE-for-DMS workflow described in the
main design. The IDE path must reuse the same staged schema workspace and bootstrap ordering as the
canonical Docker path; it is not a parallel schema-resolution or auth-initialization design. Step 7 remains
the only phase that resolves target DMS instance IDs for the run. Optional smoke-test credentials are
CMS-only pre-DMS work anchored to that selected target set, while the DMS-dependent continuation covers only
automatic health wait plus the `SeedLoader`/seed-delivery path when `-LoadSeedData` is requested.
Within this story, the source story wording about "skip/resume" is satisfied by safe skip behavior and
optional same-invocation continuation only. It does not introduce a stopped-then-resume bootstrap contract
across separate invocations.

## Acceptance Criteria

- `start-local-dms.ps1` is the primary automation surface for the local developer bootstrap workflow.
- `start-local-dms.ps1` is also the canonical developer-facing single-command entry point for the bootstrap
  workflow; no wrapper script or alias is required.
- The story-aligned bootstrap flow stays consistent:
  - Config Service remains part of the canonical flow,
  - `-EnableConfig` is not treated as a meaningful developer-facing opt-out,
  - every non-teardown bootstrap run starts Config Service, including the default no-argument flow and
    keycloak-backed runs.
- `-InfraOnly` excludes the DMS container from Docker startup and, together with the optional
  `-DmsBaseUrl`, defines the two explicit workflow shapes in this story's public contract.
- Story 03 documents only two workflow shapes:
  - `-InfraOnly` without `-DmsBaseUrl` completes the pre-DMS phase only: infrastructure startup, instance
    creation or narrow existing-instance reuse, optional CMS-only smoke-test credentials, schema
    provisioning or validation, and next-step guidance for the IDE-hosted DMS launch.
  - `-InfraOnly` with `-DmsBaseUrl` set continues from that same pre-DMS phase by automatically waiting for
    the IDE-hosted DMS process to become healthy, then running DMS-dependent continuation work such as the
    dedicated `SeedLoader` credential flow and seed loading when requested.
- `-InfraOnly` without `-DmsBaseUrl` is terminal for that invocation. It is not a checkpoint or later
  bootstrap-resume mechanism for unfinished post-start work.
- Story 03's accepted reading of "resume" is only the same-invocation continuation selected up front with
  `-InfraOnly -DmsBaseUrl`; it does not define a persisted resume mechanism for a later bootstrap run.
- `-LoadSeedData` with `-InfraOnly` but without `-DmsBaseUrl` is invalid and fails fast during parameter
  validation; the pre-DMS-only workflow never silently defers seed loading to an implicit later
  continuation.
- `-DmsBaseUrl` selects the external IDE-hosted DMS endpoint used in that continuation flow and is valid
  only with `-InfraOnly`.
- `-DmsBaseUrl` implies automatic health wait with a clear timeout failure if the IDE-hosted process never
  becomes healthy; there is no separate health-wait flag in the public contract.
- If the school-year workflow needs DMS-dependent continuation work such as seed loading under the IDE-hosted
  process, `-DmsBaseUrl` must be present on that original `-InfraOnly` invocation. Story 03 does not define
  a later multi-year resume from the stopped pre-DMS shape.
- `-AddSmokeTestCredentials` is a CMS-only operation that runs after CMS readiness and step-7 instance
  selection. It is valid with `-InfraOnly` even when `-DmsBaseUrl` is not set.
- Step 7 is the only phase allowed to resolve target DMS instance IDs for the run. All later phases consume
  that selected target set and never rediscover instances through CMS.
- `-NoDmsInstance` is only a narrow manual reuse escape hatch:
  - valid only when exactly one existing instance is present in the current tenant scope,
  - invalid with `-SchoolYearRange`,
  - ambiguous reuse fails fast with teardown or manual-environment-preparation guidance instead of a second
    target-selection subsystem.
- The narrowed DMS-916 definition of `-NoDmsInstance` is documented as a deliberate breaking change for
  existing fresh-stack scripts; migration guidance tells users to rerun without the flag or pre-create the
  intended single target instance.
- Story 03 keeps CMS-only smoke credentials separate from DMS-dependent `SeedLoader` credentials and seed
  loading; no acceptance criterion describes one blended post-start credential-bootstrap phase.
- Developer-facing IDE guidance exists for the localhost configuration values required to run DMS against the
  Docker-managed infrastructure.
- Story 03 owns the bootstrap-time provisioning or validation of the fixed dev-only `CMSReadOnlyAccess`
  client and documented secret used by IDE-hosted DMS to query CMS; the IDE workflow must not rely on
  pre-existing local seed state for that client to exist.
- The design documents one fixed localhost contract for that dev-only `CMSReadOnlyAccess` client so the IDE
  workflow is deterministic; broader CMS client lifecycle behavior remains outside this story.
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
  all later phases begin only after CMS startup claim loading has completed; if CMS health can become green
  before claims loading finishes, bootstrap must add an explicit claims-ready verification step.
- The IDE workflow remains Docker-first and does not introduce a second non-Docker bootstrap path.
- `eng/docker-compose/.bootstrap/` is treated as scratch bootstrap state and is excluded from source
  control.

## Tasks

1. Keep `start-local-dms.ps1` as the single developer-facing and automation bootstrap surface for the local
   workflow.
2. Put the story-aligned parameter surface directly on `start-local-dms.ps1` without creating a second
   lifecycle contract.
3. Implement the `-InfraOnly` and `-DmsBaseUrl` behaviors on the script-owned bootstrap surface so only two
   workflow shapes remain: pre-DMS stop, or automatic-health-wait continuation against the external DMS
   endpoint. Keep optional smoke-test credentials in the CMS-only pre-DMS phase, reserve the DMS-dependent
   continuation for `SeedLoader`/seed-loading work, fail fast when `-LoadSeedData` is requested in the
   pre-DMS-only `-InfraOnly` shape without `-DmsBaseUrl`, and document that the stopped pre-DMS shape is a
   terminal invocation rather than a later resume checkpoint. The only "resume" interpretation accepted by
   this story is continuation declared up front on that same invocation via `-DmsBaseUrl`. The next-step
   guidance printed at the
   pre-DMS termination point must include the exact IDE-hosted DMS configuration values needed to start the
   application against the prepared environment, and it must not present a second bootstrap invocation as a
   resume mechanism for skipped `SeedLoader` or seed-loading work.
4. Keep the documented identity-provider ordering aligned with the real dependency chain for both Keycloak
   and self-contained auth, including the explicit rule that self-contained identity runs
   `setup-openiddict.ps1 -InsertData` after Config Service readiness while Keycloak skips that step, and keep
   the step-6 readiness contract aligned with claims loading: bootstrap does not proceed to step 7 until CMS
   startup claim loading is complete for the selected staged inputs.
5. Implement bootstrap-time provisioning or validation of the fixed dev-only `CMSReadOnlyAccess` client so
   the IDE-hosted DMS flow has a deterministic local CMS credential contract aligned to the documented
   localhost values, without expanding this story into broader CMS client lifecycle design.
6. Add or update the developer-facing IDE guidance, including the localhost configuration values and staged
   schema-path settings described in the main bootstrap design, including the bootstrap-provisioned
   `CMSReadOnlyAccess` read-only OAuth client details and the fixed, documented dev-only secret value used locally.
7. Provide the starter configuration artifact for IDE use (`appsettings.Development.json.example`) so the
   documented workflow is directly actionable and remains aligned with the bootstrap-seeded
   `CMSReadOnlyAccess` credentials unless the provisioning scripts are intentionally changed.
8. Update the `-NoDmsInstance` rerun behavior to the narrowed DMS-916 escape hatch: valid only when exactly
   one existing instance is present in the current tenant scope and `-SchoolYearRange` is not set. Ambiguity
   or zero matches fail fast with teardown or manual-environment-preparation guidance, and all later phases
   consume the step-7-selected target set without performing a second CMS discovery pass.
9. Carry the repo-local bootstrap-workspace hygiene through this slice by keeping
   `eng/docker-compose/.bootstrap/` git-ignored and documented as scratch-only state.
10. Document the narrowed `-NoDmsInstance` semantics as a deliberate breaking change for existing fresh-stack
   scripts and provide the migration guidance on the local bootstrap surface.

## Out of Scope

- Adding smoke, E2E, or integration test runner flags to the bootstrap surface.
- SDK generation as part of bootstrap.
- Creating a second bootstrap implementation that runs DMS and its supporting infrastructure entirely outside
  the Docker-first model.
- Creating a second schema-materialization path for IDE-hosted DMS.

## Design References

- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 1, 5, 7, 9, 12, 13, and 14.1
