---
jira: DMS-1150
jira_url: https://edfi.atlassian.net/browse/DMS-1150
---

# Story: Bootstrap Schema and Security Selection

## Description

Implement the first schema and security input slice for developer bootstrap over the stable direct filesystem
contract. This slice covers:

- direct filesystem schema bootstrap through `-ApiSchemaPath` for core-only, core plus recognized mapped
  extensions, and custom schema/security/seed inputs,
- additive security staging through `-ClaimsDirectoryPath`,
- the shared v1 mapping used to recognize supported extension schemas and stage their base security
  fragments when those schemas are already present in the direct filesystem input.

This story intentionally does not implement package-backed standard selection through omitted `-Extensions`
or named `-Extensions`. Those modes remain in the main bootstrap design as the target day-to-day developer
flow, but they require asset-only ApiSchema packages and are implemented in Story 06
after the MetaEd packaging work in Story 05.

It also covers the matching CMS claims-loading behavior, including additive staging of extension-derived and
developer-supplied `*-claimset.json` fragments into one workspace directory. The selected schema set must be
materialized once into the normalized file-based ApiSchema asset container at
`eng/docker-compose/.bootstrap/ApiSchema/`. Direct filesystem ApiSchema loading through `-ApiSchemaPath` is
the stable core contract. Later package-backed selection is an input-materialization path that must produce
the same workspace. The schema JSON files in that workspace must drive all three schema consumers for the
run:

- `dms-schema hash`,
- Docker-hosted DMS startup,
- IDE-hosted DMS startup.

The same workspace also carries optional schema-adjacent static content and
`bootstrap-api-schema-manifest.json` as defined in [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md).
That manifest indexes normalized schema and content paths, but it is not a second schema authority.

Bootstrap must not invent a second schema fingerprint or a second schema-resolution path.
In this story, the selected schema set drives the DDL target, the hash-validation path, and the exact
physical schema footprint for the run. A direct filesystem input containing only the core schema yields only
core tables. Core plus a recognized mapped extension such as Sample yields the tables required by that
combined schema set. A database provisioned for a different extension selection is incompatible existing
state for this story's bootstrap run.
This story also makes the default-profile migration explicit: the current `eng/docker-compose` baseline
includes TPDM in `SCHEMA_PACKAGES`, but TPDM is not available as of Ed-Fi 6.2 and is therefore outside
the recognized mapped extension surface for this story.

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
is the single source of truth for the staged ApiSchema files and for the matching base staged claimset
fragments for core and recognized mapped extension schemas. If `-ApiSchemaPath` stages unmapped non-core
schemas, `-ClaimsDirectoryPath` is required for caller-supplied fragments. No later phase re-derives or
replaces that security selection. Story 06 package-backed `-Extensions` mode must feed the same root bootstrap
manifest schema contract and claims-staging contract rather than introducing a second path.

## Acceptance Criteria

- Story 00 `prepare-dms-schema.ps1` requires `-ApiSchemaPath`. Omitting `-ApiSchemaPath` or supplying
  `-Extensions` fails fast with a message that package-backed standard mode is deferred until Story 06.
- Direct filesystem `-ApiSchemaPath` loading is the only schema input contract delivered by this story.
  It normalizes developer-supplied `ApiSchema*.json` files and any optional schema-adjacent static content
  through the staged workspace, requires the staged result to be exactly one core schema plus zero or more
  extensions, and drives automatic base security selection for core and recognized mapped extension schemas.
- The staged ApiSchema workspace contains `bootstrap-api-schema-manifest.json` with deterministic
  manifest-relative paths for each selected project's normalized schema file and optional static content. This
  ApiSchema manifest is a runtime asset index only.
- `prepare-dms-schema.ps1` writes the schema section of
  `eng/docker-compose/.bootstrap/bootstrap-manifest.json` with schema selection mode (`ApiSchemaPath`),
  selected mapped extension names, expected `EffectiveSchemaHash`, an ApiSchema workspace fingerprint, and the
  relative ApiSchema manifest path. Extension namespace prefixes come from the same v1 extension catalog used
  by the phase-owned lookups and are recorded later in the seed section by claims staging.
- Bootstrap detects normalized-path collisions before finalizing the staged ApiSchema workspace.
- Package-backed selection, asset-only NuGet package extraction, package rejection for DLL-only packages,
  no-argument core-only convenience, and named `-Extensions` convenience are not Story 00 acceptance
  criteria; they are Story 06 acceptance criteria.
- `-ClaimsDirectoryPath` is required with `-ApiSchemaPath` when the staged set contains unmapped non-core
  schemas, and additive otherwise.
- `-ClaimsDirectoryPath` is additive-only: fragments may attach permissions only to effective claim set
  references already declared in the embedded `Claims.json`. Bootstrap fails fast when a staged fragment
  references an unknown effective claim set name.
- `-ApiSchemaPath` mode satisfies the DMS-916 requirement that claimset loading is automatic from the
  selected schema for core and recognized mapped extension schemas. It validates staged schema normalization
  and caller-supplied fragment structure, but it does not infer or guarantee authorization coverage for
  arbitrary custom non-core resources. Runtime authorization failures for incomplete expert-supplied
  fragments remain possible.
- Same-checkout reruns reuse the existing staged claims workspace only when the intended fragment set is
  identical. If the intended security inputs differ, bootstrap fails fast with teardown guidance rather than
  rewriting a directory that may still be bind-mounted into CMS or attempting in-place replacement of
  populated CMS claims data.
- Claim-fragment validation in bootstrap is structural only: staged fragments must be
  parseable, must target effective claim set references that already exist in embedded `Claims.json`, and
  must not collide by filename in the staged workspace. The precise reference rule matches
  `command-boundaries.md`: every explicit `resourceClaims[].claimSets[].name` is validated, and the
  fragment top-level `name` is validated only when CMS composition uses it as the implicit claim set name
  for a non-parent resource claim. A top-level fragment/group label for explicit parent-claim attachments is
  not itself rejected merely because it is absent from embedded `Claims.json`. Bootstrap does not check
  attachment overlap or perform semantic composition reasoning; CMS startup is the authoritative composition
  gate for those outcomes.
- Extension-derived and developer-supplied claimset fragments are staged into one workspace directory with
  fail-fast validation for:
  - missing directory,
  - no `*-claimset.json` files,
  - malformed JSON,
  - filename collisions,
  - unknown effective claim set references in staged fragments.
- Every bootstrap-managed extension fragment in the supported v1 mapping preserves ordinary developer access
  by attaching `EdFiSandbox` permissions for the extension resources it contributes. Extension entries that
  eventually advertise built-in seed support must additionally attach the required `SeedLoader` permissions
  for those extension seed resources.
- CMS consumes that staged workspace through the Config Service `/app/additional-claims` mount. The claims
  section of `eng/docker-compose/.bootstrap/bootstrap-manifest.json` is the source of truth for the CMS claims
  mode, relative claims directory, claims fingerprint, and expected claims-verification checks.
  `start-local-dms.ps1` translates that manifest section into the `local-config.yml` environment and
  bind-mount inputs used for the run.
- `prepare-dms-claims.ps1` updates `eng/docker-compose/.bootstrap/bootstrap-manifest.json` after claims
  staging succeeds. The claims section records only the effective claims startup inputs and fingerprints; the
  seed section records only extension namespace prefixes. The root manifest must not contain built-in
  seed-package entries, resource definitions, claim grants, instance IDs, credentials, URLs, Docker state,
  environment settings, seed file paths, phase progress, or resume checkpoints.
- Story 00 does not own built-in seed-support advertisement. It stages and validates claims inputs; Story 02
  decides when built-in seed support is available and enforces the `SeedLoader` requirements for that path.
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
- `TPDM` is not part of the recognized mapped extension surface for Story 00. TPDM is not available as of
  Ed-Fi 6.2 and is not silently substituted by `sample`, `homograph`, or any default.
- Only extensions backed by current schema and security artifacts appear in the DMS-916 v1 mapping and
  recognized mapped extension surface. Deferred extensions are not advertised as recognized mapped schemas.

## Tasks

0. Add `eng/docker-compose/.bootstrap/` to `.gitignore` before schema or claims staging can write generated
   artifacts, so staged schema, claims, and seed files cannot be accidentally committed.
1. Add or refine the direct filesystem schema/security parameter surface for `-ApiSchemaPath` and
   `-ClaimsDirectoryPath` across `prepare-dms-schema.ps1` (schema inputs) and `prepare-dms-claims.ps1`
   (security inputs) per the command boundary contracts in `command-boundaries.md` Section 3.1-3.2.
   `prepare-dms-schema.ps1` should fail fast when `-Extensions` is supplied or when `-ApiSchemaPath` is
   omitted, with a message that package-backed standard mode is deferred to Story 06.
2. Implement one v1 extension artifact catalog for schema/security ownership: `prepare-dms-schema.ps1` uses
   schema identity fields to recognize mapped schemas from the direct filesystem input, and
   `prepare-dms-claims.ps1` uses security-fragment fields. `load-dms-seed-data.ps1` owns the separate seed
   catalog lookup when seed delivery runs. The same recognized mapped extension set drives those phase-owned
   lookups, with `EdFiSandbox` coverage required for every supported extension and `SeedLoader` coverage
   required only where built-in seed support is advertised. Ensure `TPDM` is absent from this recognized
   mapping.
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
4. Update compose/startup behavior so bootstrap mode mounts `.bootstrap/ApiSchema` to `/app/ApiSchema`,
   sets `USE_API_SCHEMA_PATH=true`, sets `API_SCHEMA_PATH=/app/ApiSchema`, and clears `SCHEMA_PACKAGES`.
5. Implement additive security-fragment staging in `prepare-dms-claims.ps1`: stage fragments into one
   bootstrap workspace, with duplicate-filename detection, malformed-JSON validation, unknown effective
   claim-set-reference validation, and updates to the root bootstrap manifest claims section that record the
   Embedded versus Hybrid mode, the relative staged claims workspace CMS must read through the Config Service
   `/app/additional-claims` mount, the claims fingerprint, and expected verification checks.
   `start-local-dms.ps1` consumes that manifest section and applies it through `local-config.yml` startup
   inputs. Also write the root bootstrap manifest seed section from extension namespace-prefix metadata so
   seed delivery can consume prepared schema/security context without accepting schema or claims parameters.
   Bootstrap validation stays structural; CMS remains the authority for final composition outcomes.
6. Remove the redundant DMS-service `/app/additional-claims` bind mounts from `local-dms.yml` and
   `published-dms.yml`. Keep the Config Service mounts in `local-config.yml` and `published-config.yml`;
   DMS reads claimsets from CMS authorization metadata, not from fragment files mounted into the DMS
   container.
7. Restrict the v1 recognized mapped extension surface and operator-facing validation messages to
   extensions backed by current schema and security artifacts; keep deferred extensions out of the advertised
   support surface.
8. Remove bootstrap-surface dependence on `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` and
   remove standalone `-AddExtensionSecurityMetadata` from the DMS-916 normative contract. The staged claims
   workspace is the only security-selection path in this story; bootstrap manages core and mapped extension
   fragments, while unmapped custom fragments come from `-ClaimsDirectoryPath`.
9. Treat changed claims inputs in an existing staged workspace as incompatible rerun state; reuse identical
   staged content as-is, but fail fast with teardown guidance instead of rewriting bind-mounted claims files
   or attempting in-place CMS claims replacement.
10. Keep Story 00 limited to direct filesystem schema asset-container staging and claims staging. Runtime
   reads of metadata/XSD content from that container stay in Story 04. MetaEd package production changes stay
   in Story 05. Package-backed standard mode waits for Story 06 after asset-only
   package inputs exist. Built-in seed-support advertisement and `SeedLoader` enforcement stay in Story 02.
11. Document the expert-mode boundary explicitly: `-ApiSchemaPath` still validates staged schema
   normalization, requires `-ClaimsDirectoryPath` for unmapped non-core schemas, and validates fragment
   structure, but bootstrap does not certify complete authorization coverage for arbitrary custom non-core
   resources ahead of runtime.

## Out of Scope

- A generalized plugin system beyond the documented extension-selection surface.
- Per-extension version-override features beyond the design's current defaults.
- Full `Claims.json` replacement or arbitrary new top-level claim set creation through
  `-ClaimsDirectoryPath`.
- Using unrestricted runtime claims-upload endpoints as the standard bootstrap path.
- Package-backed standard-mode selection through omitted `-Extensions` or named `-Extensions`; that belongs
  to Story 06 after Story 05 publishes asset-only packages.
- Updating DMS runtime `ContentProvider` behavior; that belongs to Story 04.
- Updating MetaEd packaging to produce asset-only ApiSchema packages; that belongs to Story 05.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 3, 4, 8, 9.3, and 11
- [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Section 3.1 (`prepare-dms-schema.ps1`) and Section 3.2 (`prepare-dms-claims.ps1`)
