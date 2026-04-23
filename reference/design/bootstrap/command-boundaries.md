# DMS-916 Bootstrap — Command Boundaries

**Basis:** `bootstrap-design.md` Sections 3-12.
`responsibility-inventory.md` is a supporting summary and must not restate this file as a competing
ownership contract.

---

## 1. Purpose

This document states the normative ownership contract for each phase command in the composable
bootstrap design. Each command owns exactly one primary concern. The phase-oriented commands are
the normative bootstrap contract; any thin wrapper is convenience packaging over them.

---

## 2. Normative Contract Statement

> **The composable phase commands are the authoritative bootstrap contract for DMS-916.**
>
> Any wrapper over those commands is a convenience entry point for the happy path only. The wrapper
> is not the source of lifecycle semantics, schema policy, claims logic, provisioning behavior, or
> any other phase-specific concern. Wrapper behavior changes do not change the normative contract.

---

## 3. Phase Commands

### 3.1 `prepare-dms-schema.ps1` — Schema Selection and Staging

**Primary concern:** Resolve and stage the concrete `ApiSchema*.json` file set for the run.

| Item | Detail |
|---|---|
| **Preconditions** | NuGet feed reachable (Mode 1/2) or local path supplied (Mode 3). No Docker services required. |
| **Inputs** | `-Extensions <name>` (0..N, standard names only); `-ApiSchemaPath <path>` (Mode 3 expert path, mutually exclusive with `-Extensions`) |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/ApiSchema/` containing resolved schema files; the staged workspace itself is the downstream schema contract consumed by all later phases |
| **Side effects** | Writes staged workspace; computes and records expected `EffectiveSchemaHash` via `dms-schema hash` |
| **Failure conditions** | Unrecognized extension name; `-Extensions` and `-ApiSchemaPath` both supplied; NuGet feed unreachable; staged workspace exists with different content; `dms-schema hash` exits non-zero; fewer or more than 1 core schema present after staging |
| **Must NOT do** | Start or depend on Docker services; modify `.env` or Docker Compose variables; perform DDL work; contact the Config Service; accept claims-related parameters |

**Mode-to-security contract (precise):** In standard mode (`-Extensions`, including the omitted-`-Extensions` core-only case), schema selection here automatically determines the matching security fragment set that `prepare-dms-claims.ps1` will stage. In expert mode (`-ApiSchemaPath`), no such automatic security derivation occurs; the caller must supply explicit security input via `-ClaimsDirectoryPath` whenever the staged schema set includes non-core schemas. This phase command only records which mode is in effect; the actual claims-staging requirement is enforced by `prepare-dms-claims.ps1`.

**Boundary note:** Mode 3 (`-ApiSchemaPath`) emits an expert-mode warning and requires `-ClaimsDirectoryPath` when non-core schemas are present — but that requirement is enforced by `prepare-dms-claims.ps1` at claim-staging time, not here. This command validates schema inputs only.

---

### 3.2 `prepare-dms-claims.ps1` — Claims and Security Staging

**Primary concern:** Stage `*-claimset.json` fragments into the workspace that the Config Service reads on startup.

| Item | Detail |
|---|---|
| **Preconditions** | Staged-schema manifest produced by `prepare-dms-schema.ps1`. No Docker services required. |
| **Inputs** | `-ClaimsDirectoryPath <path>` (optional; required when Mode 3 is in use with non-core schemas); `-AddExtensionSecurityMetadata` (legacy compat flag, deprecated when `-Extensions` or `-ClaimsDirectoryPath` is set) |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/claims/` containing claimset fragments; derived `DMS_CONFIG_CLAIMS_SOURCE` and `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` values for the Config Service |
| **Side effects** | Writes staged claims workspace; validates JSON well-formedness, no duplicate filenames, and no unknown claim set names |
| **Failure conditions** | Duplicate filenames; malformed JSON in any fragment; unknown claim set name; staged workspace exists with different content; Mode 3 with non-core schemas and no `-ClaimsDirectoryPath` |
| **Must NOT do** | Contact Docker, the database, or the Config Service; perform schema resolution or hash computation; accept schema-selection parameters |

**Mode-to-security contract (precise):** Standard `-Extensions` mode (including the omitted-`-Extensions` core-only case) is the only mode in which this command derives the staged claims set automatically from the schema selection recorded by `prepare-dms-schema.ps1`. Expert `-ApiSchemaPath` mode is not auto-derived: when the staged schema set includes any non-core schema, this command requires explicit `-ClaimsDirectoryPath` input and fails fast if it is missing. Core-only `-ApiSchemaPath` runs may rely on embedded claims only. This is the single point of truth for "automatic vs explicit" in claims staging; nothing later in the pipeline retro-fits expert-mode security defaults.

**Boundary note:** Claim-fragment validation here is structural only: JSON shape, duplicate filenames, and claim-set-name references. This phase does not inspect attachment overlap, reject duplicate `(resource claim, claim set name)` pairs, or perform semantic composition reasoning; CMS startup remains the authoritative composition gate. Built-in seed-support advertisement is owned by Story 02 / `load-dms-seed-data.ps1`; this phase only stages and validates the claims inputs that later seed delivery depends on.

---

### 3.3 `start-local-dms.ps1` — Infrastructure Lifecycle

**Primary concern:** Docker stack management and service health waiting.

| Item | Detail |
|---|---|
| **Preconditions** | Staged claims workspace (`eng/docker-compose/.bootstrap/claims/`) present when CMS is included (normal flow). |
| **Inputs** | `-InfraOnly` (exclude DMS container from Docker startup); `-DmsBaseUrl <url>` (health endpoint of IDE-hosted DMS; valid only with `-InfraOnly`); `-Rebuild` / `-r`; `-IdentityProvider`; `-EnableConfig` (legacy compat, not a meaningful opt-out in the normative flow); teardown flags `-d`/`-v` |
| **Outputs** | Running Docker services; healthy Config Service; healthy DMS container (non-`-InfraOnly` path) |
| **Side effects** | Docker Compose up/down; calls `setup-openiddict.ps1 -InitDb` after PostgreSQL health; calls `setup-openiddict.ps1 -InsertData` after Config Service readiness (self-contained path); polls `$DmsBaseUrl/health` with timeout when `-DmsBaseUrl` is provided |
| **Failure conditions** | Docker compose start failure; health-wait timeout for any service; `-DmsBaseUrl` health-wait timeout |
| **Must NOT do** | Resolve or validate ApiSchema files; inspect or write the staged-schema or staged-claims workspace; provision databases; configure DMS instances; create CMS clients; load seed data; accept schema or claims parameters |

**Boundary note:** `-InfraOnly` and `-DmsBaseUrl` are Docker-layer controls — they decide whether and which DMS health endpoint to poll. They do not express schema selection, continuation policy, or any concern owned by another phase.

---

### 3.4 `configure-local-dms-instance.ps1` — Instance and Client Setup

**Primary concern:** Configure DMS instances and CMS client records that downstream phases and IDE-hosted DMS depend on.

| Item | Detail |
|---|---|
| **Preconditions** | Config Service healthy and claims-loaded (Docker service ready). |
| **Inputs** | `-NoDmsInstance` (narrow reuse escape hatch: valid only when exactly one existing instance is present); `-SchoolYearRange <range>` (school-year path); `-AddSmokeTestCredentials` (creates CMS-only test application) |
| **Outputs** | One or more DMS instance records in CMS; `CMSReadOnlyAccess` client record for IDE-hosted DMS; `EdFiSandbox` application when `-AddSmokeTestCredentials` is set; repo-local run-context file `eng/docker-compose/.bootstrap/run-context.json` containing the resolved instance ID set and downstream connection metadata for the current run |
| **Side effects** | CMS API calls to `Add-DmsInstance` / `Add-DmsSchoolYearInstances`; writes `eng/docker-compose/.bootstrap/run-context.json` for later phases; writes `CMSReadOnlyAccess` client credentials to printed output and/or `appsettings.Development.json` guidance |
| **Failure conditions** | Config Service unreachable; `-NoDmsInstance` with 0 or >1 existing instances; `-NoDmsInstance` with `-SchoolYearRange` (invalid combination) |
| **Must NOT do** | Create `SeedLoader` credentials (those belong to `load-dms-seed-data.ps1`); perform DDL work; re-query CMS for instance IDs in later phases (this is the one and only resolution phase for the run); accept schema or claims parameters |

**Boundary note:** This is the only phase permitted to resolve target DMS instance IDs. All later phases consume the selected instance set from `eng/docker-compose/.bootstrap/run-context.json` for the current run. At minimum, that JSON handoff records the selected instance IDs, the connection details later phases require, and whether the run is single-instance or school-year-qualified. No subsequent phase may perform a second CMS discovery pass.

---

### 3.5 `provision-dms-schema.ps1` — Authoritative Schema Provisioning

**Primary concern:** Invoke the SchemaTools/runtime-owned path to provision or validate target databases.

| Item | Detail |
|---|---|
| **Preconditions** | Instance IDs and connection strings from `eng/docker-compose/.bootstrap/run-context.json` written by `configure-local-dms-instance.ps1`; staged schema workspace and expected `EffectiveSchemaHash` from `prepare-dms-schema.ps1`; Config Service and PostgreSQL reachable. |
| **Inputs** | Instance connection strings (derived from `eng/docker-compose/.bootstrap/run-context.json`, not user-supplied directly); staged schema paths (read from `eng/docker-compose/.bootstrap/ApiSchema/`) |
| **Outputs** | Provisioned or validated databases for each target instance; printed IDE next-step guidance (staged schema path, `appsettings` values, `CMSReadOnlyAccess` credentials) after infra-only shape completes |
| **Side effects** | Invokes authoritative SchemaTools/runtime provisioning path; exits non-zero if provisioning or validation fails |
| **Failure conditions** | SchemaTools/runtime provisioning exits non-zero; stored `EffectiveSchemaHash` mismatches expected hash for a target instance; connection to target database fails |
| **Must NOT do** | Accept user-facing schema-selection parameters; repair or work around a failed SchemaTools path; run inside DMS startup via `AppSettings__DeployDatabaseOnStartup`; silently reuse a database provisioned for a different schema selection; resolve schema files; query CMS |

**Boundary note:** `AppSettings__DeployDatabaseOnStartup=false` is always set. Schema provisioning is entirely owned by this phase; DMS startup never performs it.

---

### 3.6 `load-dms-seed-data.ps1` — Seed Delivery

**Primary concern:** Materialize JSONL files and invoke BulkLoadClient against a healthy DMS endpoint.

| Item | Detail |
|---|---|
| **Preconditions** | Live DMS process healthy (`/health` returns 200); CMS remains reachable so this phase can create `SeedLoader` credentials immediately before BulkLoadClient invocation. Blocked externally: ODS-6738 (BulkLoadClient JSONL support) and DMS-1119 (published seed artifacts). See Story 02. |
| **Inputs** | `-LoadSeedData`; `-SeedTemplate Minimal\|Populated` (mutually exclusive with `-SeedDataPath`); `-SeedDataPath <path>` (custom JSONL); `-SchoolYearRange <range>` (multi-year iteration) |
| **Outputs** | Seeded DMS instance(s); seed workspace cleaned up on success |
| **Side effects** | Creates `SeedLoader` application via `Add-CmsClient` / `Add-Application`; resolves BulkLoadClient package; copies JSONL into seed workspace; invokes BulkLoadClient once per school year; retains seed workspace on failure |
| **Failure conditions** | `-SeedTemplate` with Mode 3 (`-ApiSchemaPath`) run; `-SeedTemplate` and `-SeedDataPath` both supplied; BulkLoadClient exits non-zero; seed package unavailable; DMS health endpoint unreachable; filename collisions in seed workspace |
| **Must NOT do** | Create `CMSReadOnlyAccess` or smoke-test credentials (those belong to `configure-local-dms-instance.ps1`); reuse `SeedLoader` credentials for smoke tests; perform DDL work; accept schema or claims parameters |

**Boundary note:** Story 02 blockers (ODS-6738, DMS-1119) prevent end-to-end delivery. This phase is designed and documented as blocked-but-ready. The design does not normalize the legacy direct-SQL path as the target state.

---

### 3.7 `bootstrap-local-dms.ps1` — Thin Convenience Wrapper (Optional)

**Delivery status:** Convenience packaging only. The wrapper is optional, may forward `-ConfigFile`, and owns no policy. The composable phase commands remain the authoritative bootstrap contract for DMS-916.

**Primary concern:** Sequence the above phase commands in the correct order for the common happy path.

| Item | Detail |
|---|---|
| **Preconditions** | None additional beyond what phase commands require. |
| **Inputs** | `-ConfigFile <path>` (optional; path to a JSON defaults file). Each phase command reads its own relevant keys from that file directly. The wrapper does not extract or re-route individual parameter values. |
| **Outputs** | Delegated entirely to the phase commands it calls |
| **Side effects** | Delegates to phase commands; prints next-step guidance when a phase is intentionally omitted |
| **Failure conditions** | Propagates non-zero exit from any called phase command; fails fast if `-ConfigFile` is supplied but the file is not valid JSON |
| **Must NOT do** | Extract individual parameter values from the config file and forward them by name to phase commands; own schema logic; perform claims parsing; inspect database state; synthesize credentials; implement retry or continuation policy; absorb any concern owned by a phase command |

**Boundary note:** The wrapper sequences phase commands, may forward `-ConfigFile` unchanged, and may print next-step guidance. It never owns policy, schema or claims logic, credential behavior, or per-phase parameter handling. Validation ownership is precise: the wrapper may validate only that the file is well-formed JSON. Each phase consumes only the keys it owns, and a phase command invoked directly with `-ConfigFile` must not reject keys owned by other phases. A lightweight example key list may be documented as a reader aid, but it is illustrative rather than a second config-schema contract.

---

## 4. Dependency Chain

```
prepare-dms-schema.ps1
  -> prepare-dms-claims.ps1
       -> start-local-dms.ps1 -InfraOnly  (starts PostgreSQL, Keycloak/OpenIddict, Config Service)
            -> configure-local-dms-instance.ps1  (CMS HTTP API ready)
                 -> provision-dms-schema.ps1  (instance IDs + staged schema)
                      -> start-local-dms.ps1  (starts DMS container; or IDE-hosted DMS starts here)
                           -> load-dms-seed-data.ps1  (live DMS + SeedLoader credentials)
```

Each phase begins only when all of its required inputs are ready. No phase polls for or waits on
services it does not consume. Phases can be invoked individually for re-runs, debugging, and testing
without re-executing the full chain.

---

## 5. Non-Overlap Guarantees

The following concerns are each owned by exactly one phase:

| Concern | Owner | All other phases must NOT |
|---|---|---|
| Schema file resolution and staging | `prepare-dms-schema.ps1` | Re-resolve or re-stage schema |
| `EffectiveSchemaHash` computation | `prepare-dms-schema.ps1` | Compute an alternate hash |
| Claims fragment staging and validation | `prepare-dms-claims.ps1` | Accept or validate claims parameters |
| Docker service startup and health waiting | `start-local-dms.ps1` | Start or stop Docker services |
| DMS instance ID resolution | `configure-local-dms-instance.ps1` | Query CMS for instance IDs |
| `CMSReadOnlyAccess` client provisioning | `configure-local-dms-instance.ps1` | Create IDE-access CMS clients |
| Smoke-test credentials | `configure-local-dms-instance.ps1` | Create `EdFiSandbox` application |
| DDL provisioning and hash validation | `provision-dms-schema.ps1` | Perform or bypass DDL work |
| `SeedLoader` credential creation | `load-dms-seed-data.ps1` | Create or reference SeedLoader credentials |
| BulkLoadClient seed invocation | `load-dms-seed-data.ps1` | Invoke BulkLoadClient |
| Orchestration sequence | `bootstrap-local-dms.ps1` | Replicate the full phase sequence |

---

## 6. Parameter Surface by Owner

Each phase accepts only the parameters relevant to its concern. Every phase command may also accept
an optional `-ConfigFile <path>` parameter; when present, the phase reads its own relevant keys
from that file before applying CLI flags. CLI flags always take precedence over config file values.
The wrapper may pass `-ConfigFile` through unchanged and does nothing else with it.

Ownership rule for validation:

- The wrapper may validate that the shared defaults file is well-formed JSON.
- A phase command validates only the keys it owns and must tolerate keys owned by other phases in the same file.
- A phase command invoked directly with `-ConfigFile` must fail on malformed JSON or invalid values for its owned keys, but not on the mere presence of keys outside its ownership.

| Phase command | Owned parameters |
|---|---|
| `prepare-dms-schema.ps1` | `-Extensions`, `-ApiSchemaPath`, `-ConfigFile` |
| `prepare-dms-claims.ps1` | `-ClaimsDirectoryPath`, `-AddExtensionSecurityMetadata` (legacy compat), `-ConfigFile` |
| `start-local-dms.ps1` | `-InfraOnly`, `-DmsBaseUrl`, `-Rebuild`/`-r`, `-IdentityProvider`, `-EnableConfig` (legacy compat), `-d`/`-v`, `-ConfigFile` |
| `configure-local-dms-instance.ps1` | `-NoDmsInstance`, `-SchoolYearRange`, `-AddSmokeTestCredentials`, `-ConfigFile` |
| `provision-dms-schema.ps1` | *(no user-facing schema flags; connection inputs derived from instance list)*; `-ConfigFile` |
| `load-dms-seed-data.ps1` | `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath`, `-SchoolYearRange`, `-ConfigFile` |
| `bootstrap-local-dms.ps1` | `-ConfigFile` *(only; all per-phase defaults come from the config file, not from wrapper params)* |

The wrapper has no parameter surface beyond `-ConfigFile`. Adding a new parameter to a phase command does
not require a wrapper change.

---

## 7. Wrapper Prohibitions (Explicit)

This section is the compact reference form of the wrapper rule already stated in Section 3.7. It exists
to make reviews faster, not to define a second wrapper contract.

The thin wrapper `bootstrap-local-dms.ps1` must never become the owner of:

- schema-selection logic or `ApiSchema*.json` resolution
- claims parsing or claims-fragment validation
- database state inspection or `EffectiveSchemaHash` computation
- credential synthesis for any CMS client
- retry logic or error-recovery policy
- continuation policy (what runs next when a previous phase fails or is skipped)
- any behavior that requires knowledge of a phase's internal state

Violating any of these prohibitions converts the wrapper back into the monolithic control plane
this design exists to remove.

**Allowed extension:** The wrapper may forward a single `-ConfigFile <path>` argument unchanged to each
phase command. Each phase reads its own keys; the wrapper does not extract, route, rename, or interpret
individual values. See `bootstrap-design.md` Section 9.4.2.
