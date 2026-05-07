---
jira: DMS-1154
jira_url: https://edfi.atlassian.net/browse/DMS-1154
---

# Story: Replace DMS ApiSchema DLL Resource Loading

## Description

Replace DMS runtime loading of ApiSchema DLL resources for the DMS-916 bootstrap path with loading from the
normalized file-based ApiSchema asset workspace staged by Story 00. The current `ContentProvider`
implementation searches `AppSettings:ApiSchemaPath` for `*.ApiSchema.dll`, loads assemblies, and serves
metadata/specification JSON and XSD files from embedded manifest resources. That keeps DMS runtime coupled to
DLL-backed ApiSchema packages even when bootstrap stages concrete JSON files.

This story removes that bootstrap-path coupling without making DMS runtime aware of NuGet packages. When DMS
is configured with
`AppSettings:UseApiSchemaPath=true` and `AppSettings:ApiSchemaPath` pointing at the staged workspace, runtime
metadata/specification and XSD endpoints read manifest-relative files from that workspace. Runtime does not
generate DLLs from loose files and does not extract content from assembly resources for the DMS-916 bootstrap
path.

This story depends on the normalized filesystem workspace and ApiSchema asset manifest contract, not on
asset-only package publication. It can be implemented in parallel with the MetaEd package switch-over because
package-backed inputs in Story 06 and direct `-ApiSchemaPath` inputs from Story 00 both
materialize to the same workspace before runtime starts.

## Acceptance Criteria

- With `AppSettings:UseApiSchemaPath=true`, DMS reads schema-adjacent static content from the normalized
  ApiSchema workspace produced by Story 00:
  - discovery/specification JSON from manifest paths,
  - XSD file lists from each selected project's manifest `xsdDirectory`,
  - XSD file streams through validated manifest-relative file paths.
- `ContentProvider` no longer requires `*.ApiSchema.dll` files for the DMS-916 bootstrap path.
- Runtime does not generate `*.ApiSchema.dll` files from loose assets.
- Manifest-relative file paths are validated so content loading cannot escape the configured ApiSchema
  workspace.
- Missing optional content, such as an extension with no discovery/specification JSON or no XSD files, follows
  the same endpoint behavior as a selected package that does not provide those optional assets. Missing schema
  JSON remains a Story 00 staging failure, not a runtime fallback.
- Docker-hosted DMS and IDE-hosted DMS use the same staged workspace contract:
  - Docker: `AppSettings:UseApiSchemaPath=true`, `AppSettings:ApiSchemaPath=/app/ApiSchema`,
  - IDE: `AppSettings:UseApiSchemaPath=true`,
    `AppSettings:ApiSchemaPath=<repo-root>/eng/docker-compose/.bootstrap/ApiSchema`.
- Existing non-bootstrap behavior may keep a compatibility path for assembly-bundled content when
  `UseApiSchemaPath=false`, but that path is not the DMS-916 bootstrap contract.
- Unit or integration coverage proves that metadata/specification JSON and XSD endpoints work from a
  file-based staged workspace with no `*.ApiSchema.dll` files present.

## Tasks

1. Add a small manifest reader for `bootstrap-api-schema-manifest.json`, or extend an existing ApiSchema
   workspace reader, using the shape defined in `../../design-docs/bootstrap/apischema-container.md`.
2. Update `ContentProvider` so the `UseApiSchemaPath=true` path reads JSON and XSD content from
   manifest-relative file paths under the configured workspace.
3. Preserve current endpoint behavior for missing optional static content without falling back to stale
   assembly-packaged assets.
4. Validate all manifest-relative paths before opening files.
5. Add tests covering:
   - metadata/specification JSON loading from files,
   - XSD file listing from manifest `xsdDirectory` entries,
   - XSD stream loading from files,
   - no `*.ApiSchema.dll` required in the staged workspace,
   - rejection of manifest paths that escape the workspace.
6. Keep runtime content loading separate from bootstrap package resolution, schema selection, claims staging,
   and DDL provisioning.

## Out of Scope

- Publishing or changing ApiSchema NuGet packages; that belongs to Story 05.
- Resolving packages; that belongs to Story 06.
- Creating the normalized workspace from direct filesystem inputs; that belongs to Story 00.
- Loading `.nupkg` files, reading the NuGet cache, or interpreting NuGet package layout at runtime.
- Generating `*.ApiSchema.dll` files from loose assets.
- Deprecating direct filesystem ApiSchema loading.
- Replacing NuGet as a distribution mechanism.
- Defining new metadata or XSD endpoint routes beyond the existing DMS behavior.

## Design References

- [`../../design-docs/bootstrap/apischema-container.md`](../../design-docs/bootstrap/apischema-container.md)
- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 3, 12, 13, and 14
- [`00-schema-and-security-selection.md`](00-schema-and-security-selection.md)
