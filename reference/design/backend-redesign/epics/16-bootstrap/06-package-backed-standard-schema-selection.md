---
jira: DMS-1156
jira_url: https://edfi.atlassian.net/browse/DMS-1156
---

# Story: Package-Backed Standard Schema Selection

## Description

Implement the package-backed standard schema-selection path for DMS developer bootstrap. This story turns
the target day-to-day modes from the design into an owned implementation slice:

- omitted `-Extensions` for the core-only developer default,
- named `-Extensions` for package-backed extension selection,
- package-backed ApiSchema asset resolution after Story 05 publishes asset-only packages,
- convergence on the same staged ApiSchema workspace, claims staging, and seed-input handoff contracts
  delivered by Story 00.

This story exists to close the traceability gap between the DMS-916 acceptance criterion for
parameterized extension selection and the earlier direct-filesystem-only Story 00 slice. Story 00 remains
the expert `-ApiSchemaPath` story. Story 06 owns the standard `-Extensions` acquisition path and must feed
the same normalized file-based ApiSchema asset container at
`eng/docker-compose/.bootstrap/ApiSchema/`.

The package-backed path is only an input-materialization path. It must not introduce a second schema
authority, claims path, seed catalog, wrapper contract, or lifecycle control plane. After this story resolves
and stages package assets, downstream phases consume the same manifests and workspaces used for direct
filesystem inputs.

## Acceptance Criteria

- `prepare-dms-schema.ps1` supports standard mode without `-ApiSchemaPath` after the Story 05 package
  prerequisite is available:
  - omitting `-Extensions` resolves and stages the core Data Standard ApiSchema package only,
  - supplying `-Extensions` resolves and stages the core package plus each named extension package.
- `-Extensions` is a `String[]` parameter using normal PowerShell array binding. Multi-extension usage uses
  syntax such as `-Extensions "sample","extension2"`; the implementation does not rely on comma-splitting a
  single string.
- Extension artifact availability is determined by package and companion artifact resolution; missing artifacts
  fail fast before Docker operations start.
- `-Extensions` and `-ApiSchemaPath` are mutually exclusive. Supplying both fails fast with guidance to use
  standard mode for package-backed extensions or expert mode for a custom schema directory.
- Package-backed resolution uses the configured NuGet feed and package/version defaults. The exact core
  package ID, extension package identity convention or metadata source, and version defaults are documented
  and validated against the feed during implementation.
- Package materialization rejects invalid package payloads before finalizing the staged workspace:
  - missing asset-only ApiSchema payload,
  - missing or malformed package manifest,
  - zero or multiple schema JSON files at the package contract path,
  - forbidden DLL-only `lib/` or `ref/` ApiSchema package shape after the asset-only package switch-over,
  - duplicate normalized manifest-relative paths.
- Package-backed standard mode writes the same `bootstrap-api-schema-manifest.json` runtime asset index as
  Story 00. That ApiSchema manifest records selected project identity, normalized schema paths, and optional
  static content paths only. The root bootstrap manifest records standard-mode schema selection, selected
  extension names, expected `EffectiveSchemaHash`, the ApiSchema workspace fingerprint, and the
  relative ApiSchema manifest path.
- The staged schema files from package-backed standard mode are the only files used for
  `EffectiveSchemaHash` calculation, Docker-hosted DMS startup, and IDE-hosted DMS startup.
- `prepare-dms-claims.ps1` consumes the root bootstrap manifest schema section and staged schema files through
  the same mode-to-security contract used for Story 00:
  - core-only standard mode stays Embedded mode unless additive claims are supplied by another supported
    path,
  - selected extensions automatically stage their bootstrap-managed claimset fragments when available,
  - no later phase re-derives extension security from command-line parameters.
- Standard-mode selected extensions recorded in the root bootstrap manifest are the source used by
  Story 02 seed delivery for built-in extension seed lookup. Story 06 does not load seed data directly and
  does not define a second seed catalog.
- The optional wrapper (`bootstrap-local-dms.ps1`) may expose `-Extensions` for the happy path, but only
  forwards it to `prepare-dms-schema.ps1`; the wrapper does not own package resolution or schema policy.
- Examples in the main design that use omitted `-Extensions` or named `-Extensions` are delivered by this
  story, not by Story 00.

## Tasks

1. Define package-backed schema resolution for standard mode: core package identity/version defaults,
   extension package identity convention or metadata source, namespace prefixes, and the security fragment
   lookup keys shared with claims staging.
2. Update `prepare-dms-schema.ps1` so standard mode is valid when `-ApiSchemaPath` is omitted:
   core-only when `-Extensions` is omitted, and core plus selected extensions when it is supplied.
3. Preserve `-ApiSchemaPath` as the expert direct-filesystem path from Story 00 and enforce mutual
   exclusivity between `-ApiSchemaPath` and `-Extensions`.
4. Implement NuGet package resolution and isolated extraction for the configured package feed, with clear
   diagnostics for unreachable feeds, missing packages, and version-resolution failures.
5. Validate each extracted package against the asset-only ApiSchema package contract from Story 05 before
   copying any files into the final staged workspace.
6. Reuse the Story 00 workspace-normalization path to stage one core schema plus zero or more extension
   schemas, optional static content, deterministic manifest-relative paths, normalized-path collision
   detection, and `EffectiveSchemaHash` calculation.
7. Write package-backed standard-mode schema facts into the root bootstrap manifest schema section and ensure
   `prepare-dms-claims.ps1` writes selected extension namespace prefixes into the root bootstrap
   manifest seed section.
8. Update wrapper parameter forwarding and validation so `bootstrap-local-dms.ps1 -Extensions ...` reaches
   `prepare-dms-schema.ps1` without the wrapper owning schema resolution.
9. Add tests for core-only standard mode, single and multiple extensions, artifact resolution failures,
   mutual exclusivity with `-ApiSchemaPath`, invalid package payload rejection, and reuse of identical staged
   package-backed workspaces.
10. Update developer-facing examples and failure messages so Story 00 direct-filesystem examples and Story
    06 package-backed standard-mode examples are clearly distinguished.

## Out of Scope

- Direct filesystem `-ApiSchemaPath` staging; that belongs to Story 00.
- DMS runtime `ContentProvider` changes; that belongs to Story 04.
- Publishing asset-only ApiSchema packages from MetaEd; that belongs to Story 05.
- API-based seed loading, BulkLoadClient invocation, and built-in seed package loading; those belong to
  Story 02.
- Published agency/sysadmin seed distribution; that belongs to DMS-1119.
- A generalized plugin system, arbitrary package IDs supplied by developers, or per-extension version
  override features beyond the documented DMS-916 defaults.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 3, 8, 13, and 14
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Sections 3.1, 3.2, and 3.7
- [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md)
- [`00-schema-and-security-selection.md`](00-schema-and-security-selection.md)
- [`02-api-seed-delivery.md`](02-api-seed-delivery.md)
- [`04-apischema-runtime-content-loading.md`](04-apischema-runtime-content-loading.md)
- [`05-metaed-apischema-asset-packaging.md`](05-metaed-apischema-asset-packaging.md)
