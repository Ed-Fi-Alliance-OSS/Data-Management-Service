---
design: DMS-916
---

# Story: MetaEd ApiSchema Asset Packaging

## Description

Publish ApiSchema NuGet packages as asset-only packages that DMS bootstrap can normalize into the file-based
ApiSchema asset container defined for DMS-916. The current MetaEd packaging workflow builds generated
`*.ApiSchema.dll` assemblies whose embedded resources contain `ApiSchema.json` and XSD files. That package
shape forces DMS runtime or bootstrap to understand assembly resources, which conflicts with the DMS-916
staged JSON/file workspace.

This story is the cross-repo MetaEd package-production switch-over. It changes the package production shape,
not DMS bootstrap orchestration or DMS runtime content loading. It can proceed in parallel with DMS
bootstrap work because the shared contract is the filesystem ApiSchema workspace: direct `-ApiSchemaPath`
loading remains valid, and Story 06 package-backed mode materializes this package payload into that
same workspace.

## Acceptance Criteria

- MetaEd publishes ApiSchema packages whose schema and static assets are loose files in a documented package
  location, for example:

  ```text
  contentFiles/any/any/ApiSchema/
    package-manifest.json
    ApiSchema.json
    discovery-spec.json
    xsd/
      Ed-Fi-Core.xsd
      Interchange-Student.xsd
  ```

- Extension packages use the same package contract as core packages.
- The package schema file is named `ApiSchema.json` in the package contract. If MetaEd generation produces
  `ApiSchema-EXTENSION.json`, packaging normalizes it to `ApiSchema.json`; the package manifest and schema
  content identify whether the project is core or extension.
- Each package contains `contentFiles/any/any/ApiSchema/package-manifest.json` with:
  - package manifest version,
  - package ID,
  - project name,
  - project endpoint name,
  - `isExtensionProject`,
  - relative schema path,
  - optional discovery/specification JSON path,
  - optional XSD directory.
- The produced `.nupkg` contains no `lib/`, `ref/`, generated assembly, marker source, `bin/`, or `obj/`
  payload entries.
- The packaging workflow validates the staged package directory before publish:
  - exactly one schema JSON file at the package contract path,
  - schema JSON is parseable,
  - package manifest paths exist when non-null,
  - no duplicate relative paths,
  - no forbidden DLL/source/build-output payload entries.
- Package metadata, feed, versioning, license, and documentation conventions remain compatible with the
  existing ApiSchema package publication path unless a separate package-versioning decision changes them.
- The package contract supplies enough metadata for DMS bootstrap to resolve the package from the configured
  feed, extract the documented asset payload, and reject DLL-only packages once the asset-only package
  contract is required.

## Tasks

1. Replace the DLL-oriented ApiSchema packaging path in MetaEd with an asset staging step that copies generated
   schema JSON and static content into a package staging directory.
2. Generate `package-manifest.json` for each core or extension package from the packaging matrix entry and the
   staged files.
3. For core packages, stage generated core `ApiSchema.json`, XSD/interchange files, and any available
   generated or curated discovery/specification JSON.
4. For extension packages, stage generated extension schema JSON as package-contract `ApiSchema.json`, stage
   extension XSD/interchange files, and omit optional assets that the extension does not provide.
5. Add validation that fails packaging when required files are missing, JSON cannot parse, manifest paths are
   invalid, duplicate relative paths exist, or forbidden build-output payloads are present.
6. Pack the staged directory into an asset-only `.nupkg` and publish it to the same configured feed.
7. Document the package contract for DMS bootstrap consumers.

## Out of Scope

- DMS `prepare-dms-schema.ps1` package resolution; that belongs to Story 06.
- DMS direct filesystem workspace normalization; that belongs to Story 00.
- DMS runtime `ContentProvider` changes; that belongs to Story 04.
- Direct filesystem ApiSchema loading; that remains a DMS bootstrap input contract.
- Replacing NuGet as the distribution and versioning mechanism.
- Defining a generalized plugin system.
- Changing generated ApiSchema semantics.

## Design References

- [`../apischema-container.md`](../apischema-container.md), Sections "Published NuGet Shape" and "MetaEd Packaging Replacement"
- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 3, 13, and 14
- [`00-schema-and-security-selection.md`](00-schema-and-security-selection.md)
- [`04-apischema-runtime-content-loading.md`](04-apischema-runtime-content-loading.md)
