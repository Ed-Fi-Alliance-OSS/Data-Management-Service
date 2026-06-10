# Story: Bootstrap Entry Point and IDE Workflow (DMS-1153)

Jira: [DMS-1153](https://edfi.atlassian.net/browse/DMS-1153) · Epic: DMS-1149 · Blocked by: DMS-1150 (Done)
Reference spec: `reference/design/backend-redesign/epics/16-bootstrap/03-entry-point-and-ide-workflow.md`
Contract: `reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md` §3.3, §3.7

## Summary

Finish `start-local-dms.ps1` as the local infrastructure-lifecycle-only phase command and deliver the
documented IDE debugging workflow without adding a second bootstrap model. The story owns the local
wrapper-level `-InfraOnly` / `-DmsBaseUrl` workflow shapes, the explicit CMS claims-ready gate, IDE
next-step guidance with the staged-schema and `CMSReadOnlyAccess` localhost contract, and the product-tree
starter `appsettings.Development.json.example` artifact. It deliberately leaves legacy no-manifest,
DLL-backed local/CI startup paths as audited compatibility paths until Story 04 (DMS-1154) activates staged
ApiSchema runtime loading and closes the remaining epic-level bootstrap gaps.

## Homologation and Scope Boundary

This story is homologated against:

- `reference/design/backend-redesign/epics/16-bootstrap/03-entry-point-and-ide-workflow.md`
- `reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md` §3.3, §3.7, §4-§7
- `reference/design/backend-redesign/design-docs/bootstrap/bootstrap-design.md` §9.3, §12.4, §15

If this story conflicts with those files, `command-boundaries.md` remains authoritative for phase
ownership and parameter ownership, while `bootstrap-design.md` remains authoritative for migration notes
and IDE appsettings values. Update this story before implementation if a design-doc conflict is found.

End-state ownership for this story is:

- `start-local-dms.ps1`: Docker stack lifecycle, provider-specific local identity setup, Config Service
  readiness, the claims-ready gate, `CMSReadOnlyAccess` and `CMSAuthMetadataReadOnlyAccess` local identity
  clients, `-InfraOnly`, `-DmsOnly`, and the post-provision external-DMS health wait selected by
  `-InfraOnly -DmsBaseUrl`.
- `configure-local-data-store.ps1`: DMS instance creation or narrow reuse, `-NoDataStore`,
  `-SchoolYearRange`, and CMS-only smoke-test credentials.
- `provision-dms-schema.ps1`: target database provisioning or validation, target selector resolution, and
  IDE next-step guidance after pre-DMS preparation.
- `load-dms-seed-data.ps1`: SeedLoader credentials, BulkLoadClient invocation, seed-source parameters,
  seed-phase `-DmsBaseUrl`, seed-phase `-IdentityProvider`, and seed target selectors.
- `bootstrap-local-dms.ps1`: happy-path orchestration and the two local IDE workflow shapes. It captures
  structured configure results in memory and forwards IDs to downstream phases, but it does not expose
  `-DataStoreId`, parse human-readable output, persist resume state, create credentials directly, provision
  schema directly, or load seed data directly.

No persisted stopped-then-resume contract is introduced. `-InfraOnly` without `-DmsBaseUrl` is a terminal
pre-DMS preparation shape for that invocation. `-InfraOnly -DmsBaseUrl` is same-invocation wrapper
continuation, or a manual health-wait command only after the developer has already run the configure and
provision phases.

### Epic Closure Boundary

DMS-1153 is the entry-point, IDE workflow, and claims-ready-gate story. It is not the final epic closure
story. DMS-1154 closes the remaining DMS-916 bootstrap-path gaps by making the staged ApiSchema workspace and
staged claims workspace runtime-authoritative for Docker-hosted and IDE-hosted DMS. Until then,
no-manifest/DLL-backed consumers may keep the legacy full-start shape if they are explicitly documented as
outside the bootstrap-manifest contract and assigned to the DMS-1154 cutover.

## Current Problem

Stories 00–02 (DMS-1150/1151/1152) already delivered most of the phase-command surface. A codebase
survey against the Story 03 acceptance criteria found the following **remaining gaps**:

1. **`start-local-dms.ps1` has no `-DmsBaseUrl`** and no post-provision IDE-hosted DMS health-wait;
   `-InfraOnly` exists (terminal) but the continuation shape is unimplemented
   (`eng/docker-compose/start-local-dms.ps1`).
2. **The wrapper does not expose the IDE workflow**: `bootstrap-local-dms.ps1` forces `-InfraOnly`
   internally and always continues through configure → provision → `-DmsOnly`; there is no public
   `-InfraOnly` (pre-DMS stop) or `-DmsBaseUrl` (health-wait continuation) shape
   (`eng/docker-compose/bootstrap-wrapper.psm1`).
3. **Claims-ready gate is `/health`-only**: `prepare-dms-claims.ps1` records
   `claims.expectedVerificationChecks`, but `start-local-dms.ps1` does not yet verify CMS authorization
   metadata after HTTP 200 and before instance configuration.
4. **No IDE starter artifact in the product tree**: `appsettings.Development.json.example` exists only
   as a design reference under `reference/design/.../bootstrap/`.
5. **Transitional non-infrastructure flags still on the start script**: `-NoDataStore`,
   `-SchoolYearRange`, `-LoadSeedData`, and `-AddSmokeTestCredentials` remain on
   `start-local-dms.ps1`; command-boundaries §3.3 retains the data-store/smoke/seed behavior
   "until DMS-1153 de-scopes them". In-repo consumers split into two groups:
   bootstrap-manifest consumers must move to phase flow or wrapper now, while legacy no-manifest,
   DLL-backed consumers may keep full-start compatibility only as a documented DMS-1154 handoff.
6. **No Pester coverage** for the `-InfraOnly`/`-DmsBaseUrl` shapes or the claims-ready gate.

Already delivered (verified, no work needed beyond regression protection):

- Shared local-settings helper (`env-utility.psm1` `Resolve-LocalSettingsEnvironmentFile`), used by all
  phase commands.
- `configure-local-data-store.ps1` structured result with `SelectedDataStoreIds` +
  `CMSReadOnlyAccess` fields; narrowed `-NoDataStore` validation.
- Wrapper in-memory data-store-ID capture/forwarding (`Resolve-WrapperSelectedDataStoreIds`,
  internal `-DataStoreId` to provision/seed); `-DataStoreId` not public on the wrapper.
- `-DataStoreId`/`-SchoolYear` selectors with auto-select/fail-fast on `provision-dms-schema.ps1` and
  `load-dms-seed-data.ps1`; seed phase `-DmsBaseUrl`/`-IdentityProvider`/`-AdditionalNamespacePrefix`.
- `CMSReadOnlyAccess` and `CMSAuthMetadataReadOnlyAccess` provisioning via provider-specific identity setup
  (`setup-keycloak.ps1` / `setup-openiddict.ps1 -InsertData`), not `/connect/register`.
- `prepare-dms-claims.ps1` writes `claims.expectedVerificationChecks`, including the embedded baseline
  `EdFiSandbox` + `http://ed-fi.org/identity/claims/domains/edFiTypes` + `Read` probe and staged fragment
  checks.
- `setup-openiddict.ps1` `-InitDb`/`-InsertData` ordering matches the documented sequence.
- `NEED_DATABASE_SETUP` forced `false` when a bootstrap manifest is present; compose default `false`.
- `eng/docker-compose/.bootstrap/` gitignore entry (owned by Story 00).

## Requirements

### 1. `start-local-dms.ps1` IDE continuation surface
Add `-DmsBaseUrl <url>` to `start-local-dms.ps1`, valid only with `-InfraOnly`. This is a
health-wait-only endpoint for an IDE-hosted DMS process; it does not create or select data stores, provision
schema, create smoke-test credentials, or load seed data. When set, the script starts or verifies the
Docker-managed infrastructure without the DMS container, waits for Config Service readiness and the
claims-ready gate, then waits for `$DmsBaseUrl/health` to return HTTP 200 with a clear timeout failure if it
never does. No separate health-wait flag. `-InfraOnly` without `-DmsBaseUrl` remains terminal for that
invocation and is not a resume checkpoint.

Manual phase flows must run `configure-local-data-store.ps1` and `provision-dms-schema.ps1` before invoking
`start-local-dms.ps1 -InfraOnly -DmsBaseUrl`; the start script does not infer that precondition from disk
state.

### 2. Wrapper IDE workflow shapes
Expose `-InfraOnly` and `-DmsBaseUrl` on `bootstrap-local-dms.ps1` (via `Invoke-BootstrapWrapper`). The
published wrapper surface is unchanged in this story.

- **Primary (pre-DMS stop)**: `-InfraOnly` alone runs infrastructure startup, instance creation or
  narrow reuse, optional CMS-only smoke-test credentials, schema provisioning/validation, then prints
  IDE next-step guidance and stops. Terminal for that invocation.
- **Convenience (health-wait continuation)**: `-InfraOnly -DmsBaseUrl <url>` completes the same
  pre-DMS phase, then waits for the IDE-hosted DMS to become healthy. The wrapper must **not**
  forward `-DmsBaseUrl` to the initial infrastructure invocation; it carries the value until after
  instance selection and schema provisioning. When `-LoadSeedData` is also requested, the wrapper
  forwards `-DmsBaseUrl`, `-IdentityProvider`, `-AdditionalNamespacePrefix` (when provided), and the
  in-memory selected data-store IDs to `load-dms-seed-data.ps1`.

The wrapper must never expose `-DataStoreId`; explicit data-store-ID targeting remains direct
phase-command-only. If a Pester fixture uses isolated wrapper stubs, it must still prove that the production
wrapper call graph holds `-DmsBaseUrl` until the post-provision point.

### 3. Explicit claims-ready gate (manifest-driven, forward-compatible)
After CMS `/health` is green and local identity setup has created the metadata-read client, bootstrap proves
CMS applied the expected claims content before instance configuration begins. The gate reads
`claims.expectedVerificationChecks` from `eng/docker-compose/.bootstrap/bootstrap-manifest.json`; the list
must be present and non-empty for a bootstrap-manifest run.

For each check, query CMS authorization metadata with the `CMSAuthMetadataReadOnlyAccess` client
(`edfi_admin_api/authMetadata_readonly_access`) or an equivalent bootstrap admin token if the dedicated
client is unavailable during early startup. `/v2/claimSets` may be used only as a supporting existence
check; the authoritative proof is `/authorizationMetadata`. The gate must find the expected claim set, the
expected resource claim URI in that claim set's `claims`, and the expected action name in the linked
authorization. Finding only `EdFiSandbox`, only a claim set name, or only `/health` is not enough.

The check runs against the claims sources active today (embedded defaults plus Hybrid extension fragments).
Once DMS-1154 activates staged `.bootstrap/claims` startup, the same manifest-driven check covers staged
content without redesign. Prefer a CMS load/composition result if one becomes available, but keep the
authorization-metadata proof as the externally observable readiness contract.

### 4. Full de-scope of non-infrastructure start-script flags + audited consumer boundary
Remove `-NoDataStore`, `-SchoolYearRange`, `-LoadSeedData`, and `-AddSmokeTestCredentials` from
`start-local-dms.ps1` so it is infrastructure-lifecycle-only (end-state contract, command-boundaries
§3.3). `-AddExtensionSecurityMetadata` is **not** in the de-scope group and remains on the start script
until Story 04 moves E2E runtime loading onto the staged bootstrap workspace. Document the narrowed
`-NoDataStore` semantics as a deliberate breaking change with migration guidance (rerun without the flag,
or pre-create the single intended target instance).

Do not add replacement start-script flags for schema, claims, configure, smoke-test, or seed concerns. If
existing tests assert shared `start-(local|published)-dms.ps1` transitional behavior, split those assertions
so this story can remove the local surface without accidentally changing the published-image contract.

Audit every in-repo consumer that calls `start-local-dms.ps1` and handle it in one of two ways:

- **Bootstrap-manifest consumers** must use the phase flow or wrapper in this story so claims readiness,
  instance configuration, schema provisioning, DMS startup, and optional seed delivery remain ordered.
- **Legacy no-manifest/DLL-backed consumers** may keep the full-start shape only when they rely on existing
  in-container startup provisioning or `*.ApiSchema.dll` runtime loading that DMS-1154 has not yet replaced.
  Those call sites must be labeled as outside the DMS-1153 bootstrap-manifest contract, must explain why
  `-InfraOnly`/`-DmsOnly` is not valid yet, and must point to DMS-1154 as the cutover story.

The consumer audit must cover:

- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1` (passes `-NoDataStore`)
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1` (relies on default
  data-store creation by the start script)
- CI workflows passing `-NoDataStore`: `EdFi.Dms.Minimal.Template.PostgreSQL.yml`,
  `scheduled-smoke-test.yml`, `build-populated-template.yml`
- CI workflows relying on default data-store creation: `on-dms-pullrequest.yml` (E2E),
  `scheduled-build.yml`, `Pkg EdFi.DmsApi.Sdk.yml`, `Pkg EdFi.DmsApi.TestSdk.yml`
- Pester assertions that pin the old consumer surface (e.g.
  `BootstrapSchemaAndSecuritySelection.Tests.ps1` checks on the E2E setup wrappers)

### 4.1 DMS-1154 Epic-Closure Handoff

Story 04 (DMS-1154) must close the compatibility gaps left intentionally open here:

- Move CI, E2E, and `build-dms.ps1` local paths off legacy DLL-backed full start where they are part of the
  DMS-916 bootstrap path.
- Activate Docker-hosted and IDE-hosted DMS runtime loading from `.bootstrap/ApiSchema` instead of requiring
  `*.ApiSchema.dll`.
- Activate staged CMS claims from `.bootstrap/claims` for the same root bootstrap manifest that selects the
  runtime schema.
- Make the DMS-1153 claims-ready gate prove the final staged claims path, not only embedded/default or Hybrid
  extension claims.
- Retire the direct-SQL/database-template seed carve-out once API seed delivery can run against the staged
  workspace.
- Remove transitional comments that say "Story 04 moves this..." after the cutover is complete.
- Validate the final epic path with real Docker startup, not only Pester stubs.

### 5. IDE guidance and starter configuration artifact
Developer-facing IDE guidance covering: localhost configuration values, staged schema workspace settings
(`AppSettings__UseApiSchemaPath=true`,
`AppSettings__ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema`), the dev-only
`CMSReadOnlyAccess` localhost contract (exact secret value remains identity-setup output/guidance), and the
self-contained `-SchoolYearRange` variant's `/connect/token/{schoolYear}` behavior documented separately.
Ship `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json.example`
as an actionable starter artifact aligned with
`reference/design/backend-redesign/design-docs/bootstrap/appsettings.Development.json.example`.

### 6. Pester coverage
Cover the new `-InfraOnly`/`-DmsBaseUrl` shapes (start script and wrapper), parameter fail-fast rules
(`-DmsBaseUrl` without `-InfraOnly`, initial wrapper infra call receiving `-InfraOnly` but not
`-DmsBaseUrl`), the claims-ready gate, and the de-scoped local start-script surface, following the existing
`eng/docker-compose/tests/Bootstrap*.Tests.ps1` style.

At minimum, tests must pin:

- `start-local-dms.ps1` declares `-DmsBaseUrl`, rejects it without `-InfraOnly`, and no longer declares
  `-NoDataStore`, `-SchoolYearRange`, `-LoadSeedData`, or `-AddSmokeTestCredentials`.
- `bootstrap-local-dms.ps1` declares `-InfraOnly` and `-DmsBaseUrl`, while `bootstrap-published-dms.ps1`
  remains unchanged for IDE workflow parameters.
- Wrapper `-InfraOnly` runs configure and provision, stops before DMS startup, and prints IDE guidance.
- Wrapper `-InfraOnly -DmsBaseUrl` does not pass `-DmsBaseUrl` to the first start-script invocation, waits
  only after provision, and forwards the URL to seed loading when `-LoadSeedData` is set.
- The claims-ready gate fails when expected manifest checks are missing from `/authorizationMetadata`,
  including the case where only `EdFiSandbox` or `/health` is present.

## Design Decisions

### D1: Repo terminology wins over Jira wording
The Jira description uses older names (`configure-local-dms-instance.ps1`, `-InstanceId`). The repo
and design docs already use `configure-local-data-store.ps1` / `-DataStoreId` / `-NoDataStore`.
Design docs are authoritative.

### D2: Build on the delivered DMS-1151/1152 surface
The wrapper sequencing, structured configure result, ID forwarding, selectors, and shared env helper
already exist. This story extends them; it does not redesign them.

### D3: `NEED_DATABASE_SETUP` stays "disabled", not removed
The current behavior (forced `false` whenever a bootstrap manifest is present; compose default
`false`) satisfies the AC's "disabled or removed". Verify with a regression test; do not remove the
legacy installer branch in this story.

### D4: `appsettings.Development.json.example` location
Default: ship next to the DMS frontend project (`src/dms/frontend/...AspNetCore/`), aligned with the
design-reference copy under `reference/design/.../bootstrap/`.

### D5: IDE flags exposed on the local wrapper only
`-InfraOnly`/`-DmsBaseUrl` plumbing lands in the shared `bootstrap-wrapper.psm1` where natural, but
the IDE workflow shapes are exposed on `bootstrap-local-dms.ps1` only; `bootstrap-published-dms.ps1`
surface is unchanged.

### D6: Branch from main; rebase after PR #1017 (DMS-1156) merges
PR #1017 modifies `bootstrap-local-dms.ps1`, `bootstrap-wrapper.psm1`, `prepare-dms-schema.ps1`, and
`prepare-dms-claims.ps1` (adds `-Extensions`). Its cross-story note states the `-Extensions` seed
preflight guard is the only DMS-1153 item it absorbs. DMS-1153 branches from `main` now and rebases
once #1017 merges, resolving wrapper conflicts then. The PR stays independently reviewable and is not
stacked on #1017's review timeline. Expect conflicts concentrated in `Invoke-BootstrapWrapper`'s
param block and the pre-start seed preflight region.

### D7: DMS-1153 is not the epic closure story
DMS-1153 hardens the entry point and makes the bootstrap-manifest phase contract observable, but DMS-1154 is
the story that completes the epic path by turning the staged ApiSchema and staged claims workspaces into the
runtime source for DMS/CMS. Legacy no-manifest/DLL-backed consumers that cannot yet run through the staged
workspace are acceptable only as explicitly labeled DMS-1154 handoffs.

## Out of Scope

- Smoke/E2E/integration test-runner flags on the bootstrap surface.
- SDK generation as part of bootstrap.
- A second non-Docker bootstrap implementation or second schema-materialization path for IDE-hosted DMS.
- Persisted stop/resume bootstrap contract across separate invocations ("resume" = same-invocation
  sequencing selected up front with `-InfraOnly -DmsBaseUrl`).
- Broader CMS client lifecycle design beyond the dev-only `CMSReadOnlyAccess` contract.
- Story 04 (DMS-1154) staged ApiSchema runtime content loading itself.
- Cutting over legacy no-manifest/DLL-backed startup consumers to the staged workspace; DMS-1154 owns that
  final epic closure.
- Re-implementing already-delivered DMS-1151/1152 behaviors listed under Current Problem.

## Acceptance Criteria

- [ ] `start-local-dms.ps1` accepts `-DmsBaseUrl` only with `-InfraOnly`; it health-waits the external
      endpoint after Config Service and claims readiness with a clear timeout failure; `-InfraOnly` alone
      stays terminal and neither path performs configure/provision/seed work.
- [ ] `bootstrap-local-dms.ps1` exposes the two documented workflow shapes; `-DmsBaseUrl` is never
      forwarded to the initial infrastructure invocation and `bootstrap-published-dms.ps1` does not gain
      IDE workflow parameters in this story.
- [ ] Pre-DMS terminal output prints actionable IDE configuration guidance (staged schema path,
      appsettings values, `CMSReadOnlyAccess` details) and does not present a second
      `start-local-dms.ps1` run as a resume mechanism.
- [ ] With `-LoadSeedData`, the wrapper forwards `-DmsBaseUrl`, `-IdentityProvider`,
      `-AdditionalNamespacePrefix`, and selected data-store IDs to `load-dms-seed-data.ps1`.
- [ ] `bootstrap-local-dms.ps1 -InfraOnly -AddSmokeTestCredentials` works without `-DmsBaseUrl`
      (CMS-only, post-CMS readiness and instance selection).
- [ ] Instance configuration and later phases begin only after the explicit claims-ready gate passes:
      `/health` is green, `claims.expectedVerificationChecks` is present, and `/authorizationMetadata`
      proves the expected claim set + resource claim URI + action combinations.
- [ ] `start-local-dms.ps1` no longer declares `-NoDataStore`, `-SchoolYearRange`, `-LoadSeedData`, or
      `-AddSmokeTestCredentials`; breaking change and migration guidance documented.
- [ ] All in-repo `start-local-dms.ps1` consumers (E2E setup scripts, CI workflows listed in
      Requirement 4) are either migrated to the phase flow/wrapper or explicitly documented as legacy
      no-manifest/DLL-backed compatibility paths assigned to DMS-1154; CI remains green.
- [ ] The claims-ready gate authenticates with `CMSAuthMetadataReadOnlyAccess` or an equivalent bootstrap
      admin token, and asserts manifest-selected resource claim URIs/actions in CMS authorization metadata,
      not just `/health`, `/v2/claimSets`, or `EdFiSandbox` presence.
- [ ] `appsettings.Development.json.example` ships in the product tree and matches the
      `CMSReadOnlyAccess` and staged-schema contract.
- [ ] Documented identity-provider ordering matches repo behavior (PostgreSQL →
      `setup-openiddict.ps1 -InitDb`; CMS readiness → `-InsertData` for self-contained; Keycloak skips
      `-InsertData`).
- [ ] Pester tests cover the new shapes, fail-fast validation, claims-ready gate, published-wrapper
      non-change, and de-scoped local start-script surface.

## Review Log

### 2026-06-10
- **Homologation pass**: Contract rechecked against `03-entry-point-and-ide-workflow.md`,
  `command-boundaries.md` §3.3/§3.7/§4-§7, and `bootstrap-design.md` §9.3/§12.4/§15. PR #1017 was
  verified open on 2026-06-10, so D6's rebase-after-merge instruction remains current.
- **D6 decided**: Branch from `main`; rebase after PR #1017 (DMS-1156) merges. Not stacked on the
  DMS-1156 branch; conflicts in `bootstrap-wrapper.psm1`/`bootstrap-local-dms.ps1` resolved at rebase
  time.
- **R3 decided**: Claims-ready gate is implemented in this story as a manifest-driven authorization
  metadata check (expected resource claim URIs/actions for the selected schema set), running against
  currently-active claims sources and forward-compatible with DMS-1154 staged-claims activation.
  Resolves the apparent conflict with command-boundaries §3.3's "Story 04 owns the claims-ready gate
  for staged bootstrap startup" — Story 03 owns the gate mechanism; Story 04 owns pointing CMS at
  staged content.
- **R4 decided**: Full de-scope of `-NoDataStore`/`-SchoolYearRange`/`-LoadSeedData`/
  `-AddSmokeTestCredentials` from `start-local-dms.ps1`, plus an audited consumer boundary. Consumers already
  on the bootstrap-manifest contract must move to phase flow or wrapper in this story. Legacy no-manifest,
  DLL-backed consumers may remain full-start compatibility paths only with explicit comments assigning their
  cutover to DMS-1154. `-AddExtensionSecurityMetadata` stays on the start script until Story 04 moves E2E
  runtime loading onto the staged bootstrap workspace.
- **D7 decided**: DMS-1153 does not close the full DMS-916 bootstrap epic. Story 04 (DMS-1154) closes the
  remaining gaps by activating staged ApiSchema/staged claims runtime loading, migrating legacy full-start
  consumers, retiring direct-SQL/template seed compatibility, and validating the final Docker bootstrap path.
- **Self-resolved (no questions needed)**:
  - Jira's older terminology (`configure-local-dms-instance.ps1`, `-InstanceId`) vs repo/design-doc
    terminology (`configure-local-data-store.ps1`, `-DataStoreId`): design docs win; repo already
    matches.
  - Tasks 5–7 and 11 of the reference spec are already delivered by DMS-1150/1151/1152: shared
    local-settings helper (`env-utility.psm1:33`), structured configure result with
    `SelectedDataStoreIds` + `CMSReadOnlyAccess`, wrapper in-memory ID forwarding
    (`bootstrap-wrapper.psm1:404-448`), phase selectors with auto-select/fail-fast,
    `CMSReadOnlyAccess` and `CMSAuthMetadataReadOnlyAccess` provisioning via provider identity setup,
    `prepare-dms-claims.ps1` expected-verification-check manifest output, `setup-openiddict.ps1`
    `-InitDb`/`-InsertData` ordering, `.bootstrap/` gitignore.
  - `NEED_DATABASE_SETUP` is already forced `false` when a bootstrap manifest is present
    (`start-local-dms.ps1:392-406`) and defaults `false` in compose — satisfies "disabled or
    removed"; regression test only (D3).
  - `appsettings.Development.json.example` exists only as a design reference; product-tree copy
    defaults to the DMS frontend project (D4) — minor placement choice, not asked.
