---
jira: TBD
jira_url: TBD
---

# Story: Common-type extensions (`_ext` attachment to commons)

## Description

Support Ed-Fi “common extensions” where an extension project adds fields under `_ext.{project}` within a common type that is part of a core resource shape (e.g., `$.addresses[*]._ext.sample`).

Problem: core `resourceSchema.jsonSchemaForInsert` is fully expanded and currently has no ApiSchema construct to indicate “this common type is extended here, so attach an `_ext` object.” Today, this can be represented indirectly via OpenAPI fragments `openApiFragments.resources.exts` (schema patches). Example: `sample-api-schema-authoritative.json` shows an `exts.EdFi_Contact` fragment that swaps `addresses` to reference `Sample_Contact_Address` which contains an `_ext.sample` block (lines 4357–4493).

This missing construct prevents the relational mapper from discovering `_ext` sites inside common types by walking `jsonSchemaForInsert`, which in turn blocks extension-table derivation for those sites (see `reference/design/backend-redesign/design-docs/extensions.md`).

Decision: the solution is a new, explicit ApiSchema element that declares (1) the insertion locations in core `jsonSchemaForInsert` and (2) the extension-schema fragments to insert there, eliminating the need for emitting further `_ext` OpenAPI `exts` fragments.

## Integration (ordered passes)

- Set-level (pre-pass): apply common-type extension attachments during effective-schema composition (before DMS-929 base traversal and DMS-932 `_ext` mapping), so schema traversal sees `_ext` at the correct JSON paths.
- Per-resource: base traversal and `_ext` mapping consume the augmented `jsonSchemaForInsert` and produce scope-aligned extension tables per `reference/design/backend-redesign/design-docs/extensions.md`.

## Acceptance Criteria

- For a fixture where an extension project extends a common type used by a core resource:
  - the effective insert schema includes `_ext.{project}` at the expected common-type site(s),
  - `_ext` site detection and extension-table derivation produce the expected scope-aligned extension tables,
  - schema traversal remains deterministic and stable across runs.
- OpenAPI fragments do not need to carry `_ext` common-extension patches (`openApiFragments.resources.exts`) once the new ApiSchema element is present (zero more `_ext` fragments).
- Conflicting or ambiguous attachments fail fast with actionable diagnostics.

## Tasks

1. Define the new ApiSchema element for common extensions:
   - insertion locations (JSONPath) into core `jsonSchemaForInsert`,
   - extension project key (resolvable to `ProjectEndpointName`),
   - the schema fragment to be inserted under `_ext.{project}` (without requiring OpenAPI `exts` patches).
2. Implement effective-schema composition/augmentation so core `jsonSchemaForInsert` reflects those `_ext` attachment points.
3. Ensure `documentPathsMapping`/descriptor/reference binding and any “descriptor path must be encountered” enforcement behave correctly when `_ext` is attached inside commons.
4. Add unit tests covering at least:
   1. an extension on a common type in a core collection (`$.addresses[*]._ext.{project}`),
   2. nested-collection common extensions,
   3. conflict/fail-fast scenarios.
