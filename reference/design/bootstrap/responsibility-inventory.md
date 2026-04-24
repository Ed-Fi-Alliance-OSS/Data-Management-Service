# DMS-916 Bootstrap - Responsibility Inventory

**Basis:** `bootstrap-design.md` and companion Stories 00-03.

---

## 1. Purpose

This document assigns every major bootstrap concern to exactly one phase command in the composable
bootstrap design. Each concern is listed once under its owning phase, with a reference to the companion
design section or story that defines its acceptance criteria. Some higher-level story acceptance areas are
composite and therefore span more than one owning phase or documentation artifact; when that happens, the
table in Section 5 shows the contributing owners while the individual concern ownership remains singular.
[`command-boundaries.md`](command-boundaries.md) is the normative ownership contract for the DMS-916
bootstrap implementation; this document is a supporting inventory and dependency summary.

---

## 2. Ownership by Phase

### 2.1 Infrastructure Lifecycle - `start-local-dms.ps1`

Docker stack management and service health waiting. This is the only concern this script owns.

| Responsibility | Design reference |
|---|---|
| Start PostgreSQL container; wait for health | Section 5 |
| Start Keycloak container; wait for health (Keycloak path) | Section 5 |
| Run `setup-openiddict.ps1 -InitDb` after PostgreSQL is healthy (self-contained path) | Section 5 |
| Run `setup-openiddict.ps1 -InsertData` after Config Service is ready (self-contained path) | Section 5 |
| Start Config Service container; wait for readiness and claims-load completion | Section 5, Story 03 |
| Start DMS container (Docker flow); poll DMS health endpoint, fail on timeout | Section 5, Section 9 |
| Poll `$DmsBaseUrl/health` with timeout when `-DmsBaseUrl` is provided | Section 12, Story 03 |
| Accept `-d`/`-v` teardown flags; manage compose lifecycle | Section 9 |
| Accept `-InfraOnly` to exclude the DMS container from Docker startup | Section 12, Story 03 |

`-InfraOnly` and `-DmsBaseUrl` remain on this script because they control Docker-layer behavior
(whether DMS starts in a container and which health endpoint to poll). They are not schema or
continuation policy.

---

### 2.2 Schema Selection and Staging - `prepare-dms-schema.ps1`

Resolves the concrete `ApiSchema*.json` files for the run and produces the staged workspace that
every downstream phase depends on. Runs before any Docker service starts.

| Responsibility | Design reference |
|---|---|
| Validate `-Extensions` against the supported v1 mapping; fail fast on unrecognized names | Section 3.3, Story 00 |
| Validate mutual exclusion of `-Extensions` and `-ApiSchemaPath` | Section 3.3, Story 00 |
| Download core package host-side via `ApiSchemaDownloader` (Mode 1/2) | Section 3.3 |
| Download selected extension packages host-side (Mode 2) | Section 3.3, Story 00 |
| Copy and normalize developer-supplied `ApiSchema*.json` files into staged workspace (Mode 3) | Section 3.3, Story 00 |
| Validate staged set: exactly 1 core schema, 0..N extension schemas | Section 3.3, Story 00 |
| Detect staged workspace; reuse identical content, fail fast on mismatch | Section 3.3, Story 00 |
| Compute expected `EffectiveSchemaHash` via `dms-schema hash` over staged files | Section 3.3, Story 00 |
| Surface `dms-schema hash` diagnostics unchanged on non-zero exit | Section 3.3 |
| Fail fast with clear error when NuGet feed is unreachable; no partial downloads | Section 3.4 |
| Record that the run is in expert mode; may emit a warning when non-core schemas are staged | Section 3.3, Story 00 |
| Produce the staged schema workspace used directly by downstream consumers | Section 3.3 |

The staged workspace `eng/docker-compose/.bootstrap/ApiSchema/` is the sole schema source for
`dms-schema hash`, Docker-hosted DMS, and IDE-hosted DMS. No downstream phase re-resolves packages.

---

### 2.3 Claims and Security Staging - `prepare-dms-claims.ps1`

Stages `*-claimset.json` fragments into a workspace directory that the Config Service reads on startup.
Depends on the staged schema workspace from `prepare-dms-schema.ps1`; runs before the Config Service starts.
Standard-vs-expert mode behavior is defined normatively in
[`command-boundaries.md` Section 3.2](command-boundaries.md#32-prepare-dms-claimsps1). This file only
records ownership and dependencies.

| Responsibility | Design reference |
|---|---|
| Stage extension-derived claimset fragments into `eng/docker-compose/.bootstrap/claims/` | Section 4, Story 00 |
| Stage developer-supplied `-ClaimsDirectoryPath` fragments into the same workspace | Section 4, Story 00 |
| Detect staged claims workspace; reuse identical content, fail fast on mismatch | Section 4.4, Story 00 |
| Validate: no duplicate filenames in staged workspace | Section 4, Story 00 |
| Validate: no malformed JSON in staged fragments | Section 4, Story 00 |
| Validate: no unknown claim set names (must exist in embedded `Claims.json`) | Section 4, Story 00 |
| Derive `DMS_CONFIG_CLAIMS_SOURCE` (Embedded or Hybrid) from staged results | Section 4, Story 00 |
| Set `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` bind-mount variable for CMS | Section 4, Story 00 |
| Stage and validate the claims inputs that later built-in seed delivery depends on | Section 4, Story 00 |
| Treat legacy `-AddExtensionSecurityMetadata` as redundant when `-Extensions` or `-ClaimsDirectoryPath` is set; emit warning | Section 4, Story 00 |
| Enforce `-ClaimsDirectoryPath` requirement when expert mode (`-ApiSchemaPath`) is in use with non-core schemas; fail fast if missing | Section 4, [`command-boundaries.md` Section 3.2](command-boundaries.md#32-prepare-dms-claimsps1--claims-and-security-staging), Story 00 |

This phase has no dependency on Docker state at execution time. Its only schema input is the staged
workspace produced by `prepare-dms-schema.ps1`.

---

### 2.4 Authoritative Schema Provisioning - `provision-dms-schema.ps1`

Invokes the shared SchemaTools/runtime-owned path to provision or validate target databases.
Resolves target DMS instances via explicit `-InstanceId` or `-SchoolYear` selectors supplied by
the caller, or auto-selects when exactly one instance exists in CMS. Depends on the staged-schema
manifest from `prepare-dms-schema.ps1`. Runs before any DMS process serves requests.

| Responsibility | Design reference |
|---|---|
| Accept `-InstanceId <guid[]>` and `-SchoolYear <int[]>` selectors; auto-select when exactly one DMS instance exists in CMS; fail fast when zero or multiple match without an explicit selector | [`command-boundaries.md` §3.5](command-boundaries.md#35-provision-dms-schemaps1--authoritative-schema-provisioning), Story 01 |
| Collect target connection strings from the selected DMS instances | Section 11, Story 01 |
| Pass staged schema paths and expected `EffectiveSchemaHash` into the SchemaTools/runtime path | Section 11, Story 01 |
| Invoke the authoritative SchemaTools/runtime provisioning and validation path (unconditional) | Section 11, Story 01 |
| Surface SchemaTools diagnostics unchanged on non-zero exit; no bootstrap-owned repair | Section 11, Story 01 |
| Keep `AppSettings__DeployDatabaseOnStartup=false`; never route schema work through DMS startup | Section 11, Story 01 |
| Print IDE next-step guidance after infra-only completion, including staged schema path, `appsettings` values, and `CMSReadOnlyAccess` credentials | Section 12, Story 03 |

Selecting a different extension combination changes the required physical schema target. This phase
does not silently reuse a database provisioned for a different selected schema set.

---

### 2.5 Instance and Client Setup - `configure-local-dms-instance.ps1`

Configures DMS instances and the CMS client records that downstream phases and IDE-hosted DMS depend on.
Runs after the Config Service is ready.

| Responsibility | Design reference |
|---|---|
| Create DMS instance via `Add-DmsInstance` (default path) | Section 5, Story 03 |
| Create school-year DMS instances via `Add-DmsSchoolYearInstances` (school-year path) | Section 10, Story 03 |
| Implement narrow `-NoDmsInstance` reuse: proceed only when exactly one existing instance is present; fail on 0 or >1 | Section 9, Story 03 |
| Print selected instance IDs to stdout for the caller to consume; the thin wrapper may capture and forward them in-memory to downstream phases within the same invocation | Section 5, Story 03 |
| Provision or validate the bootstrap-time fixed `CMSReadOnlyAccess` client for IDE-hosted DMS | Section 12, Story 03 |
| Create smoke-test application via `Add-CmsClient` / `Add-Application` (`EdFiSandbox`) when `-AddSmokeTestCredentials` is set | Section 7, Story 03 |

This phase creates or confirms the DMS instance records that downstream phases target. Selected
instance IDs are printed to stdout; the thin wrapper may capture and forward them in-memory. When
phase commands are run separately, downstream phases resolve target instances through their own
explicit `-InstanceId` or `-SchoolYear` selectors via CMS-backed lookup, with single-match
auto-selection when exactly one instance exists and no selector is supplied.

---

### 2.6 Seed Delivery - `load-dms-seed-data.ps1`

Materializes JSONL files and invokes BulkLoadClient against a healthy DMS endpoint.
Depends on a live DMS process; creates `SeedLoader` credentials immediately before BulkLoadClient
invocation. Blocked on external dependencies: ODS-6738 (BulkLoadClient JSONL support) and
DMS-1119 (published seed artifacts). See Story 02 for the explicit blocker documentation.

| Responsibility | Design reference |
|---|---|
| Accept `-InstanceId <guid[]>` and `-SchoolYear <int[]>` selectors; auto-select when exactly one DMS instance exists in CMS; fail fast when zero or multiple match without an explicit selector | [`command-boundaries.md` §3.6](command-boundaries.md#36-load-dms-seed-dataps1--seed-delivery), Story 02 |
| Validate `-SeedTemplate` and `-SeedDataPath` mutual exclusion | Section 6, Story 02 |
| Validate that expert mode (`-ApiSchemaPath`) rejects `-SeedTemplate` | Section 6, Story 02 |
| Resolve repo-pinned BulkLoadClient package; fail fast if unavailable | Section 6, Story 02 |
| Resolve Ed-Fi-provided seed packages for `-SeedTemplate Minimal`/`Populated` | Section 6, Story 02 |
| Resolve extension seed packages from the v1 mapping (standard mode only) | Section 8, Story 02 |
| Emit informational warning when a selected extension has no built-in seed package | Section 8, Story 02 |
| Create `SeedLoader` application via `Add-CmsClient` / `Add-Application` | Section 7, Story 02 |
| Copy JSONL files into repo-local seed workspace; detect and reject filename collisions | Section 6, Story 02 |
| Invoke BulkLoadClient once per school-year with `--year`; derive token URL per iteration | Section 6, Story 02 |
| Surface terminal BulkLoadClient diagnostics unchanged; fail fast on non-zero exit | Section 6, Story 02 |
| Clean up seed workspace on success; retain on failure for debugging | Section 6, Story 02 |

`SeedLoader` credentials belong to this phase, not to the infrastructure or instance phases. They are
created immediately before BulkLoadClient invocation and are never reused for smoke-test purposes.

---

### 2.7 IDE Workflow - composition of existing phase commands plus `appsettings.Development.json.example` and printed guidance

Developer experience of running DMS in an IDE against Docker-managed infrastructure. The workflow is
not owned by a separate script; it is the composition of `prepare-dms-schema.ps1`,
`prepare-dms-claims.ps1`, `start-local-dms.ps1 -InfraOnly`, `configure-local-dms-instance.ps1`, and
`provision-dms-schema.ps1`, together with the `appsettings.Development.json.example` documentation
artifact and the next-step guidance printed by the provisioning phase. No phase command in this
list exists solely for the IDE flow; each is the same phase command used in the Docker-hosted
path, invoked in a shape (`-InfraOnly`) that leaves DMS off so the developer can run it from the
IDE.

| Responsibility | Design reference |
|---|---|
| `appsettings.Development.json.example` — illustrative guidance only; bootstrap does not generate or read it; the developer copies it to `appsettings.Development.json` and sets the staged schema path and secrets from printed provisioning output | Section 12, Story 03 |
| Printed next-step guidance from `provision-dms-schema.ps1` after infra-only completion, including localhost `CMSReadOnlyAccess` credentials and staged schema path | Section 12, Story 03 |
| `.gitignore` entry excluding `eng/docker-compose/.bootstrap/` from source control | Story 03 |

The developer stages schema (`prepare-dms-schema.ps1`), stages claims (`prepare-dms-claims.ps1`),
starts infrastructure (`start-local-dms.ps1 -InfraOnly`), provisions the database, then launches DMS
in the IDE pointing at the staged schema workspace and the Docker-managed PostgreSQL and Config Service.

---

## 3. Phase Sequence and Dependency Rules

Each phase runs only when its required inputs are ready. No phase requires the availability of services
it does not consume.

| Phase | Earliest it can run | Depends on |
|---|---|---|
| `prepare-dms-schema.ps1` | Immediately; no services needed | NuGet feed or local path only |
| `prepare-dms-claims.ps1` | After `prepare-dms-schema.ps1` | Staged-schema manifest |
| `start-local-dms.ps1 -InfraOnly` | After schema and claims are staged | Staged claims workspace for CMS mount |
| `configure-local-dms-instance.ps1` | After Config Service is claims-ready | CMS HTTP API |
| `provision-dms-schema.ps1` | After at least one DMS instance exists in CMS | Resolvable DMS instance(s) in CMS (explicit `-InstanceId`/`-SchoolYear` or single-match auto-selection); staged-schema manifest |
| `start-local-dms.ps1` (DMS start) | After provisioning completes | Provisioned databases |
| `load-dms-seed-data.ps1` | After DMS is healthy | Live DMS HTTP API and `SeedLoader` credentials |

This ordering means each phase can be invoked explicitly, re-run independently on failure, and tested
without the full stack being live.

---

## 4. Phase Command to Parameter Mapping

See [`command-boundaries.md` Section 6](command-boundaries.md#6-parameter-surface-by-owner) for the authoritative per-phase parameter surface.

---

## 5. Acceptance Coverage

Every DMS-916 story acceptance area maps to one or more phase commands or documentation artifacts. Read
this table as a design-ownership map, not as a delivery-status report; delivery state remains tracked in
`bootstrap-design.md` Section 14.

| Story acceptance area | Phase owner |
|---|---|
| ApiSchema.json selection | `prepare-dms-schema.ps1` (Section 2.2) |
| Security database configuration from ApiSchema.json | `prepare-dms-claims.ps1` (Section 2.3) |
| Database schema provisioning | `provision-dms-schema.ps1` (Section 2.4) |
| Sample data loading | `load-dms-seed-data.ps1` (Section 2.6) |
| Extension selection | `prepare-dms-schema.ps1` + `prepare-dms-claims.ps1` (Sections 2.2, 2.3) |
| Credential bootstrapping | `configure-local-dms-instance.ps1` (smoke-test), `load-dms-seed-data.ps1` (`SeedLoader`) |
| Single entry point / skip-resume | Phase commands are the normative contract; `bootstrap-local-dms.ps1` is an optional thin wrapper, and `start-local-dms.ps1 -InfraOnly` provides the same-invocation IDE continuation. See `command-boundaries.md` Section 3.7 and `bootstrap-design.md` Section 9. |
| IDE debugging workflow | Composition of `start-local-dms.ps1 -InfraOnly` (infra-only shape), `prepare-dms-schema.ps1` and `prepare-dms-claims.ps1` (staged workspaces the IDE process reads), `configure-local-dms-instance.ps1` (instance plus `CMSReadOnlyAccess` for the IDE process), `provision-dms-schema.ps1` (databases plus printed next-step guidance), and `appsettings.Development.json.example` (Section 2.7). No additional script or control plane is introduced. |
| Backend redesign awareness | `provision-dms-schema.ps1` via SchemaTools plus `EffectiveSchemaHash` |
| ODS initdev audit | Informational reference only; no phase required |

