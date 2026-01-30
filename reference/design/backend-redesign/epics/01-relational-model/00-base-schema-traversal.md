---
jira: DMS-929
jira_url: https://edfi.atlassian.net/browse/DMS-929
---

# Story: Derive Base Tables/Columns + Bind Descriptors from JSON Schema

## Description

Implement the base JSON-schema traversal that derives relational tables/columns and binds descriptor paths from
`resourceSchema.jsonSchemaForInsert`, per `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Arrays create child tables keyed by parent key parts + `Ordinal`.
- Objects inline (except `_ext`) by turning scalar descendants into prefixed columns.
- Scalars become typed columns (with nullability/required-ness derived from JSON Schema).
- Descriptor value paths are bound to `*_DescriptorId` FK columns (and descriptor edge metadata) using the
  precomputed descriptor-path map.
- `additionalProperties` is treated as “prune/ignore” (closed-world persistence).
- Traversal is deterministic and does not depend on dictionary iteration order.

Note: `jsonSchemaForInsert` is fully dereferenced and expanded. `$ref`, schema unions (`oneOf`/`anyOf`/`allOf`),
and `enum` cannot occur. If any appear, treat them as invalid schema input.

This story produces the base per-resource table/column shape used to populate `DerivedRelationalModelSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`).

## Integration (ordered passes)

- Per-resource: implemented as pipeline steps that derive tables/keys, scalar columns, and descriptor bindings from a
  single `resourceSchema.jsonSchemaForInsert`.
- Set-level (`DMS-1033`): runs this per-resource pipeline for **every** concrete resource across the effective schema
  set as the initial “base traversal + descriptor binding” pass. Later passes enrich these per-resource models
  (references, extensions, naming, indexes/triggers, etc.).

## Acceptance Criteria

- For a small fixture resource schema, derived tables match expected scopes:
  - root table for `$`,
  - child table per array path (including nested arrays),
  - correct composite keys (`{Root}_DocumentId`, ancestor ordinals, `Ordinal`).
- Scalar columns are derived deterministically with correct nullability and type metadata inputs.
- Descriptor paths are bound deterministically to `*_DescriptorId` columns with descriptor edge metadata.
- No columns/tables are created for unknown/dynamic `additionalProperties`.
- Traversal is deterministic and does not depend on dictionary iteration order.

## Tasks

1. Implement a deterministic schema walker that produces:
   - table scopes (root + arrays),
   - scalar column definitions within each scope,
   - descriptor bindings for descriptor value paths.
2. Encode key/ordinal rules for child table primary keys and parent FKs.
3. Add unit tests for:
   1. nested collections,
   2. scalar-inlined objects,
   3. `additionalProperties` behavior,
   4. descriptor path binding to `*_DescriptorId` columns.
4. Wire this derivation into the `DMS-1033` set-level builder as the initial “base traversal + descriptor binding”
   pass over all resources.
