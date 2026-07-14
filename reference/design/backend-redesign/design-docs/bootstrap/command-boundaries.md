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
| **Inputs** | Story 00: `-ApiSchemaPath <path>` (direct filesystem ApiSchema source). Story 06 standard mode omits `-ApiSchemaPath`: with `-EnvironmentFile <path>`, the effective `SCHEMA_PACKAGES` list drives the complete package-backed schema set; without `-EnvironmentFile`, direct invocation falls back to the catalog-pinned core package. Custom or unpublished schema sets use Story 00 `-ApiSchemaPath`. |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/ApiSchema/` containing normalized schema JSON files, optional schema-adjacent static content, and `bootstrap-api-schema-manifest.json`; the staged workspace itself is the downstream schema and runtime-asset contract consumed by later phases and by DMS runtime (staged workspace loading delivered by Story 04, DMS-1154) |
| **Side effects** | Writes staged workspace; computes expected `EffectiveSchemaHash` via `api-schema-tools hash`; records manifest-relative paths for schema and optional static content in `bootstrap-api-schema-manifest.json`; writes the schema section of `eng/docker-compose/.bootstrap/bootstrap-manifest.json` with schema-selection mode, selected extensions, the effective schema hash, an ApiSchema workspace fingerprint, and the relative ApiSchema manifest path |
| **Failure conditions** | Story 00: missing `-ApiSchemaPath`; normalized-path collision; staged workspace exists with different content; `api-schema-tools hash` exits non-zero; fewer or more than 1 core schema present after staging. Story 06 adds: NuGet feed unreachable for package-backed materialization; the core package is missing the required asset-only ApiSchema payload; the core package contains only DLL-backed ApiSchema resources after the asset-only package switch-over. |
| **Must NOT do** | Start or depend on Docker services; modify `.env` or Docker Compose variables; perform DDL work; contact the Config Service; accept claims-related parameters |

**Mode-to-security contract (precise):** In Story 00 direct filesystem mode (`-ApiSchemaPath`), automatic
base security selection comes from the staged schema and available claims inputs. Any non-core schema that
needs additional security metadata remains detectable from the staged schema files and requires
developer-supplied claim fragments through `-ClaimsDirectoryPath`; this command does not reject that shape
because it does not own claims inputs. Story 06 package-backed standard mode must write the same
root bootstrap manifest schema facts so `prepare-dms-claims.ps1` can use the same security-selection contract.

**Boundary note:** The stable contract is the staged filesystem ApiSchema workspace, not the package shape
that produced it. Story 00 delivers only the direct `-ApiSchemaPath` acquisition path. Story 06
package-backed standard mode is an acquisition/materialization path that must converge on the same workspace after
asset-only packages replace DLL-backed package distribution. `-ApiSchemaPath` remains an expert
schema-selection path because seed-source defaults and extension artifact acquisition stay package-backed
there. This command validates schema inputs only; complete authorization coverage for custom resources is a
caller-owned contract expressed through `-ClaimsDirectoryPath` and runtime behavior. The
staged workspace shape follows
[`apischema-container.md`](apischema-container.md): schema JSON files are the hash/DDL/API authority, while
the manifest also indexes optional discovery/specification JSON and XSD assets for the runtime
content-loading story.

---

### 3.2 `prepare-dms-claims.ps1` — Claims and Security Staging

**Primary concern:** Stage `*-claimset.json` fragments into the workspace that the Config Service will read when staged runtime startup is enabled.

| Item | Detail |
|---|---|
| **Preconditions** | Staged ApiSchema workspace and `eng/docker-compose/.bootstrap/bootstrap-manifest.json` schema section produced by `prepare-dms-schema.ps1`. No Docker services required. |
| **Inputs** | `-ClaimsDirectoryPath <path>` (optional additive input except required when the staged schema set needs additional non-core security fragments) |
| **Outputs** | Staged workspace `eng/docker-compose/.bootstrap/claims/` containing claimset fragments; root bootstrap manifest `eng/docker-compose/.bootstrap/bootstrap-manifest.json` updated with the effective Config Service claims mode, relative claims directory, claims fingerprint, expected claims-verification checks, and seed namespace-prefix inputs |
| **Side effects** | Writes staged claims workspace; validates JSON well-formedness, no duplicate filenames, and no unknown effective claim set references; records a core baseline check plus expected `(claim set name, resource claim URI, action)` entries from staged fragments for later CMS readiness verification in the bootstrap manifest; records extension namespace prefixes for seed delivery in the same bootstrap manifest |
| **Failure conditions** | Duplicate filenames; malformed JSON in any fragment; unknown effective claim set reference; staged workspace exists with different content; staged schema set needs additional non-core security fragments and `-ClaimsDirectoryPath` is not supplied |
| **Must NOT do** | Contact Docker, the database, or the Config Service; perform schema resolution or hash computation; accept schema-selection parameters |

**Mode-to-security contract (precise):** This command always stages the automatic base claims set identified by the schema section of the bootstrap manifest from `prepare-dms-schema.ps1`. If the staged schema set is core only, the bootstrap manifest claims mode may stay in Embedded mode. If the staged schema set includes extension resources with matching security fragments, this command stages those fragments automatically and records Hybrid mode. If the staged schema set needs additional non-core security fragments from `-ApiSchemaPath`, `-ClaimsDirectoryPath` is required and its validated fragments are staged alongside any automatic base fragments. Bootstrap validates the supplied fragments structurally, but does not prove that they authorize every custom resource.

**Claim-set-reference validation contract:** This command validates only the claim set names that CMS
composition will use as effective authorization attachments: explicit
`resourceClaims[].claimSets[].name` entries on parent resource claims, plus the fragment top-level `name`
for non-parent resource claims that rely on it as the implicit claim set name. Non-parent
`resourceClaims[].claimSets[]` entries are rejected because the CMS composer does not use that shape for
non-parent grants; those fragments must use `authorizationStrategyOverridesForCRUD` with an effective
top-level fragment `name`. A fragment top-level `name` that is only a fragment/group label for explicit
parent-claim attachments is not by itself a claim set reference and must not be rejected merely because it
is absent from embedded `Claims.json`.

**Bootstrap manifest handoff contract:** `eng/docker-compose/.bootstrap/bootstrap-manifest.json` is the only
persisted compatibility and handoff state between bootstrap phases. The ApiSchema manifest under
`.bootstrap/ApiSchema/` remains only the runtime asset index for staged schema/content files. The root
bootstrap manifest records stable prepared inputs and fingerprints only:

```json
{
  "version": 1,
  "schema": {
    "selectionMode": "Standard",
    "selectedExtensions": [],
    "selectedPackages": ["EdFi.DataStandard52.ApiSchema@1.0.333"],
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

`schema.selectedPackages` records the `<packageId>@<version>` identity of each package that produced a
package-backed Standard-mode workspace (requested versions, never feed URLs); the bootstrap wrapper compares
it against the effective `SCHEMA_PACKAGES` value to decide whether a staged workspace is still current.
Expert `-ApiSchemaPath` staging omits the field.

It does not include data store IDs, credentials, URLs, Docker or container state, seed file lists, phase
progress, or resume checkpoints. Compose environment variables, absolute host paths, and mount-source values
are derived from the repo root plus the manifest's relative directories; they are not stored as additional
state. DMS compose services do not consume claimset fragment files, so `local-dms.yml` and
`published-dms.yml` must not mount `/app/additional-claims`; DMS reads authorization metadata from CMS.

**Boundary note:** Claim-fragment validation here is structural only: JSON shape, duplicate filenames, effective claim-set-name references, rejection of CMS-ignored non-parent explicit `claimSets`, and mechanical extraction of the expected verification entries. This phase does not inspect attachment overlap, reject duplicate `(resource claim, claim set name)` pairs, or perform semantic composition reasoning; CMS startup remains the authoritative composition gate. Built-in seed-package advertisement is owned by Story 02 / `load-dms-seed-data.ps1`; this phase only stages and validates the claims inputs that later seed delivery depends on. The bootstrap manifest is not a cross-invocation resume mechanism, mutable workflow checkpoint, or second control plane.

---

### 3.3 `start-local-dms.ps1` — Infrastructure Lifecycle

**Primary concern:** Docker stack management, local identity setup, and service health waiting.

| Item | Detail |
|---|---|
| **Preconditions** | Story 00 stages and validates the bootstrap manifest; Story 04 (DMS-1154, delivered) activates staged-schema and staged-claims runtime loading when a valid manifest is present. |
| **Inputs** | `-EnvironmentFile <path>` (select Docker Compose env file and shared local settings); `-Rebuild` / `-r`; `-IdentityProvider`; `-EnableConfig` (legacy compat, not a meaningful opt-out in the normative flow); `-EnableKafkaUI`; `-EnableSwaggerUI`; teardown flags `-d`/`-v`; `-AddExtensionSecurityMetadata` (applies only to no-manifest startup; in bootstrap mode staged claims activate from the manifest and this flag's non-bootstrap Hybrid fallback does not apply); split-startup switches `-InfraOnly` and `-DmsOnly` (mutually exclusive; the bootstrap wrapper uses them to run schema provisioning between infrastructure startup and DMS startup); `-DbOnly` (starts only the database container and waits for engine-appropriate readiness - `pg_isready` polling for PostgreSQL, `Wait-MssqlReady` for SQL Server - then stops; mutually exclusive with `-InfraOnly` and `-DmsOnly` on both scripts, with `-r` / `-Rebuild` on the local script, and, on `start-published-dms.ps1`, also with `-NoDataStore`, `-SchoolYearRange`, and `-AddSmokeTestCredentials`; a narrow phase slice for database-only startup that other orchestration can sequence around); `-DmsBaseUrl <url>` (valid only with `-InfraOnly`, not valid with `-DbOnly`; when set, the script starts infrastructure without the DMS container, waits for Config Service readiness and the claims-ready gate, then polls `<DmsBaseUrl>/health` until HTTP 200 is returned, with a 300-second timeout) |
| **Outputs** | Running Docker services; provider-specific local identity clients including `CMSReadOnlyAccess`; healthy Config Service; healthy DMS container (the `-DbOnly` shape outputs only a running, ready database container - no identity clients, Config Service, or DMS container) |
| **Side effects** | Docker Compose up/down; runs provider-specific local identity setup, including the fixed `CMSReadOnlyAccess` read-only client; activates manifest-selected staged claims and staged schema at startup when a valid bootstrap manifest is present (Story 04, delivered); calls `setup-openiddict.ps1 -InitDb` after PostgreSQL health; calls `setup-openiddict.ps1 -InsertData` after Config Service readiness (self-contained path); in bootstrap mode, skips default Debezium connector registration because the bootstrap relational schema does not include the legacy CDC tables the default connector targets; `-DbOnly` performs only `docker compose up db` and the matching readiness wait, with no identity, Config Service, Keycloak, or DMS side effects |
| **Failure conditions** | Docker compose start failure; health-wait timeout for any service; malformed or incomplete bootstrap manifest when present |
| **Must NOT do** | Resolve or validate ApiSchema files; inspect or write the staged-schema or staged-claims workspace; provision databases; enable the legacy `NEED_DATABASE_SETUP` / `EdFi.DataManagementService.Backend.Installer.dll` startup provisioning path; accept schema or claims parameters; configure data stores; create smoke-test or seed-loading CMS application credentials; load seed data. `-DbOnly` must not start Keycloak, run identity setup, start the Config Service, run the claims-ready gate, or start Kafka - it starts and waits on the database container only. **Note:** `start-published-dms.ps1` retains `-NoDataStore`, `-SchoolYearRange`, and `-AddSmokeTestCredentials` as transitional flags for the published-image workflow; the local `start-local-dms.ps1` is infrastructure-lifecycle-only as of DMS-1153. `start-published-dms.ps1` no longer accepts a `-LoadSeedData` switch of its own (removed; seed delivery on the published flow uses the same wrapper-level, API-based `-LoadSeedData` opt-in as the local flow); it also accepts `-DatabaseEngine`, mirroring the local flow's engine selection. |

**Boundary note:** Story 00 makes staged schema/security the prepared bootstrap contract. Story 04 (DMS-1154,
delivered) makes it the Docker runtime source of truth by activating staged schema and staged claims together
at startup when a valid manifest is present — activating only one side while the other remains on the
non-bootstrap env-file path could produce mismatched authorization metadata, so the two activations ship as
one boundary. Story 03 owns
the claims-ready gate mechanism: the manifest-driven authorization metadata check that verifies expected
resource claim URIs and actions against `/authorizationMetadata` after CMS `/health` is green, running
against currently-active claims sources. Because `/authorizationMetadata` serializes leaf resource claims
only, the gate asserts non-parent checks and defers checks marked `isParent` (parent grants materialize on
leaf descendants via hierarchy lineage); composed-hierarchy verification of parent grants belongs to the
claims-composition work. Once DMS health is confirmed, any later step is owned by wrapper orchestration or
by the developer invoking the next phase command explicitly.

The Must-NOT row is permanent for `start-local-dms.ps1`. DMS-1153 (`epics/16-bootstrap/03-entry-point-and-ide-workflow.md`)
made `start-local-dms.ps1` infrastructure-lifecycle-only by removing `-NoDataStore`, `-SchoolYearRange`,
`-LoadSeedData`, and `-AddSmokeTestCredentials`. At the time, `start-published-dms.ps1` retained all four as
transitional flags for the published-image workflow and was not subject to this de-scope. Its own
`-LoadSeedData` switch, which ran the direct-SQL database-template load through
`setup-database-template.psm1`, has since been removed; seed delivery on the published flow now uses only
the wrapper-level, API-based `-LoadSeedData` opt-in (see `bootstrap-published-dms.ps1`, Section 3.7).
`-NoDataStore`, `-SchoolYearRange`, and `-AddSmokeTestCredentials` remain on `start-published-dms.ps1`
unchanged. `start-published-dms.ps1` also accepts `-DatabaseEngine`, added after DMS-1153 to mirror the
local flow's SQL Server / PostgreSQL engine selection.

---

### 3.4 `configure-local-data-store.ps1` — Instance Setup

**Primary concern:** Configure DMS instances that downstream phases and IDE-hosted DMS depend on.

| Item | Detail |
|---|---|
| **Preconditions** | Config Service healthy and claims-loaded (Docker service ready). |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth, tenant scope, and database defaults); `-NoDataStore` (narrow reuse escape hatch: valid only when exactly one existing instance is present); `-SchoolYearRange <range>` (school-year path); `-AddSmokeTestCredentials` (creates CMS-only test application) |
| **Outputs** | One or more DMS instance records in CMS; `EdFiSandbox` application when `-AddSmokeTestCredentials` is set; structured success-pipeline result containing selected data store IDs and, when available, the pre-provisioned `CMSReadOnlyAccess` credential values needed by IDE guidance |
| **Side effects** | CMS API calls to `Add-DataStore` / `Add-DmsSchoolYearInstances`; optional CMS API calls for smoke-test credentials; may validate or report the `CMSReadOnlyAccess` client created by the local identity setup path; emits human-readable progress and guidance on non-success streams; no files written beyond CMS records |
| **Failure conditions** | Config Service unreachable; `-NoDataStore` with 0 or >1 existing instances; `-NoDataStore` with `-SchoolYearRange` (invalid combination) |
| **Must NOT do** | Create or scope the `CMSReadOnlyAccess` identity client (that belongs to `start-local-dms.ps1` through provider-specific local identity setup); use `/connect/register` to create `CMSReadOnlyAccess`; create `SeedLoader` credentials (those belong to `load-dms-seed-data.ps1`); perform DDL work; write persisted runtime state to disk for later phases; accept schema or claims parameters |

**Boundary note:** This phase creates or confirms the DMS instance records that downstream phases target.
The structured result is JSON-compatible and includes at least `SelectedDataStoreIds` and
may include `CMSReadOnlyAccess` credential fields from local identity setup for IDE guidance.
Human-readable text is separate from the success-pipeline object, so the thin wrapper never scrapes prose to
discover IDs or credentials. This phase uses the shared `-EnvironmentFile` local-settings resolver to
avoid hard-coded CMS, tenant, identity-provider, and PostgreSQL defaults. When phase commands are run separately,
downstream phases resolve target instances through their own explicit selectors (`-DataStoreId`,
`-SchoolYear`) via a CMS-backed lookup - a deliberate tradeoff that makes each phase independently
re-runnable without hidden disk artifacts.

---

### 3.5 `provision-dms-schema.ps1` — Authoritative Schema Provisioning

**Primary concern:** Invoke the SchemaTools/runtime-owned path to provision or validate target databases.

| Item | Detail |
|---|---|
| **Preconditions** | At least one resolvable DMS instance in CMS (explicit via `-DataStoreId` or `-SchoolYear`, or exactly one instance present for auto-selection); staged schema workspace from `prepare-dms-schema.ps1`; Config Service and the target database reachable. |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth, tenant scope, and database connection defaults); `-DataStoreId <long[]>` (explicit numeric DMS data store ID selector; omit when exactly one instance exists); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists); staged schema paths (read from `eng/docker-compose/.bootstrap/ApiSchema/`) |
| **Outputs** | Provisioned or validated databases for each target instance; printed IDE next-step guidance (staged schema path, `appsettings` values, `CMSReadOnlyAccess` credentials) after infra-only shape completes |
| **Side effects** | Invokes authoritative SchemaTools/runtime provisioning path; exits non-zero if provisioning or validation fails |
| **Failure conditions** | Zero matching instances found; multiple matching instances found without an explicit `-DataStoreId` or `-SchoolYear` selector; SchemaTools/runtime provisioning exits non-zero, including when target stored schema state is incompatible with the staged schema set; connection to target database fails; CMS data store connection string matches neither engine's dialect markers; a target's resolved dialect contradicts the effective environment's `DMS_DATASTORE` |
| **Must NOT do** | Accept user-facing schema-selection parameters; repair or work around a failed SchemaTools path; run inside DMS startup via `AppSettings__DeployDatabaseOnStartup`, `NEED_DATABASE_SETUP`, `EdFi.DataManagementService.Backend.Installer.dll`, or any container entrypoint/pre-launch hook; silently reuse a database provisioned for a different schema selection; resolve schema files; create or mutate instance records in CMS |

**Boundary note:** `AppSettings__DeployDatabaseOnStartup=false` is always set, and the legacy
`NEED_DATABASE_SETUP` / `EdFi.DataManagementService.Backend.Installer.dll` startup path is disabled or
removed for the DMS-916 bootstrap flow. Schema provisioning is entirely owned by this phase; DMS startup
never performs it. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is
supplied, auto-select it; when multiple instances exist and no explicit `-DataStoreId` or `-SchoolYear` is
provided, fail fast with guidance to supply an explicit selector. CMS lookup and database target resolution
use the shared `-EnvironmentFile` local-settings resolver, so direct phase invocation and wrapper
orchestration target the same local environment. Dialect: `pgsql`|`mssql`, auto-detected per target
from the shape of the CMS data-store connection string. Definitive PostgreSQL markers (`host`,
`username`, `port`, `sslmode`) are checked first, then SQL Server markers; a connection string
carrying neither is rejected with an actionable error at `Resolve-TargetDialect`. The
Docker-internal database host is translated to the host-side mapped port for both engines.
`Resolve-ExpectedProvisioningDialect` derives the dialect the effective environment expects from
`DMS_DATASTORE`, and `New-ProvisionTarget` fails fast with remediation guidance before any
SchemaTools invocation when a target's resolved dialect contradicts it.

---

### 3.6 `load-dms-seed-data.ps1` — Seed Delivery

**Primary concern:** Materialize ODS XML interchange files and invoke BulkLoadClient against a healthy DMS endpoint.

| Item | Detail |
|---|---|
| **Preconditions** | Live DMS process healthy at the target base URL (`/health` returns 200); CMS remains reachable so this phase can create `SeedLoader` credentials immediately before BulkLoadClient invocation; bootstrap manifest exists with schema, claims, and seed sections compatible with the requested seed-source flags; the repo-pinned BulkLoadClient XML mode is compatible with the target DMS discovery/dependency metadata, OAuth, data, and XSD metadata or staged-XSD inputs. See Story 02. |
| **Inputs** | `-EnvironmentFile <path>` (select local settings for CMS URL, auth defaults, tenant scope, and Docker-local DMS URL); `-BootstrapManifestPath <path>` (optional override for the bootstrap manifest; defaults to `eng/docker-compose/.bootstrap/bootstrap-manifest.json`); `-DataStoreId <long[]>` (explicit numeric DMS data store ID selector; omit when exactly one instance exists); `-DmsBaseUrl <url>` (BulkLoadClient target endpoint; defaults to the Docker-local DMS URL resolved from the local settings and must be explicit for IDE-hosted seed loading); `-IdentityProvider` (auth provider used to resolve the BulkLoadClient OAuth endpoint; defaults to the provider resolved from the local settings when that parameter is omitted); `-SeedTemplate Minimal\|Populated` (mutually exclusive with `-SeedDataPath`); `-SeedDataPath <path>` (custom ODS XML interchange directory); `-AdditionalNamespacePrefix <string[]>` (optional additive namespace prefixes for custom seed authorization, especially `-SeedDataPath` payloads with agency or custom namespaces); `-SchoolYear <int[]>` (school-year filter; omit when exactly one instance exists) |
| **Outputs** | Seeded DMS instance(s); seed workspace cleaned up on success |
| **Side effects** | Creates `SeedLoader` application via `Add-CmsClient` / `Add-Application` using the de-duplicated baseline seed namespace prefixes, selected extension namespace prefixes, and any `-AdditionalNamespacePrefix` values; resolves BulkLoadClient package; resolves the OAuth URL from `-IdentityProvider`; materializes XML interchange files into ignored seed workspaces using BulkLoadClient-discoverable target paths such as `InterchangeName.xml`, `InterchangeName-*.xml`, and `InterchangeName/*.xml`; invokes BulkLoadClient once per selected target and seed tier with the route-qualified DMS base URL, `-d` data directory, `-w` working directory, `-k`/`-s` credentials, `-o` OAuth URL, and either `-x` staged XSD directory or `-z` XSD metadata URL; retains seed workspace on failure |
| **Failure conditions** | Missing, malformed, unsupported-version, or incomplete bootstrap manifest; bootstrap manifest schema section says expert mode (`-ApiSchemaPath`) and `-SeedTemplate` is specified; zero matching instances found; multiple matching instances found without an explicit `-DataStoreId` or `-SchoolYear` selector; unsupported `-IdentityProvider`; `-SeedTemplate` and `-SeedDataPath` both supplied; blank or malformed `-AdditionalNamespacePrefix` value; BulkLoadClient exits non-zero; package-backed built-in seed source unavailable; catalog-advertised built-in seed package for an extension unavailable; DMS health endpoint unreachable; XML seed source cannot be materialized into a valid BulkLoadClient data directory; required XSD inputs are unavailable |
| **Must NOT do** | Create `CMSReadOnlyAccess` (that belongs to `start-local-dms.ps1` through provider-specific local identity setup) or smoke-test credentials (those belong to `configure-local-data-store.ps1`); reuse `SeedLoader` credentials for smoke tests; perform DDL work; accept schema or claims parameters |

**Boundary note:** This phase uses existing BulkLoadClient XML interchange loading as the API-based replacement for the deprecated direct-SQL seed path. Direct invocation of `load-dms-seed-data.ps1` always performs seed delivery; it does not accept a second `-LoadSeedData` switch. Selector resolution rule: when exactly one DMS instance exists in CMS and no selector is supplied, auto-select it; when multiple instances exist and no explicit `-DataStoreId` or `-SchoolYear` is provided, fail fast with guidance to supply an explicit selector. Endpoint resolution is phase-owned: `load-dms-seed-data.ps1` never infers the IDE URL from a prior `start-local-dms.ps1` invocation. Manual IDE-hosted seed loading passes `-DmsBaseUrl` explicitly; DMS-1152 wrappers do not expose the deferred IDE-hosted continuation flags. Identity-provider resolution is also phase-owned: direct seed invocation passes `-IdentityProvider` when the running environment uses a non-default provider; otherwise the seed phase uses the provider from the shared `-EnvironmentFile` resolver and does not rely on process environment variables left behind by an earlier `start-local-dms.ps1` call. `load-dms-seed-data.ps1` consumes schema mode, selected extensions, and extension namespace prefixes from the bootstrap manifest instead of accepting schema or claims parameters; it owns any seed-catalog lookup for built-in extension seed packages. `-AdditionalNamespacePrefix` is a declared authorization input for SeedLoader vendor creation only; it does not cause bootstrap to inspect XML files, infer missing extensions, or synthesize claim grants.

---

### 3.7 `bootstrap-local-dms.ps1` — Thin Convenience Wrapper (Optional)

**Delivery status:** Convenience packaging plus a narrow slice of cross-phase orchestration policy: materializing a per-invocation `.bootstrap/.env.derived` with the bootstrap profile (loose `FAILURE_RATIO` so intra-tier BulkLoadClient retries don't trip the circuit breaker); unifying `-IdentityProvider` across the start and seed phases so a single wrapper invocation can't run infra under one provider and authenticate seeds under another; expanding the developer-facing `-SchoolYearRange "YYYY-YYYY"` into the `-SchoolYear <int[]>` array the seed phase consumes. The wrapper is optional and owns no phase-specific policy. The composable phase commands remain the authoritative bootstrap contract for DMS-916.

**Primary concern:** Sequence the above phase commands in the correct order for the common happy path.

| Item | Detail |
|---|---|
| **Preconditions** | None additional beyond what phase commands require. |
| **Inputs** | `-EnvironmentFile <path>` (forwarded to the start and seed phases); `-IdentityProvider` (resolved once and forwarded to `start-local-dms.ps1` or `start-published-dms.ps1`, and also to `load-dms-seed-data.ps1` when seed loading is selected); `-EnableKafkaUI`, `-EnableSwaggerUI`, `-EnableConfig`, and `-AddExtensionSecurityMetadata` (forwarded to the selected start phase; `-EnableConfig` is forced when seed loading is selected); `-SchoolYearRange <range>` (forwarded to the selected start phase and expanded to `-SchoolYear <int[]>` for the seed phase when seed loading is selected); `-LoadSeedData` (wrapper-level opt-in that causes the wrapper to invoke `load-dms-seed-data.ps1`); `-SeedTemplate`, `-SeedDataPath <path>`, and `-AdditionalNamespacePrefix <string[]>` (forwarded to `load-dms-seed-data.ps1` when seed loading is selected); `-NoDataStore` and `-AddSmokeTestCredentials` (forwarded to `configure-local-data-store.ps1` during the configure -> provision sequence); **`bootstrap-local-dms.ps1` only** (DMS-1153): `-InfraOnly` (pre-DMS stop shape: run infrastructure startup, configure, provision, print IDE next-step guidance, then stop; terminal for that invocation); `-DmsBaseUrl <url>` (health-wait continuation shape: valid only with `-InfraOnly`; withheld from the initial `start-local-dms.ps1` invocation and used only for the post-provision health-wait; when `-LoadSeedData` is also set, forwarded to `load-dms-seed-data.ps1`) — **`bootstrap-published-dms.ps1` does not gain these parameters in DMS-1153**; **`bootstrap-local-dms.ps1` only** (DMS-1272): `-d` / `-v` teardown (short-circuits before any staging/configure/provision/DMS/seed orchestration and delegates to `start-local-dms.ps1 -d [-v -RemoveBootstrap]`; `-d -v` also deletes volumes and removes the `.bootstrap` workspace; `-v` without `-d` is rejected; only `-EnvironmentFile`, `-IdentityProvider`, `-EnableKafkaUI`, `-EnableSwaggerUI`, and `-DatabaseEngine` are forwarded to teardown) — **`bootstrap-published-dms.ps1` does not gain the teardown flags** |
| **Outputs** | Delegated entirely to the phase commands it calls |
| **Side effects** | Delegates to phase commands; prints next-step guidance when a phase is intentionally omitted |
| **Failure conditions** | Propagates non-zero exit from any called phase command |
| **Must NOT do** | Implement phase-specific behavior; own schema logic; perform claims parsing; inspect database state; synthesize credentials; implement retry or fallback logic; persist runtime state across invocations (per-invocation transient artifacts under `.bootstrap/` are permitted for the orchestration-policy slice listed in the delivery status above); absorb any concern owned by a phase command |

**Boundary note:** The wrapper owns orchestration only: it may sequence phase commands, forward
developer-facing parameters, expand wrapper-owned convenience inputs into phase-owned parameters, and print
next-step guidance. The DMS-1152 wrapper does not expose `-DataStoreId`; explicit ID
targeting is phase-command-only. The wrapper's school-year flag is `-SchoolYearRange`, matching the existing
start-script workflow it calls; when seed loading is selected, it expands that range to the
`-SchoolYear <int[]>` selector consumed by `load-dms-seed-data.ps1`. Downstream manual selector flags remain
`-SchoolYear <int[]>` on `provision-dms-schema.ps1` and `load-dms-seed-data.ps1`. If seed loading is selected,
it forwards the selected `-IdentityProvider` so the seed phase resolves the matching OAuth endpoint. It also
forwards `-AdditionalNamespacePrefix` to the seed phase when supplied. It
forwards the same `-EnvironmentFile` to every phase that contacts local services so manual and wrapper flows
resolve the same CMS, tenant, DMS, and database defaults. It must not implement
phase-specific behavior, retry or fallback logic, persisted resume state, schema provisioning, CMS
configuration, or seed loading directly, and it never parses human-readable output to recover phase results.

Standard-mode schema staging is package-backed and has no `-Extensions` parameter on any wrapper or phase
command. When no workspace is staged, `bootstrap-local-dms.ps1` and `bootstrap-published-dms.ps1` pass their
effective environment to `prepare-dms-schema.ps1`, which stages the full `SCHEMA_PACKAGES` set. The default
local DS 5.2 profile is core + TPDM; DS 6.1 is core-only because TPDM is folded into core. Direct standard-mode
invocation without `-EnvironmentFile` retains the catalog-pinned core-only fallback. An already-staged workspace (including a manual expert
`-ApiSchemaPath` flow) is reused (or fails fast on mismatch) per the prepare-dms-schema.ps1 rerun contract
rather than being rewritten. Standard-mode reuse requires an exact, non-empty `selectedPackages`
`<packageId>@<version>` set; a missing/malformed identity or any package-set mismatch stops before package
downloads, Docker startup, or CMS activity and directs the operator to stop the stack and remove the entire
`eng/docker-compose/.bootstrap` workspace. DMS-1255 intentionally does not delete or partially repair a
workspace that may still be bind-mounted.

**DMS-1271 handoff:** guarded automatic replacement is follow-up scope. Before replacing a mismatched
workspace it must prove the stack is stopped, remove the whole `.bootstrap` tree (never schema alone), and
regenerate schema, claims, and seed handoff state together. Until that contract is implemented and tested,
future reviews should treat the DMS-1255 fail-fast behavior as intentional, not as a missing automatic
re-stage. Extension/custom schema sets use the expert `-ApiSchemaPath` path. Other broader
wrapper consolidation flags
such as `-ApiSchemaPath`, `-ClaimsDirectoryPath`, and `-Rebuild` remain deferred to their owning bootstrap
stories. DMS-1153 delivered the local wrapper IDE workflow
shapes: `-InfraOnly` (pre-DMS stop; distinct from the start-script split-startup switch of the same name
that the wrapper drives internally) and `-DmsBaseUrl` (health-wait continuation, valid only with
`-InfraOnly`, withheld from the initial start invocation) on `bootstrap-local-dms.ps1` only.
`bootstrap-published-dms.ps1` does not gain these flags. The wrapper also forwards `-NoDataStore` and
`-AddSmokeTestCredentials` to `configure-local-data-store.ps1` (which owns both per §3.4) as part of its
configure → provision sequencing.

---

## 4. Dependency Chain

```
prepare-dms-schema.ps1
  -> prepare-dms-claims.ps1
       -> start-local-dms.ps1  (starts PostgreSQL or SQL Server, Keycloak/OpenIddict, Config Service, and DMS)
            -> configure-local-data-store.ps1  (CMS HTTP API ready)
                 -> provision-dms-schema.ps1  (-DataStoreId or -SchoolYear selector)
                      -> load-dms-seed-data.ps1  (-DataStoreId or -SchoolYear selector, optional -DmsBaseUrl for direct seed target, -IdentityProvider for OAuth endpoint, live DMS + SeedLoader credentials)
```

Each phase begins only when all of its required inputs are ready. No phase polls for or waits on
services it does not consume. Phases can be invoked individually for re-runs, debugging, and testing
without re-executing the full chain.

For the common developer happy path, `bootstrap-local-dms.ps1` / `bootstrap-published-dms.ps1` is the
recommended entry point that orchestrates this sequence (prepare → infra → configure → provision → DMS,
plus optional seed). Direct invocation of `start-local-dms.ps1` / `start-published-dms.ps1` is supported for
diagnostics and partial-phase orchestration via the `-InfraOnly` / `-DmsOnly` switches, where the caller is
responsible for ensuring schema provisioning has run before DMS serves requests. This convenience framing
does not change the normative contract in §2: the phase commands remain authoritative and the wrapper owns
no phase-specific policy.

---

## 5. Non-Overlap Guarantees

The following concerns are each owned by exactly one phase:

| Concern | Owner | All other phases must NOT |
|---|---|---|
| Schema file resolution and staging | `prepare-dms-schema.ps1` | Re-resolve or re-stage schema |
| `EffectiveSchemaHash` computation | `prepare-dms-schema.ps1` | Compute an alternate hash |
| Claims fragment staging, validation, and bootstrap manifest claims section | `prepare-dms-claims.ps1` | Accept or validate claims parameters; write or reinterpret CMS claims startup policy |
| Docker service startup and health waiting | `start-local-dms.ps1` | Start or stop Docker services |
| DMS instance record creation | `configure-local-data-store.ps1` | Create or modify DMS instance records |
| Downstream instance target selection | `provision-dms-schema.ps1`, `load-dms-seed-data.ps1` (each phase resolves its own selectors) | Resolve target instances on behalf of another phase |
| `CMSReadOnlyAccess` client provisioning | `start-local-dms.ps1` provider-specific local identity setup | Create or scope `CMSReadOnlyAccess`, especially through CMS registration APIs such as `/connect/register` unless those APIs support read-only scope selection |
| Smoke-test credentials | `configure-local-data-store.ps1` | Create `EdFiSandbox` application |
| DDL provisioning and hash validation | `provision-dms-schema.ps1` | Perform or bypass DDL work |
| `SeedLoader` credential creation and namespace-prefix composition | `load-dms-seed-data.ps1` | Create or reference SeedLoader credentials or infer seed namespace prefixes |
| BulkLoadClient seed invocation | `load-dms-seed-data.ps1` | Invoke BulkLoadClient |
| Orchestration sequence | `bootstrap-local-dms.ps1` | Replicate the full phase sequence |

---

## 6. Parameter Surface by Owner

Each phase accepts only the parameters relevant to its concern.

| Phase command | Owned parameters |
|---|---|
| `prepare-dms-schema.ps1` | `-ApiSchemaPath` (Story 00 expert mode); `-EnvironmentFile <path>` (standard-mode `SCHEMA_PACKAGES` source); without either parameter, standard mode uses the catalog-pinned core-only fallback |
| `prepare-dms-claims.ps1` | `-ClaimsDirectoryPath` |
| `start-local-dms.ps1` | `-EnvironmentFile <path>`, `-Rebuild`/`-r`, `-IdentityProvider`, `-EnableConfig` (legacy compat), `-EnableKafkaUI`, `-EnableSwaggerUI`, `-DatabaseEngine` (`postgresql`/`mssql`; selects the database compose file), `-d`/`-v`, `-AddExtensionSecurityMetadata` (no-manifest startup only; bootstrap mode activates staged claims from manifest), split-startup switches `-InfraOnly`, `-DmsOnly`, and `-DbOnly` (database container plus readiness only), `-DmsBaseUrl <url>` (valid only with `-InfraOnly`; not valid with `-DbOnly`) |
| `configure-local-data-store.ps1` | `-EnvironmentFile <path>`, `-DatabaseEngine postgresql\|mssql`, `-NoDataStore`, `-SchoolYearRange`, `-AddSmokeTestCredentials` |
| `provision-dms-schema.ps1` | `-EnvironmentFile <path>`, `-DatabaseEngine postgresql\|mssql`, `-DataStoreId <long[]>`, `-SchoolYear <int[]>` |
| `load-dms-seed-data.ps1` | `-EnvironmentFile <path>`, `-BootstrapManifestPath <path>`, `-DataStoreId <long[]>`, `-DmsBaseUrl <url>`, `-IdentityProvider`, `-SeedTemplate`, `-SeedDataPath`, `-AdditionalNamespacePrefix <string[]>`, `-SchoolYear <int[]>` |
| `bootstrap-local-dms.ps1` | DMS-1152: `-EnvironmentFile <path>`, `-IdentityProvider`, `-EnableKafkaUI`, `-EnableSwaggerUI`, `-EnableConfig`, `-AddExtensionSecurityMetadata`, `-SchoolYearRange`, `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath <path>`, `-AdditionalNamespacePrefix <string[]>`; DMS-1238: `-DatabaseEngine` (`postgresql`/`mssql`; forwarded to the start and configure phases and to teardown); DMS-1151: `-NoDataStore`, `-AddSmokeTestCredentials` (forwarded to configure); DMS-1153: `-InfraOnly`, `-DmsBaseUrl <url>` (local wrapper only); DMS-1272: `-d`, `-v` (teardown, local wrapper only; `-d -v` implies `start-local-dms.ps1 -RemoveBootstrap`) |
| `bootstrap-published-dms.ps1` | Same as `bootstrap-local-dms.ps1` except **does not** include `-InfraOnly`, `-DmsBaseUrl` (DMS-1153 IDE workflow parameters are local-only), or the `-d`/`-v` teardown flags (DMS-1272, local-only). It accepts `-DatabaseEngine postgresql\|mssql`, mirroring the local wrapper's engine selection; `start-published-dms.ps1` no longer accepts a direct-SQL `-LoadSeedData` switch of its own, so seed delivery on the published flow uses the same API-based, wrapper-level `-LoadSeedData` opt-in as the local flow |

`-DmsBaseUrl` is phase-owned by direct `load-dms-seed-data.ps1` invocation for seed delivery. The local
wrapper also accepts `-DmsBaseUrl` (with `-InfraOnly`) as a convenience input for the IDE health-wait
continuation shape delivered by DMS-1153; this does not transfer seed-phase ownership.
`-IdentityProvider` also remains phase-owned: startup uses it to configure the local auth environment, and
seed loading uses the same value to resolve the BulkLoadClient OAuth endpoint. The wrapper forwards the
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
pwsh eng/docker-compose/provision-dms-schema.ps1 -DataStoreId 1

# Multiple instances — school-year selector (each year is targeted)
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026

# Fail-fast output when multiple instances exist and no selector is supplied:
# ERROR: 2 DMS instance(s) found in CMS without an explicit selector.
#        Supply -DataStoreId <long> or -SchoolYear <int> to target a specific instance.
#        Exit code: non-zero.
```

**Wrapper path:** The thin wrapper reads selected data store IDs from the structured
`configure-local-data-store.ps1` result and forwards them as internal `-DataStoreId` arguments to
downstream phases within the same invocation. Developers never copy-paste numeric data store IDs between phases when using
the wrapper. Public `-DataStoreId` targeting is reserved for direct `provision-dms-schema.ps1` and
`load-dms-seed-data.ps1` invocations.

**Manual phase path:** Each phase command resolves its own selectors independently. No shared disk artifact
exists for this purpose; each phase contacts CMS directly.
