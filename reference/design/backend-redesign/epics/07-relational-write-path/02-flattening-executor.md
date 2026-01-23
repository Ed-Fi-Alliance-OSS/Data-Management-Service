---
jira: DMS-983
jira_url: https://edfi.atlassian.net/browse/DMS-983
---

# Story: Flatten JSON into Relational Row Buffers (Root + Children + `_ext`)

## Description

Implement the write-time flattener described in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Applies to non-descriptor resources; descriptor resources are handled by a dedicated `dms.Descriptor` write path.
- Traverse the validated JSON once, tracking collection ordinals.
- Emit row buffers for:
  - the resource root table,
  - child tables for collections (including nested collections),
  - extension tables derived from `_ext` sites.
- Populate scalar columns from JSON values and FK columns from resolved `DocumentId`/`DescriptorId` values.
- Avoid runtime JSONPath parsing in hot loops by using compiled path expressions.

## Acceptance Criteria

- Flattening produces deterministic row buffers that preserve array order via `Ordinal`.
- References inside nested collections are written to the correct child rows (using core-emitted concrete paths + ordinal path mapping).
- `_ext` rows are emitted only when extension values exist for the scope.
- No general-purpose JSONPath engine is invoked per value in the hot loop.
- Unit tests cover:
  - nested collections,
  - references at multiple depths,
  - `_ext` at root and within a collection.

## Tasks

1. Implement a JSON walker that tracks scope keys and ordinals while traversing.
2. Implement scalar value reading and type conversion consistent with the derived type mapping.
3. Implement ordinal-path mapping from extracted reference paths to the owning scope row buffer.
4. Implement `_ext` handling aligned to the extension mapping rules.
5. Add unit tests comparing produced row buffers to expected shapes for small fixtures.
