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
  - computes `_etag` from the serialized descriptor JSON representation and serves `_lastModifiedDate` from stored `dms.Document` stamps.
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

2. Question removed.

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

2. Question removed.

3. Treat dms.Document.ResourceKeyId as authoritative. dms.Descriptor.Discriminator is diagnostic. Missing/wrong Document is 404; matching Document with no Descriptor row is a 500 invariant failure.

4. Drive descriptor field mapping from DescriptorMetadata.ColumnContract, with an optional startup sanity check against ApiSchema.queryFieldMapping. The data model already defines descriptor
     query fields, and current generic query capability intentionally omits SharedDescriptorTable resources.

5. Yes, support ?id= and totalCount=true, matching DMS-993.

6. Yes, use DMS-993 exact-match semantics. Invalid date values are already handled at the core level.

7. Use case-sensitive/ordinal value matching. Query parameter names can remain case-insensitive, but descriptor string values should behave like the current relational query compiler, not legacy
    case-insensitive value matching.

8. Fail closed for interim auth. Allow only no strategies or NoFurtherAuthorizationRequired; otherwise return 501/not implemented for descriptor GET/query until SQL-layer auth lands. Descriptor
    namespace auth can later be implemented directly against dms.Descriptor.Namespace.

9. Apply readable profile projection when Core supplies a profile context. Preserve id, _etag, and _lastModifiedDate, and recompute _etag after projection.

10. Use both test styles: POST-created descriptors for end-to-end acceptance and direct dms.Document/dms.Descriptor seeding for focused read-path, paging, and invariant-failure cases.

11. Add a dedicated descriptor read handler, parallel to IDescriptorWriteHandler, and have RelationalDocumentStoreRepository delegate from its existing descriptor branches. That keeps SQL/
    reconstitution out of the repository.

12. Keep descriptor query outside the existing RelationalQueryCapability root-table path. Add descriptor-specific query capability/plan metadata instead. The current compiler intentionally omits
    SharedDescriptorTable resources, so DMS-994 should branch before GetQueryCapabilityOrThrow for descriptor resources.

Main implementation shape: IDescriptorReadHandler owns GET by id, page selection, total count, and descriptor JSON materialization from dms.Document + dms.Descriptor; the repository just detects
descriptor resources and delegates.

### Questions 2

  1. Should descriptor GET fail-closed auth require adding AuthorizationStrategyEvaluators to IRelationalGetRequest, like query already has, so we can detect “no strategies /
     NoFurtherAuthorizationRequired” without invoking the legacy auth handler?

  2. For descriptor query corruption cases: if dms.Document has a matching descriptor ResourceKeyId but no dms.Descriptor row, should the whole query return a 500 invariant failure, or should page
     selection only join rows that have a descriptor row and leave that invariant to GET-by-id?

  3. Should nullable public descriptor fields be omitted when null, matching existing descriptor responses, or emitted as "description": null, "effectiveBeginDate": null, etc.? My default would be
     omit nulls.

  4. Should DMS-994 support all public descriptor filters from DescriptorColumnContract now: namespace, codeValue, shortDescription, description, effectiveBeginDate, effectiveEndDate, plus id; or
     only the acceptance-test minimum?

  5. Do you want the optional startup sanity check against ApiSchema.queryFieldMapping included in this story, or deferred? If included, should mismatch fail the resource’s descriptor query support
     or fail startup/mapping compilation?

  6. For readable profiles on descriptor endpoints, should namespace and codeValue be preserved as identity fields even if profile rules exclude them? The current generic identity extraction may
     not naturally preserve them because descriptor identity is synthetic.

  7. Existing descriptor E2E scenarios have some legacy behavior around ?namespace=<namespace>#<codeValue>. Should relational descriptor query strictly match the stored Namespace column, even if
     that means updating/skipping legacy expectations?

  8. Should descriptor GET/query integration coverage be required for both PostgreSQL and SQL Server in this story, consistent with DMS-990/DMS-993, or is one provider plus shared unit tests
     acceptable?

  9. For the POST-created acceptance tests, do you want full API/E2E scenarios under UseRelationalBackend, or backend integration tests through RelationalDocumentStoreRepository are enough for DMS-
     994?

  10. Should descriptor responses participate in dms.DocumentCache if/when that runtime cache is enabled, or should this first descriptor read handler always materialize directly from dms.Document
     + dms.Descriptor?

### Answers 2

  1. Yes. Add AuthorizationStrategyEvaluators to IRelationalGetRequest, populate it from GetByIdHandler, and use the same fail-closed rule as query: empty strategies or only
     NoFurtherAuthorizationRequired are allowed; anything else returns 501. Do not invoke the legacy auth handler for relational descriptor GET.

  2. Do not silently inner-join away corruption. Use dms.Document.ResourceKeyId as authoritative. If a selected descriptor document has no dms.Descriptor row, return a 500 invariant failure. I
     would not add a global corruption scan for every query; fail when the corrupt document enters the selected/returned candidate set.

  3. Omit nullable fields when null. This matches the existing descriptor _etag canonical shape in src/dms/backend/EdFi.DataManagementService.Backend/RelationalApiMetadataFormatter.cs:58. Required
     fields like namespace, codeValue, and effectively shortDescription should be present; missing required DB values are corruption.

  4. Support all public descriptor filters now. Implement id, namespace, codeValue, shortDescription, description, effectiveBeginDate, and effectiveEndDate, not just the acceptance-test minimum.
     The data model explicitly names those descriptor query fields in reference/design/backend-redesign/design-docs/data-model.md:344.

  5. Include the sanity check if it is cheap, but make failure resource-scoped. DescriptorMetadata.ColumnContract remains authoritative. If ApiSchema.queryFieldMapping disagrees, omit descriptor
     query support for that resource with a 501/actionable diagnostic rather than failing the whole mapping set.

  6. Yes, always preserve namespace and codeValue under readable profiles. Descriptor identity is synthetic internally, so the normal identity-property extraction may not protect those fields.
     Treat them as descriptor identity fields, along with id, _etag, and _lastModifiedDate; recompute _etag after projection per reference/design/backend-redesign/design-docs/update-
     tracking.md:136. The implementation stance should be: descriptor read handler materializes the public descriptor JSON; profile projection
     remains generic; Core tells the generic projector that descriptor identity includes namespace and codeValue.

  7. Strictly match the stored Namespace column. ?namespace=<namespace>#<codeValue> should not match unless that full value was actually stored in Namespace. Preserve the legacy E2E
     expectations that treat namespace as URI, but create new E2E tests tagged for relational backend that use the correct namespace.

  8. Require PostgreSQL and SQL Server integration coverage. Descriptor query has provider-sensitive UUID/date/paging/count SQL, and DMS-990/DMS-993 set the same bar. Keep the matrix small, but
     cover both providers.

  9. Add a thin API/E2E path tagged for the relational backend if necessary, but the ideal is to simply tag existing E2E tests covering descriptor GET/query as relational backend and have them work with the feature file unmodified. Backend repository integration tests are right for filters,
     paging, direct seeding, and invariant failures, but one POST-created full API scenario is needed to prove Core routing,
     descriptor write/read integration, auth guard, and profile projection are wired.

  10. Materialize directly from dms.Document + dms.Descriptor for DMS-994. Do not wire dms.DocumentCache in this story. The cache
     design is caller-agnostic and optional, but descriptor read support should first be a simple direct read path; cache
     participation can land with the broader cache/projector work described in reference/design/backend-redesign/design-docs/
     data-model.md:519.

### Questions 3

  1. Should descriptor query capability metadata become a first-class compiled mapping-set concept, for example
     DescriptorQueryCapabilitiesByResource, so resource-scoped 501 diagnostics are visible alongside generic query capabilities?
     Or is it acceptable for DMS-994 to build descriptor query plans directly from ConcreteResourceModel.DescriptorMetadata at
     request time?

  2. Should descriptor GET support RelationalGetRequestReadMode.StoredDocument, returning the stored descriptor shape without
     response metadata/profile projection, or is descriptor read support only required for external GET/query endpoints in this
     story?

### Answers 3
  
  1. Make descriptor query capability first-class in the compiled MappingSet, but keeping it separate from the
     existing generic RelationalQueryCapability.

    Concretely: add something like DescriptorQueryCapabilitiesByResource, with supported/omitted entries per descriptor resource.
    Build it during mapping-set compilation from ConcreteResourceModel.DescriptorMetadata.ColumnContract, and run the cheap sanity
    check against ApiSchema query-field mapping there. If a descriptor resource has a mismatch, mark only that descriptor
    capability as omitted with a clear 501 diagnostic.

  2. I recommend supporting StoredDocument in the descriptor GET handler now.

     The implementation cost should be small because it can reuse the same lookup and invariant checks as external GET. The mode
     should only change materialization:

    - ExternalResponse: public descriptor fields plus id, _etag, _lastModifiedDate; apply readable profile projection; preserve
        namespace/codeValue; recompute _etag.
    - StoredDocument: public descriptor fields only; no id, _etag, _lastModifiedDate, no profile projection, no ChangeVersion.
    - Neither mode should emit internal Uri or Discriminator.  
