---
jira: DMS-983
jira_url: https://edfi.atlassian.net/browse/DMS-983
---

# Story: Flatten `WritableRequestBody` into Relational Row Buffers and Collection Candidates

## Description

Implement the write-time flattener described in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`:

- Applies to non-descriptor resources; descriptor resources are handled by a dedicated `dms.Descriptor` write path.
- Consume `ProfileAppliedWriteRequest.WritableRequestBody` when Core applied a writable profile; otherwise consume the normal validated request body.
- Traverse the validated JSON once, tracking request sibling order for collections.
- Emit row buffers for:
  - the resource root table,
  - non-collection child/extension tables, and
  - logical collection/common-type candidates for collection tables (including nested collections),
  - extension tables derived from `_ext` sites.
- Populate scalar columns from JSON values and FK columns from resolved `DocumentId`/`DescriptorId` values.
- Compute deterministic semantic collection identities for persisted multi-item collection scopes without evaluating profile rules in backend.
- Avoid runtime JSONPath parsing in hot loops by using compiled path expressions.

## Acceptance Criteria

- Flattening produces deterministic row buffers and collection candidates that preserve request sibling order.
- When a profile-applied request is supplied, the flattener uses `WritableRequestBody` exactly as provided and does not re-evaluate profile filters.
- References inside nested collections are written to the correct child rows (using core-emitted concrete paths + ordinal path mapping).
- `_ext` rows are emitted only when extension values exist for the scope.
- Collection candidates carry the scope/request-order information needed for later binding to existing or newly reserved `CollectionItemId` values.
- No general-purpose JSONPath engine is invoked per value in the hot loop.
- Unit tests cover:
  - nested collections,
  - references at multiple depths,
  - `_ext` at root and within a collection, and
  - profile-applied request bodies flowing through flattening without backend-side filtering.

## Tasks

1. Implement a JSON walker that tracks scope keys and request sibling order while traversing.
2. Implement scalar value reading and type conversion consistent with the derived type mapping.
3. Implement ordinal-path mapping from extracted reference paths to the owning scope row buffer.
4. Emit deterministic collection/common-type candidate records, including semantic identity inputs where required for later merge binding.
5. Implement `_ext` handling aligned to the extension mapping rules.
6. Add unit tests comparing produced row buffers/candidates to expected shapes for small fixtures.
