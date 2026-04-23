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
parallel schema-resolution or auth-initialization design. Step 7 remains
the only phase that resolves target DMS instance IDs for the run. Optional smoke-test credentials are
CMS-only pre-DMS work anchored to that selected target set, while the DMS-dependent continuation covers only
automatic health wait plus the `SeedLoader`/seed-delivery path when `-LoadSeedData` is requested.
Within this story, the source story wording about "skip/resume" is satisfied by safe skip behavior and
optional same-invocation continuation only. It does not introduce a stopped-then-resume bootstrap contract
across separate invocations.

## Acceptance Criteria

- `start-local-dms.ps1` is the infrastructure-lifecycle phase command: it owns Docker stack management,
  service health waiting, and the `-InfraOnly`/`-DmsBaseUrl` behaviors documented in this story. It does
  not own schema preparation, claims staging, instance configuration, or seed delivery.
- The optional thin wrapper (`bootstrap-local-dms.ps1`) sequences the phase commands for the common
  happy-path developer workflow. It is convenience packaging only — not the normative bootstrap contract
  and not required; developers may invoke the phase commands directly.
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
    by automatically waiting for the IDE-hosted DMS process to become healthy, then running DMS-dependent
    continuation work such as the dedicated `SeedLoader` credential flow and seed loading when requested.
    This shape is retained because removing it would require a separate bootstrap re-invocation after IDE
    launch to perform seed loading, forcing redundant re-execution of prior phase outputs (instance IDs,
    schema hash, credentials); it is documented as a convenience option rather than a required path.
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
  all later phases begin only after CMS startup claim loading has completed; if service health alone is not
  sufficient to guarantee that, the implementation must ensure readiness is checked before continuing.
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
   `command-boundaries.md` §3.3; parameters owned by other phase commands stay with those commands.
3. Implement the `-InfraOnly` and `-DmsBaseUrl` behaviors on the script-owned bootstrap surface so only two
   workflow shapes remain: pre-DMS stop, or automatic-health-wait continuation against the external DMS
   endpoint. Keep optional smoke-test credentials in the CMS-only pre-DMS phase, reserve the DMS-dependent
   continuation for `SeedLoader`/seed-loading work, fail fast when `-LoadSeedData` is requested in the
   pre-DMS-only `-InfraOnly` shape without `-DmsBaseUrl`, and document that the stopped pre-DMS shape is a
   terminal invocation rather than a later resume checkpoint. The only "resume" interpretation accepted by
   this story is continuation declared up front on that same invocation via `-DmsBaseUrl`. The next-step
   guidance printed at the pre-DMS termination point must give the developer the required IDE-hosted DMS
   configuration needed to start against the prepared environment, and it must not present a second
   bootstrap invocation as a resume mechanism for skipped `SeedLoader` or seed-loading work.
4. Keep the documented identity-provider ordering aligned with the real dependency chain for both Keycloak
   and self-contained auth, including the explicit rule that self-contained identity runs
   `setup-openiddict.ps1 -InsertData` after Config Service readiness while Keycloak skips that step, and keep
   the step-6 readiness contract aligned with claims loading: bootstrap does not proceed to step 7 until CMS
   startup claim loading is complete for the selected staged inputs.
5. Implement bootstrap-time provisioning or validation of the dev-only `CMSReadOnlyAccess` client so
   the IDE-hosted DMS flow has a stable local CMS credential contract aligned to the documented
   localhost values, without expanding this story into broader CMS client lifecycle design.
6. Add or update the developer-facing IDE guidance, including the localhost configuration values and staged
   schema-path settings described in the main bootstrap design, including the bootstrap-provisioned
   `CMSReadOnlyAccess` read-only OAuth client details and the bootstrap-managed local-development credential
   value used by that flow.
7. Provide the starter configuration artifact for IDE use (`appsettings.Development.json.example`) so the
   documented workflow is actionable once the bootstrap-managed local-development credential is inserted from
   printed guidance or provisioning output, and remains aligned with the bootstrap-seeded
   `CMSReadOnlyAccess` client contract unless the provisioning scripts are intentionally changed.
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
- [`../command-boundaries.md`](../command-boundaries.md), §3.3 (infrastructure-lifecycle phase contract)
