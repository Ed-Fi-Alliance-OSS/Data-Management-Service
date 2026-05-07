# DMS-916: Bootstrap DMS Design — Developer Environment Initialization

## Table of Contents

- [1. Introduction](#1-introduction)
  - [1.1 Bootstrap Contract Change (DMS-916 Review Delta)](#11-bootstrap-contract-change-dms-916-review-delta)
- [2. ODS InitDev Audit (Informational Reference)](#2-ods-initdev-audit-informational-reference)
- [3. ApiSchema.json Selection](#3-apischemajson-selection)
  - [3.5 Compatibility Validation](#35-compatibility-validation)
- [4. Security Database Configuration from ApiSchema.json](#4-security-database-configuration-from-apischemajson)
- [5. Bootstrap Contract Reference](#5-bootstrap-contract-reference)
- [6. API-Based Seed Data Loading](#6-api-based-seed-data-loading)
- [7. Credential Bootstrapping](#7-credential-bootstrapping)
- [8. Extension Selection and Loading](#8-extension-selection-and-loading)
- [9. Bootstrap Commands and Parameters](#9-bootstrap-commands-and-parameters)
  - [9.4.3 Thin Wrapper Contract](#943-thin-wrapper-contract)
  - [9.5 Bootstrap Working Directory](#95-bootstrap-working-directory)
- [10. School-Year Range Handling](#10-school-year-range-handling)
- [11. Backend Redesign Impact and DDL Provisioning](#11-backend-redesign-impact-and-ddl-provisioning)
- [12. IDE Debugging Workflow](#12-ide-debugging-workflow)
- [13. Companion Implementation Stories](#13-companion-implementation-stories)
- [14. Design Delivery vs. Operational Delivery](#14-design-delivery-vs-operational-delivery)
  - [14.1 Design-Complete Criteria](#141-design-complete-criteria)
  - [14.2 Operationally Blocked Criteria](#142-operationally-blocked-criteria)
  - [14.3 Blocking Cross-Team Dependencies](#143-blocking-cross-team-dependencies)
  - [14.4 Close-Out Status](#144-close-out-status)
- [15. Breaking Changes and Migration Notes](#15-breaking-changes-and-migration-notes)

## 1. Introduction

This document is the primary design artifact for DMS-916. It designs a composable, phase-oriented developer
bootstrap experience for the Ed-Fi Data Management Service, grounded in the existing Docker-first workflow
in `eng/docker-compose/`. The intended audience is the DMS platform team - specifically developers
bootstrapping a local environment for active development and testing.

**Scope**: This document covers the **developer bootstrap workflow only**. Sysadmin and agency deployment
concerns — including versioned seed data distribution and production provisioning — are out of scope.

The design builds on the existing `start-local-dms.ps1` Docker-first workflow, which already covers
roughly 50-60% of the proposed bootstrap steps (infrastructure startup, instance creation, basic
credential provisioning). The main gaps are extension selection, hash-aware schema provisioning,
additive security composition, API-based seeding, and IDE debugging. The enhancements proposed here are
incremental additions to that foundation, not a replacement.

**This design does not mirror ODS initdev.** DMS has fundamentally different constraints: it is
Docker-first with no codegen step, no dacpac-based database deployment, and no NuGet restore for
generated artifacts. The ODS audit in Section 2 is included as informational context only — it is not
a gap-fill checklist. The DMS bootstrap design stands on its own.

**Bootstrap contract**: The normative developer bootstrap contract is the composable set of phase-oriented
commands documented in `reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md`. Each phase command owns exactly
one primary concern; no single script is the source of all lifecycle semantics. Any thin wrapper over those
commands is a convenience entry point for the happy path only and is not the source of lifecycle semantics,
schema policy, claims logic, provisioning behavior, or any other phase-specific concern. The canonical
bootstrap always includes the Config Service because instance discovery, claimset seeding, and credential
bootstrap depend on it. The legacy `-EnableConfig` switch remains on the infrastructure-startup phase for
backward compatibility but is not a normative opt-out on the phase-oriented contract. Subsequent sections
address each design concern in turn.

**Story scope**: DMS-916 is a developer-bootstrap design spike. In-scope outcomes: schema selection,
security composition, schema provisioning, API-based seeding, extension selection, seed-loader credentials,
a phase-oriented bootstrap entry point with safe skip behavior, IDE debugging against Docker-managed
infrastructure, backend-redesign awareness, and the ODS audit as informational reference. Out of scope:
SDK generation, smoke-test runners, broader post-bootstrap automation, published compose/script parity
outside the local developer workflow, a persisted bootstrap control plane, broader tenant/credential
strategies, and a second non-Docker bootstrap surface.

**Platform note**: The canonical bootstrap targets PowerShell 7 (`pwsh`) and Docker on Windows, macOS, and
Linux. Examples in this document sometimes use Windows-style paths because that mirrors the team's current
workflow, but path-valued parameters such as `-ApiSchemaPath`, `-ClaimsDirectoryPath`, and `-SeedDataPath`
accept native host paths for the platform running `pwsh` (for example, `C:\dev\schema`,
`/Users/alex/schema`, or `/home/alex/schema`). The repo-local staging directory
`eng/docker-compose/.bootstrap/` is relative to the repository root and is therefore platform-neutral.

**Normative rules summary**: The rest of this document expands these rules; this list is the quickest way
to verify the design without re-reading every section.

1. The normative bootstrap contract is the composable set of phase commands in
   `command-boundaries.md`, not a monolithic control plane.
2. The wrapper, if implemented, is sequencing convenience only. It exposes direct developer-facing flags
   (forwarded to the appropriate phase command) and must not own schema, claims, provisioning, credential,
   retry, or continuation policy.
3. `ApiSchema.json` selection is the source of truth for API surface, security, and the exact DDL target
   for the run in every schema-selection mode.
4. Standard mode means `-Extensions`, including the omitted-`-Extensions` core-only case. Expert mode
   means `-ApiSchemaPath`; expert mode narrows bootstrap-managed seed selection and requires
   `-ClaimsDirectoryPath` when direct filesystem inputs do not include matching claims fragments.
5. Implementation is split: Story 00 delivers the direct filesystem `-ApiSchemaPath` path first; Story 06
   delivers standard `-Extensions` mode after Story 05.
6. DDL provisioning and seed loading are separate phases. DMS startup is never the authoritative schema
   deployment path.
7. Omitting `-Extensions` means core only once standard mode is implemented. DMS-916 does not preserve the
   legacy default that included extensions.
8. "Skip/resume" in this story means safe per-phase invocation plus optional same-invocation continuation
   through `-InfraOnly -DmsBaseUrl` after the pre-DMS phases have completed; it does not mean a persisted
   cross-invocation resume model.
9. Production provisioning, versioned seed distribution, and broader orchestration concerns are out of scope.

**Terminology**:

- **Standard mode**: schema selection through `-Extensions`, including the omitted-`-Extensions`
  core-only case; implemented after the Story 05 package-backed prerequisite.
- **Expert mode**: schema selection through `-ApiSchemaPath`.
- **Thin wrapper**: `bootstrap-local-dms.ps1` as optional sequencing convenience only.
- **Same-invocation continuation**: `-InfraOnly -DmsBaseUrl` carrying the current wrapper run through
  instance configuration and schema provisioning, then continuing against an IDE-hosted DMS process.
- **Explicit selectors**: `-InstanceId <long[]>` and `-SchoolYear <int[]>` flags passed to downstream
  phase commands (`provision-dms-schema.ps1`, `load-dms-seed-data.ps1`) to identify target DMS instances
  when the thin wrapper is not orchestrating the run.
- **Single-match auto-selection**: when no explicit selector is supplied and exactly one DMS instance
  exists in the current tenant scope, downstream phase commands auto-select that instance. Zero or multiple
  instances without an explicit selector is a fast-fail error.
- **Exact physical schema**: the concrete relational footprint implied by the selected staged schema set;
  this is the DDL target for the run.

**Recommended developer paths**:

- **Core only**: omit `-Extensions`; stage only the core schema, embedded claims, and core seed sources.
- **Core plus extension**: use `-Extensions <name>`; bootstrap stages the selected schema and
  claims automatically and loads built-in seed data only when the seed catalog defines it.
- **Custom schema**: use `-ApiSchemaPath`; bootstrap derives the base security set from the staged schema
  and the claim fragments available for that run. Pair direct filesystem schemas that need additional
  security metadata with `-ClaimsDirectoryPath`, and pair custom seed loading with `-SeedDataPath`.
- **IDE workflow**: use `-InfraOnly` for pre-DMS preparation, or `-InfraOnly -DmsBaseUrl` when the wrapper
  should complete instance configuration and schema provisioning before waiting for an IDE-hosted DMS
  process.

### 1.1 Bootstrap Contract Change (DMS-916 Review Delta)

The DMS-916 story originally called for a single normative entry point as the bootstrap control plane:

> **Replaced contract:** `eng/docker-compose/start-local-dms.ps1` is the single normative bootstrap
> control plane and single source of lifecycle semantics. All bootstrap phases — infrastructure startup,
> schema selection, security configuration, schema provisioning, instance creation, credential
> bootstrapping, and seed data loading — are owned by or orchestrated directly by this script.

Design review determined that combining all phases behind one monolithic command surface creates an
untestable, hard-to-maintain `initdev` clone. This section captures the resulting contract change.

The DMS-916 design adopts the following replacement instead:

> **New normative contract:** Bootstrap is a composable set of phase-oriented commands, each owning
> exactly one primary concern. No single script is the source of all lifecycle semantics. Any thin
> wrapper over those commands is a convenience entry point for the happy path only — not the source of
> schema policy, claims logic, provisioning behavior, or any other phase-specific concern.

The six phase commands are:

| Command | Primary concern |
|---|---|
| `start-local-dms.ps1` | Docker stack management, local identity setup, and service health waiting |
| `prepare-dms-schema.ps1` | Schema resolution and staging |
| `prepare-dms-claims.ps1` | Security (claims) staging |
| `configure-local-dms-instance.ps1` | DMS instance setup and optional smoke-test credentials |
| `provision-dms-schema.ps1` | Authoritative schema provisioning and hash validation |
| `load-dms-seed-data.ps1` | API-based seed data delivery |

The optional `bootstrap-local-dms.ps1` thin wrapper sequences those commands for the common developer
path. It introduces no additional policy. Per-phase ownership and boundary rules are in
[`command-boundaries.md`](command-boundaries.md).

---

## 2. ODS InitDev Audit (Informational Reference)

> **Scope note:** DMS is Docker-first with no code generation step. This audit is informational context for understanding ODS heritage, **not a gap-fill checklist**. The DMS bootstrap design stands on its own.

> **Maintenance note:** Detailed ODS step implementation notes (module sources, strategy internals, file system layout) are documented in [`reference-initdev-workflow.md`](reference-initdev-workflow.md). That document is a point-in-time reference. Update the DMS design artifacts only when a changed ODS concept materially affects the DMS audit narrative or scope boundaries captured here.

### 2.1 Phase Mapping

The ODS initdev is organized into eight abstract phases. The table below maps each phase to the DMS equivalent.

| ODS Phase | ODS Concern | DMS Equivalent |
|-----------|-------------|----------------|
| Phase 1: Environment Setup | Load modules, resolve cross-repo paths, detect platform | `Import-Module` calls at top of `start-local-dms.ps1`; `env-utility.psm1` reads `.env` file |
| Phase 2: Configuration | Assemble settings from parameters, config files, defaults; generate `appsettings.Development.json`; generate secrets | Phase-owned parameters across the composable bootstrap commands; `.env` / `.env.example` consumed via `env-utility.psm1`; `setup-openiddict.ps1` generates OpenIddict public/private keys |
| Phase 3: Code Generation | Generate NHibernate mappings and API metadata from data model | **Obsolete** — DMS derives API shape from the ApiSchema at runtime; no compile-time code generation |
| Phase 4: Build | Compile the .NET solution | **Obsolete for Docker path** — Docker images are built from `Dockerfile`s via `docker compose build`; local `dotnet build` is used only for IDE/unit-test workflows |
| Phase 5: Tool Installation | Install `EdFi.Db.Deploy`, `EdFi.BulkLoadClient` as .NET global tools | Partially covered - no explicit tool-install step; tools are baked into Docker images. DMS should reuse the existing repo-pinned NuGet resolution path for BulkLoadClient rather than introducing a new global-tool requirement |
| Phase 6: Database Provisioning | Drop/create/migrate Admin, Security, ODS databases per engine and install type | Covered by the DMS-916 design for Docker-managed PostgreSQL: `postgresql.yml` starts the engine, `Add-DmsInstance` / `Add-DmsSchoolYearInstances` in `Dms-Management.psm1` identify the target instances, and the explicit `provision-dms-schema.ps1` SchemaTools/runtime-owned provisioning and validation path performs the authoritative pre-start schema work for those targets. See [Backend Redesign Impact and DDL Provisioning](#11-backend-redesign-impact-and-ddl-provisioning). |
| Phase 7: Data Seeding | Load bootstrap/sample data through an API host | Partially covered - the legacy `-LoadSeedData` path currently lives on `start-local-dms.ps1` and calls `setup-database-template.psm1`, which executes SQL directly. DMS-916 replaces that with the phase-owned `load-dms-seed-data.ps1` API-based JSONL path |
| Phase 8: Verification | Run unit, integration, smoke tests | Partially covered — `Invoke-NonDestructiveApiTests.ps1` exists as a separate smoke test script; no integrated `-RunSmokeTest` flag on `start-local-dms.ps1` |

---

### 2.2 Step-by-Step Audit (17 ODS Steps)

The ODS initdev pipeline (`Initialize-DevelopmentEnvironment`) defines 17 steps, each wrapped in an `Invoke-Task` call that provides timing, error capture, and CI integration. Not all steps run on every invocation — each has a gating condition (see the "ODS Condition" column below). Steps 1–12 form the core pipeline: most run unconditionally (`always`), but steps 3 and 5 require `-UsePlugins` (off by default), step 6 is skipped with `-ExcludeCodeGen`, step 7 is skipped with `-NoRebuild`, and steps 11–12 are skipped with `-NoDeploy`. Steps 13–17 are opt-in via dedicated flags (`-RunPester`, `-RunDotnetTest`, `-RunPostman`, `-RunSmokeTest`, `-RunSdkGen`) and do not run unless explicitly requested. The "ODS Function" column lists the internal PowerShell function invoked at each step. In this design, those optional verification/generation steps are informational reference only and are not part of DMS-916 scope.

| # | ODS Step | ODS Function | ODS Condition | DMS Status | DMS Equivalent / Notes |
|---|----------|-------------|---------------|------------|------------------------|
| 1 | Clear errors | `Clear-Error` | always | **Obsolete** | PowerShell error state management is not replicated; Docker exit-code checks (`$LASTEXITCODE -ne 0`) with `throw` serve the same purpose inline |
| 2 | Assemble deployment settings | `Set-DeploymentSettings` | always | **Covered** | `env-utility.psm1` → `ReadValuesFromEnvFile` reads `.env`; script parameters override individual values; result flows as `$envValues` through the script |
| 3 | Merge plugin settings | `Merge plugin settings` | `-UsePlugins` | **Gap** | No extension/plugin selection mechanism exists. See [Extension Selection and Loading](#8-extension-selection-and-loading) for proposed `-Extensions` parameter design |
| 4 | Generate app settings | `Invoke-NewDevelopmentAppSettings` | always | **Covered** | `appsettings.json` per project is version-controlled; Docker env vars override at runtime via `local-dms.yml` / `local-config.yml`; `setup-openiddict.ps1 -InitDb` generates and stores RSA key pairs in PostgreSQL |
| 5 | Install plugins | `Install-Plugins` | `-UsePlugins` | **Gap** | No plugin/extension install step. Volume mounts in Docker Compose currently load extension ApiSchema files. Parameterized selection design is in [Extension Selection and Loading](#8-extension-selection-and-loading) |
| 6 | Code generation | `Invoke-CodeGen` | `!ExcludeCodeGen` | **Obsolete** | DMS has no code generation step. The ApiSchema JSON drives the runtime API surface without NHibernate mapping generation or similar compile-time artifacts |
| 7 | Build solution | `Invoke-RebuildSolution` | `!NoRebuild` | **Covered (Docker path)** | `docker compose build --no-cache` via `-r` flag rebuilds Docker images. For IDE workflow, `dotnet build` is run manually |
| 8 | Install DbDeploy tool | `Install-DbDeploy` | always | **Covered (implicitly)** | DMS-916 does not introduce a host-side database deployment tool install step. The authoritative provisioning path is the explicit `provision-dms-schema.ps1` SchemaTools/runtime-owned provisioning and validation phase that runs before DMS serves requests; it is not owned by DMS startup. |
| 9 | Reset test Admin database | `Reset-TestAdminDatabase` | always | **Obsolete (as standalone step)** | DMS has no separate Admin database. The Config Service database (`edfi_config`) is created and migrated automatically when the `local-config.yml` container starts |
| 10 | Reset test Security database | `Reset-TestSecurityDatabase` | always | **Obsolete (as standalone step)** | DMS has no separate Security database. Authorization metadata (claimsets) is seeded into the Config Service database via `AddExtensionSecurityMetadata` environment-variable path |
| 11 | Reset populated template database | `Reset-TestPopulatedTemplateDatabase` | `!NoDeploy` | **Gap** | DMS has no populated template database concept. Seed data loading replaces this. The `-LoadSeedData` flag currently runs `setup-database-template.psm1` (direct SQL). See [API-Based Seed Data Loading](#6-api-based-seed-data-loading) for proposed API-based replacement |
| 12 | Full database deployment | `Initialize-DeploymentEnvironment` | `!NoDeploy` | **Covered by DMS-916 design, pending implementation** | `Add-DmsInstance` and `Add-DmsSchoolYearInstances` in `Dms-Management.psm1` identify the target instances, and the DMS-916 design then requires the explicit `provision-dms-schema.ps1` SchemaTools/runtime-owned provisioning and validation path before DMS startup. The remaining work here is implementation delivery of that designed path, not a missing design contract. See [Backend Redesign Impact and DDL Provisioning](#11-backend-redesign-impact-and-ddl-provisioning) and Story 01. |
| 13 | Run Pester tests | `Invoke-PesterTests` | `-RunPester` | **Not applicable** | DMS test suite uses NUnit, not Pester. `dotnet test` is the equivalent; not integrated into `start-local-dms.ps1` |
| 14 | Run .NET integration tests | `Invoke-DotnetTest` | `-RunDotnetTest` | **Out of scope for DMS-916** | DMS integration tests already run separately via `build-dms.ps1 IntegrationTest` / `dotnet test`. Wiring them into bootstrap is optional orchestration work, not required to satisfy this story |
| 15 | Run Postman tests | `Invoke-PostmanIntegrationTests` | `-RunPostman` | **Out of scope for DMS-916** | DMS replaced Postman with NUnit-based E2E suites (`EdFi.DataManagementService.Tests.E2E` and `EdFi.InstanceManagement.Tests.E2E`). Keeping test-runner orchestration separate avoids inflating bootstrap scope |
| 16 | Run smoke tests | `Invoke-SmokeTests` | `-RunSmokeTest` | **Out of scope for DMS-916** | `Invoke-NonDestructiveApiTests.ps1` already exists as a separate script. Folding smoke tests into bootstrap is useful follow-on tooling, but not part of the story's required bootstrap contract |
| 17 | Generate SDK | `Invoke-SdkGen` | `-RunSdkGen` | **Out of scope for DMS-916** | DMS already has SDK generation tooling (`build-sdk.ps1`, `eng/sdkGen/`). Integrating it into bootstrap is optional post-bootstrap automation rather than a story requirement |

---

### 2.3 Summary Counts

| Classification | Count | Notes |
|----------------|-------|-------|
| Covered | 5 | Steps 2, 4, 7, 8, 12 |
| Obsolete / Not Applicable | 5 | Steps 1, 6, 9, 10, 13 |
| Gap | 3 | Steps 3, 5, 11 |
| Out of scope for DMS-916 | 4 | Steps 14, 15, 16, 17 |

> Note: Steps 9 and 10 are classified as Obsolete because they refer to ODS-specific Admin/Security database concepts that do not have a direct one-to-one equivalent in DMS. Step 12 is not a remaining DMS-916 design gap: the authoritative resolution is the explicit `provision-dms-schema.ps1` contract in [Section 11](#11-backend-redesign-impact-and-ddl-provisioning) and companion Story 01. The remaining work is implementation delivery of that already-defined path. Steps 14-17 remain outside DMS-916 scope even though they are valid optional ODS phases.

> Note: The `-SearchEngine` parameter (which controlled OpenSearch inclusion in the Docker stack) has already been removed from `start-local-dms.ps1` as part of the backend redesign. Any remaining documentation cleanup or retirement of the associated Docker Compose fragments (`opensearch.yml`, `opensearch-dashboards.yml`) is follow-up cleanup outside the DMS-916 bootstrap slices.

## 3. ApiSchema.json Selection

### 3.1 Current State

The DMS runtime already supports loading `ApiSchema.json` from the filesystem through the ASP.NET Core
settings `AppSettings__UseApiSchemaPath` and `AppSettings__ApiSchemaPath`. In the current manual
Docker Compose flow, `eng/docker-compose/local-dms.yml` feeds those settings from `.env` interpolation
variables named `USE_API_SCHEMA_PATH` and `API_SCHEMA_PATH`.

Today `run.sh` also contains a container-only convenience path: when `SCHEMA_PACKAGES` is populated, the
container downloads schema packages with `EdFi.DataManagementService.ApiSchemaDownloader` before launching
DMS. That explains the current `.env.example` pattern, for example:

> **Non-normative example** — the JSON block below illustrates the `SCHEMA_PACKAGES` format currently in
> `.env.example`; the exact package names and versions shown here are current-state illustration only and
> are not part of the DMS-916 contract.

```json
[
  {
    "version": "1.0.328",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.DataStandard52.ApiSchema"
  },
  {
    "version": "1.0.328",
    "feedUrl": "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
    "name": "EdFi.Sample.ApiSchema"
  }
]
```

When `AppSettings__UseApiSchemaPath=false` (the test default), DMS falls back to an `ApiSchema.json`
embedded as a build resource inside the DMS assembly. This path is used in unit and integration tests but
is not the intended developer-bootstrap path.

The gap for DMS-916 is that the current `SCHEMA_PACKAGES` flow materializes schema files only inside the
container. That is insufficient for a canonical bootstrap contract because:

- it does not tell bootstrap which exact files must be hashed before DMS starts,
- it gives an IDE-hosted DMS process no documented way to consume the same concrete schema files,
- it still requires developers to edit `.env` by hand to change the selected schema set.

### 3.2 ApiSchema.json as Single Source of Truth

The selected schema set drives everything downstream:

- **API surface** - only resources declared in the loaded schema files are exposed as REST endpoints.
- **DDL compatibility and deployment target** - the selected schema set determines the required schema
  deployment target for the environment: the resolved platform schema package version and
  `EffectiveSchemaHash` that bootstrap must provision or validate before DMS starts (see
  [Section 11](#11-backend-redesign-impact-and-ddl-provisioning)).
  Bootstrap reuses the existing DMS `EffectiveSchemaHash` algorithm; it does not introduce a second,
  package-only surrogate fingerprint. The physical relational deployment for that target is the exact table
  set implied by the selected core plus extension `ApiSchema.json` inputs.
- **Security/authorization configuration** - claimsets and resource-action mappings are seeded based on the resource names present in the schema (see [Section 4](#4-security-database-configuration-from-apischemajson)).
- **Seed data compatibility** - sample data packages are tied to a specific schema version; selecting a schema implicitly constrains which seed data is valid.

Because the schema is the single source of truth for **API surface**, **security selection**, and the
**required DDL target**, schema selection must happen before any other bootstrap step that depends on
resource visibility or schema compatibility. DDL provisioning remains a separate concern, but it is driven
by the schema deployment target derived from the selected `ApiSchema.json` set and validated with the live
database's stored `EffectiveSchemaHash` (see
[Section 11.5](#115-selected-apischemajson-drives-exact-physical-ddl-shape)).

> **ApiSchema.json authority - approved DMS-916 interpretation.** The selected schema set drives
> **API surface and security configuration**, and it drives the **DDL target** the environment must provision
> or validate against: platform schema version plus `EffectiveSchemaHash`. It also determines the exact
> physical schema footprint for the run. If the selected schema set changes from core-only to core plus an
> extension such as Sample, the DDL to provision changes accordingly. A database provisioned for a
> different schema selection is
> incompatible existing state, not a silently reusable superset. See
> [Section 11.5](#115-selected-apischemajson-drives-exact-physical-ddl-shape) for the authoritative treatment
> of the DDL/schema relationship.

### 3.3 Proposed Selection Mechanism

The target standard developer flow uses `-Extensions`, owned by `prepare-dms-schema.ps1` (see
[`command-boundaries.md` Section 3.1](command-boundaries.md#31-prepare-dms-schemaps1--schema-selection-and-staging)).
It is typed as `String[]` and accepts one or more extension names using normal PowerShell array binding.
The phase command does not rely on comma-splitting a single string; multi-extension examples therefore use
PowerShell array syntax such as `-Extensions "sample","extension2"`. Implementation is intentionally split:
Story 00 delivers the direct filesystem `-ApiSchemaPath` path first, while package-backed no-argument
core-only mode and named `-Extensions` modes are delivered by
[`Story 06`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) after asset-only ApiSchema packages
exist.

Bootstrap owns ApiSchema materialization. The stable contract is direct filesystem ApiSchema loading through
a normalized workspace, not the package format that produced the files. Before any DMS host starts,
bootstrap stages the selected inputs into the normalized file-based ApiSchema asset container described in
[`apischema-container.md`](apischema-container.md). The stable repo-local workspace is
`eng/docker-compose/.bootstrap/ApiSchema/`. The schema JSON files in that workspace are the only schema files
used for:

- bootstrap-time `dms-schema hash` calculation,
- Docker-hosted DMS startup,
- IDE-hosted DMS startup.

The same workspace also contains optional schema-adjacent static content supplied by the selected ApiSchema
packages, such as discovery/specification JSON and XSD files, plus `bootstrap-api-schema-manifest.json`.
Those static assets are runtime content inputs, not a second schema authority for hash or DDL decisions.

On same-checkout reruns, bootstrap treats this staged schema workspace as immutable while a running DMS
process or already-provisioned database may still depend on it. If the intended staged schema set matches
the existing workspace exactly, bootstrap reuses it as-is. If it differs, bootstrap fails fast and requires
`start-local-dms.ps1 -d -v` or equivalent environment reset rather than rewriting live schema files in
place.

Package names, package versions, and developer-supplied source directories are only selection inputs. They
are not themselves the hashed artifact. Package-backed selection is a later input-materialization path that
must converge on the same filesystem workspace. Direct `-ApiSchemaPath` loading remains a supported
bootstrap path after asset-only packages replace DLL-backed package distribution.

The staged ApiSchema workspace is the runtime schema/content contract for the run. DMS runtime reads
`eng/docker-compose/.bootstrap/ApiSchema/` and applies the same normalization rules to that staged file set:

- exactly one normalized schema file must have `projectSchema.isExtensionProject=false`; that file is the core
  schema passed to `dms-schema hash`,
- zero or more normalized schema files may have `projectSchema.isExtensionProject=true`; those files are passed as
  extensions,
- expert-mode `-ApiSchemaPath` fails fast if the staged directory does not normalize to that shape,
- optional discovery/specification JSON and XSD assets are indexed through
  `bootstrap-api-schema-manifest.json` manifest-relative paths for runtime content loading.

`bootstrap-api-schema-manifest.json` is only the runtime asset index for the normalized ApiSchema workspace.
It records selected project identity and file-location facts such as normalized schema paths and optional
schema-adjacent content paths. It is not the bootstrap compatibility manifest, not a second schema authority,
not a claims-file layout, and not a seed handoff file.

The root bootstrap manifest, `eng/docker-compose/.bootstrap/bootstrap-manifest.json`, records the stable
schema-selection facts and fingerprints that later bootstrap phases need: selection mode, selected
extensions, expected `EffectiveSchemaHash`, the ApiSchema workspace fingerprint, and the relative
`bootstrap-api-schema-manifest.json` path. Claims staging may inspect the staged schema files when it needs to
determine whether additional caller-supplied claim fragments are required; the ApiSchema asset manifest does
not carry separate seed or claims policy. `prepare-dms-claims.ps1` still owns all security staging and
validation.

Hash calculation always runs through the existing SchemaTools normalization path by invoking
`dms-schema hash <coreSchemaPath> <extensionSchemaPath...>` over that staged file set. Bootstrap does not
invent a package-only surrogate fingerprint. For DMS-916, bootstrap depends on the existing SchemaTools CLI
through a deliberately narrow black-box contract:

- the documented command names and required arguments/options,
- exit code `0` for success,
- any non-zero exit code for failure, and
- pass-through user diagnostics from SchemaTools stdout/stderr rather than bootstrap parsing text output to
  infer semantic failure subclasses.

The public invocation surface is documented in
`src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md`; the bootstrap design reuses that public
surface rather than defining a bootstrap-specific alternate CLI. It does not assume the README is the sole
normative source for every internal provisioning safety rule implemented behind that CLI.

> **Implementation note:** The assumed command names (`dms-schema hash`, `dms-schema ddl provision`) and
> their argument shapes reflect the SchemaTools CLI as documented at design time. These must be verified
> against the actual SchemaTools README before Story 01 implementation begins; any discrepancy takes
> precedence over this design document.

Three usage modes are supported in the target design. Story 00 implements Mode 3 first; Story 06 implements
Modes 1 and 2 after Story 05 publishes asset-only packages.

#### Standard Development Flows

Modes 1 and 2 cover the vast majority of development workflows. Use these for day-to-day environment setup.

**Mode 1 - Default (core Ed-Fi only)**

Omit `-Extensions` entirely. In the target package-backed flow, bootstrap resolves only the standard core
Data Standard ApiSchema asset package (e.g. `EdFi.DataStandard52.ApiSchema`; **exact package name must be
confirmed against the NuGet feed before Story 06 implementation begins**, not Story 00 or Story
01) host-side, extracts it into an isolated package-specific temporary directory, normalizes its asset payload into
`eng/docker-compose/.bootstrap/ApiSchema/`, and computes the expected `EffectiveSchemaHash` from the
normalized core schema file. The target package payload is asset-only as defined in
[`apischema-container.md`](apischema-container.md); publishing that package shape is tracked separately in
[`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md).

This package-backed default is a convenience acquisition path over the same filesystem contract. It is not a
prerequisite for implementing or validating Story 00 bootstrap against an already-materialized ApiSchema
directory through `-ApiSchemaPath`.

This is an intentional migration from the current `SCHEMA_PACKAGES`-driven local default documented under
`eng/docker-compose`, which stages Data Standard 5.2 plus extensions today. DMS-916 defines the normative
developer bootstrap profile as core only when `-Extensions` is omitted. Any extension needed for a run must be
selected explicitly through `-Extensions` or supplied through `-ApiSchemaPath`.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1
```

**Mode 2 - Core + named extensions**

Pass one or more extension names. In the target package-backed flow, the bootstrap script resolves
the core asset package plus the corresponding extension asset packages host-side, extracts each package in
isolation, then normalizes the resulting schema JSON and optional schema-adjacent content into the same
ApiSchema asset workspace before computing the expected `EffectiveSchemaHash`.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample"
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample","extension2"
```

Extension artifact availability is determined by whether the requested extension's package and companion
artifacts can be resolved from the configured package and artifact sources. A resolution failure produces a
clear pre-Docker error for the requested extension name.

#### Direct Filesystem ApiSchema Usage

`-ApiSchemaPath` is the direct filesystem input to the stable bootstrap contract. It remains supported even
after package-backed standard mode moves to asset-only packages. It is still an expert workflow for seed and
extension ergonomics because built-in seed selection is disabled and direct filesystem inputs may need
explicit security input.

**Mode 3 - Custom local path (Direct Filesystem / Expert Workflow)**

> **WARNING:** `-ApiSchemaPath` is the stable direct filesystem input, but it remains an expert workflow for
> custom schema ergonomics. Bootstrap still stages the base claimset set available for the run, but built-in
> seed selection is disabled in this mode.
> If the staged schema requires claim fragments that are not supplied by the direct filesystem input,
> `-ClaimsDirectoryPath` is required. Use `-SeedDataPath` for custom seed input. Prefer Modes 1 and 2 for
> standard development workflows.

Pass a local filesystem path to a directory containing one or more pre-built `ApiSchema.json` files.
Bootstrap stages every `ApiSchema*.json` file from that directory into the same repo-local workspace used by
Modes 1 and 2, validates that exactly one staged file is a non-extension project, and computes the
expected `EffectiveSchemaHash` from the staged copies. The staged schema set and available claim fragments
drive base claimset selection for the run. Direct filesystem schemas that require additional claims metadata
are paired with explicit `-ClaimsDirectoryPath` input. Built-in seed selection remains disabled in this mode.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1 -ApiSchemaPath "C:\dev\my-custom-schema"
```

`-Extensions` and `-ApiSchemaPath` are mutually exclusive. If both are specified, the bootstrap script exits
with a clear error. When `-ApiSchemaPath` is used, bootstrap still derives the automatic base claimset set
from the staged schema and available claims inputs, while custom claims and custom seed inputs continue to use
`-ClaimsDirectoryPath` and/or `-SeedDataPath`.

**Fail-fast expert-mode contract:** `-ApiSchemaPath` is intentionally not a full alias for standard-mode
ergonomics.

- If seed delivery is requested for a staged `-ApiSchemaPath` run, bootstrap requires `-SeedDataPath` and fails during
  parameter validation when it is missing. The built-in `Minimal` or `Populated` seed-template defaults do
  not apply in expert custom-schema mode.
- If the staged `-ApiSchemaPath` workspace includes schemas that need additional claim fragments, bootstrap requires
  `-ClaimsDirectoryPath` and fails during claims-phase validation when it is missing. Bootstrap validates
  those fragments structurally, but does not prove complete authorization coverage for every custom resource.
- `-SeedTemplate` remains part of the standard bootstrap-managed seed contract only. When `-ApiSchemaPath` is
  supplied, passing `-SeedTemplate` is an error because expert mode disables bootstrap-managed seed-source
  selection.

**Bootstrap-time warning:** When `-ApiSchemaPath` is used, the bootstrap script emits a warning before
proceeding. The warning communicates that built-in seed selection is disabled, that automatic base claims
come only from the claims inputs available for the run, and that the caller must provide `-SeedDataPath`
and any required `-ClaimsDirectoryPath` input explicitly. The exact warning text is an implementation detail
and is not prescribed here.

**Expert companion parameters for a complete custom bootstrap:** To avoid editing environment variables or
Docker mounts by hand, Mode 3 introduces two expert-only companion parameters alongside `-ApiSchemaPath`:

- `-ApiSchemaPath` - supplies the custom schema directory (normalized into the staged schema workspace)
- `-ClaimsDirectoryPath` - supplies a directory of `*-claimset.json` files used for additive CMS
  hybrid-mode loading on top of the automatic base claimset set.
- `-SeedDataPath` - supplies custom JSONL seed files; without this, no seed data is loaded

### 3.4 How Selection Flows Through the System

The following sequence describes the canonical schema flow for all bootstrap modes:

```text
prepare-dms-schema.ps1
  -> Resolve schema inputs from -Extensions or -ApiSchemaPath
     -> Mode 1/2 target flow after Story 05: package-backed materialization resolves asset-only ApiSchema packages on the host,
        extracts each package in isolation, and normalizes schema JSON plus optional static content into
        eng/docker-compose/.bootstrap/ApiSchema/
     -> Mode 3 Story 00 flow: direct filesystem materialization copies/normalizes developer-supplied ApiSchema*.json files
        and optional static content into the same workspace
  -> Stage one normalized core schema file plus 0..N normalized extension schema files
  -> Write bootstrap-api-schema-manifest.json with manifest-relative schema/content paths
  -> Run `dms-schema hash <coreSchemaPath> <extensionSchemaPath...>` over the normalized schema files
  -> Write/update .bootstrap/bootstrap-manifest.json schema section with selection mode, selected extensions,
     EffectiveSchemaHash, ApiSchema workspace fingerprint, and the relative ApiSchema manifest path
  -> Docker-hosted DMS:
     -> bind-mount eng/docker-compose/.bootstrap/ApiSchema/ to /app/ApiSchema
     -> set AppSettings__UseApiSchemaPath=true
     -> set AppSettings__ApiSchemaPath=/app/ApiSchema
     -> leave SCHEMA_PACKAGES empty so run.sh does not perform a second download
  -> IDE-hosted DMS:
     -> set AppSettings__UseApiSchemaPath=true
     -> set AppSettings__ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema
  -> DMS loads schema JSON and schema-adjacent static content from the filesystem
```

> **Note:** This diagram shows only the schema delivery flow. Security staging happens before the Config
> Service starts, and DDL deployment mode is decided after instance creation but before the DMS process
> starts. See [`command-boundaries.md`](command-boundaries.md) for the authoritative full phase ordering.
> Runtime loading of schema-adjacent static content is implemented by Story 04; package production for the
> asset-only shape is implemented by Story 05. Both target the same filesystem workspace contract, so direct
> `-ApiSchemaPath` loading can be implemented and retained independently of package publication. Story 00
> implements that direct filesystem path only; package-backed Modes 1 and 2 belong to Story 06.

This reuses the existing host-side pattern already present in `eng/preflight-dms-schema-compile.ps1`: the
host materializes concrete schema files first, then downstream steps operate on those staged files rather
than re-resolving packages or inferring schema shape from container-only environment variables.

**NuGet feed failure handling:** If the configured NuGet feed is unreachable during host-side schema package
resolution or catalog-advertised built-in extension seed package download, the owning phase command fails fast with a clear error message
including the feed URL and HTTP status. No partial downloads are attempted. For offline or air-gapped
environments, use `-ApiSchemaPath` (Mode 3) and, if loading data, `-SeedDataPath` to bypass NuGet entirely
and supply pre-downloaded artifacts from a local directory. That staged custom schema set still drives
automatic base security selection from the claims inputs available for the run. If additional non-core
security fragments are needed, `-ClaimsDirectoryPath` is required for caller-supplied fragments; bootstrap
does not infer security rules for arbitrary custom resources.

### 3.5 Compatibility Validation

For each extension in `-Extensions`, bootstrap resolves the schema package and any companion security
fragment metadata from the configured artifact sources. The seed phase owns a separate seed catalog that may
define an optional built-in seed data package for the same extension name:

1. **Schema package** (e.g., `EdFi.Sample.ApiSchema`) - determines API surface
2. **Security fragment** (e.g., `004-sample-extension-claimset.json`) - determines authorization rules
3. **Optional built-in seed data package** (when present in the seed catalog) - determines bootstrap-managed
   sample data

**Version contract**: Schema and built-in seed NuGet packages for a given extension must align to the same
Data Standard version when both artifacts exist for that extension. Security fragments (e.g.,
`004-sample-extension-claimset.json`) are repo-bundled files, not NuGet packages, and do not carry NuGet
version metadata; their version alignment follows the artifact source that provides them. Each phase consumes
only the metadata it owns: schema package fields in `prepare-dms-schema.ps1`, security fragment fields in
`prepare-dms-claims.ps1`, and seed package fields in `load-dms-seed-data.ps1`.

**Validation rules**:

- When `-Extensions` is used (Mode 1/2): Schema and claims phase commands resolve their owned artifact types
  for each selected extension. The seed phase resolves seed artifacts from its seed catalog. If the
  schema package for a requested extension cannot be resolved, schema staging fails before starting
  containers. If the security fragment for a requested extension cannot be resolved, claims staging fails
  before starting containers. Seed package requirements when seed delivery runs are governed by the
  seed-specific rule below.
- When `-ApiSchemaPath` is used (Mode 3): Schema hashing and DDL validation still work because bootstrap
  stages and hashes the exact files. What is disabled is bootstrap-managed seed-package selection, not
  automatic base security selection from the claims inputs available for the run. If the staged set needs
  additional non-core claims metadata, `-ClaimsDirectoryPath` is required and bootstrap validates those
  fragments only structurally. Bootstrap emits a warning indicating that compatibility with custom claimset
  fragments and custom seed data is validated against the explicit companion inputs for this run. The exact
  warning text is an implementation detail.
- When `-ApiSchemaPath` is used (Mode 3): The staged directory must normalize to exactly one
  `projectSchema.isExtensionProject=false` file and zero or more `true` files. Any other shape is a
  bootstrap-time error.
- When seed delivery runs: For each selected extension in the root bootstrap manifest, check the seed
  catalog for a built-in seed package entry.
  - If the extension has a seed catalog entry and its NuGet seed package fails to resolve (network/feed
    error): error before starting containers.
  - If the extension has no seed package entry in the seed catalog: informational warning only —
    Bootstrap emits an informational warning indicating no built-in seed package is available for
    that extension; schema and security configuration from the extension still apply and the
    developer may provide custom seed data via `-SeedDataPath`.

**Ownership**: The bootstrap script owns pre-flight validation of the artifacts it is asked to stage. DMS
runtime does not validate artifact compatibility - it loads whatever staged schema files it receives. This
makes pre-flight checks the only safety net.

### 3.6 Versioning

NuGet package versions for package-backed schema selection are maintained as defaults inside the bootstrap
script or its package-resolution metadata. For the initial implementation, a single version applies to all
packages in a given bootstrap invocation, matching the current `.env.example` pattern. A future
`-SchemaVersion` parameter (or per-extension version map) could override the default version when needed; this
is not included in the initial parameter set (Section 9.3) and is deferred to a follow-up enhancement.

## 4. Security Database Configuration from ApiSchema.json

### 4.1 Current State

The Config Service (CMS) owns authorization metadata: the claims hierarchy (resource claims and their
authorization strategy assignments) and claimsets (named permission bundles assigned to API clients).
On startup, CMS loads this metadata into the `edfi_config` PostgreSQL database using one of two modes
controlled by `DMS_CONFIG_CLAIMS_SOURCE`:

- **`Embedded` (default)** - CMS loads `Claims.json` from the assembly's embedded resources. This is
  the core Ed-Fi claims hierarchy covering standard Data Standard resources. No external files are
  needed.
- **`Hybrid`** - CMS loads the same embedded `Claims.json` as the base, then discovers every
  `*-claimset.json` file in `DMS_CONFIG_CLAIMS_DIRECTORY` (container path `/app/additional-claims`)
  and composes them in deterministic filename order on top of the base using the current CMS additive
  fragment behavior.

**Fragment file contract:** each `*-claimset.json` file is a JSON object with a `resourceClaims` array
and an optional top-level `name`. When `name` is absent, the current CMS implementation falls back to the
filename without extension as the effective claim-set name for composed attachments. Each entry in
`resourceClaims` can carry:

- `isParent: true` - declares a new parent node (or extends an existing one) in the resource-claims
  hierarchy, including nested `children`, per-resource `_defaultAuthorizationStrategiesForCrud`, and
  per-claimset `claimSets` entries with `actions` and `authorizationStrategyOverrides`.
- `isParent: false` (or omitted) - attaches a claimset entry to an existing leaf resource claim and may
  supply `authorizationStrategyOverridesForCrud`.

Fragment files therefore extend the claims hierarchy; they do not replace or augment the top-level
`claimSets` collection loaded from embedded `Claims.json`. Under the current CMS additive-fragment
behavior, the embedded top-level claim-set definitions remain authoritative and fragments compose only into
the hierarchy. In practice this means:

- a fragment may create or extend resource-claim nodes,
- a fragment may attach actions to claim set names that already exist in embedded `Claims.json`,
- a fragment may not, by itself, introduce a brand-new top-level claim set definition that
  `Add-Application` can select later.

DMS-916 therefore bounds `-ClaimsDirectoryPath` to additive fragments that target claim sets already
present in embedded `Claims.json`. Supporting arbitrary new claim set definitions would require a separate
full-file `Claims.json` replacement path (filesystem mode), which is outside this story.

The extension claimset files live in
`src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/`. Docker Compose
mounts that directory to `/app/additional-claims` inside the CMS container. `start-local-dms.ps1`
enables hybrid mode today via the `-AddExtensionSecurityMetadata` switch and Docker startup wiring. DMS-916
replaces that transient cross-process handoff shape with the claims section of the root bootstrap manifest
under `.bootstrap`, which carries the effective startup inputs for the run without introducing a separate
claims handoff artifact.

**Gap**: The current mechanism is all-or-nothing. When `-AddExtensionSecurityMetadata` is set, every
claimset fragment in the directory is loaded regardless of which extensions the developer has actually
selected. There is no filtering by extension. A developer bootstrapping with `-Extensions sample` still
loads other extension claimsets, and vice versa.

### 4.2 Why ApiSchema.json Drives Security Configuration

The selected ApiSchema.json determines which resources exist in the DMS API surface. Security
configuration must be consistent with those resources:

- A claimset fragment that references resources from a selected extension is only meaningful when that
  extension's ApiSchema is loaded. Loading extension claimsets without the corresponding extension
  resources causes authorization checks to operate on resource names that DMS does not recognize,
  producing confusing errors.
- Conversely, if extension resources are present in the schema but no matching extension claimset fragment
  is loaded, API clients will have no claimset that grants access to those endpoints even though they
  exist.
- The security DB must therefore be in a consistent state with the schema before seed data loading
  begins.

### 4.3 Proposed Automatic Security Configuration

> **Scope of automatic derivation (precise).** "Automatic" here means core security plus the matching
> security fragments available for the selected schema set. `prepare-dms-claims.ps1` pairs staged schema
> inputs with those base claims inputs whether the schema came from omitted `-Extensions`, named
> `-Extensions`, or `-ApiSchemaPath`. If `-ApiSchemaPath` stages schemas whose security fragments are not
> supplied by the run, bootstrap cannot infer security rules for those arbitrary resources;
> `-ClaimsDirectoryPath` is required and remains an additive, caller-supplied input.

After schema selection is resolved for the run, `prepare-dms-claims.ps1` derives the corresponding base
security configuration for the staged schema set.
`-ClaimsDirectoryPath` remains available for additive security fragments without editing environment
variables or Docker mounts manually, and is required when expert `-ApiSchemaPath` mode needs additional
non-core security metadata. Each phase consumes only the artifact metadata it owns: schema package metadata
in `prepare-dms-schema.ps1`, security fragment metadata
in `prepare-dms-claims.ps1`, and optional seed package metadata in `load-dms-seed-data.ps1`. Built-in seed
loading participates only when the seed phase has a concrete built-in seed package for the selected
extension.

The bootstrap logic is:

1. **Core security metadata is always loaded.** The embedded `Claims.json` (Embedded mode) covers all
   core Ed-Fi Data Standard resources. No extra configuration is needed for Mode 1 (core only).

2. **Extension security metadata is loaded based on the effective staged schema set.** If the staged
   schema set includes extension resources with matching security fragments, the bootstrap script switches CMS
   to `Hybrid` mode and stages the corresponding fragment files. This avoids requiring CMS-side filtering
   changes and keeps selection logic entirely in the bootstrap script. For DMS-916, a bootstrap-managed
   extension fragment must attach permissions to the embedded claim sets required by the developer workflow:
   `EdFiSandbox` for general developer access and `SeedLoader` when a built-in extension seed package exists.

   **Contract:** If a bootstrap-managed security fragment for a selected extension cannot be resolved,
   bootstrap fails before container startup with a clear error listing the missing file(s). If seed delivery
   runs and the selected extension fragment does not attach `SeedLoader` permissions for the extension
   resources emitted by that extension's built-in seed package, bootstrap also fails before container startup.
   If expert `-ApiSchemaPath` mode stages schemas that need additional claim fragments, bootstrap requires
   `-ClaimsDirectoryPath` and fails during claims-phase validation when it is missing.

   **Compose wiring contract:** CMS continues to read fragments from `/app/additional-claims`, and the
   container-side path setting remains aligned to that location. The relative host-side source for the bind
   mount and the effective Embedded-vs-Hybrid startup mode are carried through the claims section of
   `eng/docker-compose/.bootstrap/bootstrap-manifest.json`, which `start-local-dms.ps1` consumes at Docker
   startup. The Config Service compose files (`local-config.yml` and `published-config.yml`) own the
   `/app/additional-claims` mount.
   DMS compose files (`local-dms.yml` and `published-dms.yml`) do not consume claimset fragment files and
   should not mount that directory.

3. **Additional claimset fragments can be layered in through `-ClaimsDirectoryPath`.** When a developer
    uses `-ApiSchemaPath` with schemas that need additional security metadata or otherwise needs custom
    claimset fragments,
    `-ClaimsDirectoryPath` points to a local directory containing one or more `*-claimset.json` files.
    Bootstrap validates that the path exists, then stages those files into the same claims workspace used for
    extension-derived fragments. This makes `-ClaimsDirectoryPath` additive rather than mutually exclusive
    with `-Extensions`.

   **Contract:** If `-ClaimsDirectoryPath` is supplied and the directory does not exist or contains no
   `*-claimset.json` files, bootstrap fails before container startup with a clear error. Bootstrap also
    parses the embedded base `Claims.json` and fails if any staged fragment references a claim set name that
    does not already exist there.

    **Expert-mode completeness boundary:** In `-ApiSchemaPath` mode, bootstrap validates only staged
    fragment presence and structure for caller-supplied claims inputs. For DMS-916, the acceptance criterion
    "automatic claimset loading based on selected schema" is satisfied by staging the claims inputs available
    for the run. Bootstrap does not attempt to infer or prove authorization coverage for arbitrary
    expert-supplied non-core resources ahead of runtime. If the custom schema and custom claims are
    incomplete, the resulting authorization failures remain DMS or BulkLoadClient runtime errors rather than
    a bootstrap-owned certification gap.

4. **Bootstrap performs structural claims-workspace validation before container startup.** The bootstrap
   surface validates only the staged conditions it directly owns:
   - duplicate filenames remain fail-fast because the staged workspace must contain one deterministic file
     per path;
   - every staged fragment must be parseable JSON and follow the `*-claimset.json` file contract used by CMS;
   - any claim set name referenced by a staged fragment must already exist in the embedded base
     `Claims.json`.

   Bootstrap validation is structural only. Bootstrap does not inspect attachment overlap, predict the
   fully composed authorization graph, or define a second semantic claims authority. CMS startup is the
   authoritative composition gate: any semantic composition issue — including overlapping claim
   attachments — is surfaced as a CMS startup or runtime error rather than a bootstrap-side rule.

5. **The DMS-916 normative bootstrap contract has no alternate non-schema-driven security-selection path.**
   Within this design, ApiSchema-driven staging is the only bootstrap-managed source of security selection.
   Legacy flags remain historical context only and are not part of the normative contract.

### 4.4 Security Configuration Boundary

Security metadata loading is a CMS startup concern: CMS reads and seeds claimsets when the container
starts. Current CMS startup behavior only performs the initial claims load when the claim tables are empty;
it does not replace an already populated claims document during a normal bootstrap rerun. DMS-916 therefore
treats changed security inputs as incompatible existing state rather than a hot-reload scenario. Same-checkout
reruns without teardown are supported only when the intended security inputs match the already-staged
claims workspace.

The authoritative phase order and ownership rules live only in
[`command-boundaries.md`](command-boundaries.md), especially Sections 3.2, 3.3, and 4. This section records
the security-specific acceptance context:

- `prepare-dms-claims.ps1` derives and stages the claims inputs before the Config Service starts.
- `start-local-dms.ps1` consumes the bootstrap manifest claims section and treats Config Service readiness
  as both service health and claims-ready verification for the staged inputs.
- A missing claimset for an extension resource causes authorization failures during API-based seed loading,
  so seed delivery never proceeds until the claims-ready gate has passed.
- Because the current CMS startup path only initial-loads claims into empty tables, changing the selected
  security inputs against a populated `edfi_config` database is outside the supported rerun surface for
  DMS-916. Bootstrap requires teardown rather than trying to replace claims in place.

For expert-mode bootstraps, `prepare-dms-claims.ps1` still derives the base claimset set from the staged
schema and available claims inputs. If the staged schema selection requires additional non-core security
metadata, `-ClaimsDirectoryPath` is required. Bootstrap stages the combined validated set into one workspace
directory and records the
effective CMS startup inputs in the root bootstrap manifest under `.bootstrap`. Those
fragments may extend resource-claim nodes, but they may only attach actions to effective claim set
references that already exist in embedded `Claims.json`. Bootstrap validates the same effective reference
set defined in `command-boundaries.md`: explicit `resourceClaims[].claimSets[].name` values, plus the
top-level fragment `name` only when CMS composition uses it as an implicit claim set name for a non-parent
resource claim.

### 4.5 Claims-Loading Approach: Safe Path vs. Legacy Flag

The bootstrap design uses the claims section of `eng/docker-compose/.bootstrap/bootstrap-manifest.json` to
carry the effective Config Service claims inputs for the run, including the equivalent of Hybrid vs Embedded
startup mode and the relative staged claims workspace when Hybrid mode is active. This is the intended path for
every schema-selection flow, with `-ClaimsDirectoryPath` acting only as an additive input.
For expert `-ApiSchemaPath` flows that need additional non-core security metadata, that additive input is
required because bootstrap does not infer security fragments for arbitrary resources.

**`DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING`** is a separate, legacy flag used for dynamic management endpoints (where the Config Service API is called post-boot to push arbitrary claimset data) and for legacy dev-only workflows. It is **not** required by the proposed `-Extensions` bootstrap path and is **not** part of the standard bootstrap flow described in this design.

All three schema-selection modes are explicitly designed to operate without this flag:

- **Mode 1 (core only)**: the bootstrap manifest claims section resolves to Embedded mode - no extra flag needed.
- **Mode 2 (named extensions)**: the bootstrap manifest claims section resolves to Hybrid mode and points at
  the staged claims workspace containing only the selected claimset files - no extra flag needed.
- **Mode 3 (expert `-ApiSchemaPath`)**: claims loading is automatic from the staged schema and available
  claims inputs. When the staged schema set contains extension resources with matching fragments, the same
  bootstrap manifest mechanism stages the matching base fragments and selects Hybrid mode automatically.
  Additional non-core claim fragments use `-ClaimsDirectoryPath` and that same mechanism. The
  dangerously-enable flag is **not** required in expert mode either.
- `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` is **not set** in any of the three modes above.

Removing `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` from bootstrap usage belongs to the
schema-and-security-selection slice rather than a separate bootstrap story.

## 5. Bootstrap Contract Reference

The authoritative bootstrap sequence, phase ownership, parameter ownership, and rerun-sensitive boundary
rules are defined only in [`command-boundaries.md`](command-boundaries.md). This section intentionally does
not restate that contract; doing so creates a second source of truth.

Read this design document as rationale and acceptance context for the individual concerns:

- schema selection and staged ApiSchema inputs: [Section 3](#3-apischemajson-selection);
- claims and CMS startup inputs: [Section 4](#4-security-database-configuration-from-apischemajson);
- API-based seed delivery: [Section 6](#6-api-based-seed-data-loading);
- credentials: [Section 7](#7-credential-bootstrapping);
- extension selection: [Section 8](#8-extension-selection-and-loading);
- developer-facing examples and IDE guidance: [Sections 9](#9-bootstrap-commands-and-parameters) and
  [12](#12-ide-debugging-workflow).

If any example, section narrative, or companion story conflicts with `command-boundaries.md`, the command
boundary contract wins.

## 6. API-Based Seed Data Loading

This section replaces the current direct-SQL seed path (`setup-database-template.psm1`) with an API-based flow that routes all seed data through the DMS REST API. The change addresses real-world data integrity issues caused by bypassing API validation (see Current Problem in the story).

### 6.0 Bootstrap Dependency Summary — BulkLoadClient

`EdFi.BulkLoadClient` is a required external dependency for API-based seed loading, but bootstrap should
consume it through the repo's existing pinned NuGet-resolution path rather than as a globally installed
machine tool. All API-based seed loading in the bootstrap depends on it through the
`load-dms-seed-data.ps1` phase defined in
[`command-boundaries.md` Section 3.6](command-boundaries.md#36-load-dms-seed-dataps1--seed-delivery).

**Cross-team dependency:** BulkLoadClient must be extended by the ODS team to support JSONL input files
before API-based seeding can be operationally delivered (see
[Section 14.3](#143-blocking-cross-team-dependencies)). The bootstrap-side consumption contract for that
dependency is defined in [Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract) below.

**Pre-flight check:** When `load-dms-seed-data.ps1` runs, it resolves the pinned
BulkLoadClient package and fails immediately if the package cannot be downloaded/resolved or if the required
JSONL interface is not available (see [Pinned BulkLoadClient Resolution](#633-pinned-bulkloadclient-resolution)).

**Operational gate:** API-based seeding remains blocked until BulkLoadClient JSONL support is delivered and
verified against DMS. Any temporary retention of the current SQL seeding path during implementation
sequencing is an operational bridge, not part of the DMS-916 design contract.

---

### 6.1 BulkLoadClient Bootstrap Consumption Contract

> **Note:** This section defines only the external contract the developer bootstrap consumes.

The Ed-Fi BulkLoadClient is an external ODS/API ecosystem tool (see ODS-6738 for context). This section
records only the minimum callable surface and result semantics that the DMS bootstrap depends on. It is a
consumer-boundary document for DMS-916, not a product-design specification for BulkLoadClient, and it does
not prescribe internal BulkLoadClient implementation details.

Only the CLI surface consumed by bootstrap is in scope here. Packaging, installer UX, and broader
BulkLoadClient product behavior remain outside DMS-916.

#### 6.1.1 Minimum Invocation Surface Assumed by Bootstrap

| Flag | Expected by bootstrap | Description |
|------|-----------------------|-------------|
| `--input-format jsonl` | Yes | Activates JSONL mode; each line in an input file is one JSON resource body |
| `--data <directory>` | Yes | Directory containing the JSONL seed workspace prepared by bootstrap. |
| `--base-url <dms-url>` | Yes | DMS API host root for the current flow (for example, `http://localhost:8080`). |
| `--key <key>` | Yes | OAuth client key for credential grant |
| `--secret <secret>` | Yes | OAuth client secret |
| `--token-url <oauth-url>` | Yes | Token endpoint for the selected identity provider. In self-contained CMS mode this is `http://localhost:8081/connect/token` when no route qualifier is active, or `http://localhost:8081/connect/token/{schoolYear}` in the school-year-qualified workflow. In Keycloak mode it remains the provider-native realm token URL. |
| `--year <school-year>` | No | Used only for the existing school-year developer workflow. When present, the DMS-local route qualification remains `/{schoolYear}/data/...` rather than an ODS-style `/data/v3/{year}/...` convention. |

The single-segment `/{schoolYear}/data/...` examples in this section are intentionally scoped to the
local DMS-916 bootstrap profile when route qualifiers are configured as `schoolYear` only. Broader DMS
route-context shapes remain valid repo behavior outside this narrowed local workflow and are not redefined
here.

Bootstrap treats `--key`, `--secret`, `--token-url`, and `--base-url` as one atomic invocation set. If the
resolved tool surface cannot accept the full set for a run, bootstrap treats the seed-delivery path as
unsupported and fails fast rather than defining an alternate DMS-owned invocation mode.

#### 6.1.2 Bootstrap-Relevant Runtime Expectations

Bootstrap depends on the following runtime behaviors and does not constrain more than this:

- The tool consumes the bootstrap-prepared JSONL workspace from `--data`.
- In the school-year developer workflow, bootstrap supplies `--year` together with the matching
  `/connect/token/{schoolYear}` token URL for the existing local DMS route-qualified shape.
- Ordering of files within the `--data` directory, if the JSONL interface defines one, remains owned by
  BulkLoadClient and the seed-source manifest rather than by DMS bootstrap.
- When bootstrap passes `--continue-on-error`, BulkLoadClient must classify duplicate-resource
  `409 Conflict` responses as non-fatal and return `0` when every failure is within that tolerated conflict
  set; broader conflict classification, retry behavior, batching, and request shaping remain
  BulkLoadClient-owned behavior.
- Exit code `0` means the run completed within the accepted bootstrap success boundary; any fatal failure
  returns non-zero.
- The tool emits terminal diagnostics for the run, and bootstrap surfaces those diagnostics directly rather
  than defining a second DMS-owned result taxonomy.

---

### 6.2 Seed Source Selection for Developer Bootstrap

#### 6.2.1 Bootstrap Consumption Boundary

DMS-916 defines only the **developer bootstrap consumption contract** for seed data. The built-in core
developer templates selected by `-SeedTemplate` are repo-local JSONL assets owned by the DMS bootstrap
implementation, not published deployment packages. Broader artifact distribution concerns - package naming,
publishing workflow, versioning policy, and long-term seed-data distribution outside this repo - remain out
of scope for this spike and stay with DMS-1119, but they do not block the DMS-916 developer bootstrap path.

For DMS-916, bootstrap materializes a local directory of JSONL files for the selected developer seed source.
The seed source for a run can come from:

- the repo-local `eng/docker-compose/seed-data/minimal/` directory selected by `-SeedTemplate Minimal`,
- the repo-local `eng/docker-compose/seed-data/populated/` directory selected by `-SeedTemplate Populated`,
- catalog-advertised built-in extension seed artifacts resolved from selected extensions when the seed catalog defines a
  built-in seed source, or
- a developer-supplied directory selected by `-SeedDataPath`.

Once materialized, every source is treated the same way: bootstrap merges the JSONL files into one repo-local
workspace and hands that directory to BulkLoadClient. The exact external package shape for distributing those
same seed files outside the repo is intentionally not part of this design.

#### 6.2.2 Core Seed Templates

| Template | Contents | Use Case |
|----------|----------|----------|
| `Minimal` | All core descriptor resources plus `schoolYearTypes` | CI, automated tests, minimal developer environments |
| `Populated` | `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and `studentSchoolAssociations` | Full developer environments, manual smoke testing |

The `-SeedTemplate` parameter on `load-dms-seed-data.ps1` controls which core seed source is used. The
default is `Minimal` to keep bootstrap fast and lightweight. Use `-SeedTemplate Populated` when you need
realistic sample data for manual testing or demos. The canonical DMS-916 v1 source locations are
`eng/docker-compose/seed-data/minimal/` and `eng/docker-compose/seed-data/populated/`.

These built-in manifests are the authoritative DMS-916 seed-source contract. If a future seed source adds
or removes built-in resources, the manifest table above, the core `SeedLoader` permissions in
`Claims.json`, and any extension-fragment `SeedLoader` permissions must be updated in the same change.

#### 6.2.3 Developer Sample Data Selection

Developers may use Ed-Fi provided seed sources or supply their own JSONL files. The `-SeedTemplate` and
`-SeedDataPath` parameters on `load-dms-seed-data.ps1` control this selection. The seed phase also accepts
`-IdentityProvider` so direct phase invocation can resolve the same OAuth token endpoint as the running DMS
environment without depending on a previous `start-local-dms.ps1` process, and
`-AdditionalNamespacePrefix` so custom seed payloads can declare agency or custom namespace prefixes for
the SeedLoader vendor application. The full seed-parameter contract is defined normatively in
[`command-boundaries.md`](command-boundaries.md).

**Three modes:**

| Mode | Parameter | Behavior |
|------|-----------|----------|
| Ed-Fi Minimal (default in standard modes only) | `load-dms-seed-data.ps1` with no seed-source flag, or wrapper `-LoadSeedData` with no seed-source flag | In Modes 1 and 2 only, resolves the repo-managed `Minimal` seed source and loads all core descriptor resources plus `schoolYearTypes`. Fast bootstrap for CI and automated testing. This default does not apply when `-ApiSchemaPath` is used. |
| Ed-Fi Populated | `-SeedTemplate Populated` | In Modes 1 and 2 only, resolves the repo-managed `Populated` seed source and loads `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and `studentSchoolAssociations`. For manual testing and demos. Not valid with `-ApiSchemaPath`. |
| Custom seed data | `-SeedDataPath <directory>` | Uses JSONL files from the specified directory as the source input and copies them into the repo-local seed workspace before invocation. Bypasses bootstrap-managed artifact resolution entirely. Compatibility comes from the run's root bootstrap manifest and staged schema/security inputs: embedded claims, selected extension fragments, any additive `-ClaimsDirectoryPath` fragments, and any explicitly supplied `-AdditionalNamespacePrefix` values needed by SeedLoader vendor authorization. Bootstrap does not inspect arbitrary JSONL files to certify that every record is authorized or schema-valid ahead of time. In expert `-ApiSchemaPath` mode, this is the only supported seed-source input when seed delivery is requested. |

**Parameter interaction:**

- `-SeedTemplate` and `-SeedDataPath` are mutually exclusive. Providing both is a script error.
- `-ApiSchemaPath` disables bootstrap-managed seed-source selection. In that mode, seed delivery requires
  `-SeedDataPath`, and `-SeedTemplate` is invalid.
- `-SeedDataPath` skips bootstrap-managed artifact resolution, but bootstrap still copies the supplied JSONL
  files into the repo-local seed workspace so every seed flow invokes the pinned BulkLoadClient against one
  materialized directory.
- The `-Extensions` parameter applies alongside `-SeedTemplate`. When a selected extension defines a
  built-in seed source, that source is merged into the same bootstrap workspace. When `-SeedDataPath` is specified,
  `-Extensions` seed-source resolution is skipped - the developer manages the contents of their own
  directory. Schema package selection and staged security configuration from `-Extensions` still apply
  regardless of `-SeedDataPath`, and additive `-ClaimsDirectoryPath` fragments still apply too. Custom seed
  compatibility is therefore evaluated against the same root bootstrap manifest and staged schema/security
  inputs as the rest of the run, even though bootstrap-managed seed-package resolution is
  bypassed.
- Bootstrap does not inspect arbitrary custom seed files to infer missing extensions, namespace prefixes, or
  claimsets. `-SeedDataPath` is a data-source selection mechanism, not a second schema-discovery path, not a
  dynamic claim-derivation mechanism, and not a payload-certification pass.
- `-AdditionalNamespacePrefix` is additive only. The seed phase always includes the baseline Ed-Fi seed
  prefixes and any selected extension prefixes, then appends de-duplicated additional values for
  `SeedLoader` vendor creation. It does not replace baseline prefixes, infer extensions, or grant claims.
- `load-dms-seed-data.ps1` reads `eng/docker-compose/.bootstrap/bootstrap-manifest.json` by default. That
  root manifest is the explicit handoff from schema and claims staging to seed delivery; the seed phase does
  not accept `-Extensions`, `-ApiSchemaPath`, or `-ClaimsDirectoryPath`.

**Example commands:**

```powershell
# Default: Ed-Fi Minimal seed data (descriptors only)
pwsh eng/docker-compose/load-dms-seed-data.ps1

# Ed-Fi Populated template
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedTemplate Populated

# Keycloak-backed environment
pwsh eng/docker-compose/load-dms-seed-data.ps1 -IdentityProvider keycloak -SeedTemplate Minimal

# Custom seed data directory with an agency namespace prefix for SeedLoader authorization
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedDataPath "./my-seeds/" -AdditionalNamespacePrefix "uri://state.example.org"

# Custom seed data after the run's prepared schema/security inputs have already been staged
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedDataPath "./sample-seeds/"
```

When selected extensions do not define built-in seed packages, bootstrap still stages their schema and
security inputs, but extension payloads themselves must come from `-SeedDataPath` if needed.

#### 6.2.4 Combined Seed Workspace

When bootstrap resolves more than one seed source for a run, it materializes each source into the local seed
workspace and then invokes BulkLoadClient once against the merged directory. This design deliberately does
not prescribe:

- external package IDs,
- publishing conventions,
- internal archive layouts, or
- reserved numeric ranges for custom or third-party extension publishers.

The bootstrap-side contract is simpler: every resolved source must be materializable into a flat directory of
JSONL files, and the merged workspace must not contain filename collisions. If two materialized sources
would stage the same relative filename into that workspace, the bootstrap script must detect the collision
and exit with a clear error before invoking BulkLoadClient. Automatic merging of more than one built-in
source is supported only when those published artifacts already conform to the external JSONL ordering
contract consumed by BulkLoadClient. Bootstrap materializes the files into one directory; it does not define
or reinterpret that ordering contract.

#### 6.2.5 Bootstrap Manifest Seed Handoff

Seed delivery depends on the schema and claims choices that were already staged, but it must not re-own
those choices. DMS-916 makes that dependency explicit through the root bootstrap manifest:

`eng/docker-compose/.bootstrap/bootstrap-manifest.json`

`prepare-dms-schema.ps1` writes the schema section after ApiSchema staging succeeds.
`prepare-dms-claims.ps1` updates the same file with claims and seed sections after claims staging succeeds,
using extension namespace-prefix metadata and the completed claims-staging result. `load-dms-seed-data.ps1`
reads this file before resolving seed sources or creating `SeedLoader` credentials. If the file is missing,
malformed, has an unsupported version, or lacks the required schema, claims, or seed section, seed delivery
fails before invoking BulkLoadClient.

Example shape:

```json
{
  "version": 1,
  "schema": {
    "selectionMode": "Standard",
    "selectedExtensions": ["sample"],
    "effectiveSchemaHash": "...",
    "workspaceFingerprint": "...",
    "apiSchemaManifestPath": "ApiSchema/bootstrap-api-schema-manifest.json"
  },
  "claims": {
    "mode": "Hybrid",
    "directory": "claims",
    "fingerprint": "...",
    "expectedVerificationChecks": []
  },
  "seed": {
    "extensionNamespacePrefixes": ["uri://sample.ed-fi.org"]
  }
}
```

The manifest deliberately stays small. It records only stable prepared inputs and fingerprints needed to
validate `.bootstrap` compatibility, reject built-in templates in expert `-ApiSchemaPath` mode, compose the
`SeedLoader` vendor namespace-prefix list, and let the seed phase decide whether selected extensions have
built-in seed packages. It does not contain built-in seed-package entries, resource definitions, claim grants,
instance IDs, credentials, URLs, Docker or container state, environment settings, seed file paths, phase
progress, or resume checkpoints. It is not a second schema authority; the staged ApiSchema files and CMS
claims composition remain authoritative.

---

### 6.3 DMS-Side Integration

#### 6.3.1 Seed Delivery Phase Behavior

Direct invocation of `load-dms-seed-data.ps1` always performs seed delivery after the pre-DMS phases have
completed and the selected DMS endpoint is healthy. The `-LoadSeedData` switch remains a wrapper-level
opt-in that decides whether `bootstrap-local-dms.ps1` invokes this phase. Once invoked, the phase command
performs the following steps:

**Step 1. Read prepared inputs.** Read `bootstrap-manifest.json` from the standard bootstrap workspace or
from `-BootstrapManifestPath` when an explicit path is supplied. The seed phase uses this manifest to learn
the staged schema mode, selected extensions, extension namespace prefixes, and claims mode. It does not
parse schema-selection parameters, claims directories, or the ApiSchema files to rediscover that context.

**Step 2. Resolve seed sources.** Build the set of developer seed sources to materialize for the run:
include the core seed source selected by `-SeedTemplate` (`Minimal` by default, or `Populated` when
specified) only when the bootstrap manifest says the run is in standard schema-selection mode. When the
bootstrap manifest says the run came from expert `-ApiSchemaPath` mode, bootstrap-managed seed-source
selection is disabled, so seed delivery is valid only when `-SeedDataPath` supplies the sole seed source.
In standard modes, use the selected extensions recorded in the bootstrap manifest to look up any built-in
extension seed sources in the seed catalog unless `-SeedDataPath` is supplied, in which case the custom
directory is the only seed source even though the bootstrap manifest's prepared schema/security inputs still
govern compatibility for the run.

**Step 3. Materialize seed files.** For the core `Minimal` and `Populated` seed sources, copy the selected
repo-local JSONL directory into the bootstrap seed workspace. For catalog-advertised built-in extension seed sources, use
the seed catalog's seed-source resolution path. For `-SeedDataPath`, copy the supplied JSONL files into
that same workspace so the BulkLoadClient invocation shape stays uniform. Detect and abort on filename
collisions between all materialized sources before proceeding. The external artifact naming and publishing
model for distributing seed files outside the repo remains in DMS-1119; any required JSONL ordering contract
remains owned by BulkLoadClient.

**Step 4. Invoke BulkLoadClient.** Call the pinned BulkLoadClient DLL with the merged seed workspace, the
DMS base URL supplied to `load-dms-seed-data.ps1 -DmsBaseUrl` or, when omitted for Docker-hosted seed
loading, the Docker-local DMS URL resolved from `-EnvironmentFile`. Resolve the OAuth token URL from
`load-dms-seed-data.ps1 -IdentityProvider` for the current iteration, falling back to the provider selected
by the same env-file settings when the parameter is omitted. Invoke the client with the bootstrap credentials
using the contracted surface defined in
[Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract). In the existing `-SchoolYearRange`
workflow, `--year` maps to a route-qualified DMS path where the school-year segment appears before `/data`;
when self-contained CMS identity is selected, `--token-url` carries the same context path after
`/connect/token/{schoolYear}`:

> **Non-normative illustration:** The code block below shows the expected invocation shape derived from the
> Section 6.1 consumption contract. Exact flag names are subject to BulkLoadClient implementation
> verification before Story 02.

```powershell
dotnet $bulkLoadClientDll `
    --input-format jsonl `
    --data $seedWorkDir `
    --base-url $dmsBaseUrl `
    --token-url $tokenUrl `
    --key $seedKey `
    --secret $seedSecret `
    --year $schoolYear   # only if school-year-partitioned instance
    --continue-on-error  # requested rerun tolerance; exact behavior is BulkLoadClient-owned
```

**Step 5. Check exit code.** If the BulkLoadClient exits non-zero, the bootstrap script throws and halts.

**Step 6. Clean up.** Remove the seed workspace on success (leave it on failure to aid debugging).

> **Seed rerun tolerance.** When `load-dms-seed-data.ps1` is invoked against a database that already contains seed
> data (e.g., re-running bootstrap without `-v` teardown), duplicate resources are expected to produce
> `409 Conflict` responses from the DMS API. Bootstrap may pass `--continue-on-error`, but rerun tolerance is
> a required BulkLoadClient contract for Story 02 delivery, not a guaranteed DMS-916 behavior until ODS-6738
> delivers and verifies it. Under that contract, BulkLoadClient classifies duplicate-resource `409 Conflict`
> responses as non-fatal and returns success only when all failures are within the tolerated conflict set.

> **Operator diagnostics requirement.** The seed-loading step must surface the tool's terminal summary or
> terminal error diagnostics to the operator. In v1 bootstrap passes those diagnostics through rather than
> inventing a second accounting layer or a DMS-owned result taxonomy.

#### 6.3.2 Credential Handoff

The `--key` and `--secret` values come from the SeedLoader credential bootstrap step in
[Credential Bootstrapping](#7-credential-bootstrapping). The bootstrap credentials must have sufficient DMS
authorization scope to POST all resource types present in the seed files. The script passes them directly -
no intermediate storage in files or environment variables beyond what the bootstrap step already provides.

#### 6.3.3 Pinned BulkLoadClient Resolution

Bootstrap should reuse the existing repo-managed package resolution path that already exists for bulk-load
workflows:

- `eng/Package-Management.psm1` resolves the pinned package version through `Get-BulkLoadClient`
- `eng/bulkLoad/modules/BulkLoad.psm1` locates `EdFi.BulkLoadClient.Console.dll` inside that package and
  executes it with `dotnet`

The DMS-916 bootstrap should follow the same pattern. This keeps the BulkLoadClient version pinned to the
repo's expectations, avoids machine-level drift, and works the same way in CI and on developer machines.
If the package cannot be resolved, bootstrap fails before attempting any seed loading.

---

### 6.4 Deprecation of Direct-SQL Path

`setup-database-template.psm1` is deprecated by this design. The intended implementation switch is a hard
cut-over of `-LoadSeedData` to the API-based path in the same API-based seed-delivery slice once
BulkLoadClient JSONL support is delivered and verified. DMS-916 does not introduce a second long-lived flag
or parallel user-facing seed mode.

Rationale: the direct-SQL path bypasses DMS API validation and serialization, which has caused discriminator column corruption and referential integrity violations in production ODS deployments. The risk of keeping both paths is higher than the cost of a hard cut-over.

> **Implementation gate:** The removal of `setup-database-template.psm1` in the API-based seed-delivery slice
> is blocked on BulkLoadClient JSONL support (`--input-format jsonl`), which is a cross-team dependency (see
> [Section 14.3](#143-blocking-cross-team-dependencies)). If delivery is delayed, that blocks operational
> completion of the intended API-based path; it does not change the design contract.

**Removal checklist (for implementation slice):**

- Delete `eng/docker-compose/setup-database-template.psm1`.
- Remove the `Import-Module ./setup-database-template.psm1` and `LoadSeedData` call from `start-local-dms.ps1` (lines 174–176).
- Remove the `DATABASE_TEMPLATE_PACKAGE` variable from `.env.example`.
- Replace with the BulkLoadClient invocation logic described in [Seed Delivery Phase Behavior](#631-seed-delivery-phase-behavior).
- Update any developer-facing documentation that references the old SQL template package.

### 6.5 Performance Considerations

The API-based path replaces a single ~30-second direct SQL template load with per-resource HTTP POSTs
through BulkLoadClient. Exact end-to-end timings for the JSONL path are not yet benchmark-backed in this
design; any duration ranges discussed here are provisional placeholders rather than operational commitments.

For the common single-instance bootstrap shape, API-based seed delivery is expected to be materially slower
than the direct-SQL path. Multi-year `-SchoolYearRange` runs amplify that cost because BulkLoadClient is
invoked once per year.

The tradeoff is intentional. The API-based path exercises full DMS validation and serialization,
preventing the discriminator corruption and referential integrity violations documented in Section 6.4.
For a developer bootstrap workflow, data integrity and early detection of schema regressions outweigh
raw loading speed.

The API-based path may be materially slower than the direct-SQL path because it exercises full HTTP request
handling and validation. Any performance follow-up should be treated as a separate optimization concern, not
as part of the required Story 02 functionality.

Future optimization options — including parallel per-year loading, BulkLoadClient request batching,
and pre-validated seed data caching — are out of scope for the initial implementation and deferred to
follow-up work.

## 7. Credential Bootstrapping

DMS-916 intentionally defines two credential flows with different dependency gates and purposes. Optional
smoke-test credentials are CMS-only pre-DMS work anchored to the target set selected by
`configure-local-dms-instance.ps1`. `SeedLoader` credentials are a separate DMS-dependent flow used only for
seed delivery after a healthy DMS endpoint is available. Bootstrap does not expose one blended post-start
credential-bootstrap phase.

### 7.1 Current Credential Flow

The existing bootstrap flow spans three files:

- `eng/docker-compose/start-local-dms.ps1` — top-level orchestration; calls `Get-SmokeTestCredentials` when `-AddSmokeTestCredentials` is passed
- `eng/Dms-Management.psm1` — low-level CMS API wrappers: `Add-CmsClient`, `Get-CmsToken`, `Add-Vendor`, `Add-Application`
- `eng/smoke_test/modules/SmokeTest.psm1` — `Get-SmokeTestCredentials` composes the above into a single call

The current five-step sequence is the baseline smoke-test credential flow:

1. **`Add-CmsClient`** — registers a system admin client in the Config Service (`POST /connect/register`) with a known `ClientId`/`ClientSecret`.
2. **`Get-CmsToken`** — authenticates as that client and retrieves an OAuth bearer token (`POST /connect/token`, scope `edfi_admin_api/full_access`).
3. **`Add-Vendor`** — creates a vendor record with the core namespace prefixes
   (`uri://ed-fi.org,uri://gbisd.edu`) plus any selected extension namespace prefixes; returns a vendor ID.
4. **`Add-Application`** — creates an application bound to the vendor, a claim set (`EdFiSandbox`), a set of education organization IDs, and the DMS instance ID; returns `Key` and `Secret`.
5. **Use credentials** — `Key` and `Secret` are passed directly to `Invoke-SmokeTestUtility` as `-k`/`-s` CLI arguments for the `EdFi.SmokeTest.Console` tool.

The claim set used today (`EdFiSandbox`) grants broad read/write access and is sufficient for smoke testing,
but there is no dedicated seed-loading application with a scoped claim set.

---

### 7.2 Explicit Credential Contracts

DMS-916 keeps smoke-test credentials and seed-delivery credentials as separate contracts. The two flows share
the same CMS primitives (`Add-CmsClient`, `Get-CmsToken`, `Add-Vendor`, `Add-Application`), but they do not
share dependency gates, claim sets, or purpose.

#### 7.2.1 CMS-only smoke-test credentials

Smoke-test credentials remain the existing `EdFiSandbox` application flow, but DMS-916 makes their
dependency boundary explicit:

- **Trigger**: runs only when `-AddSmokeTestCredentials` is set.
- **Claim set**: `EdFiSandbox`.
- **Target binding**: uses the DMS instance IDs already selected by `configure-local-dms-instance.ps1` and never performs
  instance creation, broad target-selection policy, or non-selector-driven discovery.
- **Dependency gate**: depends only on CMS readiness and the selected target set. It does not require a live
  DMS endpoint, DMS health wait, or `-DmsBaseUrl`, so it is valid in `-InfraOnly` mode.
- **Purpose**: surfaces credentials for smoke tooling or manual verification only. BulkLoadClient does not
  consume them, and they are not a prerequisite for the `SeedLoader` flow.

#### 7.2.2 `SeedLoader` credentials for API seed delivery

**Seed Loader Application**

- **Trigger**: runs only during `load-dms-seed-data.ps1` execution, after the current flow has a healthy DMS endpoint.
- **Claim set**: a dedicated `SeedLoader` claim set that grants the bootstrap writer permissions required
  by the built-in seed manifests plus any staged `SeedLoader` permissions that the run brings in through
  selected extensions or additive claims fragments.
- **Target binding**: uses the DMS instance IDs already selected by `configure-local-dms-instance.ps1` and never performs
  instance creation, broad target-selection policy, or non-selector-driven discovery.
- **Namespace prefixes**: must cover the namespaces present in the selected seed source. For Ed-Fi-provided
  seed packages, the baseline set is `uri://ed-fi.org` and `uri://gbisd.edu`, plus the namespace prefix for
  each loaded extension (for example, `uri://sample.ed-fi.org`). When `-Extensions` is used (see
  [Extension Selection and Loading](#8-extension-selection-and-loading)), the extension portion of the
  prefix list is computed dynamically from the selected extension set. Custom `-SeedDataPath` inputs may
  add agency or custom values explicitly with `-AdditionalNamespacePrefix`; bootstrap de-duplicates those
  values with the baseline and selected-extension prefixes before `Add-Vendor`. This is not a namespace
  discovery mechanism, and the files must stay compatible with the schema, namespace, and security inputs
  already staged for the run.
- **Education organization IDs**: at minimum the top-level LEA/SEA IDs already used by the standard bootstrap path. DMS-916 does not add a second parameter surface for arbitrary seed-specific education organization scoping; custom `-SeedDataPath` scenarios are supported as alternate payload sources, not as a full custom authorization-model designer.
- **Separate from smoke test credentials**: the Seed Loader application uses a distinct `ClientId` (e.g., `seed-loader`) and its credentials are not surfaced as output. Smoke test credentials continue to use `EdFiSandbox` via a separate application record. The `SeedLoader` flow does not depend on smoke-test credentials and does not reuse them when both opt-ins are selected.

`SeedLoader` is a deterministic bootstrap claim set, not a runtime-generated per-run claim-set builder.
For DMS-916:

- the embedded base `Claims.json` defines the top-level `SeedLoader` claim set and the core permissions
  required by the built-in `Minimal` and `Populated` seed manifests,
- each selected extension security fragment adds `SeedLoader` permissions alongside its normal extension
  developer permissions when that extension has a built-in seed package,
- additive `-ClaimsDirectoryPath` fragments may attach additional `SeedLoader` permissions under the same
  staged-fragment rules used elsewhere in the run, and
- `-SeedDataPath` is compatible with the run's root bootstrap manifest and staged schema/security
  inputs, including embedded claims, selected extension fragments, additive `-ClaimsDirectoryPath`
  fragments, and any explicit `-AdditionalNamespacePrefix` values needed for vendor namespace
  authorization. Bootstrap does not inspect arbitrary JSONL files to certify authorization completeness,
  and payload-level authorization or schema mismatches remain BulkLoadClient or DMS runtime failures.

DMS-916 does not introduce a second dedicated "seed loader fragment" type and it does not synthesize
missing grants from arbitrary JSONL content.

**Credential Handoff to BulkLoadClient**

`Key` and `Secret` returned by `Add-Application` are stored in PowerShell variables and passed as CLI
arguments when invoking BulkLoadClient using the contracted surface in
[Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract):

> **Non-normative illustration:** The code block below shows the expected credential-handoff invocation shape.
> Exact flag names are subject to BulkLoadClient implementation verification before Story 02.

```powershell
$seedCreds  = Add-Application -ApplicationName "Seed Loader" -ClaimSetName "SeedLoader" ...
$seedKey    = $seedCreds.Key
$seedSecret = $seedCreds.Secret

# Invoked once against the seed data directory:
dotnet $bulkLoadClientDll `
    --input-format jsonl `
    --data        $seedWorkDir `
    --base-url    $dmsBaseUrl `
    --token-url   $oauthTokenUrl `
    --key         $seedKey `
    --secret      $seedSecret
```

The variables remain in scope only for the lifetime of `load-dms-seed-data.ps1`. Credentials are held in
memory for that command invocation and are not written to disk. The `--token-url` value is resolved from
the seed phase's `-IdentityProvider` parameter using the same `OAUTH_TOKEN_ENDPOINT` logic the current
script already applies for DMS startup, so Keycloak and self-contained auth continue to work consistently.
The seed phase does not read transient process environment variables from an earlier `start-local-dms.ps1`
invocation; manual flows pass the provider explicitly when they are not using the default provider.
Implementation should keep the provider-to-token-endpoint mapping in one shared helper used by both
startup and seed delivery rather than duplicating switch logic in each phase command.

#### Credential Lifecycle

`$seedKey`, `$seedSecret`, `$smokeKey`, and `$smokeSecret` exist only as PowerShell variables within the
owning phase command's process lifetime. They are not persisted to disk. `load-dms-seed-data.ps1` owns the
SeedLoader credentials, `configure-local-dms-instance.ps1` owns smoke-test credentials, and the local
identity setup path owns the fixed `CMSReadOnlyAccess` identity client. On subsequent bootstrap runs,
bootstrap-managed identity clients and CMS application records are handled deterministically by name and
scope:

- `CMSReadOnlyAccess`,
- the optional smoke-test application, and
- the optional `SeedLoader` application.

For those bootstrap-owned records, reruns reuse the existing record when it already matches the intended
target scope, or replace/update it in the same bootstrap-owned slot before using or generating credentials
for the current run. The design does not treat these client/application records as unbounded create-only
state.

This rule is intentionally narrow. It applies only to bootstrap-owned identity clients and CMS
applications with fixed developer-bootstrap purposes. It does not broaden DMS-916 into a general lifecycle
management system, and it does not change the teardown-oriented guidance for DMS instances themselves.

**Design rationale:** Writing OAuth secrets - even to a git-ignored local file - creates unnecessary
plaintext-at-rest exposure on developer machines and in CI agents. Bootstrap credential creation is fast
(< 3 seconds), so there is no practical benefit to caching secrets across runs. Developers who need to
reuse credentials across a long session should simply re-run bootstrap. Whenever a selected flow needs
credentials (`-AddSmokeTestCredentials` or seed delivery), bootstrap recreates them in v1 rather than
caching them locally. Any future credential-reuse optimization must rely on secure application-record
discovery rather than plaintext secret caching.

**Authoritative built-in `SeedLoader` inventory**

The descriptor coverage below is intentionally anchored to the current embedded
`Claims.json` hierarchy. Bootstrap must use the descriptor-related parent claims
already present there and must not invent a synthetic
`http://ed-fi.org/identity/claims/domains/edFi/descriptors` URI.

| Built-in source | Resources present in the seed source | Required `SeedLoader` claim URIs |
|----------|-------------|-------------|
| `Minimal` | All core descriptor resources plus `schoolYearTypes` | `http://ed-fi.org/identity/claims/domains/systemDescriptors`, `http://ed-fi.org/identity/claims/domains/managedDescriptors`, `http://ed-fi.org/identity/claims/ed-fi/schoolYearType` |
| `Populated` | `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and `studentSchoolAssociations` | `Minimal` plus `http://ed-fi.org/identity/claims/domains/educationOrganizations`, `http://ed-fi.org/identity/claims/ed-fi/school`, `http://ed-fi.org/identity/claims/ed-fi/course`, `http://ed-fi.org/identity/claims/ed-fi/student`, `http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation` |
| Selected extension seed source | Whatever resource types the selected extension seed package emits | The selected extension security fragment must attach matching `SeedLoader` permissions for every such extension resource. |

**Required additions**

| Addition | Description |
|----------|-------------|
| `SeedLoader` claim set | Add the top-level `SeedLoader` definition and the required core permissions to the embedded claims resource at `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json`. **This is a bootstrap blocker** for API-based seeding: if `SeedLoader` metadata is not present in CMS claims data, seed delivery must fail fast before invoking BulkLoadClient. See required permissions table below. |
| Extension `SeedLoader` coverage | Each bootstrap-managed extension fragment must attach `SeedLoader` permissions for every resource emitted by that extension's built-in seed package, alongside the extension's normal developer-access permissions. |
| `Add-SeedLoaderCredentials` helper | Wraps the same `Add-CmsClient` -> `Get-CmsToken` -> `Add-Vendor` -> `Add-Application` flow with Seed Loader-specific defaults; alternatively, parameterize `Get-SmokeTestCredentials` to accept a claim set name. |
| Dynamic namespace prefix list | Computed from the standard seed baseline (`uri://ed-fi.org`, `uri://gbisd.edu`) plus the `-Extensions` parameter ([Extension Selection and Loading](#8-extension-selection-and-loading)) plus any explicit `-AdditionalNamespacePrefix` values; passed to `Add-Vendor` as a comma-separated string. |

**`SeedLoader` claimset - required resource claim permissions:**

| Resource claim URI pattern | Authorization strategy | Operations |
|---|---|---|
| `http://ed-fi.org/identity/claims/domains/systemDescriptors` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/domains/managedDescriptors` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/ed-fi/schoolYearType` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/domains/educationOrganizations` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/ed-fi/school` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/ed-fi/course` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/ed-fi/student` | `NoFurtherAuthorizationRequired` | Create |
| `http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation` | `NoFurtherAuthorizationRequired` | Create |
| *(extension resource claims per selected built-in extension seed source)* | `NoFurtherAuthorizationRequired` | Create |

> **Note:** Read (GET) access is not required by the bootstrap seed-loading contract. BulkLoadClient uses
> POST for seed records; duplicate detection and `--continue-on-error` handling for `409 Conflict` responses
> remain part of the required BulkLoadClient rerun-tolerance contract in Section 6.1.2.

---

### 7.3 Phase Boundary Reference

The authoritative credential placement is in [`command-boundaries.md`](command-boundaries.md):

- `configure-local-dms-instance.ps1` owns optional `EdFiSandbox` smoke-test credentials.
- `load-dms-seed-data.ps1` owns `SeedLoader` credential creation and BulkLoadClient invocation.
- `start-local-dms.ps1` owns the local `CMSReadOnlyAccess` identity client needed by IDE-hosted DMS.

Smoke-test credential bootstrap is CMS-only work anchored to the selected target set. `SeedLoader`
credential bootstrap is a separate DMS-dependent flow used only for seed delivery. BulkLoadClient
authenticates against the DMS OAuth endpoint using the `SeedLoader` key/secret, so that credential creation
must complete before seed data loading. `-AddSmokeTestCredentials` therefore does not require
`-DmsBaseUrl`, and the DMS-dependent continuation is entered only for seed delivery.

## 8. Extension Selection and Loading

### 8.1 Current Extension Loading

Extensions in DMS map to two concerns: (1) security metadata (claimsets) and (2) API surface
(ApiSchema overlays). Today the two concerns use different startup paths and neither has a single
selection abstraction.

**Claimset loading** is gated by the `-AddExtensionSecurityMetadata` flag on `start-local-dms.ps1`. When set, the script exports `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims` and the Config Service compose startup mounts `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets` to that path. The Config Service reads every JSON file found in the directory on startup. There is no filtering - all mounted claimset files are loaded regardless of which extensions the developer intends to use.

**ApiSchema overlays** are configured through package-backed environment variables:
`USE_API_SCHEMA_PATH=true`, `API_SCHEMA_PATH=/app/ApiSchema`, and `SCHEMA_PACKAGES=...`. At container
startup, `src/dms/run.sh` downloads the configured ApiSchema packages into `/app/ApiSchema`. This is a
package list, not a unified developer-facing extension selector, and it leaves schema staging inside the
container startup path.

The bootstrap design changes the source of `/app/ApiSchema`: `prepare-dms-schema.ps1` materializes the
selected core and extension ApiSchema files into `.bootstrap/ApiSchema`, and the compose configuration
mounts that staged workspace into the DMS container. In this bootstrap mode, `SCHEMA_PACKAGES` is left
empty so the container does not download schema packages again.

Key characteristics of the current model:

- No parity with ODS `-UsePlugins` — there is no parameter to specify extension names
- Selection is split across low-level package and claimset inputs rather than one extension selector
- Extension seed data has no defined bootstrap path; it must be loaded manually after container startup

---

### 8.2 Proposed `-Extensions` Parameter

`-Extensions` is the standard developer-facing selector for adding extension artifacts to a bootstrap run.
Extension artifacts are handled uniformly: when their schema package and companion artifacts can be resolved,
bootstrap stages them; when an artifact cannot be resolved, the owning phase fails before Docker starts with a
message naming the missing artifact.

Omitting `-Extensions` means the default profile is the standard core Data Standard schema with no extension
schemas, no extension claimsets, and no extension seed packages. It does not fall back to the legacy
`eng/docker-compose` `SCHEMA_PACKAGES` baseline that included extensions by default. Any extension needed for
the run must be selected explicitly through `-Extensions` or supplied through `-ApiSchemaPath`.

Built-in seed package availability is seed-catalog-driven, not automatic per extension name. Custom seed
payloads still flow through `-SeedDataPath`.

`-Extensions` belongs to `prepare-dms-schema.ps1` (see `command-boundaries.md` §3.1). It accepts one or
more extension identifiers typed as `String[]` through normal PowerShell array binding. The phase command
runs before any Docker services start:

```powershell
# Examples
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample"
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample","extension2"
pwsh eng/docker-compose/prepare-dms-schema.ps1   # no -Extensions: core only
```

**Default behavior**: omitting `-Extensions` loads core Ed-Fi resources only - no staged extension security
fragments, no extension ApiSchema overlays, and no extension seed data. This is an intentional change from
today's `eng/docker-compose` baseline, which included extensions in `SCHEMA_PACKAGES`; DMS-916 does not carry
that default forward into the normative bootstrap contract. Select the needed extensions explicitly through
`-Extensions` when those schemas are needed. The core-only default keeps the environment minimal and fast to
start.

**How it works at runtime:**

1. Bootstrap resolves extension artifacts by extension short name from the configured package and metadata
   sources. The schema phase owns schema package resolution, the claims phase owns security fragment
   resolution, and the seed phase owns optional built-in seed package lookup. The lookup mechanism may start
   as script-local metadata or move to shared files as it grows, but that does not change phase ownership.
2. `prepare-dms-schema.ps1` consumes only the schema-owned metadata. It stages the selected
   `ApiSchema*.json` files, computes the expected `EffectiveSchemaHash`, and writes minimal schema metadata
   for downstream phases.
3. `prepare-dms-claims.ps1` consumes the bootstrap manifest schema section, the staged schema files, and the
   security-owned metadata. For each selected extension with matching security fragments, it stages the
   corresponding JSON file(s) into `eng/docker-compose/.bootstrap/claims/`; when additional fragments are
   needed, it requires `-ClaimsDirectoryPath`. Config Service startup consumes that staged host claims
   workspace through the claims section of `eng/docker-compose/.bootstrap/bootstrap-manifest.json`. The
   compatible startup wiring continues to mount the staged workspace `eng/docker-compose/.bootstrap/claims`
   into the Config Service container for the run. Remove the redundant `/app/additional-claims` mounts from
   `local-dms.yml` and `published-dms.yml`; DMS gets claimsets from CMS authorization metadata, not from local
   fragment files.
4. `load-dms-seed-data.ps1` consumes the seed catalog when seed delivery runs and built-in seed packages
   exist for the selected extension set.
5. If `-Extensions` is non-empty, the bootstrap manifest claims section resolves to Hybrid mode
   automatically and the developer does not need any separate legacy claims-selection flag. Historical
   repo behavior may still mention `-AddExtensionSecurityMetadata`, but that flag is outside the DMS-916
   normative contract.
6. If any specified extension artifact cannot be resolved, the owning phase exits with a clear error before
   starting any containers.

The staged claims directory must remain in place while Docker containers are running because it is
bind-mounted into the container at `/app/additional-claims`. For v1, bootstrap uses a stable repo-local
workspace under `eng/docker-compose/.bootstrap/claims` rather than per-run temp directories plus a state
file. On same-checkout reruns, bootstrap compares the intended claims set to the existing workspace.
Identical contents are reused as-is; a different intended set requires teardown because the current CMS
startup path only initial-loads claims into empty tables and does not replace a populated claims document in
place. Teardown removes the entire `eng/docker-compose/.bootstrap/` workspace.

**Extension selection changes the physical schema footprint:** The database DDL is derived from the selected
staged schema set. `-Extensions` therefore controls both which API resources are authorized and which
extension tables are provisioned. A developer cannot safely switch between extension combinations on an
already-provisioned database unless the live fingerprint still matches the exact selected schema set.

---

### 8.3 Extension Seed Data

The seed-workspace design reserves room for extension-provided JSONL files that load after core seed data.
For Ed-Fi-managed built-in seed sources, the intended file-ordering convention is to keep core-owned files in
the lower range and extension-owned files in the higher range:

| Range | Scope |
|-------|-------|
| `01`-`49` | Core Ed-Fi standard seed data (descriptors, base education organizations, etc.) |
| `50`-`99` | Extension seed data for Ed-Fi-managed built-in extension packages when one is available |

Built-in extension seed package availability is determined only by the seed catalog.

This table is source-author guidance for Ed-Fi-managed built-in artifacts, not a bootstrap validation rule
for third-party publishers or for developer-supplied `-SeedDataPath` directories. The bootstrap contract
remains collision-based: any merged workspace is valid only when it can be flattened into one directory
without filename collisions. Any stronger ordering semantics remain external to DMS bootstrap, and future
built-in multi-source merging depends on those external artifacts already honoring the published JSONL
contract consumed by BulkLoadClient.

**Bootstrap implications:**

1. When seed delivery runs and no selected extension has a built-in seed package, bootstrap stages only the
   selected repo-local core seed source.
2. The seed catalog may add an optional built-in seed-package entry so an extension can merge
   its JSONL files into the same workspace without changing the bootstrap shape, but only when that published
   package already follows the external JSONL ordering contract required by BulkLoadClient.
3. Until an extension defines that package in the seed catalog, developers supply extension
   payloads through `-SeedDataPath` when needed.
4. If more than one built-in source is ever merged into the workspace, filename collisions remain a
   bootstrap-time error and must abort before BulkLoadClient runs.

This keeps the BulkLoadClient invocation uniform (one call, one directory) while keeping seed package
availability a seed-catalog concern rather than a schema-selection concern.

### 8.4 Integration with ApiSchema.json Selection (Section 3)

The `-Extensions` parameter is the single developer-facing control for enabling extensions. Passing it triggers three coordinated actions automatically - the developer does not need to configure each concern separately:

1. **ApiSchema files staged host-side (Section 3)** - The script resolves the extension's NuGet schema
   package (e.g., `EdFi.Sample.ApiSchema`) on the host, stages the resulting file in
   `eng/docker-compose/.bootstrap/ApiSchema/`, and includes that staged file in the exact set later hashed
   and mounted/read by DMS.

2. **Security fragments loaded (Section 4)** - The corresponding security fragment JSON file(s) are staged
   and bind-mounted to `/app/additional-claims`. The bootstrap manifest claims section resolves to Hybrid
   mode automatically and points at the relative staged workspace. No separate legacy claims-selection flag is
   part of this DMS-916 flow. For built-in extension seed packages, those fragments must attach both
   `EdFiSandbox` and `SeedLoader` permissions to the extension resources they cover.

3. **Extension seed data handling (Section 8.3)** - When seed delivery runs, bootstrap checks
   whether any selected extension has a built-in seed package in the seed catalog. If no selected extension
   has one, the seed workspace remains core-only unless the developer supplies `-SeedDataPath`. If an
   extension has a built-in seed package, its JSONL files are merged
   into the same bootstrap workspace only when that package participates in the same external JSONL contract
   used for core artifacts.

**Example - sample-enabled bootstrap run:**

After schema and claims preparation have already selected the sample extension for the run, the seed-delivery
phase behaves as follows:

```powershell
pwsh eng/docker-compose/load-dms-seed-data.ps1
# Result:
#   eng/docker-compose/.bootstrap/ApiSchema/ includes the staged Sample ApiSchema.json  (Section 3)
#   /app/additional-claims contains the sample security fragment with EdFiSandbox coverage  (Section 4)
#   Seed workspace contains only the selected core seed source unless -SeedDataPath is supplied  (Section 8.3)
```

**Note - `-ApiSchemaPath` mutual exclusivity:** `-ApiSchemaPath` and `-Extensions` are mutually exclusive
parameters. When `-ApiSchemaPath` is provided, the developer supplies a complete custom schema environment.
Bootstrap still derives the base security set from the staged schema and available claims inputs, while any
additional non-core security fragments come through `-ClaimsDirectoryPath`. Custom seed inputs continue to
use `-SeedDataPath`. This preserves the schema/security single-source-of-truth principle while keeping
arbitrary custom authorization rules explicit.

---

## 9. Bootstrap Commands and Parameters

In DMS-916, "skip/resume" means safe per-phase invocation plus optional same-invocation continuation
through `-InfraOnly -DmsBaseUrl` after instance configuration and schema provisioning have completed —
not a persisted cross-invocation resume mechanism, checkpoint file, or second control plane. Phase
commands are the normative contract; `bootstrap-local-dms.ps1` is optional convenience packaging only, not
a mandatory design deliverable.

> **Wrapper simplicity rule.** The normative wrapper contract is
> [`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional).
> This document uses the wrapper only to illustrate common-path orchestration.

### 9.1 Infrastructure Phase Command

`eng/docker-compose/start-local-dms.ps1` is the infrastructure-phase command. Its primary concern is
Docker stack management and service health waiting. Its authoritative parameter surface and prohibitions
are in [`command-boundaries.md` Section 3.3](command-boundaries.md#33-start-local-dmsps1--infrastructure-lifecycle)
and [`Section 6`](command-boundaries.md#6-parameter-surface-by-owner).
Direct startup assumes the upstream schema and claims preparation phases have already produced their
workspace artifacts; the no-argument developer happy path is the wrapper, not this phase command.

```powershell
# Infrastructure phase after schema and claims have been staged
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly

# Infrastructure phase with Keycloak and Swagger UI
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -EnableSwaggerUI -IdentityProvider keycloak

# Teardown stack and volumes
pwsh eng/docker-compose/start-local-dms.ps1 -d -v
```

Parameters owned by other phase commands stay with those commands; this document does not restate the full
mapping.

---

### 9.2 Proposed New Flags

ODS initdev exposes skip flags so developers can bypass expensive steps when iterating on a partially-provisioned environment:

| ODS Flag | Purpose |
|----------|---------|
| `-NoRebuild` | Skip solution build |
| `-NoDeploy` | Skip database deployment |
| `-ExcludeCodeGen` | Skip code generation |

DMS equivalents should follow the same pattern, but only where they are justified by real developer
workflow cost and can be made safe from live state. DMS-916 does not add a schema-deployment skip flag.
After `configure-local-dms-instance.ps1` selects the target instances, `provision-dms-schema.ps1` always
invokes the authoritative SchemaTools/runtime-owned provisioning and validation path over the staged schema
set before any DMS process is expected to serve requests.

The existing `-LoadSeedData` flag remains the wrapper-level intentional opt-in for DMS-dependent seed
delivery; omitting it from `bootstrap-local-dms.ps1` skips both SeedLoader credential creation and seed
loading for the wrapper run. Direct invocation of `load-dms-seed-data.ps1` is itself the opt-in and always
performs seed delivery. `-AddSmokeTestCredentials` remains a separate opt-in for the CMS-only
smoke-credential phase after instance selection. `-InfraOnly` and `-DmsBaseUrl` shape whether bootstrap
stops before DMS starts or carries the wrapper run into the IDE-hosted DMS health wait, but they do not
bypass schema provisioning.

**Bootstrap does not add extra skip/resume flags inside either credential path in v1.** When
`-AddSmokeTestCredentials` or seed delivery is selected, the corresponding credential creation reruns each
time because credentials are ephemeral, not written to disk, and inexpensive to recreate. This avoids
reintroducing plaintext secret caching or designing an incomplete secret-recovery path.

**Safety**: `provision-dms-schema.ps1` delegates schema readiness to the shared SchemaTools/runtime-owned
path rather than to a bootstrap-only state file or bootstrap-authored readiness classifier. Security
configuration is intentionally not separately skippable in v1 because it is cheap, deterministic, and
derived directly from the current inputs each run.

Optional post-bootstrap automation such as SDK generation or integrated smoke/E2E/integration test runners
was considered during the ODS audit, but it is intentionally out of scope for DMS-916.

---

### 9.3 Parameter Surface

#### 9.3.1 Illustrative V1 Wrapper Surface

The thin convenience wrapper may expose direct developer-facing flags that it forwards to the appropriate
phase command. The authoritative wrapper surface and per-phase parameter distribution are
[`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional)
and [`Section 6`](command-boundaries.md#6-parameter-surface-by-owner).
This design document uses wrapper invocations only as examples.

#### 9.3.2 Local Settings and Infrastructure Phase Parameters

`start-local-dms.ps1` owns the Docker infrastructure lifecycle: stack management, service health waiting,
and backward-compatible continuation flags. Its authoritative parameter surface is
[`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner).
`-EnvironmentFile` is also accepted by every phase command that contacts the local services. Those phases
use one shared local-settings helper to resolve CMS URL, identity provider, tenant, Docker-local DMS URL,
and database connection defaults from the same env file. The wrapper forwards the same `-EnvironmentFile`
value to `start-local-dms.ps1`, `configure-local-dms-instance.ps1`, `provision-dms-schema.ps1`, and
`load-dms-seed-data.ps1` when those phases run. Infrastructure-only UI flags such as `-EnableKafkaUI` and
`-EnableSwaggerUI` remain on `start-local-dms.ps1`.

The Config Service is mandatory for the normative bootstrap contract. Every non-teardown
DMS-916 bootstrap invocation starts the Config Service unconditionally, including
the default no-argument core-only Mode 1 flow and keycloak-backed runs. `-EnableConfig` therefore remains
only as a backward-compatibility switch, not as a meaningful opt-out on the canonical bootstrap contract.

**Breaking-change note:** The DMS-916 definition of `-NoDmsInstance` (owned by `configure-local-dms-instance.ps1`) deliberately narrows the current behavior. Existing scripts that used it on a fresh stack as a generic "skip creation" switch must now either drop the flag or pre-create exactly one intended target instance before rerunning. See also [Section 15](#15-breaking-changes-and-migration-notes) for the consolidated migration reference.

#### Parameter Validation Rules

Each validation rule below is owned by the phase command responsible for the affected parameters. Phase commands exit immediately on invalid combinations; supported combinations requiring non-fatal warnings continue with documented behavior.

| Rule | Outcome |
|------|---------|
| `-Extensions` and `-ApiSchemaPath` both specified | "Error: -Extensions and -ApiSchemaPath are mutually exclusive. Use -Extensions for package-backed extension selection or -ApiSchemaPath for a custom schema directory." |
| `-ApiSchemaPath` staging does not normalize to exactly one core `ApiSchema*.json` file plus zero or more extension files | "Error: -ApiSchemaPath must resolve to exactly one core ApiSchema.json and zero or more extension ApiSchema.json files after staging." |
| `-ApiSchemaPath` stages schemas that need additional claims metadata without `-ClaimsDirectoryPath` | "Error: -ApiSchemaPath requires additional claims metadata for one or more staged schemas. Provide -ClaimsDirectoryPath with claimset fragments, or use -Extensions when package-backed artifacts are available." |
| `-SeedTemplate` and `-SeedDataPath` both specified | "Error: -SeedTemplate and -SeedDataPath are mutually exclusive. Use -SeedTemplate for repo-local Ed-Fi templates or -SeedDataPath for a custom seed directory." |
| Bootstrap manifest schema section was produced from `-ApiSchemaPath`, and `-SeedTemplate` is specified | "Error: -SeedTemplate is not valid with -ApiSchemaPath. Expert custom-schema mode disables bootstrap-managed seed selection; use -SeedDataPath when seed delivery is required." |
| `load-dms-seed-data.ps1` with a bootstrap manifest schema section from `-ApiSchemaPath`, but without `-SeedDataPath` | "Error: Seed delivery with -ApiSchemaPath requires -SeedDataPath. Expert custom-schema mode does not fall back to built-in Minimal or Populated seed templates." |
| Seed delivery with `-SeedDataPath` and `-Extensions` | `-Extensions` seed package resolution is skipped. Schema packages and staged security configuration from `-Extensions` still apply. The script emits a warning indicating that extension seed package lookup is skipped when `-SeedDataPath` is provided. |
| `-AdditionalNamespacePrefix` contains a blank or malformed value | "Error: -AdditionalNamespacePrefix values must be non-empty namespace URI prefixes, for example uri://state.example.org." |
| `load-dms-seed-data.ps1` cannot read a valid bootstrap manifest with schema, claims, and seed sections | "Error: Seed delivery requires prepared schema/security inputs. Run prepare-dms-schema.ps1 and prepare-dms-claims.ps1 first, or pass -BootstrapManifestPath to the bootstrap manifest." |
| `-InfraOnly` without `-DmsBaseUrl` | Permitted. `start-local-dms.ps1` performs infrastructure startup and readiness checks only, then stops. This is not a checkpoint for a later bootstrap resume; instance creation, schema provisioning, and seed work remain in later explicit phase commands or wrapper orchestration. |
| `-InfraOnly` without `-DmsBaseUrl`, with smoke-credential opt-in | Permitted only through wrapper orchestration or manual later invocation of `configure-local-dms-instance.ps1`; `start-local-dms.ps1` itself still stops after infrastructure readiness and does not create smoke-test credentials. |
| `start-local-dms.ps1 -DmsBaseUrl` without `-InfraOnly` | "Error: -DmsBaseUrl is only valid on start-local-dms.ps1 when -InfraOnly is used to continue bootstrap against an IDE-hosted DMS endpoint." |
| `-InfraOnly -DmsBaseUrl` before `configure-local-dms-instance.ps1` and `provision-dms-schema.ps1` complete | Invalid workflow. The wrapper must not forward `-DmsBaseUrl` until the post-provision DMS-start/health-wait point. Manual phase flows must run instance configuration and schema provisioning before invoking the external endpoint health wait. |
| `bootstrap-local-dms.ps1 -InstanceId` | "Error: -InstanceId is a phase-command-only selector. Use provision-dms-schema.ps1 or load-dms-seed-data.ps1 directly when explicit instance-ID targeting is required." |
| `-NoDmsInstance`, no `-SchoolYearRange`, exactly one existing instance found in the current tenant scope | Permitted. Bootstrap reuses that single existing instance as the canonical target set for the run. |
| `-NoDmsInstance`, no `-SchoolYearRange`, zero existing instances found in the current tenant scope | "Error: -NoDmsInstance requires exactly one existing DMS instance in the current tenant scope, but none were found. Re-run without -NoDmsInstance to create the instance, or prepare the environment manually before retrying." |
| `-NoDmsInstance`, no `-SchoolYearRange`, multiple existing instances found in the current tenant scope | "Error: -NoDmsInstance requires exactly one existing DMS instance in the current tenant scope, but multiple instances were found. Tear down the extra instances or manually prepare the environment so one intended target remains before rerunning." |
| `-NoDmsInstance` with `-SchoolYearRange` | "Error: -NoDmsInstance with -SchoolYearRange is not supported in DMS-916. Tear down and recreate the environment for multi-year runs." |
| `-SchoolYearRange` is not in `YYYY-YYYY` format with four-digit integers and `endYear >= startYear` | "Error: -SchoolYearRange must use YYYY-YYYY format with four-digit years, and the ending year must be greater than or equal to the starting year." |
| `-ClaimsDirectoryPath` path does not exist or contains no `*-claimset.json` files | "Error: -ClaimsDirectoryPath must point to a directory containing one or more *-claimset.json files." |
| `-ClaimsDirectoryPath` fragments collide with staged extension fragments by filename | "Error: Claimset fragment filename collision detected for '<file>'. Each staged *-claimset.json filename must be unique before bootstrapping." |
| `-ClaimsDirectoryPath` fragment references a claim set name that does not exist in the embedded `Claims.json` | "Error: Claimset fragment '<file>' references unknown claim set '<name>'. DMS-916 additive fragments may only attach to claim sets declared in the embedded Claims.json." |
| Seed delivery runs but the pinned BulkLoadClient package cannot be resolved or does not expose the required JSONL interface | "Error: BulkLoadClient package resolution failed or the required JSONL interface is unavailable." |
| Seed delivery runs but the embedded claims metadata does not define the top-level `SeedLoader` claim set | "Error: Seed delivery requires the embedded CMS claims metadata to define the top-level SeedLoader claim set. Bootstrap cannot continue to BulkLoadClient until that claim set exists." |
| Extension artifact for `-Extensions` cannot be resolved | "Error: Extension artifact resolution failed for '<name>'. Check the extension name, configured feed, package metadata, or supply a direct schema directory with -ApiSchemaPath." |
| Seed delivery with `-Extensions` where an extension is in the seed catalog but its NuGet seed package fails to resolve | "Error: Seed package for extension '<name>' could not be resolved. Check network/feed access or supply the package manually." |
| Seed delivery with `-Extensions` where an extension has no seed package entry in the seed catalog | Permitted with warning. Bootstrap emits an informational warning indicating no built-in seed package is available; schema and security configuration from the extension still apply. |
| Seed delivery with a built-in extension seed source whose staged security fragments do not attach required `SeedLoader` permissions for that extension's seed resources | "Error: Extension '<name>' seed package configuration is incomplete. The staged security fragments do not provide the required SeedLoader permissions for the extension seed resources." |

---

### 9.4 Developer Invocation Examples

Phase commands may be called individually for targeted re-runs, or sequenced through the thin convenience
wrapper (`bootstrap-local-dms.ps1`) for the happy path. Common invocations:

```powershell
# Full bootstrap via thin wrapper — core only, no seed data
pwsh eng/docker-compose/bootstrap-local-dms.ps1

# Full bootstrap with sample extension and seed data
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -Extensions sample -LoadSeedData -SeedTemplate Minimal

# Keycloak-backed seed loading
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -IdentityProvider keycloak -LoadSeedData -SeedTemplate Minimal

# Custom seed data with agency namespace authorization
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -LoadSeedData -SeedDataPath "./my-seeds/" -AdditionalNamespacePrefix "uri://state.example.org"

# Multi-year bootstrap
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -SchoolYearRange "2025-2026" -LoadSeedData -SeedTemplate Minimal

# Wrapper infrastructure-only workflow (no DMS container; prints IDE next-step guidance)
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly

# Wrapper IDE continuation: configure/provision first, then wait for IDE DMS to report healthy
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"

# Teardown stack and volumes
pwsh eng/docker-compose/start-local-dms.ps1 -d -v
```

No shell/session preparation is required before invoking the wrapper. Direct phase-command invocation is
for targeted re-runs and manual flows, and each phase requires the upstream outputs documented in
[`command-boundaries.md`](command-boundaries.md). The story-aligned behaviors documented elsewhere -
`-Extensions`, authoritative schema provisioning, `-InfraOnly`, `-DmsBaseUrl`, `-IdentityProvider` - apply in both
wrapper and manual flows, but manual callers must run the dependency chain explicitly.

#### 9.4.2 Wrapper Direct-Flag Interface

The wrapper exposes direct developer-facing flags and forwards each flag unchanged to the phase command
that owns it. It does not expose `-InstanceId`; explicit ID targeting is phase-command-only. `-EnvironmentFile`
is the one shared local-settings input and is forwarded to each local-service phase; no other shared
defaults layer or run-context file exists. For the school-year
workflow, that means the wrapper exposes the same `-SchoolYearRange` flag owned by
`configure-local-dms-instance.ps1`; downstream manual selector flags remain `-SchoolYear <int[]>` on
`provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. The complete flag-to-phase mapping is in
[Section 9.3.1](#931-illustrative-v1-wrapper-surface) and [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner).

Within a single wrapper invocation, the wrapper reads selected instance IDs from the structured
`configure-local-dms-instance.ps1` result and forwards them as internal `-InstanceId` arguments to
`provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. It never parses human-readable console text to
recover IDs or credentials. In an IDE continuation run, the wrapper also forwards the same external
`-DmsBaseUrl` value to `load-dms-seed-data.ps1` when `-LoadSeedData` is selected. When the wrapper exposes
`-IdentityProvider`, it forwards the same value to both `start-local-dms.ps1` and
`load-dms-seed-data.ps1` so token endpoint resolution remains explicit in each phase. When the wrapper
exposes seed authorization inputs such as `-AdditionalNamespacePrefix`, it forwards them only to
`load-dms-seed-data.ps1` when `-LoadSeedData` is selected. When the wrapper exposes `-EnvironmentFile`, it
forwards that value to every phase that needs local CMS, identity, tenant, DMS, or database settings. No
state is written to disk for this purpose.

#### 9.4.1 Recommended Bootstrap Output

Bootstrap should present the major pipeline steps in a human-readable order so developers can tell what ran
and where a failure occurred. Exact formatting is intentionally non-normative; DMS-916 only needs clear step
names, clear failure reporting, and enough context to distinguish schema selection, schema deployment
results, credential bootstrap, and seed loading.

A minimal acceptable console shape is:

```text
Bootstrap-DMS: Starting...

[prepare-dms-schema]                                       (0.4s)
  Core:  EdFi.DataStandard52.ApiSchema 1.0.328
  Ext:   EdFi.Sample.ApiSchema         1.0.328
[prepare-dms-claims]                                       (0.2s)
  Hybrid mode: 1 security fragment(s) staged
[start-local-dms -InfraOnly]                              (13.7s)
  Infrastructure and Config Service are claims-ready
[configure-local-dms-instance]                             (1.4s)
[smoke-test credentials]                                   (0.7s)
  Smoke credentials created for selected target instance(s)
[provision-dms-schema]                                     (1.1s)
  SchemaTools provisioned 1 database(s); EffectiveSchemaHash=abc123...
[start-local-dms]                                         (10.0s)
  DMS healthy at http://localhost:8080
[load-dms-seed-data: credentials]                          (2.1s)
[load-dms-seed-data: BulkLoadClient]                     (187.3s)
  Core seed:  1,204 loaded, 0 conflicts, 0 skipped, 0 fatal errors

Bootstrap complete. DMS is ready at http://localhost:8080

Summary
  Phase                                  Duration
  ----------------------------------------------
  Schema selection                          0.4s
  Claims staging                            0.2s
  Infrastructure readiness                 13.7s
  Create DMS instances                      1.4s
  Smoke-test credentials                    0.7s
  Schema provisioning                       1.1s
  Start DMS                                10.0s
  SeedLoader credentials                    2.1s
  Seed data loading                       187.3s
  ----------------------------------------------
  Total                                   216.0s
```

CI environments may suppress color output. The summary table is always emitted on both success and failure;
on failure the failing phase name and error are printed before the summary.

#### 9.4.3 Thin Wrapper Contract

The normative wrapper contract is
[`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional).
This design section does not restate wrapper prohibitions; it only illustrates the wrapper's common-path
role in the invocation examples.

#### 9.4.4 Manual Phase Command Flow

The wrapper and the individual phase commands are complementary: use the wrapper for the common developer
happy path; invoke phase commands directly for targeted re-runs, CI-level scripting, or debugging a single
phase without repeating earlier work.

A canonical manual flow (core schema, single instance):

```powershell
# Stage schema and claims (Docker not required)
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1

# Start infrastructure (PostgreSQL, identity provider, Config Service)
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly

# Create DMS instance; emits structured selected-instance output
pwsh eng/docker-compose/configure-local-dms-instance.ps1

# Provision schema (auto-selects the one existing instance)
pwsh eng/docker-compose/provision-dms-schema.ps1

# Start DMS container
pwsh eng/docker-compose/start-local-dms.ps1

# Load seed data (auto-selects the one existing instance)
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedTemplate Minimal
```

If the manual flow started the environment with a non-default identity provider, pass the same provider to
seed loading, for example `load-dms-seed-data.ps1 -IdentityProvider keycloak -SeedTemplate Minimal`.

If the manual flow uses a non-default env file, pass the same `-EnvironmentFile` value to every phase that
contacts local services:

```powershell
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/configure-local-dms-instance.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/provision-dms-schema.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/start-local-dms.ps1 -EnvironmentFile "./.env.local"
pwsh eng/docker-compose/load-dms-seed-data.ps1 -EnvironmentFile "./.env.local" -SeedTemplate Minimal
```

When re-running only a downstream phase after a partial failure, supply an explicit selector if more than
one instance exists:

```powershell
# Re-run seed delivery against a specific instance (two instances present)
$instanceId = 1
pwsh eng/docker-compose/load-dms-seed-data.ps1 `
    -InstanceId $instanceId `
    -SeedTemplate Minimal
```

#### 9.4.5 Selector Resolution Examples

Selector behavior for `provision-dms-schema.ps1` and `load-dms-seed-data.ps1` follows the same rule:
auto-select when exactly one DMS instance exists in CMS; fail fast when zero or multiple instances exist
without an explicit selector. See [`command-boundaries.md` Section 7](command-boundaries.md#7-selector-resolution-examples)
for the compact reference form.

```powershell
# Auto-selection: exactly one instance — no selector required
pwsh eng/docker-compose/provision-dms-schema.ps1
# Bootstrap finds one DMS instance and proceeds.

# Explicit selector: multiple instances — target a specific one by ID
pwsh eng/docker-compose/provision-dms-schema.ps1 -InstanceId 1

# Multi-year selector: target instances by school year
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026

# Fail-fast: two instances exist, no selector supplied
# ERROR: 2 DMS instance(s) found in CMS without an explicit selector.
#        Supply -InstanceId <long> or -SchoolYear <int> to target a specific instance.
#        Exit code: non-zero.
```

The same rule and examples apply to `load-dms-seed-data.ps1`. When the thin wrapper orchestrates a full
run, instance IDs from the structured `configure-local-dms-instance.ps1` result are forwarded in memory -
the developer never needs to copy-paste numeric instance IDs between phases in the wrapper path.

### 9.5 Bootstrap Working Directory

Bootstrap uses a repo-local working directory at `eng/docker-compose/.bootstrap/`. This directory is a
staging area, not an authoritative control plane. Real state continues to live in Docker containers,
volumes, environment files, and the target database.
The workspace is scratch-only bootstrap state; it must be excluded from source control via `.gitignore`.

**Layout**

- `eng/docker-compose/.bootstrap/claims/` - staged `*-claimset.json` files bind-mounted into CMS
- `eng/docker-compose/.bootstrap/ApiSchema/` - staged `ApiSchema*.json` files used for hashing and for both Docker-hosted and IDE-hosted DMS runs
- `eng/docker-compose/.bootstrap/ApiSchema/bootstrap-api-schema-manifest.json` - runtime asset index for staged schema/content paths only
- `eng/docker-compose/.bootstrap/bootstrap-manifest.json` - the only persisted bootstrap compatibility and handoff manifest for schema, claims, and seed phases
- `eng/docker-compose/.bootstrap/seed/` - merged JSONL files for the current seed-loading run

**Lifecycle**

- The schema directory is materialized on first bootstrap run. On same-checkout reruns, bootstrap compares the
  intended schema inputs and ApiSchema workspace fingerprint to `bootstrap-manifest.json`: matching values are
  reused as-is; differing values are treated as incompatible existing state and require teardown before
  bootstrap rewrites the workspace.
- The schema directory remains in place while Docker-hosted or IDE-hosted DMS processes may still need to read it.
- The claims directory follows the same manifest rule: bootstrap materializes it on first run, reuses it
  unchanged when the intended claims fingerprint matches `bootstrap-manifest.json`, and fails fast with
  teardown guidance when it differs.
- `bootstrap-manifest.json` is the only authoritative `.bootstrap` compatibility record. It is written or
  updated only when schema and claims staging are valid for the current checkout state; downstream phases fail
  fast if it is missing, malformed, unsupported, incomplete, or incompatible with the requested seed-source
  flags.
- The seed directory is deleted and recreated from scratch only when seed delivery runs.
- The seed directory is deleted on successful completion of the seed step and left in place on failure for debugging.
- `start-local-dms.ps1 -d -v` removes the entire `eng/docker-compose/.bootstrap/` tree.
- Story 00 adds `eng/docker-compose/.bootstrap/` to `.gitignore` before staging writes generated artifacts; staged schema, claims, and seed files must never be committed.
- Concurrent bootstrap runs against the same workspace are not supported because they would share the same staged directories.
- DMS-916 does not define a same-workspace lock file or locking protocol. If parallel bootstrap runs are required in CI or local automation, use separate repository checkouts or isolated workspaces instead.

Using a stable workspace directory keeps bind mounts and cleanup deterministic without trying to mirror live
container/database state into a JSON control plane.

**`-InfraOnly` behavior**: When `-InfraOnly` is used through the wrapper, the same working-directory and
authoritative schema-provisioning rules apply through `provision-dms-schema.ps1`. Without `-DmsBaseUrl`,
bootstrap then stops and leaves the staged schema workspace in place for the developer's next IDE-hosted DMS
launch, though optional smoke-test credentials may already have been created. That stopped shape is terminal
for the invocation: DMS-916 does not define a later bootstrap resume that picks up DMS-dependent work from
the stopped run. With `-DmsBaseUrl`, the wrapper still runs instance configuration and schema provisioning
before any DMS health wait, then carries the endpoint into the DMS-start/health-wait phase. The later
DMS-dependent SeedLoader and seed phases target that external endpoint only after bootstrap automatically
confirms its health.

## 10. School-Year Range Handling

The only multi-instance concern kept in the normative DMS-916 design is the existing
`-SchoolYearRange` developer workflow. That flag is owned by `configure-local-dms-instance.ps1`; when the
thin wrapper is used, it exposes the same `-SchoolYearRange` input and forwards it unchanged to that phase.
Broader multi-tenant orchestration concerns are intentionally left outside this story.

### 10.1 School-Year Instance Creation

`-SchoolYearRange` on `configure-local-dms-instance.ps1` (or on `bootstrap-local-dms.ps1`, which forwards
it to that phase) creates one DMS instance per school year. The range is a closed interval inclusive on both
ends: `"2022-2026"` enumerates school years 2022, 2023, 2024, 2025, and 2026. The format is
`"<startYear>-<endYear>"` where both values are four-digit integers and `endYear >= startYear`; any other
format causes a pre-flight validation error.

`Add-DmsSchoolYearInstances` in `Dms-Management.psm1` is the primary helper for this path. It loops from
`$StartYear` to `$EndYear`, calls `Add-DmsInstance` for each year, then calls `Add-DmsInstanceRouteContext`
to bind the `schoolYear` context key to the new instance.

### 10.2 Per-Instance Seed Data Loading

When `-SchoolYearRange` is used, seed data must be loaded into each instance independently. A single
BulkLoadClient invocation targets one school-year instance via the `--year` flag (see
[Minimum Invocation Surface Assumed by Bootstrap](#611-minimum-invocation-surface-assumed-by-bootstrap)). In this design, that workflow stays scoped to the existing
school-year developer path: the selected year appears as the route qualifier before `/data`, so the per-run
resource URL shape is `{base-url}/{year}/data/{namespace}/{resource}`. When self-contained CMS identity is
used for the same iteration, the token URL carries the matching context path at
`/connect/token/{schoolYear}`. The bootstrap script loops over the school year range and invokes
BulkLoadClient once per year:

The single-segment `/{schoolYear}/data/...` examples in this section intentionally assume the local
DMS-916 bootstrap route profile where route qualifiers are configured as `schoolYear` only. Broader repo
profiles such as `/{districtId}/{schoolYear}/data/...` remain valid DMS behavior outside this narrowed
local bootstrap workflow and are not redefined here.

> **Non-normative illustration:** The code block below shows the expected per-year invocation loop shape
> derived from the Section 6.1 consumption contract. Exact flag names are subject to BulkLoadClient
> implementation verification before Story 02.

```powershell
$startYear, $endYear = $SchoolYearRange -split '-'
foreach ($year in [int]$startYear..[int]$endYear) {
    $tokenUrl =
        if ($IdentityProvider -eq 'Keycloak') {
            'http://localhost:8045/realms/edfi/protocol/openid-connect/token'
        } else {
            "http://localhost:8081/connect/token/$year"
        }

    dotnet $bulkLoadClientDll `
        --input-format jsonl `
        --data        $seedWorkDir `
        --base-url    $dmsBaseUrl `
        --token-url   $tokenUrl `
        --key         $seedKey `
        --secret      $seedSecret `
        --continue-on-error `
        --year        $year
}
```

Key points:

- The same `$seedWorkDir` (merged core + extension JSONL files) is reused for every year; only the target
  instance differs. Seed packages are downloaded and extracted once outside the loop.
- The self-contained identity path constructs the token URL per iteration at
  `http://localhost:8081/connect/token/{schoolYear}`. Keycloak keeps its provider-native static token URL.
- If any BulkLoadClient invocation exits non-zero, the bootstrap script throws and halts before proceeding
  to subsequent years.
- When `-SchoolYearRange` is not specified, BulkLoadClient is invoked once without `--year`, targeting the
  single default instance.
- The same `$seedKey` / `$seedSecret` pair is reused across the loop. Developer bootstrap uses one
  `SeedLoader` application record per run whose instance associations cover every instance created or
  explicitly selected for that bootstrap.
- `--continue-on-error` remains in the per-year invocation shape as requested rerun tolerance;
  duplicate-resource `409 Conflict` handling remains BulkLoadClient-owned per Section 6.1.2.

**`-InfraOnly` + `-SchoolYearRange` combination**: This combination is fully supported. Infrastructure
startup, instance creation, and schema provisioning proceed as usual before any DMS health wait. When
`-DmsBaseUrl` is set, each BulkLoadClient invocation in the per-year loop targets that IDE-hosted DMS
process rather than the containerized endpoint. Without `-DmsBaseUrl`, the run stops after schema
provisioning and prints the settings the later IDE launch must use. That pre-DMS-only shape is still manual-only for the
school-year workflow; if the same run also needs SeedLoader credentials or seed loading under the IDE-hosted
process, `-DmsBaseUrl` must be present on the original wrapper invocation and must be forwarded to
`load-dms-seed-data.ps1` because DMS-916 does not define a multi-year resume from the stopped pre-DMS state.

### 10.3 Explicit Tenant Variation Is Out of Scope

Anything beyond the existing school-year instance helper is not part of DMS-916. If future work needs
tenant-specific extension variation, route filtering, or more isolated credential strategies, that is a
separate Config Service/runtime design concern rather than additional bootstrap scope for this story.

## 11. Backend Redesign Impact and DDL Provisioning

### 11.1 Backend Redesign Context

The DMS backend is moving from document-based storage (JSONB columns in PostgreSQL) to a relational model with typed tables per resource. This change affects database provisioning — the DDL is no longer a thin wrapper around a single JSONB column per resource, but a full relational schema with foreign keys, indexes, and extension tables.

Two concerns are intentionally kept separate in this design:

- **DDL provisioning** — deploying the relational schema to the database. This is backend-storage-model-specific.
- **Seed data loading** — populating descriptors, education organizations, and other bootstrap records through the API. This is storage-model-transparent: seed data goes through the API regardless of whether the backend stores records as JSONB or relational rows.

Keeping these concerns separate means the bootstrap sequence does not need to change again when the relational model stabilizes.

### 11.2 Current State: Bundled DDL and Seed Data

The current `-LoadSeedData` path in `start-local-dms.ps1` calls `setup-database-template.psm1`, which:

1. Downloads a NuGet package (`EdFi.Dms.Minimal.Template.PostgreSql.*`) identified by the `DATABASE_TEMPLATE_PACKAGE` environment variable.
2. Extracts a single `.sql` file from the package.
3. Copies that SQL file into the running `dms-postgresql` container and executes it with `psql`.

This single SQL file bundles two distinct concerns:

- **Schema DDL** — `CREATE TABLE`, `CREATE INDEX`, schema creation statements, and the `dms` schema itself. The script guards against re-execution by checking whether the `dms` schema already exists.
- **Seed data** — `INSERT` statements for descriptors, education organization types, and other bootstrap records.

Bundling these two concerns creates several problems:

- Schema deployment is opaque from the bootstrap script perspective — there is no discrete "schema is deployed" signal.
- Schema changes during backend redesign require publishing a new seed-data NuGet package even when only seed content changed, and vice versa.
- The SQL path bypasses API validation (see [API-Based Seed Data Loading](#6-api-based-seed-data-loading)), which has caused data integrity issues in the ODS world.

### 11.3 Proposed DDL Provisioning Hook

The authoritative phase order in [`command-boundaries.md`](command-boundaries.md) includes a discrete
DDL provisioning phase after instance configuration and before DMS becomes the active API host for the run.

**v1 canonical mechanism: direct SchemaTools provisioning.** The committed Docker defaults leave
`AppSettings__DeployDatabaseOnStartup=false`, and bootstrap keeps that setting in place. The existing Docker
startup script also has a legacy pre-launch provisioning path controlled by `NEED_DATABASE_SETUP` that
invokes `EdFi.DataManagementService.Backend.Installer.dll`; the DMS-916 flow must disable that path (for
example by setting `NEED_DATABASE_SETUP=false`) or remove it from the story-aligned startup path entirely.
`provision-dms-schema.ps1` uses the existing SchemaTools provisioning surface (`dms-schema ddl provision`)
or a thin helper over the same `IDatabaseProvisioner` and runtime-owned validation APIs against each target
database before any DMS process is expected to serve requests. This matches the repo's existing provisioning
surface and keeps schema preparation on the authoritative toolchain path rather than on host-start side
effects.

Bootstrap relies on a deliberately narrow integration contract for that path:

- invoke the documented SchemaTools command shape with staged schema inputs and target connection details,
- treat exit code `0` as success and any non-zero exit code as failure,
- surface SchemaTools diagnostics directly to the user, and
- avoid bootstrap-owned parsing of stdout/stderr to classify specific rejection reasons.

The README is therefore the public invocation reference, not the sole normative source for the full
internal provisioning semantics implemented by SchemaTools. This design does not define a bootstrap-specific
alternate CLI surface, and it does not require bootstrap to reverse-engineer undocumented text-output
details in order to implement safe provisioning behavior.

**`provision-dms-schema.ps1` / `Invoke-DmsSchemaProvisioning`** in v1 delegates to that shared path. Its
responsibilities are:

1. Resolve the staged schema inputs for the run and read the expected `EffectiveSchemaHash` metadata
   produced earlier by `prepare-dms-schema.ps1` for logging or comparison.
2. Collect the target connection strings and dialect details from the DMS instances selected or created by
   `configure-local-dms-instance.ps1`.
3. Invoke `dms-schema ddl provision` (or a thin helper over the same provisioning/runtime contract) for the
   selected targets before DMS starts, using the staged schema files from `prepare-dms-schema.ps1`.
4. Let the shared provisioning/runtime contract perform the authoritative live-state inspection, any
   required provisioning work, and the serviceability checks needed to accept or reject the target for the
   selected schema set.
5. Continue only when that authoritative path returns success; otherwise fail fast on the non-zero exit code
   and surface its diagnostics without bootstrap-owned classification of specific stdout/stderr text.

**Precomputed fingerprint metadata:** `prepare-dms-schema.ps1` still records the expected
`EffectiveSchemaHash` using the same algorithm DMS and `dms-schema hash` already use. The provisioning phase
may log that value, or compare it through a stable machine-readable SchemaTools contract if one is added,
but the direct `dms-schema ddl provision` handoff is the staged schema files plus target connection details.
SchemaTools computes the effective schema and hash internally, and the shared runtime/SchemaTools path
remains the authority for serviceability checks such as schema-component validation and resource-key seed
validation.

```powershell
Invoke-DmsSchemaProvisioning `
    -SchemaPaths $stagedSchemaPaths `
    -TargetConnectionStrings $targetConnectionStrings
```

**Failure detection** happens in `provision-dms-schema.ps1` itself. `dms-schema ddl provision` already
executes inside a transaction and performs preflight validation through the repo's existing provisioning
APIs. Because DMS-916 delegates to that authoritative path before DMS starts, the DMS-start/health-wait
phase only waits for DMS health; it does not introduce a second bootstrap-owned schema-readiness classifier
after startup.

**Requirements on `Invoke-DmsSchemaProvisioning`:**

- Reuses the existing DMS `EffectiveSchemaHash`; it does not invent a second bootstrap-only fingerprint.
- Delegates live target-database state evaluation to the shared SchemaTools/runtime-owned contract rather
  than inventing a bootstrap-only classifier.
- Uses the existing SchemaTools provisioning surface instead of routing DDL through DMS startup behavior.
- Is idempotent - safe to call on any re-run.
- Returns only after the authoritative provisioning/validation path has completed.

#### Exact Schema Footprint

The DDL hook deploys the **exact physical schema** implied by the selected staged `ApiSchema.json` set. A
core-only run provisions only core tables. A core plus Sample run provisions the sample-extension tables
required by that combined schema set. Extension selection therefore affects both the runtime API surface and the physical
database footprint.
This means:

- The DDL provisioning step targets the selected database set and the exact selected schema combination, not
  a platform-wide superset of extension tables.
- Adding an extension to the selected schema set requires provisioning a database whose physical schema
  includes that extension's tables.
- Removing an extension from the selected schema set means an already-provisioned broader database is no
  longer considered aligned for that run. DMS-916 does not define in-place table pruning; bootstrap treats
  that database as incompatible existing state and requires recreate/reprovision through the supported
  provisioning path.

### 11.4 Separating Schema Deployment from Seed Data Loading

The proposed split of the current `setup-database-template.psm1` responsibilities:

| Concern | Current location | Proposed location |
|---------|-----------------|-------------------|
| Schema DDL (CREATE TABLE, indexes, schema) | Bundled in NuGet SQL template | Direct SchemaTools provisioning (`dms-schema ddl provision` or an equivalent helper over the same APIs) against the selected staged schema set and its exact physical footprint |
| Seed data (descriptors, ed-org types, bootstrap records) | Bundled in NuGet SQL template | API-based JSONL loading via BulkLoadClient ([API-Based Seed Data Loading](#6-api-based-seed-data-loading)) |

`setup-database-template.psm1` is deprecated as part of this design. Its removal is tracked in the
API-based seed-delivery slice (see [Companion Implementation Stories](#13-companion-implementation-stories)).

### 11.5 Selected ApiSchema.json Drives Exact Physical DDL Shape

The DDL deployed to the database is derived from the selected staged `ApiSchema.json` set for the run (see
[Section 8.2](#82-proposed--extensions-parameter) and [Section 11.3](#113-proposed-ddl-provisioning-hook)).
Under this strong DMS-916 reading, selecting different extension combinations changes the required physical
table set, not just the expected hash or runtime API surface. The separation of concerns is:

- **Schema selection drives the required DDL target** - the resolved `ApiSchema.json` package set produces
  staged schema context consumed by `provision-dms-schema.ps1` before bootstrap continues.
  The expected `EffectiveSchemaHash` is recorded as metadata for logging or comparison.
- **Schema selection also drives physical table creation** - only the tables required by the selected schema
  set are considered aligned for the run. Core-only selection yields only core tables. Core plus Sample
  yields the core tables plus the sample-extension tables required by that combined schema set.
- **Schema selection still controls API surface and security** - the `-Extensions` value and the resulting
  `ApiSchema.json` determine which REST endpoints are exposed and which claimsets are loaded, and now those
  runtime inputs are expected to stay in sync with the exact physical schema provisioned for that run.
- **`Invoke-DmsSchemaProvisioning` consumes schema context at the target level** - the bootstrap script does
  not pass individual resource definitions to the hook, but it does pass the staged schema paths and target
  connection strings so the authoritative path can validate the correct provisioning context. The derived
  expected hash may be logged, or compared through a stable machine-readable tool contract if one is added,
  but it is not a required provisioning input. In v1 the function drives direct SchemaTools provisioning
  rather than toggling DMS startup behavior. A representative signature:

  ```powershell
  Invoke-DmsSchemaProvisioning `
      -SchemaPaths $stagedSchemaPaths `
      -TargetConnectionStrings $targetConnectionStrings
  ```

- **Changing `-Extensions` is a schema-compatibility decision** - adding or removing an extension changes
  the selected physical schema footprint for the run. The authoritative provisioning path validates or
  provisions against the newly selected staged schema set rather than silently reusing an incompatible
  existing database.

### 11.6 Forward Compatibility

The phase ordering is identical whether the backend uses JSONB or relational storage; the authoritative
sequence remains [`command-boundaries.md`](command-boundaries.md) Section 4. The only storage-model-sensitive
phase is `provision-dms-schema.ps1`, because that phase delegates to the shared SchemaTools/runtime
provisioning and validation implementation. If the backend redesign changes the authoritative provisioning
surface, bootstrap still delegates to that surface rather than inventing a bootstrap-owned schema-readiness
model.

---

## 12. IDE Debugging Workflow

This section documents how developers run or debug DMS locally in an IDE (Visual Studio, Rider) while Docker manages the supporting infrastructure. It covers the architecture, the mechanism for starting infrastructure without DMS, the environment variables required for the local process, and how bootstrap interacts with a locally running DMS instance.

> **Status (read with Section 14.1).** The IDE workflow described here is the accepted DMS-916 design,
> not a feature that is already wired into `start-local-dms.ps1`. The `-InfraOnly` switch and the
> `-DmsBaseUrl` continuation behavior are still labeled "Proposed" in this section because their
> implementation is owned by [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md)
> and is tracked as "Designed, implementation pending" in Section 14.1 and as an operationally
> blocked item in Section 14.2. Read the architecture and parameter narrative below as the design
> contract those tickets must implement, not as a description of behavior available today.

### 12.1 Architecture

The IDE debugging pattern follows the standard "Docker for infrastructure, local process for the application under development" model:

- **Docker manages**: PostgreSQL (exposed on `localhost:5435`), Kafka (bootstrap server `localhost:9092`), the Configuration Service / identity provider (exposed on `localhost:8081`), and any optional supporting services (Kafka UI, OpenSearch).
- **Developer runs**: the DMS ASP.NET Core process inside an IDE on a local port (e.g., `http://localhost:5198` or any available port). The IDE process connects outward to Docker services using `localhost` addresses rather than Docker-internal hostnames such as `dms-postgresql` or `dms-config-service`.

This separation means the DMS binary under the debugger is the live code being edited, while all persistence
and auth services are stable and shared across debug sessions. The staged schema workspace is part of that
topology: bootstrap materializes `ApiSchema*.json` files once under
`eng/docker-compose/.bootstrap/ApiSchema/`, `dms-schema hash` runs over those exact staged files, the
Docker-hosted DMS bind-mounts them at `/app/ApiSchema`, and the IDE-hosted DMS reads the host path
directly. This is the only sanctioned local-process variation within the Docker-first design; it does not
define a second non-Docker bootstrap path.

```text
+-----------------------------------------------------------+
| Docker network (dms)                                      |
|                                                           |
|  dms-postgresql :5432      -> localhost:5435              |
|  dms-config-service :8081  -> localhost:8081              |
|  kafka :9092               -> localhost:9092              |
+-----------------------------------------------------------+

+-----------------------------------------------------------+
| Repository workspace                                      |
|                                                           |
|  eng/docker-compose/.bootstrap/ApiSchema/                 |
|  - staged core ApiSchema.json                             |
|  - staged extension ApiSchema*.json                       |
+-----------------------------------------------------------+

                same staged files hashed and consumed

+-----------------------------------------------------------+
| Developer machine                                         |
|                                                           |
|  IDE runs DMS at http://localhost:5198                    |
|  AppSettings__UseApiSchemaPath=true                       |
|  AppSettings__ApiSchemaPath=<repo>/eng/docker-compose/    |
|                                .bootstrap/ApiSchema       |
+-----------------------------------------------------------+
```

The local DMS process must resolve all service addresses using `localhost` and the externally exposed Docker ports. The Docker-internal hostnames (`dms-postgresql`, `dms-config-service`) are not reachable from the host.

### 12.2 Starting Infrastructure Without DMS

The current `start-local-dms.ps1` always starts the DMS container. To support the IDE debugging workflow, the script needs a mechanism to bring up only the infrastructure services.

**Current state**: A `-NoDmsInstance` flag exists on the script, but it does not suppress DMS container
startup. The current script still includes `local-dms.yml` in the compose file list and starts the normal
DMS service in non-teardown flows; `-NoDmsInstance` only changes whether bootstrap creates instance records
in the Config Service.

**Proposed mechanism**: Add an `-InfraOnly` switch to `start-local-dms.ps1` that excludes the DMS service
from the compose startup while still bringing up the supporting infrastructure used by the canonical
bootstrap flow:

```powershell
# Schema and claims must be staged first (prepare-dms-schema.ps1, then prepare-dms-claims.ps1).
# Then start PostgreSQL + Kafka + Config Service; skip DMS container.
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
```

Implementation approach: `local-dms.yml` defines the `dms` service. When `-InfraOnly` is specified, the script passes `--scale dms=0` to `docker compose up`, or uses a dedicated compose override file (`local-infra-only.yml`) that omits the `dms` service. The simpler `--scale dms=0` approach is preferred to avoid maintaining a parallel compose file.

`-InfraOnly` does not make the Config Service optional. The canonical bootstrap still starts CMS because DMS
instance discovery, claimset seeding, and schema provisioning depend on it. `-InfraOnly` changes
only the Docker startup scope; what happens after infrastructure is ready depends on whether the wrapper
carries an external DMS endpoint into the post-provision DMS-start/health-wait step:

- `-InfraOnly` alone completes the infrastructure phase only: infrastructure startup and readiness checks.
  Any later instance creation, optional smoke-test credential creation, schema provisioning, or printed IDE
  guidance comes from later phase commands or wrapper orchestration, not from `start-local-dms.ps1` itself.
- `-InfraOnly -DmsBaseUrl <url>` is not passed to the initial infrastructure-only invocation. The wrapper
  first runs instance creation, optional smoke-test credential creation, and schema provisioning, then uses
  the same value at the DMS-start/health-wait phase to wait for the explicit external DMS endpoint. Once
  health is confirmed, any later DMS-dependent work remains owned by wrapper orchestration or by the next
  explicit phase command, such as `load-dms-seed-data.ps1 -DmsBaseUrl <url>`, when wrapper `-LoadSeedData`
  is selected.

**Relationship with `-NoDmsInstance`**: `-NoDmsInstance` skips creating DMS instance records in the Config Service but still starts the DMS container in the normal flow. `-InfraOnly` skips the DMS container entirely. When `-InfraOnly` is used, `-NoDmsInstance` is typically omitted because the IDE-hosted DMS process still reads instance records from the Config Service just as the containerized DMS would. If `-NoDmsInstance` is used in this workflow, the same narrow reuse rule applies: exactly one existing instance must already be present, `-SchoolYearRange` is not supported, and later phases consume that selected target rather than rediscovering it.

### 12.3 Key Environment Variables

When DMS runs outside Docker, ASP.NET Core reads configuration from `appsettings.json` and environment
variable overrides. The settings below are the normal IDE-hosted DMS runtime contract for connecting to the
Docker-managed infrastructure after bootstrap has already completed schema provisioning.

The values below assume the default port mapping from `.env.example`.

`start-local-dms.ps1` owns establishing the dev-only `CMSReadOnlyAccess` local contract through the
identity-provider setup path. Keycloak uses `setup-keycloak.ps1` with the read-only Config Service scope;
self-contained identity uses `setup-openiddict.ps1 -InsertData` with the same read-only scope after CMS is
healthy. `configure-local-dms-instance.ps1` may validate and emit the local credential details needed by
IDE guidance, but it does not create or scope that client. Do not implement this client through
`/connect/register` unless that endpoint supports read-only scope selection; the current registration path
creates admin-scoped clients.

| Variable (appsettings key) | Local value | Description |
|---|---|---|
| `ConnectionStrings__DatabaseConnection` | `host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;` | PostgreSQL connection. Uses `localhost:5435` (Docker-exposed port) instead of Docker-internal `dms-postgresql:5432`. |
| `ConfigurationServiceSettings__BaseUrl` | `http://localhost:8081` | Config Service URL. Uses `localhost:8081` instead of Docker-internal `http://dms-config-service:8081`. |
| `ConfigurationServiceSettings__ClientId` | `CMSReadOnlyAccess` | Local identity setup read-only OAuth client ID that DMS uses to authenticate against the Config Service during local development. |
| `ConfigurationServiceSettings__ClientSecret` | `<local-cms-readonly-secret>` | Local-development secret for `CMSReadOnlyAccess`, taken from the identity setup output or IDE guidance. **DEV-ONLY**: This localhost credential must not be reused in shared, remote, or production environments. |
| `ConfigurationServiceSettings__Scope` | `edfi_admin_api/readonly_access` | OAuth scope for Config Service read access. |
| `AppSettings__AuthenticationService` | `http://localhost:8081/connect/token` (self-contained) or `http://localhost:8045/realms/edfi/protocol/openid-connect/token` (Keycloak) | Token endpoint must match the selected `-IdentityProvider`, using host-reachable URLs rather than Docker-internal addresses. |
| `JwtAuthentication__Authority` | `http://localhost:8081` (self-contained) or `http://localhost:8045/realms/edfi` (Keycloak) | JWT authority for token validation, translated to host-local endpoints for IDE debugging. |
| `JwtAuthentication__MetadataAddress` | `http://localhost:8081/.well-known/openid-configuration` (self-contained) or `http://localhost:8045/realms/edfi/.well-known/openid-configuration` (Keycloak) | OIDC discovery document URL for the selected identity provider. |
| `JwtAuthentication__ClientRole` | `dms-client` | Required DMS client role issued by the Docker-managed local identity provider. Overrides the committed DMS default so IDE-hosted DMS uses the same role contract as Docker-hosted local DMS. |
| `JwtAuthentication__RoleClaimType` | `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` | Role claim type emitted by the Docker-managed local identity provider. Overrides the committed DMS default so local IDE token validation maps `dms-client` into role claims. |
| `AppSettings__UseApiSchemaPath` | `true` | Required for IDE-hosted DMS so it reads the staged schema workspace instead of falling back to the default packaged schema input. |
| `AppSettings__ApiSchemaPath` | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` | Host path to the staged schema workspace created by bootstrap. This must point at the same staged files used for `dms-schema hash` and for Docker-hosted DMS runs. |
| `AppSettings__UseRelationalBackend` | `true` | Required for this IDE workflow because DMS-916 provisions the relational schema before DMS starts and expects the IDE-hosted process to run against that relational backend. |
| `AppSettings__DeployDatabaseOnStartup` | `false` | Keep the committed default at `false`. Bootstrap provisions schema directly before DMS starts and does not rely on DMS startup side effects in the IDE path. |
| `Serilog__MinimumLevel__Default` | `Debug` | Log verbosity for local development. |

For Kafka-based event streaming (if enabled), the Kafka bootstrap server must be configured to `localhost:9092` rather than the Docker-internal broker address.

**Optional diagnostic setting:** If a developer intentionally runs SchemaTools manually outside the normal
bootstrap path, they may also define an admin-capable connection string for that separate tooling step. It
is not part of the normal IDE-hosted DMS runtime contract because this design keeps
`AppSettings__DeployDatabaseOnStartup=false` and performs schema provisioning before DMS starts.

These values can be placed in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json` (git-ignored) so they are picked up automatically by the ASP.NET Core configuration system in the `Development` environment without modifying the committed `appsettings.json`. An illustrative guidance file - not generated by bootstrap and not read as a bootstrap input - is provided at `reference/design/backend-redesign/design-docs/bootstrap/appsettings.Development.json.example`:

```json
{
  "ConnectionStrings": {
    "DatabaseConnection": "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"
  },
  "ConfigurationServiceSettings": {
    "BaseUrl": "http://localhost:8081",
    "ClientId": "CMSReadOnlyAccess",
    "ClientSecret": "<local-cms-readonly-secret>",
    "Scope": "edfi_admin_api/readonly_access"
  },
  "AppSettings": {
    "UseApiSchemaPath": true,
    "ApiSchemaPath": "<repo-root>/eng/docker-compose/.bootstrap/ApiSchema",
    "UseRelationalBackend": true,
    "DeployDatabaseOnStartup": false,
    "AuthenticationService": "http://localhost:8081/connect/token"
  },
  "JwtAuthentication": {
    "Authority": "http://localhost:8081",
    "MetadataAddress": "http://localhost:8081/.well-known/openid-configuration",
    "ClientRole": "dms-client",
    "RoleClaimType": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

This file is illustrative IDE guidance only. It shows the common single-instance workflow using the non-qualified self-contained token endpoint and is intentionally not a multi-year template. It also explicitly enables the relational backend because this IDE flow assumes bootstrap has already provisioned the relational schema. Bootstrap does not generate it and does not read it as a bootstrap input; the developer copies it to `appsettings.Development.json`, replaces `<local-cms-readonly-secret>` with the credential from local identity setup output or printed IDE guidance, and replaces `<repo-root>` with the native absolute path for the host platform running the IDE, for example a Windows path, `/Users/...`, or `/home/...`.

When the existing `-SchoolYearRange` developer workflow is used with self-contained identity and the
developer continues bootstrap against an IDE-hosted DMS process for one selected school year, update the
token-endpoint setting for that run so it matches the same route-qualified context used by seed loading:

- `AppSettings:AuthenticationService` -> `http://localhost:8081/connect/token/{schoolYear}`

For example, debugging the 2025 instance uses:

```json
{
  "AppSettings": {
    "AuthenticationService": "http://localhost:8081/connect/token/2025"
  }
}
```

The authority and metadata-address values remain host-root URLs in this design:

- `JwtAuthentication:Authority` -> `http://localhost:8081`
- `JwtAuthentication:MetadataAddress` -> `http://localhost:8081/.well-known/openid-configuration`

### 12.4 Bootstrap with Local DMS

In the normal Docker workflow, the infrastructure-lifecycle phase command `start-local-dms.ps1` targets the
containerized DMS at `http://localhost:8080`. When DMS runs in an IDE, the initial infrastructure invocation
uses `-InfraOnly` and stops before any DMS process is started. The broader bootstrap contract remains the
composable phase sequence defined in [`command-boundaries.md`](command-boundaries.md); instance creation and
schema provisioning still run before any DMS health wait.

**Continuation rule**: Instance creation and schema provisioning are Config Service / database concerns and
do not require DMS to be running. When a wrapper run chooses `-DmsBaseUrl`, the wrapper must hold that value
until after `configure-local-dms-instance.ps1` and `provision-dms-schema.ps1` have completed. Only then does
the post-provision DMS-start/health-wait phase poll the IDE-hosted DMS process. The bootstrap script cannot
start the IDE process - the developer must start DMS in the IDE before or during that post-provision health
wait window. DMS-916 intentionally does **not** define a stopped-then-resume second bootstrap invocation.
`-InfraOnly` without `-DmsBaseUrl` is a terminal pre-DMS preparation shape for manual IDE startup against an
already prepared environment, not a checkpoint from which a later bootstrap run picks up unfinished
DMS-dependent work. Any run that intends SeedLoader credential bootstrap or seed loading must declare that
up front via the wrapper's `-InfraOnly -DmsBaseUrl`, but those later steps remain phase-owned rather than
`start-local-dms.ps1` behavior.

**Proposed mechanism**: The wrapper exposes `-DmsBaseUrl` as the external IDE-hosted DMS endpoint for
same-invocation continuation. It forwards `-InfraOnly` to the first `start-local-dms.ps1` infrastructure
invocation, but it does **not** forward `-DmsBaseUrl` until the post-provision DMS-start/health-wait
invocation:

```powershell
# Manual phase flow: provision the environment before starting or waiting for IDE-hosted DMS.
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
pwsh eng/docker-compose/configure-local-dms-instance.ps1 -AddSmokeTestCredentials
pwsh eng/docker-compose/provision-dms-schema.ps1

# Start DMS in the IDE now, using the printed settings and staged schema path.
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"

# Optional manual seed phase: target the same IDE-hosted DMS endpoint explicitly.
pwsh eng/docker-compose/load-dms-seed-data.ps1 -DmsBaseUrl "http://localhost:5198"
```

When the infrastructure was started with a non-default identity provider, the matching seed phase must pass
the same provider explicitly, for example
`load-dms-seed-data.ps1 -IdentityProvider keycloak -DmsBaseUrl "http://localhost:5198"`.

When `-InfraOnly` is used **without** `-DmsBaseUrl`, `start-local-dms.ps1` itself performs only:

1. Docker infrastructure startup without the DMS container.
2. Config Service readiness checks. In DMS-916 this means `/health` is green and bootstrap has verified
   that CMS applied the staged claims content as defined in `command-boundaries.md`.
3. Exit after the infrastructure phase is ready.

The broader IDE preparation flow then continues through separate phase commands or the thin wrapper:

1. `configure-local-dms-instance.ps1` creates or confirms the target DMS instance records, may validate or
   report the fixed dev-only `CMSReadOnlyAccess` client created by local identity setup, and may optionally
   create smoke-test credentials.
2. `provision-dms-schema.ps1` provisions or validates the target databases directly through SchemaTools.
3. The developer starts DMS in the IDE using the printed next-step guidance and the staged schema path.
4. `load-dms-seed-data.ps1` may run later only when a healthy DMS endpoint is available.

A later bootstrap invocation may still be used to bring supporting infrastructure back up for an already
bootstrapped environment, but that is a new availability run, not a resume of unfinished DMS-dependent work
from the earlier stopped invocation.

When `-DmsBaseUrl` is provided alongside wrapper `-InfraOnly`, the same pre-DMS phases still complete first:

1. Start Docker infrastructure (PostgreSQL, Kafka, Config Service) but not the DMS container.
2. Wait for Config Service readiness. In DMS-916 this means `/health` is green and bootstrap has verified
   that CMS applied the staged claims content as defined in `command-boundaries.md`.
3. Run `configure-local-dms-instance.ps1` to create or confirm the target DMS instances, validate or report
   the fixed dev-only `CMSReadOnlyAccess` client created by local identity setup, and optionally create
   smoke-test credentials.
4. Run `provision-dms-schema.ps1` to provision or validate the target databases directly through SchemaTools.
5. Prompt the developer to start DMS in the IDE using the printed next-step guidance and the staged schema
   path, then poll `$DmsBaseUrl/health` at a configurable interval with a maximum timeout. Success is
   defined as an HTTP 200 response from the health endpoint; timeout or persistent non-success is fatal for
   the run.
6. If the run also requested seed loading, wrapper orchestration or the developer then invokes
   `load-dms-seed-data.ps1 -DmsBaseUrl $DmsBaseUrl -IdentityProvider $IdentityProvider`, which owns
   SeedLoader credential bootstrap and seed loading targeting the same healthy endpoint, token endpoint,
   and target instance set selected by the surrounding phase flow.

`start-local-dms.ps1` does not call `Get-DmsInstances` again and does not own the post-health handoff
policy. Its external endpoint health wait is only valid after the selected instances and target databases
already exist.

**Debugging during and after bootstrap**: IDE debugging must work both *during* bootstrap (the DMS process receives live API calls as seed data loads, exercising the full request path under the debugger) and *after* bootstrap (subsequent debug sessions reuse an already-bootstrapped environment without re-running seed loading). The `-DmsBaseUrl` mechanism satisfies both scenarios:

- *During*: Developer runs the wrapper with `-InfraOnly -DmsBaseUrl`; the wrapper starts infrastructure,
  configures instances, provisions schema, and then waits while the developer starts DMS in the IDE and
  attaches the debugger. When wrapper `-LoadSeedData` is selected, the wrapper passes that same `-DmsBaseUrl` value
  and the selected `-IdentityProvider` value to `load-dms-seed-data.ps1`, so breakpoints in request
  handlers fire as seed data POST calls arrive.
- *After*: Subsequent sessions can use `-InfraOnly` to bring up the Docker-managed infrastructure, then
  start DMS in the IDE against the already-bootstrapped databases without re-running seed loading. This is a
  fresh infrastructure bring-up for an already-complete bootstrap state, not a resume of skipped
  DMS-dependent work from an earlier pre-DMS-only invocation.

No IDE-specific runtime architecture is introduced for this workflow. The IDE path uses the same DMS runtime
configuration shape as Docker-hosted DMS, including the normalized ApiSchema workspace. The separate Story 04
runtime change removes the bootstrap-path dependency on `*.ApiSchema.dll` content loading; it is not an
IDE-only behavior.

## 13. Companion Implementation Stories

This section breaks the design into DMS-side companion implementation stories plus the cross-repo MetaEd
package-production switch-over. It is intentionally not a
prescribed multi-ticket rollout plan. DMS-916 is satisfied when the acceptance criteria in Section 14.1 are
implemented without adding extra bootstrap responsibilities. Teams may merge, split, or reorder work items as
needed.
Companion implementation-ready story definitions live in
[`../../epics/16-bootstrap/EPIC.md`](../../epics/16-bootstrap/EPIC.md).

### 13.0 Story-Aligned Implementation Map

| Slice | Companion story definition | Story outcome |
|-------|----------------------------|---------------|
| Schema and security selection | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md) | Story 00 delivers direct filesystem `-ApiSchemaPath` schema staging and `-ClaimsDirectoryPath` security staging over the stable filesystem ApiSchema workspace. |
| Schema deployment safety | [`../../epics/16-bootstrap/01-schema-deployment-safety.md`](../../epics/16-bootstrap/01-schema-deployment-safety.md) | Bootstrap invokes the authoritative SchemaTools/runtime-owned provisioning and validation path over the staged schema set before DMS starts. The expected `EffectiveSchemaHash` is diagnostic metadata, and different extension selections remain different physical-schema targets. |
| API-based seed delivery | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md) | Built-in repo-local seed sources remain deterministic, and custom directories load through the repo-pinned BulkLoadClient path using the root bootstrap manifest from schema/security staging, dedicated `SeedLoader` credentials, and the existing `-SchoolYearRange` developer workflow when present. |
| Entry point and IDE workflow | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md) | `start-local-dms.ps1` is the infrastructure-lifecycle phase command, while the normative bootstrap contract remains the composable phase-command set. `-InfraOnly` / `-DmsBaseUrl` define the two IDE-hosted workflow shapes: stop after schema provisioning, or carry the wrapper run through schema provisioning before automatically health-waiting against the external endpoint. |
| Replace DMS ApiSchema DLL resource loading | [`../../epics/16-bootstrap/04-apischema-runtime-content-loading.md`](../../epics/16-bootstrap/04-apischema-runtime-content-loading.md) | DMS runtime reads metadata/specification JSON and XSD assets from the normalized ApiSchema workspace and ApiSchema asset manifest instead of requiring `*.ApiSchema.dll` assemblies for the bootstrap path. DMS runtime does not read NuGet packages directly. |
| MetaEd ApiSchema asset packaging | [`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md) | MetaEd publishes asset-only ApiSchema NuGet packages whose loose-file payload can be normalized by Story 06 without assembly-resource extraction. This is a cross-repo package-production story over the same filesystem contract, not a new DMS runtime responsibility. |
| Package-backed standard schema selection | [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) | Story 06 delivers omitted `-Extensions` core-only mode and named `-Extensions` standard mode by resolving asset-only packages into the same normalized ApiSchema workspace, ApiSchema asset manifest, and root bootstrap manifest schema section used by Story 00. |

**Cross-story dependency notes**

- Story 00 may deliver schema/security selection independently, including the root bootstrap manifest sections
  that record the staged schema/security facts later seed delivery needs. No seed catalog entry may
  advertise a built-in seed package until Story 02 adds the top-level `SeedLoader` claim set to
  embedded `Claims.json` and the matching extension fragment supplies the required `SeedLoader`
  permissions.
- Story 00 owns the `.gitignore` entry for `eng/docker-compose/.bootstrap/` before generated staging
  artifacts are written. Story 03 owns the remaining repo-local `.bootstrap/` workspace lifecycle hygiene and
  the user-facing migration note for the narrowed `-NoDmsInstance` contract.
- Story 04 depends on Story 00's normalized ApiSchema workspace and ApiSchema asset manifest contract.
  Docker-hosted and IDE-hosted DMS are not fully on the DMS-916 staged-asset path until Story 04 removes the bootstrap-path
  `ContentProvider` dependency on `*.ApiSchema.dll` assemblies.
- Story 05 is the cross-repo MetaEd package-production switch-over. It is a prerequisite for Story 06
  package-backed standard mode against published packages, but it is not a prerequisite for Story 00's
  direct filesystem ApiSchema loading contract. Story 00, Story 04, and Story 05 can proceed in parallel
  because they all meet at the normalized filesystem workspace.
- Story 06 owns the standard developer schema-selection path: omitted `-Extensions` for core-only bootstrap
  and named `-Extensions` for package-backed extension selection. It depends on Story 05 for asset-only package
  inputs and on Story 00 for the shared workspace, ApiSchema asset manifest, claims-staging, and root
  bootstrap manifest contracts. Story 02 remains the owner for seed delivery and built-in extension seed lookup.

### 13.1 Explicitly Out of Scope for DMS-916

- Optional post-bootstrap orchestration such as smoke, E2E, or integration test runners, and SDK generation.
- A persisted bootstrap workflow control plane or resume state. The root bootstrap manifest records only stable
  prepared inputs and fingerprints for phase handoff and compatibility checks.
- New tenant models or credential strategies beyond the existing `-SchoolYearRange` developer workflow.
- Published compose/script parity outside the local developer bootstrap path.
- Broader cleanup or deprecation work that is not required to satisfy the story-aligned slices above.

## 14. Design Delivery vs. Operational Delivery

> **Important:** Merging this design artifact into the branch does **not** mean the bootstrap flow is
> working end to end. This section draws a hard line between what design work alone delivers and what
> remains blocked on implementation or cross-team dependencies.

### 14.1 Design-Complete Criteria

This table is the authoritative scope contract for DMS-916. If a change is not needed to satisfy one of
these rows, it is outside the intended scope of this design spike.

> **DDL interpretation - approved DMS-916 contract.** The database-schema-provisioning row reflects the
> interpretation approved for DMS-916 and defined in Sections 3.2 and 11.5: selected `ApiSchema.json`
> drives the DDL target/version/hash validation path and the exact physical table set for the run.
> Per-selection physical schema shaping is in scope for this design.

> **How to read the status columns.** "Design completeness" reports whether the normative design is in
> place in this artifact set. "Delivery readiness" reports whether the implementation work needed to put
> that design in front of a developer is in place today; rows still waiting on companion-story implementation
> work read as "Designed, implementation pending" rather than as fully achieved. "Blocking dependency /
> gap" names the specific pending item (story, ticket, or cross-team dependency) so a stakeholder can
> trace each non-ready row to its owner without re-reading Sections 14.2 and 14.3.
>
> **Design-spike evaluation rule.** DMS-916 is a design spike. For spike close-out purposes, **"Design completeness = Designed" is the success criterion for each row.** "Delivery readiness" and "Blocking dependency / gap" are informational tracking data — they name what remains for implementation teams and are not deductions against the design deliverable. A criterion marked "Designed" in the design-completeness column is satisfied for spike purposes even when delivery readiness is pending or externally blocked.

| Ticket acceptance criterion | Evidence in this document | Design completeness | Delivery readiness | Blocking dependency / gap |
|-----------------------------|---------------------------|---------------------|--------------------|---------------------------|
| ApiSchema.json selection - how developers choose core, extensions, or custom path | Sections 3.3, 8.2, 8.4, 9.3; [`apischema-container.md`](apischema-container.md) | Designed. The selected schema set is staged as a normalized file-based ApiSchema asset container: schema JSON drives hash/DDL/API surface, while the ApiSchema asset manifest indexes optional static content for runtime metadata/XSD endpoints. Direct filesystem ApiSchema loading is the stable core contract; package-backed selection is the Story 06 input-materialization path. | Designed, implementation pending. Story 00 owns direct filesystem workspace staging through `-ApiSchemaPath`; Story 04 owns runtime file-based content loading; Story 05 owns producing asset-only MetaEd packages; Story 06 owns package-backed no-argument core-only mode and named `-Extensions` delivery. | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md); [`../../epics/16-bootstrap/04-apischema-runtime-content-loading.md`](../../epics/16-bootstrap/04-apischema-runtime-content-loading.md); [`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md); [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) |
| Security database configuration from ApiSchema.json | Sections 4.3-4.5, 9.3 (`-ClaimsDirectoryPath`) | Designed. DMS-916 derives base claims inputs from the staged schema and available claims artifacts in every schema-selection mode. Expert `-ApiSchemaPath` mode uses `-ClaimsDirectoryPath` for additional non-core security fragments, with structural validation only. | Designed, implementation pending. Story 00 owns claims staging and direct-filesystem inputs; Story 06 feeds the same root bootstrap manifest schema contract for package-backed standard mode. | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md); [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md) |
| Database schema provisioning - DDL hook separated from seed data loading, driven by selected ApiSchema.json | Sections 3.2, 11.3-11.5; [`command-boundaries.md` Section 3.5](command-boundaries.md#35-provision-dms-schemaps1--authoritative-schema-provisioning) | Designed under the strong interpretation above. Selected schema drives the DDL target/version/`EffectiveSchemaHash` validation path and the exact physical schema provisioned for that run. DMS startup provisioning is explicitly disabled, including both `AppSettings__DeployDatabaseOnStartup` and the legacy `NEED_DATABASE_SETUP` / `Backend.Installer` pre-launch path. | Designed, implementation pending. Bootstrap delegates to the SchemaTools / runtime-owned provisioning path; readiness is gated on that surface remaining stable. | [`../../epics/16-bootstrap/01-schema-deployment-safety.md`](../../epics/16-bootstrap/01-schema-deployment-safety.md); SchemaTools dependency in Section 14.3 |
| Sample data loading - API-based JSON/JSONL loading replacing direct SQL, with repo-local Ed-Fi seed templates or developer-supplied JSONL directories paired with compatible schema/security inputs | Section 6 | Designed. All DMS-side design decisions are complete: BulkLoadClient consumption contract (Section 6.1), seed-source selection (`-SeedTemplate` / `-SeedDataPath`, Section 6.2), bootstrap manifest handoff (Section 6.2.5), combined seed workspace with collision detection (Section 6.3.1), per-year invocation for school-year paths (Section 10), and the bootstrap manifest compatibility boundary for `-SeedDataPath`, including explicit `-AdditionalNamespacePrefix` values for SeedLoader vendor authorization. The design target — API-based JSONL replacement of the deprecated direct-SQL path — is fully specified. | Designed, implementation pending. End-to-end delivery is blocked externally only by BulkLoadClient JSONL support; repo-local `Minimal` and `Populated` assets are DMS-owned implementation work. | ODS-6738 (BulkLoadClient JSONL); Story 02 implementation of repo-local seed assets and loader wiring |
| Extension selection - parameterized `-Extensions` flag driving schema and security automatically, and driving built-in seed data automatically only where the seed catalog defines a built-in seed package | Sections 3.3, 8.2-8.4 | Designed | Designed, implementation pending. Direct filesystem schema input belongs to Story 00; package-backed `-Extensions` materialization belongs to Story 06 after Story 05. Story 02 owns built-in extension seed lookup and loading from the bootstrap manifest. | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md); [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md); [`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md); [`../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md`](../../epics/16-bootstrap/06-package-backed-standard-schema-selection.md); see Section 14.2 |
| Credential bootstrapping - enhancements for seed data loading support | Section 7 | Designed. Both credential flows are fully specified: CMS-only `EdFiSandbox` smoke-test credentials (Section 7.2.1) and the separate DMS-dependent `SeedLoader` credential flow (Section 7.2.2), including the complete `SeedLoader` permission table (resource claim URI patterns, authorization strategies, operations). Adding the `SeedLoader` top-level claim set to the embedded CMS `Claims.json` is the very first implementation task in Story 02 Task 3 — the design specifies exactly what to add. | Designed, implementation pending. Story 02 Task 3 owns adding the `SeedLoader` claim set to `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json`; this is the first deliverable from that story and unblocks all DMS-side seed delivery. | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md) Task 3; see Section 14.2 |
| Bootstrap entry point and safe skip behavior - composable phase commands with optional same-invocation continuation | Sections 1, 9, 9.2-9.5 | Designed. The normative contract is the composable phase commands in `command-boundaries.md`; any thin wrapper is convenience only, may expose happy-path flags, and only sequences phases and forwards values owned by those phases. "Skip/resume" means safe skip behavior across phase commands plus optional same-invocation continuation via `-InfraOnly -DmsBaseUrl` after instance configuration and schema provisioning, not a persisted resume model. | Designed, implementation pending across the phase commands and the optional thin wrapper. | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md) and the phase-command implementation tickets |
| IDE debugging workflow - running DMS in IDE against Docker infrastructure | Section 12 | Designed | Designed, implementation pending. `-InfraOnly` and the post-provision `-DmsBaseUrl` continuation behavior are not yet implemented in `start-local-dms.ps1`. | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md); see Section 14.2 |
| Backend redesign awareness - forward-compatible with relational tables replacing JSONB | Section 11 | Designed | Achieved as a forward-compatibility property of this design; no separate implementation deliverable is required beyond honoring the SchemaTools provisioning boundary. | None within this story; tracked as the SchemaTools cross-team dependency in Section 14.3. |
| ODS initdev audit - informational reference only, not a gap-fill checklist | Sections 1-2 and `reference-initdev-workflow.md` | Designed | Achieved as informational reference; no implementation deliverable. | None |

### 14.2 Operationally Blocked Criteria

#### DMS-Internal Implementation Prerequisites (design complete; no external dependency)

The following items are fully designed in this document and their companion stories. No external team action
is required. The first deliverable from each owning story unblocks all downstream work in that story.

| Capability | What remains | Owning story task |
|---|---|---|
| `SeedLoader` claim set | Add the top-level `SeedLoader` definition and required core permissions to `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json`. The exact permission table is in Section 7.2.2. This is the first deliverable from Story 02 and is prerequisite to all seed-delivery testing. | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md) Task 3 |
| Repo-local built-in seed assets | Add deterministic JSONL files for `eng/docker-compose/seed-data/minimal/` and `eng/docker-compose/seed-data/populated/` matching the Section 6.2.2 manifests. These are DMS-owned developer bootstrap assets, not published deployment packages. | [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md) Task 2 |
| Direct filesystem schema + explicit-claims staging | Implement `-ApiSchemaPath` schema staging plus `-ClaimsDirectoryPath` staging per the command-boundary contracts in `command-boundaries.md` Sections 3.1–3.2. | [`../../epics/16-bootstrap/00-schema-and-security-selection.md`](../../epics/16-bootstrap/00-schema-and-security-selection.md) Tasks 1-5 |
| `-InfraOnly` flag for IDE debugging | Implement the `-InfraOnly` switch and post-provision `-DmsBaseUrl` continuation behavior on `start-local-dms.ps1` per Story 03. | [`../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md`](../../epics/16-bootstrap/03-entry-point-and-ide-workflow.md) Task 1 |
| Replace DMS ApiSchema DLL resource loading | Update DMS runtime so `ContentProvider` reads metadata/specification JSON and XSD assets from the normalized ApiSchema workspace and ApiSchema asset manifest instead of requiring `*.ApiSchema.dll` assemblies for the bootstrap path. DMS runtime does not read `.nupkg` files or NuGet cache layout directly. | [`../../epics/16-bootstrap/04-apischema-runtime-content-loading.md`](../../epics/16-bootstrap/04-apischema-runtime-content-loading.md) |

#### External Cross-Team Blockers (design complete; blocked on other teams)

The following items are design-complete on the DMS side. Delivery is blocked on cross-team dependencies.
See Section 14.3 for unblocking actions.

| Capability | Blocking condition | External dependency |
|---|---|---|
| API-based seed data loading (`load-dms-seed-data.ps1` via BulkLoadClient) | BulkLoadClient does not yet support `--input-format jsonl` or `--data <directory>` | ODS-6738 (ODS team) |

Asset-only ApiSchema package production is tracked as parallel package-transition work, not as a blocker for
the filesystem ApiSchema contract. It gates only Story 06 package-backed standard mode against
published packages; direct `-ApiSchemaPath` loading and the normalized workspace design can proceed
independently.

Published seed package distribution for deployment or agency provisioning remains with DMS-1119, but it is
not a blocker for the DMS-916 developer bootstrap path because `Minimal` and `Populated` resolve from
repo-local JSONL assets.

### 14.3 Blocking Cross-Team Dependencies

| Item | External owner | Unblocking action |
|---|---|---|
| `--input-format jsonl` support in BulkLoadClient | ODS team | ODS-6738: extend `EdFi.BulkLoadClient` with JSONL mode. The bootstrap consumption contract is in [Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract). This dependency must land before DMS-916 can deliver the intended direct-SQL replacement described in [`../../epics/16-bootstrap/02-api-seed-delivery.md`](../../epics/16-bootstrap/02-api-seed-delivery.md). |
| SchemaTools provisioning contract for the relational backend | DMS backend redesign team | The v1 bootstrap design assumes `dms-schema ddl provision` (or an equivalent runtime-owned provisioning surface) remains the authoritative pre-start provisioning and validation path over the staged schema set. If the backend team changes that surface, its inputs, or where final serviceability validation lives, `provision-dms-schema.ps1` must be re-pointed to the new authoritative path rather than preserving bootstrap-owned safety rules. |
| SchemaTools CLI version stability | DMS backend redesign team | Bootstrap depends on a stable SchemaTools CLI invocation shape (command name, argument surface, exit code `0`/non-zero contract). A major version break in the SchemaTools CLI could cause bootstrap to invoke with an incorrect argument surface rather than receiving a clean non-zero exit code. Before Story 01 implementation begins, the SchemaTools team should confirm that the CLI surface used by bootstrap (`dms-schema hash`, `dms-schema ddl provision`, documented arguments) is stable or publish a migration note alongside any breaking CLI change. Bootstrap does not require a minimum-version enforcement mechanism, but the team should verify the CLI surface against the README before Story 01 implementation. |

Until the relevant blockers are resolved, the affected bootstrap capabilities cannot be delivered end to end.
The seed-loading design target remains replacement of the deprecated direct-SQL path
(`setup-database-template.psm1`); the seed blockers do not change that scope contract.

Parallel package-transition work remains required before Story 06 package-backed standard mode can be
implemented. MetaEd must
replace the current DLL/resource ApiSchema package workflow with asset-only NuGet packages containing loose
schema JSON, optional static content, and a package manifest as defined in
[`apischema-container.md`](apischema-container.md). The packaging work is tracked in
[`../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md`](../../epics/16-bootstrap/05-metaed-apischema-asset-packaging.md), can
proceed in parallel with DMS bootstrap work, and does not gate the stable filesystem ApiSchema workspace
contract or direct `-ApiSchemaPath` loading.

### 14.4 Close-Out Status

The bootstrap architecture documented here — composable phase commands, expert-mode security boundary,
schema-driven DDL provisioning, and the optional thin convenience wrapper — is **accepted as the normative
DMS-916 design**. The phase ownership, parameter, readiness, and wrapper contract lives in
[`command-boundaries.md`](command-boundaries.md); stakeholders should treat that file as the implementation
contract that Stories 00-03 use for command behavior.

**Design acceptance is not feature completion.** The non-seed items marked "Designed, implementation pending"
in Section 14.1 — direct filesystem schema plus explicit-claims staging, the `-InfraOnly` /
`-DmsBaseUrl` IDE workflow, the `SeedLoader` claim set, and the phase-command surface itself — are **not delivered today**
and are not counted as complete by virtue of this document being merged. Each remains owned by its Story
00–03 ticket. Seed-delivery readiness is a separate concern, tracked under the cross-team blockers in
Section 14.3, and is not what this close-out is asserting.

A stakeholder reading only this section should leave with one takeaway: the design is settled and
approved; the implementation work it describes is tracked in the linked tickets and is not yet done.

## 15. Breaking Changes and Migration Notes

This section collects every deliberate behavior change introduced by DMS-916 in one place. Each entry names
the old behavior, the new DMS-916 behavior, and the one-line migration action required of existing scripts
or contributors.

| # | Area | Old behavior | New DMS-916 behavior | Migration action |
|---|------|--------------|----------------------|------------------|
| 1 | Default schema profile when `-Extensions` is omitted | `SCHEMA_PACKAGES` staged Data Standard 5.2 plus extensions by default in the existing `eng/docker-compose` flow | Bootstrap resolves and stages **core only** (`EdFi.DataStandard52.ApiSchema`) when `-Extensions` is omitted | Omit `-Extensions` for core-only runs; pass the needed extension identifiers through `-Extensions` for scripts that rely on extension schemas |
| 2 | `-NoDmsInstance` semantics | Generic "skip instance creation" switch used on fresh stacks as a convenient no-op | **Narrow rerun escape hatch only:** valid only when exactly one existing instance is present in the current tenant scope, and invalid with `-SchoolYearRange`; zero or multiple instances fail fast requiring teardown or manual preparation | Drop the flag on fresh-stack runs, or pre-create exactly one target instance before rerunning with `-NoDmsInstance` |
| 3 | Seed-loading parameter ownership | `-LoadSeedData`, `-SeedTemplate`, and `-SeedDataPath` were accepted directly by `start-local-dms.ps1` | `-LoadSeedData` is a wrapper-level opt-in only; direct `load-dms-seed-data.ps1` invocation always loads seed data and owns `-SeedTemplate`, `-SeedDataPath`, `-AdditionalNamespacePrefix`, `-BootstrapManifestPath`, the seed-phase BulkLoadClient target `-DmsBaseUrl`, and the seed-phase token endpoint selector `-IdentityProvider`; `start-local-dms.ps1` no longer accepts seed-source or seed-authorization parameters | Call `load-dms-seed-data.ps1` directly for seed loading after `prepare-dms-schema.ps1` and `prepare-dms-claims.ps1`, passing `-DmsBaseUrl` for an IDE-hosted endpoint, `-IdentityProvider` when the running environment uses a non-default provider, and `-AdditionalNamespacePrefix` when custom seed data needs additional vendor namespace authorization, or use `bootstrap-local-dms.ps1 -LoadSeedData [-SeedTemplate <name>]` which orchestrates the phase commands including seed loading |
| 4 | Persisted instance-ID hand-off via `.bootstrap/run-context.json` | `configure-local-dms-instance.ps1` wrote selected instance IDs to `.bootstrap/run-context.json`; downstream phases read that file to resolve their target set | Instance IDs are emitted in a structured `configure-local-dms-instance.ps1` result and **forwarded in-memory** within the same wrapper invocation. Separate phase-command invocations use explicit `-InstanceId <long[]>` or `-SchoolYear <int[]>` selectors with CMS-backed lookup; no disk artifact is written | Remove any scripts that read or depend on `.bootstrap/run-context.json`; use explicit `-InstanceId` or `-SchoolYear` selectors when invoking `provision-dms-schema.ps1` or `load-dms-seed-data.ps1` independently |
| 5 | Wrapper explicit instance targeting | `bootstrap-local-dms.ps1 -InstanceId` could target downstream provision/seed phases while the configure phase still created or selected a different target set | The wrapper no longer exposes `-InstanceId`; it always provisions and optionally seeds the instance set from the structured `configure-local-dms-instance.ps1` result in the same invocation. Explicit `-InstanceId` targeting is **phase-command-only**. | Use `provision-dms-schema.ps1 -InstanceId ...` or `load-dms-seed-data.ps1 -InstanceId ...` directly for explicit ID targeting; use the wrapper only when its configure phase owns target selection for the run |
