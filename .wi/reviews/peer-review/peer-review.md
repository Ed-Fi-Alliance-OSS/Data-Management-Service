DMS-1148
Claude
  ---

  Design / spec drift

  D1 · Duplicate IsDescending helper diverges from base class

- What's wrong: ResourceClaimRepository.IsDescending(PagingQuery)
  (src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Repositories/ResourceClaimRepository.cs:110-115) duplicates the logic of PagingQuery.IsDescending
  (src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/PagingQuery.cs:20-25). Both branches accept desc/descending case-insensitively.
- Evidence: Two implementations of the same rule, in two assemblies.
- Impact: If PagingQuery.IsDescending is later extended (e.g., a new alias, or stricter rejection), the repository's static helper will silently diverge. Low immediate impact but
  real maintenance trap.
- Recommendation: Delete the static helper and call query.IsDescending in each SortAndPage overload.

  D2 · Misleading pragma comment on unused using in MssqlUnsupportedResourceClaimRepository

- What's wrong: src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/Repositories/MssqlUnsupportedResourceClaimRepository.cs:7-10 suppresses S1128 with the comment "Query
  types in Model, response types in Model.ResourceClaims - both needed". The file never instantiates anything from Model.ResourceClaims — only FailureUnknown records (which live in
  Backend.Repositories). The Model.ResourceClaims using is genuinely unused.
- Impact: A future reader trusts the comment and leaves the pragma in place; pragmas accumulate.
- Recommendation: Drop the using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims; line and remove the surrounding pragma.

  ---
  Test coverage gaps

  T1 · No negative test for query params on /v2/resourceClaims/{id}

- Missing scenario: A GET /v2/resourceClaims/1?orderBy=invalidField must not 400 — the spec explicitly says "Accepts only its path parameter. Query parameters do not apply to this
  route." The current implementation does not bind a paging-query validator on GetById, so this should pass — but there's no regression guard.
- Why it matters: If someone later adds [AsParameters] FrontendPagingQuery to GetById (e.g., copy-paste from GetAll), a 400 would silently appear and no test would fail.
- Suggested test: In ResourceClaimModuleTests.Given_resource_claims_exist, add It_ignores_query_parameters_on_getById that GETs
  /v2/resourceClaims/1?orderBy=invalidField&direction=sideways and asserts 200.

  T2 · No explicit test for FailureMultipleHierarchiesFound → 500

- Missing scenario: ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound (src/config/backend/EdFi.DmsConfigurationService.Backend/Repositories/IClaimsHierarchyRepository.cs:45)
   is currently mapped to 500 via the _ arm in the repository switch — which matches AuthorizationMetadataModule. There is no test that asserts this.
- Why it matters: If someone narrows the switch (e.g., adds an explicit arm that returns FailureNotFound for multi-hierarchy), the public contract changes silently.
- Suggested test: Integration test that inserts two rows into dmscs.ClaimsHierarchy and asserts FailureUnknown (which maps to 500).

  ---
  Simplification opportunities

  S1 · Three nearly identical SortAndPage overloads

- Location: ResourceClaimRepository.cs:117-205.
- What: Each overload has the same shape: switch on OrderBy?.ToLowerInvariant() ?? "<default>", optionally descend, then Skip/Take. The only differences are the available orderBy
  cases and the response type.
- Recommendation (only if it stays simple): A generic SortAndPage<T>(items, query, keySelectors) taking a Dictionary<string, Func<T, object>> would consolidate this. Skip if it
  would require gymnastics for type variance — duplication here is acceptable.

  S2 · Reflection-based private-method tests

- Location: src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Repositories/ResourceClaimRepositorySortingTests.cs (entire file) reaches SortAndPage and
  IsDescending via BindingFlags.NonPublic | Static reflection.
- Why it matters: Rename SortAndPage → Sort and these tests pass by skipping invocation (the Invoke returns null/throws but the assertion is on a private method that no longer
  exists). The csproj already references EdFi.DmsConfigurationService.Backend.Postgresql (per the diff), so promoting these methods to internal and adding [InternalsVisibleTo] would
  give direct test access without reflection.
- Recommendation: Promote SortAndPage / IsDescending to internal static, add InternalsVisibleTo("EdFi.DmsConfigurationService.Backend.Tests.Unit") to the Postgresql project, and
  replace method.Invoke(null, [claims, query]) calls with direct invocation. If D1 is taken (delete IsDescending), its tests go away entirely.

  ---
  Maintainability

  M1 · Identical preamble across 4 endpoint methods

- Location: ResourceClaimRepository.GetResourceClaims, GetResourceClaim, GetResourceClaimActions, GetResourceClaimActionAuthStrategies — each opens with await
  claimsHierarchyRepository.GetClaimsHierarchy() → result switch → LoadResourceClaimMetadata() → BuildProjectedHierarchy() → null-check → log. ~40 lines repeated 4×.
- Why it matters: A change to the projection contract (e.g., new failure variant on ClaimsHierarchyGetResult) needs to be applied four times. T2 already shows one place where
  someone could narrow the switch and only update one site.
- Recommendation: Extract a private Task<(ProjectionResult? projection, TFailure? failure)> TryBuildProjection<TFailure>(Func<...> failureFactories) helper. Or, simpler: introduce
  a TryGetProjection(out ProjectionResult?, out ProjectionFailureKind) that each method maps to its own result type. This is a refactor — only worth doing if the next story touches
  this file.

------------------------------------------------------------
Codex:
• - High: src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0009_Insert_ResourceClaim.sql:424 adds required seed rows by editing an already-journaled
    DbUp script. Existing PostgreSQL databases that already recorded 0009 will skip those homograph/sample rows, causing hierarchy projection to fail instead of serving the new
    endpoints.

- Medium: src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Repositories/ResourceClaimRepository.cs:297 validates action names for /v2/resourceClaimActions but
    never resolves the referenced authorization strategies before returning success at line 376. Bad DefaultAuthorization strategy data can still return 200 OK, violating the
    complete-or-fail lookup contract.
