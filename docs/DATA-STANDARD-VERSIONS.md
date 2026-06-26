# Data Standard Versions

The Data Management Service is built to support more than one Ed-Fi Data Standard
version at a time (up to three concurrently). **Data Standard 5.2 is the default
everywhere** — every command, container, and CI lane behaves exactly as it always has
when no version is selected. Selecting a different version is an explicit, opt-in act.

This document explains where each Data Standard version "lives" in the repository,
how to run a non-default version locally, and the maintainer procedure for adding or
dropping a version.

## What a "supported version" is made of

A Data Standard version is not a single switch. Each version is the sum of four
artifacts, and the design goal is that **each artifact has one obvious place to change**:

| Concern | Single obvious place | Selected by |
|---------|----------------------|-------------|
| **Schema** (ApiSchema + extensions) | `SCHEMA_PACKAGES` in the `.env.ds<NN>` overlay, with matching `PackageVersion` entries in `src/Directory.Packages.props` | `USE_API_SCHEMA_PATH=true` + `SCHEMA_PACKAGES` (downloaded at container start by `src/dms/run.sh`) |
| **Security metadata** (CMS claims) | `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/Standards/ds<NN>/Claims.json` | `ClaimsOptions:DataStandardVersion` (env `DMS_CONFIG_DATA_STANDARD_VERSION`) |
| **Local dev** | the `.env.ds<NN>` overlay | the `-DataStandardVersion` script parameter |
| **CI** (template/package builds) | the per-workflow `standard_version` matrix | the matrix `include` entry |

The version token `ds<NN>` is the version with the dot removed: `5.2` → `ds52`,
`6.1` → `ds61`.

## Running a specific version locally

The local start/build scripts accept an optional `-DataStandardVersion` parameter:

```powershell
# Default: Data Standard 5.2 (no parameter — base env file used unchanged)
./build-dms.ps1 run

# Explicitly select a Data Standard version
cd eng/docker-compose
./start-local-dms.ps1 -EnvironmentFile "./.env.e2e" -DataStandardVersion 6.1
```

`-DataStandardVersion` is accepted by `start-local-dms.ps1`,
`start-published-dms.ps1`, the repo-root `build-dms.ps1` (which composes the overlay once
and applies the derived env file across its provisioning, seed, configure, and startup
steps), and both E2E `setup-local-dms.ps1` wrappers
(`src/dms/tests/EdFi.DataManagementService.Tests.E2E/`,
`src/dms/tests/EdFi.InstanceManagement.Tests.E2E/`).

### How it composes

When `-DataStandardVersion` is supplied, the script composes the matching
`eng/docker-compose/.env.ds<NN>` **overlay** onto the base `-EnvironmentFile`,
writing a derived file to `eng/docker-compose/.derived/<base>.<token>` (gitignored)
and handing that single file to `docker compose --env-file`. The overlay carries only
the data-standard-specific variables — `SCHEMA_PACKAGES`, `DATABASE_TEMPLATE_PACKAGE`,
and `DMS_CONFIG_DATA_STANDARD_VERSION` — so it can be applied on top of any base
`.env.*` file. Everything else comes from the base file.

Because the overlay sets both `SCHEMA_PACKAGES` (the DMS schema surface) and
`DMS_CONFIG_DATA_STANDARD_VERSION` (the CMS claims selector), selecting a version
keeps schema and security metadata in lockstep.

- **No parameter ⇒ Data Standard 5.2.** The base env file is used unchanged, so every
  existing flow is byte-for-byte identical.
- **Fail fast.** An unknown version or a missing `.env.ds<NN>` overlay throws
  immediately (the error lists the overlays that do exist).

> **Note on the DLL-backed fallback.** When `USE_API_SCHEMA_PATH=false`, DMS loads a
> schema baked into the image at build time, which is Data Standard 5.2 only. Any
> non-default version must therefore run with `USE_API_SCHEMA_PATH=true` (the download
> path), which the `.env.ds<NN>` overlays assume.

The mechanics live in `eng/docker-compose/env-utility.psm1`
(`Resolve-DataStandardEnvironmentFile`, `New-DataStandardDerivedEnvFile`,
`Get-DataStandardOverlayToken`) and are covered by
`eng/docker-compose/tests/DataStandardEnvironmentFile.Tests.ps1`.

## Security metadata selection (CMS)

The Configuration Service embeds each version's base `Claims.json` as an assembly
resource under `Claims/Standards/ds<NN>/`. The csproj globs
`Claims/Standards/**/Claims.json`, so **adding a version's claims is dropping a folder**
— no csproj edit. At startup `ClaimsProvider` resolves the embedded resource from
`ClaimsOptions:DataStandardVersion` (default `5.2`), and fails fast with a clear message
if the requested version's resource is absent. This covers the **Embedded** and
**Hybrid** claims-loading modes (both load the embedded base); see
[Claims Loading Deployment Guide](./CLAIMS-LOADING-GUIDE.md) for the full mode matrix.

The version key flows from configuration: `appsettings.json`
(`ClaimsOptions:DataStandardVersion`) and the compose files
(`ClaimsOptions__DataStandardVersion: ${DMS_CONFIG_DATA_STANDARD_VERSION:-5.2}`).

> **ResourceClaim seeding:** the static `ResourceClaim` seed
> (`0009_Insert_ResourceClaim.sql`, run by dbup) stays the Data Standard 5.2 baseline so
> existing rows keep their identifiers. On top of that, `ClaimsDataLoader` derives the
> resource-claim rows from the version-selected `Claims.json` hierarchy at initial load and
> inserts only the rows missing from the table (`ResourceClaimMetadataRepository`,
> `ON CONFLICT (ClaimName) DO NOTHING`), so a version contributes its own resources
> (e.g. DS 6.1's in-core TPDM) without editing the dbup script. Seeding is PostgreSQL-only;
> on the MSSQL backend the loader falls back to a no-op. The authorization-strategy seed
> (`0008_Insert_AuthorizationStrategy.sql`) is data-standard-independent and shared across
> all versions.

## CI / package builds

Workflows that build per-version artifacts select the version through a per-workflow
`standard_version` matrix rather than a hard-coded literal.

- **Populated template** (`EdFi.Api.Populated.Template.PostgreSQL.yml`) and the
  **scheduled smoke test** (`scheduled-smoke-test.yml`) delegate to the reusable
  `build-populated-template.yml`. Each matrix leg runs the whole
  build → SBOM → provenance pipeline as one unit (so per-version outputs stay
  correlated), and the per-version package name, provenance file, and template env
  file are read from the matrix `include` entry; **adding a version is adding one
  `include` entry** (plus the schema artifacts and a template env file for that version).
  - The **populated template** product workflow (tag/release/`workflow_dispatch`) builds
    both `5.2.0` and `6.1.0`. The `6.1.0` leg is validated by dispatch
    (`publish_package` defaults off) before any release; the reusable workflow's
    `environment_file` allowlist gates which env files may be used.
  - The **scheduled smoke test** (which also runs on PRs touching its paths) stays
    `5.2.0`-only until the `6.1.0` populated build is validated end-to-end, so the PR
    lane never goes red on an unproven leg; the `6.1.0` smoke leg is a one-`include`-entry
    addition once that validation passes.

  > The template surface is intentionally reduced versus the local dev surface
  > (Core + TPDM + Sample + Homograph in `.env.ds<NN>`): DS 5.2 is Core + TPDM
  > (`.env.template.relational`); DS 6.1 is Core only (`.env.template.relational.ds61`,
  > TPDM folded into core). Each version gets its own standalone template env file,
  > referenced from the matrix `include` entry — it is **not** the dev `.env.ds<NN>` overlay.

- **SDK** (`Pkg EdFi.Api.Sdk.yml`, `Pkg EdFi.Api.TestSdk.yml`) and **Minimal
  template** (`EdFi.Api.Minimal.Template.PostgreSQL.yml`) are currently
  single-version (`5.2.0`), with the version held in each file's top-level `env`
  block. These workflows are flat, self-contained job graphs whose provenance job
  reads the build job's `hash-code` output; GitHub Actions does not correlate matrix
  legs across jobs for outputs, so matrixing them safely requires first extracting a
  reusable workflow (mirroring `build-populated-template.yml`) and matrixing the thin
  caller. That refactor is deferred to a dedicated, separately-validated change.

## Version-coupled tests and special handling

Some tests assert response bodies that embed the running data standard version. They pass
against whichever version the stack is running (5.2 today), but enabling a **new** version's
E2E lane requires updating them with that version's *actual* output — which has to be captured
from a running stack of that version, not hand-written. Known surfaces:

- **`.../Tests.E2E/Features/General/XSDMetadata.feature`** — embeds the version string
  (`"5.2.0"`), the XSD namespace (`http://ed-fi.org/5.2.0`), and the full per-extension XSD file
  listing.
- **`.../Tests.E2E/Features/General/DiscoveryAPI.feature`** — embeds the data model `version`
  and `informationalVersion`.
- The **integration-test fixtures** also pin a data standard version when materializing the
  runtime schema.

**Structural difference, not just a version string (DS 6.1 example):** in DS 6.1 the **TPDM
model is folded into core** — there is no separate TPDM extension. So a 6.1 `/metadata/xsd`
response has **no `tpdm` entry** and there is **no `/metadata/xsd/tpdm/...` files endpoint**,
whereas 5.2 lists TPDM as a distinct extension. Enabling 6.1 E2E therefore means *rewriting*
these expectations for 6.1, not search-replacing `5.2.0` → `6.1.0`. This is part of the staged
PR-E2E work (it lands when DS 6.1 joins the E2E lane).

### Per-PR E2E scope — DS 6.1 is staged (decision)

The every-change (per-PR) relational E2E lane stays **DS 5.2** for now: `on-dms-pullrequest.yml`
runs `build-dms.ps1 E2ETest … -EnvironmentFile './.env.e2e.relational'` (no `-DataStandardVersion`),
sharded but single-version. DS 6.1 is exercised by the **scheduled / package-build** lanes (the
populated-template product matrix) rather than on every PR, so the slow, occasionally-flaky per-PR
lane is not doubled before 6.1 is otherwise proven.

Turning the per-PR lane on for 6.1 later is a small, contained change — add a `standard_version`
dimension to the E2E matrix and pass `-DataStandardVersion 6.1` (`build-dms.ps1` now composes the
`.env.ds<NN>` overlay consistently across provisioning, seed, configure, and startup). The gating
prerequisite is **not** the wiring — it is authoring the version-coupled expectations above against
a running 6.1 stack.

## Adding a Data Standard version

Adding a version is the same set of small edits regardless of which version:

1. **Confirm the package facts (external precondition).** The exact ApiSchema package
   IDs and feed versions, which extension variants exist (TPDM / Sample / Homograph),
   and the template package id — these come from the published Ed-Fi feed and cannot be
   invented.
2. **Schema artifacts.** Add the version's `PackageVersion` entries to
   `src/Directory.Packages.props`, keeping the versions consistent with the
   `SCHEMA_PACKAGES` you put in the overlay.
3. **Local overlay.** Add `eng/docker-compose/.env.ds<NN>` modeled on `.env.ds52`:
   `DMS_CONFIG_DATA_STANDARD_VERSION=<N.N>`, the version's `DATABASE_TEMPLATE_PACKAGE`,
   and a single-line `SCHEMA_PACKAGES` listing that version's packages.
4. **Security metadata.** Add `Claims/Standards/ds<NN>/Claims.json` (authored from the
   ODS API SecurityMetadata XML export via the `eng/CmsHierarchy` tool). No csproj
   change is needed — the embedded-resource glob picks it up.
5. **CI.** Add an `include` entry to the `standard_version` matrix in
   `EdFi.Api.Populated.Template.PostgreSQL.yml` (and, once validated, `scheduled-smoke-test.yml`),
   add the version's standalone template env file (its product schema surface — Core + TPDM for
   5.2, Core only for 6.1) referenced by that entry, and allow that env file in
   `build-populated-template.yml`'s `environment_file` allowlist. (The SDK and Minimal lanes
   follow once their reusable-workflow extraction lands.)

Because Data Standard 5.2 is the default, none of these edits change 5.2 behavior:
new folders, overlays, package entries, and matrix legs sit alongside the existing
ones.

## Dropping a Data Standard version

Reverse of the above — remove that version's `Claims/Standards/ds<NN>/` folder, its
`.env.ds<NN>` overlay, its `Directory.Packages.props` entries, its template env file,
and its matrix `include` entry. Ensure the default version (5.2, unless the default is
being changed) still exists.
