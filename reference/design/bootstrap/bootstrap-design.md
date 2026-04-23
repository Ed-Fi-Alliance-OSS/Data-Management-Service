# DMS-916: Bootstrap DMS Design — Developer Environment Initialization

## Table of Contents

- [1. Introduction](#1-introduction)
  - [1.1 Bootstrap Contract Change (DMS-916 Review Delta)](#11-bootstrap-contract-change-dms-916-review-delta)
- [2. ODS InitDev Audit (Informational Reference)](#2-ods-initdev-audit-informational-reference)
- [3. ApiSchema.json Selection](#3-apischemajson-selection)
  - [3.5 Compatibility Validation](#35-compatibility-validation)
- [4. Security Database Configuration from ApiSchema.json](#4-security-database-configuration-from-apischemajson)
- [5. Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence)
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
commands documented in `reference/design/bootstrap/command-boundaries.md`. Each phase command owns exactly
one primary concern; no single script is the source of all lifecycle semantics. Any thin wrapper over those
commands is a convenience entry point for the happy path only and is not the source of lifecycle semantics,
schema policy, claims logic, provisioning behavior, or any other phase-specific concern. The canonical
bootstrap always includes the Config Service because instance discovery, claimset seeding, and credential
bootstrap depend on it. The legacy `-EnableConfig` switch remains on the infrastructure-startup phase for
backward compatibility but is not a normative opt-out on the phase-oriented contract. Subsequent sections
address each design concern in turn.

**Scope guardrail**: DMS-916 is a developer-bootstrap design spike, not a general orchestration platform.
Bootstrap-integrated SDK generation, smoke-test runners, broader post-bootstrap automation, and published
compose/script parity outside the local developer workflow are future ideas, not part of the story-aligned
design surface.

**Story-complete boundary**: This spike is complete when it designs only the local developer-bootstrap
outcomes required by the story: schema selection, security composition, schema provisioning, API-based
seeding, extension selection, seed-loader credentials, a single bootstrap entry point with safe skip
behavior, IDE debugging against Docker-managed infrastructure, backend-redesign awareness, and the ODS audit
as informational reference. It does not add post-bootstrap runners, a persisted bootstrap control plane,
broader tenant/credential strategies, or a second non-Docker bootstrap surface.

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
2. The wrapper, if implemented, is sequencing convenience only. It forwards `-ConfigFile` unchanged and
   must not own schema, claims, provisioning, credential, retry, or continuation policy.
3. `ApiSchema.json` selection is the source of truth for API surface, security pairing in standard mode,
   and the exact DDL target for the run.
4. Standard mode means `-Extensions`, including the omitted-`-Extensions` core-only case. Expert mode
   means `-ApiSchemaPath`; expert mode requires explicit `-ClaimsDirectoryPath` when non-core schemas are
   staged.
5. DDL provisioning and seed loading are separate phases. DMS startup is never the authoritative schema
   deployment path.
6. Omitting `-Extensions` means core only. DMS-916 does not preserve the legacy TPDM-included default.
7. "Skip/resume" in this story means safe per-phase invocation plus optional same-invocation continuation
   through `-InfraOnly -DmsBaseUrl`; it does not mean a persisted cross-invocation resume model.
8. This is a developer-bootstrap design spike. Production provisioning, versioned seed distribution, and
   broader orchestration concerns remain out of scope.

**Terminology**:

- **Standard mode**: schema selection through `-Extensions`, including the omitted-`-Extensions`
  core-only case.
- **Expert mode**: schema selection through `-ApiSchemaPath`.
- **Thin wrapper**: `bootstrap-local-dms.ps1` as optional sequencing convenience only.
- **Same-invocation continuation**: `-InfraOnly -DmsBaseUrl` continuing the current run against an
  IDE-hosted DMS process.
- **Exact physical schema**: the concrete relational footprint implied by the selected staged schema set;
  this is the DDL target for the run.

**Recommended developer paths**:

- **Core only**: omit `-Extensions`; stage only the core schema, embedded claims, and core seed sources.
- **Core plus supported extension**: use `-Extensions <name>`; bootstrap stages the mapped schema and
  claims automatically and loads built-in seed data only when that mapping row defines it.
- **Custom schema**: use `-ApiSchemaPath`; pair it with `-ClaimsDirectoryPath` for non-core resources and
  `-SeedDataPath` when loading data.
- **IDE workflow**: use `-InfraOnly` for pre-DMS preparation, or `-InfraOnly -DmsBaseUrl` for
  same-invocation continuation once the IDE-hosted DMS process is healthy.

### 1.1 Bootstrap Contract Change (DMS-916 Review Delta)

The DMS-916 story originally called for a single normative entry point as the bootstrap control plane:

> **Replaced contract:** `eng/docker-compose/start-local-dms.ps1` is the single normative bootstrap
> control plane and single source of lifecycle semantics. All bootstrap phases — infrastructure startup,
> schema selection, security configuration, schema provisioning, instance creation, credential
> bootstrapping, and seed data loading — are owned by or orchestrated directly by this script.

Design review determined that combining all phases behind one monolithic command surface creates an
untestable, hard-to-maintain `initdev` clone. The complete review reasoning is in
[`.review/feedback_PR.md`](../../../../.review/feedback_PR.md).

The DMS-916 design adopts the following replacement instead:

> **New normative contract:** Bootstrap is a composable set of phase-oriented commands, each owning
> exactly one primary concern. No single script is the source of all lifecycle semantics. Any thin
> wrapper over those commands is a convenience entry point for the happy path only — not the source of
> schema policy, claims logic, provisioning behavior, or any other phase-specific concern.

The six phase commands are:

| Command | Primary concern |
|---|---|
| `start-local-dms.ps1` | Docker stack management and service health waiting |
| `prepare-dms-schema.ps1` | Schema resolution and staging |
| `prepare-dms-claims.ps1` | Security (claims) staging |
| `configure-local-dms-instance.ps1` | DMS instance and CMS client setup |
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
| Phase 6: Database Provisioning | Drop/create/migrate Admin, Security, ODS databases per engine and install type | Covered by the DMS-916 design for Docker-managed PostgreSQL: `postgresql.yml` starts the engine, `Add-DmsInstance` / `Add-DmsSchoolYearInstances` in `Dms-Management.psm1` identify the target instances, and the explicit step-8 SchemaTools/runtime-owned provisioning and validation path performs the authoritative pre-start schema work for those targets. See [Backend Redesign Impact and DDL Provisioning](#11-backend-redesign-impact-and-ddl-provisioning). |
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
| 8 | Install DbDeploy tool | `Install-DbDeploy` | always | **Covered (implicitly)** | DMS-916 does not introduce a host-side database deployment tool install step. The authoritative provisioning path is the explicit step-8 SchemaTools/runtime-owned provisioning and validation phase that runs before DMS serves requests; it is not owned by DMS startup. |
| 9 | Reset test Admin database | `Reset-TestAdminDatabase` | always | **Obsolete (as standalone step)** | DMS has no separate Admin database. The Config Service database (`edfi_config`) is created and migrated automatically when the `local-config.yml` container starts |
| 10 | Reset test Security database | `Reset-TestSecurityDatabase` | always | **Obsolete (as standalone step)** | DMS has no separate Security database. Authorization metadata (claimsets) is seeded into the Config Service database via `AddExtensionSecurityMetadata` environment-variable path |
| 11 | Reset populated template database | `Reset-TestPopulatedTemplateDatabase` | `!NoDeploy` | **Gap** | DMS has no populated template database concept. Seed data loading replaces this. The `-LoadSeedData` flag currently runs `setup-database-template.psm1` (direct SQL). See [API-Based Seed Data Loading](#6-api-based-seed-data-loading) for proposed API-based replacement |
| 12 | Full database deployment | `Initialize-DeploymentEnvironment` | `!NoDeploy` | **Covered by DMS-916 design, pending implementation** | `Add-DmsInstance` and `Add-DmsSchoolYearInstances` in `Dms-Management.psm1` identify the target instances, and the DMS-916 design then requires the explicit step-8 SchemaTools/runtime-owned provisioning and validation path before DMS startup. The remaining work here is implementation delivery of that designed path, not a missing design contract. See [Backend Redesign Impact and DDL Provisioning](#11-backend-redesign-impact-and-ddl-provisioning) and Story 01. |
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

> Note: Steps 9 and 10 are classified as Obsolete because they refer to ODS-specific Admin/Security database concepts that do not have a direct one-to-one equivalent in DMS. Step 12 is not a remaining DMS-916 design gap: the authoritative resolution is the explicit step-8 schema-provisioning contract in [Section 11](#11-backend-redesign-impact-and-ddl-provisioning) and companion Story 01. The remaining work is implementation delivery of that already-defined path. Steps 14-17 remain outside DMS-916 scope even though they are valid optional ODS phases.

> Note: The `-SearchEngine` parameter (which controlled OpenSearch inclusion in the Docker stack) has already been removed from `start-local-dms.ps1` as part of the backend redesign. Any remaining documentation cleanup or retirement of the associated Docker Compose fragments (`opensearch.yml`, `opensearch-dashboards.yml`) is follow-up cleanup outside the four DMS-916 bootstrap slices.

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
> physical schema footprint for the run. If the selected schema set changes from core-only to core plus a
> supported extension such as Sample, the DDL to provision changes accordingly. A database provisioned for a
> different schema selection is
> incompatible existing state, not a silently reusable superset. See
> [Section 11.5](#115-selected-apischemajson-drives-exact-physical-ddl-shape) for the authoritative treatment
> of the DDL/schema relationship.

### 3.3 Proposed Selection Mechanism

`-Extensions` is the primary schema-selection input, owned by `prepare-dms-schema.ps1` (see
[`command-boundaries.md` Section 3.1](command-boundaries.md#31-prepare-dms-schemaps1--schema-selection-and-staging)).
It is typed as `String[]` and accepts one or more extension names using normal PowerShell array binding.
The phase command does not rely on comma-splitting a single string; multi-extension examples therefore use
PowerShell array syntax such as `-Extensions "sample","homograph"`.

Bootstrap owns schema materialization. Before any DMS host starts, it stages the selected schema files into
the stable repo-local workspace `eng/docker-compose/.bootstrap/ApiSchema/`. The exact staged files in that
directory are the only schema files used for:

- bootstrap-time `dms-schema hash` calculation,
- Docker-hosted DMS startup,
- IDE-hosted DMS startup.

On same-checkout reruns, bootstrap treats this staged schema workspace as immutable while a running DMS
process or already-provisioned database may still depend on it. If the intended staged schema set matches
the existing workspace exactly, bootstrap reuses it as-is. If it differs, bootstrap fails fast and requires
`start-local-dms.ps1 -d -v` or equivalent environment reset rather than rewriting live schema files in
place.

Package names, package versions, and developer-supplied source directories are only selection inputs. They
are not themselves the hashed artifact.

Bootstrap also keeps an in-memory staged-schema manifest for the current run:

- exactly one staged file must have `projectSchema.isExtensionProject=false`; that file is the core schema
  passed to `dms-schema hash`,
- zero or more staged files may have `projectSchema.isExtensionProject=true`; those files are passed as
  extensions,
- expert-mode `-ApiSchemaPath` fails fast if the staged directory does not normalize to that shape.

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

Three usage modes are supported:

#### Standard Development Flows

Modes 1 and 2 cover the vast majority of development workflows. Use these for day-to-day environment setup.

**Mode 1 - Default (core Ed-Fi only)**

Omit `-Extensions` entirely. Bootstrap resolves only the standard core Data Standard schema package
(e.g. `EdFi.DataStandard52.ApiSchema`; exact package name to be confirmed before Story 01) host-side with
`EdFi.DataManagementService.ApiSchemaDownloader`, stages the extracted `ApiSchema.json` into
`eng/docker-compose/.bootstrap/ApiSchema/`, and computes the expected `EffectiveSchemaHash` from that
staged file. Until a dedicated downloader README exists, the authoritative downloader CLI contract for this
host-side materialization path is the option set implemented in
`src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/CommandLineOverrides.cs` and
`src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/Program.cs`; bootstrap consumes that existing
surface rather than defining a second downloader contract in this design.

This is an intentional migration from the current `SCHEMA_PACKAGES`-driven local default documented under
`eng/docker-compose`, which stages Data Standard 5.2 plus TPDM today. DMS-916 v1 narrows the normative
developer bootstrap profile to core only when `-Extensions` is omitted. TPDM is outside the initial
`-Extensions` support surface for this ticket rather than an implied default that bootstrap must preserve.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1
```

**Mode 2 - Core + named extensions**

Pass one or more well-known extension names. The bootstrap script resolves the core package plus the
corresponding extension packages host-side with `EdFi.DataManagementService.ApiSchemaDownloader`, then
stages the resulting `ApiSchema.json` files into the same workspace before computing the expected
`EffectiveSchemaHash`.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample"
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample","homograph"
```

Supported extension names for v1 (closed set; future additions require an explicit mapping change
in a follow-on ticket):

| Name | NuGet Package |
|------|--------------|
| `sample` | `EdFi.Sample.ApiSchema` |
| `homograph` | `EdFi.Homograph.ApiSchema` |

An unrecognized extension name causes an immediate error with a clear message listing valid names. This
validation runs before any Docker operations start. TPDM and any other extension not present in this table
are intentionally outside the v1 `-Extensions` surface and are not silently substituted by `sample`,
`homograph`, or any default.

#### Advanced / Expert Usage - Custom Local Schema Path

> **This mode is for expert users only.** Standard development workflows should use Modes 1 and 2 above.
> Mode 3 disables automatic claimset and seed data selection; use the expert-only companion parameters
> described below to provide those inputs explicitly.

**Mode 3 - Custom local path (Expert / Manual)**

> **WARNING:** `-ApiSchemaPath` is an expert escape hatch. Automatic extension-derived claimset and seed
> selection are disabled when this mode is used. Pair it with `-ClaimsDirectoryPath` and, if loading data,
> `-SeedDataPath`. Prefer Modes 1 and 2 for standard development workflows.

Pass a local filesystem path to a directory containing one or more pre-built `ApiSchema.json` files.
Bootstrap stages every `ApiSchema*.json` file from that directory into the same repo-local workspace used by
Modes 1 and 2, validates that exactly one staged file is a non-extension project, and computes the
expected `EffectiveSchemaHash` from the staged copies. Automatic extension-derived claimset and seed
selection are still disabled in this mode.

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1 -ApiSchemaPath "C:\dev\my-custom-schema"
```

`-Extensions` and `-ApiSchemaPath` are mutually exclusive. If both are specified, the bootstrap script exits
with a clear error. When `-ApiSchemaPath` is used, no automatic claimset or seed selection occurs. The
developer supplies those inputs explicitly via `-ClaimsDirectoryPath` and/or `-SeedDataPath`.

**Fail-fast expert-mode contract:** `-ApiSchemaPath` is intentionally not a partial alias for standard-mode
defaults.

- If the staged custom schema set contains one or more extension schemas, bootstrap requires
  `-ClaimsDirectoryPath` and fails before container startup when it is missing. Core-only custom-schema runs
  may continue without it because embedded claims already cover the core resource set.
- If `-LoadSeedData` is used with `-ApiSchemaPath`, bootstrap requires `-SeedDataPath` and fails during
  parameter validation when it is missing. The built-in `Minimal` or `Populated` seed-template defaults do
  not apply in expert custom-schema mode.
- `-SeedTemplate` remains part of the standard bootstrap-managed seed contract only. When `-ApiSchemaPath` is
  supplied, passing `-SeedTemplate` is an error because expert mode disables bootstrap-managed seed-source
  selection.

**Bootstrap-time warning:** When `-ApiSchemaPath` is used, the bootstrap script emits a warning before
proceeding. The warning communicates that automatic extension-derived claimset and seed data selection are
disabled and that the caller must provide `-ClaimsDirectoryPath` and, if loading data, `-SeedDataPath`
explicitly. The exact warning text is an implementation detail and is not prescribed here.

**Expert companion parameters for a complete custom bootstrap:** To avoid editing environment variables or
Docker mounts by hand, Mode 3 introduces two expert-only companion parameters alongside `-ApiSchemaPath`:

- `-ApiSchemaPath` - supplies the custom schema directory (normalized into the staged schema workspace)
- `-ClaimsDirectoryPath` - supplies a directory of `*-claimset.json` files used for CMS hybrid-mode
  loading. This is the first-class way to provide security metadata for a custom schema set, and the same
  parameter can also be used additively with `-Extensions` in standard mode.
- `-SeedDataPath` - supplies custom JSONL seed files; without this, no seed data is loaded

### 3.4 How Selection Flows Through the System

The following sequence describes the canonical schema flow for all bootstrap modes:

```text
prepare-dms-schema.ps1
  -> Resolve schema inputs from -Extensions or -ApiSchemaPath
     -> Mode 1/2: download package-backed ApiSchema.json files on the host into
        eng/docker-compose/.bootstrap/ApiSchema/
     -> Mode 3: copy/normalize developer-supplied ApiSchema*.json files into the same workspace
  -> Build the staged-schema manifest (1 core schema + 0..N extension schemas)
  -> Run `dms-schema hash <coreSchemaPath> <extensionSchemaPath...>` over the staged files
  -> Docker-hosted DMS:
     -> bind-mount eng/docker-compose/.bootstrap/ApiSchema/ to /app/ApiSchema
     -> set AppSettings__UseApiSchemaPath=true
     -> set AppSettings__ApiSchemaPath=/app/ApiSchema
     -> leave SCHEMA_PACKAGES empty so run.sh does not perform a second download
  -> IDE-hosted DMS:
     -> set AppSettings__UseApiSchemaPath=true
     -> set AppSettings__ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema
  -> DMS loads the staged files from the filesystem
```

> **Note:** This diagram shows only the schema delivery flow. Security staging happens before the Config
> Service starts, and DDL deployment mode is decided after instance creation but before the DMS process
> starts. See [Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence) for the full ordering.

This reuses the existing host-side pattern already present in `eng/preflight-dms-schema-compile.ps1`: the
host materializes concrete schema files first, then downstream steps operate on those files. Later bootstrap
steps use the staged-schema manifest from the current run rather than re-resolving packages or inferring
schema shape from container-only environment variables.

**NuGet feed failure handling:** If the configured NuGet feed is unreachable during host-side schema package
resolution (step 2) or seed package download (step 11), the bootstrap script fails fast with a clear error
message including the feed URL and HTTP status. No partial downloads are attempted. For offline or
air-gapped environments, use `-ApiSchemaPath` (Mode 3) and, if loading data, `-SeedDataPath` to bypass
NuGet entirely and supply pre-downloaded artifacts from a local directory. If that staged custom schema set
includes non-core resources, the same air-gapped run must also provide `-ClaimsDirectoryPath`; expert mode
does not infer or synthesize non-core security metadata from the schema files.

### 3.5 Compatibility Validation

For each extension in `-Extensions`, the bootstrap resolves two required artifacts and, when the mapping
defines one, an optional built-in seed data package:

1. **Schema package** (e.g., `EdFi.Sample.ApiSchema`) - determines API surface
2. **Security fragment** (e.g., `004-sample-extension-claimset.json`) - determines authorization rules
3. **Optional built-in seed data package** (when present in the mapping) - determines bootstrap-managed
   sample data

**Version contract**: Schema and built-in seed NuGet packages for a given extension must align to the same
Data Standard version when the mapping includes both artifacts. Security fragments (e.g.,
`004-sample-extension-claimset.json`) are repo-bundled files, not NuGet packages, and do not carry NuGet
version metadata; their version alignment is guaranteed by the extension mapping that pins schema package,
optional seed package, and security fragment together in one entry. The bootstrap script validates NuGet
artifact versions during step 1 (parameter validation) by checking that the version fields in the
schema-package and seed-package NuGet descriptors match for each extension whose mapping defines both
packages.

**Validation rules**:

- When `-Extensions` is used (Mode 1/2): The script resolves version numbers for each artifact type from the
  well-known extension mapping. If versions are pinned in the mapping (recommended), they are guaranteed
  consistent. If the schema package or security fragment for a requested extension cannot be resolved,
  bootstrap errors before starting containers. Seed package requirements when `-LoadSeedData` is set are
  governed by the seed-specific rule below.
- When `-ApiSchemaPath` is used (Mode 3): Schema hashing and DDL validation still work because bootstrap
  stages and hashes the exact files. What is disabled is automatic security-fragment and seed-package
  selection. Bootstrap emits a warning indicating that compatibility with claimsets and seed data is only
  validated against the explicit companion inputs for this run. The exact warning text is an implementation detail.
- When `-ApiSchemaPath` is used (Mode 3): The staged directory must normalize to exactly one
  `projectSchema.isExtensionProject=false` file and zero or more `true` files. Any other shape is a
  bootstrap-time error.
- When `-LoadSeedData` is set: For each extension in `-Extensions`, check the extension mapping for a
  built-in seed package entry.
  - If the extension has a mapping entry and its NuGet seed package fails to resolve (network/feed
    error): error before starting containers.
  - If the extension has no seed package entry in the mapping: informational warning only —
    Bootstrap emits an informational warning indicating no built-in seed package is available for
    that extension; schema and security configuration from the extension still apply and the
    developer may provide custom seed data via `-SeedDataPath`.

**Ownership**: The bootstrap script owns pre-flight validation. DMS runtime does not validate artifact
compatibility - it loads whatever staged schema files it receives. This makes pre-flight checks the only
safety net.

### 3.6 Versioning

NuGet package versions for known extensions are maintained as defaults inside the bootstrap script. For the initial implementation, a single version applies to all packages in a given bootstrap invocation, matching the current `.env.example` pattern. A future `-SchemaVersion` parameter (or per-extension version map) could override the default version when needed; this is not included in the initial parameter set (Section 9.3) and is deferred to a follow-up enhancement.

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
enables hybrid mode via the `-AddExtensionSecurityMetadata` switch, which exports
`DMS_CONFIG_CLAIMS_SOURCE=Hybrid` and `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims`.

**Gap**: The current mechanism is all-or-nothing. When `-AddExtensionSecurityMetadata` is set, every
claimset fragment in the directory is loaded regardless of which extensions the developer has actually
selected. There is no filtering by extension. A developer bootstrapping with `-Extensions sample` still
loads Homograph claimsets, and vice versa.

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

> **Scope of automatic derivation (precise).** "Automatic" here means standard `-Extensions` mode only,
> including the omitted-`-Extensions` core-only case. In that mode the bootstrap script pairs the
> selected schema set with its matching security fragments from the v1 extension mapping below. Expert
> `-ApiSchemaPath` mode does **not** derive security automatically: when the staged schema set includes
> non-core schemas the caller must supply explicit `-ClaimsDirectoryPath` input, and `prepare-dms-claims.ps1`
> fails fast if it is missing (see [`command-boundaries.md` Section 3.2](command-boundaries.md#32-prepare-dms-claimsps1--claims-and-security-staging)). This section
> describes the standard-mode pairing; nothing in it promises universal claimset loading from schema alone
> across every supported mode.

When the `-Extensions` parameter (Section 3.3) drives schema selection, the bootstrap script derives the
corresponding security configuration automatically in standard mode only. For expert mode, the design also
provides an explicit `-ClaimsDirectoryPath` parameter so custom schema bootstraps can supply security
fragments without editing environment variables or Docker mounts manually. The bootstrap script maintains
one extension mapping that carries schema, security, namespace, and built-in seed support together. In
DMS-916 v1, a row advertises built-in seed support only when it names a concrete built-in seed package.
Rows that say "Not defined in DMS-916 v1" do not participate in automatic built-in seed loading and must
use custom `-SeedDataPath` payloads instead:

| Extension name | ApiSchema NuGet package | Security fragment file(s) | Built-in seed package (only when explicitly defined) | Namespace prefix |
|----------------|------------------------|---------------------------|----------------------------------------|------------------|
| `sample` | `EdFi.Sample.ApiSchema` | `004-sample-extension-claimset.json` | Not defined in DMS-916 v1 | `uri://sample.ed-fi.org` |
| `homograph` | `EdFi.Homograph.ApiSchema` | `005-homograph-extension-claimset.json` | Not defined in DMS-916 v1 | `uri://homograph.ed-fi.org` |

Only extensions backed by current schema and security artifacts belong in the DMS-916 v1 mapping and
validation surface. Deferred extensions stay out of the supported-extension list until their artifacts
exist and are intentionally brought into scope.

The bootstrap logic is:

1. **Core security metadata is always loaded.** The embedded `Claims.json` (Embedded mode) covers all
   core Ed-Fi Data Standard resources. No extra configuration is needed for Mode 1 (core only).

2. **Extension security metadata is loaded based on `-Extensions` selection.** If `-Extensions` is
   non-empty, the bootstrap script switches CMS to `Hybrid` mode and stages only the fragment files that
   correspond to the selected extensions. This avoids requiring CMS-side filtering changes and keeps
   selection logic entirely in the bootstrap script. For DMS-916, a bootstrap-managed extension fragment
   must attach permissions to the embedded claim sets required by the developer workflow:
   `EdFiSandbox` for general developer access and `SeedLoader` when built-in extension seed support exists.

   **Contract:** If any selected extension maps to a missing fragment file, bootstrap fails before container
   startup with a clear error listing the missing file(s). If `-LoadSeedData` is used and the selected
   extension fragment does not attach `SeedLoader` permissions for the extension resources emitted by that
   extension's built-in seed package, bootstrap also fails before container startup.

   **Compose wiring contract:** CMS continues to read fragments from `/app/additional-claims`, and
   `DMS_CONFIG_CLAIMS_DIRECTORY` remains the container-side path setting for that location. The host-side
   source for the bind mount is controlled separately by a new
   `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` variable consumed by `local-config.yml`. Its default value preserves
   the current static source path
   `../../src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets`, and bootstrap
   overrides it to the staged workspace `eng/docker-compose/.bootstrap/claims` for runs that stage selected
   or additive claim fragments. The existing `/app/additional-claims` mount in `local-dms.yml` is not part
   of the CMS-loading contract and remains cleanup-neutral compatibility wiring rather than a DMS-916
   behavior change. Any equivalent published-surface wiring is follow-on work outside this developer-only
   story.

3. **Additional claimset fragments can be layered in through `-ClaimsDirectoryPath`.** When a developer
    uses `-ApiSchemaPath` or otherwise needs custom claimset fragments, `-ClaimsDirectoryPath` points to a
    local directory containing one or more `*-claimset.json` files. Bootstrap validates that the path
    exists, then stages those files into the same claims workspace used for extension-derived fragments.
    This makes `-ClaimsDirectoryPath` additive rather than mutually exclusive with `-Extensions`.

   **Contract:** If `-ClaimsDirectoryPath` is supplied and the directory does not exist or contains no
   `*-claimset.json` files, bootstrap fails before container startup with a clear error. Bootstrap also
    parses the embedded base `Claims.json` and fails if any staged fragment references a claim set name that
    does not already exist there.

    **Expert-mode completeness boundary:** In `-ApiSchemaPath` mode, `-ClaimsDirectoryPath` is the
    explicit companion input for non-core security metadata, but bootstrap validates only staged-fragment
    presence and structure. It does not attempt to prove that arbitrary expert-supplied fragments fully
    authorize every staged non-core resource ahead of runtime. If the custom schema and custom claims are
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

5. **`-AddExtensionSecurityMetadata` becomes redundant when `-Extensions` or `-ClaimsDirectoryPath` is
   used.** As noted in Section 8, the existing flag is retained for backward compatibility but no longer
   needs to be specified when the new developer-facing inputs drive the selection. If
   `-AddExtensionSecurityMetadata` is passed without either of those inputs, behavior is unchanged from
   today (all fragments loaded).

### 4.4 Security Configuration in the Bootstrap Sequence

Security metadata loading is a CMS startup concern: CMS reads and seeds claimsets when the container
starts. Current CMS startup behavior only performs the initial claims load when the claim tables are empty;
it does not replace an already populated claims document during a normal bootstrap rerun. DMS-916 therefore
treats changed security inputs as incompatible existing state rather than a hot-reload scenario. Same-checkout
reruns without teardown are supported only when the intended security inputs match the already-staged claims
workspace. The bootstrap sequence therefore places security configuration as follows:

```text
... (identity provider and PostgreSQL already running)

5. Prepare security environment from ApiSchema.json
    - Derive DMS_CONFIG_CLAIMS_SOURCE from whether any fragments were staged
      (Hybrid if yes, Embedded otherwise)
    - Build the intended staged claims set from extension-specific and/or
      developer-supplied fragment files
    - Validate staged fragments before CMS startup (structural checks only):
      - every referenced claim set name already exists in embedded Claims.json
      - no duplicate filenames exist in the staged workspace
    - Compare the intended staged claims set to any existing
      eng/docker-compose/.bootstrap/claims workspace
    - If no workspace exists yet: materialize the staged claims workspace for the run
    - If an identical workspace already exists: reuse it as-is and do not rewrite a
      directory that may still be bind-mounted into CMS
    - If the intended staged claims set differs from the existing workspace: fail fast
      with teardown guidance because DMS-916 has no in-place claims-replacement path for
      a populated Config Service database
    - Keep `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims`
    - When fragments were staged, set `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` to the staged host workspace
    - This is not a separate Docker step; it prepares env vars and mounts consumed by CMS startup

6. Start Config Service                                    [Existing/Extended]
    - CMS reads `DMS_CONFIG_CLAIMS_SOURCE` and `DMS_CONFIG_CLAIMS_DIRECTORY` on startup and receives the
      staged host files through the `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` bind mount when Hybrid mode is active
    - On first startup against empty claims tables, CMS seeds core + selected extension claim metadata
      into edfi_config
    - Same-checkout reruns are supported only when step 5 reused the identical claims workspace; bootstrap
      does not attempt claims hot reload or claim-document replacement through management endpoints
    - Wait for CMS readiness; for DMS-916 this means the service is healthy and startup claim loading for
      the selected staged inputs has completed

7. Create DMS instances                                    [Existing]
    - Instance definitions and connection strings are written to CMS
    - These instances become the target set for the later schema-provisioning step

8. Provision or validate database schema                   [Proposed]
   - Applies or validates the selected schema target before DMS starts

9. Start DMS (or wait for IDE-hosted DMS)                  [Existing/Extended]
    - DMS loads ApiSchema from the staged filesystem path prepared in step 2
    - Step 8 has already provisioned or validated the target databases before DMS starts
    - Bootstrap waits only for DMS availability at this point; schema work is not deferred to host startup

11. Seed data loading                                      [Existing/Proposed]
    - BulkLoadClient authenticates and POSTs resources
    - CMS authorization checks pass because claimsets match loaded schema
```

Security metadata must be in place (step 6 - CMS container start) before seed data loading (step 11)
because every API request from BulkLoadClient is authorized against the claimsets seeded during CMS
startup. A missing claimset for an extension resource causes 403 responses that abort the seed load.

Because the current CMS startup path only initial-loads claims into empty tables, changing the selected
security inputs against a populated `edfi_config` database is outside the supported rerun surface for
DMS-916. Bootstrap requires teardown rather than trying to replace claims in place.

The order dependency is:

1. Schema selection (Section 3, step 2) - determines which resources exist.
2. Security metadata derivation (this section, step 5) - determines which claimsets CMS will load.
3. Config Service startup (step 6) - seeds claimsets into `edfi_config`.
4. DMS instance creation (step 7) - defines which databases bootstrap must provision or validate.
5. Optional smoke-test credential bootstrap (Section 7, step 7a) - uses CMS only to create
   `EdFiSandbox` credentials for the step-7-selected target instances without re-querying CMS.
6. Direct schema provisioning (step 8) - applies or validates the selected schema target before DMS starts.
7. Seed-loader credential bootstrap (Section 7, step 10) - prepares `SeedLoader` credentials for
   DMS-dependent API seed delivery.
8. Seed data loading (Section 6, step 11) - uses authenticated BulkLoadClient POST requests.

For expert-mode bootstraps, step 5 accepts `-ClaimsDirectoryPath` as a first-class companion to schema
selection. Whether fragments come from `-Extensions`, `-ClaimsDirectoryPath`, or both, bootstrap stages
the combined validated set into one workspace directory and points
`DMS_CONFIG_CLAIMS_HOST_DIRECTORY` at that host directory while leaving
`DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims`. Those fragments may extend resource-claim nodes, but
they may only attach actions to claim set names that already exist in embedded `Claims.json`. Bootstrap
rejects unknown claim set names before CMS startup.

### 4.5 Claims-Loading Approach: Safe Path vs. Legacy Flag

The bootstrap design uses `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` with
`DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims` and
`DMS_CONFIG_CLAIMS_HOST_DIRECTORY=<repo-root>/eng/docker-compose/.bootstrap/claims` as the correct
mechanism for loading extension or expert-supplied claimsets. This is the intended path for both the
proposed `-Extensions` bootstrap flow and the expert `-ClaimsDirectoryPath` flow.

**`DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING`** is a separate, legacy flag used for dynamic management endpoints (where the Config Service API is called post-boot to push arbitrary claimset data) and for legacy dev-only workflows. It is **not** required by the proposed `-Extensions` bootstrap path and is **not** part of the standard bootstrap flow described in this design.

All three schema-selection modes are explicitly designed to operate without this flag:

- **Mode 1 (core only)**: `DMS_CONFIG_CLAIMS_SOURCE=Embedded` — no extra flag needed.
- **Mode 2 (named extensions)**: `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` with
  `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims` and
  `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` pointing at the staged claims workspace containing only the selected
  claimset files - no extra flag needed.
- **Mode 3 (expert `-ApiSchemaPath`)**: claims loading is **not** automatic. When the staged schema set
  contains any non-core schema, the caller must supply `-ClaimsDirectoryPath`; `prepare-dms-claims.ps1`
  fails fast otherwise (see [`command-boundaries.md` Section 3.2](command-boundaries.md#32-prepare-dms-claimsps1--claims-and-security-staging)). When fragments are
  supplied, the same `Hybrid` + `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims` +
  `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` mechanism is used; core-only `-ApiSchemaPath` runs may stay on
  `Embedded`. The dangerously-enable flag is **not** required in expert mode either.
- `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` is **not set** in any of the three modes above.

The deprecation path for `-AddExtensionSecurityMetadata` explicitly includes removal of `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` from standard bootstrap usage as the `-Extensions` mechanism supersedes it. That cleanup belongs to the schema-and-security-selection slice rather than a separate bootstrap story.

## 5. Proposed Bootstrap Sequence

The bootstrap sequence is organized around the composable phase commands defined in
[`command-boundaries.md`](command-boundaries.md). Each phase owns exactly one primary concern;
no single script is the source of all lifecycle semantics. The consolidated flow below is derived from
[ApiSchema.json Selection](#3-apischemajson-selection),
[Security Database Configuration from ApiSchema.json](#4-security-database-configuration-from-apischemajson),
[API-Based Seed Data Loading](#6-api-based-seed-data-loading), and
[IDE Debugging Workflow](#12-ide-debugging-workflow). Optional SDK generation and post-bootstrap test
orchestration remain outside this story and are not part of the normative bootstrap flow.

**Phase dependency chain:**

```text
prepare-dms-schema.ps1
  └─▶ prepare-dms-claims.ps1
        └─▶ start-local-dms.ps1 -InfraOnly  (PostgreSQL, identity provider, Config Service)
              └─▶ configure-local-dms-instance.ps1  (instance and client setup)
                    └─▶ provision-dms-schema.ps1  (authoritative schema provisioning)
                          └─▶ start-local-dms.ps1  (DMS container, or IDE-hosted DMS starts here)
                                └─▶ load-dms-seed-data.ps1  (live DMS + SeedLoader credentials)
```

The detailed step sequence below maps to those phases and preserves the behavioral invariants required
by implementers.

```text
1.  Validate parameters and read environment file        [Existing]
    - env-utility.psm1 reads .env; script parameters override
    - Exit immediately on unrecognized -Extensions values
    - Exit immediately if any required extension artifact is missing
      (schema package descriptor, security fragment, seed package when -LoadSeedData is set)
    - When -LoadSeedData is set: resolve the pinned BulkLoadClient package and fail fast
      if the package cannot be resolved or the required JSONL interface is unavailable
    - When -InfraOnly is set without -DmsBaseUrl: reject -LoadSeedData up front because this
      pre-DMS-only variant does not provide a live DMS endpoint for SeedLoader credential bootstrap
      or BulkLoadClient execution

2.  Resolve ApiSchema.json selection                     [Proposed] - Section 3
    - Determine which ApiSchema inputs to use based on -Extensions and -ApiSchemaPath
    - Build the intended staged schema set for the run
    - If no staged-schema workspace exists yet: materialize the selected files into
      eng/docker-compose/.bootstrap/ApiSchema/
    - If an identical staged-schema workspace already exists: reuse it as-is
    - If the intended staged schema set differs from the existing workspace: fail fast
      with teardown guidance because running DMS processes and already-provisioned
      databases may still depend on the prior selection
    - In Mode 1/2: resolve core + selected extension packages host-side with ApiSchemaDownloader
    - In Mode 3: stage developer-supplied ApiSchema*.json files into the same workspace
    - Validate that the staged set contains exactly 1 core schema and 0..N extension schemas
    - Compute the expected EffectiveSchemaHash by running dms-schema hash over the staged files
    - Fail fast if dms-schema hash exits non-zero; bootstrap stops before any container startup and
      surfaces the tool diagnostics unchanged

3.  Start PostgreSQL                                     [Existing]
    - Wait for PostgreSQL health check

4.  Prepare identity provider                            [Existing + Proposed]
    - Keycloak: start container and wait for health before CMS startup
    - OpenIddict (self-contained): now that PostgreSQL is healthy, run setup-openiddict.ps1 -InitDb

5.  Prepare security environment for CMS startup         [Proposed] - Section 4
    - Stage extension-derived and user-supplied *-claimset.json fragments into one workspace directory
    - Derive DMS_CONFIG_CLAIMS_SOURCE from whether any staged fragments exist
    - Validate staged fragments before CMS startup (structural checks only):
      - every referenced claim set name already exists in embedded Claims.json
      - no duplicate filenames exist in the staged workspace
    - Keep `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims`
    - When staged fragments exist, set `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` to the staged workspace directory
    - This is env-var preparation for the CMS container, not direct DB mutation
    - Failure: if staging or validation fails, bootstrap aborts before containers start

6.  Start Config Service                                 [Existing + Proposed]
    - Bring up the Config Service and supporting infrastructure required before DMS startup
    - Mount the staged claims workspace when fragments were prepared in step 5
    - Wait for Config Service readiness; for DMS-916 this means the service is healthy and startup claim
      loading for the selected staged inputs has completed
    - Self-contained mode: after CMS readiness, run setup-openiddict.ps1 -InsertData to seed the
      OpenIddict data required for the self-contained identity path
    - Keycloak mode does not run setup-openiddict.ps1 -InsertData
    - Story 03 also provisions or validates the fixed dev-only CMSReadOnlyAccess client at this
      stage because IDE-hosted DMS depends on that client to query CMS in local development
    - CMS seeds core + selected extension claim metadata into edfi_config on startup
    - DMS-916 treats CMS readiness as a claims-ready gate for the selected staged inputs:
      step 7 and all later phases begin only after CMS startup claim loading has completed
    - If CMS implementation changes so HTTP health can report ready before claim loading
      completes, bootstrap must add an explicit claims-ready verification before proceeding
      to step 7

7.  Create DMS instances                                 [Existing]
    - Add-DmsInstance / Add-DmsSchoolYearInstances in Dms-Management.psm1
    - Default path: create the requested DMS instance set in CMS for this run.
    - When `-NoDmsInstance` is set, bootstrap switches to a narrow existing-instance reuse check:
      it queries CMS (`Get-DmsInstances`) for the current tenant scope (or globally when
      multi-tenancy is disabled) and proceeds only when exactly one existing instance is present.
      That single discovered instance becomes the canonical target for all subsequent applicable
      steps. Zero or multiple instances are fatal errors; bootstrap does not offer an inline
      target-selection override and instead requires teardown or manual environment preparation
      before rerun.
    - `-NoDmsInstance` is not supported with `-SchoolYearRange`; multi-year existing-instance
      reuse is intentionally out of scope for DMS-916.
    - Step 7 is the only phase allowed to resolve target DMS instance IDs for the run. All later
      phases consume the selected set from this step and never call `Get-DmsInstances` or perform
      a second CMS discovery pass.
    - This must complete before bootstrap can provision or validate the target databases in step 8

7a. Optional smoke-test credential bootstrap             [Existing + Proposed] - Section 7
    - Runs only when -AddSmokeTestCredentials is set
    - Uses the target instance IDs already selected in step 7 and does not call `Get-DmsInstances`
      or perform a second CMS discovery pass
    - Add-CmsClient
    - Get-CmsToken
    - Add-Vendor
    - Add-Application        -> $smokeKey / $smokeSecret (EdFiSandbox claim set)
    - Produces credentials for smoke tooling only; step 10 never reuses them for seed delivery
    - Completes entirely through CMS before schema provisioning, DMS health wait, or seed loading

8.  Provision or validate database schema               [Proposed] - Section 11
    - Collect the target connection strings from the instances selected in step 7
    - Invoke the authoritative SchemaTools/runtime-owned provisioning and validation path against
      each target using the staged schema files from step 2
    - Pass the expected `EffectiveSchemaHash` into that shared path as one of the staged-schema
      inputs; bootstrap does not treat it as a standalone serviceability classifier
    - Continue only when the shared provisioning path reports the target databases are ready for
      the selected schema set; otherwise fail fast and surface the underlying diagnostics
    - In the Docker flow, DMS starts in step 9 after step 8 completes
    - In the -InfraOnly flow without -DmsBaseUrl, bootstrap stops after reporting that schema
      provisioning/validation is complete and prints IDE next-step guidance
    - In the -InfraOnly flow with -DmsBaseUrl set, the same completed schema state carries into
      the external-endpoint continuation in step 9

9.  Start DMS or continue against external DMS endpoint  [Existing + Proposed]
    - Docker flow: mount eng/docker-compose/.bootstrap/ApiSchema/ to /app/ApiSchema
    - Docker flow: set AppSettings__UseApiSchemaPath=true and AppSettings__ApiSchemaPath=/app/ApiSchema
    - Docker flow: leave SCHEMA_PACKAGES empty so run.sh does not perform a second schema download
    - -InfraOnly + -DmsBaseUrl flow: developer starts DMS in the IDE at the selected URL with
      AppSettings__UseApiSchemaPath=true and AppSettings__ApiSchemaPath pointing at
      eng/docker-compose/.bootstrap/ApiSchema/
    - -InfraOnly without -DmsBaseUrl: stop here and print next-step guidance for the IDE-hosted
      DMS launch; no DMS health wait, SeedLoader credential bootstrap, or seed loading occurs in this variant
    - Docker flow: poll the containerized DMS health endpoint (configurable interval and maximum
      timeout) and fail fast on timeout or non-success health status before any DMS-dependent continuation work begins
    - -InfraOnly + -DmsBaseUrl flow: poll `$DmsBaseUrl/health` (configurable interval and maximum
      timeout) and fail fast on timeout or non-success health status before SeedLoader credential bootstrap or seed loading
    - Step 8 has already completed any required schema provisioning before DMS health checks begin

10. Seed-loader credential bootstrap                     [Existing + Proposed] - Section 7
    - Runs only when -LoadSeedData is set
    - Runs only in the Docker flow or when -InfraOnly is combined with -DmsBaseUrl
    - Uses the target instance IDs already selected in step 7 and does not call `Get-DmsInstances`
      or perform a second CMS discovery pass
    - Does not depend on step 7a; bootstrap creates a separate SeedLoader application even when
      smoke-test credentials were not requested
    - Add-CmsClient          (system admin client in CMS)
    - Get-CmsToken           (OAuth bearer token for CMS admin API)
    - Add-Vendor             (baseline seed namespaces + dynamic extension namespace prefixes)
    - Add-Application        -> $seedKey / $seedSecret   (SeedLoader claim set)
    - Seed loading later authenticates against the token endpoint selected by -IdentityProvider,
      not a hard-coded CMS-only endpoint

11. Seed data loading via API                            [Proposed] - Section 6
    - Runs only in the Docker flow or when -InfraOnly is combined with -DmsBaseUrl
    - Only when -LoadSeedData is set
    - Resolve seed package list: built-in core manifest + extension packages from -Extensions,
      unless -SeedDataPath supplies the data directory explicitly
    - Download and extract JSONL files into a repo-local seed workspace; abort on filename collisions
    - Per -SchoolYearRange: loop over each year, invoking the pinned BulkLoadClient once per year;
      derive `$tokenUrl` per iteration for the self-contained identity path; Keycloak keeps its
      provider-native static token URL; single invocation when no range
    - Invoke BulkLoadClient against the prepared seed workspace using the contracted surface defined
      in [Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract)
    - Check exit code; throw and halt on non-zero
    - Clean up the seed workspace on success; leave it in place on failure for debugging
```

**Alignment notes:**

- Step 2 must precede steps 5, 8, and 9 because those steps consume the resolved staged schema set.
- Step 3 must precede the self-contained branch of step 4 because `setup-openiddict.ps1 -InitDb` writes
  schema, extension, and key rows into PostgreSQL.
- Step 5 must precede step 6 because CMS consumes the staged claims directory on startup.
- Step 7 must precede step 8 because the target databases are discovered from the DMS instances that
  bootstrap will use for this run.
- Step 7 is the only target-resolution phase; later steps consume that selected instance set as-is
  and never re-query CMS to reinterpret the run target.
- Step 7a is a CMS-only phase anchored to the step-7-selected target set; it can create smoke-test
  credentials before any DMS endpoint exists because it does not depend on DMS health or seed loading.
- Step 8 must precede step 9 because bootstrap always invokes the authoritative
  SchemaTools/runtime-owned provisioning and validation path before any DMS process begins serving requests.
- Seed-loader credential bootstrap (step 10) must complete before seed loading (step 11) because
  BulkLoadClient authenticates using the Seed Loader Key/Secret provisioned in step 10.
- The `-InfraOnly` path ([Starting Infrastructure Without DMS](#122-starting-infrastructure-without-dms))
  always diverges at step 9. Without `-DmsBaseUrl`, bootstrap stops after step 8 with IDE next-step
  guidance, though step 7a may already have created smoke-test credentials if requested. With
  `-DmsBaseUrl`, steps 9-11 run against that external DMS endpoint.

### Failure and Recovery

The table below consolidates idempotency behavior for the rerun-sensitive bootstrap steps to guide safe
re-runs after a partial failure.

| Step | Idempotent? | Re-run behavior |
|------|-------------|-----------------|
| 2 - Resolve ApiSchema.json selection | Conditionally | Same-checkout reruns reuse the existing staged schema workspace only when the intended staged schema set is identical. If the intended set differs, bootstrap fails fast and requires teardown rather than mutating a workspace that a running DMS host or already-provisioned database may still depend on. |
| 5 - Prepare security environment | Conditionally | Same-checkout reruns reuse the existing staged claims workspace only when the intended fragment set is identical. If the intended set differs, bootstrap fails fast and requires teardown because DMS-916 does not replace populated CMS claims in place (see [Section 4.4](#44-security-configuration-in-the-bootstrap-sequence)). |
| 6 - Config Service start | Conditionally | Normal `docker compose up` idempotency is sufficient only when step 5 reused the identical staged claims workspace or when CMS is not yet running. DMS-916 does not hot-reload claims or replace the stored claims document through startup on a populated config database. |
| 7 - Instance creation | No | `Add-DmsInstance` creates new configuration records on every call. Use `-NoDmsInstance` only for narrow reruns where exactly one existing instance already exists in the current tenant scope and `-SchoolYearRange` is not set. If zero or multiple instances are present, bootstrap fails fast and requires teardown or manual environment preparation before rerun. The target set resolved in step 7 is then reused as-is by later phases. |
| 7a - Smoke-test credential bootstrap | No | `Add-Application` creates new application records on every call. In v1 this optional step re-runs whenever `-AddSmokeTestCredentials` is requested; it consumes the step-7-selected target set and does not call `Get-DmsInstances` again. Because it depends only on CMS readiness and the selected targets, it is valid during infra-only preparation. |
| 8 - Schema provisioning / validation | Yes | Each rerun invokes the same authoritative SchemaTools/runtime-owned provisioning and validation path over the staged schema set. Successful reruns either no-op or complete required provisioning before DMS starts; any state the shared path rejects fails fast with its diagnostics rather than a bootstrap-owned decision table (see [Section 11.3](#113-proposed-ddl-provisioning-hook)). |
| 9 - DMS start / availability | Yes | DMS startup is re-attemptable after step 8 completes. Schema work is already finished before this step begins. |
| 10 - Seed-loader credential bootstrap | No | `Add-Application` creates new application records on every call. In v1 this step re-runs whenever `-LoadSeedData` is requested; credentials are ephemeral and intentionally not restored from local state (see [Credential Lifecycle](#credential-lifecycle)). This flow remains independent of optional smoke-test credential creation in step 7a. |
| 11 - Seed loading | Yes | BulkLoadClient runs with `--continue-on-error`; 409 Conflict responses are treated as non-fatal, so re-loading already-present resources is safe (see [Section 6.3.1](#631-updated--loadseeddata-behavior)). |

## 6. API-Based Seed Data Loading

This section replaces the current direct-SQL seed path (`setup-database-template.psm1`) with an API-based flow that routes all seed data through the DMS REST API. The change addresses real-world data integrity issues caused by bypassing API validation (see Current Problem in the story).

### 6.0 Bootstrap Dependency Summary — BulkLoadClient

`EdFi.BulkLoadClient` is a required external dependency for API-based seed loading, but bootstrap should
consume it through the repo's existing pinned NuGet-resolution path rather than as a globally installed
machine tool. All API-based seed loading in the bootstrap (step 11 of
[Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence)) depends on it.

**Cross-team dependency:** BulkLoadClient must be extended by the ODS team to support JSONL input files
before API-based seeding can be operationally delivered (see
[Section 14.3](#143-blocking-cross-team-dependencies)). The bootstrap-side consumption contract for that
dependency is defined in [Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract) below.

**Pre-flight check (bootstrap step 1):** When `-LoadSeedData` is set, bootstrap resolves the pinned
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
  BulkLoadClient and the published seed-artifact contract rather than by DMS bootstrap.
- Bootstrap may choose to pass `--continue-on-error` for rerun tolerance, but conflict classification,
  retry behavior, batching, and request shaping remain BulkLoadClient-owned behavior.
- Exit code `0` means the run completed within the accepted bootstrap success boundary; any fatal failure
  returns non-zero.
- The tool emits terminal diagnostics for the run, and bootstrap surfaces those diagnostics directly rather
  than defining a second DMS-owned result taxonomy.

---

### 6.2 Seed Source Selection for Developer Bootstrap

#### 6.2.1 Bootstrap Consumption Boundary

DMS-916 defines only the **developer bootstrap consumption contract** for seed data. The broader artifact
distribution concerns - package naming, publishing workflow, versioning policy, and long-term seed-data
ownership - remain out of scope for this spike and stay with DMS-1119.

For DMS-916, bootstrap only requires the ability to materialize a local directory of JSONL files
for the selected developer seed source. The seed source for a run can come from:

- a repo-managed Ed-Fi-provided seed artifact selected by `-SeedTemplate`,
- extension-derived seed artifacts resolved from `-Extensions` when the selected extension mapping defines a
  built-in seed source, or
- a developer-supplied directory selected by `-SeedDataPath`.

Once materialized, every source is treated the same way: bootstrap merges the JSONL files into one repo-local
workspace and hands that directory to BulkLoadClient. The exact external artifact shape that produces those
files is intentionally not part of this design.

#### 6.2.2 Core Seed Templates

| Template | Contents | Use Case |
|----------|----------|----------|
| `Minimal` | All core descriptor resources plus `schoolYearTypes` | CI, automated tests, minimal developer environments |
| `Populated` | `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and `studentSchoolAssociations` | Full developer environments, manual smoke testing |

The `-SeedTemplate` parameter on `load-dms-seed-data.ps1` controls which core seed source is used. The
default is `Minimal` to keep bootstrap fast and lightweight. Use `-SeedTemplate Populated` when you need
realistic sample data for manual testing or demos.

These built-in manifests are the authoritative DMS-916 seed-source contract. If a future seed package adds
or removes built-in resources, the manifest table above, the core `SeedLoader` permissions in
`Claims.json`, and any extension-fragment `SeedLoader` permissions must be updated in the same change.

#### 6.2.3 Developer Sample Data Selection

Developers may use Ed-Fi provided seed sources or supply their own JSONL files. The `-SeedTemplate` and
`-SeedDataPath` parameters on `load-dms-seed-data.ps1` control this selection. The full seed-parameter
contract is defined normatively in [`command-boundaries.md`](command-boundaries.md).

**Three modes:**

| Mode | Parameter | Behavior |
|------|-----------|----------|
| Ed-Fi Minimal (default in standard modes only) | `-LoadSeedData` (no template flag) | In Modes 1 and 2 only, resolves the repo-managed `Minimal` seed source and loads all core descriptor resources plus `schoolYearTypes`. Fast bootstrap for CI and automated testing. This default does not apply when `-ApiSchemaPath` is used. |
| Ed-Fi Populated | `-SeedTemplate Populated` | In Modes 1 and 2 only, resolves the repo-managed `Populated` seed source and loads `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and `studentSchoolAssociations`. For manual testing and demos. Not valid with `-ApiSchemaPath`. |
| Custom seed data | `-SeedDataPath <directory>` | Uses JSONL files from the specified directory as the source input and copies them into the repo-local seed workspace before invocation. Bypasses bootstrap-managed artifact resolution entirely. Compatibility comes from the run's staged schema and security inputs: embedded claims, selected extension fragments, and any additive `-ClaimsDirectoryPath` fragments. Bootstrap does not inspect arbitrary JSONL files to certify that every record is authorized or schema-valid ahead of time. In expert `-ApiSchemaPath` mode, this is the only supported seed-source input when `-LoadSeedData` is requested. |

**Parameter interaction:**

- `-SeedTemplate` and `-SeedDataPath` are mutually exclusive. Providing both is a script error.
- `-ApiSchemaPath` disables bootstrap-managed seed-source selection. In that mode, `-LoadSeedData` requires
  `-SeedDataPath`, and `-SeedTemplate` is invalid.
- `-SeedDataPath` skips bootstrap-managed artifact resolution, but bootstrap still copies the supplied JSONL
  files into the repo-local seed workspace so every seed flow invokes the pinned BulkLoadClient against one
  materialized directory.
- The `-Extensions` parameter applies alongside `-SeedTemplate`. When a selected extension defines a
  built-in seed source, that source is merged into the same bootstrap workspace. When `-SeedDataPath` is specified,
  `-Extensions` seed-source resolution is skipped - the developer manages the contents of their own
  directory. Schema package selection and staged security configuration from `-Extensions` still apply
  regardless of `-SeedDataPath`, and additive `-ClaimsDirectoryPath` fragments still apply too. Custom seed
  compatibility is therefore evaluated against the same staged schema/security inputs as the rest of the
  run, even though bootstrap-managed seed-package resolution is bypassed.
- Bootstrap does not inspect arbitrary custom seed files to infer missing extensions, namespace prefixes, or
  claimsets. `-SeedDataPath` is a data-source selection mechanism, not a second schema-discovery path, not a
  dynamic claim-derivation mechanism, and not a payload-certification pass.

**Example commands:**

```powershell
# Default: Ed-Fi Minimal seed data (descriptors only)
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData

# Ed-Fi Populated template
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData -SeedTemplate Populated

# Custom seed data directory (bypasses NuGet download)
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData -SeedDataPath "./my-seeds/"

# Custom seed data after the run's schema/security inputs have already been staged
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData -SeedDataPath "./sample-seeds/"
```

In DMS-916 v1, the supported `sample` and `homograph` extensions do not define built-in seed packages. When
`-LoadSeedData` is used with those extensions, bootstrap still stages their schema and security inputs, but
extension payloads themselves must come from `-SeedDataPath` if needed.

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

---

### 6.3 DMS-Side Integration

#### 6.3.1 Updated `-LoadSeedData` Behavior

The `-LoadSeedData` switch is owned by `load-dms-seed-data.ps1`, which runs after the pre-DMS phases have
completed and the selected DMS endpoint is healthy. When set, that phase command performs the following
steps:

**Step 1. Resolve seed sources.** Build the set of developer seed sources to materialize for the run: always
include the core seed source selected by `-SeedTemplate` (`Minimal` by default, or `Populated` when
specified) only in standard Modes 1 and 2. In expert `-ApiSchemaPath` mode, bootstrap-managed seed-source
selection is disabled, so `-LoadSeedData` is valid only when `-SeedDataPath` supplies the sole seed source.
In standard modes, append any extension-derived seed sources from `-Extensions` unless `-SeedDataPath` is
supplied, in which case the custom directory is the only seed source even though schema/security inputs
from `-Extensions` and `-ClaimsDirectoryPath` still govern compatibility for the run.

**Step 2. Materialize seed files.** For bootstrap-managed Ed-Fi seed sources, use the repo-managed resolution
path to download and expand the selected artifact into the bootstrap seed workspace. For `-SeedDataPath`,
copy the supplied JSONL files into that same workspace so the BulkLoadClient invocation shape stays uniform.
Detect and abort on filename collisions between all materialized sources before proceeding. The exact
external artifact naming, ordering, and publishing model for the bootstrap-managed sources remains in
DMS-1119 or the external BulkLoadClient JSONL contract.

**Step 3. Invoke BulkLoadClient.** Call the pinned BulkLoadClient DLL with the merged seed workspace, DMS
base URL, the OAuth token URL resolved from `-IdentityProvider` for the current iteration, and the
bootstrap credentials using the contracted surface defined in
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
    --continue-on-error  # tolerate duplicates on re-runs
```

**Step 4. Check exit code.** If the BulkLoadClient exits non-zero, the bootstrap script throws and halts.

**Step 5. Clean up.** Remove the seed workspace on success (leave it on failure to aid debugging).

> **Re-run idempotency.** When `-LoadSeedData` is invoked against a database that already contains seed data (e.g., re-running bootstrap without `-v` teardown), the DMS API will return `409 Conflict` for resources that already exist. The `--continue-on-error` flag is passed by default so that BulkLoadClient skips duplicates and continues loading any new or missing resources. This makes seed loading safe to re-run without requiring a full teardown.

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
- Replace with the BulkLoadClient invocation logic described in [Updated `-LoadSeedData` Behavior](#631-updated--loadseeddata-behavior).
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
smoke-test credentials are CMS-only pre-DMS work anchored to the target set selected in step 7. `SeedLoader`
credentials are a separate DMS-dependent flow used only for `-LoadSeedData` after step 9 confirms a healthy
DMS endpoint. Bootstrap does not expose one blended post-start credential-bootstrap phase.

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
- **Target binding**: uses the DMS instance IDs already selected in step 7 and never calls
  `Get-DmsInstances` or performs a second CMS discovery pass.
- **Dependency gate**: depends only on CMS readiness and the selected target set. It does not require a live
  DMS endpoint, DMS health wait, or `-DmsBaseUrl`, so it is valid in `-InfraOnly` mode.
- **Purpose**: surfaces credentials for smoke tooling or manual verification only. BulkLoadClient does not
  consume them, and they are not a prerequisite for the `SeedLoader` flow.

#### 7.2.2 `SeedLoader` credentials for API seed delivery

**Seed Loader Application**

- **Trigger**: runs only when `-LoadSeedData` is set, after step 9 confirms a healthy DMS endpoint for the
  current flow.
- **Claim set**: a dedicated `SeedLoader` claim set that grants the bootstrap writer permissions required
  by the built-in seed manifests plus any staged `SeedLoader` permissions that the run brings in through
  selected extensions or additive claims fragments.
- **Target binding**: uses the DMS instance IDs already selected in step 7 and never calls
  `Get-DmsInstances` or performs a second CMS discovery pass.
- **Namespace prefixes**: must cover the namespaces present in the selected seed source. For Ed-Fi-provided
  seed packages, the baseline set is `uri://ed-fi.org` and `uri://gbisd.edu`, plus the namespace prefix for
  each loaded extension (for example, `uri://sample.ed-fi.org`). When `-Extensions` is used (see
  [Extension Selection and Loading](#8-extension-selection-and-loading)), the extension portion of the
  prefix list is computed dynamically from the selected extension set. Custom `-SeedDataPath` inputs do not
  add a second namespace-discovery mechanism; the files must stay compatible with the schema, namespace, and
  security inputs already staged for the run.
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
- `-SeedDataPath` is compatible with the run's staged schema/security inputs, including embedded claims,
  selected extension fragments, and additive `-ClaimsDirectoryPath` fragments. Bootstrap does not inspect
  arbitrary JSONL files to certify authorization completeness, and payload-level authorization or schema
  mismatches remain BulkLoadClient or DMS runtime failures.

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

The variables remain in scope for the duration of `start-local-dms.ps1`. Credentials are held in memory for
the run; they are not written to disk. The `--token-url` value is resolved from the selected
`-IdentityProvider` using the same `OAUTH_TOKEN_ENDPOINT` logic the current script already applies for DMS
startup, so Keycloak and self-contained auth continue to work consistently.

#### Credential Lifecycle

`$seedKey`, `$seedSecret`, `$smokeKey`, and `$smokeSecret` exist only as PowerShell variables within the
bootstrap script's process lifetime. They are not persisted to disk. On subsequent bootstrap runs, new
application records are created. In v1, bootstrap treats application creation as create-only work and does
not promise in-place cleanup or overwrite semantics for existing CMS application records. Fixed application
names remain useful as stable display labels, but fresh credentials come from new records created during the
current run.

**Design rationale:** Writing OAuth secrets - even to a git-ignored local file - creates unnecessary
plaintext-at-rest exposure on developer machines and in CI agents. Bootstrap credential creation is fast
(< 3 seconds), so there is no practical benefit to caching secrets across runs. Developers who need to
reuse credentials across a long session should simply re-run bootstrap. Whenever a selected flow needs
credentials (`-AddSmokeTestCredentials` or `-LoadSeedData`), bootstrap recreates them in v1 rather than
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
| `SeedLoader` claim set | Add the top-level `SeedLoader` definition and the required core permissions to the embedded claims resource at `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json`. **This is a bootstrap blocker** for API-based seeding: if `SeedLoader` metadata is not present in CMS claims data, `-LoadSeedData` must fail fast before invoking BulkLoadClient. See required permissions table below. |
| Extension `SeedLoader` coverage | Each bootstrap-managed extension fragment must attach `SeedLoader` permissions for every resource emitted by that extension's built-in seed package, alongside the extension's normal developer-access permissions. |
| `Add-SeedLoaderCredentials` helper | Wraps the same `Add-CmsClient` -> `Get-CmsToken` -> `Add-Vendor` -> `Add-Application` flow with Seed Loader-specific defaults; alternatively, parameterize `Get-SmokeTestCredentials` to accept a claim set name. |
| Dynamic namespace prefix list | Computed from the standard seed baseline (`uri://ed-fi.org`, `uri://gbisd.edu`) plus the `-Extensions` parameter ([Extension Selection and Loading](#8-extension-selection-and-loading)); passed to `Add-Vendor` as a comma-separated string. |

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

> **Note:** Read (GET) access is not required. BulkLoadClient uses POST for all seed records and relies on `--continue-on-error` for duplicate detection (409 Conflict). No GET calls are made during seed loading.

---

### 7.3 Bootstrap Sequence

Section 7 therefore spans two explicit credential contracts in the overall sequence (see
[Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence)): optional CMS-only smoke-test credentials in
step 7a, and DMS-dependent `SeedLoader` credentials in step 10. The abbreviated sub-sequence below shows
only the steps directly relevant to this section, using the same step numbers as the main bootstrap
sequence in Section 5:

```text
Steps 3-6. Infrastructure and CMS up
  - PostgreSQL, the selected identity provider, and the Config Service are healthy.
  - Claimsets have been staged and loaded into CMS.

Step 7. DMS instance creation / selection
  - Create the requested DMS instances in CMS, or select the single allowed rerun target when
    `-NoDmsInstance` is used.

Step 7a. Optional smoke-test credential bootstrap
  - Uses the step-7-selected target instance IDs and does not call `Get-DmsInstances` again.
  - `Add-CmsClient`
  - `Get-CmsToken`
  - `Add-Vendor`
  - Optional `Add-Application` -> `$smokeKey` / `$smokeSecret` for `EdFiSandbox`
  - These credentials are for smoke tooling only and are not reused by step 10.

Steps 8-9. Authoritative schema preparation and DMS availability
  - Invoke the authoritative SchemaTools/runtime-owned provisioning and validation path over the
    staged schema set for the target databases selected in step 7.
  - Continue only when that shared path reports the targets are ready for the selected schema set.
  - In the IDE path, wait for the developer-started DMS process after step 8 completes.

Step 10. Seed-loader credential bootstrap
  - Runs only when `-LoadSeedData` is set.
  - Begins only after step 9 confirms a live DMS endpoint for the current flow.
  - Reuses the selected target instance set from step 7; no second CMS discovery pass occurs.
  - Does not depend on step 7a; bootstrap creates a separate `SeedLoader` application even when
    smoke-test credentials were never requested.
  - `Add-Application` -> `$seedKey` / `$seedSecret` for the `SeedLoader` claim set

Step 11. Seed data loading
  - Invoke BulkLoadClient with `$seedKey` / `$seedSecret`.
  - Invoke once against the merged seed workspace; bootstrap does not define a second core-before-extension
    ordering rule beyond the external JSONL ordering already carried by the staged sources.
```

Smoke-test credential bootstrap (step 7a) is CMS-only work anchored to the step-7-selected target set.
`SeedLoader` credential bootstrap (step 10) is a separate DMS-dependent flow used only for `-LoadSeedData`.
BulkLoadClient authenticates against the DMS OAuth endpoint using the Key/Secret issued in step 10, so
step 10 must complete before step 11. `-AddSmokeTestCredentials` therefore does not require `-DmsBaseUrl`,
and the DMS-dependent continuation is entered only for seed delivery.

## 8. Extension Selection and Loading

### 8.1 Current Extension Loading

Extensions in DMS map to two concerns: (1) security metadata (claimsets) and (2) API surface (ApiSchema overlays). Today both are handled via static volume mounts with no selection abstraction.

**Claimset loading** is gated by the `-AddExtensionSecurityMetadata` flag on `start-local-dms.ps1`. When set, the script exports `DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims` and `local-dms.yml` mounts `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets` to that path. The Config Service reads every JSON file found in the directory on startup. There is no filtering — all mounted claimset files are loaded regardless of which extensions the developer intends to use.

**ApiSchema overlays** for extensions require a separate volume mount that places extension-specific `ApiSchema.json` files into the DMS container's schema directory. This mount is also all-or-nothing; a developer cannot limit which extensions are active without editing `local-dms.yml` by hand.

Key characteristics of the current model:

- No parity with ODS `-UsePlugins` — there is no parameter to specify extension names
- All-or-nothing per mount — selecting a subset of extensions requires manual `local-dms.yml` edits
- Extension seed data has no defined bootstrap path; it must be loaded manually after container startup

---

### 8.2 Proposed `-Extensions` Parameter

> **v1 scope guardrail (precise).** This section describes the v1 `-Extensions` surface as a deliberately
> narrow scope, not a generalized extension framework. Three constraints apply together and are normative
> for DMS-916:
>
> 1. **Closed v1 set.** `-Extensions` accepts only names present in the well-known v1 mapping (currently
>    `sample` and `homograph`; see Section 3.3, Mode 2 table). Any other name is a fail-fast error before container
>    startup. Adding a new built-in extension is an explicit follow-on ticket that updates the mapping.
> 2. **Omitting `-Extensions` means core only.** The default profile is the standard core Data Standard
>    schema with no extension schemas, no extension claimsets, and no extension seed packages. It does not
>    fall back to the legacy `eng/docker-compose` `SCHEMA_PACKAGES` baseline that includes TPDM. TPDM is not
>    part of the v1 `-Extensions` surface and is not part of the default profile.
> 3. **Built-in support is per-mapping-row, not per-extension-name.** A name in the v1 set automatically
>    receives only the artifact slots its mapping row defines (schema package and security fragments are
>    required; built-in seed package is optional). For v1, neither `sample` nor `homograph` defines a
>    built-in seed package; bootstrap therefore does not advertise built-in seed support for them. Custom
>    seed payloads still flow through `-SeedDataPath` (Mode 3 / additive).
>
> This is a scope guardrail. It does not introduce new mapping fields, fallback rules, or runtime
> mechanisms beyond what is already defined in Sections 3.3, 8.3, and 8.4.

**V1 support matrix**:

| Extension name | Schema support | Security support | Built-in seed support | DMS-916 v1 status |
|---|---|---|---|---|
| Core only (omit `-Extensions`) | Yes | Yes, embedded claims | Yes, core-managed seed sources | Supported default |
| `sample` | Yes | Yes | No built-in extension seed package | Supported |
| `homograph` | Yes | Yes | No built-in extension seed package | Supported |
| `tpdm` | No | No | No | Intentionally unsupported in v1 |
| Any other extension name | No | No | No | Fail-fast unsupported |

This table is the explicit v1 support contract. It separates "supported now" from "possible later"
without widening scope:

- `sample` and `homograph` are supported for schema and security selection today.
- Built-in extension seed support is a per-mapping capability, not an automatic property of every
  supported extension.
- Unsupported names stay outside the v1 surface until their schema and security artifacts are
  intentionally added to the mapping.

`-Extensions` belongs to `prepare-dms-schema.ps1` (see `command-boundaries.md` §3.1). It accepts one or
more extension identifiers (lowercase short names) typed as `String[]` through normal PowerShell array
binding. The phase command runs before any Docker services start:

```powershell
# Examples
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample"
pwsh eng/docker-compose/prepare-dms-schema.ps1 -Extensions "sample","homograph"
pwsh eng/docker-compose/prepare-dms-schema.ps1   # no -Extensions: core only
```

**Default behavior**: omitting `-Extensions` loads core Ed-Fi resources only - no staged extension security
fragments, no extension ApiSchema overlays, and no extension seed data. This is an intentional change from
today's `eng/docker-compose` baseline, which includes TPDM in `SCHEMA_PACKAGES`; DMS-916 v1 does not carry
that default forward into the normative bootstrap contract. The narrowed default keeps the environment
minimal and fast to start.

**How it works at runtime:**

1. `prepare-dms-schema.ps1` maintains a well-known mapping of extension short names to the bootstrap
   artifacts they own: schema package, staged security fragment path, extension seed package (when built-in
   seed support exists), and namespace prefix. Initially this mapping lives as a hashtable in
   `prepare-dms-schema.ps1`; it can be moved to a lookup file as the extension catalog grows. Each entry
   uses the same field set: `ApiSchemaPackage`, `SecurityFragments`, `BuiltInSeedPackage`, and
   `NamespacePrefix`.
2. For each name in `-Extensions`, bootstrap stages the corresponding security fragment JSON file(s) into
   the repo-local bootstrap workspace. That staged directory is bind-mounted to `/app/additional-claims`
   instead of the static `AdditionalClaimsets` source path. When `-LoadSeedData` is also set, the same
   mapping resolves the extension seed package list for the seed-workspace step. The compose files expose
   that source path through `DMS_CONFIG_CLAIMS_HOST_DIRECTORY`, a host-path variable consumed by
   `local-config.yml`. Its default value preserves the current static `AdditionalClaimsets` source path, and
   bootstrap overrides it to the staged workspace `eng/docker-compose/.bootstrap/claims` for the run. The
   `/app/additional-claims` mount in `local-dms.yml` stays outside the normative CMS-loading contract and may
   remain unchanged as compatibility cleanup. Any equivalent published-surface wiring is follow-on work
   outside DMS-916.
3. If `-Extensions` is non-empty, `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` is set automatically,
   `DMS_CONFIG_CLAIMS_DIRECTORY` remains `/app/additional-claims`, and the developer does not need to pass
   `-AddExtensionSecurityMetadata` separately. The existing flag remains for backward compatibility but is
   otherwise redundant when `-Extensions` is used.
4. If any specified extension name is not in the mapping, the phase command exits with a clear error before starting any containers.

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
For Ed-Fi-managed built-in seed artifacts, the intended packaging convention is to keep core-owned files in
the lower range and future extension-owned files in the higher range:

| Range | Scope |
|-------|-------|
| `01`-`49` | Core Ed-Fi standard seed data (descriptors, base education organizations, etc.) |
| `50`-`99` | Extension seed data for Ed-Fi-managed built-in extension packages when a supported extension defines one |

DMS-916 v1 does not advertise built-in extension seed packages as part of the supported extension surface.
The supported `sample` and `homograph` extensions participate in schema and security selection, but their
mapping rows do not currently define built-in seed packages.

This table is package-author guidance for Ed-Fi-managed built-in artifacts, not a bootstrap validation rule
for third-party publishers or for developer-supplied `-SeedDataPath` directories. The bootstrap contract
remains collision-based: any merged workspace is valid only when it can be flattened into one directory
without filename collisions. Any stronger ordering semantics remain external to DMS bootstrap, and future
built-in multi-source merging depends on those external artifacts already honoring the published JSONL
contract consumed by BulkLoadClient.

**Bootstrap implications:**

1. When `-LoadSeedData` is set with only the v1-supported extensions, bootstrap downloads and stages only
   the selected core seed source.
2. The extension mapping retains an optional built-in seed-package slot so a future supported extension can
   merge its JSONL files into the same workspace without changing the bootstrap shape, but only when that
   published package already follows the external JSONL ordering contract required by BulkLoadClient.
3. Until a supported extension actually defines that package in the mapping, developers supply extension
   payloads through `-SeedDataPath` when needed.
4. If more than one built-in source is ever merged into the workspace, filename collisions remain a
   bootstrap-time error and must abort before BulkLoadClient runs.

This keeps the BulkLoadClient invocation uniform (one call, one directory) while keeping deferred extension
seed packages out of the DMS-916 v1 support surface.

### 8.4 Integration with ApiSchema.json Selection (Section 3)

The `-Extensions` parameter is the single developer-facing control for enabling extensions. Passing it triggers three coordinated actions automatically - the developer does not need to configure each concern separately:

1. **ApiSchema files staged host-side (Section 3)** - The script resolves the extension's NuGet schema
   package (e.g., `EdFi.Sample.ApiSchema`) on the host, stages the resulting file in
   `eng/docker-compose/.bootstrap/ApiSchema/`, and includes that staged file in the exact set later hashed
   and mounted/read by DMS.

2. **Security fragments loaded (Section 4)** - The corresponding security fragment JSON file(s) are staged
   and bind-mounted to `/app/additional-claims`. `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` is set automatically,
   `DMS_CONFIG_CLAIMS_DIRECTORY` remains `/app/additional-claims`, and bootstrap points
   `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` at the staged host workspace. The developer does not need to pass
   `-AddExtensionSecurityMetadata` separately. For built-in extension seed support, those fragments must
   attach both `EdFiSandbox` and `SeedLoader` permissions to the extension resources they cover.

3. **Extension seed data handling (Section 8.3)** - When `-LoadSeedData` is also set, bootstrap checks
   whether any selected extension has a built-in seed package in the mapping. DMS-916 v1-supported
   extensions do not, so the seed workspace remains core-only unless the developer supplies
   `-SeedDataPath`. If a future supported extension adds a built-in seed package, its JSONL files are merged
   into the same bootstrap workspace only when that package participates in the same external JSONL contract
   used for core artifacts.

**Example - sample-enabled bootstrap run:**

After schema and claims preparation have already selected the sample extension for the run, the seed-delivery
phase behaves as follows:

```powershell
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData
# Result:
#   eng/docker-compose/.bootstrap/ApiSchema/ includes the staged Sample ApiSchema.json  (Section 3)
#   /app/additional-claims contains the sample security fragment with EdFiSandbox coverage  (Section 4)
#   Seed workspace contains only the selected core seed source unless -SeedDataPath is supplied  (Section 8.3)
```

**Note - `-ApiSchemaPath` mutual exclusivity:** `-ApiSchemaPath` and `-Extensions` are mutually exclusive
parameters. When `-ApiSchemaPath` is provided, the developer supplies a complete custom schema environment
and must also supply any non-core security or seed inputs explicitly via `-ClaimsDirectoryPath` and
`-SeedDataPath`. This avoids the schema/security mismatch described in the single source of truth principle
above while still giving expert users a fully supported custom-schema workflow.

---

## 9. Bootstrap Commands and Parameters

In DMS-916, the story's "skip/resume" wording is interpreted narrowly and intentionally: the design defines
safe skip behavior through the phase-oriented commands plus optional same-invocation continuation through
`-InfraOnly -DmsBaseUrl`. It does **not** define a persisted cross-invocation resume mechanism,
checkpoint file, or second control plane. Phase commands are the normative contract;
`bootstrap-local-dms.ps1` is a thin convenience entry point for the common developer path when
implemented — it is optional convenience packaging only and is not a mandatory design deliverable.

> **Wrapper simplicity rule (precise, read with [`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional)).**
> The wrapper sequences phase commands and forwards `-ConfigFile` unchanged. Accepting `-ConfigFile` does
> not make the wrapper a policy owner: each phase reads its own keys from that file (see
> [Section 9.4.2](#942-wrapper-defaults-configuration-file)), and the wrapper never parses, translates,
> or re-routes individual parameter values. Adding, renaming, or removing any phase parameter — CLI flag
> or config file key — is a change to the owning phase command alone and never requires wrapper-specific
> logic.

### 9.1 Infrastructure Phase Command

`eng/docker-compose/start-local-dms.ps1` is the infrastructure-phase command. Its primary concern is
Docker stack management and service health waiting. The infrastructure lifecycle parameters are shown
below; parameters owned by other phase commands are documented under their owning commands and listed in
[`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner). The
`-InfraOnly` and `-DmsBaseUrl` parameters proposed by DMS-916 appear in Section 9.3.2.

```powershell
# Default core-only stack
pwsh eng/docker-compose/start-local-dms.ps1

# Keycloak identity provider with Swagger UI
pwsh eng/docker-compose/start-local-dms.ps1 -EnableSwaggerUI -IdentityProvider keycloak

# Infrastructure only — stop before DMS starts (IDE workflow)
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly

# Teardown stack and volumes
pwsh eng/docker-compose/start-local-dms.ps1 -d -v
```

Infrastructure lifecycle parameters on `start-local-dms.ps1`:

| Parameter | Type | Description |
|-----------|------|-------------|
| `-d` | Switch | Stop running services instead of starting them |
| `-v` | Switch | Delete volumes after stopping (used with `-d`) |
| `-EnvironmentFile` | String | Path to `.env` file; defaults to `./.env` |
| `-r` | Switch | Force a Docker image rebuild (`--no-cache`) |
| `-EnableKafkaUI` | Switch | Include Kafka UI container in the stack |
| `-EnableConfig` | Switch | Include the DMS Configuration Service container |
| `-EnableSwaggerUI` | Switch | Include Swagger UI container for the DMS API |
| `-IdentityProvider` | String | Identity provider selection: `keycloak` or `self-contained` (default) |

Parameters owned by other phase commands (`-LoadSeedData`, `-AddExtensionSecurityMetadata`, `-AddSmokeTestCredentials`, `-NoDmsInstance`, `-SchoolYearRange`) are documented under their owning commands; see [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner) for the per-phase distribution.

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
After step 7 selects the target instances, step 8 always invokes the authoritative
SchemaTools/runtime-owned provisioning and validation path over the staged schema set before any DMS
process is expected to serve requests.

The existing `-LoadSeedData` flag remains the intentional opt-in for DMS-dependent seed delivery; omitting
it skips both SeedLoader credential creation and seed loading for the run. `-AddSmokeTestCredentials`
remains a separate opt-in for the CMS-only smoke-credential phase after step 7. `-InfraOnly` and
`-DmsBaseUrl` shape whether bootstrap stops after the pre-DMS phase or continues against an IDE-hosted DMS
endpoint, but they do not bypass step 8.

**Bootstrap does not add extra skip/resume flags inside either credential path in v1.** When
`-AddSmokeTestCredentials` or `-LoadSeedData` is selected, the corresponding credential creation reruns each
time because credentials are ephemeral, not written to disk, and inexpensive to recreate. This avoids
reintroducing plaintext secret caching or designing an incomplete secret-recovery path.

**Safety**: step 8 delegates schema readiness to the shared SchemaTools/runtime-owned path rather than to a
bootstrap-only state file or bootstrap-authored readiness classifier. Security configuration is
intentionally not separately skippable in v1 because it is cheap, deterministic, and derived directly from
the current inputs each run.

Optional post-bootstrap automation such as SDK generation or integrated smoke/E2E/integration test runners
was considered during the ODS audit, but it is intentionally out of scope for DMS-916.

---

### 9.3 Parameter Surface

#### 9.3.1 V1 Top-Level Surface

For the common developer path, the thin convenience wrapper exposes exactly one parameter:

| Command | Parameter | Required | Purpose |
|---------|-----------|----------|---------|
| `bootstrap-local-dms.ps1` | `-ConfigFile <path>` | No | Path to `bootstrap-local-dms.config.json`; each phase reads its own keys before CLI overrides apply. |

All behavioral flags — schema selection, claims configuration, instance management, seed loading — belong to the phase command that owns each concern. Adding a new parameter to any phase command never requires a wrapper change. See [Section 9.4.2](#942-wrapper-defaults-configuration-file) for the config file key reference and [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner) for the complete per-phase distribution.

#### 9.3.2 Infrastructure Phase Parameters (`start-local-dms.ps1`)

`start-local-dms.ps1` owns the Docker infrastructure lifecycle: stack management, service health waiting, and backward-compatible continuation flags. This section covers only the parameters owned by this command. For the complete per-phase parameter distribution — including schema selection, claims, instance configuration, and seed loading — see [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner).

The Config Service is mandatory for the normative bootstrap contract. Every non-teardown
DMS-916 bootstrap invocation starts the Config Service unconditionally, including
the default no-argument core-only Mode 1 flow and keycloak-backed runs. `-EnableConfig` therefore remains
only as a backward-compatibility switch, not as a meaningful opt-out on the canonical bootstrap contract.

| Parameter | Type | Default | Status | Description |
|-----------|------|---------|--------|-------------|
| `-d` | Switch | — | Existing | Stop running services |
| `-v` | Switch | — | Existing | Delete volumes (use with `-d`) |
| `-EnvironmentFile` | String | `./.env` | Existing | Path to `.env` file |
| `-r` | Switch | — | Existing | Force Docker image rebuild |
| `-EnableKafkaUI` | Switch | — | Existing | Include Kafka UI container |
| `-EnableConfig` | Switch | - | Existing (script compatibility) | Retained on `start-local-dms.ps1` for compatibility. The story-aligned bootstrap flow always includes the Config Service, so this is not treated as a meaningful opt-out on the canonical bootstrap contract. |
| `-EnableSwaggerUI` | Switch | — | Existing | Include Swagger UI container |
| `-IdentityProvider` | String | `self-contained` | Existing | Identity provider: `keycloak` or `self-contained` |
| `-InfraOnly` | Switch | - | **Proposed** | Exclude the DMS container from Docker startup. Bootstrap still performs the pre-DMS steps through instance creation and schema provisioning/validation. With `-DmsBaseUrl`, the same invocation then continues against an external DMS endpoint; without it, the run stops after reporting IDE next steps. This pre-DMS-only shape is terminal for that invocation: DMS-916 does not define a later bootstrap "resume" that picks up unfinished post-start work from the stopped run. ([IDE Debugging Workflow](#12-ide-debugging-workflow)) |
| `-DmsBaseUrl` | String | - | **Proposed** | External IDE-hosted DMS endpoint used only with `-InfraOnly`. When omitted, `-InfraOnly` stops after the pre-DMS phase; when set, bootstrap automatically waits for that external DMS process to become healthy, then continues against it. Any run that intends DMS-dependent continuation work such as seed loading must provide `-DmsBaseUrl` on that same invocation. |

**Breaking-change note:** The DMS-916 definition of `-NoDmsInstance` (owned by `configure-local-dms-instance.ps1`) deliberately narrows the current behavior. Existing scripts that used it on a fresh stack as a generic "skip creation" switch must now either drop the flag or pre-create exactly one intended target instance before rerunning. See also [Section 15](#15-breaking-changes-and-migration-notes) for the consolidated migration reference.

**Phase parameter ownership cross-reference:** All bootstrap parameters not in the table above belong to the phase command listed here. See [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner) for authoritative per-phase ownership.

| Parameter | Owned by |
|-----------|----------|
| `-Extensions`, `-ApiSchemaPath` | `prepare-dms-schema.ps1` |
| `-ClaimsDirectoryPath`, `-AddExtensionSecurityMetadata` | `prepare-dms-claims.ps1` |
| `-NoDmsInstance`, `-SchoolYearRange`, `-AddSmokeTestCredentials` | `configure-local-dms-instance.ps1` |
| `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath` | `load-dms-seed-data.ps1` |
| `-InfraOnly`, `-DmsBaseUrl`, teardown flags, identity flags | `start-local-dms.ps1` |

#### Parameter Validation Rules

Each validation rule below is owned by the phase command responsible for the affected parameters; cross-phase mutual-exclusion rules are enforced at the earliest gate phase. Phase commands exit immediately on invalid combinations; supported combinations requiring non-fatal warnings continue with documented behavior.

| Rule | Outcome |
|------|---------|
| `-Extensions` and `-ApiSchemaPath` both specified | "Error: -Extensions and -ApiSchemaPath are mutually exclusive. Use -Extensions for well-known extensions or -ApiSchemaPath for a custom schema directory." |
| `-ApiSchemaPath` staging does not normalize to exactly one core `ApiSchema*.json` file plus zero or more extension files | "Error: -ApiSchemaPath must resolve to exactly one core ApiSchema.json and zero or more extension ApiSchema.json files after staging." |
| `-SeedTemplate` and `-SeedDataPath` both specified | "Error: -SeedTemplate and -SeedDataPath are mutually exclusive. Use -SeedTemplate for Ed-Fi packages or -SeedDataPath for a custom seed directory." |
| `-ApiSchemaPath` with `-SeedTemplate` | "Error: -SeedTemplate is not valid with -ApiSchemaPath. Expert custom-schema mode disables bootstrap-managed seed selection; use -SeedDataPath when -LoadSeedData is required." |
| `-ApiSchemaPath` with staged extension schemas but without `-ClaimsDirectoryPath` | "Error: -ApiSchemaPath staged one or more extension schemas, but no -ClaimsDirectoryPath was provided. Expert custom-schema mode requires explicit non-core security metadata for staged extension resources." |
| `-ApiSchemaPath` with `-LoadSeedData` but without `-SeedDataPath` | "Error: -LoadSeedData with -ApiSchemaPath requires -SeedDataPath. Expert custom-schema mode does not fall back to built-in Minimal or Populated seed templates." |
| `-AddExtensionSecurityMetadata` with `-Extensions` or `-ClaimsDirectoryPath` | Permitted with warning. The script ignores `-AddExtensionSecurityMetadata` and emits a warning indicating it is a legacy-only flag when `-Extensions` or `-ClaimsDirectoryPath` is supplied and that bootstrap will use the staged claims workspace instead. |
| `-LoadSeedData` with `-SeedDataPath` and `-Extensions` | `-Extensions` seed package resolution is skipped. Schema packages and staged security configuration from `-Extensions` still apply. The script emits a warning indicating that extension seed package lookup is skipped when `-SeedDataPath` is provided. |
| `-InfraOnly` without `-DmsBaseUrl` | Permitted. Bootstrap performs infrastructure startup, instance creation, and schema provisioning/validation, then stops with next-step guidance for the IDE-hosted DMS launch. This is a terminal pre-DMS-only invocation, not a checkpoint for a later bootstrap resume. |
| `-LoadSeedData` with `-InfraOnly` but without `-DmsBaseUrl` | "Error: -LoadSeedData requires an active DMS endpoint. Supply -DmsBaseUrl to continue bootstrap against an IDE-hosted DMS, or remove -LoadSeedData for infra-only preparation. The pre-DMS-only -InfraOnly flow never defers seed loading to a later implicit continuation." |
| `-InfraOnly` without `-DmsBaseUrl`, with smoke-credential opt-in | Permitted. Bootstrap creates smoke-test credentials after step 7 using the selected target instance IDs, then stops after step 8 with IDE next-step guidance. |
| `-DmsBaseUrl` without `-InfraOnly` | "Error: -DmsBaseUrl is only valid when -InfraOnly is used to continue bootstrap against an IDE-hosted DMS endpoint." |
| `-NoDmsInstance`, no `-SchoolYearRange`, exactly one existing instance found in the current tenant scope | Permitted. Bootstrap reuses that single existing instance as the canonical step-7 target set for the run. |
| `-NoDmsInstance`, no `-SchoolYearRange`, zero existing instances found in the current tenant scope | "Error: -NoDmsInstance requires exactly one existing DMS instance in the current tenant scope, but none were found. Re-run without -NoDmsInstance to create the instance, or prepare the environment manually before retrying." |
| `-NoDmsInstance`, no `-SchoolYearRange`, multiple existing instances found in the current tenant scope | "Error: -NoDmsInstance requires exactly one existing DMS instance in the current tenant scope, but multiple instances were found. Tear down the extra instances or manually prepare the environment so one intended target remains before rerunning." |
| `-NoDmsInstance` with `-SchoolYearRange` | "Error: -NoDmsInstance with -SchoolYearRange is not supported in DMS-916. Tear down and recreate the environment for multi-year runs." |
| `-SchoolYearRange` is not in `YYYY-YYYY` format with four-digit integers and `endYear >= startYear` | "Error: -SchoolYearRange must use YYYY-YYYY format with four-digit years, and the ending year must be greater than or equal to the starting year." |
| `-ClaimsDirectoryPath` path does not exist or contains no `*-claimset.json` files | "Error: -ClaimsDirectoryPath must point to a directory containing one or more *-claimset.json files." |
| `-ClaimsDirectoryPath` fragments collide with staged extension fragments by filename | "Error: Claimset fragment filename collision detected for '<file>'. Each staged *-claimset.json filename must be unique before bootstrapping." |
| `-ClaimsDirectoryPath` fragment references a claim set name that does not exist in the embedded `Claims.json` | "Error: Claimset fragment '<file>' references unknown claim set '<name>'. DMS-916 additive fragments may only attach to claim sets declared in the embedded Claims.json." |
| `-LoadSeedData` set but the pinned BulkLoadClient package cannot be resolved or does not expose the required JSONL interface | "Error: BulkLoadClient package resolution failed or the required JSONL interface is unavailable." |
| `-LoadSeedData` is set but the embedded claims metadata does not define the top-level `SeedLoader` claim set | "Error: -LoadSeedData requires the embedded CMS claims metadata to define the top-level SeedLoader claim set. Bootstrap cannot continue to BulkLoadClient until that claim set exists." |
| Unrecognized name in `-Extensions` | "Error: Unrecognized extension '<name>'. Valid extensions: sample, homograph" |
| `-LoadSeedData` with `-Extensions` where an extension is in the mapping but its NuGet seed package fails to resolve | "Error: Seed package for extension '<name>' could not be resolved. Check network/feed access or supply the package manually." |
| `-LoadSeedData` with `-Extensions` where an extension has no seed package entry in the mapping | Permitted with warning. Bootstrap emits an informational warning indicating no built-in seed package is available; schema and security configuration from the extension still apply. |
| `-LoadSeedData` with a built-in extension seed source whose staged security fragments do not attach required `SeedLoader` permissions for that extension's seed resources | "Error: Extension '<name>' seed support is incomplete. The staged security fragments do not provide the required SeedLoader permissions for the extension seed resources." |

---

### 9.4 Developer Invocation Examples

Phase commands may be called individually for targeted re-runs, or sequenced through the thin convenience
wrapper (`bootstrap-local-dms.ps1`) for the happy path. Common invocations:

```powershell
# Full bootstrap via thin wrapper (the wrapper contributes only -ConfigFile;
# each phase reads its own defaults from that file)
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -ConfigFile eng/docker-compose/bootstrap-local-dms.config.json

# Infrastructure phase only (no DMS container; prints IDE next-step guidance)
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly

# Infrastructure phase with same-invocation IDE continuation (waits for DMS to report healthy)
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -DmsBaseUrl "http://localhost:5198"

# Teardown stack and volumes
pwsh eng/docker-compose/start-local-dms.ps1 -d -v
```

No shell/session preparation is required before invoking any phase command. The story-aligned behaviors
documented elsewhere — `-Extensions`, authoritative step-8 schema-provisioning, `-InfraOnly`,
`-DmsBaseUrl` — apply regardless of whether phase commands are called individually or through the wrapper.

#### 9.4.2 Wrapper Defaults Configuration File

Developers who often use the same phase defaults can place an optional JSON defaults file alongside the
wrapper instead of repeating those values on every invocation. The wrapper passes `-ConfigFile <path>` to
each phase command; each phase reads its own relevant keys from that file before applying CLI overrides.
This file is a convenience defaults mechanism only. It is not a second orchestration surface, and it does
not let the wrapper own cross-phase policy or behavioral decisions.

**Why phases read their own keys directly (black-box boundary):**
If the wrapper extracted individual values and forwarded them by name, it would need to know
every phase command's parameter surface. Any new phase parameter would require a wrapper
change too — exactly the coupling the phase-oriented design exists to prevent. With
phase-reads-own-config, the wrapper is reduced to one concern: pass the config file path
through. Adding or renaming a parameter in any phase command is that phase's change alone.

**Precedence:** explicit CLI flag to the phase > config file value > built-in default.

**File location:** `eng/docker-compose/bootstrap-local-dms.config.json` (git-ignored).
Committed example: `eng/docker-compose/bootstrap-local-dms.config.json.example`.

**Key reference** (each phase reads only its own keys; unknown keys are ignored):

| Key | Type | Read by |
|---|---|---|
| `Extensions` | `string[]` | `prepare-dms-schema.ps1`, `prepare-dms-claims.ps1` |
| `IdentityProvider` | `string` | `start-local-dms.ps1` |
| `AddSmokeTestCredentials` | `bool` | `configure-local-dms-instance.ps1` |
| `SchoolYearRange` | `string` | `configure-local-dms-instance.ps1`, `load-dms-seed-data.ps1` |
| `LoadSeedData` | `bool` | `load-dms-seed-data.ps1` |
| `SeedTemplate` | `string` | `load-dms-seed-data.ps1` |
| `SeedDataPath` | `string` | `load-dms-seed-data.ps1` |

**Guard rails:**

- Each phase fails fast if the file exists but is not valid JSON.
- Unknown keys are silently ignored by each phase (forward-compatibility).
- The config file does not make the wrapper normative for any phase's behavior;
  phase commands remain the authoritative source of every lifecycle and domain rule.
- Parameters that are mode-changing, destructive, or expert-only (`-InfraOnly`,
  `-DmsBaseUrl`, `-d`, `-v`, `-ApiSchemaPath`, `-ClaimsDirectoryPath`, `-NoDmsInstance`)
  are intentionally absent from the key reference. They must be supplied directly on the
  owning phase command.

#### 9.4.1 Recommended Bootstrap Output

Bootstrap should present the major pipeline steps in a human-readable order so developers can tell what ran
and where a failure occurred. Exact formatting is intentionally non-normative; DMS-916 only needs clear step
names, clear failure reporting, and enough context to distinguish schema selection, schema deployment
results, credential bootstrap, and seed loading.

A minimal acceptable console shape is:

```text
Bootstrap-DMS: Starting...

[Step 1 - Validate parameters]                             (0.1s)
[Step 2 - Resolve ApiSchema.json selection]                (0.4s)
  Core:  EdFi.DataStandard52.ApiSchema 1.0.328
  Ext:   EdFi.Sample.ApiSchema         1.0.328
[Step 3 - Start PostgreSQL]                                (2.1s)
[Step 4 - Start identity provider]                         (3.2s)
[Step 5 - Prepare security environment]                    (0.2s)
  Hybrid mode: 1 security fragment(s) staged
[Step 6 - Start Config Service]                            (8.4s)
[Step 7 - Create DMS instances]                            (1.4s)
[Step 7a - Smoke-test credentials]                         (0.7s)
  Smoke credentials created for selected target instance(s)
[Step 8 - Provision or validate database schema]           (1.1s)
  SchemaTools provisioned 1 database(s); EffectiveSchemaHash=abc123...
[Step 9 - Start DMS]                                      (10.0s)
  DMS healthy at http://localhost:8080
[Step 10 - SeedLoader credential bootstrap]                (2.1s)
[Step 11 - Seed data loading]                            (187.3s)
  Core seed:  1,204 loaded, 0 conflicts, 0 skipped, 0 fatal errors

Bootstrap complete. DMS is ready at http://localhost:8080

Summary
  Step                                   Duration
  ----------------------------------------------
  Validate parameters                       0.1s
  Resolve ApiSchema selection               0.4s
  Start PostgreSQL                          2.1s
  Start identity provider                   3.2s
  Prepare security env                      0.2s
  Start Config Service                      8.4s
  Create DMS instances                      1.4s
  Smoke-test credentials                    0.7s
  Schema provisioning                       1.1s
  Start DMS                                10.0s
  SeedLoader credentials                    2.1s
  Seed data loading                       187.3s
  ----------------------------------------------
  Total                                   216.0s
```

CI environments may suppress color output. The summary table is always emitted on both success and failure; on failure the failing step name and error are printed before the summary.

#### 9.4.3 Thin Wrapper Contract

The complete normative wrapper contract — including the full prohibition list — is
[`command-boundaries.md` Section 3.7](command-boundaries.md#37-bootstrap-local-dmsps1--thin-convenience-wrapper-optional).
Read in conjunction with the wrapper simplicity rule in [Section 9](#9-bootstrap-commands-and-parameters)
and the single-parameter surface in [Section 9.3.1](#931-v1-top-level-surface), §3.7 is the authoritative
statement of what the wrapper does and does not do.

For convenience, the wrapper's permitted actions in this design narrative are:

- Call phase commands in the documented dependency order
  (`prepare-dms-schema.ps1` → `prepare-dms-claims.ps1` → `start-local-dms.ps1` →
  `configure-local-dms-instance.ps1` → `provision-dms-schema.ps1` → DMS startup → `load-dms-seed-data.ps1`).
- Pass `-ConfigFile <path>` to each phase command when the caller supplies it.
- Print next-step guidance after an intentionally omitted phase.
- Propagate the non-zero exit code immediately when any phase command fails.

Everything else — schema/claims/credential/state/continuation/policy ownership — is prohibited by §3.7
and is not restated here.

### 9.5 Bootstrap Working Directory

Bootstrap uses a repo-local working directory at `eng/docker-compose/.bootstrap/`. This directory is a
staging area, not an authoritative control plane. Real state continues to live in Docker containers,
volumes, environment files, and the target database.
The workspace is scratch-only bootstrap state; it must be excluded from source control via `.gitignore`.

**Layout**

- `eng/docker-compose/.bootstrap/claims/` - staged `*-claimset.json` files bind-mounted into CMS
- `eng/docker-compose/.bootstrap/ApiSchema/` - staged `ApiSchema*.json` files used for hashing and for both Docker-hosted and IDE-hosted DMS runs
- `eng/docker-compose/.bootstrap/seed/` - merged JSONL files for the current seed-loading run

**Lifecycle**

- The schema directory is materialized on first bootstrap run. On same-checkout reruns, bootstrap compares the intended schema set to the existing workspace: identical contents are reused as-is; differing contents are treated as incompatible existing state and require teardown before bootstrap rewrites the workspace.
- The schema directory remains in place while Docker-hosted or IDE-hosted DMS processes may still need to read it.
- The claims directory follows the same rule: bootstrap materializes it on first run, reuses it unchanged when the intended staged claims set is identical, and fails fast with teardown guidance when the intended set differs.
- The seed directory is deleted and recreated from scratch only when `-LoadSeedData` runs.
- The seed directory is deleted on successful completion of the seed step and left in place on failure for debugging.
- `start-local-dms.ps1 -d -v` removes the entire `eng/docker-compose/.bootstrap/` tree.
- The repo adds `eng/docker-compose/.bootstrap/` to `.gitignore`; staged claims and merged seed files must never be committed.
- Concurrent bootstrap runs against the same workspace are not supported because they would share the same staged directories.
- DMS-916 does not define a same-workspace lock file or locking protocol. If parallel bootstrap runs are required in CI or local automation, use separate repository checkouts or isolated workspaces instead.

Using a stable workspace directory keeps bind mounts and cleanup deterministic without trying to mirror live
container/database state into a separate JSON state file.

**`-InfraOnly` behavior**: When `-InfraOnly` is used, the same working-directory and authoritative
schema-provisioning rules apply through step 8. Without `-DmsBaseUrl`, bootstrap then stops and leaves the staged schema workspace in
place for the developer's next IDE-hosted DMS launch, though optional smoke-test credentials may already
have been created in step 7a. That stopped shape is terminal for the invocation: DMS-916 does not define a
later bootstrap resume that picks up step 9-11 from the stopped run. With `-DmsBaseUrl`, the later
DMS-dependent SeedLoader and seed steps target that external endpoint only after bootstrap automatically
confirms its health.

## 10. School-Year Range Handling

The only multi-instance concern kept in the normative DMS-916 design is the existing
`-SchoolYearRange` developer workflow. Broader multi-tenant orchestration concerns are intentionally left
outside this story.

### 10.1 School-Year Instance Creation

`-SchoolYearRange` on `start-local-dms.ps1` (for example, `"2022-2026"`) creates one DMS instance per
school year. The range is a closed interval inclusive on both ends: `"2022-2026"` enumerates school years
2022, 2023, 2024, 2025, and 2026. The format is `"<startYear>-<endYear>"` where both values are four-digit
integers and `endYear >= startYear`; any other format causes a pre-flight validation error.

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
- `--continue-on-error` remains enabled in the per-year path so duplicate-resource `409 Conflict` responses
  stay non-fatal during reruns of an already-seeded workspace.

**`-InfraOnly` + `-SchoolYearRange` combination**: This combination is fully supported. Infrastructure
startup, instance creation, and schema provisioning proceed as usual. When `-DmsBaseUrl` is set,
each BulkLoadClient invocation in the per-year loop targets that IDE-hosted DMS process rather than the
containerized endpoint. Without `-DmsBaseUrl`, the run stops after schema provisioning and prints
the settings the later IDE launch must use. That pre-DMS-only shape is still manual-only for the
school-year workflow; if the same run also needs SeedLoader credentials or seed loading under the IDE-hosted
process, `-DmsBaseUrl` must be present on the original invocation because DMS-916 does not define a
multi-year resume from the stopped pre-DMS state.

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

The bootstrap sequence in [Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence) includes a discrete
DDL step after instance creation but before DMS becomes the active API host for the run:

```text
Identity provider and PostgreSQL up
  -> [Step 5] Prepare security environment
  -> [Step 6] Start Config Service
  -> [Step 7] Create DMS instances
  -> [Step 7a] Optional smoke-test credentials
  -> [Step 8] Provision or validate schema directly through SchemaTools
  -> [Step 9] Start DMS (or wait for IDE-hosted DMS)
  -> [Step 10] SeedLoader credential bootstrap (when -LoadSeedData)
  -> Seed data loading via API
```

**v1 canonical mechanism: direct SchemaTools provisioning.** The committed Docker defaults leave
`AppSettings__DeployDatabaseOnStartup=false`, and bootstrap keeps that setting in place. Step 8 uses the
existing SchemaTools provisioning surface (`dms-schema ddl provision`) or a thin helper over the same
`IDatabaseProvisioner` and runtime-owned validation APIs against each target database before any DMS
process is expected to serve requests. This matches the repo's existing provisioning surface and keeps
schema preparation on the authoritative toolchain path rather than on host-start side effects.

Bootstrap relies on a deliberately narrow integration contract for that path:

- invoke the documented SchemaTools command shape with staged schema inputs and target connection details,
- treat exit code `0` as success and any non-zero exit code as failure,
- surface SchemaTools diagnostics directly to the user, and
- avoid bootstrap-owned parsing of stdout/stderr to classify specific rejection reasons.

The README is therefore the public invocation reference, not the sole normative source for the full
internal provisioning semantics implemented by SchemaTools. This design does not define a bootstrap-specific
alternate CLI surface, and it does not require bootstrap to reverse-engineer undocumented text-output
details in order to implement safe provisioning behavior.

**Bootstrap step 8 (`Invoke-DmsSchemaProvisioning`)** in v1 delegates to that shared path. Its
responsibilities are:

1. Resolve the staged schema inputs for the run, including the expected `EffectiveSchemaHash`, using the
   same hashing algorithm DMS and `dms-schema hash` already use.
2. Collect the target connection strings and dialect details from the DMS instances selected or created in
   step 7.
3. Invoke `dms-schema ddl provision` (or a thin helper over the same provisioning/runtime contract) for the
   selected targets before step 9 begins, using the staged schema files from step 2.
4. Let the shared provisioning/runtime contract perform the authoritative live-state inspection, any
   required provisioning work, and the serviceability checks needed to accept or reject the target for the
   selected schema set.
5. Continue only when that authoritative path returns success; otherwise fail fast on the non-zero exit code
   and surface its diagnostics without bootstrap-owned classification of specific stdout/stderr text.

**Shared fingerprint inputs (existing repo contract):** Bootstrap still reuses the shared effective-schema
fingerprint inputs already consumed by runtime and SchemaTools, including `EffectiveSchemaHash`. The
authoritative storage shape and lookup mechanism for those inputs remain backend-owned implementation
details. The step-8 helper may rely on that shared backend contract as one input into the authoritative
provisioning path, but bootstrap does not turn the underlying storage model into a separate public decision
table. `EffectiveSchemaHash` remains a shared input to provisioning and validation, not bootstrap's last
word on serviceability; broader checks such as schema-component validation and resource-key seed validation
remain owned by the shared runtime/SchemaTools path.

```powershell
Invoke-DmsSchemaProvisioning `
    -SchemaPaths $stagedSchemaPaths `
    -ExpectedEffectiveSchemaHash $expectedEffectiveSchemaHash `
    -TargetConnectionStrings $targetConnectionStrings
```

**Failure detection** happens in step 8 itself. `dms-schema ddl provision` already executes inside a
transaction and performs preflight validation through the repo's existing provisioning APIs. Because DMS-916
delegates to that authoritative path before DMS starts, step 9 only waits for DMS health; it does not
introduce a second bootstrap-owned schema-readiness classifier after startup.

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
  the expected `EffectiveSchemaHash` and staged schema context consumed by the authoritative step-8
  provisioning path before bootstrap continues.
- **Schema selection also drives physical table creation** - only the tables required by the selected schema
  set are considered aligned for the run. Core-only selection yields only core tables. Core plus Sample
  yields the core tables plus the sample-extension tables required by that combined schema set.
- **Schema selection still controls API surface and security** - the `-Extensions` value and the resulting
  `ApiSchema.json` determine which REST endpoints are exposed and which claimsets are loaded, and now those
  runtime inputs are expected to stay in sync with the exact physical schema provisioned for that run.
- **`Invoke-DmsSchemaProvisioning` consumes schema context at the target level** - the bootstrap script does
  not pass individual resource definitions to the hook, but it does pass the staged schema paths, the
  derived expected hash, and the target connection strings so the authoritative path can validate the
  correct provisioning context. In v1 the function drives direct SchemaTools provisioning rather than
  toggling DMS startup behavior. A representative signature:

  ```powershell
  Invoke-DmsSchemaProvisioning `
      -SchemaPaths $stagedSchemaPaths `
      -ExpectedEffectiveSchemaHash $expectedEffectiveSchemaHash `
      -TargetConnectionStrings $targetConnectionStrings
  ```

- **Changing `-Extensions` is a schema-compatibility decision** - adding or removing an extension changes
  the selected physical schema footprint for the run. If the selected schema set resolves to a different
  `EffectiveSchemaHash`, bootstrap treats the existing database as incompatible unless it is reprovisioned to
  the newly selected schema set.

### 11.6 Forward Compatibility

The bootstrap sequence is identical whether the backend uses JSONB or relational storage:

1. Supporting services come up. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), steps 1-6: validate parameters, resolve ApiSchema.json, start identity provider/PostgreSQL, stage security inputs, start Config Service)*
2. Bootstrap creates or selects the target DMS instances. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 7)*
3. Optional smoke-test credentials are created from the step-7-selected target set through CMS only. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 7a)*
4. Bootstrap invokes the authoritative SchemaTools/runtime-owned provisioning and validation path over the staged schema set for those targets. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 8)*
5. DMS starts (or the IDE-hosted DMS process is awaited). *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 9)*
6. SeedLoader credential bootstrap runs when `-LoadSeedData` is requested. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 10)*
7. Seed data loads through the API. *([Proposed Bootstrap Sequence](#5-proposed-bootstrap-sequence), step 11)*

The only storage-model-sensitive step is step 4 - the authoritative provisioning/validation implementation
behind the shared SchemaTools/runtime path. Steps 1, 2, 3, 5, 6, and 7 are storage-model-transparent.
If the backend redesign changes the authoritative provisioning surface, bootstrap still delegates to that
surface at step 8 rather than inventing a bootstrap-owned schema-readiness model.

---

## 12. IDE Debugging Workflow

This section documents how developers run or debug DMS locally in an IDE (Visual Studio, Rider) while Docker manages the supporting infrastructure. It covers the architecture, the mechanism for starting infrastructure without DMS, the environment variables required for the local process, and how bootstrap interacts with a locally running DMS instance.

> **Status (read with Section 14.1).** The IDE workflow described here is the accepted DMS-916 design,
> not a feature that is already wired into `start-local-dms.ps1`. The `-InfraOnly` switch and the
> `-DmsBaseUrl` continuation behavior are still labeled "Proposed" in this section because their
> implementation is owned by [`tickets/03-entry-point-and-ide-workflow.md`](tickets/03-entry-point-and-ide-workflow.md)
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
# Start PostgreSQL + Kafka + Config Service; skip DMS container
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
```

Implementation approach: `local-dms.yml` defines the `dms` service. When `-InfraOnly` is specified, the script passes `--scale dms=0` to `docker compose up`, or uses a dedicated compose override file (`local-infra-only.yml`) that omits the `dms` service. The simpler `--scale dms=0` approach is preferred to avoid maintaining a parallel compose file.

`-InfraOnly` does not make the Config Service optional. The canonical bootstrap still starts CMS because DMS
instance discovery, claimset seeding, and schema provisioning depend on it. `-InfraOnly` changes
only the Docker startup scope; what happens after infrastructure is ready depends on whether the run also
chooses an external DMS endpoint:

- `-InfraOnly` alone completes the pre-DMS phase: infrastructure startup, claims staging, instance creation,
  optional smoke-test credential creation, and schema provisioning/validation. It then stops and prints the
  settings the next IDE-hosted DMS launch must use.
- `-InfraOnly -DmsBaseUrl <url>` continues into the post-start phase against that explicit external DMS
  endpoint: automatic DMS health wait plus the SeedLoader-and-seed continuation when `-LoadSeedData` is
  selected. Optional smoke-test credentials, when requested, were already created in the pre-DMS phase.

**Relationship with `-NoDmsInstance`**: `-NoDmsInstance` skips creating DMS instance records in the Config Service but still starts the DMS container in the normal flow. `-InfraOnly` skips the DMS container entirely. When `-InfraOnly` is used, `-NoDmsInstance` is typically omitted because the IDE-hosted DMS process still reads instance records from the Config Service just as the containerized DMS would. If `-NoDmsInstance` is used in this workflow, the same narrow step-7 reuse rule applies: exactly one existing instance must already be present, `-SchoolYearRange` is not supported, and later phases consume that selected target rather than rediscovering it.

### 12.3 Key Environment Variables

When DMS runs outside Docker, ASP.NET Core reads configuration from `appsettings.json` and environment
variable overrides. The settings below are the normal IDE-hosted DMS runtime contract for connecting to the
Docker-managed infrastructure after bootstrap has already completed schema provisioning in step 8.

The values below assume the default port mapping from `.env.example`.

Story 03 owns establishing the dev-only `CMSReadOnlyAccess` local contract used by IDE-hosted DMS to query
the Config Service. That work happens in the post-CMS-health, pre-DMS phase of the canonical bootstrap so
the IDE workflow does not depend on pre-existing local seed state. The IDE guidance and example file should
stay aligned with the bootstrap-managed local-development credential contract for that client, but the exact
secret value is an implementation detail rather than a story-level invariant.

| Variable (appsettings key) | Local value | Description |
|---|---|---|
| `ConnectionStrings__DatabaseConnection` | `host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;` | PostgreSQL connection. Uses `localhost:5435` (Docker-exposed port) instead of Docker-internal `dms-postgresql:5432`. |
| `ConfigurationServiceSettings__BaseUrl` | `http://localhost:8081` | Config Service URL. Uses `localhost:8081` instead of Docker-internal `http://dms-config-service:8081`. |
| `ConfigurationServiceSettings__ClientId` | `CMSReadOnlyAccess` | Bootstrap-provisioned read-only OAuth client ID that DMS uses to authenticate against the Config Service during local development. |
| `ConfigurationServiceSettings__ClientSecret` | `<bootstrap-provisioned-local-secret>` | Local-development secret for `CMSReadOnlyAccess`, taken from the bootstrap-managed IDE guidance or local provisioning output. **DEV-ONLY**: This localhost credential must not be reused in shared, remote, or production environments. |
| `ConfigurationServiceSettings__Scope` | `edfi_admin_api/readonly_access` | OAuth scope for Config Service read access. |
| `AppSettings__AuthenticationService` | `http://localhost:8081/connect/token` (self-contained) or `http://localhost:8045/realms/edfi/protocol/openid-connect/token` (Keycloak) | Token endpoint must match the selected `-IdentityProvider`, using host-reachable URLs rather than Docker-internal addresses. |
| `JwtAuthentication__Authority` | `http://localhost:8081` (self-contained) or `http://localhost:8045/realms/edfi` (Keycloak) | JWT authority for token validation, translated to host-local endpoints for IDE debugging. |
| `JwtAuthentication__MetadataAddress` | `http://localhost:8081/.well-known/openid-configuration` (self-contained) or `http://localhost:8045/realms/edfi/.well-known/openid-configuration` (Keycloak) | OIDC discovery document URL for the selected identity provider. |
| `AppSettings__UseApiSchemaPath` | `true` | Required for IDE-hosted DMS so it reads the staged schema workspace instead of falling back to the default packaged schema input. |
| `AppSettings__ApiSchemaPath` | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` | Host path to the staged schema workspace created by bootstrap. This must point at the same staged files used for `dms-schema hash` and for Docker-hosted DMS runs. |
| `AppSettings__DeployDatabaseOnStartup` | `false` | Keep the committed default at `false`. Bootstrap provisions schema directly in step 8 and does not rely on DMS startup side effects in the IDE path. |
| `Serilog__MinimumLevel__Default` | `Debug` | Log verbosity for local development. |

For Kafka-based event streaming (if enabled), the Kafka bootstrap server must be configured to `localhost:9092` rather than the Docker-internal broker address.

**Optional diagnostic setting:** If a developer intentionally runs SchemaTools manually outside the normal
bootstrap path, they may also define an admin-capable connection string for that separate tooling step. It
is not part of the normal IDE-hosted DMS runtime contract because this design keeps
`AppSettings__DeployDatabaseOnStartup=false` and performs schema provisioning before DMS starts.

These values can be placed in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.Development.json` (git-ignored) so they are picked up automatically by the ASP.NET Core configuration system in the `Development` environment without modifying the committed `appsettings.json`. A starter example lives at `reference/design/bootstrap/appsettings.Development.json.example`:

```json
{
  "ConnectionStrings": {
    "DatabaseConnection": "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"
  },
  "ConfigurationServiceSettings": {
    "BaseUrl": "http://localhost:8081",
    "ClientId": "CMSReadOnlyAccess",
    "ClientSecret": "<bootstrap-provisioned-local-secret>",
    "Scope": "edfi_admin_api/readonly_access"
  },
  "AppSettings": {
    "UseApiSchemaPath": true,
    "ApiSchemaPath": "<repo-root>/eng/docker-compose/.bootstrap/ApiSchema",
    "DeployDatabaseOnStartup": false,
    "AuthenticationService": "http://localhost:8081/connect/token"
  },
  "JwtAuthentication": {
    "Authority": "http://localhost:8081",
    "MetadataAddress": "http://localhost:8081/.well-known/openid-configuration"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

This starter file shows the common single-instance IDE workflow and therefore uses the non-qualified
self-contained token endpoint. It is intentionally not a multi-year template. Replace `<repo-root>` with the
native absolute path for the host platform running the IDE, for example a Windows path, `/Users/...`, or
`/home/...`.

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
containerized DMS at `http://localhost:8080`. When DMS runs in an IDE, that phase can either stop after the
pre-DMS phase (`-InfraOnly`) or continue bootstrap against the IDE process (`-InfraOnly -DmsBaseUrl ...`).
The broader bootstrap contract remains the composable phase sequence defined in
[`command-boundaries.md`](command-boundaries.md); `start-local-dms.ps1` owns infrastructure lifecycle only.

**Continuation rule**: Instance creation and schema provisioning are Config Service / database concerns and
do not require DMS to be running. When a run chooses `-DmsBaseUrl`, `start-local-dms.ps1` automatically
waits for the IDE-hosted DMS process to become healthy before any post-start work begins. The bootstrap
script cannot start the IDE process - the developer must start DMS in the IDE before or during that health
wait window. Optional smoke-test credentials remain in the pre-DMS phase and do not depend on this wait.
DMS-916 intentionally does **not** define a stopped-then-resume second bootstrap invocation. `-InfraOnly`
without `-DmsBaseUrl` is a terminal pre-DMS preparation shape for manual IDE startup against an already
prepared environment, not a checkpoint from which a later bootstrap run picks up unfinished DMS-dependent
work. Any run that intends SeedLoader credential bootstrap or seed loading must declare that up front via
`-InfraOnly -DmsBaseUrl`.

**Proposed mechanism**: Add a `-DmsBaseUrl` parameter to `start-local-dms.ps1` that selects the external
IDE-hosted DMS endpoint used during the `-InfraOnly` continuation flow:

```powershell
# Developer runs bootstrap against an IDE-hosted DMS endpoint.
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly -AddSmokeTestCredentials `
    -DmsBaseUrl "http://localhost:5198"
pwsh eng/docker-compose/load-dms-seed-data.ps1 -LoadSeedData
```

When `-InfraOnly` is used **without** `-DmsBaseUrl`, the script:

1. Starts Docker infrastructure (PostgreSQL, Kafka, Config Service) but not the DMS container.
2. Waits for Config Service readiness. In DMS-916 this means the service is healthy and startup claim
   loading for the selected staged inputs has completed.
3. Creates the target DMS instances in CMS, or - when `-NoDmsInstance` is set - reuses the one
   existing target already present in the current tenant scope. This rerun escape hatch is valid
   only when exactly one instance exists and `-SchoolYearRange` is not set; otherwise bootstrap
   fails and requires teardown or manual environment preparation before rerunning.
4. Optionally creates smoke-test credentials through CMS using that selected target set when
   `-AddSmokeTestCredentials` is requested.
5. Provisions or validates the target databases directly through SchemaTools.
6. Provisions or validates the fixed dev-only `CMSReadOnlyAccess` client used by IDE-hosted DMS.
7. Stops with next-step guidance. This pre-DMS-only variant never performs DMS health wait,
   SeedLoader credential creation, or seed loading. If `-LoadSeedData` was requested without
   `-DmsBaseUrl`, bootstrap fails during parameter validation rather than deferring seed loading to
   an implicit later continuation.

A later bootstrap invocation may still be used to bring supporting infrastructure back up for an already
bootstrapped environment, but that is a new availability run, not a resume of unfinished step-9-to-step-11
work from the earlier stopped invocation.

When `-DmsBaseUrl` is provided alongside `-InfraOnly`, the script continues from that same pre-DMS phase
into the external-endpoint phase:

1. Starts Docker infrastructure (PostgreSQL, Kafka, Config Service) but not the DMS container.
2. Waits for Config Service readiness. In DMS-916 this means the service is healthy and startup claim
   loading for the selected staged inputs has completed.
3. Creates the target DMS instances in CMS, or - when `-NoDmsInstance` is set - reuses the one
   existing target already present in the current tenant scope. This rerun escape hatch is valid
   only when exactly one instance exists and `-SchoolYearRange` is not set; otherwise bootstrap
   fails and requires teardown or manual environment preparation before rerunning.
4. Optionally creates smoke-test credentials through CMS using that selected target set when
   `-AddSmokeTestCredentials` is requested.
5. Provisions or validates the target databases directly through SchemaTools.
6. Provisions or validates the fixed dev-only `CMSReadOnlyAccess` client used by IDE-hosted DMS.
7. Automatically waits for the developer-started DMS process to become healthy at `$DmsBaseUrl` by polling
   the health endpoint at a configurable interval with a maximum timeout. Success is defined as an HTTP
   200 response from the health endpoint; timeout or persistent non-success is fatal for the run.
8. When `-LoadSeedData` is set, provisions SeedLoader credentials and runs seed data loading targeting
   `$DmsBaseUrl` and the same target instance set selected earlier in the run; the continuation path does
   not call `Get-DmsInstances` again.

**Debugging during and after bootstrap**: IDE debugging must work both *during* bootstrap (the DMS process receives live API calls as seed data loads, exercising the full request path under the debugger) and *after* bootstrap (subsequent debug sessions reuse an already-bootstrapped environment without re-running seed loading). The `-DmsBaseUrl` mechanism satisfies both scenarios:

- *During*: Developer starts DMS in IDE, attaches the debugger, then runs bootstrap with `-DmsBaseUrl`. Breakpoints in request handlers fire as seed data POST calls arrive.
- *After*: Subsequent sessions can use `-InfraOnly` to bring up the Docker-managed infrastructure, then
  start DMS in the IDE against the already-bootstrapped databases without re-running seed loading. This is a
  fresh infrastructure bring-up for an already-complete bootstrap state, not a resume of skipped step-10 or
  step-11 work from an earlier pre-DMS-only invocation.

No changes to DMS runtime behavior are needed for this workflow. The changes stay in the bootstrap surface
and developer guidance rather than introducing a second application architecture.

## 13. Companion Implementation Stories

This section breaks the design into four companion implementation stories. It is intentionally not a
prescribed multi-ticket rollout plan. DMS-916 is satisfied when the acceptance criteria in Section 14.1 are
implemented without adding extra bootstrap responsibilities. Teams may merge, split, or reorder work items as
needed.
Companion implementation-ready story definitions for these four slices live in
[`tickets/INDEX.md`](tickets/INDEX.md).

### 13.0 Story-Aligned Implementation Map

| Slice | Companion story definition | Story outcome |
|-------|----------------------------|---------------|
| Schema and security selection | [`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md) | `-Extensions`, `-ApiSchemaPath`, and `-ClaimsDirectoryPath` resolve one schema/security story, with v1 support limited to extensions backed by the current schema and security artifacts. |
| Schema deployment safety | [`tickets/01-schema-deployment-safety.md`](tickets/01-schema-deployment-safety.md) | Bootstrap invokes the authoritative SchemaTools/runtime-owned provisioning and validation path over the staged schema set before DMS starts. `EffectiveSchemaHash` remains a shared input to that path, and different extension selections remain different physical-schema targets. |
| API-based seed delivery | [`tickets/02-api-seed-delivery.md`](tickets/02-api-seed-delivery.md) | Built-in seed packages remain deterministic, and custom directories load through the repo-pinned BulkLoadClient path using the run's staged schema/security inputs, dedicated `SeedLoader` credentials, and the existing `-SchoolYearRange` developer workflow when present. |
| Entry point and IDE workflow | [`tickets/03-entry-point-and-ide-workflow.md`](tickets/03-entry-point-and-ide-workflow.md) | `start-local-dms.ps1` is the infrastructure-lifecycle phase command, while the normative bootstrap contract remains the composable phase-command set. `-InfraOnly` / `-DmsBaseUrl` define the two IDE-hosted infrastructure workflow shapes: stop after the pre-DMS phase, or automatically health-wait and continue against the external endpoint. |

**Cross-story dependency notes**

- Story 00 may deliver schema/security selection independently, but no extension mapping entry may advertise
  built-in seed support until Story 02 adds the top-level `SeedLoader` claim set to embedded `Claims.json`
  and the matching extension fragment supplies the required `SeedLoader` permissions.
- Story 03 owns the repo-local `.bootstrap/` workspace hygiene and the user-facing migration note for the
  narrowed `-NoDmsInstance` contract.

### 13.1 Explicitly Out of Scope for DMS-916

- Optional post-bootstrap orchestration such as smoke, E2E, or integration test runners, and SDK generation.
- A persisted bootstrap state-file control plane. The design uses live environment inspection instead.
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
> that design in front of a developer is in place today; rows still waiting on Story 00–03 implementation
> work read as "Designed, implementation pending" rather than as fully achieved. "Blocking dependency /
> gap" names the specific pending item (story, ticket, or cross-team dependency) so a stakeholder can
> trace each non-ready row to its owner without re-reading Sections 14.2 and 14.3.

| Ticket acceptance criterion | Evidence in this document | Design completeness | Delivery readiness | Blocking dependency / gap |
|-----------------------------|---------------------------|---------------------|--------------------|---------------------------|
| ApiSchema.json selection - how developers choose core, extensions, or custom path | Sections 3.3, 8.2, 8.4, 9.3 | Designed | Designed, implementation pending | [`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md) |
| Security database configuration from ApiSchema.json | Sections 4.3-4.5, 9.3 (`-ClaimsDirectoryPath`) | Designed | Designed, implementation pending. Standard `-Extensions` mode covers automatic schema-and-security selection; expert `-ApiSchemaPath` mode requires explicit `-ClaimsDirectoryPath` companion input when non-core schemas are present. | [`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md) |
| Database schema provisioning - DDL hook separated from seed data loading, driven by selected ApiSchema.json | Sections 3.2, 5 step 8, 11.3-11.5 | Designed under the strong interpretation above. Selected schema drives the DDL target/version/`EffectiveSchemaHash` validation path and the exact physical schema provisioned for that run. | Designed, implementation pending. Bootstrap delegates to the SchemaTools / runtime-owned provisioning path; readiness is gated on that surface remaining stable. | [`tickets/01-schema-deployment-safety.md`](tickets/01-schema-deployment-safety.md); SchemaTools dependency in Section 14.3 |
| Sample data loading - API-based JSON/JSONL loading replacing direct SQL, with Ed-Fi packages or developer-supplied JSONL directories paired with compatible schema/security inputs | Section 6 | Designed | Not deliverable end to end today | ODS-6738 (BulkLoadClient JSONL) and DMS-1119 (seed artifact packages); see Sections 14.2 and 14.3 |
| Extension selection - parameterized `-Extensions` flag driving schema and security automatically, and driving built-in seed data automatically only where the extension mapping defines built-in seed support | Sections 3.3, 8.2-8.4 | Designed | Designed, implementation pending. The combined `-Extensions` plus `-ClaimsDirectoryPath` additive-claims staging path is not yet implemented. | [`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md); see Section 14.2 |
| Credential bootstrapping - enhancements for seed data loading support | Section 7 | Designed | Designed, implementation pending. The `SeedLoader` claim set is not yet defined in the embedded CMS claims resource. | [`tickets/02-api-seed-delivery.md`](tickets/02-api-seed-delivery.md); see Section 14.2 |
| Bootstrap entry point and safe skip behavior — composable phase commands with optional same-invocation continuation | Sections 1, 9, 9.2–9.5 | Designed. The normative contract is the composable phase commands in `command-boundaries.md`; any thin wrapper is convenience only. "Skip/resume" means safe skip behavior across phase commands plus optional same-invocation continuation via `-InfraOnly -DmsBaseUrl`, not a persisted resume model. | Designed, implementation pending across the phase commands and the optional thin wrapper. | [`tickets/03-entry-point-and-ide-workflow.md`](tickets/03-entry-point-and-ide-workflow.md) and the phase-command implementation tickets |
| IDE debugging workflow - running DMS in IDE against Docker infrastructure | Section 12 | Designed | Designed, implementation pending. `-InfraOnly` and the `-DmsBaseUrl` continuation behavior are not yet implemented in `start-local-dms.ps1`. | [`tickets/03-entry-point-and-ide-workflow.md`](tickets/03-entry-point-and-ide-workflow.md); see Section 14.2 |
| Backend redesign awareness - forward-compatible with relational tables replacing JSONB | Section 11 | Designed | Achieved as a forward-compatibility property of this design; no separate implementation deliverable is required beyond honoring the SchemaTools provisioning boundary. | None within this story; tracked as the SchemaTools cross-team dependency in Section 14.3. |
| ODS initdev audit - informational reference only, not a gap-fill checklist | Sections 1-2 and `reference-initdev-workflow.md` | Designed | Achieved as informational reference; no implementation deliverable. | None |

### 14.2 Operationally Blocked Criteria

| Capability | Blocking condition | Owning companion story / dependency |
|---|---|---|
| API-based seed data loading (`-LoadSeedData` via BulkLoadClient) | BulkLoadClient does not yet support `--input-format jsonl` or `--data <directory>` | ODS-6738 (concrete ODS-team implementation dependency) |
| Seed-loader credential bootstrap (`SeedLoader` claim set) | `SeedLoader` claim set is not yet defined in the embedded CMS claims resource (`src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Claims.json`) | [`tickets/02-api-seed-delivery.md`](tickets/02-api-seed-delivery.md) |
| Built-in seed package resolution (`-SeedTemplate` and any future built-in extension seed packages) | The required published JSONL seed artifacts and their package coordinates are not yet available as a stable bootstrap dependency | DMS-1119 or the follow-on artifact-delivery work that publishes those seed packages |
| Additive extension + explicit-claims staging | The combined `-Extensions` / `-ClaimsDirectoryPath` staging path is not yet implemented | [`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md) |
| `-InfraOnly` flag for IDE debugging | Not yet implemented in `start-local-dms.ps1` | [`tickets/03-entry-point-and-ide-workflow.md`](tickets/03-entry-point-and-ide-workflow.md) |

### 14.3 Blocking Cross-Team Dependencies

| Item | External owner | Unblocking action |
|---|---|---|
| `--input-format jsonl` support in BulkLoadClient | ODS team | ODS-6738: extend `EdFi.BulkLoadClient` with JSONL mode. The bootstrap consumption contract is in [Section 6.1](#61-bulkloadclient-bootstrap-consumption-contract). This dependency must land before DMS-916 can deliver the intended direct-SQL replacement described in [`tickets/02-api-seed-delivery.md`](tickets/02-api-seed-delivery.md). |
| SchemaTools provisioning contract for the relational backend | DMS backend redesign team | The v1 bootstrap design assumes `dms-schema ddl provision` (or an equivalent runtime-owned provisioning surface) remains the authoritative pre-start provisioning and validation path over the staged schema set. If the backend team changes that surface, its inputs, or where final serviceability validation lives, step 8 must be re-pointed to the new authoritative path rather than preserving bootstrap-owned safety rules. |

Until these blockers are resolved, the intended API-based replacement path cannot be delivered end to end.
The design target remains replacement of the deprecated direct-SQL path (`setup-database-template.psm1`);
the blocker does not change that scope contract.

### 14.4 Close-Out Status

The bootstrap architecture documented here — composable phase commands, expert-mode security boundary,
schema-driven DDL provisioning, and the optional thin convenience wrapper — is **accepted as the normative
DMS-916 design**. Stakeholders may treat it as the contract that Stories 00–03 implement against.

**Design acceptance is not feature completion.** The non-seed items marked "Designed, implementation pending"
in Section 14.1 — additive extension plus explicit-claims staging, the `-InfraOnly` / `-DmsBaseUrl` IDE
workflow, the `SeedLoader` claim set, and the phase-command surface itself — are **not delivered today**
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
| 1 | Default schema profile when `-Extensions` is omitted | `SCHEMA_PACKAGES` staged Data Standard 5.2 **plus TPDM** by default in the existing `eng/docker-compose` flow | Bootstrap resolves and stages **core only** (`EdFi.DataStandard52.ApiSchema`) when `-Extensions` is omitted | Remove any assumption that TPDM artifacts are present by default; omit `-Extensions` for core-only runs |
| 2 | TPDM in the v1 `-Extensions` surface | TPDM was implied as a bundled default via the `SCHEMA_PACKAGES` environment variable | TPDM is **not a supported value** for `-Extensions` in the DMS-916 v1 surface; specifying it produces a fast-fail with a clear error message | Remove `-Extensions TPDM` from any scripts; do not assume TPDM support is available in the DMS-916 bootstrap path |
| 3 | `-NoDmsInstance` semantics | Generic "skip instance creation" switch used on fresh stacks as a convenient no-op | **Narrow rerun escape hatch only:** valid only when exactly one existing instance is present in the current tenant scope, and invalid with `-SchoolYearRange`; zero or multiple instances fail fast requiring teardown or manual preparation | Drop the flag on fresh-stack runs, or pre-create exactly one target instance before rerunning with `-NoDmsInstance` |
| 4 | Seed-loading parameter ownership | `-LoadSeedData`, `-SeedTemplate`, and `-SeedDataPath` were accepted directly by `start-local-dms.ps1` | These parameters are **owned by `load-dms-seed-data.ps1`**; `start-local-dms.ps1` no longer accepts them | Call `load-dms-seed-data.ps1` directly for seed loading, or use `bootstrap-local-dms.ps1 -ConfigFile` which orchestrates the phase commands including seed loading |
