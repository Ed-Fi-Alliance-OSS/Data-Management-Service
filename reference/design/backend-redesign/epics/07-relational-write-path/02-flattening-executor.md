---
jira: DMS-983
jira_url: https://edfi.atlassian.net/browse/DMS-983
---

# Story: Flatten Validated Write Bodies into Relational Row Buffers and Collection Candidates

## Description

Implement the write-time flattener described in `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` as a backend-mechanics thin slice:

- Applies to non-descriptor resources; descriptor resources are handled by a dedicated `dms.Descriptor` write path.
- Consume a caller-supplied validated request body through a backend-local flattening input contract.
- For this story, the caller supplies the normal validated request body already present on the write request.
- Profile-applied request-body selection is intentionally out of scope here and is deferred to follow-on story `DMS-1123` / `reference/design/backend-redesign/epics/07-relational-write-path/02b-profile-applied-request-flattening.md`.
- Traverse the supplied JSON once, tracking request sibling order for collections.
- Emit row buffers for:
  - the resource root table,
  - non-collection child/extension tables, and
  - logical collection/common-type candidates for collection tables (including nested collections),
  - extension tables derived from `_ext` sites.
- Populate scalar columns from JSON values and FK columns from resolved `DocumentId`/`DescriptorId` values.
- Compute deterministic semantic collection identities for persisted multi-item collection scopes without evaluating profile rules in backend.
- Avoid runtime JSONPath parsing in hot loops by using compiled path expressions.

This re-scope intentionally separates flattener mechanics from the Core-owned profile hand-off so backend can land traversal, row-buffer emission, and reference binding before `DMS-1106`. The flattener contract produced here must be reusable by the follow-on profile integration story without redesigning traversal internals.

## Acceptance Criteria

- Flattening produces deterministic row buffers and collection candidates that preserve request sibling order.
- The flattener accepts a caller-supplied JSON body through a backend-local input contract and does not depend directly on `ProfileAppliedWriteRequest` types in this story.
- For this story's runtime path, the caller supplies the normal validated request body; profile-applied request-body selection remains out of scope and is deferred to `02b-profile-applied-request-flattening.md`.
- References inside nested collections are written to the correct child rows (using core-emitted concrete paths + ordinal path mapping).
- `_ext` rows are emitted only when extension values exist for the scope.
- Collection candidates carry the scope/request-order information needed for later binding to existing or newly reserved `CollectionItemId` values, including root-level and collection-aligned extension child collections.
- No general-purpose JSONPath engine is invoked per value in the hot loop.
- Unit tests cover:
  - nested collections,
  - references at multiple depths,
  - `_ext` at root, a root-level extension child collection, `_ext` within a collection, and a collection-aligned extension child collection.
- Tests prove the flattener treats the supplied body as authoritative input and does not inspect profile metadata or recover values from any original-request copy outside the supplied body.

## Tasks

1. Define the backend-local flattening input/output contract so callers supply the body to flatten without exposing Core profile contract types inside the flattener.
2. Implement a JSON walker that tracks scope keys and request sibling order while traversing.
3. Implement scalar value reading and type conversion consistent with the derived type mapping.
4. Implement ordinal-path mapping from extracted reference paths to the owning scope row buffer.
5. Emit deterministic collection/common-type candidate records, including semantic identity inputs where required for later merge binding.
6. Implement `_ext` handling aligned to the extension mapping rules.
7. Add unit tests comparing produced row buffers/candidates to expected shapes for small fixtures, including the “supplied body is authoritative” seam.
