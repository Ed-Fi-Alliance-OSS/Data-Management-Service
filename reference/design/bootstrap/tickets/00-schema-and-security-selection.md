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
This story also makes the default-profile migration explicit: the current `eng/docker-compose` baseline
includes TPDM in `SCHEMA_PACKAGES`, but TPDM is not available as of Ed-Fi 6.2 and is therefore outside
this story's supported extension surface. Specifying `TPDM` via `-Extensions` produces a fast-fail with a
clear unsupported-extension message.

Within the composable bootstrap design, schema selection and staging are owned exclusively by the
`prepare-dms-schema.ps1` phase command (see `command-boundaries.md` Section 3.1). Claims and security staging are
owned exclusively by `prepare-dms-claims.ps1` (see `command-boundaries.md` Section 3.2). Both commands run without
Docker services and hand their staged outputs — the staged schema workspace and the staged claims workspace
— to the downstream infrastructure and provisioning phases.

**Mode-to-security summary (normative):** Standard `-Extensions` mode — including the omitted-`-Extensions`
core-only case — provides automatic schema-and-security selection: the chosen extension set drives both the
staged ApiSchema files and the matching staged claimset fragments via the single extension mapping defined
below. Expert `-ApiSchemaPath` mode does not auto-derive security: when the staged schema set includes any
non-core schema, the caller must supply explicit `-ClaimsDirectoryPath` input, and bootstrap fails fast if
it is missing. Core-only `-ApiSchemaPath` runs may rely on embedded claims only. This story does not promise
automatic claimset loading from schema alone in expert mode, and no later phase retro-fits expert-mode
security defaults.

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
  structure, but it does not guarantee full authorization coverage for arbitrary custom non-core resources.
  Runtime authorization failures for incomplete expert-supplied fragments remain possible.
- Same-checkout reruns reuse the existing staged claims workspace only when the intended fragment set is
  identical. If the intended security inputs differ, bootstrap fails fast with teardown guidance rather than
  rewriting a directory that may still be bind-mounted into CMS or attempting in-place replacement of
  populated CMS claims data.
- Claim-fragment validation in bootstrap is structural only: staged fragments must be
  parseable, must target claim set names that already exist in embedded `Claims.json`, and must not collide
  by filename in the staged workspace. Bootstrap does not check attachment overlap or perform semantic
  composition reasoning; CMS startup is the authoritative composition gate for those outcomes.
- Extension-derived and developer-supplied claimset fragments are staged into one workspace directory with
  fail-fast validation for:
  - missing directory,
  - no `*-claimset.json` files,
  - malformed JSON,
  - filename collisions,
  - unknown claim set names referenced by staged fragments.
- Every bootstrap-managed extension fragment in the supported v1 mapping preserves ordinary developer access
  by attaching `EdFiSandbox` permissions for the extension resources it contributes. Extension entries that
  eventually advertise built-in seed support must additionally attach the required `SeedLoader` permissions
  for those extension seed resources.
- CMS consumes that staged workspace through the standard `/app/additional-claims` mount, with the compose
  source path redirected from the static repo folder to `eng/docker-compose/.bootstrap/claims` by the
  `DMS_CONFIG_CLAIMS_HOST_DIRECTORY` bind-mount variable in `local-config.yml`.
- Story 00 does not own built-in seed-support advertisement. It stages and validates claims inputs; Story 02
  decides when built-in seed support is available and enforces the `SeedLoader` requirements for that path.
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
- `TPDM` is not a supported value for `-Extensions`. TPDM is not available as of Ed-Fi 6.2; specifying it
  produces a fast-fail with a clear unsupported-extension message rather than silent fallback.
- Only extensions backed by current schema and security artifacts appear in the DMS-916 v1 mapping and
  valid-extension surface. Deferred extensions are not advertised as accepted `-Extensions` values.

## Tasks

0. **Prerequisite:** The `.gitignore` update for `eng/docker-compose/.bootstrap/` (Story 03 task 9) must be
   delivered first or concurrently with this story to prevent accidental commits of staged artifacts. If
   Story 03 delivery is not concurrent, this story must carry that one-line `.gitignore` entry itself.
1. Add or refine the schema/security parameter surface for `-Extensions`, `-ApiSchemaPath`, and
   `-ClaimsDirectoryPath` across `prepare-dms-schema.ps1` (schema inputs) and `prepare-dms-claims.ps1`
   (security inputs) per the command boundary contracts in `command-boundaries.md` Section 3.1-3.2.
2. Implement one extension artifact mapping (shared by both phase commands) that drives schema-package
   resolution, security-fragment resolution, and extension seed-package resolution from the same selected
   extension set, with `EdFiSandbox` coverage required for every supported extension and `SeedLoader`
   coverage required only where built-in seed support is advertised. Ensure `TPDM` is absent from this
   mapping and produces a fast-fail unsupported-extension error when specified via `-Extensions`.
3. Implement schema-resolution logic in `prepare-dms-schema.ps1`: stage the selected schema files once,
   normalize `-ApiSchemaPath` to one core plus zero or more extensions, compute `EffectiveSchemaHash` from
   that staged set using the existing DMS algorithm, and fail fast if the hash tool exits non-zero. Hand
   the resulting DDL target/hash context to the later schema-provisioning safety flow as the exact physical
   schema contract for the run.
4. Implement additive security-fragment staging in `prepare-dms-claims.ps1`: stage fragments into one
   bootstrap workspace, with duplicate-filename detection, malformed-JSON validation, unknown-claim-set
   validation, and the environment-variable selection for Embedded versus Hybrid mode plus the
   compose/runtime wiring in `local-config.yml` that points CMS at the staged workspace through
   `DMS_CONFIG_CLAIMS_HOST_DIRECTORY`. Bootstrap validation stays structural; CMS remains the authority for
   final composition outcomes.
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
8. Keep Story 00 limited to schema and claims staging. Built-in seed-support advertisement and `SeedLoader`
   enforcement stay in Story 02.
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
- [`../command-boundaries.md`](../command-boundaries.md), Section 3.1 (`prepare-dms-schema.ps1`) and Section 3.2 (`prepare-dms-claims.ps1`)

