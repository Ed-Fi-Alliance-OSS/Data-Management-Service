---
jira: DMS-1150
jira_url: https://edfi.atlassian.net/browse/DMS-1150
---

# Story: Bootstrap Schema and Security Selection

> **Superseded note:** The `-Extensions` references below reflect this completed story's original
> scoping, when a named / standard-mode `-Extensions` surface was still anticipated for Story 06. The story
> instead **removed `-Extensions` entirely**: standard mode (omit `-ApiSchemaPath`) is package-backed
> **core-only**, and extension/custom schema sets use the expert `-ApiSchemaPath` path this story owns. The
> historical acceptance criteria and tasks below are left intact as the record of what Story 00 delivered; see
> [`06-package-backed-standard-schema-selection.md`](06-package-backed-standard-schema-selection.md) for the
> authoritative current contract.

## Description

Implement the first schema and security input slice for developer bootstrap over the stable direct filesystem
contract. This slice covers:

- direct filesystem schema bootstrap through `-ApiSchemaPath` for core-only, core plus known mapped
  extensions, and unmapped custom schema/security/seed inputs,
- additive security staging through `-ClaimsDirectoryPath`,
- staging base security fragments for the selected schema set when those schemas are already present in the
  direct filesystem input and are mapped by the Story 00 v1 claims lookup.

This story intentionally does not implement package-backed standard selection through omitted `-Extensions`
or named `-Extensions`. Those modes remain in the main bootstrap design as the target day-to-day developer
flow, but they require asset-only ApiSchema packages and are implemented in Story 06
after the MetaEd packaging work in Story 05.

It also covers the matching CMS claims-loading behavior, including additive staging of extension-derived and
developer-supplied `*-claimset.json` fragments into one workspace directory. The selected schema set must be
materialized once into the normalized file-based ApiSchema asset container at
`eng/docker-compose/.bootstrap/ApiSchema/`. Direct filesystem ApiSchema loading through `-ApiSchemaPath` is
the stable core contract. Later package-backed selection is an input-materialization path that must produce
the same workspace. The schema JSON files in that workspace are the stable target source for all schema
consumers, but Story 00 activates only the staging, hashing, and manifest handoff path:

- `dms-schema hash`,
- downstream schema provisioning input,
- future Docker-hosted and IDE-hosted DMS startup after Story 04 updates runtime content loading.

The same workspace also carries optional schema-adjacent static content and
`bootstrap-api-schema-manifest.json` as defined in [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md).
That manifest indexes normalized schema and content paths, but it is not a second schema authority.

Bootstrap must not invent a second schema fingerprint or a second schema-resolution path.
In this story, the selected schema set drives the DDL target, the hash-validation path, and the exact
physical schema footprint for the run. A direct filesystem input containing only the core schema yields only
core tables. Core plus a known mapped extension such as Sample yields the tables required by that combined
schema set. Unmapped extension schemas (a custom extension outside the built-in map) are not
silently substituted by Sample, Homograph, TPDM, or any default mapping. A database provisioned for a different
extension selection is incompatible existing state for this story's bootstrap run.

Within the composable bootstrap design, schema selection and asset-container staging are owned exclusively by the
`prepare-dms-schema.ps1` phase command (see `command-boundaries.md` Section 3.1). Claims and security staging are
owned exclusively by `prepare-dms-claims.ps1` (see `command-boundaries.md` Section 3.2). Both commands run without
Docker services and hand their staged outputs — the staged schema workspace, staged claims workspace, and root
bootstrap manifest — to the downstream infrastructure, provisioning, and seed phases.

This story owns consuming and normalizing the target asset-container contract from already-materialized
filesystem inputs. Updating DMS runtime content loading to read the workspace is Story 04. Updating MetaEd
packaging to publish asset-only ApiSchema NuGet packages is Story 05; that package-production work enables a
Story 06 package-backed standard-mode slice but does not replace or deprecate direct filesystem loading.

**Mode-to-security summary (normative for Story 00):** The effective staged schema set from `-ApiSchemaPath`
is the single source of truth inside the prepared workspace for the staged ApiSchema files and for the
matching base staged claimset fragments available for the run through the Story 00 v1 mapped-extension lookup.
If `-ApiSchemaPath` stages unmapped non-core schemas that need additional security metadata,
`-ClaimsDirectoryPath` is required for caller-supplied fragments. No later phase re-derives or replaces that
security selection. Story 06 package-backed `-Extensions` mode must feed the same root bootstrap manifest
schema contract and claims-staging contract rather than introducing a second path.

## Acceptance Criteria

- Story 00 `prepare-dms-schema.ps1` requires `-ApiSchemaPath`. Omitting `-ApiSchemaPath` or supplying
  `-Extensions` fails fast with a message that package-backed standard mode is deferred until Story 06.
- Direct filesystem `-ApiSchemaPath` loading is the only schema input contract delivered by this story.
  It normalizes developer-supplied `ApiSchema*.json` files and any optional schema-adjacent static content
  through the staged workspace, requires the staged result to be exactly one core schema plus zero or more
  extensions, and drives automatic base security selection from the staged schema and available claims inputs.
- The staged ApiSchema workspace contains `bootstrap-api-schema-manifest.json` with deterministic
  manifest-relative paths for each selected project's normalized schema file and optional static content. This
  ApiSchema manifest is a runtime asset index only.
- Story 00 makes staged schema/security the prepared bootstrap contract, not the Docker runtime source of
  truth. Startup may validate the root bootstrap manifest, but DMS continues using DLL-backed schema
  assemblies and CMS continues using the non-staged claims path until Story 04 switches both runtime inputs
  together.
- `prepare-dms-schema.ps1` writes the schema section of
  `eng/docker-compose/.bootstrap/bootstrap-manifest.json` with schema selection mode (`ApiSchemaPath`),
  selected extension names, expected `EffectiveSchemaHash`, an ApiSchema workspace fingerprint, and the
  relative ApiSchema manifest path. In this story, selected extension names are the schema
  `projectEndpointName` values from non-core staged `ApiSchema*.json` files, normalized for manifest
  comparison as lower-case short names such as `sample` and `homograph`. Claims staging may still use
  `projectName` for its v1 shipped-fragment lookup. Extension namespace prefixes are recorded later in the
  seed section by claims staging.
- Bootstrap detects normalized-path collisions before finalizing the staged ApiSchema workspace.
- Same-checkout reruns reuse the existing staged ApiSchema workspace only when the intended schema set is
  identical (matching `EffectiveSchemaHash` and workspace fingerprint). If the intended schema inputs
  differ, `prepare-dms-schema.ps1` fails fast with teardown guidance rather than rewriting a workspace that
  may still be bind-mounted into a running DMS container. This mirrors the claims-workspace rerun
  protection and matches `command-boundaries.md` §3.1 ("staged workspace exists with different content").
- Workspace normalization in `prepare-dms-schema.ps1` is path/container normalization only: it produces the
  deterministic `schemas/<project>/ApiSchema.json` and `content/<project>/...` layout described in
  `apischema-container.md` without rewriting the JSON payloads. Runtime-only payloads such as
  `projectSchema.openApiBaseDocuments`, `resourceSchemas[*].openApiFragments`, and
  `abstractResources[*].openApiFragment` remain in the staged files so the Story 04 Docker-hosted and
  IDE-hosted DMS startup path can read complete schema content. Hash-time stripping (see
  `Core/Startup/ApiSchemaInputNormalizer.cs`) is an in-memory transform applied while computing
  `EffectiveSchemaHash` and must not be written back to the staged workspace.
- Package-backed selection, asset-only NuGet package extraction, package rejection for DLL-only packages,
  no-argument core-only convenience, and named `-Extensions` convenience are not Story 00 acceptance
  criteria; they are Story 06 acceptance criteria.
- `-ClaimsDirectoryPath` is required with `-ApiSchemaPath` when the staged set needs additional non-core
  security metadata, and additive otherwise.
- `-ClaimsDirectoryPath` is additive-only: fragments may attach permissions only to effective claim set
  references already declared in the embedded `Claims.json`. Bootstrap fails fast when a staged fragment
  references an unknown effective claim set name.
- `-ApiSchemaPath` mode satisfies the DMS-916 requirement that claimset loading is automatic from the
  selected schema and available claims inputs. It validates staged schema normalization and caller-supplied
  fragment structure, but it does not infer or guarantee authorization coverage for arbitrary custom non-core
  resources. Runtime authorization failures for incomplete expert-supplied fragments remain possible.
- Same-checkout reruns reuse the existing staged claims workspace only when the intended fragment set is
  identical. If the intended security inputs differ, bootstrap fails fast with teardown guidance rather than
  rewriting a directory that may still be bind-mounted into CMS or attempting in-place replacement of
  populated CMS claims data.
- Claim-fragment validation in bootstrap is structural only: staged fragments must be
  parseable, must target effective claim set references that already exist in embedded `Claims.json`, must
  not use explicit `resourceClaims[].claimSets[]` on non-parent claims because CMS composes those through the
  fragment top-level `name` plus `authorizationStrategyOverridesForCRUD`, and must not collide by filename
  in the staged workspace. The precise reference rule matches `command-boundaries.md`: explicit
  `resourceClaims[].claimSets[].name` entries are valid only for parent resource claims, and the fragment
  top-level `name` is validated when CMS composition uses it as the implicit claim set name for a non-parent
  resource claim. A top-level fragment/group label for explicit parent-claim attachments is not itself
  rejected merely because it is absent from embedded `Claims.json`. Bootstrap does not check attachment
  overlap or perform semantic composition reasoning; CMS startup is the authoritative composition gate for
  those outcomes.
- Extension-derived and developer-supplied claimset fragments are staged into one workspace directory with
  fail-fast validation for:
  - missing directory,
  - no `*-claimset.json` files,
  - malformed JSON,
  - filename collisions,
  - unknown effective claim set references in staged fragments.
- Every bootstrap-managed extension fragment preserves ordinary developer access by attaching `EdFiSandbox`
  permissions for the extension resources it contributes. Story 00 stages each shipped fragment as-is; if a
  shipped fragment already attaches `SeedLoader` permissions for its extension seed resources, those
  attachments pass through staging unchanged. Story 00 does not decide which extensions advertise built-in
  seed support and does not author or require `SeedLoader` attachments on fragments that lack them — Story
  02 owns built-in seed-package advertisement and the `SeedLoader` coverage requirement enforced at seed
  delivery.
- CMS will consume the staged claims workspace through the Config Service `/app/additional-claims` mount target
  when Story 04 enables staged runtime startup. Story 00 records the manifest-selected
  `eng/docker-compose/.bootstrap/claims/` workspace, claims mode, claims fingerprint, and expected
  claims-verification checks, but `start-local-dms.ps1` does not translate that manifest section into
  `local-config.yml` environment and bind-mount inputs until DMS also reads the matching staged ApiSchema
  workspace.
- `prepare-dms-claims.ps1` updates `eng/docker-compose/.bootstrap/bootstrap-manifest.json` after claims
  staging succeeds. The claims section records only the effective claims startup inputs and fingerprints; the
  seed section records only extension namespace prefixes. The root manifest must not contain built-in
  seed-package entries, resource definitions, claim grants, instance IDs, credentials, URLs, Docker state,
  environment settings, seed file paths, phase progress, or resume checkpoints.
- Story 00 does not own built-in seed-package advertisement. It stages and validates claims inputs; Story 02
  decides when built-in seed packages are available and enforces the `SeedLoader` requirements for that path.
- The normative DMS-916 bootstrap surface does not preserve standalone `-AddExtensionSecurityMetadata` as a
  security-selection path. ApiSchema-driven staging is the only security-selection mechanism within this
  story's contract.
- Bootstrap computes the expected `EffectiveSchemaHash` for the selected schema set using the existing DMS
  hashing algorithm over the staged schema files.
- The resolved schema set drives the DDL target/version/`EffectiveSchemaHash` validation path and the exact
  physical table set for the run. A database provisioned for a different extension selection is not treated
  as an aligned superset.
- CMS claims mode is driven from the staged inputs:
  - Embedded mode for core-only bootstrap,
  - Hybrid mode when one or more staged fragments exist.
- Automatic extension-fragment selection in `prepare-dms-claims.ps1` is deterministic and explicit. The
  command holds a short in-script lookup keyed by extension project identity from the staged schema set
  (`projectName` from each non-core `ApiSchema*.json`) that maps to the exact shipped fragment filename
  under `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/` (for v1:
  `Sample` → `004-sample-extension-claimset.json`, `Homograph` → `005-homograph-extension-claimset.json`).
  The same lookup records any built-in extension seed namespace prefix known to the DMS-916 bootstrap
  contract; v1 records `Sample` → `uri://sample.ed-fi.org`. Entries without a known built-in seed namespace
  prefix write no prefix to the root bootstrap manifest, and bootstrap must not infer prefixes from arbitrary
  direct filesystem schema content.
  Core-baseline fragments (`001-namespace-claimset.json`, `002-nofurtherauth-claimset.json`,
  `003-edorgsonly-claimset.json`) remain part of embedded `Claims.json` loading and are never staged into
  the additive workspace. Staged extensions whose `projectName` is not in the lookup are treated as
  unmapped: `-ClaimsDirectoryPath` is required and the caller-supplied fragments are the only security
  inputs for those projects. The lookup is a v1 implementation detail of the claims phase, not a separate
  catalog artifact in the repo.
- TPDM is bootstrap-mapped for Data Standard 5.2 (added by DMS-1247). The embedded DS 5.2 `Claims.json`
  already carries the full TPDM claims hierarchy and its `EdFiSandbox` grants, so the claims phase recognizes
  TPDM without staging a fragment (Embedded mode) and does not require `-ClaimsDirectoryPath`. It records leaf
  readiness checks so the claims-ready gate confirms CMS composed TPDM, and records TPDM's descriptor seed
  namespace (`uri://tpdm.ed-fi.org`) so the `SeedLoader` credential can load TPDM descriptor seed data. Any
  other direct filesystem extension outside the built-in map is treated as unmapped: it requires
  caller-supplied `-ClaimsDirectoryPath` fragments and is never silently replaced by a built-in mapping.

## Tasks

0. Add `eng/docker-compose/.bootstrap/` to `.gitignore` before schema or claims staging can write generated
   artifacts, so staged schema, claims, and seed files cannot be accidentally committed.
1. Add or refine the direct filesystem schema/security parameter surface for `-ApiSchemaPath` and
   `-ClaimsDirectoryPath` across `prepare-dms-schema.ps1` (schema inputs) and `prepare-dms-claims.ps1`
   (security inputs) per the command boundary contracts in `command-boundaries.md` Section 3.1-3.2.
   `prepare-dms-schema.ps1` should fail fast when `-Extensions` is supplied or when `-ApiSchemaPath` is
   omitted, with a message that package-backed standard mode is deferred to Story 06.
2. Implement phase-owned extension artifact resolution for schema/security ownership: `prepare-dms-schema.ps1`
   uses schema identity fields from the direct filesystem input, and `prepare-dms-claims.ps1` uses
   security-fragment fields. `load-dms-seed-data.ps1` owns the separate seed catalog lookup when seed delivery
   runs. `EdFiSandbox` coverage is required for every bootstrap-managed extension fragment and `SeedLoader`
   coverage is required only where a built-in seed package is advertised. The v1 mapped security lookup
   covered Sample and Homograph; DMS-1247 additionally maps TPDM via the embedded DS 5.2 claims. Any other
   direct filesystem extension not in the lookup is treated as unmapped and requires `-ClaimsDirectoryPath`.
3. Implement direct filesystem schema-materialization logic in `prepare-dms-schema.ps1`: normalize
   `-ApiSchemaPath` inputs into the staged workspace, normalize one core schema plus zero or more extension
   schemas into the staged ApiSchema asset workspace, copy optional schema-adjacent static content into
   deterministic manifest-relative locations, detect normalized-path collisions, compute
   `EffectiveSchemaHash` from the normalized schema files using the existing DMS algorithm, write
   `bootstrap-api-schema-manifest.json` with deterministic schema/content paths needed by runtime loading, write
   the root bootstrap manifest schema section with the stable schema-selection facts and fingerprints needed by
   later bootstrap phases, and fail fast if the hash tool exits non-zero.
   Hand the staged schema workspace to the later schema-provisioning safety flow as the exact physical schema
   contract for the run; the computed hash remains metadata for logging or comparison. Do not resolve NuGet
   packages, stage DLLs as a package bridge, perform assembly-resource extraction as the target contract, or
   stage/validate claim fragments in this phase.
4. Define the bootstrap-dms compose surface and runtime env wiring (mount `.bootstrap/ApiSchema` →
   `/app/ApiSchema`, set `USE_API_SCHEMA_PATH=true`, set `API_SCHEMA_PATH=/app/ApiSchema`, clear
   `SCHEMA_PACKAGES`) **in design only** — see the expert `-ApiSchemaPath` flow in
   [`bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md) for the end-state diagram
   Story 04 implements. Story 00 does not enable that runtime DMS bootstrap startup path because
   `ContentProvider` still expects `*.ApiSchema.dll` assemblies; enabling it now would cause
   discovery/XSD content fetches to fail in bootstrap mode. To keep the deferral robust against
   `.env`-leaked values (the repo `.env` / `.env.example` / `.env.e2e` currently set
   `USE_API_SCHEMA_PATH=true` / `API_SCHEMA_PATH=/app/ApiSchema` for the eventual Story 04 end state),
   `Set-BootstrapStartupEnvironment` explicitly blanks `USE_API_SCHEMA_PATH` and `API_SCHEMA_PATH` in
   the process environment when a bootstrap manifest is present. Compose then falls back to
   `${VAR:-false}` / empty path, so the DMS container does not execute the `SCHEMA_PACKAGES` downloader and
   uses its built-in DLL-backed schema assemblies. Because DMS is still on that non-staged path here, Story
   00 leaves `DMS_CONFIG_CLAIMS_SOURCE` and `DMS_CONFIG_CLAIMS_DIRECTORY` to the env file or
   `-AddExtensionSecurityMetadata` compatibility path, but clears `DMS_CONFIG_CLAIMS_MOUNT_SOURCE` so CMS
   cannot mount staged `.bootstrap/claims` for a different staged schema set. Story 04 owns flipping DMS and
   CMS over to the staged workspaces together (re-introducing `bootstrap-dms.yml` and the corresponding
   env-var wiring in `Set-BootstrapStartupEnvironment`). Activating `.bootstrap/claims` without also
   activating `.bootstrap/ApiSchema` is explicitly prohibited in Story 00 because it can create a
   schema/security metadata mismatch.
5. Implement additive security-fragment staging in `prepare-dms-claims.ps1`: stage fragments into one
   bootstrap workspace, with duplicate-filename detection, malformed-JSON validation, unknown effective
   claim-set-reference validation, and updates to the root bootstrap manifest claims section that record the
   Embedded versus Hybrid mode, the relative staged claims workspace CMS will read through the Config Service
   `/app/additional-claims` mount once Story 04 enables staged runtime startup, the claims fingerprint, and
   expected verification checks. `start-local-dms.ps1` validates that manifest section in Story 00 but does
   not apply it to Config Service startup until DMS also reads the matching staged ApiSchema workspace.
   Also write the root bootstrap manifest seed section from extension namespace-prefix metadata so seed
   delivery can consume prepared schema/security context without accepting schema or claims parameters.
   Bootstrap validation stays structural; CMS remains the authority for final composition outcomes.
6. Remove the redundant DMS-service `/app/additional-claims` bind mounts from `local-dms.yml` and
   `published-dms.yml`. Keep the Config Service `/app/additional-claims` mount source configurable through
   `DMS_CONFIG_CLAIMS_MOUNT_SOURCE`, but do not set that variable from the bootstrap manifest in Story 00;
   staged claims startup must move with the Story 04 DMS staged-schema runtime switch. DMS reads claimsets
   from CMS authorization metadata, not from fragment files mounted into the DMS container.
7. Ensure operator-facing validation messages report missing schema or security artifacts as artifact
   resolution/configuration failures. Seed artifact resolution messages belong to Story 02.
8. Remove bootstrap-surface dependence on `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` and
   remove standalone `-AddExtensionSecurityMetadata` from the DMS-916 normative contract. The staged claims
   workspace is the only security-selection path in this story; bootstrap manages core and selected extension
   fragments, while additional custom fragments come from `-ClaimsDirectoryPath`.
9. Treat changed claims inputs in an existing staged workspace as incompatible rerun state; reuse identical
   staged content as-is, but fail fast with teardown guidance instead of rewriting bind-mounted claims files
   or attempting in-place CMS claims replacement.
10. Keep Story 00 limited to direct filesystem schema asset-container staging and claims staging. Runtime
   reads of metadata/XSD content from that container stay in Story 04. MetaEd package production changes stay
   in Story 05. Package-backed standard mode waits for Story 06 after asset-only
   package inputs exist. Built-in seed-package advertisement and `SeedLoader` enforcement stay in Story 02.
11. Document the expert-mode boundary explicitly: `-ApiSchemaPath` still validates staged schema
   normalization, requires `-ClaimsDirectoryPath` when additional non-core security metadata is needed, and validates fragment
   structure, but bootstrap does not certify complete authorization coverage for arbitrary custom non-core
   resources ahead of runtime.
12. Add focused tests for the Story 00 staging contracts: schema workspace normalization and collision
    detection, schema and claims rerun fingerprint mismatch handling, selected-extension manifest identity,
    automatic extension-fragment filtering, known namespace-prefix handoff, Story 00 deferral of the staged
    Config Service claims mount source to Story 04, and structural claim-fragment validation failures.

## Out of Scope

- A generalized plugin system beyond selecting schema artifacts for the run.
- Per-extension version-override features beyond the design's current defaults.
- Full `Claims.json` replacement or arbitrary new top-level claim set creation through
  `-ClaimsDirectoryPath`.
- Using unrestricted runtime claims-upload endpoints as the standard bootstrap path.
- Package-backed standard-mode selection through omitted `-Extensions` or named `-Extensions`; that belongs
  to Story 06 after Story 05 publishes asset-only packages.
- Updating DMS runtime `ContentProvider` behavior; that belongs to Story 04.
- Updating MetaEd packaging to produce asset-only ApiSchema packages; that belongs to Story 05.
- Flipping DMS Docker startup to read the staged ApiSchema workspace at runtime; that belongs to Story 04.
  Story 00 stages the workspace and validates the manifest, but `bootstrap-dms.yml` and the
  `USE_API_SCHEMA_PATH`/`API_SCHEMA_PATH` env-var wiring in `Set-BootstrapStartupEnvironment` are not
  activated until Story 04 updates `ContentProvider` to read staged JSON instead of `*.ApiSchema.dll`.
- Flipping Config Service startup to read the staged `.bootstrap/claims` workspace for bootstrap mode; that
  must happen with the Story 04 DMS staged-schema runtime switch so schema and authorization metadata remain
  aligned.
- Treating the prepared Story 00 schema/security workspace as the Docker runtime source of truth. Story 00
  records and validates the prepared contract; Story 04 makes that contract runtime-authoritative.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 3, 4, 8, 9.3, and 11
- [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Section 3.1 (`prepare-dms-schema.ps1`) and Section 3.2 (`prepare-dms-claims.ps1`)

## Feedback Review Log

### 2026-05-15 — External PR review (DMS-1150 / branch)

Reviewer reported four findings; all four resolved as actionable:

- **FB1 (High, VALID):** Removing `-AddExtensionSecurityMetadata` from E2E wrappers (`tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1`, `tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1`) and `build-dms.ps1` regresses extension claims loading for any startup that does not first run the bootstrap commands. `.env.e2e` still loads Sample/Homograph schema packages, but `DMS_CONFIG_CLAIMS_SOURCE` falls back to `Embedded`. Restore the non-bootstrap hybrid claims path until E2E moves onto the staged bootstrap path in Story 04.
- **FB2 (Medium, VALID — escalated from PARTIAL):** `Set-BootstrapStartupEnvironment` (`bootstrap-manifest.psm1`) flips DMS into `USE_API_SCHEMA_PATH=true` and `bootstrap-dms.yml` mounts `.bootstrap/ApiSchema`, but the existing `ContentProvider` only loads `*.ApiSchema.dll`. Story 00 should stop short of changing runtime DMS loading; remove or gate the bootstrap-mode startup wiring and re-enable it in Story 04 when ContentProvider reads the staged workspace. Update Task #4 wording to match.
- **FB3 (Medium, VALID):** `prepare-dms-schema.ps1` rebuilds a missing `.bootstrap/ApiSchema` workspace even when the manifest still contains stale `claims`/`seed` sections. Fail fast on this partial state, mirroring the workspace-mismatch path.
- **FB4 (Low/Medium, VALID):** `Assert-FragmentValidAndExtractChecks` silently accepts `resourceClaims` entries that are not objects. Add an `is [IDictionary]` guard and a Pester test.

No findings dismissed.

### 2026-05-15 — External PR review round 2 (DMS-1150 / branch)

Three follow-up findings; all three accepted:

- **FB5 (Medium, VALID):** `Assert-FragmentValidAndExtractChecks` only requires `resourceClaims[].name` inside the explicit `claimSets` loop. Parent claims and implicit non-parent claims bypass the check, so a fragment can stage with a nameless entry and later cause CMS to deref `resourceClaim.Name!`. Hoist the check to the top of the loop and add Pester cases.
- **FB6 (Low/Medium, VALID):** The non-bootstrap `-AddExtensionSecurityMetadata` Hybrid branch sets `DMS_CONFIG_CLAIMS_SOURCE`/`DMS_CONFIG_CLAIMS_DIRECTORY` but leaves `DMS_CONFIG_CLAIMS_MOUNT_SOURCE` untouched. `local-config.yml` honors that env var, so a stale ambient value mounts the wrong directory. Clear it to empty in both `start-local-dms.ps1` and `start-published-dms.ps1`, with a Pester guard.
- **FB7 (Low, VALID):** `eng/docker-compose/README.md:207-213` still describes the deleted `bootstrap-dms.yml` and the `.bootstrap/ApiSchema` runtime mount. Rewrite that paragraph so only `.bootstrap/claims` is described as active in Story 00; DMS schema runtime stays on DLL-backed packages until Story 04.

No round-2 findings dismissed.

### 2026-05-15 — External PR review round 3 (DMS-1150 / branch)

Single high-severity finding; accepted with the "clear `.bootstrap/` before start" remediation (not the alternative `-NoBootstrap` opt-out switch):

- **FB8 (High, VALID):** `start-local-dms.ps1` checks the bootstrap manifest first and only applies `-AddExtensionSecurityMetadata` in the `elseif`. A stale `.bootstrap/` workspace (core-only, partial, or with a different extension selection) overrides the DLL-backed E2E/build path: CMS reads `.bootstrap/claims` while runtime loads DLL schemas, producing mismatched authorization or a fail-fast on a missing claims/seed section. `build-dms.ps1`'s teardown doesn't pass `-RemoveBootstrap`, and the E2E setup wrappers assume the caller ran teardown first. Fix: add `-RemoveBootstrap` to `build-dms.ps1`'s teardown calls, and add a defensive `.bootstrap/` removal step at the top of both E2E setup wrappers. Add Pester coverage that asserts the cleanup contracts.

No round-3 findings dismissed.

### 2026-05-20 — External PR review round 4 (DMS-1150 / branch)

Five findings; one high accepted as a Story 04 boundary, four mediums accepted:

- **FB9 (High, ACCEPTED AS STORY 04 BOUNDARY):** Reviewer flagged `Set-BootstrapStartupEnvironment` blanking `USE_API_SCHEMA_PATH`/`API_SCHEMA_PATH` (bootstrap-manifest.psm1:447) and `DMS_CONFIG_CLAIMS_MOUNT_SOURCE` (:510) as leaving startup on DLL-backed schemas and non-staged claims after both prepare scripts run. That observation is correct for Story 00: staged schema/security is the prepared bootstrap contract, not the Docker runtime source of truth. Runtime activation is deferred because `ContentProvider` still loads `*.ApiSchema.dll`, and activating staged CMS claims without staged DMS schema can create mismatched authorization metadata. Story 04 owns flipping DMS and CMS over to the staged workspaces together. The blanking remains as the defensive guard against `.env`-leaked Story-04 values.
- **FB10 (Medium, VALID):** `prepare-dms-claims.ps1` recreated `.bootstrap/claims` when the workspace was missing without checking for stale manifest `claims`/`seed` sections, then overwrote those sections, bypassing the teardown-guidance fail-fast required by Task #9. Mirror the schema-phase guard (FB3) so partial prior state fails fast with the workspace-mismatch message.
- **FB11 (Medium, VALID):** Claimset discovery in `prepare-dms-claims.ps1:Get-UserFragmentFile` read only the top level of `-ClaimsDirectoryPath`. CMS `ClaimsFragmentComposer.DiscoverFragmentFiles` uses `SearchOption.AllDirectories`, so nested valid fragments were silently dropped and nested filename collisions were missed. Add `-Recurse` so bootstrap input discovery matches CMS runtime discovery; the existing filename-collision guard then catches nested duplicates after flattening.
- **FB12 (Medium, VALID):** `Test-TruthyJsonValue` coerced non-bool `isParent` via `[System.Convert]::ToBoolean`, and `resourceClaims`/`claimSets`/`actions` accepted singleton non-arrays through `@(...)` coercion. CMS deserializes `IsParent` as a strict `bool` and the three array fields as `List<T>` with no lenient `JsonSerializerOptions`, so malformed fragments passed bootstrap and failed later at CMS startup. Tighten each to strict shape. Extend the same strict-array guard to `authorizationStrategyOverridesForCRUD` for consistency. (Fixing the IList check also required `Get-ValueOrNull` to wrap returns with the unary comma so PowerShell's function-output unwrapping does not flatten single-element arrays to scalars.)
- **FB13 (Medium, VALID):** `Copy-Item`/`Move-Item` at `prepare-dms-schema.ps1:431,:497` and `prepare-dms-claims.ps1:452,:502` are non-terminating cmdlets, and neither script sets `$ErrorActionPreference = 'Stop'`. A failed staging copy/move could still allow the manifest write that follows, producing a manifest pointing at an incomplete workspace. Add `-ErrorAction Stop` to the four staging calls.

No round-4 findings dismissed. FB9 is accepted as an intentional Story 04 runtime-activation boundary.
