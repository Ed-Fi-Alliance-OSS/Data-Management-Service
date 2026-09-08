---
jira: DMS-1410
jira_url: https://edfi.atlassian.net/browse/DMS-1410
---

# Story: Implement Ownership-based Authorization for GET-many

## Description

Implement ownership-based authorization for GET-many per
[`reference/design/backend-redesign/design-docs/auth.md`](../../design-docs/auth.md).
`OwnershipTokenIds` comes from the tenant-qualified CMS `ApplicationContext`.
GET-many does not consume `CreatorOwnershipTokenId` or JWT ownership claims.
CMS limits assignments to 1,999 ownership tokens; DMS defensively fails at 2,000 or more.

## Acceptance Criteria

- Filter against `dms.Document.CreatedByOwnershipTokenId` using the API client's `OwnershipTokenIds`.
- Exclude null and nonmatching ownership values without returning a 403.
- Apply authorization before pagination and total-count calculation.
- Treat an empty ownership-token list as an empty page and `totalCount = 0` when requested.
- Apply Ownership-based as an AND filter with Namespace-based, custom view-based, and the relationship-strategy OR group.
- Execute Ownership-based last among AND strategies.
- Replace the temporary `DMS-1055` 501 for supported mixed configurations containing Ownership-based.
- Preserve duplicate-free query results.
- Support PostgreSQL and SQL Server; use parameterized SQL Server `IN` below 2,000 tokens and fail at 2,000 or more without a TVP.
- Cover ownership-only, mixed-strategy, null-token, empty-token-list, pagination/count, parameter-limit, and both-provider cases.

## Implementation Notes

Landed on branch `DMS-1410` in three steps so that no commit ever let `OwnershipBased` GET-many stop
returning 501 before the repository actually applied the filter: additive compiler and budget primitives
first, then planner enablement and repository consumption in one commit, then provider integration fixtures.

- **Planning.** `RelationalAuthorizationPlanner` gates GET-many ownership with `EnforcesOwnershipPageFilter`
  (ReadMany over relationally stored resources) and hands the repository a `PageOwnershipFilterSpec` on
  `Plan.OwnershipPageFilter`. The gate is disjoint from DMS-1060's `EnforcesOwnershipChecks` (ReadSingle,
  Update, Delete), so the single-record `OwnershipAuthorizationPlanner` is never asked to plan ReadMany.
  Descriptor GET-many keeps the DMS-1060 501 carve-out, and tracked changes are unchanged.
- **Compilation.** `PageDocumentIdSqlCompiler` joins `dms.Document` once under alias `doc`, shared with the
  `?id=` predicate, and emits `doc.CreatedByOwnershipTokenId IS NOT NULL AND <membership>` after the value
  predicates and the namespace and custom-view filters, ahead of the relationship OR group and the cursor
  bounds. Membership is `= ANY(@ownershipTokenIds)` bound as `smallint[]` on PostgreSQL and a scalar
  `IN (@ownershipTokenIds_0, …)` list on SQL Server. Page and total-count SQL share the WHERE clause, so the
  count is the owned total.
- **Repository.** `ComposePageQueryAuthorization` is the fail-closed choke point: a planned filter with no
  parameterization throws instead of selecting every row. An empty token list short-circuits to an empty page
  with `totalCount = 0` and `SelectionSkipped`, but only after namespace, custom-view, and relationship
  planning produced non-terminals and after every resolved custom view was validated. At 2,000 or more tokens
  the planner's `OwnershipTokenCapExceeded` terminal becomes the
  `OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded` 500 on both providers, again after
  custom-view validation. The SQL Server parameter budget counts the token list alongside namespace prefixes
  and claim EdOrg ids, reported through the four-part `CommandParameterCapExceeded` message.
- **Partitions and cursors.** `QueryPartitions` resolves authorization through the same path, so boundary
  starts are cut from the ownership-filtered candidate relation and agree with bounded cursor pages. The
  DMS-1392 cursor-bound auth-view compiler behavior is preserved and pinned by tests.
- **Tests.** Compiler (`PageDocumentIdSqlCompilerOwnershipTests`), planner, and repository unit tests;
  PostgreSQL and SQL Server backend integration fixtures (`*RelationalOwnershipQueryAuthorizationTests`)
  covering token match and null exclusion, multi-token callers, paging window and count, composition with the
  relationship OR group, namespace, and custom views, duplicate-free roots, cursor pages, partition boundaries
  and their agreement with cursor pages, the 2,000-token cap, and 1,999 tokens on both providers; the
  API-level `OwnershipAuthorizationIntegrationScenario`; and the E2E scenario "Mixed strategies with
  OwnershipBased filter GET-many by the client's ownership tokens", flipped from 501 to 200 with an empty
  body. No positive E2E coverage was added, by decision.
