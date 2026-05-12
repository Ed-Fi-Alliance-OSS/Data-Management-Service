---
jira: DMS-1055
jira_url: https://edfi.atlassian.net/browse/DMS-1055
---

# Story: Implement EdOrg-only Relationship-based Authorization for GET-many

## Description

Implement the EdOrg-only relationship-based authorization strategies for the GET-many scenario, plus the shared authorization subquery framework, per:

- `reference/design/backend-redesign/design-docs/auth.md`

This ticket delivers the complete authorization subquery pipeline (SQL generation, caching, pagination, TVP threshold, OR semantics, inverted strategies) proven end-to-end with the simpler EdOrg case. People-involved strategies are handled in [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).

## Acceptance Criteria

### EdOrg-only strategies

- The following relationship-based strategies are implemented for GET-many:
  - RelationshipsWithEdOrgsOnly — includes only EducationOrganization securable elements.
  - RelationshipsWithEdOrgsOnlyInverted — swaps the Source/Target filtering in the auth.EducationOrganizationIdToEducationOrganizationId table (bottom-to-top instead of top-to-bottom).
- GET-many results are filtered based on the configured strategy; unauthorized resources are never returned.

### Shared authorization subquery framework

- Authorization subqueries filter the auth views/table using the EdOrgIds from the client's token.
- When multiple relationship-based strategies are configured for the same resource, they are combined with OR semantics.
- No duplicate results are returned (uses IN subquery approach, not JOIN).
- Pagination (offset/limit) and total count work correctly with the authorization filter applied.
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

  3. EdOrg-only strategy but no EdOrg securable elements: return a 500 security configuration error. ODS throws SecurityConfigurationException when a relationship strategy produces no
     authorization subjects; silently returning all rows is unsafe, and returning no rows hides bad metadata.

  4. Child collection EdOrg columns: do not state that EdOrg securables are always root-table columns. The design says EdOrg/Namespace columns live on whichever table owns the reference. For DMS-1055, implement child-table EdOrg filtering with an EXISTS/IN semi-join back to root DocumentId.

  5. Multiple EdOrg securable elements in one strategy: yes, AND them. ODS builds one relationship strategy as a conjunction of its filters, and the DMS design says the token must have access to all securable elements.

  6. Normal plus inverted on the same resource: yes, OR them as separate relationship strategies, even if they target the same securable column. The inverted strategy is not redundant; it swaps
     SourceEducationOrganizationId and TargetEducationOrganizationId.

  7. SQL parameter contract:

  Use ClaimEducationOrganizationIds as the logical auth-list parameter name, matching ODS terminology.

  For RelationshipsWithEdOrgsOnly:

  authEdOrg.SourceEducationOrganizationId IN/ANY token EdOrg IDs
  AND authEdOrg.TargetEducationOrganizationId = resource EdOrg column

  For RelationshipsWithEdOrgsOnlyInverted:

  authEdOrg.TargetEducationOrganizationId IN/ANY token EdOrg IDs
  AND authEdOrg.SourceEducationOrganizationId = resource EdOrg column

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

  Treat this as a security configuration failure, not as an empty result and not as a silent skip. ODS throws a security configuration exception when a relationship strategy produces no authorization subjects /home/brad/work/ods/Ed-Fi-ODS/Application/EdFi.Ods.Api/Security/AuthorizationStrategies/Relationships/RelationshipsAuthorizationStrategyBase.cs:74. DMS should fail fast with resource, strategy, and securable element
  details in logs/error text.

  If one path cannot resolve but another valid path for the same securable can, use the valid shortest/canonical path. If none resolve, fail.

  12. ProblemDetails:

  No new ProblemDetails shape is needed for normal GET-many denial. Unauthorized rows are filtered out; the response is 200 with an authorized page, possibly empty, and total count over authorized rows.

  Use existing failure paths for exceptional cases:

  - Empty EdOrg token list: keep current DMS behavior, a 403 from the auth filter provider.
  - Unsupported/mixed strategies outside DMS-1055 scope: fail fast, likely current 501 not implemented.
  - Invalid security configuration/path resolution failure: server/configuration failure, likely 500.

  13. Acceptance tests:

  Minimum acceptance set:

  - Normal EdOrg filtering returns only rows whose resource EdOrg is reachable from token EdOrg.
  - Inverted filtering swaps Source/Target and returns bottom-to-top authorized rows.
  - Normal plus inverted relationship strategies are ORed.
  - Multiple EdOrg securable elements within one strategy are ANDed, matching current DMS single-item behavior.
  - Duplicate avoidance: one document returned once when multiple auth rows or strategies match.
  - Pagination and totalCount are applied after authorization filtering.
  - Empty token EdOrg IDs produces the existing 403.
  - SQL Server 1,999 unique IDs uses expanded scalar params; 2,000 unique IDs uses dms.BigIntTable.
  - Duplicate token IDs are deduped before threshold selection.
  - PostgreSQL uses a single array parameter.
  - Unsupported mixed strategies such as NamespaceBased, ownership, custom view, People strategies, and mixed NoFurtherAuthorizationRequired plus relationship fail fast unless a later story explicitly defines their semantics.
  - Unresolvable/no EdOrg securable path fails as configuration error.


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
