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
  `build-populated-template.yml`.
  Each matrix leg runs the whole build → SBOM → provenance pipeline as one unit (so
  per-version outputs stay correlated), and the per-version package name, provenance
  file, and template env file are read from the matrix leg.
  For the populated template workflow, that leg is a `standard_version` matrix
  `include` entry, and **adding a version there is adding one `include` entry**
  (plus the schema artifacts and a template env file for that version).
  - The **populated template** product workflow (tag/release/`workflow_dispatch`) builds
    both `5.2.0` and `6.1.0`. The `6.1.0` leg is validated by dispatch
    (`publish_package` defaults off) before any release; the reusable workflow's
    `environment_file` allowlist gates which env files may be used.
  - The **scheduled smoke test** (which also runs on PRs touching its paths) does not
    read a static `include` list.
    A `select-legs` job computes its matrix from the leg table inside
    `eng/DatabaseTemplates/Select-SmokeTestLegs.ps1`, which is the single place the
    smoke legs are defined.
    Each Data Standard version carries two legs there, one per database engine
    (postgresql and mssql), so the workflow runs four legs total: PostgreSQL/5.2.0,
    PostgreSQL/6.1.0, MSSQL/5.2.0, and MSSQL/6.1.0 (the `6.1.0` legs' populated builds
    are validated end-to-end on both engines).
    Scheduled and `workflow_dispatch` runs always execute the full four-leg matrix;
    PR runs execute only the legs a changed file can safely affect, falling back to
    the full matrix when a change is unmapped or the changed-file list is empty.
    The ODS-published-SDK sweep stays `5.2.0`-only on both engines - it consumes the
    DS-5.2-specific `EdFi.Suite3.OdsApi.TestSdk.Standard.5.2.0` package - and is gated
    by a `run_ods_sdk_tests` matrix flag; the `6.1.0` legs' API surface is instead
    covered by the NonDestructive API tests and the DMS-generated SDK.

  > The template surface is intentionally reduced versus the local dev surface
  > (Core + TPDM + Sample + Homograph in `.env.ds<NN>`): DS 5.2 is Core + TPDM
  > (`.env.template`); DS 6.1 is Core only (`.env.template.ds61`,
  > TPDM folded into core). Each version gets its own standalone template env file,
  > referenced from the matrix `include` entry — it is **not** the dev `.env.ds<NN>` overlay.

- **SDK** (`Pkg EdFi.Api.Sdk.yml`, `Pkg EdFi.Api.TestSdk.yml`) and **Minimal template**
  (`EdFi.Api.Minimal.Template.PostgreSQL.yml`) follow the same pattern: each is now a thin
  matrix caller over a per-lane reusable workflow (`build-sdk-package.yml`,
  `build-minimal-template.yml`), with one `include` entry per Data Standard version
  (`5.2.0` and `6.1.0`). Extracting the reusable workflow first is what makes matrixing
  safe — these lanes' provenance job reads the build job's `hash-code` output, and GitHub
  Actions does not correlate matrix legs across jobs for outputs, so a flat job graph would
  feed the wrong leg's hash to provenance once a second version is added. The reusable
  workflow keeps each leg's build → SBOM → provenance pipeline self-contained, and each leg
  uses a distinct package name and provenance file so artifacts never collide.
  - The **Minimal template** `6.1.0` leg reuses the `.env.template.ds61` template
    env file (Core only).
  - The **SDK** lanes generate each package from the OpenAPI document a running DMS serves,
    so the `6.1.0` leg starts DMS with `-DataStandardVersion 6.1` (the `.env.ds61` overlay)
    and names the package `…Standard.6.1.0`; the `5.2.0` leg keeps the base `.env.e2e`
    behavior.
  - These lanes are tag/release/`workflow_dispatch`-triggered (not exercised on PRs), so the
    `6.1.0` legs are validated by dispatch (`publish_package` defaults off) before release —
    the same gate the populated-template `6.1.0` leg uses.

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
whereas 5.2 lists TPDM as a distinct extension. Enabling 6.1 E2E therefore meant *rewriting*
these expectations for 6.1, not search-replacing `5.2.0` → `6.1.0`. Those 6.1 expectations are now
authored as `@StandardVersion-6_1` scenario variants alongside the 5.2 ones — captured from a
running 6.1 stack — with the two TPDM scenarios (and the TPDM data model in the Discovery root)
dropped.

### Per-PR E2E scope — DS 6.1 version-coupled lane

The every-change (per-PR) relational E2E lane runs **DS 5.2** as the full sharded suite
(`on-dms-pullrequest.yml` → `run-e2e-tests`: four shards, `-EnvironmentFile './.env.e2e'`,
no `-DataStandardVersion`). DS 6.1 is covered per-PR by a **dedicated, lean lane**
(`run-e2e-tests-ds61`) that brings up a 6.1 stack (`-DataStandardVersion 6.1`) and runs **only the
version-coupled scenarios** — the `@StandardVersion-6_1`-tagged XSDMetadata and Discovery-root
variants:

```
build-dms.ps1 E2ETest … -EnvironmentFile './.env.e2e' -DataStandardVersion 6.1 \
  -TestFilter 'Category=@StandardVersion-6_1'
```

Those scenarios carry **no shard tag**, so the 5.2 shard lanes (which filter on a
`@e2e-ci-shard-N` category) never pick them up, and the 6.1 lane never re-runs the
version-independent suite. Both lanes gate the PR through `e2e-summary`.

This keeps per-PR cost contained: the version-coupled scenarios are exactly what differs between
versions, while **full DS 6.1 E2E coverage runs in the scheduled smoke test** rather than on every
PR — so the slow, occasionally-flaky relational suite is not doubled. The version-coupled scenarios
are version-selected purely by category tags (`@StandardVersion-<NN>`), not by separate stacks per
shard: a future version adds its own `@StandardVersion-<NN>` variants and a sibling lane.

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
5. **CI.** Add an `include` entry to the `standard_version` matrix in each per-version lane -
   `EdFi.Api.Populated.Template.PostgreSQL.yml`, `EdFi.Api.Minimal.Template.PostgreSQL.yml`, and
   the SDK callers `Pkg EdFi.Api.Sdk.yml` / `Pkg EdFi.Api.TestSdk.yml`.
   Add the version's standalone template env file (its product schema surface - Core +
   TPDM for 5.2, Core only for 6.1), referenced by the template entries, and allow that
   env file in `build-populated-template.yml`'s `environment_file` allowlist.
   The SDK legs need no template env file - they start DMS via the `.env.ds<NN>` overlay
   (a non-default version requires a `data_standard_version` value in the leg so the
   overlay is applied).
   Once validated, add the version to the scheduled smoke test too: the leg table inside
   `eng/DatabaseTemplates/Select-SmokeTestLegs.ps1` is the single place the smoke legs are
   defined, and each Data Standard version carries two legs there, one for postgresql and
   one for mssql, so adding a version means adding both engine legs to that table.
6. **Version-coupled E2E.** Author the version's `@StandardVersion-<NN>` scenario variants for the
   version-coupled features (`XSDMetadata.feature`, `DiscoveryAPI.feature`) — captured from a running
   stack of that version, not hand-written — and add a per-PR lane that runs them with
   `-DataStandardVersion <N.N>` and
   `-TestFilter 'Category=@StandardVersion-<NN>'` (see *Per-PR E2E scope*
   above). Give the variants **no** shard category so the default-version shard lanes skip them.

Because Data Standard 5.2 is the default, none of these edits change 5.2 behavior:
new folders, overlays, package entries, and matrix legs sit alongside the existing
ones.

## Dropping a Data Standard version

Reverse of the above - remove that version's `Claims/Standards/ds<NN>/` folder, its
`.env.ds<NN>` overlay, its `Directory.Packages.props` entries, its template env file,
and its matrix `include` entry in each per-version lane.
For the scheduled smoke test, remove the version's two legs (postgresql and mssql)
from the leg table inside `eng/DatabaseTemplates/Select-SmokeTestLegs.ps1` instead of
an `include` entry.
Ensure the default version (5.2, unless the default is being changed) still exists.
