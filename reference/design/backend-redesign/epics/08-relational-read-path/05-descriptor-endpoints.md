# Story: Serve Descriptor GET/Query Endpoints from `dms.Descriptor` (No Per-Descriptor Tables)

## Description

Descriptor resources are stored in `dms.Descriptor` (keyed by `dms.Document.DocumentId`) and must be served like normal resources:

- GET by id returns the descriptor resource representation.
- Query endpoints support filtering/paging on descriptor fields.

This story covers serving descriptor resources themselves (distinct from descriptor URI projection for *other* resources, which is covered by `03-descriptor-projection.md`).

## Acceptance Criteria

- GET by id for a descriptor resource:
  - resolves `DocumentUuid → DocumentId`,
  - verifies the document is of the expected descriptor resource type,
  - returns JSON reconstituted from `dms.Descriptor` columns plus `id` from `dms.Document.DocumentUuid`,
  - serves `_etag/_lastModifiedDate/ChangeVersion` from stored `dms.Document` stamps.
- Query for a descriptor resource:
  - compiles filters for descriptor fields to `dms.Descriptor` columns (root-only semantics),
  - pages deterministically using `DocumentId` ordering,
  - returns items reconstituted from `dms.Descriptor` for the page keyset.
- Implementation does not require per-descriptor tables.
- Integration tests cover:
  - GET by id,
  - query filtering on at least `namespace`, `codeValue`, `effectiveBeginDate`, and `effectiveEndDate`,
  - paging across multiple pages.

## Tasks

1. Implement descriptor GET-by-id read plan: `dms.Document` + `dms.Descriptor` by `DocumentId`.
2. Implement descriptor query plan and field mapping (descriptor columns only), including resource-type discrimination.
3. Integrate descriptor reconstitution into the read pipeline (do not route descriptor resources through project-schema hydration).
4. Add integration tests for descriptor GET/query behavior.
