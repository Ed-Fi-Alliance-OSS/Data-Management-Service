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
> any other phase-specific concern. Phase-command contract expansion never requires a wrapper change.
> A wrapper may later expose convenience aliases for common-path ergonomics, but the normative
> bootstrap surface is already complete without them.

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

**Mode-to-security contract (precise):** In every supported schema-selection mode, the effective staged schema set resolved here automatically determines the matching base security fragment set that `prepare-dms-claims.ps1` will stage. That includes standard mode (`-Extensions`, including the omitted-`-Extensions` core-only case) and expert mode (`-ApiSchemaPath`). This phase command records the schema-derived security inputs for the run; the actual claims staging and additive-fragment validation remain owned by `prepare-dms-claims.ps1`.

**Boundary note:** Mode 3 (`-ApiSchemaPath`) remains an expert schema-selection path because seed-source defaults and bootstrap-managed extension ergonomics stay narrower there, but it no longer changes the source of truth for security selection. This command validates schema inputs only.

---

### 3.2 `prepare-dms-claims.ps1` — Claims and Security Staging

**Primary concern:** Stage `*-claimset.json` fragments into the workspace that the Config Service reads on startup.

| Item | Detail |
|---|---|
| **Preconditions** | Staged-schema manifest produced by `prepare-dms-schema.ps1`. No Docker services required. |
| **Inputs** | `-ClaimsDirectoryPath <path>` (optional additive input) |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/claims/` containing claimset fragments; persisted claims-startup contract artifact under `.bootstrap` describing the effective Config Service claims inputs for this run |
| **Side effects** | Writes staged claims workspace; validates JSON well-formedness, no duplicate filenames, and no unknown claim set names |
| **Failure conditions** | Duplicate filenames; malformed JSON in any fragment; unknown claim set name; staged workspace exists with different content |
| **Must NOT do** | Contact Docker, the database, or the Config Service; perform schema resolution or hash computation; accept schema-selection parameters |

**Mode-to-security contract (precise):** This command always stages the schema-derived base claims set recorded by `prepare-dms-schema.ps1`. If the staged schema set is core only, the resulting startup contract may stay in Embedded mode. If the staged schema set includes one or more non-core schemas, this command stages the matching schema-derived fragments automatically and the resulting startup contract is Hybrid mode. When `-ClaimsDirectoryPath` is supplied, its validated fragments are added on top of that schema-derived base set rather than replacing it.

**Boundary note:** Claim-fragment validation here is structural only: JSON shape, duplicate filenames, and claim-set-name references. This phase does not inspect attachment overlap, reject duplicate `(resource claim, claim set name)` pairs, or perform semantic composition reasoning; CMS startup remains the authoritative composition gate. Built-in seed-support advertisement is owned by Story 02 / `load-dms-seed-data.ps1`; this phase only stages and validates the claims inputs that later seed delivery depends on. The persisted claims-startup contract is bootstrap input state only: it records the effective Config Service claims inputs for this run and is not a cross-invocation resume mechanism, mutable workflow checkpoint, or second control plane.

---

### 3.3 `start-local-dms.ps1` — Infrastructure Lifecycle

**Primary concern:** Docker stack management and service health waiting.

| Item | Detail |
|---|---|
| **Preconditions** | Staged claims workspace (`eng/docker-compose/.bootstrap/claims/`) and persisted claims-startup contract artifact present when CMS is included (normal flow). |
| **Inputs** | `-InfraOnly` (exclude DMS container from Docker startup); `-DmsBaseUrl <url>` (health endpoint of IDE-hosted DMS; valid only with `-InfraOnly`); `-Rebuild` / `-r`; `-IdentityProvider`; `-EnableConfig` (legacy compat, not a meaningful opt-out in the normative flow); teardown flags `-d`/`-v` |
| **Outputs** | Running Docker services; claims-ready Config Service; healthy DMS container (non-`-InfraOnly` path) |
| **Side effects** | Docker Compose up/down; reads the persisted claims-startup contract artifact and applies it to Config Service startup; calls `setup-openiddict.ps1 -InitDb` after PostgreSQL health; calls `setup-openiddict.ps1 -InsertData` after Config Service readiness (self-contained path); after `/health` is green, probes `/authorizationMetadata?claimSetName=<name>` for `EdFiSandbox` and each staged additional claim set name when hybrid claims are staged; polls `$DmsBaseUrl/health` with timeout when `-DmsBaseUrl` is provided |
| **Failure conditions** | Docker compose start failure; health-wait timeout for any service; Config Service `/authorizationMetadata` readiness probe fails for `EdFiSandbox` or any staged additional claim set name; `-DmsBaseUrl` health-wait timeout |
| **Must NOT do** | Resolve or validate ApiSchema files; inspect or write the staged-schema or staged-claims workspace; provision databases; configure DMS instances; create CMS clients; load seed data; accept schema or claims parameters |

**Boundary note:** `-InfraOnly` and `-DmsBaseUrl` are Docker-layer controls - they decide whether and which DMS health endpoint to poll. Config Service readiness in this phase is the claims-ready gate for later phases: `/health` must be green, and bootstrap must be able to query `/authorizationMetadata?claimSetName=...` successfully for `EdFiSandbox` plus each staged additional claim set name when hybrid claims are staged. This phase consumes the persisted claims-startup contract produced earlier; it does not re-derive claims policy from schema or fragment contents. These controls do not express schema selection, post-health sequencing, or any concern owned by another phase. Once health is confirmed, any later step is owned by wrapper orchestration or by the developer invoking the next phase command explicitly.

---

### 3.4 `configure-local-dms-instance.ps1` — Instance and Client Setup

**Primary concern:** Configure DMS instances and CMS client records that downstream phases and IDE-hosted DMS depend on.

| Item | Detail |
|---|---|
| **Preconditions** | Config Service healthy and claims-loaded (Docker service ready). |
| **Inputs** | `-NoDmsInstance` (narrow reuse escape hatch: valid only when exactly one existing instance is present); `-SchoolYearRange <range>` (school-year path); `-AddSmokeTestCredentials` (creates CMS-only test application) |
| **Outputs** | One or more DMS instance records in CMS; `CMSReadOnlyAccess` client record for IDE-hosted DMS; `EdFiSandbox` application when `-AddSmokeTestCredentials` is set; selected instance IDs printed to stdout for the caller to consume or forward |
| **Side effects** | CMS API calls to `Add-DmsInstance` / `Add-DmsSchoolYearInstances`; prints selected instance IDs and `CMSReadOnlyAccess` client credentials to output; no files written beyond CMS records |
| **Failure conditions** | Config Service unreachable; `-NoDmsInstance` with 0 or >1 existing instances; `-NoDmsInstance` with `-SchoolYearRange` (invalid combination) |
| **Must NOT do** | Create `SeedLoader` credentials (those belong to `load-dms-seed-data.ps1`); perform DDL work; write persisted runtime state to disk for later phases; accept schema or claims parameters |

**Boundary note:** This phase creates or confirms the DMS instance records that downstream phases target. Selected instance IDs are printed to stdout; the thin wrapper may capture and forward them in memory during a single invocation. When phase commands are run separately, downstream phases resolve target instances through their own explicit selectors (`-InstanceId`, `-SchoolYear`) via a CMS-backed lookup — a deliberate tradeoff that makes each phase independently re-runnable without hidden disk artifacts.

---

### 3.5 `provision-dms-schema.ps1` — Authoritative Schema Provisioning

**Primary concern:** Invoke the SchemaTools/runtime-owned path to provision or validate target databases.

| Item | Detail |
|---|---|
| **Preconditions** | At least one resolvable DMS instance in CMS (explicit via `-InstanceId` or `-SchoolYear`, or exactly one instance present for auto-selection); staged schema workspace and expected `EffectiveSchemaHash` from `prepare-dms-schema.ps1`; Config Service and PostgreSQL reachable. |
| **Inputs** | `-InstanceId <guid[]>` (explicit target selector; omit when exactly one instance exists); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists); staged schema paths (read from `eng/docker-compose/.bootstrap/ApiSchema/`) |
| **Outputs** | Provisioned or validated databases for each target instance; printed IDE next-step guidance (staged schema path, `appsettings` values, `CMSReadOnlyAccess` credentials) after infra-only shape completes |
| **Side effects** | Invokes authoritative SchemaTools/runtime provisioning path; exits non-zero if provisioning or validation fails |
| **Failure conditions** | Zero matching instances found; multiple matching instances found without an explicit `-InstanceId` or `-SchoolYear` selector; SchemaTools/runtime provisioning exits non-zero; stored `EffectiveSchemaHash` mismatches expected hash for a target instance; connection to target database fails |
| **Must NOT do** | Accept user-facing schema-selection parameters; repair or work around a failed SchemaTools path; run inside DMS startup via `AppSettings__DeployDatabaseOnStartup`; silently reuse a database provisioned for a different schema selection; resolve schema files; create or mutate instance records in CMS |

**Boundary note:** `AppSettings__DeployDatabaseOnStartup=false` is always set. Schema provisioning is entirely owned by this phase; DMS startup never performs it. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is supplied, auto-select it; when multiple instances exist and no explicit `-InstanceId` or `-SchoolYear` is provided, fail fast with guidance to supply an explicit selector.

---

### 3.6 `load-dms-seed-data.ps1` — Seed Delivery

**Primary concern:** Materialize JSONL files and invoke BulkLoadClient against a healthy DMS endpoint.

| Item | Detail |
|---|---|
| **Preconditions** | Live DMS process healthy (`/health` returns 200); CMS remains reachable so this phase can create `SeedLoader` credentials immediately before BulkLoadClient invocation. Blocked externally: ODS-6738 (BulkLoadClient JSONL support) and DMS-1119 (published seed artifacts). See Story 02. |
| **Inputs** | `-InstanceId <guid[]>` (explicit target selector; omit when exactly one instance exists); `-LoadSeedData`; `-SeedTemplate Minimal\|Populated` (mutually exclusive with `-SeedDataPath`); `-SeedDataPath <path>` (custom JSONL); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists) |
| **Outputs** | Seeded DMS instance(s); seed workspace cleaned up on success |
| **Side effects** | Creates `SeedLoader` application via `Add-CmsClient` / `Add-Application`; resolves BulkLoadClient package; copies JSONL into seed workspace; invokes BulkLoadClient once per school year; retains seed workspace on failure |
| **Failure conditions** | Zero matching instances found; multiple matching instances found without an explicit `-InstanceId` or `-SchoolYear` selector; `-SeedTemplate` with Mode 3 (`-ApiSchemaPath`) run; `-SeedTemplate` and `-SeedDataPath` both supplied; BulkLoadClient exits non-zero; seed package unavailable; DMS health endpoint unreachable; filename collisions in seed workspace |
| **Must NOT do** | Create `CMSReadOnlyAccess` or smoke-test credentials (those belong to `configure-local-dms-instance.ps1`); reuse `SeedLoader` credentials for smoke tests; perform DDL work; accept schema or claims parameters |

**Boundary note:** Story 02 blockers (ODS-6738, DMS-1119) prevent end-to-end delivery. This phase is designed and documented as blocked-but-ready. The design does not normalize the legacy direct-SQL path as the target state. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is supplied, auto-select it; when multiple instances exist and no explicit `-InstanceId` or `-SchoolYear` is provided, fail fast with guidance to supply an explicit selector.

---

### 3.7 `bootstrap-local-dms.ps1` — Thin Convenience Wrapper (Optional)

**Delivery status:** Convenience packaging only. The wrapper is optional and owns no policy. The composable phase commands remain the authoritative bootstrap contract for DMS-916.

**Primary concern:** Sequence the above phase commands in the correct order for the common happy path.

| Item | Detail |
|---|---|
| **Preconditions** | None additional beyond what phase commands require. |
| **Inputs** | `-Extensions <name>` (forwarded to `prepare-dms-schema.ps1`); `-ApiSchemaPath <path>` (forwarded to `prepare-dms-schema.ps1`); `-ClaimsDirectoryPath <path>` (forwarded to `prepare-dms-claims.ps1`); `-InfraOnly` (forwarded to `start-local-dms.ps1`); `-DmsBaseUrl <url>` (forwarded to `start-local-dms.ps1`); `-IdentityProvider` (forwarded to `start-local-dms.ps1`); `-SchoolYearRange <range>` (forwarded to `configure-local-dms-instance.ps1` for the school-year instance-creation workflow); `-InstanceId <guid[]>` (forwarded to `provision-dms-schema.ps1` and `load-dms-seed-data.ps1`); `-LoadSeedData` (forwarded to `load-dms-seed-data.ps1`); `-SeedTemplate` (forwarded to `load-dms-seed-data.ps1`); `-SeedDataPath <path>` (forwarded to `load-dms-seed-data.ps1`); `-Rebuild`/`-r`; `-AddSmokeTestCredentials` |
| **Outputs** | Delegated entirely to the phase commands it calls |
| **Side effects** | Delegates to phase commands; prints next-step guidance when a phase is intentionally omitted |
| **Failure conditions** | Propagates non-zero exit from any called phase command |
| **Must NOT do** | Own schema logic; perform claims parsing; inspect database state; synthesize credentials; implement retry or continuation policy; write runtime state to disk; absorb any concern owned by a phase command |

**Boundary note:** The wrapper sequences phase commands, forwards real developer-facing flags to the appropriate phase, and may print next-step guidance. During a single invocation the wrapper may capture instance IDs returned by `configure-local-dms-instance.ps1` and forward them as explicit `-InstanceId` arguments to later phases in the same process. The wrapper's school-year flag is `-SchoolYearRange`, matching the instance-creation phase it calls; downstream manual selector flags remain `-SchoolYear <int[]>` on `provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. It never owns schema policy, claims logic, credential behavior, or continuation policy.

---

## 4. Dependency Chain

```
prepare-dms-schema.ps1
  -> prepare-dms-claims.ps1
       -> start-local-dms.ps1 -InfraOnly  (starts PostgreSQL, Keycloak/OpenIddict, Config Service)
            -> configure-local-dms-instance.ps1  (CMS HTTP API ready)
                 -> provision-dms-schema.ps1  (-InstanceId passed by wrapper in-memory, or explicit selector in manual flow)
                      -> start-local-dms.ps1  (starts DMS container; or IDE-hosted DMS starts here)
                           -> load-dms-seed-data.ps1  (-InstanceId passed by wrapper in-memory or explicit selector, live DMS + SeedLoader credentials)
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
| Claims fragment staging, validation, and persisted claims-startup contract | `prepare-dms-claims.ps1` | Accept or validate claims parameters; write or reinterpret claims-startup policy |
| Docker service startup and health waiting | `start-local-dms.ps1` | Start or stop Docker services |
| DMS instance and client record creation | `configure-local-dms-instance.ps1` | Create or modify DMS instance records |
| Downstream instance target selection | `provision-dms-schema.ps1`, `load-dms-seed-data.ps1` (each phase resolves its own selectors) | Resolve target instances on behalf of another phase |
| `CMSReadOnlyAccess` client provisioning | `configure-local-dms-instance.ps1` | Create IDE-access CMS clients |
| Smoke-test credentials | `configure-local-dms-instance.ps1` | Create `EdFiSandbox` application |
| DDL provisioning and hash validation | `provision-dms-schema.ps1` | Perform or bypass DDL work |
| `SeedLoader` credential creation | `load-dms-seed-data.ps1` | Create or reference SeedLoader credentials |
| BulkLoadClient seed invocation | `load-dms-seed-data.ps1` | Invoke BulkLoadClient |
| Orchestration sequence | `bootstrap-local-dms.ps1` | Replicate the full phase sequence |

---

## 6. Parameter Surface by Owner

Each phase accepts only the parameters relevant to its concern.

| Phase command | Owned parameters |
|---|---|
| `prepare-dms-schema.ps1` | `-Extensions`, `-ApiSchemaPath` |
| `prepare-dms-claims.ps1` | `-ClaimsDirectoryPath` |
| `start-local-dms.ps1` | `-InfraOnly`, `-DmsBaseUrl`, `-Rebuild`/`-r`, `-IdentityProvider`, `-EnableConfig` (legacy compat), `-d`/`-v` |
| `configure-local-dms-instance.ps1` | `-NoDmsInstance`, `-SchoolYearRange`, `-AddSmokeTestCredentials` |
| `provision-dms-schema.ps1` | `-InstanceId <guid[]>`, `-SchoolYear <int[]>` |
| `load-dms-seed-data.ps1` | `-InstanceId <guid[]>`, `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath`, `-SchoolYear <int[]>` |
| `bootstrap-local-dms.ps1` | `-Extensions`, `-ApiSchemaPath`, `-ClaimsDirectoryPath`, `-InfraOnly`, `-DmsBaseUrl`, `-IdentityProvider`, `-SchoolYearRange`, `-InstanceId <guid[]>`, `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath <path>`, `-Rebuild`/`-r`, `-AddSmokeTestCredentials` |

Adding a new parameter to a phase command requires a corresponding wrapper parameter only when the developer-facing happy path benefits from that flag.

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

**Allowed extension:** The wrapper may capture instance IDs returned during `configure-local-dms-instance.ps1` invocation and forward them as explicit `-InstanceId` values to later phases within the same process execution. No state is written to disk for this purpose.

---

## 8. Selector Resolution Examples

The selector resolution rule (auto-select on exactly one match; fail fast otherwise) applies to both
`provision-dms-schema.ps1` (§3.5) and `load-dms-seed-data.ps1` (§3.6) identically. The examples below
use `provision-dms-schema.ps1` but the behavior is the same for the seed phase.

```powershell
# One instance in CMS — auto-selected, no flag required
pwsh eng/docker-compose/provision-dms-schema.ps1

# Multiple instances — explicit ID selector required
pwsh eng/docker-compose/provision-dms-schema.ps1 -InstanceId "a1b2c3d4-1234-5678-abcd-000000000001"

# Multiple instances — school-year selector (each year is targeted)
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026

# Fail-fast output when multiple instances exist and no selector is supplied:
# ERROR: 2 DMS instance(s) found in CMS without an explicit selector.
#        Supply -InstanceId <guid> or -SchoolYear <int> to target a specific instance.
#        Exit code: non-zero.
```

**Wrapper path:** The thin wrapper captures instance IDs from `configure-local-dms-instance.ps1` in memory
and forwards them as `-InstanceId` to downstream phases within the same invocation — developers never
copy-paste GUIDs between phases when using the wrapper.

**Manual phase path:** Each phase command resolves its own selectors independently. No shared disk artifact
exists for this purpose; each phase contacts CMS directly.
