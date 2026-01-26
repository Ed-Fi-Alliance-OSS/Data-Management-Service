---
jira: DMS-929
jira_url: https://edfi.atlassian.net/browse/DMS-929
---

# Story: Derive Base Tables/Columns from JSON Schema

## Description

Implement the base JSON-schema traversal that derives relational tables and scalar columns from `resourceSchema.jsonSchemaForInsert`, per `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Arrays create child tables keyed by parent key parts + `Ordinal`.
- Objects inline (except `_ext`) by turning scalar descendants into prefixed columns.
- Scalars become typed columns (with nullability/required-ness derived from JSON Schema).
- `additionalProperties` is treated as “prune/ignore” (closed-world persistence).
- Traversal is deterministic and does not depend on dictionary iteration order.

Note: `jsonSchemaForInsert` is fully dereferenced and expanded. `$ref`, schema unions (`oneOf`/`anyOf`/`allOf`),
and `enum` cannot occur. If any appear, treat them as invalid schema input.

This story produces the base per-resource table/column shape used to populate `DerivedRelationalModelSet` (see `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`).

## Acceptance Criteria

- For a small fixture resource schema, derived tables match expected scopes:
  - root table for `$`,
  - child table per array path (including nested arrays),
  - correct composite keys (`{Root}_DocumentId`, ancestor ordinals, `Ordinal`).
- Scalar columns are derived deterministically with correct nullability and type metadata inputs.
- No columns/tables are created for unknown/dynamic `additionalProperties`.
- Traversal is deterministic and does not depend on dictionary iteration order.

## Tasks

1. Implement a deterministic schema walker that produces:
   - table scopes (root + arrays),
   - scalar column definitions within each scope.
2. Encode key/ordinal rules for child table primary keys and parent FKs.
3. Add unit tests for:
   1. nested collections,
   2. scalar-inlined objects,
   3. `additionalProperties` behavior.
