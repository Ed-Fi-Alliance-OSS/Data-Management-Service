---
design: DMS-916
---

# ApiSchema Asset Container

## Purpose

This note defines the preferred long-term shape for delivering ApiSchema runtime assets to DMS bootstrap.
It addresses the current mismatch between the DMS-916 staged `ApiSchema*.json` workspace and the existing
`ContentProvider` implementation, which still loads metadata and XSD content from `*.ApiSchema.dll`
assemblies.

The goal is to keep NuGet as a valid distribution mechanism without requiring schema assets to be bundled
as .NET assemblies in published packages or at runtime. The target state drops ApiSchema DLLs completely.

## Problem

The current design stages selected `ApiSchema*.json` files into:

```text
eng/docker-compose/.bootstrap/ApiSchema/
```

That is enough for the core ApiSchema loader path, which can read JSON files directly. It is not enough for
all current DMS schema-adjacent content paths. `ContentProvider` searches `AppSettings:ApiSchemaPath` for
`*.ApiSchema.dll`, loads those assemblies, enumerates embedded manifest resources, and serves embedded JSON
and XSD files from the assembly resource streams.

That means a JSON-only staged workspace can produce a split runtime:

- API surface and DDL validation use the selected staged JSON files.
- Metadata, discovery, or XSD endpoints still require package assemblies, can fail, or can reflect stale
  packaged assets.

This is a runtime contract gap, not a NuGet limitation. A `.nupkg` can carry loose content files. The current
ApiSchema packages use DLL embedded resources because that was the package shape chosen for distribution.

## Current MetaEd Packaging

MetaEd already generates the asset files before DLL packaging happens. In the MetaEd repository workflow
`.github/workflows/api-schema-packaging.yml`, the workflow:

1. runs the MetaEd project and uploads `MetaEdOutput`,
2. downloads that generated output in the packaging job,
3. runs `eng/ApiSchema/build.ps1 -Command MoveMetaEdSchema`, which copies generated `ApiSchema.json`,
   extension `ApiSchema-EXTENSION.json`, and XSD files into `eng/ApiSchema`,
4. mutates `Marker.cs` for extension packages,
5. runs `dotnet build` against an ApiSchema `.csproj`,
6. runs `dotnet pack` to publish a NuGet whose only payload is a generated `lib/.../*.ApiSchema.dll`
   containing embedded JSON and XSD resources.

The `.csproj` files exist to embed `*.json` and `xsd/*.xsd` as resources. `Marker.cs` documents that its only
purpose is enabling DLL creation. That layer is packaging ceremony, not a schema-generation requirement.

## Decision

The DMS-916 bootstrap target should be a normalized file-based ApiSchema asset container.

Direct filesystem ApiSchema loading is the stable core contract. Bootstrap may receive loose files from a
developer-supplied `-ApiSchemaPath`, or it may resolve a package and materialize its payload first. In both
cases, the output is the same stable repo-local workspace and every downstream DMS phase reads files from
that workspace. DMS runtime does not require `*.ApiSchema.dll` files for the bootstrap path.

NuGet remains only a transport and versioning mechanism. When package-backed bootstrap is used, the target
package shape is asset-only: bootstrap resolves the package, extracts its documented content-file payload,
and normalizes the selected assets into the same filesystem workspace. Package support is therefore an input
materialization concern, not the source-of-truth runtime contract.

The corresponding MetaEd packaging target is an asset-only NuGet package. ApiSchema NuGet packages should not
contain `lib/`, `ref/`, generated assemblies, or marker source compiled only to carry resources.

## Normalized Workspace

The staged workspace should contain both schema files and schema-adjacent static content:

```text
eng/docker-compose/.bootstrap/ApiSchema/
  bootstrap-api-schema-manifest.json
  schemas/
    EdFi.DataStandard52/
      ApiSchema.json
    EdFi.Sample/
      ApiSchema.json
  content/
    EdFi.DataStandard52/
      discovery-spec.json
      xsd/
        Ed-Fi-Core.xsd
        Interchange-Student.xsd
        Interchange-Descriptors.xsd
    EdFi.Sample/
      xsd/
        EXTENSION-Ed-Fi-Extended-Core.xsd
        EXTENSION-Interchange-Example.xsd
```

The directory names are implementation details, but the workspace must provide these logical assets:

- one core `ApiSchema.json`,
- zero or more extension `ApiSchema.json` files,
- any static discovery/specification JSON files supplied by the selected schema packages,
- any XSD files supplied by the selected schema packages,
- a manifest describing the staged projects and file paths.

## Manifest

The manifest is the runtime asset index for the normalized workspace. It should be small, relative-path based,
and deterministic.

Example shape:

```json
{
  "version": 1,
  "projects": [
    {
      "projectName": "EdFi",
      "projectEndpointName": "ed-fi",
      "isExtensionProject": false,
      "schemaPath": "schemas/EdFi.DataStandard52/ApiSchema.json",
      "discoverySpecPath": "content/EdFi.DataStandard52/discovery-spec.json",
      "xsdDirectory": "content/EdFi.DataStandard52/xsd"
    },
    {
      "projectName": "Sample",
      "projectEndpointName": "sample",
      "isExtensionProject": true,
      "schemaPath": "schemas/EdFi.Sample/ApiSchema.json",
      "discoverySpecPath": null,
      "xsdDirectory": "content/EdFi.Sample/xsd"
    }
  ]
}
```

The manifest is not a second schema authority and not the bootstrap compatibility manifest. The schema files
remain the authoritative inputs for `dms-schema hash`, DDL provisioning, and runtime API surface. The manifest
only records the project identity needed to interpret each file and the normalized relative paths to schema and
schema-adjacent runtime content.

## Bootstrap Responsibilities

`prepare-dms-schema.ps1` owns materializing this container. Its stable output is the normalized filesystem
workspace; package formats are only acquisition inputs.

For expert `-ApiSchemaPath`, bootstrap should normalize caller-supplied loose files into the same workspace.
If optional content such as discovery specs or XSD files is absent, DMS should return the same behavior it
would for a selected package that does not provide those optional assets. Missing schema files remain a
bootstrap failure. This direct filesystem input remains a supported loading path even after asset-only
packages replace DLL-backed package distribution.

For asset-only ApiSchema packages, bootstrap should:

1. Resolve the NuGet package.
2. Extract the package into an isolated package-specific temporary directory.
3. Read the package asset manifest from the documented package path.
4. Copy package assets into the normalized workspace:
   - package schema JSON becomes `schemas/<project>/ApiSchema.json`,
   - package discovery/specification JSON becomes `content/<project>/<name>.json`,
   - package XSD files become `content/<project>/xsd/<file>.xsd`.
5. Validate that the normalized workspace contains exactly one core schema and zero or more extension schemas.
6. Detect normalized-path collisions and fail before writing or finalizing the workspace.
7. Compute `EffectiveSchemaHash` from the normalized schema files.
8. Write `bootstrap-api-schema-manifest.json`.

Bootstrap should reject selected packages that only contain DLL-backed resources once the asset-only package
contract is required. Supporting those legacy packages would preserve the runtime/design ambiguity this file
is intended to remove. This rejection applies to package-backed materialization only; it does not deprecate
or narrow direct filesystem `-ApiSchemaPath` loading.

## Runtime Responsibilities

`ApiSchemaProvider` should load schema JSON from the normalized workspace. It may use either:

- the manifest's `schemaPath` entries, or
- a documented `schemas/**/ApiSchema.json` convention.

`ContentProvider` should stop using assembly loading for the DMS-916 bootstrap path. It should read static
content from the normalized workspace:

- discovery/specification JSON from manifest paths,
- XSD file lists from each project's `xsdDirectory`,
- XSD file streams through `File.OpenRead` on validated manifest-relative paths.

The runtime should not generate DLLs from files. Generating DLLs would require runtime compilation, resource
name synthesis, cache invalidation, write permissions, cleanup, and additional trust boundaries. That keeps
the old assembly-resource shape alive and adds complexity in the wrong layer.

## Published NuGet Shape

ApiSchema NuGet packages should ship loose files under a documented package path. One acceptable package
shape is:

```text
contentFiles/any/any/ApiSchema/
  package-manifest.json
  ApiSchema.json
  discovery-spec.json
  xsd/
    Ed-Fi-Core.xsd
    Interchange-Student.xsd
```

Extension packages use the same shape. The schema file should be named `ApiSchema.json` in the package
contract even if MetaEd originally emitted `ApiSchema-EXTENSION.json`; the package manifest and schema
content identify whether the project is core or extension.

The package should contain no `lib/` or `ref/` entries. It may include docs and license files:

```text
docs/
  README.md
  LICENSE
```

Example package manifest:

```json
{
  "version": 1,
  "packageId": "EdFi.Sample.ApiSchema",
  "projectName": "Sample",
  "projectEndpointName": "sample",
  "isExtensionProject": true,
  "schemaPath": "ApiSchema.json",
  "discoverySpecPath": null,
  "xsdDirectory": "xsd"
}
```

Bootstrap opens or extracts the `.nupkg` itself. DMS runtime never reads from the `.nupkg` or NuGet cache
directly. Bootstrap should not depend on `PackageReference` content-file copy behavior.

## MetaEd Packaging Replacement

Replace the current DLL packaging workflow with an asset-package workflow:

1. Keep the existing MetaEd generation job and artifact upload. `MetaEdOutput` remains the source of truth.
2. Replace `MoveMetaEdSchema` with an asset staging command that creates an isolated package staging directory
   for each matrix entry.
3. For core:
   - copy `MetaEdOutput/EdFi/ApiSchema/ApiSchema.json` to `contentFiles/any/any/ApiSchema/ApiSchema.json`,
   - copy `MetaEdOutput/EdFi/XSD/*` and `MetaEdOutput/EdFi/Interchange/*` to
     `contentFiles/any/any/ApiSchema/xsd/`,
   - include static generated or curated content such as `discovery-spec.json` when available.
4. For extensions:
   - copy `MetaEdOutput/<Extension>/ApiSchema/ApiSchema-EXTENSION.json` to
     `contentFiles/any/any/ApiSchema/ApiSchema.json`,
   - copy extension XSD/interchange files to `contentFiles/any/any/ApiSchema/xsd/` using the same file names
     DMS should serve after normalization,
   - omit optional assets that the extension does not provide.
5. Generate `contentFiles/any/any/ApiSchema/package-manifest.json` from the matrix entry and the staged files.
6. Validate the staged package directory:
   - exactly one schema JSON file at the contract path,
   - schema JSON is parseable,
   - package manifest paths exist,
   - no duplicate relative paths,
   - no `*.dll`, `*.cs`, `bin/`, `obj/`, `lib/`, or `ref/` payload entries.
7. Pack the staged directory into a `.nupkg` with package metadata equivalent to today's packages.
8. Publish that `.nupkg` to the same Azure Artifacts feed.

This removes the following workflow concerns from the target design:

- `Marker.cs` namespace mutation,
- ApiSchema `.csproj` files used only for `EmbeddedResource`,
- `dotnet build` for ApiSchema asset packages,
- `dotnet pack` producing `lib/<tfm>/*.ApiSchema.dll`,
- runtime assembly loading to recover static assets.

The packaging implementation can use a `.nuspec`-based pack step or an SDK pack project with
`IncludeBuildOutput=false`, but the produced `.nupkg` must be asset-only. The package artifact, not the
packaging tool, is the contract.

## Implementation Stories

[`tickets/00-schema-and-security-selection.md`](tickets/00-schema-and-security-selection.md) should explicitly
include the direct filesystem asset-container staging contract:

- stage schema files and schema-adjacent content into the normalized workspace,
- keep direct filesystem ApiSchema loading as the stable core input contract,
- detect collisions after normalization,
- write the manifest,
- point Docker-hosted and IDE-hosted DMS at the normalized workspace.

[`tickets/04-apischema-runtime-content-loading.md`](tickets/04-apischema-runtime-content-loading.md) should
update `ContentProvider` to read the normalized workspace and ApiSchema asset manifest instead of requiring
`*.ApiSchema.dll` assemblies. This story depends on the filesystem workspace and asset manifest contract, not
on published asset-only packages. It can proceed in parallel with MetaEd package replacement. Staging DLLs as a
runtime bridge is not part of this design.

[`tickets/05-metaed-apischema-asset-packaging.md`](tickets/05-metaed-apischema-asset-packaging.md) should own
the MetaEd packaging replacement required to publish asset-only ApiSchema NuGet packages. DMS bootstrap
consumes those packages in Story 06 by materializing them into the same filesystem
workspace. Direct filesystem loading does not wait for those packages and remains a supported contract after
package publication.

## Out of Scope

- Generating `*.ApiSchema.dll` files from loose assets.
- Publishing ApiSchema packages that contain `lib/` or `ref/` assemblies.
- Runtime or bootstrap extraction from assembly manifest resources as the target contract.
- Treating package assembly resource names as the long-term runtime contract.
- Defining a generalized plugin system.
- Replacing NuGet as a distribution mechanism.
- Requiring every custom `-ApiSchemaPath` input to include optional discovery or XSD content.
