# DMS-1338 — Add paging/filtering support to `GET /v3/actions` and `GET /v3/authorizationStrategies`

**Implementation specification (Configuration Service / CMS)**

---

## 1. Document control

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1338-actions-authorization-strategies-paging.md` |
| Jira ticket | DMS-1338 — *Add paging/filtering support to /v3/actions and /v3/authorizationStrategies* (Status: Open, fix version Ed-Fi API v8.1, epic DMS-1072 *Admin API v2.3 / CMS Gap Remediation*, label `no-blockers`) |
| Ticket comments at time of writing | None (verified via Jira API, 2026-09-11) |
| Branch | `DMS-1338` (existing; head `7d3f31c7b` = current `main`) |
| Author | Samuel Lugo (with automated repository audit) |
| Date | 2026-09-11 |
| **Status** | **Approved — 2026-09-11.** R1 was challenged and approved the same day with all seven §12 questions resolved (see revision note R2). Committed as C00 before any code. |
| Revision | R2 — spec-only touch-up requested at approval: status marked approved, §12 converted from open questions to resolved decisions, and test-plan wording loosened so no test depends on the ordering of the validator's set-built "Allowed values" text |
| Pre-existing repo design doc for DMS-1338 | None (`grep -rli "DMS-1338" reference docs` → none) |

> **Control document.** Until marked **Approved**, no production code or test code is edited and nothing is committed. After approval, work proceeds phase-by-phase and step-by-step; each step ends in a local commit followed by a stop for review (SHA, behavior summary, files, tests run, residual risk). Nothing is pushed until explicitly approved.

**Revision note (R2, 2026-09-11).** The architect challenge approved R1 and resolved every question in §12: OQ-1 parameter taxonomy (`urn:ed-fi:api:bad-request:parameter`; the ticket's `ForDataValidation` wording is stale); OQ-2 actions `orderBy` = `id, name`; OQ-3 pass-through when no query parameters are supplied; OQ-4 direction without `orderBy` sorts by `id`, `name` filter is exact and case-insensitive, string sorts use `OrdinalIgnoreCase` with `ThenBy(Id)`; OQ-5 module-level implementation with no `IClaimSetRepository` or backend change; OQ-6 no CMS E2E feature; OQ-7 no draft-spec defaults. The only other change is a test-plan wording fix: the shared validator builds its "Allowed values: …" text from a `HashSet`, so tests assert the shared parameter-validation envelope, the absence of any client echo, and stable message content such as `'orderBy' is not a valid field` (plus expected field names where useful), never an exact full-message ordering.

**Evidence tags:** **[JIRA]** verified in the ticket · **[SPEC]** verified in the merged Management API 3.0.0 YAML · **[REPO]** verified in the repository at `7d3f31c7b` · **[INFER]** inference · **[REC]** author recommendation · **[OQ-n]** decision in §12 (resolved at approval).

---

## 2. Objective

Make the two CMS reference-list endpoints honor the query parameters the Management API 3.0.0 draft spec advertises for them, using only CMS's existing shared paging infrastructure, with no behavior change for callers that pass no parameters. **[JIRA]**

Ticket framing to preserve: this is *contract consistency*, not a functional pagination feature. The lists are ~5 actions and ~13 out-of-the-box authorization strategies plus any custom ones. **[JIRA]**

---

## 3. Authoritative sources

1. **DMS-1338 description and acceptance criteria** (quoted verbatim in §4). **[JIRA]**
2. **Merged Management API 3.0.0 spec**, `api-specifications/management/management-api-3.0.0.yaml` on `main` of `Ed-Fi-Alliance-OSS/Ed-Fi-API-Specifications` (merged via PR 54). Relevant excerpts in §5. **[SPEC]**
3. **Current repository behavior** at `7d3f31c7b` (§6). **[REPO]**

---

## 4. Ticket text (verbatim) and AC traceability

### 4.1 "Required change" (verbatim) **[JIRA]**

> Reuse CMS's existing shared paging infrastructure (PagingQuery, PagingQueryValidators, the IsDescending/OrderByColumn-equivalent pattern already used by VendorRepository and other collection endpoints) rather than building new pagination logic. This should be a small, mechanical wiring change, not new infrastructure.
>
> * GET /v3/actions: bind offset, limit, orderBy, direction, id, name. Allowed orderBy values: id.
> * GET /v3/authorizationStrategies: bind offset, limit, orderBy, direction.
> * Both: omitting all parameters returns the full list unchanged (no breaking change for existing callers)
> * Both: an invalid orderBy or direction value returns CMS's existing 400 validation response pattern (FailureResponse.ForDataValidation), matching other collection endpoints.

### 4.2 Acceptance criteria (verbatim) **[JIRA]**

1. GET /v3/actions accepts offset, limit, orderBy (id/name), direction, id, name and applies them correctly.
2. GET /v3/authorizationStrategies accepts offset, limit, orderBy (id/name/displayName), direction and applies them correctly.
3. Omitting all query parameters on either endpoint returns the full list
4. An unsupported orderBy value or invalid direction returns CMS's standard 400 validation response, matching the pattern used by other collection endpoints (e.g., vendors).
5. Both endpoints reuse the existing shared PagingQuery/validator infrastructure rather than introducing new pagination logic.
6. The OpenAPI schema/Swagger metadata for both endpoints documents the supported query parameters.
7. Unit and endpoint tests added for both endpoints covering: default (no params), offset/limit, each supported orderBy value with both directions, and invalid orderBy/direction returning 400.

### 4.3 Traceability matrix

| AC | How it is satisfied | Where (phase.step) | Verified by |
|---|---|---|---|
| AC1 | `FrontendActionQuery` binds `offset`, `limit`, `orderBy`, `direction`, `id`, `name`; `ActionPagingQueryValidator` allows `orderBy ∈ {id, name}`; `ActionsModule` filters by `id`/`name`, sorts by `orderBy`+`direction`, applies `offset`/`limit` | 1.1, 1.2, 1.3, 2.1 | T-A2…T-A9 (endpoint), T-V1…T-V6 (validator) |
| AC2 | `FrontendAuthorizationStrategyQuery` binds `offset`, `limit`, `orderBy`, `direction`; `AuthorizationStrategyPagingQueryValidator` allows `orderBy ∈ {id, name, displayName}`; `AuthorizationStrategiesModule` sorts and pages | 1.1, 1.2, 1.3, 3.1 | T-S2…T-S8, T-V7…T-V12 |
| AC3 | Request with no query string → repository list returned as-is (no sort, no filter, no paging applied). Both existing "full list" tests kept unchanged and still pass | 2.1, 3.1 | Existing `Given_valid_token_and_role` in both test files + T-A1, T-S1 |
| AC4 | Validation goes through the shared `PagingQueryValidator<T>` + `GuardAsync` → `ParameterValidationException` → `GlobalExceptionHandler` → `FailureResponse.ForParameterValidation` (400, `type: urn:ed-fi:api:bad-request:parameter`). This is *exactly* what `/v3/vendors` returns today **[REPO]**. See **[OQ-1]** on the ticket's `ForDataValidation` wording | 1.3, 2.1, 3.1 | T-A10…T-A13, T-S9…T-S12 assert status 400, `type`, `title`, and the shared fixed messages |
| AC5 | No new infrastructure: new query DTOs subclass `FrontendPagingQuery`; new validators subclass `PagingQueryValidator<T>` with an allowed-field set; sorting uses `PagingQuery.IsDescending`; no new base classes, extension methods, or helpers outside the two modules. Validators are DI-registered automatically by the existing `AddValidatorsFromAssembly` scan **[REPO]** | 1.1–1.3 | Code review; §9 file list shows only additive edits to the three existing shared files |
| AC6 | `[AsParameters]` DTO with `[FromQuery(Name=…)]` + `[Description]` on every parameter. The built-in OpenAPI generator already emits these for `/v3/profiles` (proven by `OpenApi_Profile_Collection_Endpoint_Exposes_Filter_Params`) **[REPO]**. No MetadataModule change needed | 1.2, 4.1 | T-M1 (both paths added to the shared paging-params assertion), T-M2 (actions `id`/`name` filter params) |
| AC7 | Endpoint tests per endpoint: no params; offset only; limit only; offset+limit; each orderBy value × {asc, desc}; invalid orderBy → 400; invalid direction → 400. Validator unit tests per validator | 1.4, 2.2, 3.2, 4.1 | Test IDs in §10 |

---

## 5. Spec facts (Management API 3.0.0, merged YAML) **[SPEC]**

| Path | Declared query parameters | Response item schema |
|---|---|---|
| `GET /v3/actions` | `offset` (int32, default '0'), `limit` (int32, default '25'), `orderBy` (string, default ''), `direction` (enum Ascending/Descending, default Descending), `id` (int32, "Action id"), `name` (string, "Action name") | `actionModel { id:int32, name:string?, uri:string? }` |
| `GET /v3/authorizationStrategies` | `offset`, `limit`, `orderBy`, `direction` (same shapes as above). **No `id`/`name` filter params.** | `authorizationStrategyModel { id:int32, name:string?, displayName:string? }` |

Notes:

- The spec's `default: '25'` for `limit` and `default: Descending` for `direction` are **not** implemented by any CMS collection endpoint today; `PagingQuery.BuildPagingClause` is explicitly documented as applying *no implicit row cap* **[REPO]**. This ticket follows CMS convention (no defaults), which is also required by AC3. See §7 Non-goals.
- The spec's `direction` enum is `Ascending`/`Descending`; CMS accepts `asc|ascending|desc|descending` case-insensitively on every endpoint **[REPO]**. Same convention is reused here (superset of the spec).

---

## 6. Current repository state **[REPO]**

### 6.1 Endpoints

- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ActionsModule.cs`
  `GetUserActions(IClaimSetRepository repository)` → `Results.Ok(repository.GetActions())`. No query binding, no validator.
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/AuthorizationStrategiesModule.cs`
  `GetAuthorizationStrategies(IClaimSetRepository, HttpContext)` → `Results.Json(success.AuthorizationStrategy)` or `FailureResults.Unknown(traceId)` on `FailureUnknown`. No query binding, no validator.

### 6.2 Data source for each list

- `IClaimSetRepository.GetActions()` returns a **hard-coded in-memory array** of 5 `Action { Id, Name, Uri }` in Id order (1 Create, 2 Read, 3 Update, 4 Delete, 5 ReadChanges). Identical in the PostgreSQL and MSSQL repositories.
- `IClaimSetRepository.GetAuthorizationStrategies()` runs `SELECT Id, AuthorizationStrategyName, DisplayName FROM dmscs.AuthorizationStrategy WHERE <tenant clause>` with **no `ORDER BY`** (both dialects), so today's order is storage order (undefined by SQL, insertion order in practice).
- Both methods have other callers that must keep working unchanged: `ClaimSetDataProvider` (PG + MSSQL) and `ResourceClaimRepository` (PG + MSSQL) and the integration tests `DeployTests`, `DatabaseShapeTests`, `ResourceClaimRepositoryTests` (PG + MSSQL). 15 test-side references exist across 6 files.

### 6.3 Shared paging infrastructure (to be reused, not modified in behavior)

- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/PagingQuery.cs` — `Offset`, `Limit`, `OrderBy`, `Direction`, `IsDescending`, SQL clause builders.
- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/EntityQueries.cs` — one `XxxQuery : PagingQuery` per collection (e.g. `ProfileQuery { Id, Name }`, `OwnershipTokenQuery : PagingQuery;`).
- `src/config/frontend/.../Models/FrontendQueryModels.cs` — `FrontendPagingQuery` (binding metadata + `[Description]` on offset/limit/orderBy/direction) and one `FrontendXxxQuery : FrontendPagingQuery` per collection with `[FromQuery]`+`[Description]` filters and `ToQuery()` via `ApplyPagingTo`.
- `src/config/frontend/.../Infrastructure/PagingQueryValidators.cs` — `PagingQueryValidator<T>` (offset ≥ 0, limit > 0, direction ∈ set, orderBy ∈ allowed set; fixed non-echoing messages) + `GuardAsync` throwing `ParameterValidationException`; one `XxxPagingQueryValidator` per collection. All are discovered by `AddValidatorsFromAssembly` in `WebApplicationBuilderExtensions.cs:54`.
- `GlobalExceptionHandler.cs:42` maps `ParameterValidationException` → `FailureResponse.ForParameterValidation(...)` = 400, `type: urn:ed-fi:api:bad-request:parameter`, `title: "Parameter Validation Failed"`, `detail: "Parameter validation failed. See 'errors' for details."`, `errors: [messages]`.

### 6.4 Existing in-memory sort/page precedents (the "IsDescending/orderBy-column pattern" applied to lists)

- `ProfileRepository.ApplyOrdering` (PG + MSSQL): `switch (orderBy ?? "id")` with `query.IsDescending` choosing `OrderBy`/`OrderByDescending`, then `.Skip(offset ?? 0)` and conditional `.Take(limit)`.
- `ResourceClaimRepository.SortAndPage` / `ApplyPaging<T>` (PG + MSSQL): same shape, `StringComparer.OrdinalIgnoreCase` for names, `Skip` only when offset has a value.
- SQL-backed repositories (Vendor, ClaimSet, …) default to `ORDER BY Id ASC` when `orderBy` is absent.

### 6.5 Existing tests touching these endpoints

- `...Tests.Unit/Modules/ActionModuleTests.cs` — `RegisterActionEndpointTests.When_Making_Action_Request`: 200 + full 5-item list (uses the **real** repository; `GetActions` needs no DB), 401 without auth, 403 with wrong role.
- `...Tests.Unit/Modules/AuthorizationStrategiesModuleTests.cs` — same three tests with a **faked** `IClaimSetRepository` returning 4 strategies.
- `...Tests.Unit/Modules/MetadataModuleTests.cs`:
  - `OpenApi_Registers_Actions_And_AuthorizationStrategies_As_Collection_Routes` asserts only route presence (comment notes these GETs currently have no parameters).
  - `OpenApi_Collection_Endpoints_Expose_Paging_And_Sort_Params` iterates a `collectionEndpoints` list (13 paths, **not** including these two) and asserts `offset/limit/orderBy/direction` exist with descriptions, integer schema, `limit.minimum == 1`, and a direction description mentioning asc/desc.
  - `OpenApi_Profile_Collection_Endpoint_Exposes_Filter_Params` is the template for asserting filter params (`id` integer, `name` string).
- No CMS E2E feature covers `/v3/actions`; `ResourceClaims.feature` only references the `authorizationStrategiesForActions` property, not the endpoint.

---

## 7. Scope boundaries and non-goals

**In scope**

- Query binding, validation, in-memory filter/sort/page for the two GET endpoints.
- New DTO/query/validator entries in the three existing shared files.
- OpenAPI parameter metadata via attributes on the DTOs.
- Unit (validator) and endpoint (WebApplicationFactory) tests; MetadataModuleTests extensions.

**Explicit non-goals (will not be done in this ticket)**

- N1. No change to `IClaimSetRepository` signatures or to any backend repository, `ClaimSetDataProvider`, or `ResourceClaimRepository` (see §8.1 for why). No backend integration tests are added or changed.
- N2. No implicit default `limit` (spec says 25) and no default `direction` (spec says Descending). Matches every other CMS collection endpoint and AC3. **[REC]**
- N3. No `id`/`name` filters on `/v3/authorizationStrategies` (not in the spec; not in AC2). **[SPEC]**
- N4. No `.Produces<T>()`/response-schema documentation for these endpoints (existing comment in MetadataModuleTests explains the current state; out of ticket scope).
- N5. No change to the JSON shape of `Action` or `AuthorizationStrategy` items.
- N6. No new CMS E2E `.feature`; AC7 asks for unit and endpoint tests, which the WebApplicationFactory tests are. E2E can be added later if the reviewer asks. **[OQ-6]**
- N7. No refactor of existing modules/validators/tests; no formatting churn beyond CSharpier on touched files.
- N8. No changes to the `PagingQueryValidator<T>` base, `FrontendPagingQuery`, `PagingQuery`, or `GlobalExceptionHandler`.
- N9. No change to DMS API (`src/dms`).

---

## 8. Design

### 8.1 Where the filter/sort/page logic lives — module-level (frontend) **[REC]**

Two options were weighed:

| | **Option A — apply in the frontend module (recommended)** | Option B — push into `IClaimSetRepository` |
|---|---|---|
| Interface change | None | `GetActions(ActionQuery)` / `GetAuthorizationStrategies(AuthorizationStrategyQuery)` (or overloads) |
| Files touched | 2 modules + 3 shared files + tests | + `IClaimSetRepository`, PG + MSSQL `ClaimSetRepository`, PG + MSSQL `ClaimSetDataProvider`, PG + MSSQL `ResourceClaimRepository`, 4 integration-test files, `ClaimSetModuleTests` fakes |
| Logic duplication | One implementation | Two dialect copies (repo already tolerates this in `ResourceClaimRepository`, but it is a known smell) |
| Test lanes required | Frontend unit project only | + PG and MSSQL backend integration lanes (MSSQL is not always available locally; see memory notes) |
| Matches ticket wording | "small, mechanical wiring change" ✔; reuses `PagingQuery`/validators/`IsDescending` ✔ | "IsDescending/OrderByColumn pattern used by VendorRepository" read literally (SQL) ✔, but not "small" |
| Behavior for actions (hard-coded list) | Natural: it's already an in-memory array | Repository would still do it in memory |
| Behavior for auth strategies (≤ ~20 rows) | In-memory sort/page after the existing SQL — identical result set | SQL `ORDER BY` + `LIMIT/OFFSET` (PG) / `OFFSET … FETCH` (MSSQL) |

**Decision (proposed):** Option A. The lists are tiny by the ticket's own framing, Option A has the smallest blast radius (no interface break for 4 production callers and 6 test files), it needs exactly one implementation and one test project, and it still reuses every piece of shared infrastructure the ticket names. `ProfileRepository` and `ResourceClaimRepository` already establish the in-memory `IsDescending` switch as an accepted CMS pattern. **[OQ-5]** invites the reviewer to overrule this.

### 8.2 Request pipeline (both endpoints)

```
bind [AsParameters] FrontendXxxQuery
  → validator.GuardAsync(query)            // shared; throws ParameterValidationException → 400 (parameter taxonomy)
  → repository call (unchanged signature)  // actions: in-memory; strategies: existing SQL; FailureUnknown → 500 unchanged
  → if query.HasNoQueryParameters: return list as-is                       // AC3, exact pass-through
  → filter (actions only: id exact; name exact, OrdinalIgnoreCase)         // [OQ-4]
  → sort   (orderBy ?? "id", IsDescending; string keys OrdinalIgnoreCase; ThenBy Id as deterministic tiebreaker)
  → page   (Skip(offset) only if offset.HasValue; Take(limit) only if limit.HasValue)
  → Results.Ok(list)
```

Rules:

- R1. **Pass-through when nothing is supplied.** "Nothing supplied" = all bound properties null (`Offset`, `Limit`, `OrderBy`, `Direction`, and for actions `Id`, `Name`). The repository result is returned without re-ordering, filtering, or materializing changes. This is the strictest reading of AC3 and preserves today's byte-for-byte order for authorization strategies, whose SQL has no `ORDER BY`. **[OQ-3]**
- R2. **Default sort key is `id` ascending** whenever any parameter is present but `orderBy` is absent (matches `ProfileRepository.ApplyOrdering` and the SQL builders' `ORDER BY Id ASC` default). `direction` without `orderBy` therefore sorts by `id` in the requested direction (Profile behavior). **[OQ-4]**
- R3. **Order of operations is filter → sort → page.** `offset`/`limit` apply to the filtered, sorted set (same as SQL semantics in the other repositories).
- R4. **Sorting is stable and deterministic:** string keys use `StringComparer.OrdinalIgnoreCase` (matches `ResourceClaimRepository.SortAndPage`) with `.ThenBy(x => x.Id)` so equal or null keys (e.g. a null `displayName`) page deterministically. Null `displayName` sorts first ascending / last descending (LINQ default). **[INFER]**
- R5. **Filters are exact matches**: `id` → `Id == id`; `name` → `string.Equals(Name, name, OrdinalIgnoreCase)`. Case-insensitivity mirrors how `ResourceClaimRepository` keys action names (`StringComparer.OrdinalIgnoreCase`) and MSSQL's default collation for the other endpoints' `=` filters. A filter that matches nothing returns `200 []` (same as other collections). **[OQ-4]**
- R6. **No implicit limit** (N2). `offset` beyond the end returns `200 []`.
- R7. **Validation precedes the repository call**, so an invalid query never touches the database (same order as `ProfileModule.GetAll`).
- R8. **Unknown repository failure** for authorization strategies still returns `FailureResults.Unknown` (500) regardless of query parameters.
- R9. **Binding failures** (`?id=abc`, `?offset=abc`, `?limit=abc`) surface as `BadHttpRequestException` → 400 via the existing `MapBadHttpRequest` arm, identical to `/v3/vendors?offset=abc` today. No new code.

### 8.3 Proposed data/query model changes

`src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/EntityQueries.cs` (append, keep file ordering style):

```csharp
public class ActionQuery : PagingQuery
{
    public int? Id { get; set; }

    public string? Name { get; set; }
}

public class AuthorizationStrategyQuery : PagingQuery;
```

`src/config/frontend/.../Models/FrontendQueryModels.cs` (append):

```csharp
public class FrontendActionQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    [Description("Filter actions by identifier.")]
    public int? Id { get; set; }

    [FromQuery(Name = "name")]
    [Description("Filter actions by name.")]
    public string? Name { get; set; }

    public ActionQuery ToQuery() => ApplyPagingTo(new ActionQuery { Id = Id, Name = Name });
}

public class FrontendAuthorizationStrategyQuery : FrontendPagingQuery
{
    public AuthorizationStrategyQuery ToQuery() => ApplyPagingTo(new AuthorizationStrategyQuery());
}
```

`src/config/frontend/.../Infrastructure/PagingQueryValidators.cs` (append):

```csharp
public class ActionPagingQueryValidator : PagingQueryValidator<FrontendActionQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "name" };

    public ActionPagingQueryValidator() : base(AllowedFields) { }
}

public class AuthorizationStrategyPagingQueryValidator
    : PagingQueryValidator<FrontendAuthorizationStrategyQuery>
{
    private static readonly IReadOnlySet<string> AllowedFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "name", "displayName" };

    public AuthorizationStrategyPagingQueryValidator() : base(AllowedFields) { }
}
```

Rationale for the data-model `ActionQuery`/`AuthorizationStrategyQuery` even though logic stays in the frontend: every existing `FrontendXxxQuery` has a `ToQuery()` to a data-model type (even the empty `OwnershipTokenQuery`), so following it keeps the pattern uniform and leaves the door open to Option B later at zero cost. The modules operate on the data-model query (`query.ToQuery()`), so the sort/page helpers depend only on `PagingQuery` members. **[REC]**

### 8.4 Endpoint behavior — `GET /v3/actions`

Handler (shape, not final code):

```csharp
private static async Task<IResult> GetUserActions(
    IClaimSetRepository repository,
    [AsParameters] FrontendActionQuery query,
    ActionPagingQueryValidator validator
)
{
    await validator.GuardAsync(query);
    IEnumerable<Action> actions = repository.GetActions();
    return Results.Ok(ApplyQuery(actions, query.ToQuery()));   // ApplyQuery = private static; pass-through when empty
}
```

- Allowed `orderBy`: `id`, `name` (case-insensitive). Ordering by `uri` is **not** offered (not in AC). Sort keys: `id` → `Id`; `name` → `Name`.
- Filters: `id`, `name` per R5.
- 400 cases: `orderBy` not in {id,name}; `direction` not in {asc,ascending,desc,descending}; `offset < 0`; `limit < 1`; non-numeric `id`/`offset`/`limit` (binding).

### 8.5 Endpoint behavior — `GET /v3/authorizationStrategies`

```csharp
public static async Task<IResult> GetAuthorizationStrategies(
    IClaimSetRepository claimSetRepository,
    [AsParameters] FrontendAuthorizationStrategyQuery query,
    AuthorizationStrategyPagingQueryValidator validator,
    HttpContext httpContext
)
{
    await validator.GuardAsync(query);
    AuthorizationStrategyGetResult result = await claimSetRepository.GetAuthorizationStrategies();
    return result switch
    {
        AuthorizationStrategyGetResult.Success success =>
            Results.Json(ApplyQuery(success.AuthorizationStrategy, query.ToQuery())),
        _ => FailureResults.Unknown(httpContext.TraceIdentifier),
    };
}
```

- Allowed `orderBy`: `id`, `name`, `displayName`. Sort keys: `id` → `Id`; `name` → `AuthorizationStrategyName` (serialized as `name`); `displayName` → `DisplayName` (nullable, R4).
- No filters (N3).
- Keep `Results.Json(...)` (current serializer path) so the item JSON is unchanged (N5).
- 400 cases: invalid `orderBy`/`direction`; `offset < 0`; `limit < 1`; non-numeric `offset`/`limit`.

### 8.6 Response/error behavior summary

| Situation | Status | Body |
|---|---|---|
| No query string | 200 | Full list, as today |
| Valid paging/sort (and filters for actions) | 200 | JSON array (possibly empty) |
| Invalid `orderBy`, `direction`, `offset < 0`, `limit < 1` | 400 | `FailureResponse.ForParameterValidation`: `type: urn:ed-fi:api:bad-request:parameter`, `title: Parameter Validation Failed`, `errors` carrying the existing fixed messages, e.g. one starting `'orderBy' is not a valid field` followed by the allowed field names (set-built, so ordering is not contractual); client values are never echoed |
| Non-numeric `id`/`offset`/`limit` | 400 | Existing `MapBadHttpRequest` shaping (same as vendors) |
| Missing/invalid token or role | 401 / 403 | Unchanged (`MapSecuredGet` policies) |
| Repository `FailureUnknown` (strategies) | 500 | `FailureResults.Unknown(traceId)`, unchanged |

### 8.7 OpenAPI metadata approach

No production code beyond the DTO attributes in §8.3. The built-in ASP.NET Core OpenAPI generator (`/openapi/v1.json`, consumed by `/metadata/specifications`) reads `[AsParameters]` + `[FromQuery(Name)]` + `[Description]` + `[Range]` and already produces, for `/v3/profiles`, parameters with descriptions, `integer` schemas for offset/limit/id, `minimum: 1` on limit, and a `direction` description containing "asc"/"desc" **[REPO]**. Adding the two new DTOs yields the same for both new paths. Verified by extending MetadataModuleTests (§10, T-M1/T-M2). The stale comment in `OpenApi_Registers_Actions_And_AuthorizationStrategies_As_Collection_Routes` ("map collection-only GETs … with no parameters") is updated to stay truthful; the assertion itself is unchanged.

---

## 9. Affected files

| File | Change | Phase.step |
|---|---|---|
| `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/EntityQueries.cs` | + `ActionQuery`, `AuthorizationStrategyQuery` | 1.1 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Models/FrontendQueryModels.cs` | + `FrontendActionQuery`, `FrontendAuthorizationStrategyQuery` | 1.2 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/PagingQueryValidators.cs` | + `ActionPagingQueryValidator`, `AuthorizationStrategyPagingQueryValidator` | 1.3 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/ActionPagingQueryValidatorTests.cs` | new | 1.4 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/AuthorizationStrategyPagingQueryValidatorTests.cs` | new | 1.4 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ActionsModule.cs` | handler binds query, validates, filters/sorts/pages | 2.1 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/ActionModuleTests.cs` | + query fixture(s); existing tests untouched | 2.2 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/AuthorizationStrategiesModule.cs` | handler binds query, validates, sorts/pages | 3.1 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/AuthorizationStrategiesModuleTests.cs` | + query fixture(s); existing tests untouched | 3.2 |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs` | + both paths in `collectionEndpoints`; + actions filter-param test; comment refresh | 4.1 |
| `reference/design/configuration-service/DMS-1338-actions-authorization-strategies-paging.md` | this spec (committed first after approval) | 0 |

Not touched: anything under `src/config/backend/**`, `src/dms/**`, `docs/**`, E2E features, `GlobalExceptionHandler`, `PagingQuery`, `FrontendPagingQuery`, `PagingQueryValidator<T>`.

---

## 10. Phased plan with steps and tests

Each step = one focused local commit, then stop for review. Commit message prefix `[DMS-1338]`.

### Phase 0 — Spec approval

- **0.1** Architect/Codex challenge of this document; resolve §12 open questions; mark **Approved**.
- **0.2** Commit the approved spec (`C00`). No code.

### Phase 1 — Shared query models and validators (no endpoint behavior change yet)

- **1.1** `EntityQueries.cs`: add `ActionQuery`, `AuthorizationStrategyQuery` (§8.3).
- **1.2** `FrontendQueryModels.cs`: add `FrontendActionQuery`, `FrontendAuthorizationStrategyQuery` (§8.3).
- **1.3** `PagingQueryValidators.cs`: add the two validators (§8.3).
- **1.4** Validator unit tests (new files, `[TestFixture]` + `It_…` style mirroring `ResourceClaimPagingQueryValidatorTests`):
  - T-V1 `ActionPagingQueryValidator` allows `id`; T-V2 allows `name`; T-V3 case-insensitive (`NAME`); T-V4 rejects `uri` with a single error whose message contains `'orderBy' is not a valid field`, contains `id` and `name`, and does not contain the client value `uri` (no exact full-message ordering, since the allowed-values text is set-built); T-V5 rejects invalid direction; T-V6 accepts all four direction spellings, rejects `offset=-1`, `limit=0`.
  - T-V7…T-V12 same matrix for `AuthorizationStrategyPagingQueryValidator` with `id`, `name`, `displayName`; rejects `uri`/`authorizationStrategyName`.
  - Optionally add the two DTOs to `Given_PagingQueryValidators.Limit_accepts_large_values_for_*` for parity (small, additive). **[REC]**
  - Steps 1.1–1.4 may be a single commit (they compile only together) or 1.1–1.3 then 1.4; propose **one commit** since none change runtime behavior.
  - Run: validator tests + full frontend unit project (fast).

### Phase 2 — `GET /v3/actions`

- **2.1** `ActionsModule.cs`: handler per §8.4 with private static `ApplyQuery`/sort/page helpers (pattern of `ResourceClaimRepository.SortAndPage`; two small switch expressions, no new shared types).
- **2.2** `ActionModuleTests.cs`: new fixture `When_Making_Action_Request_With_Query_Parameters` (one factory per test via a `SetUpClient()` helper, admin scope header, real repository — as the existing fixture does). Tests:
  - T-A1 no params → 200, 5 items, order `1,2,3,4,5` (strengthens AC3 beyond `BeEquivalentTo`).
  - T-A2 `?offset=2` → ids `3,4,5`; T-A3 `?limit=2` → `1,2`; T-A4 `?offset=1&limit=2` → `2,3`; T-A5 `?offset=10` → `[]`.
  - T-A6 `?orderBy=id&direction=asc` → `1..5`; T-A7 `?orderBy=id&direction=desc` → `5..1`.
  - T-A8 `?orderBy=name&direction=asc` → `Create, Delete, Read, ReadChanges, Update`; T-A9 `?orderBy=name&direction=descending` → reverse.
  - T-A10 `?id=3` → single `Update`; T-A11 `?name=read` → single `Read` (case-insensitive exact; does not match `ReadChanges`); `?name=nomatch` → `[]`.
  - T-A12 `?orderBy=uri` → 400, body `type` ends `:bad-request:parameter`, `title == "Parameter Validation Failed"`, `errors` has one entry containing `'orderBy' is not a valid field` and not containing `uri` (shared envelope + no client echo; no exact "Allowed values" ordering asserted).
  - T-A13 `?direction=sideways` → 400 with the fixed direction message; `?offset=-1` → 400; `?limit=0` → 400; `?id=abc` → 400.
  - T-A14 `?direction=desc` (no orderBy) → sorted by id descending (R2).
  - Existing `Given_valid_token_and_role`, `Given_empty_auth_credentials`, `Given_invalid_client_secret` unchanged.

### Phase 3 — `GET /v3/authorizationStrategies`

- **3.1** `AuthorizationStrategiesModule.cs`: handler per §8.5.
- **3.2** `AuthorizationStrategiesModuleTests.cs`: new fixture with a faked repository returning deliberately unsorted data, e.g. `{Id 3, name "Beta", displayName "Zeta"}, {Id 1, name "Gamma", displayName null}, {Id 2, name "alpha", displayName "Eta"}`:
  - T-S1 no params → 200, items in **repository order** (`3,1,2`) — proves pass-through (R1).
  - T-S2 `?offset=1` → default id sort then skip → `2,3`; T-S3 `?limit=2` → `1,2`; T-S4 `?offset=1&limit=1` → `2`.
  - T-S5 `?orderBy=id` asc/desc; T-S6 `?orderBy=name` asc → `alpha, Beta, Gamma` (case-insensitive), desc reversed; T-S7 `?orderBy=displayName` asc → null first then `Eta, Zeta`, desc reversed (R4).
  - T-S8 `?orderBy=DISPLAYNAME&direction=Ascending` → 200 (case-insensitive orderBy, spec-cased direction).
  - T-S9 `?orderBy=uri` → 400 parameter taxonomy; one error containing `'orderBy' is not a valid field` and `displayName`, not containing `uri` (no exact "Allowed values" ordering asserted); T-S10 `?direction=sideways` → 400; T-S11 `?offset=-1` → 400; T-S12 `?limit=0` → 400.
  - T-S13 repository `FailureUnknown` with `?limit=1` → 500 (R8).
  - T-S14 invalid `orderBy` → repository **not called** (`A.CallTo(...).MustNotHaveHappened()`) (R7).
  - Existing three tests unchanged.

### Phase 4 — OpenAPI metadata tests

- **4.1** `MetadataModuleTests.cs`:
  - T-M1 add `"/v3/actions"` and `"/v3/authorizationStrategies"` to `collectionEndpoints` in `OpenApi_Collection_Endpoints_Expose_Paging_And_Sort_Params`.
  - T-M2 new `OpenApi_Actions_Collection_Endpoint_Exposes_Filter_Params` (clone of the profiles test: `id` integer, `name` string, descriptions present for all six params).
  - T-M3 (assertion, cheap) `/v3/authorizationStrategies` GET declares **no** `id`/`name` parameter (guards N3).
  - Refresh the comment in `OpenApi_Registers_Actions_And_AuthorizationStrategies_As_Collection_Routes`.

### Phase 5 — Verification, formatting, push gate

- **5.1** `dotnet csharpier format` on every touched file (not whole tree — pre-existing drift exists elsewhere).
- **5.2** Full frontend unit project:
  `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj`
  plus `dotnet build src/config/EdFi.DmsConfigurationService.sln` to prove backend projects still compile untouched.
- **5.3** Optional local CMS E2E smoke (`Metadata.feature`, `ResourceClaims.feature`) if the Docker stack is available; not a gate unless the reviewer asks.
- **5.4** Stop; request push approval; push; open PR with AC traceability copied from §4.3.

---

## 11. Regression risks and defensive notes

| # | Risk | Mitigation |
|---|---|---|
| RR1 | Adding `[AsParameters]` binding changes 400 behavior for garbage values (`?id=abc`) that were previously ignored | This is the intended contract (matches vendors, AC4). Covered by T-A13. Documented in §8.6. |
| RR2 | Re-ordering the authorization-strategy list when no params are supplied could alter output order for existing clients | R1 pass-through: no sorting unless a parameter is present. T-S1 asserts repository order is preserved. |
| RR3 | Existing `Given_valid_token_and_role` tests rely on `BeEquivalentTo` (order-insensitive) | Unchanged and still valid; new T-A1/T-S1 add order assertions. |
| RR4 | `MetadataModuleTests` route-presence test comment becomes stale | Comment refreshed in 4.1; assertion unchanged. |
| RR5 | Validator not registered → DI failure at request time | `AddValidatorsFromAssembly(executingAssembly)` scans the frontend assembly; every existing `*PagingQueryValidator` is registered this way. Endpoint tests exercise real DI. |
| RR6 | Sorting `displayName` with nulls throws or is unstable | LINQ `OrderBy` with a `string?` key and `StringComparer.OrdinalIgnoreCase` handles null; `ThenBy(Id)` tiebreak. T-S7 covers null. |
| RR7 | `offset` without `limit` | `Skip` only, no `Take` (same as `ResourceClaimRepository.ApplyPaging`). T-A2/T-S2. |
| RR8 | Materialization: returning a lazy `IEnumerable` from LINQ to `Results.Ok/Json` | Call `.ToList()` before returning so serialization and any exception happen inside the handler. |
| RR9 | Echoing client input in error bodies | Not possible: the shared validator messages are fixed strings; no new messages introduced. |
| RR10 | Other callers of `GetActions`/`GetAuthorizationStrategies` (data provider, resource-claim resolution) | Untouched signatures (N1). Backend solution still compiles (5.2). |
| RR11 | CSharpier whole-tree check fails on unrelated files | Format touched files only; CI CSharpier gate applies to `src/config`, which is what we format. |
| RR12 | `Results.Json` vs `Results.Ok` serializer differences | Keep each module's existing result helper (`Ok` for actions, `Json` for strategies) so item JSON is byte-identical. |

Defensive coding choices: private static pure helpers (no shared state); `switch` on `OrderBy?.ToLowerInvariant()` with `_` default → `Id` (never throws for validated input, and safe even if validation were bypassed); `ToList()` before returning; no string interpolation of client input; no new public API surface.

---

## 12. Resolved decisions (approved 2026-09-11)

> **Approval resolution.** Every item below was raised as an open question in R1 and resolved by the architect challenge on 2026-09-11. In each row the right-hand column is now the **binding decision**; it matched the R1 recommendation in all seven cases. Implementation follows these decisions exactly; any change requires a new spec revision.

| ID | Question (as raised in R1) | Decision |
|---|---|---|
| **OQ-1** | **Error taxonomy wording.** The ticket's "Required change" names `FailureResponse.ForDataValidation` (`urn:ed-fi:api:bad-request:data`). Since DMS-1218 (R5, "binding/paging/parameter classification"), *all* CMS collection endpoints return query-validation failures via `ParameterValidationException` → `FailureResponse.ForParameterValidation` (`urn:ed-fi:api:bad-request:parameter`). AC4 says "matching the pattern used by other collection endpoints (e.g., vendors)". | Follow AC4 and current vendors behavior: **parameter taxonomy** (`urn:ed-fi:api:bad-request:parameter`), zero new code. The `ForDataValidation` mention is stale. Tests assert the `:parameter` type. |
| **OQ-2** | **`orderBy` set for actions.** "Required change" says `id` only; AC1 says `id/name`. | Use **`id`, `name`** (AC wins; superset satisfies both readings; `name` is also a spec-declared filter, so sorting by it is natural). |
| **OQ-3** | **Pass-through vs. always-default-sort when no parameters are supplied.** Other SQL-backed endpoints always `ORDER BY Id ASC`; `ProfileRepository` always sorts by `id`. Strict AC3 ("full list unchanged") favors returning the repository result untouched. | **Pass-through (rule R1).** Zero behavior change for existing callers; sorting by `id` ASC is applied only when any parameter is present. |
| **OQ-4** | Minor semantics: (a) `direction` without `orderBy` → sort by `id` in that direction (Profile behavior) vs. ignore direction (Vendor SQL behavior); (b) `name` filter case-insensitive exact match vs. case-sensitive; (c) string sort comparer `OrdinalIgnoreCase` (ResourceClaim) vs. `Ordinal` (Profile). | (a) honor direction on `id`; (b) case-insensitive exact; (c) `OrdinalIgnoreCase` + `ThenBy(Id)`. All three are low-stakes on 5–20 items; pick once and pin with tests. |
| **OQ-5** | Module-level (Option A) vs. repository-level (Option B) implementation (§8.1). | **Option A.** `IClaimSetRepository` and backend repository signatures are not changed. Option B's cost is listed in §8.1 for the record. |
| **OQ-6** | Is a CMS E2E `.feature` for `/v3/actions` and `/v3/authorizationStrategies` wanted in this ticket? AC7 asks for unit and endpoint tests only. | Not required for this ticket (N6). |
| **OQ-7** | Spec drift to record but not implement: spec `limit` default 25, `direction` default Descending, `direction` enum `Ascending|Descending`. CMS applies no defaults and accepts four spellings on every endpoint. | Do not implement defaults (AC3, CMS convention). Mention in PR description as a known, platform-wide deviation already present for all other endpoints. |

---

## 13. Definition of done

- All AC rows in §4.3 map to passing tests; the full frontend unit project passes locally.
- Backend projects compile untouched; no file under `src/config/backend/**` in the diff.
- CSharpier clean on touched files.
- Each phase/step landed as its own reviewed local commit; pushed only after explicit approval.
- PR description carries the §4.3 traceability matrix and the OQ-1/OQ-7 taxonomy and spec-default notes.
