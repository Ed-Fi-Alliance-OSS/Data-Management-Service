---
spike: DMS-1156
date: 2026-06-08
---

# Asset-Only Package Feed Findings

**Purpose**: Verify that the core ApiSchema package is available on the Ed-Fi NuGet feed, confirm
whether the asset-only (Story 05 / Story 06 target) core package is published, and establish the
version pin for the package-backed core-only standard-mode work.

> **Scope note (DMS-1156, 2026-06-16):** package-backed standard mode was descoped to **core-only**.
> There is no `-Extensions` parameter and bootstrap does not resolve named extension packages.
> Extension-package findings from the original probe have been removed; extension-containing schema
> sets are staged through the expert `-ApiSchemaPath` filesystem path. Only the core package is
> relevant to this story.

---

## 1. Feed URL

```
https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json
```

This is the `feedUrl` value in every `SCHEMA_PACKAGES` entry across all `eng/docker-compose/.env*`
files. No `nuget.config` exists in the repository; the feed URL must be supplied explicitly in
configuration (sourced from the standard-mode defaults in `bootstrap-schema-catalog.psm1`, not from
`SCHEMA_PACKAGES`).

---

## 2. Core Package ID

The standard-mode core package ID is Data-Standard-qualified: `EdFi.DataStandard52.ApiSchema`.

NuGet package IDs are case-insensitive on the feed (lowercased probes resolve). The authoritative
`projectName` / `projectEndpointName` come from the package's own `package-manifest.json`, not from
the ID.

A legacy unqualified DLL-backed core ID is still present in repo `SCHEMA_PACKAGES` config (reference
only — not the Story 06 selector): `EdFi.DataStandard52.ApiSchema` pinned at `1.0.328` (DLL-backed).

---

## 3. Core Package Availability — Live Feed Probe Results (verified)

The core package is published as **asset-only at `1.0.329`** (published 2026-06-05) and returns HTTP
303 (download redirect) on the flat-container. Verified via flat2 version index,
`dotnet package search`, and direct `.nupkg` HEAD.

| Package ID                      | Version   | Shape (verified) | Status      |
|---------------------------------|-----------|------------------|-------------|
| `EdFi.DataStandard52.ApiSchema` | `1.0.329` | Asset-only       | Live, ready |

Notes:
- The `1.0.329` line is the asset-only transition. Core `1.0.328` (the legacy repo pin) is still
  DLL-backed; the asset-only switch-over happened at `1.0.329`.

### Verified core manifest (`EdFi.DataStandard52.ApiSchema` 1.0.329)

```json
{
  "version": 1,
  "packageId": "EdFi.DataStandard52.ApiSchema",
  "projectName": "Ed-Fi",
  "projectEndpointName": "ed-fi",
  "isExtensionProject": false,
  "schemaPath": "ApiSchema.json",
  "discoverySpecPath": "discovery-spec.json",
  "xsdDirectory": "xsd"
}
```

---

## 4. Recommended Pin

Pin the core package at `1.0.329`:

| Package ID                             | Pin       | Shape      |
|----------------------------------------|-----------|------------|
| `EdFi.DataStandard52.ApiSchema` (core) | `1.0.329` | Asset-only |

This is **not** provisional — it is the published asset-only core package.
`bootstrap-schema-catalog.psm1` is the single pinned location for the feed URL and core version pin.

---

## 5. Implication for the resolver

- The core asset-only package exists at `1.0.329`, so the resolver + extraction path can be exercised
  end-to-end against the **real** feed package — no fixtures required for the happy path.
- Fixture `.nupkg` files are still valuable for **failure-path** tests (missing payload, DLL-only
  shape, malformed/mismatched `package-manifest.json`, duplicate paths) and to keep the default test
  suite **offline** (tests offline by default; real-feed checks opt-in/skipped).
- The resolver resolves the core package by its pinned ID/version, feed/version driven from
  `bootstrap-schema-catalog.psm1`.
- The existing `ApiSchemaDownloader` CLI uses `ExtractApiSchemaJsonFromAssembly` and has no
  `contentFiles` extraction path. Story 06 implements its own asset-only `.nupkg` extraction; it
  cannot reuse the existing downloader.

---

## 6. Documented Asset-Only Payload Contract

Authority: `reference/design/backend-redesign/design-docs/bootstrap/apischema-container.md`.

### Required package structure

```
contentFiles/any/any/ApiSchema/
  package-manifest.json          (required)
  ApiSchema.json                 (required — one schema JSON at this contract path)
  discovery-spec.json            (optional)
  xsd/                           (optional directory)
    *.xsd
docs/
  README.md
  LICENSE
```

### Prohibited entries

The package must contain **none** of: `lib/`, `ref/`, `*.dll`, `*.cs`, `bin/`, `obj/`.

### `package-manifest.json` fields

| Field                  | Type      | Required | Description                                              |
|------------------------|-----------|----------|----------------------------------------------------------|
| `version`              | integer   | yes      | Manifest schema version (currently `1`)                  |
| `packageId`            | string    | yes      | NuGet package ID (e.g. `EdFi.DataStandard52.ApiSchema`)  |
| `projectName`          | string    | yes      | MetaEd project name (e.g. `Ed-Fi`)                       |
| `projectEndpointName`  | string    | yes      | API endpoint segment (e.g. `ed-fi`)                      |
| `isExtensionProject`   | boolean   | yes      | `false` for core                                         |
| `schemaPath`           | string    | yes      | Relative path to `ApiSchema.json` within the package     |
| `discoverySpecPath`    | string    | nullable | Relative path to `discovery-spec.json`, or `null`        |
| `xsdDirectory`         | string    | nullable | Relative path to XSD directory, or `null`                |

All non-null manifest-declared paths must exist in the package. The schema file named in
`schemaPath` must be parseable JSON.
