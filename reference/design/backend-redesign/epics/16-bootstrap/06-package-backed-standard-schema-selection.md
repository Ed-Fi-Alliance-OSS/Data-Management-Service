---
jira: DMS-1156
jira_url: https://edfi.atlassian.net/browse/DMS-1156
---

# Story: Package-Backed Standard Schema Selection (core-only)

## Description

Implement the package-backed standard schema-selection path for DMS developer bootstrap. As of the
2026-06-16 scope decision (see Review Log), this story is **package-backed core-only standard mode**:

- running `prepare-dms-schema.ps1` without `-ApiSchemaPath` resolves and stages the core Data Standard
  ApiSchema package only,
- package-backed ApiSchema asset resolution after Story 05 publishes asset-only packages,
- convergence on the same staged ApiSchema workspace, claims staging, and seed-input handoff contracts
  delivered by Story 00.

Named extension package selection is **not** part of this story. There is no `-Extensions` parameter on any
bootstrap script. Developers who need an extension-containing schema set use the expert
`-ApiSchemaPath` filesystem path owned by Story 00, which already stages core plus any extensions present in
the supplied directory (for example the in-repo `EdFi.DataStandard52.ApiSchema` directory, which includes
TPDM).

Story 00 remains the expert `-ApiSchemaPath` story. Story 06 owns the standard package-backed core-only
acquisition path and must feed the same normalized file-based ApiSchema asset container at
`eng/docker-compose/.bootstrap/ApiSchema/`.

The package-backed path is only an input-materialization path. It must not introduce a second schema
authority, claims path, seed catalog, wrapper contract, or lifecycle control plane. After this story resolves
and stages the core package assets, downstream phases consume the same manifests and workspaces used for
direct filesystem inputs.

## Acceptance Criteria

- `prepare-dms-schema.ps1` supports standard mode without `-ApiSchemaPath` after the Story 05 package
  prerequisite is available: it resolves and stages the core Data Standard ApiSchema package only.
- `prepare-dms-schema.ps1` does not declare or accept an `-Extensions` parameter. Named extension package
  selection through a standard-mode parameter is not supported. Invoking the script with `-Extensions` fails
  with the native PowerShell "parameter cannot be found" error.
- `prepare-dms-schema.ps1 -ApiSchemaPath <path>` preserves the existing expert direct-filesystem behavior
  from Story 00, including schema sets that contain extensions. Expert mode is the supported path for
  extension and custom schema scenarios.
- Package-backed resolution uses the configured NuGet feed and the explicit core package/version default.
  The default is pinned and documented in one implementation location; bootstrap must not resolve floating
  `latest` versions or silently fall back to legacy DLL-backed packages. The core package ID uses the
  canonical Data-Standard-qualified convention defined in
  [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md):
  `EdFi.DataStandard52.ApiSchema`. The version default is pinned at `1.0.329`, the published asset-only
  version verified against the feed (see
  [`../../../../spikes/DMS-1156/asset-only-package-feed-findings.md`](../../../../spikes/DMS-1156/asset-only-package-feed-findings.md)).
- Package-backed standard mode does not use `SCHEMA_PACKAGES` as a selector, schema authority, or runtime
  handoff. The core package comes from the Story 06 standard-mode default; DMS runtime reads the staged
  workspace produced by bootstrap, not the `.nupkg`, NuGet cache, or `SCHEMA_PACKAGES`.
- Package materialization rejects invalid core package payloads before finalizing the staged workspace:
  - missing asset-only ApiSchema payload,
  - missing or malformed package manifest,
  - zero or multiple schema JSON files at the package contract path,
  - forbidden DLL-only `lib/` or `ref/` ApiSchema package shape after the asset-only package switch-over,
  - package ID, project identity, or `isExtensionProject` values that do not match the requested core
    package,
  - manifest-declared static assets (`discoverySpecPath`, `xsdDirectory`) that are present but missing on
    disk or empty; optional static content may be absent only when the manifest omits the path or records it
    as null,
  - duplicate normalized manifest-relative paths.
- Package-backed standard mode writes the same `bootstrap-api-schema-manifest.json` runtime asset index as
  Story 00. The root bootstrap manifest records standard-mode schema selection (`selectionMode = "Standard"`),
  `selectedExtensions = @()`, expected `EffectiveSchemaHash`, the ApiSchema workspace fingerprint, and the
  relative ApiSchema manifest path.
- The staged schema files from package-backed standard mode are the only files used for
  `EffectiveSchemaHash` calculation, Docker-hosted DMS startup, and IDE-hosted DMS startup.
- `prepare-dms-claims.ps1` consumes the root bootstrap manifest schema section and staged schema files through
  the same mode-to-security contract used for Story 00:
  - core-only standard mode stays Embedded mode unless additive claims are supplied by another supported
    path,
  - in expert `-ApiSchemaPath` mode, extensions present in the staged schema set automatically stage their
    bootstrap-managed claimset fragments when available (e.g. Sample, Homograph), and extensions without a
    bootstrap-managed fragment require caller-supplied `-ClaimsDirectoryPath` fragments,
  - no later phase re-derives extension security from command-line parameters.
- The root bootstrap manifest `selectedExtensions` value is the source used by Story 02 seed delivery for
  built-in extension seed lookup. For core-only standard mode this is empty. Story 06 does not load seed data
  directly and does not define a second seed catalog.
- The optional wrappers (`bootstrap-local-dms.ps1`, `bootstrap-published-dms.ps1`) do not declare, document,
  or forward `-Extensions`. When no bootstrap manifest exists they stage core-only standard mode before
  continuing; when a workspace is already staged (a manual or expert prepare flow) they use it as-is.
- Same-checkout reruns reuse an existing package-backed staged ApiSchema workspace only when the intended
  package-backed selection produces the same `EffectiveSchemaHash` and workspace fingerprint. Different
  package versions or staged package contents fail fast with teardown guidance rather than rewriting a
  workspace that may still be bind-mounted into a running stack.

## Tasks

1. Pin the standard-mode core package identity/version default in one implementation location
   (`bootstrap-schema-catalog.psm1`): core package ID, project/endpoint tokens, feed URL, and version.
2. Remove the `-Extensions` parameter from `prepare-dms-schema.ps1`. Standard mode (no `-ApiSchemaPath`)
   resolves and stages the core package only.
3. Preserve `-ApiSchemaPath` as the expert direct-filesystem path from Story 00, including extension-containing
   filesystem schema sets.
4. Remove the open extension-resolution behavior from the standard-mode path (`Resolve-StandardExtensionPackage`
   and its short-name → package-ID construction). Named extension package selection is no longer supported in
   standard mode.
5. Implement/retain NuGet package resolution and isolated extraction for the configured feed, with clear
   diagnostics for unreachable feeds, missing packages, missing exact default versions, and
   version-resolution failures.
6. Validate the extracted core package against the asset-only ApiSchema package contract from Story 05 before
   copying any files into the final staged workspace, including package/project identity checks against the
   requested core package.
7. Reuse the Story 00 workspace-normalization path to stage the core schema, optional static content,
   deterministic manifest-relative paths, normalized-path collision detection, and `EffectiveSchemaHash`
   calculation, writing standard-mode schema facts (`selectedExtensions = @()`) into the root bootstrap
   manifest schema section.
8. Remove `-Extensions` from `bootstrap-local-dms.ps1`, `bootstrap-published-dms.ps1`, and
   `Invoke-BootstrapWrapper`. Preserve clean-workspace auto-staging of core-only standard mode and the
   `-LoadSeedData` core-only path.
9. Update tests: keep focused coverage for core-only package-backed standard mode and core package payload
   validation; remove tests that prove `-Extensions` comma-splitting, duplicate handling, extension package
   resolution, open/TPDM resolution, wrapper `-Extensions` forwarding, or standard-mode extension drift;
   preserve expert `-ApiSchemaPath` tests for extension-containing filesystem schema sets; add tests proving
   `prepare-dms-schema.ps1`, `bootstrap-local-dms.ps1`, `bootstrap-published-dms.ps1`, and
   `Invoke-BootstrapWrapper` no longer expose `-Extensions`.
10. Update developer-facing examples and failure messages so Story 00 direct-filesystem examples and Story 06
    package-backed core-only standard-mode examples are clearly distinguished, and so removed `-Extensions`
    usage is no longer documented.

## Out of Scope

- Direct filesystem `-ApiSchemaPath` staging; that belongs to Story 00.
- Named/standard-mode extension package selection of any kind (no `-Extensions` parameter, no allowlist, no
  open resolution). Extension and custom schema scenarios use expert `-ApiSchemaPath`.
- DMS runtime `ContentProvider` changes; that belongs to Story 04.
- Publishing asset-only ApiSchema packages from MetaEd; that belongs to Story 05.
- API-based seed loading, BulkLoadClient invocation, and built-in seed package loading; those belong to
  Story 02.
- Published agency/sysadmin seed distribution; that belongs to DMS-1119.
- Reading `SCHEMA_PACKAGES` as the Story 06 schema selector or accepting legacy unqualified package IDs.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 3, 8, 13, and 14
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Sections 3.1, 3.2, and 3.7
- [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md)
- [`00-schema-and-security-selection.md`](00-schema-and-security-selection.md)
- [`02-api-seed-delivery.md`](02-api-seed-delivery.md)
- [`04-apischema-runtime-content-loading.md`](04-apischema-runtime-content-loading.md)
- [`05-metaed-apischema-asset-packaging.md`](05-metaed-apischema-asset-packaging.md)

## Review Log

### 2026-06-16 — scope decision: remove `-Extensions`, standard mode is core-only

The team decided to refactor and simplify DMS-1156 by **removing the `-Extensions` parameter entirely**. The
new scope is package-backed **core-only** standard schema selection. Named extension package selection is no
longer part of this story; expert/custom extension scenarios use `-ApiSchemaPath`.

This decision **supersedes the 2026-06-08 "open resolution" entry below**, which had resolved a Jira↔local
conflict (closed mapped-extension catalog with TPDM rejection vs. open package-backed resolution) in favor of
open resolution. Neither the closed-catalog model nor the open-resolution model applies any longer: there is
no standard-mode extension-selection surface at all. The recurring review disagreement between the Jira
ticket (closed catalog / TPDM rejection) and the local design (open resolution) is closed by removing the
surface that the conflict was about.

Consequences:

- `prepare-dms-schema.ps1` drops `-Extensions`; standard mode stages the core package only.
- `bootstrap-schema-catalog.psm1` drops `Resolve-StandardExtensionPackage` and the short-name project-token
  map. The core package accessors and `Get-StandardKnownExtensionInfo` / `KnownExtensionClaimsMetadata`
  remain: `prepare-dms-claims.ps1` still uses the latter to auto-stage Sample/Homograph claim fragments for
  extensions present in an **expert** `-ApiSchemaPath` schema set (Story 00 behavior, unchanged).
- The wrappers drop `-Extensions`; clean-workspace core-only auto-staging and the `-LoadSeedData` core-only
  path are preserved.
- The Jira DMS-1156 acceptance criteria must be updated to match this core-only scope (the closed-catalog /
  named-`-Extensions` language is obsolete).

### 2026-06-08 — package feed verification (core package pin)

Verified the published core package directly on the feed
(`reference/spikes/DMS-1156/asset-only-package-feed-findings.md`):

- The core package `EdFi.DataStandard52.ApiSchema` is published as asset-only at `1.0.329` (the asset-only
  switch-over version; `1.0.328` is still DLL-backed). It was extracted and confirmed asset-only
  (`contentFiles/any/any/ApiSchema/…`, no `lib/`/`ref/`/`dll`).
- Implementation consequence: `bootstrap-schema-catalog.psm1` is the single pinned location for the feed URL
  and core version pin; the resolver can be exercised against the real `1.0.329` core package, with fixtures
  reserved for offline failure-path tests.

> Historical note: earlier revisions of this story and `apischema-container.md` documented an
> `EdFi.DataStandard52.<Project>.ApiSchema` extension package-ID convention used by the now-removed
> standard-mode `-Extensions` surface. Those extension package-ID references have been removed along with the
> feature. Extension packages, where needed, are consumed through expert `-ApiSchemaPath` filesystem staging,
> which does not construct package IDs.

### 2026-06-09 — post-implementation review triage (cross-story scope)

Reviewed the implemented changeset against the epic story scopes. Findings retained as in-scope for DMS-1156:

- Stage-time identity lock — the project identity inside the staged core `ApiSchema.json` is asserted against
  the validated package manifest, so a package that passes `packageId` validation cannot stage a different
  project.
- Optional `discoverySpecPath`/`xsdDirectory` fields may be omitted as well as null without a StrictMode
  error; a present-but-empty/missing declared static asset fails fast.
- `package-manifest.json` relative paths (`schemaPath`/`discoverySpecPath`/`xsdDirectory`) are rejected when
  rooted or containing `..`, as part of validating the extracted asset-only contract before staging.
- HTTP-feed service/version index parsing hardened against malformed/partial JSON.
