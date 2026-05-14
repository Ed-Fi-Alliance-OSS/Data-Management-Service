---
jira: DMS-1055
jira_url: https://edfi.atlassian.net/browse/DMS-1055
---

# Story: Implement EdOrg-only Relationship-based Authorization for GET-many

## Description

Implement the EdOrg-only relationship-based authorization strategies for the GET-many scenario, plus the shared authorization subquery framework, per:

- `reference/design/backend-redesign/design-docs/auth.md`

This ticket delivers the complete authorization subquery pipeline (SQL generation, caching, pagination, TVP threshold, OR semantics, inverted strategies) proven end-to-end with the simpler EdOrg case. People-involved strategies are handled in [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).

Scope note: this story intentionally implements the ODS-parity GET-many slice for root/base EdOrg authorization subjects only. The broader `auth.md` design still resolves and indexes EdOrg/Namespace securable paths on whichever table owns the reference, including child collection tables; those child-table paths are not used as EdOrg relationship GET-many authorization subjects in DMS-1055.

## Acceptance Criteria

### EdOrg-only strategies

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsOnly — includes only root/base EducationOrganization authorization subjects for this ODS-parity slice.
  - RelationshipsWithEdOrgsOnlyInverted — uses the same root/base subject scope and swaps the Source/Target filtering in the auth.EducationOrganizationIdToEducationOrganizationId table (bottom-to-top instead of top-to-bottom).
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.

### Shared authorization subquery framework

- Authorization subqueries filter the auth views/table using the EdOrgIds from the client's token.
- When multiple relationship-based strategies are configured for the same resource, they are combined with OR semantics.
- NoFurtherAuthorizationRequired is ignored as a no-op when combined with relationship-based strategies.
- No duplicate results are returned (uses IN subquery approach, not JOIN).
- Pagination (offset/limit) and total count work correctly with the authorization filter applied.
- For ODS parity, DMS-1055 only applies EdOrg relationship GET-many authorization to root/base-table authorization subjects. The authorization predicate joins `auth.EducationOrganizationIdToEducationOrganizationId` back to the aggregate root alias `r`, or base alias `b` for derived resources, only. EdOrg securable paths that resolve solely to child collection tables are not authorization subjects for this story.
- If the token's unique EdOrgId list is empty, GET-many returns an empty page and totalCount = 0 when requested; it does not return 403.
- Works for both PostgreSQL and SQL Server. For SQL Server, when the token's EdOrgId list has fewer than 2,000 entries, use a parameterized IN clause; otherwise, use a TVP of type dms.BigIntTable.

NOTE: The People-involved strategies (RelationshipsWithEdOrgsAndPeople, RelationshipsWithEdOrgsAndPeopleInverted, RelationshipsWithPeopleOnly, RelationshipsWithStudentsOnly, RelationshipsWithStudentsOnlyThroughResponsibility) will be implemented in [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).

NOTE: The GET-by-id, POST, PUT, and DELETE scenarios will be implemented in [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056).

## Clarifying Questions and Answers

### Questions 1

  1. For GET-many, if a resource has both DMS-1055 strategies and other strategies such as NamespaceBased, OwnershipBased, custom view-based, or People relationship strategies, should the request
     fail fast, partially apply only EdOrg relationship auth, or wait until all configured strategies are supported?
  2. If the caller has no EdOrg IDs in the token but the resource uses RelationshipsWithEdOrgsOnly or RelationshipsWithEdOrgsOnlyInverted, should DMS return 403, return an empty page, or surface
     a configuration/client authorization error?
  3. If a resource is configured with RelationshipsWithEdOrgsOnly but has no EducationOrganization securable elements, should DMS return no rows, return 403, or return a 500 security
     configuration error?
  4. Are EducationOrganization securable elements on child collection tables in scope for DMS-1055 GET-many authorization, or is this story limited to EdOrg columns on the root resource table?
  5. For a single RelationshipsWithEdOrgsOnly strategy with multiple EducationOrganization securable elements, should those elements be combined with AND semantics as stated in the design?
  6. When both normal and inverted EdOrg-only strategies are configured for the same resource, should they be ORed exactly as separate strategies, even if they reference the same securable
     column?
  7. What exact SQL parameter contract should be used for token EdOrg IDs in each dialect: PostgreSQL array parameter, SQL Server expanded scalar parameters below 2,000, and SQL Server structured
     TVP at 2,000+?
  8. For SQL Server TVP usage, what provider-specific binding details should be required: parameter type name, column name Id, SqlDbType.Structured, and ordering/deduplication expectations?
  9. Should token EdOrg IDs be deduplicated before SQL generation, and does the 2,000 threshold apply before or after deduplication?
  10. Should generated authorization SQL be cached at all, or should DMS cache only resolved securable element column paths as recommended by the design?
  11. What should happen if ResolveSecurableElementColumnPath cannot resolve an EducationOrganization securable element for a configured relationship strategy during GET-many?
  12. Should DMS-1055 include ProblemDetails changes for authorization/configuration failures, or are GET-many failures expected to be represented only by filtered results unless setup is
     invalid?
  13. What test cases are required for acceptance: normal filtering, inverted filtering, OR semantics, multiple EdOrg elements, duplicate avoidance, pagination/count after authorization, empty
     token EdOrg IDs, and SQL Server threshold behavior?

### Answers 1

  1. Mixed strategies: fail fast for DMS-1055 if any effective GET-many strategy is not implemented by the relational path, except NoFurtherAuthorizationRequired as a no-op. Do not partially
     apply EdOrg auth. Final ODS semantics are: relationship strategies are ORed, while NamespaceBased, custom view-based, and OwnershipBased are ANDed with that relationship group.

  2. Empty EdOrg token list: for GET-many, return an empty page and totalCount = 0, not 403. Legacy ODS GET-many builds an empty IN as 1 = 0, so no rows match. The current DMS filter-construction
     403 is a write/single-item-style behavior and would be an intentional ODS divergence for GET-many.

  3. EdOrg-only strategy but no ODS-parity root/base EdOrg authorization subjects: return a 500 security configuration error. ODS throws SecurityConfigurationException when a relationship strategy produces no
     authorization subjects; silently returning all rows is unsafe, and returning no rows hides bad metadata.

  4. Child collection EdOrg columns: out of scope for DMS-1055 ODS parity. DMS may still resolve and index child-table EdOrg paths for the relational model, but GET-many relationship authorization in this story must not require at least one authorized child row, must not check every child row, and must not exclude a root document solely because a configured child collection is empty.

  5. Multiple root/base EdOrg authorization subjects in one strategy: yes, AND them. ODS builds one relationship strategy as a conjunction of its filters, and the DMS design says the token must have access to all participating securable elements. Child-table EdOrg paths are excluded from the DMS-1055 participating subject set.

  6. Normal plus inverted on the same resource: yes, OR them as separate relationship strategies, even if they target the same securable column. The inverted strategy is not redundant; it swaps
     SourceEducationOrganizationId and TargetEducationOrganizationId.

  7. SQL parameter contract:

  Use ClaimEducationOrganizationIds as the logical auth-list parameter name, matching ODS terminology.

  For RelationshipsWithEdOrgsOnly:

  authEdOrg.SourceEducationOrganizationId IN/ANY token EdOrg IDs
  AND authEdOrg.TargetEducationOrganizationId = root/base resource EdOrg column

  For RelationshipsWithEdOrgsOnlyInverted:

  authEdOrg.TargetEducationOrganizationId IN/ANY token EdOrg IDs
  AND authEdOrg.SourceEducationOrganizationId = root/base resource EdOrg column

  Dialect contract:

  - PostgreSQL: one bigint[] parameter, @ClaimEducationOrganizationIds, SQL uses = ANY(@ClaimEducationOrganizationIds).
  - SQL Server, fewer than 2,000 unique IDs: expanded scalar bigint parameters named @ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1, etc.
  - SQL Server, 2,000+ unique IDs: one structured parameter @ClaimEducationOrganizationIds, SQL uses IN (SELECT [Id] FROM @ClaimEducationOrganizationIds) or an equivalent join inside the auth subquery.

  Do not inline token IDs, even though one ODS single-item path does that.

  8. SQL Server TVP binding:

  Use Microsoft.Data.SqlClient.SqlParameter with:

  - ParameterName = "@ClaimEducationOrganizationIds"
  - SqlDbType = SqlDbType.Structured
  - TypeName = "dms.BigIntTable"
  - Value = DataTable with exactly one long column named Id

  ODS’s TVP helper uses a single Id column /home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/SqlServer/SqlServerTableValuedParameterHelper.cs:20, and its structured binder sets SqlDbType.Structured plus TypeName /home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/SqlServer/SqlServerStructured.cs:61. DMS should use dms.BigIntTable, not ODS’s older dbo.BigIntTable.

  9. Deduplication and threshold:

  Yes, deduplicate before SQL generation and binding. Apply the 2,000 threshold after deduplication. Sort ascending after deduplication for deterministic SQL parameter names, test output, logs, and TVP row order. Ordering is not semantically required by SQL.

  10. Caching:

  Do not cache generated authorization SQL. Cache only helper results such as resolved securable column paths, keyed by mapping set/effective schema, resource, and securable element. This matches the design’s warning that generated SQL cache keys would need token-list cardinality, operation, strategies, resource, schema hash, and securable element reference/design/backend-redesign/design-docs/auth.md:1070.

  11. Unresolvable EdOrg securable:

  Treat an unresolvable root/base EdOrg securable path, or a strategy that produces no root/base EdOrg authorization subjects after excluding child-table paths, as a security configuration failure, not as an empty result and not as a silent skip. ODS throws a security configuration exception when a relationship strategy produces no authorization subjects /home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Api/Security/AuthorizationStrategies/Relationships/RelationshipsAuthorizationStrategyBase.cs:74. DMS should fail fast with resource, strategy, and securable element
  details in logs/error text.

  If one root/base path cannot resolve but another valid root/base path for the same securable can, use the valid shortest/canonical path. Child-only paths do not make the strategy applicable for DMS-1055. If no root/base path resolves, fail.

  12. ProblemDetails:

  No new ProblemDetails shape is needed for normal GET-many denial. Unauthorized rows are filtered out; the response is 200 with an authorized page, possibly empty, and total count over authorized rows.

  Use existing failure paths for exceptional cases:

  - Empty EdOrg token list: return an empty page and totalCount = 0 when requested. The GET-many path must not use the current write/single-item-style 403 from the auth filter provider.
  - Unsupported/mixed strategies outside DMS-1055 scope: fail fast with 501 Not Implemented for known strategies that have not yet been implemented in the relational GET-many path.
  - Invalid security configuration/path resolution failure: server/configuration failure, status 500.

  13. Acceptance tests:

  Minimum acceptance set:

  - Normal EdOrg filtering returns only rows whose resource EdOrg is reachable from token EdOrg.
  - Inverted filtering swaps Source/Target and returns bottom-to-top authorized rows.
  - Normal plus inverted relationship strategies are ORed.
  - Multiple root/base EdOrg authorization subjects within one strategy are ANDed, matching ODS GET-many behavior.
  - A resource with a root/base EdOrg subject is filtered through the aggregate root alias `r`, or base alias `b` for derived resources.
  - A resource with both root/base and child-table EdOrg securable paths authorizes using only the root/base subject for DMS-1055.
  - A resource whose EdOrg securable paths all resolve to child collection tables fails as a security configuration error for DMS-1055.
  - Generated SQL does not join child collection tables for EdOrg-only relationship GET-many authorization.
  - Duplicate avoidance: one document returned once when multiple auth rows or strategies match.
  - Pagination and totalCount are applied after authorization filtering.
  - Empty token EdOrg IDs returns an empty page and totalCount = 0 when requested.
  - SQL Server 1,999 unique IDs uses expanded scalar params; 2,000 unique IDs uses dms.BigIntTable.
  - Duplicate token IDs are deduped before threshold selection.
  - PostgreSQL uses a single array parameter.
  - Unsupported mixed strategies such as NamespaceBased, OwnershipBased, convention-matching custom view-based strategies, and People strategies fail fast with the temporary DMS-1055 501 behavior. These tests must be updated by later strategy stories to assert the final `auth.md` composition semantics, where non-relationship strategies are ANDed with the relationship strategy OR group. NoFurtherAuthorizationRequired is ignored as a no-op when combined with relationship strategies.
  - Unresolvable/no root/base EdOrg authorization subject fails as configuration error.


### Questions 2

  1. Which empty EdOrg-token behavior wins? The story says both “GET-many returns empty page / totalCount = 0” and later “keep
     current 403 from the auth filter provider.”
  2. How should NoFurtherAuthorizationRequired + RelationshipsWithEdOrgsOnly behave? One answer says NoFurtherAuthorizationRequired
     is a no-op; the test list says mixed NoFurtherAuthorizationRequired plus relationship should fail fast.
  3. Should DMS-1055 refactor the current filter-provider middleware so GET-many can reach the relational backend with empty EdOrg
     IDs, inverted strategies, or unsupported mixed strategies? Today some of those fail earlier as 403 before query planning.
  4. For unsupported mixed strategies in DMS-1055, what exact failure surface do you want: current 501 not implemented, 403, or 500
     security configuration error?
  5. For EdOrg securable elements on child collection tables, should authorization require any matching child row, or every child row
     for that securable path to be authorized? This is the biggest semantic question around child-table EXISTS.
  6. If a configured child-table securable path has no child rows for a document, should that document be excluded, or is that a
     configuration/data error?
  7. Is it acceptable to extend the compiled mapping-set/runtime contracts to carry richer securable-path metadata, such as original
     JSON path and MetaEd name, alongside ResolvedSecurableElementPath? That would make diagnostics and per-element handling cleaner.
  8. Should DMS-1055 add only GET-many support for RelationshipsWithEdOrgsOnlyInverted, or also add provider/validator stubs so the
     existing shared authorization registry does not fail unexpectedly for other operations before DMS-1056?
  9. For SQL Server TVP binding, is it acceptable to extend the page keyset/query parameter model to carry provider-specific
     parameter configuration? Current hydration query parameters only carry name/value, but TVP needs SqlDbType.Structured and
     TypeName.
  10. What test level do you expect for acceptance: backend unit/integration tests only, or also DMS E2E tests through the local
     Docker stack and real token/claim-set wiring?

### Answers 2

  1. Empty EdOrg-token behavior: GET-many returns 200 with an empty result page and totalCount = 0 when requested. This answer supersedes the conflicting "current 403" wording in Answers 1. The existing 403 is appropriate for write/single-item-style authorization, but GET-many should model ODS's empty auth list as a filter that matches no rows.

  2. NoFurtherAuthorizationRequired plus RelationshipsWithEdOrgsOnly: NoFurtherAuthorizationRequired is a no-op. It does not make the request fail fast, does not restrict results, and does not contribute error hints. The relationship strategy still applies normally.

  3. Middleware/refactoring: yes. DMS-1055 should refactor the current filter-provider/request path enough for GET-many to reach relational query planning with an empty EdOrg claim list, inverted relationship strategy names, and known unsupported mixed strategies. The relational planner/repository should then return the correct result or fail-fast surface instead of the shared filter provider preemptively returning 403.

  4. Unsupported mixed strategy failure surface: known strategies that are outside DMS-1055 scope for GET-many should fail as 501 Not Implemented. Examples include NamespaceBased, OwnershipBased, People relationship strategies, and custom view-based strategies whose names match the `{BasisResource}With...` convention and resolve to a known basis resource, until their stories are implemented. Truly unknown strategy names, invalid custom-view strategy names, custom-view names that cannot be resolved to a known basis resource, or other invalid security metadata are security configuration failures and should use the security-configuration 500 path.

  Temporary staging behavior: the 501 response is only for DMS-1055's limited implementation scope and is not the final authorization composition model. As NamespaceBased, OwnershipBased, custom view-based, and People relationship stories are implemented, this fail-fast behavior must be replaced with the final `auth.md` semantics: non-relationship strategies are ANDed with the relationship strategy OR group.

  5. Child-table EdOrg securable semantics: out of scope for DMS-1055 ODS parity. GET-many relationship authorization in this story must not use child-table predicates, must not require every existing child row to be authorized, and must not use "any matching child row" semantics. Only root/base EdOrg authorization subjects participate.

  6. Child-table path with no child rows: do not exclude the document for DMS-1055. Since child-table EdOrg paths are not authorization subjects for this story, an empty child collection has no direct effect on GET-many relationship authorization. A configuration error applies when the configured strategy has no applicable root/base EdOrg authorization subjects.

  7. Richer securable-path metadata: yes. It is acceptable to extend compiled mapping-set/runtime contracts so each resolved path carries the original JSON path and a readable/MetaEd name alongside ResolvedSecurableElementPath. This supports diagnostics, security configuration errors, and per-element handling without reverse-mapping from physical columns.

  8. Inverted strategy registry support: add GET-many support for RelationshipsWithEdOrgsOnlyInverted and add enough constants/provider registration for the shared authorization registry to recognize it. For GET-by-id, POST, PUT, and DELETE before DMS-1056, fail explicitly as not implemented rather than falling through to missing-provider 403s or accidental authorization.

  9. SQL Server TVP binding model: yes. Extend the page keyset/query parameter model or introduce a companion runtime parameter contract so parameters can carry provider-specific configuration such as SqlDbType.Structured and TypeName. Keep compiled/AOT query plans provider-neutral where possible; use the runtime binding layer to attach SQL Server-specific parameter configuration.

  10. Test level: include backend unit tests, PostgreSQL and SQL Server backend integration tests, and a focused DMS E2E slice through the local Docker stack with real token/claim-set wiring. Keep SQL shape, TVP threshold, deduplication, and provider binding coverage in unit/integration tests; use E2E for middleware/claim interactions such as empty EdOrg claims, normal filtering, inverted filtering, OR semantics, and totalCount after authorization.

### Questions 3

  1. Should DMS-1055 introduce a typed token authorization context for relational GET-many, or keep parsing EdOrg IDs from AuthorizationStrategyEvaluator.Filters? I prefer a typed context so dedupe/sort/TVP binding is not coupled to legacy filter providers.
  2. For empty EdOrg claims, should the implementation short-circuit in C# to an empty page/count 0, or should it still generate SQL with a 1 = 0 authorization predicate?
  3. How should we distinguish custom view-based strategy names from truly unknown strategy names before DMS-1062? For example, should {BasisResource}With... be treated as “known but not implemented” and return 501, while non-matching unknown names remain a 500 security configuration error?
  4. For security configuration failures in this story, should we use the current generic 500 response path, or add the urn:ed-fi:api:system-configuration:security ProblemDetails shape ahead of DMS-1099?
  5. Should we add a permanent CMS/E2E claim set for RelationshipsWithEdOrgsOnlyInverted, or create/patch that claim metadata only inside focused tests? I did not find an existing E2E inverted claim set.
  6. For non-GET operations configured with RelationshipsWithEdOrgsOnlyInverted before DMS-1056, should DMS-1055 force an explicit 501, or is it enough that GET-many works and other operations remain on the current failure path?
  7. Is it acceptable to add provider-specific parameter metadata to the query/runtime parameter model for SQL Server TVPs, while keeping actual SqlParameter construction in the executor layer?

### Answers 3

  1. Use a typed relational authorization context.
     Do not keep parsing EdOrg IDs out of AuthorizationStrategyEvaluator.Filters. Build a request-scoped context from ClientAuthorizations, with deduped/sorted ClaimEducationOrganizationIds, and pass
     it into relational GET-many. Keep AuthorizationStrategyEvaluator for effective strategy names/grouping. This also avoids the current empty-claim 403 path in src/dms/core/
     EdFi.DataManagementService.Core/Security/AuthorizationFilters/IAuthorizationFiltersProvider.cs:28.
  2. Short-circuit empty EdOrg claims in C# after strategy classification.
     First classify strategies and return 501 for unsupported known strategies. Then, if the effective supported relationship group requires EdOrg claims and the deduped list is empty, return 200,
     empty page, and Total-Count: 0 when requested. No need to generate 1 = 0 SQL for DMS-1055.
  3. Add a strategy classifier now.
     Classify strategy names as:

  - known supported in DMS-1055: RelationshipsWithEdOrgsOnly, RelationshipsWithEdOrgsOnlyInverted
  - known no-op: NoFurtherAuthorizationRequired
  - known but not implemented for GET-many yet, returning 501 Not Implemented: NamespaceBased, OwnershipBased, People relationship strategies, and custom view-based strategy names that match the `{BasisResource}With...` convention and resolve to a known basis resource or descriptor
  - unknown or invalid security metadata: security-configuration 500 path

  For custom view names, treat `{BasisResource}With...` as known-but-not-implemented only if the basis resource or descriptor resolves from the effective schema. Non-matching names, names with an unknown
  basis resource, and otherwise invalid custom-view metadata remain 500 security configuration errors. This aligns with the view-based design and DMS-1062 story in
  reference/design/backend-redesign/epics/14-authorization/13-view-based-auth-get-many.md:16.

  4. Use the canonical security-configuration ProblemDetails shape now.
     DMS-1055 should not introduce a new ProblemDetails shape or encode a generic 500 response for security configuration failures. Use the canonical 500 shape from `auth.md`:

  - type: `urn:ed-fi:api:system-configuration:security`
  - title: `Security Configuration Error`
  - detail: `A security configuration problem was detected. The request cannot be authorized.`

  If the shared DMS-1099 implementation is not available yet, add the minimal request-time mapping needed for DMS-1055 security configuration failures and keep it compatible with DMS-1099. This applies to
  invalid/missing security metadata, unresolvable root/base EdOrg securable paths, and relationship strategies with no applicable root/base EdOrg authorization subjects.

  Normal GET-many authorization denial still returns 200 with filtered results, possibly empty. Unsupported-but-known strategies outside DMS-1055 scope still return 501 Not Implemented.

  5. Add a permanent test-only inverted claim set.
     Create a focused E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet in the CMS/E2E claimset artifacts. Do not patch claim metadata ad hoc inside tests. The acceptance criteria explicitly need real
     token/claim-set wiring, and a committed fixture is easier to reason about than runtime mutation.
  6. Force explicit 501 for non-GET inverted operations before DMS-1056.
     Add constants/registration so RelationshipsWithEdOrgsOnlyInverted is recognized everywhere, but for GET-by-id, POST, PUT, and DELETE return an intentional not-implemented result until DMS-1056. Do
     not let it fall through to missing-provider 403s.
  7. Yes, add provider-specific runtime parameter metadata.
     Keep SQL Server SqlParameter construction in the executor/binder layer, but extend the query parameter contract so a parameter can say “structured TVP, type dms.BigIntTable, column Id.” The
     current hydration path only carries name/value and binds generic parameters in src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs:239; that is not enough for SQL
     Server TVPs. The existing RelationalParameter.ConfigureParameter pattern in src/dms/backend/EdFi.DataManagementService.Backend/RelationalCommandAccess.cs:43 is a useful precedent, but keep
     delegates out of AOT/compiled plan contracts.

### Implementation Notes

All new DMS E2E scenarios added for this story must be tagged for the relational backend with the scenario-level `@relational-backend` tag.

One of the last implementation steps should be to switch all existing E2E tests that already cover this story's behavior from the legacy backend to the relational backend by adding the same scenario-level `@relational-backend` tag. No other changes to those existing E2E tests should be necessary for them to pass.

Use docker to create a MSSQL container from a local image for testing.
