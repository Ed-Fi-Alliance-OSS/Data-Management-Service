---
jira: DMS-993
jira_url: https://edfi.atlassian.net/browse/DMS-993
---

# Story: Execute Root-Table Queries with Deterministic Paging

## Description

Implement query execution consistent with the redesign constraints:

- Query compilation is limited to root-table paths (`queryFieldMapping` does not cross arrays).
- Page selection is performed over the resource root table ordered by `DocumentId` ascending.
- Reconstitution is page-based (hydrate/reconstitute in bulk for the selected page).

Align with `reference/design/backend-redesign/design-docs/summary.md` and `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` query sections.

Note: applies to non-descriptor resources; descriptor endpoint query behavior is covered separately.

## Acceptance Criteria

- Query filtering uses only fields mapped to root-table columns.
- Paging is deterministic and stable (order by `DocumentId` ascending).
- Returned items are reconstituted using the page hydration path (not N get-by-id calls).
- Integration tests cover:
  - filtering on a scalar field,
  - paging behavior across multiple pages.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the query execution SQL should be designed to allow authorization filtering to be integrated into the same query (as additional WHERE clauses or JOINs). For GET-many, authorization filtering is embedded directly in the page selection query rather than executed as a separate roundtrip. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" (GET-many roundtrip #2).

## Tasks

1. Implement query compilation from `queryFieldMapping` to physical columns (root only).
2. Implement SQL generation for filters + paging and execute it safely (parameterized).
3. Integrate page keyset selection with hydration + reconstitution.
4. Add integration tests for query correctness and stable paging.

## Clarifying Questions and Answers

### Questions 1

  1. id query support: the authoritative schema still exposes id as a query field on a large portion of resources, but the story/design says query compilation is root-table-only and id
     lives on dms.Document.DocumentUuid. Do you want DMS-993 to support ?id= by joining or pre-resolving against dms.Document, or is id intentionally out of scope for the relational query
     path for now?
  2. Descriptor-valued query fields: the real queryFieldMapping surface includes a lot of ...Descriptor filters on non-descriptor resources. Should this story resolve descriptor URIs
     to ..._DescriptorId for query filtering now, even if that adds a pre-resolution step, or are descriptor filters deferred despite being present in today’s API schema?
  3. Reference-identity query fields: should DMS-993 include fields like studentUniqueId, schoolId, etc. that map into reference objects, using the local propagated/unified root-table
     columns, or do you want that subset deferred behind DMS-991 even though the query design already points that way?
  4. Relational query seam: GET-by-id already has a relational-local request contract carrying MappingSet, qualified resource info, read mode, and readable-profile inputs, but query does
     not. Do you want this story to introduce the equivalent IRelationalQueryRequest and make relational GET-many own readable-profile projection end-to-end?
  5. Profile response integration: because src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileFilteringMiddleware.cs:29 bypasses relational reads, and src/dms/core/
     EdFi.DataManagementService.Core/Handler/QueryRequestHandler.cs:57 currently just returns the array body, should DMS-993 also own the profile-specific response content type for
     relational query responses, not just per-item projection?
  6. Unsupported query mappings: if a query field cannot be compiled under the agreed DMS-993 scope, do you want that to fail fast at startup/plan-compilation time, or return a request-time
     failure only when that parameter is used?
  7. Test bar: do you want query integration coverage on both PostgreSQL and SQL Server in this story, or is one-provider integration plus shared unit tests acceptable for the first pass?

### Answers 1

  1. Support ?id= as a special-case query field. The page-selection SQL joins dms.Document only when an id predicate is present; otherwise it remains root-table-only.
  2. Descriptor-valued query fields: yes, include them in DMS-993. Resolve URI to descriptor DocumentId in one batched pre-step, then filter on the root-table ..._DescriptorId column. That
     stays within the “root-only” execution model after resolution, and the design already calls out descriptor URI to DocumentId query-time resolution via referential identity (reference/
     design/backend-redesign/design-docs/flattening-reconstitution.md:1328). If a descriptor URI does not resolve, short-circuit to an empty page rather than a 400.
  3. Reference-identity query fields: yes, include them now; do not defer them behind DMS-991. The design explicitly says query compilation should target local root-table binding/path
     columns, including UnifiedAlias columns, for reference identity fields with no joins (reference/design/backend-redesign/design-docs/compiled-mapping-set.md:414, reference/design/
     backend-redesign/design-docs/flattening-reconstitution.md:750). The existing PageDocumentIdSqlCompiler tests already cover the presence-gated alias rewrite this needs (src/dms/backend/
     EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageDocumentIdSqlCompilerTests.cs:26).
  4. Relational query seam: yes, introduce a backend-local IRelationalQueryRequest, but keep it slimmer than GET. IQueryRequest already carries ResourceInfo, query elements, auth inputs,
     and pagination (src/dms/core/EdFi.DataManagementService.Core/Backend/QueryRequest.cs:14), so the relational additions mainly need to be MappingSet and optional readable-profile
     projection context. Mirror the GET-by-id pattern (src/dms/core/EdFi.DataManagementService.Core/Backend/GetRequest.cs:22), but I would not invent a query “stored document” mode unless a
     real internal caller appears.
  5. Profile response integration: yes, DMS-993 should own the full relational readable-profile path for GET-many. The repository should project each item through IReadableProfileProjector
     and refresh per-item _etag, and the handler should set the profile-specific response content type. The legacy middleware explicitly skips relational reads (src/dms/core/
     EdFi.DataManagementService.Core/Middleware/ProfileFilteringMiddleware.cs:29), and QueryRequestHandler currently just returns the array body with no relational profile content-type
     handling (src/dms/core/EdFi.DataManagementService.Core/Handler/QueryRequestHandler.cs:57). That matches the ownership split in reference/design/backend-redesign/design-docs/
     profiles.md:1000 and the _etag rule in reference/design/backend-redesign/design-docs/update-tracking.md:121.
  6. Unsupported query mappings: fail fast during mapping/query-plan compilation for the resource, not only when a parameter is used. queryFieldMapping is schema contract, so an un-
     compilable field is a compiler/schema problem, not ordinary request-time input. Request-time latent failures would be especially confusing because Core validation has already accepted
     the parameter. If the implementation supports resource-scoped omission rather than whole-mapping-set failure, that is fine; the important part is deterministic pre-request failure, not
     lazy runtime rejection.
  7. Test bar: both PostgreSQL and SQL Server should get end-to-end integration coverage in this story. The paging SQL, keyset materialization, GUID/UUID handling, and hydration batch
     execution are provider-sensitive enough that one-provider integration plus unit tests is too weak. I would keep the matrix small: one scalar-filter scenario and one multi-page paging
     scenario per provider, plus shared unit tests for query-map compilation and unsupported-mapping cases.

  That gives DMS-993 a real non-descriptor query path instead of a slice that technically works but excludes a large percentage of the API’s current query surface.

### Questions 2

  1. Query syntax scope: do you want DMS-993 to support exact-match filters only, or should it also own range and wildcard parsing now? The current Core query contract is still just scalar
     QueryElement values in src/dms/core/EdFi.DataManagementService.Core.External/Model/QueryElement.cs:13, while src/dms/core/EdFi.DataManagementService.Core/Middleware/
     ValidateQueryMiddleware.cs:97 does not currently parse range syntax even though the schema descriptions mention it.
  2. id validation semantics: once ?id= compiles to dms.Document.DocumentUuid, what should happen for ?id=not-a-guid? The schema exposes id as a string, so today that value would pass
     validation. Do you want that to return an empty page, a 400, or be special-cased earlier in Core?
  3. Descriptor-resolution miss semantics: if a descriptor-valued filter is in scope and the supplied URI does not resolve to any descriptor row, should query return an empty page or a
     request failure? That affects whether descriptor resolution is modeled as ordinary filtering or as validation.
  4. Authorization rollout for GET-many: what is the acceptable interim behavior while relational query authorization is still out of scope? For query this is a bigger rollout risk than
     GET-by-id, because making relational GET-many active without auth could expose unauthorized rows. Should DMS-993 fail closed, stay 501 for secured resources, or is no-auth acceptable
     behind UseRelationalBackend for now?
  5. totalCount scope: do you want totalCount=true fully supported in DMS-993 first pass, or can it be deferred? The plan/compiler docs already treat it as optional, but src/dms/core/
     EdFi.DataManagementService.Core/Handler/QueryRequestHandler.cs:59 already knows how to emit the header if the backend supplies it.
  6. Meaning of “stable paging”: is the requirement only deterministic ordering by DocumentId for a fixed database state, or do you expect page-to-page stability across concurrent inserts/
     updates? The story/design currently chooses offset paging ordered by DocumentId in reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md:12, which gives
     deterministic sort order but not snapshot-stable paging across separate requests.
  7. Failure granularity for unsupported query mappings: if we choose compile-time failure instead of request-time failure, should one bad query field fail the entire mapping set, or just
     mark query support for that resource as unavailable? That changes whether the breakdown needs resource-level omission/guard rails similar to the existing unsupported-read-plan pattern.
  8. Multi-path query fields: do you want DMS-993 to preserve the general “one query field can map to multiple paths and OR them together” contract from src/dms/core/
     EdFi.DataManagementService.Core.External/Model/QueryElement.cs:13, or can the first pass assume one compiled root-column target per query field? The current authoritative 5.2 fixture
     appears not to use multi-path query fields, so this is a simplification opportunity if you want it.

### Answers 2

  After reading reference/design/backend-redesign/epics/08-relational-read-path/04-query-execution.md:91, the sibling read-path stories, and the supporting design/code, I’d keep DMS-993
  tightly focused on root-table filter compilation, deterministic page selection, and batched hydration/reconstitution. That matches reference/design/backend-redesign/design-docs/
  flattening-reconstitution.md:714, reference/design/backend-redesign/design-docs/summary.md:215, reference/design/backend-redesign/design-docs/auth.md:375, the current Core seam in src/
  dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs:97, and the existing backend plumbing in src/dms/backend/EdFi.DataManagementService.Backend.Plans/
  PageDocumentIdSqlCompiler.cs:19 and src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs:71.

  1. Query syntax: keep DMS-993 exact-match only. The compiler already has future operator support, but Core still produces scalar QueryElement values and does no range/wildcard parsing, so
     broadening syntax is separate Core-contract work, not query-execution work.
  2. Invalid ?id=: return an empty page, not 400. id is still schema-typed as a string, so the least surprising first pass is “unresolvable filter => no matches,” with a local UUID parse/
     short-circuit in the relational query layer.
  3. Descriptor-resolution miss: also return an empty page. That keeps descriptor URI resolution as ordinary query preprocessing, consistent with the earlier answer and the design’s query-
     time descriptor resolution model.
  4. Auth rollout: do not fail open. The design requires GET-many auth in the page-selection SQL, and UseRelationalBackend swaps the query handler globally, so the safe interim is 501 for
     secured resources until SQL-layer auth is embedded.
  5. totalCount: keep it in scope for DMS-993. The plan contract, batch builder, executor, hydration result, and handler already support it in src/dms/backend/
     EdFi.DataManagementService.Backend.External/Plans/QueryPlanContracts.cs:10, src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationExecutor.cs:144, src/dms/backend/
     EdFi.DataManagementService.Backend.External/HydrationContracts.cs:57, and src/dms/core/EdFi.DataManagementService.Core/Handler/QueryRequestHandler.cs:57.
  6. “Stable paging”: define it as deterministic ORDER BY DocumentId ASC for a fixed committed state, not snapshot-stable pagination across concurrent writes. Anything stronger is a
     different design, likely cursor/keyset-token based.
  7. Unsupported mappings: prefer resource-scoped compile-time omission over whole-mapping-set failure. One bad query field should make relational query support unavailable for that
     resource with actionable startup/manifest diagnostics, but should not take down unrelated resources. That fits the omission pattern already described in reference/design/backend-
     redesign/epics/15-plan-compilation/03-thin-slice-runtime-plan-compilation-and-cache.md:41.
  8. Multi-path query fields: first pass should assume one compiled physical target per query field and reject multi-path mappings at compile time. The public OR-semantics in src/dms/core/
     EdFi.DataManagementService.Core.External/Model/QueryElement.cs:8 can stay, but I checked the authoritative 5.2 fixture and found no queryFieldMapping entries with more than one path,
     so full OR-group compilation is unnecessary complexity now.

  Refactor Notes

  - Introduce a backend-local relational query request, mirroring the GET seam in src/dms/core/EdFi.DataManagementService.Core/Backend/GetRequest.cs:22, instead of widening public
    IQueryRequest.
  - Reuse the existing page-SQL, hydration, count, and materialization path rather than building a parallel query-only stack.
  - Push unsupported-feature failures into mapping/plan compilation, not ad hoc request-time branches in src/dms/backend/EdFi.DataManagementService.Backend/
    RelationalDocumentStoreRepository.cs:240.

### Questions 3

  1. Do you want DMS-993 to add a compiled per-resource query artifact to MappingSet, or should we keep SQL compilation request-scoped and only do a startup/runtime validation pass for
     query support? Right now PageDocumentIdSqlCompiler exists, but MappingSet only carries read/write plans.
  2. If relational query support is omitted for a specific resource at compile time, should runtime GET-many for that resource return 501 with an actionable message, or some other
     deterministic response? The story says “resource-scoped compile-time omission,” but not the exact runtime contract.
  3. What exact string-comparison semantics do you want across PostgreSQL and SQL Server: ordinal/case-sensitive matching, or provider/default-collation behavior? The current E2E query
     suite has ignored mixed-case-value scenarios, so this looks unresolved and will affect both SQL generation and test expectations.
  4. For the pre-auth interim, should relational GET-many be enabled only for requests/resources with no row-level authorization filtering, or do you want the entire relational GET-many
     surface to stay 501 until auth is embedded in the page-selection SQL? The story says “501 for secured resources,” but the rollout boundary still needs a concrete rule.
  5. For test scope, do you want DMS-993 to stop at provider-specific backend integration tests plus unit/golden coverage, or should we also enable/migrate selected existing E2E query
     scenarios under UseRelationalBackend now? That changes the task breakdown a lot.

  If you’re fine with defaults, my bias would be: compiled query metadata in MappingSet, 501 for omitted resources, ordinal/case-sensitive string semantics, partial rollout only when no
  row-level auth filtering is needed, and backend integration tests first with broader E2E migration deferred.



  Review these, kind of lightweight    codex resume 019d8f17-4a1b-7293-a4e5-a621b406fc35
