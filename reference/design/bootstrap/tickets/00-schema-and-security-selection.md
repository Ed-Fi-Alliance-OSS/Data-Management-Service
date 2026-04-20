---
design: DMS-916
---

# Story: Bootstrap Schema and Security Selection

## Description

Implement the story-aligned schema and security input surface for developer bootstrap. This slice covers the
three schema-selection modes described in the main bootstrap design:

- core-only bootstrap when `-Extensions` is omitted,
- named extension bootstrap through `-Extensions`,
- expert local-schema bootstrap through `-ApiSchemaPath`.

It also covers the matching CMS claims-loading behavior, including additive staging of extension-derived and
developer-supplied `*-claimset.json` fragments into one workspace directory. The selected schema set must be
materialized once into `eng/docker-compose/.bootstrap/ApiSchema/` and those exact staged files must drive
all three consumers for the run:

- `dms-schema hash`,
- Docker-hosted DMS startup,
- IDE-hosted DMS startup.

Bootstrap must not invent a second schema fingerprint or a second schema-resolution path.
In this story, the selected schema set drives the DDL target, the hash-validation path, and the exact
physical schema footprint for the run. Core-only selection yields only core tables. Core plus a supported
extension such as Sample yields the tables required by that combined schema set. A database provisioned for a
different extension selection is incompatible existing state for this story's bootstrap run.

## Acceptance Criteria

- Omitting `-Extensions` produces the core-only bootstrap path: exactly one staged core schema file, no
  extension fragment staging, and no extension seed-package lookup.
- `-Extensions` accepts only recognized extension identifiers and resolves the required bootstrap artifacts
  from one mapping:
  - ApiSchema package selection,
  - security fragment selection,
  - extension seed-package selection when `-LoadSeedData` is used and the selected extension has built-in
    bootstrap seed support.
- `-Extensions` is a `String[]` parameter that uses normal PowerShell array binding for multiple values
  rather than comma-splitting a single string.
- In standard `-Extensions` mode, bootstrap resolves schema packages host-side, stages the resulting files in
  `eng/docker-compose/.bootstrap/ApiSchema/`, computes `EffectiveSchemaHash` from those staged files, mounts
  the same directory into Docker-hosted DMS, and points IDE-hosted DMS at the same host path.
- `-ApiSchemaPath` and `-Extensions` are mutually exclusive.
- `-ApiSchemaPath` normalizes developer-supplied `ApiSchema*.json` files through the same staged workspace,
  requires the staged result to be exactly one core schema plus zero or more extensions, and disables
  automatic extension-derived claimset and seed selection. When non-core security or seed inputs are needed
  in this mode, the bootstrap path uses explicit companion inputs rather than implicit extension defaults.
- In `-ApiSchemaPath` mode, bootstrap fails fast when the staged schema set includes one or more extension
  schemas but no explicit `-ClaimsDirectoryPath` is provided. Core-only custom-schema runs may rely on
  embedded claims only.
- `-ClaimsDirectoryPath` works in both supported scenarios:
  - as the primary security input for `-ApiSchemaPath`,
  - additively with `-Extensions` in standard mode.
- `-ClaimsDirectoryPath` is additive-only: fragments may attach permissions only to claim set names already
  declared in the embedded `Claims.json`. Bootstrap fails fast when a staged fragment references an unknown
  claim set name.
- Expert `-ApiSchemaPath` mode validates explicit non-core security input presence and staged-fragment
  structure, but it does not pre-certify full authorization completeness for every staged non-core resource.
  Runtime authorization failures for incomplete expert-supplied fragments remain possible and are not masked
  as built-in extension defaults.
- Same-checkout reruns reuse the existing staged claims workspace only when the intended fragment set is
  identical. If the intended security inputs differ, bootstrap fails fast with teardown guidance rather than
  rewriting a directory that may still be bind-mounted into CMS or attempting in-place replacement of
  populated CMS claims data.
- Claim-fragment validation in bootstrap is bounded rather than fully semantic: staged fragments must be
  parseable, must target claim set names that already exist in embedded `Claims.json`, must not collide by
  filename in the staged workspace, and must not duplicate the same effective `(normalized resource claim,
  effective claim set name)` attachment under the current CMS composition behavior.
- Bootstrap does not attempt to certify the fully composed authorization graph beyond that bounded preflight.
- CMS startup remains the authoritative composition gate for broader semantic composition outcomes.
- Extension-derived and developer-supplied claimset fragments are staged into one workspace directory with
  fail-fast validation for:
  - missing directory,
  - no `*-claimset.json` files,
  - malformed JSON,
  - filename collisions,
  - unknown claim set names referenced by staged fragments,
  - duplicate effective `(normalized resource claim, effective claim set name)` attachments.
- Every bootstrap-managed extension fragment in the supported v1 mapping preserves ordinary developer access
  by attaching `EdFiSandbox` permissions for the extension resources it contributes. Extension entries that
  eventually advertise built-in seed support must additionally attach the required `SeedLoader` permissions
  for those extension seed resources.
- CMS consumes that staged workspace through the standard `/app/additional-claims` mount, with the compose
  source path redirected from the static repo folder to `eng/docker-compose/.bootstrap/claims` by the
  `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` bind-mount variable in `local-config.yml`.
- Until Story 02 adds the top-level `SeedLoader` claim set to the embedded `Claims.json`, no extension
  mapping entry may advertise built-in seed support or rely on `SeedLoader`-bearing extension fragments as
  part of the standard bootstrap surface.
- Bootstrap also fails fast when a built-in extension seed source is selected but the staged extension
  security fragments do not attach the required `SeedLoader` permissions for that extension's seed
  resources.
- When `-AddExtensionSecurityMetadata` is used without `-Extensions` or `-ClaimsDirectoryPath`, bootstrap
  preserves the legacy behavior of loading the static repo-bundled additional claimset set. The narrowed
  DMS-916 cleanup removes standard-bootstrap dependence on
  `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` only for the new schema/security-selection
  paths; it does not silently redefine the standalone legacy flag contract in this story.
- When `-AddExtensionSecurityMetadata` is used together with `-Extensions` or `-ClaimsDirectoryPath`,
  bootstrap treats the legacy flag as redundant, ignores it with a warning, and continues with the staged
  claims-workspace path selected by the new inputs.
- Bootstrap computes the expected `EffectiveSchemaHash` for the selected schema set using the existing DMS
  hashing algorithm over the staged schema files.
- The resolved schema set drives the DDL target/version/`EffectiveSchemaHash` validation path and the exact
  physical table set for the run. A database provisioned for a different extension selection is not treated
  as an aligned superset.
- CMS claims mode is driven from the staged inputs:
  - Embedded mode for core-only bootstrap,
  - Hybrid mode when one or more staged fragments exist.
- Only extensions backed by current schema and security artifacts appear in the DMS-916 v1 mapping and
  valid-extension surface. Deferred extensions are not advertised as accepted `-Extensions` values.

## Tasks

0. **Prerequisite:** The `.gitignore` update for `eng/docker-compose/.bootstrap/` (Story 03 task 9) must be
   delivered first or concurrently with this story to prevent accidental commits of staged artifacts. If
   Story 03 delivery is not concurrent, this story must carry that one-line `.gitignore` entry itself.
1. Add or refine the schema/security parameter surface for `-Extensions`, `-ApiSchemaPath`, and
   `-ClaimsDirectoryPath`.
2. Implement one extension artifact mapping that drives schema-package resolution, security-fragment
   resolution, and extension seed-package resolution from the same selected extension set, with
   `EdFiSandbox` coverage required for every supported extension and `SeedLoader` coverage required only
   where built-in seed support is advertised.
3. Implement schema-resolution logic that stages the selected schema files once, normalizes `-ApiSchemaPath`
   to one core plus zero or more extensions, computes `EffectiveSchemaHash` from that staged set using the
   existing DMS algorithm, and fails fast if the hash tool exits non-zero. It hands the resulting DDL
   target/hash context to the later schema-provisioning safety flow as the exact physical schema contract for the run.
4. Implement additive security-fragment staging into one bootstrap workspace, including duplicate-filename
   detection, malformed-JSON validation, unknown-claim-set validation, the bounded duplicate-effective-
   attachment check for `(normalized resource claim, effective claim set name)` under current CMS
   composition semantics, and the environment-variable selection for Embedded versus Hybrid mode plus the
   compose/runtime wiring in `local-config.yml` that points CMS at the staged workspace through
   `DMS_CONFIG_CLAIMS_HOST_DIRECTORY`.
5. Restrict the v1 extension mapping and operator-facing validation messages to extensions backed by current
   schema and security artifacts; keep deferred extensions out of the advertised support surface.
6. Remove standard bootstrap dependence on `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` when
   `-Extensions` or `-ClaimsDirectoryPath` drives claims selection, and carry that cleanup through the local
   developer bootstrap surface while preserving the legacy behavior of standalone
   `-AddExtensionSecurityMetadata` runs that do not use the new schema/security-selection inputs.
   Mixed-mode invocations that also pass `-Extensions` or `-ClaimsDirectoryPath` must ignore the legacy flag
   with a warning rather than reintroducing the old claims-loading path.
7. Treat changed claims inputs in an existing staged workspace as incompatible rerun state; reuse identical
   staged content as-is, but fail fast with teardown guidance instead of rewriting bind-mounted claims files
   or attempting in-place CMS claims replacement.
8. Keep any built-in extension seed-support advertisement gated on Story 02's addition of the top-level
   `SeedLoader` claim set to the embedded claims metadata.
9. Document the expert-mode boundary explicitly: `-ApiSchemaPath` plus `-ClaimsDirectoryPath` validates
   staged fragment presence and structure, but bootstrap does not certify complete authorization coverage for
   arbitrary custom non-core resources ahead of runtime.

## Out of Scope

- A generalized plugin system beyond the documented extension-selection surface.
- Per-extension version-override features beyond the design's current defaults.
- Full `Claims.json` replacement or arbitrary new top-level claim set creation through
  `-ClaimsDirectoryPath`.
- Using unrestricted runtime claims-upload endpoints as the standard bootstrap path.

## Design References

- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 3, 4, 8, 9.3, and 11
