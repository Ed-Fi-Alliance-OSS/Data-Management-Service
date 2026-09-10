# DMS-1343 — Align `GET /v3/apiClients/{id}` identifier semantics with the Management API 3.0.0 spec

**Implementation specification (Configuration Service / CMS)**

---

## 1. Document control

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1343-apiclient-get-identifier-semantics.md` |
| Jira ticket | [DMS-1343](https://edfi.atlassian.net/browse/DMS-1343) — *Decide and align GET /v3/apiClients/{id} identifier semantics with spec/Admin API* (In Progress) |
| Parent epic | DMS-1072 — Admin API v2.3 / CMS Gap Remediation |
| Branch | `DMS-1343` (created from `main` at `ab140a4d5`) |
| Author | Samuel Lugo (with repository audit) |
| Date | 2026-09-09 |
| **Status** | **Approved — 2026-09-09** (reviewer answers to Q1–Q4 recorded in §11; guardrails in §1.1) |
| Jira decision | Option 1 (replace) is struck through in the ticket. Option 2 (support both identifier styles) is the active direction. |
| Jira comment (2026-08-18) | `dmscs.ApiClient` has no DB-level unique constraint on `ClientId` or `ClientUuid`; recommends the authoritative external identifier receive a unique index. Tracked as follow-up **DMS-1529** (under DMS-1072). |
| Pre-existing design doc | None found for DMS-1343 in `reference/` or `docs/` (verified). |

> **Control document.** No production code is written and no commit is made (including this spec) until the spec is marked **Approved**. After approval, the spec is committed first, then implementation proceeds phase-by-phase with a local commit, SHA, and file summary at every gate.

### 1.1 Review guardrails (binding, 2026-09-09)

- **Identifier rule wording.** Documentation must state the exact rule, never "GET accepts either identifier": *a path segment that is a valid `int32` is the numeric ApiClient `id`; any other segment is the OAuth `clientId` key.*
- **E2E hygiene.** Tear down the CMS E2E stack before setup and again after the scoped E2E run.
- **Scope lock.** `ConfigurationServiceApplicationProvider.cs`, all repositories, DDL, and response DTOs stay untouched unless a later review explicitly approves expanding scope.
- **Gates.** The approved spec is committed alone first (SHA and files reported), then work stops for approval before Phase 1. Every implementation commit is a focused behavior slice with its tests; each gate reports SHA, files edited, and test results, and waits for approval before the next phase.
- **OpenAPI ambiguity note.** The two single-item GET path items (`{id}` and `{clientId}`) remain documented side by side as a deliberate compatibility compromise, because the final runtime behavior includes both routes (§5.2).

**Evidence tags:** **[JIRA]** ticket fact · **[SPEC]** verified in the published Management API 3.0.0 YAML · **[REPO]** verified in the repository at `ab140a4d5` · **[INFER]** inference about framework behavior, verified by a test in the plan · **[REC]** author recommendation.

---

## 2. Problem statement

**[SPEC]** The published Management API 3.0.0 specification (`api-specifications/management/management-api-3.0.0.yaml` in `Ed-Fi-Alliance-OSS/Ed-Fi-API-Specifications`) defines:

```yaml
'/v3/apiClients/{id}':
  get:
    summary: Retrieves a specific apiClient based on the identifier.
    parameters:
      - name: id
        in: path
        required: true
        schema:
          type: integer
          format: int32
    responses: 200 (apiClientModel), 401, 403, 404, 500
```

and `apiClientModel` carries **two** identifiers: `id` (`integer/int32`, the primary key) and `clientId` (`string`, the external OAuth client key).

**[REPO]** CMS today registers only a string route for the single-item GET:

```csharp
// ApiClientModule.cs:73-76
endpoints.MapLimitedAccess("/v3/apiClients/", GetAll).Produces<List<ApiClientResponse>>(200);
endpoints
    .MapLimitedAccess("/v3/apiClients/{clientId}", GetByClientId)
    .Produces<ApiClientResponse>(200);
```

`GetByClientId` (`ApiClientModule.cs:296-313`) calls `IApiClientRepository.GetApiClientByClientId(string)`, which queries `WHERE ac."ClientId" = @ClientId AND <tenant scope>`. A request such as `GET /v3/apiClients/1` therefore looks for a client whose OAuth key is the literal string `"1"` and returns `404`. An Admin-API-conformant caller that passes the numeric `id` cannot read a client.

**[REPO]** The numeric lookup already exists: `GetApiClientById(int id)` is implemented in both the PostgreSQL and MSSQL `ApiClientRepository` with identical tenant scoping (`WHERE ac."Id" = @Id AND <tenant scope>`), and is used by `UpdateApiClient`, `DeleteApiClient`, and `ResetCredential` via `AcquireApiClientLocksAsync` (`ApiClientModule.cs:809, 861`). Nothing new is needed in the backend.

**[REPO]** The client-key route has real consumers that must keep working:

- DMS: `ConfigurationServiceApplicationProvider.FetchApplicationByClientIdAsync` issues `GET /v3/apiClients/{clientId}` with the OAuth key from the bearer token (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceApplicationProvider.cs:59`), using a service account with the limited-access `AuthMetadataReadOnlyAccess` scope. Its unit test pins the path `/v3/apiClients/client-id`.
- CMS E2E: `ApiClients.feature` (scenarios 02, 03, 05, 09, 12, 14, 20, 23), `Applications.feature:960`, `OwnershipTokens.feature:84, 304`, `Tenants.feature:249, 276, 335, 353, 376` all read by key.
- Operator docs: `docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md` §"Provisioning a Client With No Data Store Assignment" tells operators to read the initial client with `GET /v3/apiClients/{key}` to discover its numeric `id`.

---

## 3. Scope

### 3.1 In scope

1. Add a numeric-id single-item GET at `/v3/apiClients/{id:int}` that resolves against `ApiClient.Id` through the existing `GetApiClientById(int)` repository method, tenant scoping included.
2. Keep the existing client-key GET at `/v3/apiClients/{clientId}` unchanged in path, handler semantics, authorization policy, and response shape.
3. Make the served OpenAPI document describe both operations accurately: `GET /v3/apiClients/{id}` with an `int32` `id` parameter (Admin API alignment) and `GET /v3/apiClients/{clientId}` with a `string` parameter, each with a summary/description that names the identifier it takes.
4. Tests at unit (module), OpenAPI-contract, and CMS E2E levels that prove the resolved identifier semantics, including `404` for a well-formed but non-existent numeric id, `404` for a missing client key, and that non-numeric values never reach the numeric path.
5. Update the operator documentation that currently states the numeric id cannot be used with GET.

### 3.2 Non-goals (explicit)

- **No change to DMS.** The DMS application-context lookup keeps calling `GET /v3/apiClients/{clientId}` with the OAuth key. No DMS file is touched.
- **No repository or DDL change.** Both repository methods already exist with tenant scoping; no SQL is edited, so backend integration tests are not re-run as part of the gate (they are unaffected).
- **No unique index on `ClientId`/`ClientUuid`.** Recorded as a follow-up risk (§10.5) and filed as DMS-1529 under DMS-1072, including duplicate-data/migration handling for PostgreSQL and MSSQL.
- **No route relocation.** The key lookup is not moved to `/v3/apiClients/byClientId/{clientId}` or to a query parameter. The ticket allows this only "if the approved spec explicitly moves it … and handles compatibility"; the recommendation is not to move it (§4.2).
- **No response-shape change.** `ApiClientResponse` is returned unchanged by both GETs. Aligning field names with `apiClientModel` (`keyStatus`, `educationOrganizationIds`) is a separate gap in `reference/DMS-1039/AdminApi-CMS GAP Analysis.md`.
- **No change to PUT/DELETE/reset-credential/ownership routes**, which already use `{id}` bound to `int`.

---

## 4. Route design

### 4.1 Final registration **[REC]**

```csharp
// Limited access endpoints - accessible by service accounts for internal DMS operations
endpoints.MapLimitedAccess("/v3/apiClients/", GetAll).Produces<List<ApiClientResponse>>(200);

// Numeric primary-key lookup, the Management API 3.0.0 / Admin API contract.
// The :int constraint gives this route precedence over the string route below
// whenever the segment parses as an Int32; anything else falls through.
endpoints
    .MapLimitedAccess("/v3/apiClients/{id:int}", GetById)
    .Produces<ApiClientResponse>(200)
    .WithSummary("Retrieves a specific apiClient based on its numeric identifier.")
    .WithDescription("… `id` is the numeric ApiClient identifier returned as `id` in responses. To look up a client by its OAuth client key (`clientId`), request GET /v3/apiClients/{clientId} with a non-numeric value.");

// OAuth client-key lookup, retained for DMS application-context resolution and existing callers.
endpoints
    .MapLimitedAccess("/v3/apiClients/{clientId}", GetByClientId)
    .Produces<ApiClientResponse>(200)
    .WithSummary("Retrieves a specific apiClient based on its OAuth client key.")
    .WithDescription("… `clientId` is the OAuth client key (returned as `key` when credentials are issued). A purely numeric value is interpreted as the numeric identifier instead and served by GET /v3/apiClients/{id}.");
```

Handler shape (new, minimal, mirrors `GetByClientId`):

```csharp
private static async Task<IResult> GetById(
    int id,
    HttpContext httpContext,
    IApiClientRepository apiClientRepository
)
{
    ApiClientGetResult getResult = await apiClientRepository.GetApiClientById(id);
    return getResult switch
    {
        ApiClientGetResult.Success success => Results.Ok(success.ApiClientResponse),
        ApiClientGetResult.FailureNotFound => FailureResults.NotFound(
            $"ApiClient with ID {id} not found.",
            httpContext.TraceIdentifier
        ),
        _ => FailureResults.Unknown(httpContext.TraceIdentifier),
    };
}
```

`GetByClientId` is untouched, including its `"ApiClient not found"` message.

### 4.2 Why two routes on the same segment rather than a relocated key route

| Option | Verdict |
|---|---|
| **A. `{id:int}` + `{clientId}` on the same segment (recommended)** | Zero change for DMS, E2E, and operators. ASP.NET Core endpoint routing ranks a constrained parameter segment above an unconstrained one, so `/v3/apiClients/7` selects the `int` route and `/v3/apiClients/c86b44f2-…` selects the string route without an `AmbiguousMatchException` **[INFER, verified by unit test in Phase 1]**. The served OpenAPI document gains `get` under the already-present `/v3/apiClients/{id}` path item. |
| B. Single `{id}` string route that branches on `int.TryParse` inside the handler | One OpenAPI path, but the documented parameter would have to be `string` (contradicting the spec) or `int32` (contradicting behavior). Also less explicit than a route constraint. Rejected. |
| C. Move the key lookup to `/v3/apiClients/byClientId/{clientId}` (or `?clientId=`) and make `{id}` numeric-only | Breaks DMS's application-context lookup and every key-based E2E step unless a compatibility shim keeps the old string route anyway, which collapses back to A plus an extra route. Rejected for this ticket; can be revisited if the spec ever adds such a path. |

### 4.3 Route-matching edge cases (defensive behavior)

| Request segment | Route selected | Repository call | Result |
|---|---|---|---|
| `1` | `{id:int}` | `GetApiClientById(1)` | 200 if the row exists in the caller's tenant, else 404 |
| `999999` (well-formed, absent) | `{id:int}` | `GetApiClientById(999999)` | 404 `"ApiClient with ID 999999 not found."` |
| `c86b44f2-a80b-450d-be0c-4ecf43397e03` (GUID key) | `{clientId}` | `GetApiClientByClientId(...)` | unchanged |
| `12abc`, `abc`, `1.5`, `1e3` | `{clientId}` | `GetApiClientByClientId(...)` | 404 unless such a key exists |
| `99999999999` (exceeds Int32) | `{clientId}` (int constraint fails) | `GetApiClientByClientId("99999999999")` | 404 |
| `-1`, `+1`, `0` | `{id:int}` (`IntRouteConstraint` accepts `NumberStyles.Integer`) | `GetApiClientById(-1 / 1 / 0)` | 404 (no such id); `+1` resolves as 1 |
| empty (`/v3/apiClients/`) | collection route | `QueryApiClient` | unchanged |

**[REPO]** CMS generates every OAuth client key with `Guid.NewGuid().ToString()` (`ApiClientModule.cs:183`, `ApplicationModule.cs:63`), so a CMS-issued key is never all-digits and can never be shadowed by the numeric route. Only a key inserted by an external process could be; that is an accepted residual risk (§10.2).

### 4.4 Authorization policy **[REC]**

Both GETs use `MapLimitedAccess` (`ServicePolicy` + `AdminOrAuthMetadataReadOnlyAccessScopePolicyOrReadOnly`). Rationale: the numeric GET exposes exactly the `ApiClientResponse` that the limited-access collection GET `/v3/apiClients/` already returns for every client, so a narrower policy would add no protection while making the two single-item reads behave differently for the same scope. Management reads (`ReadOnly`/`Admin`) are a subset of that policy, so they remain permitted. See Q1.

---

## 5. OpenAPI representation

### 5.1 Resulting document shape

The generator (`Microsoft.AspNetCore.OpenApi`) keys path items by the route template with constraints stripped **[INFER, verified in Phase 2 tests]**, so:

| Path item | Operations after this change | `id`/`clientId` parameter schema |
|---|---|---|
| `/v3/apiClients/{id}` | **`get` (new)**, `put`, `delete` | `integer` / `int32` (inferred from the `int id` handler parameter, as PUT/DELETE already are) |
| `/v3/apiClients/{clientId}` | `get` (unchanged) | `string` |
| `/v3/apiClients/{id}/reset-credential`, `/v3/apiClients/{id}/ownership` | unchanged | `int32` |

No `/v3/apiClients/{id:int}` key may appear in the document; a test asserts this.

### 5.2 Ambiguous templated paths

OpenAPI 3.x states that templated paths with the same hierarchy but different parameter names must not coexist. **[REPO]** The served CMS document **already** contains both `/v3/apiClients/{id}` (PUT, DELETE) and `/v3/apiClients/{clientId}` (GET), so this condition pre-exists and no CI tooling lints for it (no spectral/redocly/swagger-cli usage found in `.github/` or `eng/`). This change does not introduce a new conflicting path item; it adds an operation to an existing one. The operation summaries/descriptions in §4.1 disambiguate the two for human readers and Swagger UI.

Alternative considered: hide the `{clientId}` GET with `.ExcludeFromDescription()` to produce a strictly valid document. Rejected (Q2, reviewer-confirmed) because the ticket requires metadata to "accurately reflect the final behavior" and the operator docs direct users to that route. **Keeping both path items is a deliberate compatibility compromise:** the document is knowingly non-conformant on this one point so that it describes every route the service actually serves.

### 5.3 Metadata endpoint

`/metadata/specifications` (`MetadataModule.cs`) re-serves `/openapi/v1.json` with hand-written `components.parameters.id` (`int32`). No change is needed there; the existing `MetadataSpecifications_Declares_Id_Parameter_As_Int32` test keeps passing.

---

## 6. Affected files

| Area | File | Change |
|---|---|---|
| CMS frontend | `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ApiClientModule.cs` | Register `GET /v3/apiClients/{id:int}`; add `GetById` handler; add `WithSummary`/`WithDescription` to both single-item GETs. |
| CMS unit tests | `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/ApiClientModuleTests.cs` | New fixtures for numeric lookup, numeric 404, string fallthrough, and repository-call exclusivity. |
| CMS OpenAPI tests | `.../Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs` | Add `"GET /v3/apiClients/{id} int32"` to `ExpectedIdPathParameters`; update the XML doc comment that describes the set. |
| CMS OpenAPI tests | `.../Frontend.AspNetCore.Tests.Unit/Modules/ApiClientOpenApiContractTests.cs` | New tests: `{id}` GET parameter is int32 with the numeric description; `{clientId}` GET parameter is string with the key description; no constraint-bearing path key. |
| CMS E2E | `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/ApiClients.feature` | New scenarios 25–27 (numeric GET 200, numeric 404, digits-plus-letters 404). |
| CMS E2E | `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/Tenants.feature` | One added step pair in the existing cross-tenant scenario: tenant B `GET /v3/apiClients/{tenantAClientId}` → 404. |
| Docs | `docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md` | State that GET accepts either identifier; keep the key-based discovery step. |
| Docs | `reference/design/configuration-service/README.md` | Link this spec. |
| Spec | this file | Status/progress updates at each gate. |

Files deliberately **not** changed: every `IApiClientRepository` implementation, DDL scripts, `ConfigurationServiceApplicationProvider.cs` and its tests, `EndpointBuilderExtensions.cs`, `MetadataModule.cs`, the GAP analysis.

---

## 7. Test plan

### 7.1 Unit — `ApiClientModuleTests.cs` (NUnit, FluentAssertions, FakeItEasy)

New fixtures follow the file's `Given_…` / `It_…` style and reuse `SetUpClient()`.

| Fixture | Arrangement | Assertions |
|---|---|---|
| `Given_Get_By_Numeric_Id` | `GetApiClientById(7)` returns `Success(response with Id = 7, ClientId = "test-client-id")`; `GetApiClientByClientId` returns `FailureNotFound` | `GET /v3/apiClients/7` → 200; body `id == 7`; `GetApiClientById(7)` `MustHaveHappenedOnceExactly`; `GetApiClientByClientId(any)` `MustNotHaveHappened` |
| `Given_Get_By_Numeric_Id_That_Does_Not_Exist` | `GetApiClientById(999)` returns `FailureNotFound` | `GET /v3/apiClients/999` → 404 with `application/problem+json`; `GetApiClientByClientId` never called (this is the ticket's explicit AC) |
| `Given_Get_By_Client_Key` (extends existing coverage) | as today | `GET /v3/apiClients/test-client-id` → 200; `GetApiClientById(any)` `MustNotHaveHappened` |
| `Given_Get_With_Non_Numeric_Identifier_Values` (TestCases `12abc`, `1.5`, `99999999999`, `abc`) | `GetApiClientByClientId(value)` returns `FailureNotFound` | 404; `GetApiClientByClientId(value)` called once; `GetApiClientById(any)` never called |
| `Given_Get_By_Numeric_Id_Repository_Failure` | `GetApiClientById` returns `FailureUnknown` | 500 (`FailureResults.Unknown`) |
| Existing `Given_Nonexistent_Resources.It_returns_not_found_for_get_by_client_id` | unchanged | still passes |

Also update `Given_Valid_ApiClient_Operations.It_returns_success_responses_for_all_operations` to add a `GET /v3/apiClients/1` call asserting 200, so the "all operations" sweep covers the new route.

### 7.2 OpenAPI / metadata contract

- `MetadataModuleTests.OpenApi_Declares_Resource_Identifiers_As_Int32`: exact-set gains `"GET /v3/apiClients/{id} int32"` (the test fails without it and fails if the route were mis-typed).
- `ApiClientOpenApiContractTests` (`Given_the_served_openapi_document`):
  - `It_declares_the_numeric_api_client_get_id_parameter_as_int32` — `paths["/v3/apiClients/{id}"]["get"].parameters[name=id]` has `in: path`, `type: integer`, `format: int32`.
  - `It_declares_the_client_key_api_client_get_parameter_as_string` — `paths["/v3/apiClients/{clientId}"]["get"].parameters[name=clientId]` has `type: string`.
  - `It_describes_which_identifier_each_api_client_get_takes` — the numeric GET's description contains "numeric" and the key GET's description contains "OAuth client key".
  - `It_does_not_leak_route_constraints_into_path_keys` — no path key contains `:` .

### 7.3 Backend integration

None added. **[REPO]** `ApiClientTests` (PostgreSQL and MSSQL) already cover `GetApiClientById` happy path (`:319`), cross-tenant not-found (`:534`/`:632`), and `GetApiClientByClientId` cross-tenant not-found (`:551`/`:649`). No SQL changes, so these lanes are not part of the gate.

### 7.4 CMS E2E — `ApiClients.feature`

Appended after scenario 24 (no renumbering of existing scenarios):

- **25 Ensure clients can GET apiClient by numeric id** — POST `/v3/apiClients` for the background application, capture `apiClientId` and credentials; `GET /v3/apiClients/{apiClientId}` → 200 with the full body (`id`, `applicationId`, `clientId`, `clientUuid`, `name`, `isApproved`, `creatorOwnershipTokenId`, `ownershipTokenIds`, `dataStoreIds`) equal to the same client read via `GET /v3/apiClients/{clientId}`.
- **26 Verify a well-formed but non-existent numeric id returns 404** — `GET /v3/apiClients/99999` → 404.
- **27 Verify a mixed value is treated as a client key** — `GET /v3/apiClients/12abc-not-a-key` → 404.

Tag 25 with `@MssqlRepresentative` so the MSSQL smoke subset also exercises the numeric route (CI runs `TestCategory=MssqlRepresentative` against MSSQL).

### 7.5 CMS E2E — `Tenants.feature`

In the existing "Tenant B can neither read, update, reset nor delete tenant A's unassigned client" block (line ~353), add immediately after the key-based 404:

```gherkin
When a GET request is made to "/v3/apiClients/{tenantAClientId}" with header "Tenant" value "TenantB_{scenarioRunId}"
Then it should respond with 404
```

This proves tenant scoping on the numeric path end-to-end using the already-captured `tenantAClientId`.

### 7.6 Verification commands (run before push)

```powershell
# Format only what was edited
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ApiClientModule.cs
dotnet csharpier format src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules
dotnet csharpier check src/config

# Focused unit + OpenAPI contract tests
dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit --filter "FullyQualifiedName~ApiClientModuleTests|FullyQualifiedName~MetadataModuleTests|FullyQualifiedName~Given_the_served_openapi_document"

# Full CMS unit lane as the final local gate
./build-config.ps1 UnitTest -Configuration Release

# CMS E2E (PostgreSQL, self-contained IdP), scoped to the two edited features.
# Teardown first so no stale stack or image from another branch is reused, and again afterwards.
pwsh src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1
pwsh src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/setup-local-cms.ps1
dotnet test src/config/tests/EdFi.DmsConfigurationService.Tests.E2E --filter "FullyQualifiedName~ApiClients|FullyQualifiedName~Tenants"
pwsh src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1
```

---

## 8. Implementation phases and steps

Each step ends with a local commit, the SHA, an exact file list, and an approve/reject gate.

### Phase 0 — Spec approval
- **0.1** Reviewer resolved Q1–Q4 (§11) on 2026-09-09. Follow-up DMS-1529 filed and linked from a DMS-1343 comment. Spec status set to Approved; commit spec only (`[DMS-1343] Add implementation spec for apiClient GET identifier semantics`), report SHA, stop for approval.

### Phase 1 — Numeric route and handler
- **1.1** `ApiClientModule.cs`: register `MapLimitedAccess("/v3/apiClients/{id:int}", GetById)` *before* the `{clientId}` registration (order is not required for precedence but keeps the intent readable); add the `GetById` handler. No OpenAPI text yet.
- **1.2** `ApiClientModuleTests.cs`: add the fixtures in §7.1; extend the all-operations sweep. Run the focused unit filter; all green. Commit.

### Phase 2 — OpenAPI metadata
- **2.1** `ApiClientModule.cs`: add `WithSummary`/`WithDescription` to both single-item GETs (§4.1 wording).
- **2.2** `MetadataModuleTests.cs`: add `"GET /v3/apiClients/{id} int32"` to the expected set; reword the XML doc comment ("nine item routes … five secondary routes" and the `{clientId}` remark) to reflect the new count and that `{clientId}` is intentionally string.
- **2.3** `ApiClientOpenApiContractTests.cs`: add the four tests in §7.2. Run focused filter; commit.

### Phase 3 — CMS E2E
- **3.1** `ApiClients.feature`: scenarios 25–27.
- **3.2** `Tenants.feature`: the two-line cross-tenant numeric step.
- **3.3** Run `teardown-local-cms.ps1`, then `setup-local-cms.ps1`, execute the two features, report totals (passed/failed/skipped), then `teardown-local-cms.ps1` again. Commit.

### Phase 4 — Documentation
- **4.1** `docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md`: revise the "two identifiers … not interchangeable" paragraph and steps 3/8 to state the exact rule from §1.1 (a valid `int32` segment is the numeric `id`; any other segment is the OAuth `clientId` key), while the key remains the way to *discover* the id after `POST /v3/applications`.
- **4.2** `reference/design/configuration-service/README.md`: add a link to this spec.
- **4.3** This spec: status → Implemented, progress table filled with SHAs. Commit.

### Phase 5 — Final verification and push (human-gated)
- **5.1** `dotnet csharpier check src/config`; full `./build-config.ps1 UnitTest`; E2E result from 3.3 recorded.
- **5.2** Push `DMS-1343` and open the PR only after explicit approval; PR body via the `pr-description` skill.

---

## 9. Backwards compatibility

| Concern | Assessment |
|---|---|
| Existing key-based callers (`GET /v3/apiClients/{guid}`) | Unaffected: GUID keys contain hyphens and letters, never satisfy `:int`, and continue to select the string route. Response body and status codes unchanged. |
| DMS application-context lookup | Unchanged path, unchanged policy, unchanged payload; DMS's post-read check `applicationContext.ClientId == requested clientId` continues to hold. No DMS code or test changes. |
| Callers that previously received 404 for `GET /v3/apiClients/<digits>` | Now receive 200 when a client with that primary key exists in their tenant. This is the intended new capability, not a regression; no known caller depends on the old 404. |
| PUT/DELETE/reset-credential/ownership | Untouched. |
| OpenAPI consumers | `/v3/apiClients/{id}` gains a `get`; `/v3/apiClients/{clientId}` unchanged. Code generators keyed on operation ids may see one new operation. |
| Release notes | Additive change; suggest a "CMS: `GET /v3/apiClients/{id}` now accepts the numeric ApiClient id (Admin API alignment); key-based lookup unchanged" line. |

---

## 10. Risks

### 10.1 Route precedence **[INFER → tested]**
ASP.NET Core ranks constrained parameter segments above unconstrained ones, so the two templates are not ambiguous. If this assumption were wrong, the app would throw `AmbiguousMatchException` on the first matching request; the Phase 1 unit tests exercise both templates against a numeric and a string value in the same host and would fail immediately.

### 10.2 Numeric client keys
A client key that is a pure Int32 literal would be shadowed by the numeric route. CMS never issues such keys (§4.3). Mitigation for externally seeded rows: none in this ticket; documented in the route description.

### 10.3 OpenAPI templated-path ambiguity (pre-existing)
Described in §5.2. Not introduced by this change. If a future lint gate is adopted, the remedy would be to move or hide the key route under a separate ticket.

### 10.4 Message-shape difference between the two 404s
Numeric 404 uses `"ApiClient with ID {id} not found."` (matching the reset-credential message in the same module); key 404 keeps `"ApiClient not found"`. Both are `application/problem+json` through `FailureResults.NotFound`. No consumer parses the detail text (DMS checks only the status).

### 10.5 No unique constraint on `ClientId` / `ClientUuid` **[JIRA]**
`GetApiClientByClientId` uses `SingleOrDefault()`, so duplicate keys would surface as a 500 (`FailureUnknown`) rather than an arbitrary row. The numeric route is immune (primary key). Adding a unique index is a schema migration for both PostgreSQL and MSSQL and belongs in a follow-up ticket (Q3).

### 10.6 Test-set brittleness
`ExpectedIdPathParameters` is an exact set by design; forgetting to add the new GET fails the build loudly, which is the intended guard.

---

## 11. Decisions (resolved 2026-09-09) and assumptions

| # | Question | Decision |
|---|---|---|
| **Q1** | Authorization for `GET /v3/apiClients/{id:int}`: `MapLimitedAccess` (same as the sibling reads) or `MapSecuredGet` (ReadOnly/Admin only)? | **`MapLimitedAccess`** (§4.4). Matches the existing collection and key-based reads and exposes nothing the limited-access collection route does not already expose. |
| **Q2** | OpenAPI: keep documenting the `{clientId}` GET as its own path item (accurate, but the pre-existing "same hierarchy, different name" condition remains), or hide it? | **Keep it documented.** Do not hide it. The ambiguity note in §5.2 stays explicit as a deliberate compatibility compromise. |
| **Q3** | Should a follow-up Jira ticket be filed now for a unique index on `dmscs.ApiClient.ClientId` (and possibly `ClientUuid`) in both DDL sets, or is a comment on DMS-1343 sufficient? | **Filed as DMS-1529** under DMS-1072, covering `ClientId` and `ClientUuid` with duplicate-data/migration handling for PostgreSQL and MSSQL. No DDL or repository change in DMS-1343. |
| **Q4** | Numeric 404 detail text: `"ApiClient with ID {id} not found."` (module-consistent) vs. reusing the existing `"ApiClient not found"`? | **`"ApiClient with ID {id} not found."`** for numeric-id 404s. The key-based 404 message is unchanged. |
| **A1** | Assumption: the constraint-stripped path key `/v3/apiClients/{id}` is what the generator emits for `{id:int}`. | Verified by `It_does_not_leak_route_constraints_into_path_keys` and by the exact-set test. |
| **A2** | Assumption: no caller relies on `GET /v3/apiClients/<digits>` returning 404. | No such caller found in DMS, CMS, E2E, or docs. |
| **A3** | Assumption: the spec's `description` text for the GET ("Get search pattern…") is boilerplate and need not be copied verbatim; CMS wording that names the identifier is preferable. | Confirm or supply preferred wording. |

---

## 12. Progress (filled during implementation)

| Step | Commit | Files | Status |
|---|---|---|---|
| 0.1 | — | this spec | pending approval |
| 1.1–1.2 | — | | |
| 2.1–2.3 | — | | |
| 3.1–3.3 | — | | |
| 4.1–4.3 | — | | |
| 5.1–5.2 | — | | |
