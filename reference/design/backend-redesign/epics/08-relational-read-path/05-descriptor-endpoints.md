---
jira: DMS-994
jira_url: https://edfi.atlassian.net/browse/DMS-994
---

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
  - computes `_etag` from the serialized descriptor JSON representation and serves `_lastModifiedDate/ChangeVersion` from stored `dms.Document` stamps.
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

## Clarifying Questions and Answers

### Questions 1

1. Should descriptor GET emit only the public API descriptor fields, excluding Uri and Discriminator, even though the story
    says “from dms.Descriptor columns”? My default would be: emit namespace, codeValue, shortDescription, description,
    effectiveBeginDate, effectiveEndDate, plus API metadata.

2. Should DMS-994 include ChangeVersion in descriptor GET/query responses now? Existing relational GET materialization
    currently omits it, but this story explicitly mentions it.

3. For type discrimination, is dms.Document.ResourceKeyId authoritative, with dms.Descriptor.Discriminator diagnostic-only?
    Also, if a dms.Document row exists for the right resource but the dms.Descriptor row is missing, should that be 404 or a 500
    invariant failure?

4. Should descriptor query support be driven by ApiSchema.queryFieldMapping, or by a hardcoded canonical descriptor-field map
    from DescriptorMetadata.ColumnContract? This matters because current query capability compilation intentionally omits Share
    dDescriptorTable resources.

5. Should descriptor query support id filtering and totalCount=true, matching DMS-993 behavior for normal resources?

6. Should descriptor query use the same exact-match-only semantics as DMS-993, including invalid UUID/date values returning an
    empty page rather than 400?

7. Should descriptor string filters be case-sensitive/ordinal like the current relational query compiler, or should descriptor
    endpoints preserve any legacy case-insensitive behavior?

8. What interim authorization rule do you want for descriptor GET/query? My concern is descriptor resources may be namespace-
    secured; enabling descriptor GET-many without SQL-layer auth could fail open unless we keep the same 501 guard for non-no-op
    auth.

9. Should readable profile projection apply to descriptor resources if a profile context exists, or should descriptor endpoints
    always return the full public descriptor shape?

10. For integration tests, should DMS-994 depend on the descriptor write handler by creating descriptors through POST first, or
    should tests seed dms.Document/dms.Descriptor directly to isolate read behavior?

11. Architecturally, do you prefer a dedicated descriptor read handler parallel to IDescriptorWriteHandler, or should this live
    inside RelationalDocumentStoreRepository as a descriptor branch?

12. Should descriptor query remain outside RelationalQueryCapability as a separate endpoint path, or should DMS-994 change
    query capability metadata so descriptor resources become “supported” with descriptor-specific targets?

### Answers 1

1. Emit only public descriptor fields: namespace, codeValue, shortDescription, description, effectiveBeginDate, effectiveEndDate, plus API metadata. Do not emit internal Uri or Discriminator.

2. Explicitly defer ChangeVersion from descriptor GET/query responses.

3. Treat dms.Document.ResourceKeyId as authoritative. dms.Descriptor.Discriminator is diagnostic. Missing/wrong Document is 404; matching Document with no Descriptor row is a 500 invariant failure.

4. Drive descriptor field mapping from DescriptorMetadata.ColumnContract, with an optional startup sanity check against ApiSchema.queryFieldMapping. The data model already defines descriptor
     query fields, and current generic query capability intentionally omits SharedDescriptorTable resources.

5. Yes, support ?id= and totalCount=true, matching DMS-993.

6. Yes, use DMS-993 exact-match semantics. Invalid UUID/date values should short-circuit to an empty page, not 400.

7. Use case-sensitive/ordinal value matching. Query parameter names can remain case-insensitive, but descriptor string values should behave like the current relational query compiler, not legacy
    case-insensitive value matching.

8. Fail closed for interim auth. Allow only no strategies or NoFurtherAuthorizationRequired; otherwise return 501/not implemented for descriptor GET/query until SQL-layer auth lands. Descriptor
    namespace auth can later be implemented directly against dms.Descriptor.Namespace.

9. Apply readable profile projection when Core supplies a profile context. Preserve id, _etag, _lastModifiedDate, and ChangeVersion, and recompute _etag after projection.

10. Use both test styles: POST-created descriptors for end-to-end acceptance and direct dms.Document/dms.Descriptor seeding for focused read-path, paging, and invariant-failure cases.

11. Add a dedicated descriptor read handler, parallel to IDescriptorWriteHandler, and have RelationalDocumentStoreRepository delegate from its existing descriptor branches. That keeps SQL/
    reconstitution out of the repository.

12. Keep descriptor query outside the existing RelationalQueryCapability root-table path. Add descriptor-specific query capability/plan metadata instead. The current compiler intentionally omits
    SharedDescriptorTable resources, so DMS-994 should branch before GetQueryCapabilityOrThrow for descriptor resources.

Main implementation shape: IDescriptorReadHandler owns GET by id, page selection, total count, and descriptor JSON materialization from dms.Document + dms.Descriptor; the repository just detects
descriptor resources and delegates.
