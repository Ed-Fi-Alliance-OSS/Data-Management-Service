# DMS-916 Bootstrap — Command Boundaries

**Basis:** `bootstrap-design.md` Sections 3-12.
`responsibility-inventory.md` is a supporting summary and must not restate this file as a competing
ownership contract.

---

## 1. Purpose

This document states the normative ownership contract for each phase command in the composable
bootstrap design. Each command owns exactly one primary concern. The phase-oriented commands are
the normative bootstrap contract; any thin wrapper is convenience packaging over them. Other DMS-916 design
documents provide rationale, examples, dependency notes, or story-specific acceptance criteria. They must
reference this file for phase order, parameter ownership, selector rules, wrapper behavior, and non-overlap
guarantees rather than restating those rules as a competing contract.

---

## 2. Normative Contract Statement

> **The composable phase commands are the authoritative bootstrap contract for DMS-916.**
>
> Any wrapper over those commands is a convenience entry point for the happy path only. The wrapper
> is not the source of lifecycle semantics, schema policy, claims logic, provisioning behavior, or
> any other phase-specific concern. The wrapper may expose happy-path flags, but every flag remains
> owned by exactly one phase command and the wrapper only sequences phases and forwards values.

**Shared local settings contract:** Every phase command that contacts the local Docker-managed services
accepts `-EnvironmentFile <path>` and resolves CMS, identity provider, tenant, DMS base URL, and database
connection defaults through one shared local-settings helper. Omitting `-EnvironmentFile` uses the same
default env file as `start-local-dms.ps1`. The shared helper is not a workflow control plane, run-context
file, or source of schema/claims policy; it only prevents standalone phase commands from hard-coding
localhost ports, tenant values, credentials, or identity-provider settings that already belong to the local
environment configuration.

---

## 3. Phase Commands

### 3.1 `prepare-dms-schema.ps1` — Schema Selection and Staging

**Primary concern:** Resolve and stage the normalized ApiSchema asset container for the run.

| Item | Detail |
|---|---|
| **Preconditions** | Story 00: filesystem ApiSchema source available directly through `-ApiSchemaPath`. Story 06: NuGet feed reachable for asset-only package materialization. No Docker services required. |
| **Inputs** | Story 00: `-ApiSchemaPath <path>` (direct filesystem ApiSchema source). Story 06: `-Extensions <name>` (0..N, standard names only), mutually exclusive with `-ApiSchemaPath`. |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/ApiSchema/` containing normalized schema JSON files, optional schema-adjacent static content, and `bootstrap-api-schema-manifest.json`; the staged workspace itself is the downstream schema and runtime-asset contract consumed by later phases and DMS runtime |
| **Side effects** | Writes staged workspace; computes expected `EffectiveSchemaHash` via `dms-schema hash`; records manifest-relative paths for schema and optional static content in `bootstrap-api-schema-manifest.json`; writes the schema section of `eng/docker-compose/.bootstrap/bootstrap-manifest.json` with schema-selection mode, selected mapped extensions, the effective schema hash, an ApiSchema workspace fingerprint, and the relative ApiSchema manifest path |
| **Failure conditions** | Story 00: missing `-ApiSchemaPath`; `-Extensions` supplied before Story 06 behavior is implemented; normalized-path collision; staged workspace exists with different content; `dms-schema hash` exits non-zero; fewer or more than 1 core schema present after staging. Story 06 adds: unrecognized extension name; `-Extensions` and `-ApiSchemaPath` both supplied; NuGet feed unreachable for package-backed materialization; selected package is missing the required asset-only ApiSchema payload; selected package contains only DLL-backed ApiSchema resources after the asset-only package switch-over. |
| **Must NOT do** | Start or depend on Docker services; modify `.env` or Docker Compose variables; perform DDL work; contact the Config Service; accept claims-related parameters |

**Mode-to-security contract (precise):** In Story 00 direct filesystem mode (`-ApiSchemaPath`), automatic
base security selection is limited to core and non-core schemas that match the v1 extension mapping. Any
unmapped non-core schema remains detectable from the staged schema files and requires developer-supplied claim
fragments through `-ClaimsDirectoryPath`; this command does not reject that shape because it does not own
claims inputs. Story 06 package-backed `-Extensions` mode must write the same root bootstrap manifest schema
facts so `prepare-dms-claims.ps1` can use the same security-selection contract.

**Boundary note:** The stable contract is the staged filesystem ApiSchema workspace, not the package shape
that produced it. Story 00 delivers only the direct `-ApiSchemaPath` acquisition path. Story 06
package-backed standard mode is an acquisition/materialization path that must converge on the same workspace after
asset-only packages replace DLL-backed package distribution. `-ApiSchemaPath` remains an expert
schema-selection path because seed-source defaults and bootstrap-managed extension ergonomics stay narrower
there. This command validates schema inputs only; complete authorization coverage for unmapped custom
resources is a caller-owned contract expressed through `-ClaimsDirectoryPath` and runtime behavior. The
staged workspace shape follows
[`apischema-container.md`](apischema-container.md): schema JSON files are the hash/DDL/API authority, while
the manifest also indexes optional discovery/specification JSON and XSD assets for the runtime
content-loading story.

---

### 3.2 `prepare-dms-claims.ps1` — Claims and Security Staging

**Primary concern:** Stage `*-claimset.json` fragments into the workspace that the Config Service reads on startup.

| Item | Detail |
|---|---|
| **Preconditions** | Staged ApiSchema workspace and `eng/docker-compose/.bootstrap/bootstrap-manifest.json` schema section produced by `prepare-dms-schema.ps1`. No Docker services required. |
| **Inputs** | `-ClaimsDirectoryPath <path>` (optional additive input except required when the staged schema set contains unmapped non-core schemas) |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/claims/` containing claimset fragments; root bootstrap manifest `eng/docker-compose/.bootstrap/bootstrap-manifest.json` updated with the effective Config Service claims mode, relative claims directory, claims fingerprint, expected claims-verification checks, and seed namespace-prefix inputs |
| **Side effects** | Writes staged claims workspace; validates JSON well-formedness, no duplicate filenames, and no unknown effective claim set references; records a core baseline check plus expected `(claim set name, resource claim URI, action)` entries from staged fragments for later CMS readiness verification in the bootstrap manifest; records extension namespace prefixes for seed delivery in the same bootstrap manifest |
| **Failure conditions** | Duplicate filenames; malformed JSON in any fragment; unknown effective claim set reference; staged workspace exists with different content; staged schema set contains unmapped non-core schemas and `-ClaimsDirectoryPath` is not supplied |
| **Must NOT do** | Contact Docker, the database, or the Config Service; perform schema resolution or hash computation; accept schema-selection parameters |

**Mode-to-security contract (precise):** This command always stages the automatic base claims set identified by the schema section of the bootstrap manifest from `prepare-dms-schema.ps1`. If the staged schema set is core only, the bootstrap manifest claims mode may stay in Embedded mode. If the staged schema set includes one or more mapped non-core schemas, this command stages the matching v1 extension fragments automatically and records Hybrid mode. If the staged schema set includes unmapped non-core schemas from `-ApiSchemaPath`, `-ClaimsDirectoryPath` is required and its validated fragments are staged alongside any automatic base fragments. Bootstrap validates the supplied fragments structurally, but does not prove that they authorize every custom resource.

**Claim-set-reference validation contract:** This command validates only the claim set names that CMS
composition will use as effective authorization attachments: every explicit
`resourceClaims[].claimSets[].name`, plus the fragment top-level `name` only for non-parent resource
claims that rely on it as the implicit claim set name. A fragment top-level `name` that is only a
fragment/group label for explicit parent-claim attachments is not by itself a claim set reference and
must not be rejected merely because it is absent from embedded `Claims.json`.

**Bootstrap manifest handoff contract:** `eng/docker-compose/.bootstrap/bootstrap-manifest.json` is the only
persisted compatibility and handoff state between bootstrap phases. The ApiSchema manifest under
`.bootstrap/ApiSchema/` remains only the runtime asset index for staged schema/content files. The root
bootstrap manifest records stable prepared inputs and fingerprints only:

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

It does not include instance IDs, credentials, URLs, Docker or container state, seed file lists, phase
progress, or resume checkpoints. Compose environment variables, absolute host paths, and mount-source values
are derived from the repo root plus the manifest's relative directories; they are not stored as additional
state. DMS compose services do not consume claimset fragment files, so `local-dms.yml` and
`published-dms.yml` must not mount `/app/additional-claims`; DMS reads authorization metadata from CMS.

**Boundary note:** Claim-fragment validation here is structural only: JSON shape, duplicate filenames, effective claim-set-name references, and mechanical extraction of the expected verification entries. This phase does not inspect attachment overlap, reject duplicate `(resource claim, claim set name)` pairs, or perform semantic composition reasoning; CMS startup remains the authoritative composition gate. Built-in seed-support advertisement is owned by Story 02 / `load-dms-seed-data.ps1`; this phase only stages and validates the claims inputs that later seed delivery depends on. The bootstrap manifest is not a cross-invocation resume mechanism, mutable workflow checkpoint, or second control plane.

---

### 3.3 `start-local-dms.ps1` — Infrastructure Lifecycle

**Primary concern:** Docker stack management, local identity setup, and service health waiting.

| Item | Detail |
|---|---|
| **Preconditions** | Staged claims workspace (`eng/docker-compose/.bootstrap/claims/`) and bootstrap manifest claims section present when CMS is included (normal flow). When invoked with `-DmsBaseUrl`, `configure-local-dms-instance.ps1` and `provision-dms-schema.ps1` have already completed for the selected target set. |
| **Inputs** | `-InfraOnly` (exclude DMS container from Docker startup); `-DmsBaseUrl <url>` (post-provision health endpoint of IDE-hosted DMS; valid only with `-InfraOnly` and only at the DMS-start/health-wait point after schema provisioning); `-EnvironmentFile <path>` (select Docker Compose env file and shared local settings); `-Rebuild` / `-r`; `-IdentityProvider`; `-EnableConfig` (legacy compat, not a meaningful opt-out in the normative flow); `-EnableKafkaUI`; `-EnableSwaggerUI`; teardown flags `-d`/`-v` |
| **Outputs** | Running Docker services; provider-specific local identity clients including `CMSReadOnlyAccess`; claims-ready Config Service; healthy DMS container (non-`-InfraOnly` path) or confirmed healthy IDE-hosted DMS endpoint (post-provision `-DmsBaseUrl` path) |
| **Side effects** | Docker Compose up/down; runs provider-specific local identity setup, including the fixed `CMSReadOnlyAccess` read-only client; reads the bootstrap manifest claims section and applies it to Config Service startup through `local-config.yml` claims env vars and the Config Service `/app/additional-claims` bind mount; calls `setup-openiddict.ps1 -InitDb` after PostgreSQL health; calls `setup-openiddict.ps1 -InsertData` after Config Service readiness (self-contained path); after `/health` is green, verifies CMS applied the staged claims content using a CMS load/composition result when available, otherwise by probing `/authorizationMetadata?claimSetName=<name>` for the recorded claims-verification checks and requiring each expected resource claim URI/action to be present; polls `$DmsBaseUrl/health` with timeout only during the post-provision DMS-start/health-wait invocation |
| **Failure conditions** | Docker compose start failure; health-wait timeout for any service; Config Service claims composition/load result is failed, incomplete, or unavailable without successful authorization metadata fallback; authorization metadata fallback returns non-200 or omits the core baseline check or any expected `(claim set name, resource claim URI, action)` entry from the bootstrap manifest claims section; `-DmsBaseUrl` supplied before the selected instances and target databases are ready; `-DmsBaseUrl` health-wait timeout |
| **Must NOT do** | Resolve or validate ApiSchema files; inspect or write the staged-schema or staged-claims workspace; provision databases; enable the legacy `NEED_DATABASE_SETUP` / `EdFi.DataManagementService.Backend.Installer.dll` startup provisioning path; configure DMS instances; create smoke-test or seed-loading CMS application credentials; load seed data; accept schema or claims parameters |

**Boundary note:** `-InfraOnly` and `-DmsBaseUrl` are Docker-layer controls - they decide whether a DMS
container starts or an already-provisioned IDE-hosted DMS endpoint is health-checked. Config Service
readiness in the first infrastructure invocation is the claims-ready gate for later phases: `/health`
must be green, and bootstrap must prove that CMS applied the staged claims content. A CMS
load/composition result is the preferred proof. If that result is not available, the fallback is
manifest-driven authorization metadata verification: query `/authorizationMetadata?claimSetName=<name>`
for the recorded checks, require the core baseline check to pass, and require every staged fragment entry
to contain the expected resource claim URI and action for its claim set. Merely finding `EdFiSandbox`, or
finding a claim set name without the staged resource/action entries, is not claims-ready. This phase
consumes the claims section of the bootstrap manifest produced earlier; it does not re-derive claims policy
from schema or fragment contents. `-DmsBaseUrl` is never a shortcut around instance creation or schema
provisioning: the wrapper must hold that value until after `configure-local-dms-instance.ps1` and
`provision-dms-schema.ps1` have completed, and manual phase flows must invoke the external health wait in
the same post-provision position. Once DMS health is confirmed, any later step is owned by wrapper
orchestration or by the developer invoking the next phase command explicitly.

---

### 3.4 `configure-local-dms-instance.ps1` — Instance Setup

**Primary concern:** Configure DMS instances that downstream phases and IDE-hosted DMS depend on.

| Item | Detail |
|---|---|
| **Preconditions** | Config Service healthy and claims-loaded (Docker service ready). |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth, tenant scope, and database defaults); `-NoDmsInstance` (narrow reuse escape hatch: valid only when exactly one existing instance is present); `-SchoolYearRange <range>` (school-year path); `-AddSmokeTestCredentials` (creates CMS-only test application) |
| **Outputs** | One or more DMS instance records in CMS; `EdFiSandbox` application when `-AddSmokeTestCredentials` is set; structured success-pipeline result containing selected instance IDs and, when available, the pre-provisioned `CMSReadOnlyAccess` credential values needed by IDE guidance |
| **Side effects** | CMS API calls to `Add-DmsInstance` / `Add-DmsSchoolYearInstances`; optional CMS API calls for smoke-test credentials; may validate or report the `CMSReadOnlyAccess` client created by the local identity setup path; emits human-readable progress and guidance on non-success streams; no files written beyond CMS records |
| **Failure conditions** | Config Service unreachable; `-NoDmsInstance` with 0 or >1 existing instances; `-NoDmsInstance` with `-SchoolYearRange` (invalid combination) |
| **Must NOT do** | Create or scope the `CMSReadOnlyAccess` identity client (that belongs to `start-local-dms.ps1` through provider-specific local identity setup); use `/connect/register` to create `CMSReadOnlyAccess`; create `SeedLoader` credentials (those belong to `load-dms-seed-data.ps1`); perform DDL work; write persisted runtime state to disk for later phases; accept schema or claims parameters |

**Boundary note:** This phase creates or confirms the DMS instance records that downstream phases target.
The structured result is JSON-compatible and includes at least `SelectedInstanceIds` and
may include `CMSReadOnlyAccess` credential fields from local identity setup for IDE guidance.
Human-readable text is separate from the success-pipeline object, so the thin wrapper never scrapes prose to
discover IDs or credentials. This phase uses the shared `-EnvironmentFile` local-settings resolver to
avoid hard-coded CMS, tenant, identity-provider, and PostgreSQL defaults. When phase commands are run separately,
downstream phases resolve target instances through their own explicit selectors (`-InstanceId`,
`-SchoolYear`) via a CMS-backed lookup - a deliberate tradeoff that makes each phase independently
re-runnable without hidden disk artifacts.

---

### 3.5 `provision-dms-schema.ps1` — Authoritative Schema Provisioning

**Primary concern:** Invoke the SchemaTools/runtime-owned path to provision or validate target databases.

| Item | Detail |
|---|---|
| **Preconditions** | At least one resolvable DMS instance in CMS (explicit via `-InstanceId` or `-SchoolYear`, or exactly one instance present for auto-selection); staged schema workspace from `prepare-dms-schema.ps1`; Config Service and PostgreSQL reachable. |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth, tenant scope, and database connection defaults); `-InstanceId <long[]>` (explicit numeric DMS instance ID selector; omit when exactly one instance exists); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists); staged schema paths (read from `eng/docker-compose/.bootstrap/ApiSchema/`) |
| **Outputs** | Provisioned or validated databases for each target instance; printed IDE next-step guidance (staged schema path, `appsettings` values, `CMSReadOnlyAccess` credentials) after infra-only shape completes |
| **Side effects** | Invokes authoritative SchemaTools/runtime provisioning path; exits non-zero if provisioning or validation fails |
| **Failure conditions** | Zero matching instances found; multiple matching instances found without an explicit `-InstanceId` or `-SchoolYear` selector; SchemaTools/runtime provisioning exits non-zero, including when target stored schema state is incompatible with the staged schema set; connection to target database fails |
| **Must NOT do** | Accept user-facing schema-selection parameters; repair or work around a failed SchemaTools path; run inside DMS startup via `AppSettings__DeployDatabaseOnStartup`, `NEED_DATABASE_SETUP`, `EdFi.DataManagementService.Backend.Installer.dll`, or any container entrypoint/pre-launch hook; silently reuse a database provisioned for a different schema selection; resolve schema files; create or mutate instance records in CMS |

**Boundary note:** `AppSettings__DeployDatabaseOnStartup=false` is always set, and the legacy
`NEED_DATABASE_SETUP` / `EdFi.DataManagementService.Backend.Installer.dll` startup path is disabled or
removed for the DMS-916 bootstrap flow. Schema provisioning is entirely owned by this phase; DMS startup
never performs it. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is
supplied, auto-select it; when multiple instances exist and no explicit `-InstanceId` or `-SchoolYear` is
provided, fail fast with guidance to supply an explicit selector. CMS lookup and database target resolution
use the shared `-EnvironmentFile` local-settings resolver, so direct phase invocation and wrapper
orchestration target the same local environment.

---

### 3.6 `load-dms-seed-data.ps1` — Seed Delivery

**Primary concern:** Materialize JSONL files and invoke BulkLoadClient against a healthy DMS endpoint.

| Item | Detail |
|---|---|
| **Preconditions** | Live DMS process healthy at the target base URL (`/health` returns 200); CMS remains reachable so this phase can create `SeedLoader` credentials immediately before BulkLoadClient invocation; bootstrap manifest exists with schema, claims, and seed sections compatible with the requested seed-source flags. Blocked externally: ODS-6738 (BulkLoadClient JSONL support). See Story 02. |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth defaults, tenant scope, and Docker-local DMS URL); `-BootstrapManifestPath <path>` (optional override for the bootstrap manifest; defaults to `eng/docker-compose/.bootstrap/bootstrap-manifest.json`); `-InstanceId <long[]>` (explicit numeric DMS instance ID selector; omit when exactly one instance exists); `-DmsBaseUrl <url>` (BulkLoadClient target endpoint; defaults to the Docker-local DMS URL resolved from the local settings and must be explicit for IDE-hosted seed loading); `-IdentityProvider` (auth provider used to resolve the BulkLoadClient token endpoint; defaults to the provider resolved from the local settings when that parameter is omitted); `-SeedTemplate Minimal\|Populated` (mutually exclusive with `-SeedDataPath`); `-SeedDataPath <path>` (custom JSONL); `-AdditionalNamespacePrefix <string[]>` (optional additive namespace prefixes for custom seed authorization, especially `-SeedDataPath` payloads with agency or custom namespaces); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists) |
| **Outputs** | Seeded DMS instance(s); seed workspace cleaned up on success |
| **Side effects** | Creates `SeedLoader` application via `Add-CmsClient` / `Add-Application` using the de-duplicated baseline seed namespace prefixes, selected extension namespace prefixes, and any `-AdditionalNamespacePrefix` values; resolves BulkLoadClient package; resolves the OAuth token URL from `-IdentityProvider`; copies JSONL into seed workspace; invokes BulkLoadClient once per school year with `--base-url $DmsBaseUrl`; retains seed workspace on failure |
| **Failure conditions** | Missing, malformed, unsupported-version, or incomplete bootstrap manifest; bootstrap manifest schema section says Mode 3 (`-ApiSchemaPath`) and `-SeedTemplate` is specified; zero matching instances found; multiple matching instances found without an explicit `-InstanceId` or `-SchoolYear` selector; unsupported `-IdentityProvider`; `-SeedTemplate` and `-SeedDataPath` both supplied; blank or malformed `-AdditionalNamespacePrefix` value; BulkLoadClient exits non-zero; repo-local seed source unavailable; future extension seed package unavailable; DMS health endpoint unreachable; filename collisions in seed workspace |
| **Must NOT do** | Create `CMSReadOnlyAccess` (that belongs to `start-local-dms.ps1` through provider-specific local identity setup) or smoke-test credentials (those belong to `configure-local-dms-instance.ps1`); reuse `SeedLoader` credentials for smoke tests; perform DDL work; accept schema or claims parameters |

**Boundary note:** Story 02's external blocker (ODS-6738) prevents end-to-end delivery until BulkLoadClient supports the required JSONL interface. This phase is designed and documented as blocked-but-ready. The design does not normalize the legacy direct-SQL path as the target state. Direct invocation of `load-dms-seed-data.ps1` always performs seed delivery; it does not accept a second `-LoadSeedData` switch. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is supplied, auto-select it; when multiple instances exist and no explicit `-InstanceId` or `-SchoolYear` is provided, fail fast with guidance to supply an explicit selector. Endpoint resolution is phase-owned: `load-dms-seed-data.ps1` never infers the IDE URL from a prior `start-local-dms.ps1` invocation. Manual IDE-hosted seed loading passes `-DmsBaseUrl` explicitly; wrapper IDE continuation forwards the same `-DmsBaseUrl` value to this phase when `-LoadSeedData` is selected. Identity-provider resolution is also phase-owned: direct seed invocation passes `-IdentityProvider` when the running environment uses a non-default provider; otherwise the seed phase uses the provider from the shared `-EnvironmentFile` resolver and does not rely on process environment variables left behind by an earlier `start-local-dms.ps1` call. `load-dms-seed-data.ps1` consumes schema mode, selected mapped extensions, and extension namespace prefixes from the bootstrap manifest instead of accepting schema or claims parameters; it owns any seed-catalog lookup for built-in extension seed packages. `-AdditionalNamespacePrefix` is a declared authorization input for SeedLoader vendor creation only; it does not cause bootstrap to inspect JSONL files, infer missing extensions, or synthesize claim grants.

---

### 3.7 `bootstrap-local-dms.ps1` — Thin Convenience Wrapper (Optional)

**Delivery status:** Convenience packaging only. The wrapper is optional and owns no policy. The composable phase commands remain the authoritative bootstrap contract for DMS-916.

**Primary concern:** Sequence the above phase commands in the correct order for the common happy path.

| Item | Detail |
|---|---|
| **Preconditions** | None additional beyond what phase commands require. |
| **Inputs** | `-Extensions <name>` (forwarded to `prepare-dms-schema.ps1`); `-ApiSchemaPath <path>` (forwarded to `prepare-dms-schema.ps1`); `-ClaimsDirectoryPath <path>` (forwarded to `prepare-dms-claims.ps1`); `-InfraOnly` (forwarded to the initial `start-local-dms.ps1` infrastructure invocation); `-DmsBaseUrl <url>` (held by the wrapper until the post-provision DMS-start/health-wait invocation, forwarded to `start-local-dms.ps1` with `-InfraOnly`, and also forwarded to `load-dms-seed-data.ps1` as the BulkLoadClient base URL when `-LoadSeedData` is selected); `-EnvironmentFile <path>` (forwarded to every phase that contacts local services: `start-local-dms.ps1`, `configure-local-dms-instance.ps1`, `provision-dms-schema.ps1`, and `load-dms-seed-data.ps1`); `-IdentityProvider` (forwarded to `start-local-dms.ps1`, and also forwarded to `load-dms-seed-data.ps1` when seed loading is selected); `-EnableKafkaUI` and `-EnableSwaggerUI` (forwarded to `start-local-dms.ps1`); `-SchoolYearRange <range>` (forwarded to `configure-local-dms-instance.ps1` for the school-year instance-creation workflow); `-LoadSeedData` (wrapper-level opt-in that causes the wrapper to invoke `load-dms-seed-data.ps1`); `-SeedTemplate` (forwarded to `load-dms-seed-data.ps1` when seed loading is selected); `-SeedDataPath <path>` (forwarded to `load-dms-seed-data.ps1` when seed loading is selected); `-AdditionalNamespacePrefix <string[]>` (forwarded to `load-dms-seed-data.ps1` when seed loading is selected); `-Rebuild`/`-r`; `-AddSmokeTestCredentials` |
| **Outputs** | Delegated entirely to the phase commands it calls |
| **Side effects** | Delegates to phase commands; prints next-step guidance when a phase is intentionally omitted |
| **Failure conditions** | Propagates non-zero exit from any called phase command |
| **Must NOT do** | Implement phase-specific behavior; own schema logic; perform claims parsing; inspect database state; synthesize credentials; implement retry or fallback logic; write runtime state to disk; absorb any concern owned by a phase command |

**Boundary note:** The wrapper owns orchestration only: it may sequence phase commands, forward
developer-facing parameters, pass same-invocation structured outputs such as selected instance IDs to later
phases, and print next-step guidance. The wrapper does not expose `-InstanceId`; explicit ID targeting is
phase-command-only. During a single invocation the wrapper may read the structured result from
`configure-local-dms-instance.ps1` and forward its `SelectedInstanceIds` as internal `-InstanceId` arguments
to later phases in the same process. The wrapper's school-year flag is `-SchoolYearRange`, matching the
instance-creation phase it calls; downstream manual selector flags remain `-SchoolYear <int[]>` on
`provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. For IDE continuation, the wrapper does not pass
`-DmsBaseUrl` to the initial infrastructure-only start. It carries that value forward and passes it first at
the post-provision DMS-start/health-wait point, after instance configuration and schema provisioning have
completed. If seed loading is selected, it forwards the same URL again to `load-dms-seed-data.ps1` as the
BulkLoadClient base URL and forwards the selected `-IdentityProvider` so the seed phase resolves the
matching token endpoint. It also forwards `-AdditionalNamespacePrefix` to the seed phase when supplied. It
forwards the same `-EnvironmentFile` to every phase that contacts local services so manual and wrapper flows
resolve the same CMS, tenant, DMS, and database defaults. It must not implement
phase-specific behavior, retry or fallback logic, persisted resume state, schema provisioning, CMS
configuration, or seed loading directly, and it never parses human-readable output to recover phase results.

---

## 4. Dependency Chain

```
prepare-dms-schema.ps1
  -> prepare-dms-claims.ps1
       -> start-local-dms.ps1 -InfraOnly  (starts PostgreSQL, Keycloak/OpenIddict, Config Service)
            -> configure-local-dms-instance.ps1  (CMS HTTP API ready)
                 -> provision-dms-schema.ps1  (-InstanceId passed by wrapper in-memory, or explicit selector in manual flow)
                      -> start-local-dms.ps1  (starts DMS container; or `-InfraOnly -DmsBaseUrl` waits for IDE-hosted DMS here)
                           -> load-dms-seed-data.ps1  (-InstanceId passed by wrapper in-memory or explicit selector, -DmsBaseUrl for seed target, -IdentityProvider for token endpoint, live DMS + SeedLoader credentials)
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
| Claims fragment staging, validation, and bootstrap manifest claims section | `prepare-dms-claims.ps1` | Accept or validate claims parameters; write or reinterpret CMS claims startup policy |
| Docker service startup and health waiting | `start-local-dms.ps1` | Start or stop Docker services |
| DMS instance record creation | `configure-local-dms-instance.ps1` | Create or modify DMS instance records |
| Downstream instance target selection | `provision-dms-schema.ps1`, `load-dms-seed-data.ps1` (each phase resolves its own selectors) | Resolve target instances on behalf of another phase |
| `CMSReadOnlyAccess` client provisioning | `start-local-dms.ps1` provider-specific local identity setup | Create or scope `CMSReadOnlyAccess`, especially through CMS registration APIs such as `/connect/register` unless those APIs support read-only scope selection |
| Smoke-test credentials | `configure-local-dms-instance.ps1` | Create `EdFiSandbox` application |
| DDL provisioning and hash validation | `provision-dms-schema.ps1` | Perform or bypass DDL work |
| `SeedLoader` credential creation and namespace-prefix composition | `load-dms-seed-data.ps1` | Create or reference SeedLoader credentials or infer seed namespace prefixes |
| BulkLoadClient seed invocation | `load-dms-seed-data.ps1` | Invoke BulkLoadClient |
| Orchestration sequence | `bootstrap-local-dms.ps1` | Replicate the full phase sequence |

---

## 6. Parameter Surface by Owner

Each phase accepts only the parameters relevant to its concern.

| Phase command | Owned parameters |
|---|---|
| `prepare-dms-schema.ps1` | Story 00: `-ApiSchemaPath`; Story 06: `-Extensions` |
| `prepare-dms-claims.ps1` | `-ClaimsDirectoryPath` |
| `start-local-dms.ps1` | `-InfraOnly`, `-DmsBaseUrl`, `-EnvironmentFile <path>`, `-Rebuild`/`-r`, `-IdentityProvider`, `-EnableConfig` (legacy compat), `-EnableKafkaUI`, `-EnableSwaggerUI`, `-d`/`-v` |
| `configure-local-dms-instance.ps1` | `-EnvironmentFile <path>`, `-NoDmsInstance`, `-SchoolYearRange`, `-AddSmokeTestCredentials` |
| `provision-dms-schema.ps1` | `-EnvironmentFile <path>`, `-InstanceId <long[]>`, `-SchoolYear <int[]>` |
| `load-dms-seed-data.ps1` | `-EnvironmentFile <path>`, `-BootstrapManifestPath <path>`, `-InstanceId <long[]>`, `-DmsBaseUrl <url>`, `-IdentityProvider`, `-SeedTemplate`, `-SeedDataPath`, `-AdditionalNamespacePrefix <string[]>`, `-SchoolYear <int[]>` |
| `bootstrap-local-dms.ps1` | Story 00: `-ApiSchemaPath`, `-ClaimsDirectoryPath`, `-InfraOnly`, `-DmsBaseUrl`, `-EnvironmentFile <path>`, `-IdentityProvider`, `-EnableKafkaUI`, `-EnableSwaggerUI`, `-SchoolYearRange`, `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath <path>`, `-AdditionalNamespacePrefix <string[]>`, `-Rebuild`/`-r`, `-AddSmokeTestCredentials`; Story 06: `-Extensions` |

`-DmsBaseUrl` has two phase-specific uses with the same value in IDE continuation: `start-local-dms.ps1`
uses it only for the post-provision health wait, while `load-dms-seed-data.ps1` uses it as the
BulkLoadClient base URL. The wrapper forwards it at both phase points when `-LoadSeedData` is selected.
`-IdentityProvider` also remains phase-owned: startup uses it to configure the local auth environment, and
seed loading uses the same value to resolve the BulkLoadClient token endpoint. The wrapper forwards the
same developer-selected value to both phases instead of requiring either phase to infer it from the other.

The wrapper may expose a phase-owned parameter when the developer-facing happy path benefits from it;
exposing that parameter does not transfer ownership from the phase command to the wrapper.

---

## 7. Selector Resolution Examples

The selector resolution rule (auto-select on exactly one match; fail fast otherwise) applies to both
`provision-dms-schema.ps1` (§3.5) and `load-dms-seed-data.ps1` (§3.6) identically. The examples below
use `provision-dms-schema.ps1` but the behavior is the same for the seed phase.

```powershell
# One instance in CMS — auto-selected, no flag required
pwsh eng/docker-compose/provision-dms-schema.ps1

# Multiple instances — explicit ID selector required
pwsh eng/docker-compose/provision-dms-schema.ps1 -InstanceId 1

# Multiple instances — school-year selector (each year is targeted)
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026

# Fail-fast output when multiple instances exist and no selector is supplied:
# ERROR: 2 DMS instance(s) found in CMS without an explicit selector.
#        Supply -InstanceId <long> or -SchoolYear <int> to target a specific instance.
#        Exit code: non-zero.
```

**Wrapper path:** The thin wrapper reads selected instance IDs from the structured
`configure-local-dms-instance.ps1` result and forwards them as internal `-InstanceId` arguments to
downstream phases within the same invocation. Developers never copy-paste numeric instance IDs between phases when using
the wrapper. Public `-InstanceId` targeting is reserved for direct `provision-dms-schema.ps1` and
`load-dms-seed-data.ps1` invocations.

**Manual phase path:** Each phase command resolves its own selectors independently. No shared disk artifact
exists for this purpose; each phase contacts CMS directly.
