# Backend Redesign: Extensions (`_ext`) — Relational Mapping (Tables per Resource)

## Status

Draft.

This document is the extensions deep dive for `overview.md`.

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)
- Strengths and risks: [strengths-risks.md](strengths-risks.md)

## Table of Contents

- [Scope](#scope)
- [Goals & Constraints](#goals--constraints)
- [Table naming patterns](#table-naming-patterns-borrowed-from-the-old-flattening-design)
- [Detecting extensions in ApiSchema](#detecting-extensions-in-apischema)
- [Relational mapping rules](#relational-mapping-rules)
- [Flattening integration](#flattening-postput-integration)
- [Reconstitution integration](#reconstitution-getquery-integration)
- [Example](#example-contact--sample-extension-resource--common-type)
- [Schema validation notes](#schema-validation-notes)
- [Open questions](#open-questions--decisions-to-confirm)

---

## Scope

Defines how Ed-Fi-style extensions (`_ext`) are represented in the relational primary store:

- resource extensions (`isResourceExtension: true`)
- extension fields under `_ext` at the resource root
- extension fields under `_ext` inside common types, including within collections and nested collections
- multiple extension projects (e.g., Sample + TPDM) simultaneously

Authorization is intentionally out of scope for this redesign phase.

## Goals & Constraints

- **Schema-driven, no codegen**: derive extension tables/columns from effective `ApiSchema.json` (`jsonSchemaForInsert` + `documentPathsMapping`) and compile plans at startup.
- **Low coupling to document shape**: treat `_ext` as a generic “project-scoped subtree” discovered via JSON schema traversal (no hard-coded paths).
- **Cross-engine**: PostgreSQL + SQL Server parity.
- **No core-table widening**: avoid merging extension columns into core resource tables; keep extension projects’ data in their own table hierarchies.

## Table naming patterns (borrowed from the old flattening design)

Table naming patterns come from `reference/design/flattening-metadata-design.md`:
- extension root tables are named `{ResourceName}Extension`
- extension collection tables are named `{ResourceName}Extension{CollectionSuffix}` using PascalCase

This redesign keeps those table-name patterns, while using the redesigned key strategy (`DocumentId` and composite parent+ordinal keys).

### Physical database schemas

Each project (core and extension) has its own physical DB schema derived from `ProjectEndpointName` (the URL segment):

- core: `ed-fi` → `edfi`
- extension example: `sample` → `sample`, `tpdm` → `tpdm`

Extension tables for project `P` live in schema `P` (not in the core schema).

## Detecting extensions in ApiSchema

DMS already merges extension resource:schemas into the effective schema at runtime (see `ProvideApiSchemaMiddleware`). This design assumes the relational mapper consumes that merged/effective schema.

### `_ext` project key resolution

When the mapper finds a JSON schema object property named `_ext`, its child property names are treated as *extension-project keys*.

Resolve an `_ext` key `k` to a `ProjectEndpointName` as follows:

1. If `k` matches a configured `projectSchema.projectEndpointName` (case-insensitive), it is that `ProjectEndpointName`.
2. Else if `k` matches a configured `projectSchema.projectName` (case-insensitive), map it to that project’s `ProjectEndpointName` (defensive fallback).
3. Else fail fast (schema compilation/startup validation): unknown extension key.

This supports either `ProjectEndpointName` tokens (recommended) or MetaEd project name tokens inside `_ext`.

### Where `_ext` can appear

`_ext` can appear:

- at the resource root: `$._ext.{project}`
- inside common types: `$.addresses[*]._ext.{project}`, `$.addresses[*].periods[*]._ext.{project}`, etc.

The mapper discovers these sites by walking `jsonSchemaForInsert` and finding `_ext` at any depth.

## Relational mapping rules

### 1) Resource-level `_ext` → extension root table (1:1 per project)

For a base resource `R` and an extension project endpoint name `p` where `$._ext.{p}` exists:

- Create `{pSchema}.{R}Extension`
- Primary key: `DocumentId` (same surrogate key as the base resource row)
- FK: `DocumentId` → `{baseSchema}.{R}(DocumentId)` ON DELETE CASCADE
- Columns: scalar columns derived from the JSON schema under `$._ext.{p}`, plus FK columns for references/descriptors (using the same rules as core mapping)

**Row presence rule**
- If the document has no values under `$._ext.{p}` (absent or empty after Core pruning), do not store a row in `{R}Extension`.

### 2) `_ext` inside common types and collections → extension scope tables aligned to base keys

When an `_ext.{p}` subtree appears inside a base table scope (root or a collection element), store it in a table keyed exactly like the base scope it extends.

Example: core has `edfi.SchoolAddress` keyed by `(School_DocumentId, Ordinal)`. If `_ext.sample` exists under `$.addresses[*]`, create:

- `sample.SchoolExtensionAddress`
  - key columns: `(School_DocumentId, Ordinal)`
  - FK back to `edfi.SchoolAddress(School_DocumentId, Ordinal)` ON DELETE CASCADE
  - extension scalar/ref/descriptor columns from the schema under that `_ext.sample` site

This “key alignment” rule ensures:
- no orphan extension rows,
- correct delete cascades,
- deterministic reconstitution (extension rows attach to the correct base element by key/ordinal).

### 3) Arrays under `_ext` → extension child tables (parent+ordinal keys)

Arrays inside an extension subtree create extension child tables using the same parent+ordinal strategy as core collections.

Naming follows the old pattern:
- `{R}Extension{Suffix}` (or nested suffix) using PascalCase base names derived from the array property path.

### 4) References and descriptors inside extensions

Extension fields may include:
- document references (reference objects), and
- descriptor references (URI strings)

The mapping for references/descriptors inside `_ext` is identical to core:

- document references become `..._DocumentId` FK columns (resolved via `dms.ReferentialIdentity`)
- descriptor references become `..._DescriptorId` FK columns to `dms.Descriptor` (resolved via `dms.ReferentialIdentity`, validated via `dms.Descriptor`)

`documentPathsMapping` remains the authoritative source for “this is a reference/descriptor” and for identity mapping.

## Flattening (POST/PUT) integration

During write materialization, extension row buffers are produced alongside core row buffers:

1. Traverse the JSON once (as in the core flattener).
2. Whenever an `_ext.{p}` subtree is encountered at a table scope, materialize the corresponding extension row for that scope.
3. Apply the same “replace” strategy as the core baseline:
   - delete existing extension rows for the document (root extension tables + extension child tables) and insert the current rows.

This keeps semantics aligned with “replace document” and avoids requiring stable per-element IDs.

## Reconstitution (GET/query) integration

Reconstitution assembles the base JSON as usual, then overlays extensions:

1. Hydrate core root + child tables.
2. For each extension project schema `p` present in the effective schema, hydrate its extension tables for the page keyset.
3. During JSON writing:
   - emit `_ext` at the root (and/or within elements) only when there is at least one extension value to output
   - write values under `_ext.{p}` exactly as the schema defines (no flattening into the core object)

## Example: Contact + Sample extension (resource + common-type)

Assume:
- base project endpoint name: `ed-fi` → schema `edfi`
- extension project endpoint name: `sample` → schema `sample`
- base resource: `Contact`
- base collection: `addresses[*]`

Core tables:
- `edfi.Contact`
- `edfi.ContactAddress`

Sample extension tables:
- `sample.ContactExtension` (resource-level extension fields under `$._ext.sample`)
- `sample.ContactExtensionAddress` (extension fields under `$.addresses[*]._ext.sample`)
