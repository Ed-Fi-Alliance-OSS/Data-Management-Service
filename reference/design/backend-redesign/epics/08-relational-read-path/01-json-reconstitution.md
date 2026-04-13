---
jira: DMS-990
jira_url: https://edfi.atlassian.net/browse/DMS-990
---

# Story: Reconstitute JSON from Hydrated Rows (Including `_ext`)

## Description

Implement JSON reconstitution from hydrated relational rows:

- Dependency note: readable profile projection in this story is hard-blocked on Core profile story C7 (`reference/design/backend-redesign/epics/07-relational-write-path/01a-c7-readable-profile-projection.md`), which implements the Core-owned readable projector this story invokes after full reconstitution.
- Rebuild the JSON structure for the resource using the compiled reconstitution plan.
- Preserve array ordering using `Ordinal` columns.
- Attach nested collections and scope-aligned extension rows using stable parent row identity.
- Overlay `_ext` subtrees from extension tables, emitting `_ext` only when values exist.
- When readable profile semantics apply, pass the full reconstituted document through the Core-owned readable projector instead of filtering in backend.

## Acceptance Criteria

- Reconstituted JSON matches the expected shape for:
  - scalar properties,
  - inlined objects,
  - collections and nested collections (order preserved),
  - `_ext` at root and within collection elements.
- Reconstitution does not emit `_ext` when there are no extension values.
- Backend does not reimplement readable profile filtering; readable profiles are applied by Core after full reconstitution using the Core-owned readable projector from C7 (`01a-c7-readable-profile-projection.md`).
- Unit/integration tests validate reconstitution for at least one “nested + ext” fixture.

## Tasks

1. Implement a reconstitution engine that consumes hydrated rows and writes JSON deterministically.
2. Implement collection assembly using stable parent row identity plus `Ordinal` ordering.
3. Implement `_ext` overlay rules per `reference/design/backend-redesign/design-docs/extensions.md`.
4. Invoke the Core-owned readable profile projector after full reconstitution when a readable profile applies; backend does not introduce a parallel backend-owned filtering path.
5. Add tests validating end-to-end “write then read” JSON equivalence for representative fixtures.

## Clarifying Questions and Answers

### Questions 1

• I found substantial overlap already in src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs, src/dms/backend/EdFi.DataManagementService.Backend/
  RelationalWriteCurrentState.cs, and the read-plan/projection work, while external relational reads are still stubbed in src/dms/backend/EdFi.DataManagementService.Backend/
  RelationalDocumentStoreRepository.cs. Before breaking this down, these are the questions I’d want answered:

  - Should DMS-990 be scoped as “finish and harden the shared reconstitution engine” only, or should it also wire that engine into real relational GET by id responses now? Right now
    GetDocumentById and QueryDocuments still return not implemented in src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs.
  - Under UseRelationalBackend, is a temporary hybrid acceptable where writes are relational but external reads still use the legacy path until later stories, or is the expectation that relational
    GET by id becomes the active read path as part of this story?
  - Do you want this story to retire or bypass the legacy profile response filtering for relational reads? src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileResponseFilter.cs and src/
    dms/core/EdFi.DataManagementService.Core/Middleware/ProfileFilteringMiddleware.cs are still in the GET/query pipeline, so once the backend invokes IReadableProfileProjector, we need a clear
    plan to avoid double-filtering.
  - Is it acceptable to change the Core read request contracts so relational GET/query can receive the active MappingSet, similar to writes? src/dms/core/EdFi.DataManagementService.Core/Backend/
    GetRequest.cs and src/dms/core/EdFi.DataManagementService.Core/Backend/QueryRequest.cs do not currently carry it.
  - Should this story own response metadata injection for id, _etag, and _lastModifiedDate, or do you want that deferred to later GET/query stories? The design doc includes those in read-time
    materialization, but the story text is focused on JSON shape and profile projection.
  - For empty arrays, do you want the full design behavior now: emit [] for schema-required arrays and omit optional empty arrays, or is “omit when empty” acceptable for this story? The current
    reconstituter omits empty arrays, and I do not see required-array metadata carried through the current read model.
  - Should we treat src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs as the single shared engine for both external reads and write-side current-state loading, or
    is it acceptable to split those paths temporarily? src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteCurrentState.cs already depends on it.
  - Do you want us to stay with the current JsonNode-based assembler for now, or use this story to move toward the design’s Utf8JsonWriter/streaming approach? The current readable projector and a
    lot of existing tests already assume JsonNode.
  - Given that reference and descriptor projection are already partially implemented and intertwined with reconstitution, should we fold that overlap into this story’s breakdown, or keep strict
    boundaries with DMS-991 and DMS-992 and only touch them when refactoring forces it?
  - What test bar do you want for this story specifically: unit coverage on the shared reconstitution engine only, or at least one real relational repository/API GET by id integration test so the
    story proves end-to-end read wiring rather than only internal assembly?

### Answers 1

• Short version: I would scope DMS-990 as “finish the shared reconstitution engine and make relational GET by id real,” not as an internal-only refactor. The story text in reference/design/
  backend-redesign/epics/08-relational-read-path/01-json-reconstitution.md already implies backend read-path wiring, and leaving src/dms/backend/EdFi.DataManagementService.Backend/
  RelationalDocumentStoreRepository.cs stubbed would strand the work behind an unused engine.

  1. DMS-990 should include active relational GET by id, but not full query execution. GET by id can ride the existing hydration/reconstitution path now; QueryDocuments should stay with DMS-993
     even though groundwork already exists in src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageDocumentIdSqlCompiler.cs.
  2. Relational GET by id becomes the active read path as part of this story. Never mix the legacy implementation, it is deprecated.
  3. DMS-990 should own bypassing legacy profile response filtering for relational reads. src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileResponseFilter.cs already says it is
     temporary, and src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileFilteringMiddleware.cs will otherwise double-filter after backend uses src/dms/core/
     EdFi.DataManagementService.Core/Profile/IReadableProfileProjector.cs. Bypass when UseRelationalBackend=true.
  4. Yes, change the read request seam, but mirror the write pattern instead of widening the public interfaces. I would add backend-local IRelationalGetRequest and IRelationalQueryRequest, like
     src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalWriteRequestContracts.cs, and have src/dms/core/EdFi.DataManagementService.Core/Backend/GetRequest.cs and src/dms/core/
     EdFi.DataManagementService.Core/Backend/QueryRequest.cs implement them internally. MappingSet alone is not enough; backend read wiring also needs active readable-profile context.
  5. DMS-990 should own id, _etag, and _lastModifiedDate for GET by id. The data is already present in src/dms/backend/EdFi.DataManagementService.Backend.External/HydrationContracts.cs, and the
     design explicitly puts these on read-time materialization. I would defer ChangeVersion unless a current response contract needs it.
  6. For empty arrays, I would accept “omit when empty” in DMS-990. src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs already behaves that way, and the read plan
     does not appear to carry required-array metadata yet. That is a reasonable follow-up, not a good reason to stall GET by id.
  7. Treat src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs as the single shared base engine. src/dms/backend/EdFi.DataManagementService.Backend/
     RelationalWriteCurrentState.cs already depends on it, so I would keep one base “full stored body” assembler and put API-only steps like metadata injection and readable projection in a thin
     external-read layer above it.
  8. Stay with JsonNode for this story. The design recommends streaming eventually, but the existing reconstituter, the readable projector, and current tests are all JsonNode-centric; moving to
     Utf8JsonWriter now would mix a correctness story with a representation refactor.
  9. Keep DMS-991 and DMS-992 as ownership boundaries, but allow enabling refactors inside DMS-990. Reference and descriptor projection are already partially compiled and consumed by the
     reconstituter, so strict isolation is artificial; what I would avoid is moving their dedicated scenario matrices into DMS-990.
  10. The test bar should include both levels. Keep unit coverage on the shared engine, but require at least one real relational GET by id integration test through the repository, ideally proving
     write then relational read equivalence for a nested plus _ext case. Engine-only tests would miss the seam work that is currently the real risk.

### Questions 2

  1. Should relational GET-by-id change the get seam to carry full ResourceInfo or a backend-local qualified-resource contract instead of only ResourceName? IGetRequest cannot uniquely identify
     extension homographs, while the mapping/read plans are keyed by (ProjectName, ResourceName).
  2. For internal repository GETs used by write flows, should readable-profile projection be explicitly suppressed? ProfileWriteValidationMiddleware currently calls GetDocumentById to fetch the
     existing document with auth bypass, and that fetch needs the full stored document, not a readable-profile-projected one.
  3. For the backend-local read contract, do you want Core to pass the minimal readable-profile inputs only, such as ContentTypeDefinition plus the precomputed identity-property set, or do you
     want the full ProfileContext/schema context passed through?
  4. What is the expected interim authorization behavior for relational GET-by-id? The story makes relational GET active now, but the relational auth stories land later. Should DMS-990 fail closed
     for unsupported auth strategies, support only a narrow allowed subset, or temporarily authorize through the existing ResourceAuthorizationHandler after reconstitution?
  5. What should happen for descriptor-resource GET-by-id while DMS-994 is still deferred? Should DMS-990 explicitly leave descriptor resources unsupported under UseRelationalBackend, or do you
     want descriptor GETs to remain working through some temporary path?
  6. Is the rollout model acceptable where UseRelationalBackend=true gives real relational GET-by-id but GET-many still returns not implemented until DMS-993? Right now the flag swaps the whole
     repository, not just one endpoint shape.
  7. Should DMS-990 preserve LastModifiedTraceId, or is it acceptable to relax/remove that contract? GetResult.GetSuccess still requires it, but the relational document metadata model and redesign
     docs do not carry it.
  8. Is it acceptable for GET-by-id _etag to move to the serialized-JSON hash now even though write responses still use the older behavior and If-Match is deferred to DMS-1005? That affects
     temporary read/write consistency for clients.
  9. For response verification, do you want strict deterministic property-order parity, or is semantic JSON equivalence sufficient as long as array order is preserved? That changes how aggressive
     the integration assertions should be.

### Answers 2

  1. Use a backend-local relational GET contract, not a public interface widening. Current GET only carries ResourceName in src/dms/core/EdFi.DataManagementService.Core/Backend/GetRequest.cs:18,
     while query already carries ResourceInfo in src/dms/core/EdFi.DataManagementService.Core/Backend/QueryRequest.cs:14, and the relational backend already resolves plans by qualified resource in
     src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteSupport.cs:82. I’d expose BaseResourceInfo or an explicit qualified-resource value plus IsDescriptor; full ResourceInfo is
     more than GET-by-id needs.
  2. Yes, readable projection should be explicitly suppressible for internal repository GETs. src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWriteValidationMiddleware.cs:983
     fetches the existing document through GetDocumentById, and that path needs the full stored body, not a readable-profile projection. Make “stored document” vs “external response” an explicit
     local read mode.
  3. Pass minimal readable-profile inputs, not full ProfileContext. src/dms/core/EdFi.DataManagementService.Core/Profile/IReadableProfileProjector.cs:16 only needs ContentTypeDefinition and the
     top-level identity-property set. I’d use a small backend-local projection context with those two values and keep profile resolution/schema concerns in Core.
  4. No interim auth, we do not have a backend implementation that can be used. Instead just a comment that auth is missing and will be added later.
  5. Leave descriptor-resource GET-by-id explicitly unsupported until reference/design/backend-redesign/epics/08-relational-read-path/05-descriptor-endpoints.md. A clear guard rail is better than
     reviving the legacy path.
  6. UseRelationalBackend=true with GET-many still unimplemented is acceptable as a development/integration milestone. The flag swaps the repository and query
     still routes through it in src/dms/core/EdFi.DataManagementService.Core/ApiService.cs:241, while the repository still returns not-implemented in src/dms/backend/
     EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:195.
  7. Relax LastModifiedTraceId to make it optional. GetSuccess still requires it in src/dms/core/EdFi.DataManagementService.Core.External/Backend/GetResult.cs:21, but nothing in the relational redesign
     produces it yet, so make it nullable.
  8. No. Do not ship a split contract where relational GET returns a serialized-JSON-hash `_etag` while write responses use a different format.
     The normative design in reference/design/backend-redesign/design-docs/update-tracking.md:118 and reference/design/backend-redesign/design-docs/flattening-reconstitution.md:906 applies to
     the external `_etag` surface as a whole, not only to GET materialization. DMS-1005 still owns `If-Match` enforcement, but once the relational read path is active the relational write
     responses must emit the same serialized-JSON hash `_etag` format in the same branch.
  9. Use semantic JSON equivalence for integration tests, with strict array-order checks. Property-order parity is too brittle and not semantically required by the design. If you want
     deterministic object ordering, keep that as a unit-level invariant on the shared engine, not the end-to-end acceptance bar.

### Questions 3

  1. Should readable profile projection always preserve id, _etag, and _lastModifiedDate regardless of profile member rules? src/dms/core/EdFi.DataManagementService.Core/Profile/
     ReadableProfileProjector.cs:18 currently only special-cases id plus identity fields, while src/dms/core/EdFi.DataManagementService.Core/OpenApi/ProfileOpenApiSpecificationFilter.cs:20 says
     readable schemas always include those metadata fields. If yes, should DMS-990 own that Core fix?
  2. What exact format do you want for relational read metadata? We know _etag should come from the canonical JSON form of the response document, but I still need the concrete string form, and whether _lastModifiedDate should
     match the current write-side yyyy-MM-ddTHH:mm:ssZ format or preserve higher precision from stored ContentLastModifiedAt.
  3. While GET-many and descriptor endpoints remain deferred, what external behavior do you want under UseRelationalBackend=true for those unsupported reads? src/dms/backend/
     EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:95 currently returns UnknownFailure, which Core maps to HTTP 500. If you want an explicit 501/503 style response
     instead, that changes the seam.
  4. Is DMS-990 allowed to keep descriptor URI expansion as a second batched DB command for GET-by-id, reusing src/dms/backend/EdFi.DataManagementService.Backend.Plans/
     DescriptorProjectionExecutor.cs:16, or do you want descriptor expansion folded into the hydration batch now? If a second command is acceptable, do you want it wrapped in an explicit read
     transaction for consistency?
  5. For the “deterministic JSON” acceptance bar, what canonical object-property ordering should unit tests lock to: compiled schema/path order, current plan column order, or just any stable order
     per mapping set?
  6. For the required integration proof, is one dialect-specific relational GET-by-id test enough for DMS-990, or do you want parity coverage on both PostgreSQL and SQL Server in this story?

 ### Answers 3

  1. Preserve id, _etag, and _lastModifiedDate unconditionally in readable projection.
     This should be true regardless of profile member rules. src/dms/core/EdFi.DataManagementService.Core/Profile/ReadableProfileProjector.cs:16 and src/dms/core/EdFi.DataManagementService.Core/
     OpenApi/ProfileOpenApiSpecificationFilter.cs:20 should agree. I would let DMS-990 own that fix because relational GET will bypass the legacy filter path.
  2. Use `_etag = base64(SHA-256(canonical JSON))` and `_lastModifiedDate` in UTC second precision.
     Remove `id`, `_etag`, and `_lastModifiedDate`, recursively canonicalize object members while preserving array order, then hash the minified UTF-8 bytes. That keeps read/write behavior aligned without coupling `_etag` to exact frontend serializer bytes and preserves
     `_lastModifiedDate` formatting as yyyy-MM-ddTHH:mm:ssZ for now, even if the stored DB stamp has higher precision.
  3. Return an explicit “not implemented yet” result for unsupported relational reads.
     I would not leave intentional gaps as UnknownFailure/HTTP 500 from src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:95. Add a NotImplemented-style read
     result and map it to HTTP 501. That is cleaner for query and descriptor GET stubs while UseRelationalBackend=true.
  4. I want descriptor expansion folded into the hydration batch now because it is the story wiring GET-by-id in src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs:95.
     DMS-992 should still own descriptor behavior coverage, not command-topology ownership.
  5. Define deterministic object ordering as canonical compiled JSON-path order.
     I would not bind tests to source schema file order, and I would not treat current SQL column order as the contract. The stable contract should be “the
     read plan emits properties in canonical compiled path order,” with integration tests staying semantic and unit tests asserting the ordering invariant.
  6. Both dialects for one nested-plus-_ext GET-by-id scenario, because this story crosses provider-specific hydration/materialization seams even if most logic is shared. 
