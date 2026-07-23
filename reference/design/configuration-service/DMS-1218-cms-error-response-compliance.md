# DMS-1218 — Make all CMS API error responses conform to the Ed-Fi Error Response Knowledge Base

**Implementation specification (Configuration Service / CMS)**

---

## 1. Document control and approval state

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1218-cms-error-response-compliance.md` |
| Jira ticket | DMS-1218 — *Make all CMS API error responses conform to the Ed-Fi Error Response Knowledge Base* (Status: In Progress) |
| Branch | `DMS-1218-V2` |
| Base commit | `5922d4fb403ac2fe23510ad4110fb305e71a4d70` (`origin/main`) |
| Author of spec | Samuel Lugo (with automated repository audit) |
| Date | 2026-07-23 |
| **Status** | **Approved — 2026-07-23** |
| Revision | R4 — incorporates third architect challenge dated 2026-07-23 (INV-12 deny-by-default + control-document cleanups) |
| Ticket comments at time of writing | Empty (verified `acli jira workitem view DMS-1218 --fields comment`) |
| Pre-existing repo design doc for DMS-1218 | None found (verified) |

> **Control document.** Until marked **Approved**, no production code may be written, no commit may be made (including this spec), and no long test suite may be run. After approval, the approved spec is committed first (**C01**) to this tracked path — **no `git add -f`**, because this directory is tracked. Implementation then proceeds phase-by-phase behind per-commit approval gates.

**Revision note (R2).** The prior draft (R1) lived at the *ignored* path `docs/superpowers/specs/…` and left five decisions open. The architect challenge (2026-07-23) rejected R1 for approval and resolved all decisions. This R2 revision: (a) moves the definitive spec to this tracked path; (b) converts OAuth/OIDC non-success responses (no product exception); (c) makes ClaimsManagement non-success DTO conversion mandatory; (d) implements framework 401/403/404/405/415 shaping via a **scheme-independent** strategy; (e) moves the exception boundary ahead of tenant/config middleware; (f) makes `BadHttpRequestException` status-aware; (g) removes the repo-wide media-type rewrite (INV-31b/C07); (h) makes all verification commands executable verbatim. The old R1 file is removed.

**Revision note (R3).** The second architect challenge (2026-07-23) resolved the prior eight blockers and required four narrow corrections, all applied here: (1) reclassify the 415 URI as an **Ed-Fi/DMS platform convention** (not KB-documented) and **resolve D-08 now** — reachable statuses lacking a ticket-mandated, KB-documented, or established-platform URI use RFC 9457 `type: "about:blank"` with the standard reason phrase as `title` and the HTTP status preserved; (2) framework shaping applies to **every** bodiless non-2xx response with **no route-based exclusions** and **no status-code-page re-execution** — a single status-code callback/custom middleware guarded only by `Response.HasStarted` + body/content-type checks; (3) refine INV-12 so only recognized field-validation failures with non-empty paths populate `validationErrors`, while operational/database/unexpected failures use a safe generic bad-request body (raw detail logged server-side, never in the body); (4) make control evidence truthful and executable (accurate V-01/V-02 match enumeration, split V-17, corrected §15 ordering statement, and a mismatch-proof `FailureResponseWriter`).

**Revision note (R4).** The third architect challenge (2026-07-23) resolved all remaining architecture blockers and required one **blocking** fix plus small cleanups, all applied here: (1) INV-12 is now **deny-by-default** — a backend failure message reaches the body **only** for path-bearing `"Validation"` failures or the two fixed `"Structure"` literals; every other type (incl. *pathless* `"Validation"` from the data layer, `"Database"`, `"Unexpected"`, and any future type) uses a safe generic bad-request body with raw text logged server-side only, verified by a secret-sentinel test; (2) control-document cleanups — V-02 enumeration completed (adds the `FailureResults` `error_description` parser and `MetadataModule.WriteAsync`), the self-challenge/end markers advanced to R4, the stale "exclusions" wording removed from §29.2, the §10 target JSON `type` now permits an Ed-Fi URI **or** `about:blank`, and `ForUnclassifiedStatus` added to the file-impact forecast. No new architecture or product decision.

**Evidence tags:** **[JIRA]** ticket/KB fact · **[REPO]** verified in repo at base · **[INFER]** inference · **[REC]** author recommendation · **[ARCH]** architect-decided.

---

## 2. Objective

Bring **every non-success HTTP response produced by the CMS frontend** (`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore`) and the shared CMS error model (`src/config/datamodel/.../Infrastructure/FailureResponse.cs`) into conformance with the Ed-Fi Error Response Knowledge Base and the Required Error Response Contract of DMS-1218. **[JIRA]**

Per the architect challenge, **no product-approved exception exists**; therefore **all** CMS non-success responses — including OAuth/OIDC error bodies and framework-generated errors — must use the Ed-Fi contract. OAuth/OIDC **success** responses remain protocol-shaped. **[ARCH]** DMS API behavior under `src/dms` is out of scope. **[JIRA]**

---

## 3. Authoritative sources

Only these are authoritative; related tickets, prior PRs, and other DMS-1218 branches were **not** consulted.

1. **DMS-1218 current description** (scope, exclusions, Required Contract, acceptance criteria, tasks, DoD). **[JIRA]**
2. **Ed-Fi Error Response Knowledge Base**, Confluence 58589185 (ODS/API v7.2+ implements RFC 9457 Problem Details; taxonomy catalog). **[JIRA]**
3. **Current repository behavior** at `5922d4fb`. **[REPO]**
4. **Architect challenge**, 2026-07-23 (recorded in §5.1 and §24). **[ARCH]**

No repository design document matched DMS-1218 before this file (`grep -rli "DMS-1218" . --include="*.md"` → none). **[REPO]**

### 3.1 Knowledge Base facts **[JIRA]**

- Error bodies use `type` (`urn:ed-fi:api:*` URI hierarchy), `detail`, `title`, `status`, `correlationId`, plus the **extension members** `validationErrors` (bad-request validation) and `errors` (developer/operator detail).
- **Every KB error example is served as `Content-Type: application/json; charset=utf-8`**; `application/problem+json` appears nowhere on the page, despite the RFC 9457 statement.
- Taxonomy URIs catalogued (to avoid invention):
  - `urn:ed-fi:api:bad-request` (400, "Bad Request")
  - `urn:ed-fi:api:bad-request:data` (400, "Data Validation Failed")
  - `urn:ed-fi:api:bad-request:parameter` (400, "Parameter Validation Failed")
  - `urn:ed-fi:api:security:authentication` (401)
  - `urn:ed-fi:api:security:authorization` (403), children `…:access-denied:resource`, `…:access-denied:action`, `…:namespace:access-denied:namespace-mismatch`
  - `urn:ed-fi:api:not-found` (404)
  - `urn:ed-fi:api:method-not-allowed` (405, "Method Not Allowed")
  - `urn:ed-fi:api:conflict:*` (409)
  - No explicit 5xx example on the page; ticket mandates `urn:ed-fi:api:internal-server-error` (CMS already uses this).
- **415 URI classification [ARCH].** Per the architect, `urn:ed-fi:api:unsupported-media-type` (415, "Unsupported Media Type") is classified as an **existing Ed-Fi/DMS platform convention — not KB-documented**. It is established in DMS Core `src/dms/core/EdFi.DataManagementService.Core/Response/FailureResponse.cs:47` (`_unsupportedMediaTypeType`). CMS will reuse this established URI for 415 rather than invent one. (405 is both KB-documented and a DMS platform convention — `FailureResponse.cs:29`.)
- **`about:blank` for unclassified statuses [ARCH/D-08].** For any reachable HTTP status that has no ticket-mandated, KB-documented, or established Ed-Fi/DMS platform URI, the body uses RFC 9457 `type: "about:blank"` (RFC 9457 §4.2.1), preserves the HTTP status, uses the standard HTTP reason phrase as `title`, and still includes every required member (`detail`, `type`, `status`, `correlationId`, `validationErrors: {}`, `errors: []`). No URI is invented.

---

## 4. Ticket facts (verbatim distillation) **[JIRA]**

**Required Error Response Contract** — every CMS non-success error response must: (1) have a JSON body; (2) include `detail`, `type`, `title`, `status`, `correlationId`; (3) include `validationErrors` (`{}` when empty); (4) include `errors` (`[]` when empty); (5) body `status` == HTTP status; (6) `correlationId` = `HttpContext.TraceIdentifier`; (7) validation → `urn:ed-fi:api:bad-request:data`; (8) authorization → `urn:ed-fi:api:security:authorization` (unless a more-specific documented URI applies); (9) not-found → `urn:ed-fi:api:not-found`; (10) 500 → `urn:ed-fi:api:internal-server-error`; (11) never bare `Results.NotFound()/Forbid()/BadRequest()/Unauthorized()`; (12) never ad hoc `{ error, message }`.

**Seven confirmed findings** (ticket line numbers; actual lines in §12). **Plus** a complete static audit must find and close similar gaps. OAuth/OIDC **success** stays protocol-shaped; OAuth/OIDC **error** must use the Ed-Fi contract or a *documented product-approved exception* — and **[ARCH]** no such exception exists, so conversion is required.

---

## 5. Ticket consistency analysis

Classifications: **True contradiction**, **Ambiguity**, **Stale baseline**, **Incomplete audit method**, **Implementation risk**, **No contradiction after specificity**.

| ID | Statements involved | Classification | Analysis | Repository evidence | Resolution | Human decision | Status |
|---|---|---|---|---|---|---|---|
| **TC-01** | "all non-success (modules, middleware, infrastructure)" vs Task 11 grep only detecting selected `Results.*`/`Results.Json` | Incomplete audit method | Grep misses `StatusCode` assignments, `WriteAsJsonAsync`/`WriteAsync`, middleware short-circuits, auth challenge/forbid, framework 400/401/403/404/405/415. | `TenantResolutionMiddleware` uses `WriteAsJsonAsync`; `ReportInvalidConfigurationMiddleware` sets empty 500; JwtBearer lacks `OnChallenge`/`OnForbidden`; no `UseStatusCodePages`. | Broader static + pipeline audit (§22). | No | Resolved |
| **TC-02** | 7 findings vs "close any similar gaps" | No contradiction after audit | 7 are a floor. Audit found OAuth branches, ClaimsManagement DTOs, tenant responses, invalid-config, framework auth/routing/method/media. | §12. | Classify + route each (§12). | No (all decided) | Resolved |
| **TC-03** | `validationErrors`/`errors` as "extensions" vs required on every response | No contradiction after specificity | Required Contract controls. | `CreateBaseJsonObject` always emits `{}`/`[]`. | Mandatory on all error responses; already satisfied by the shared model. | No | Resolved |
| **TC-04** | Task 11 raw-results focus vs full KB (shape + taxonomy) | Ambiguity → resolved | Shape + taxonomy for non-compliant producers; preserve compliant status/taxonomy. | CRUD-module branches already correct taxonomy/status. | Preserve statuses unless ticket requires change; flag any proposed change (none proposed except none). | No | Resolved |
| **TC-05** | "Problem Details-style JSON" + Task 10 vs no media type in the contract | Ambiguity / architecture | Contract requires only JSON body. | KB=`application/json`; DMS + CMS `GlobalExceptionHandler` + 4/6 helpers=`application/problem+json`; `Unknown`/`NotFound` + inline module branches default to `application/json`. | **[ARCH/D-05]** `application/problem+json` in `FailureResults` + newly changed writers; **preserve existing compliant inline media types** (no repo-wide rewrite). | Decided | Resolved |
| **TC-06** | Red-green Tasks 1–3 assume noncompliant `FailureResponse.cs:121` | Stale baseline | Base already emits `…:data`. | `FailureResponse.cs:121 = …:data`; `FailureResponseTests.cs:134` asserts it; `rg "data-validation-failed" src/config` → none. | Finding 7 already satisfied; keep regression test; do **not** fabricate a red test. | No | Resolved |
| **TC-07** | Task 6 "Create ClaimsManagementModuleTests.cs" | Stale baseline | File exists (abstract base + nested fixtures; 401/403/404; no 500/body-shape). | `…/Modules/ClaimsManagementModuleTests.cs`. | **Extend**, don't overwrite/duplicate. | No | Resolved |
| **TC-08** | Example test filters | No contradiction (verified) | All resolve to existing types/methods. | `RegisterEndpointTests.When_allow_registration_is_disabled`; `~ClaimsManagementModuleTests` (nested); `~GlobalExceptionHandlerTests`. | Use as written. | No | Resolved |
| **TC-09** | Finding line numbers | Minor drift | 63/142/250/283/288 → actual 64/143/251/284/289; Finding 1 (128) exact. | §12. | Edit by symbol/branch, not line. | No | Resolved |
| **TC-10** | `csharpier format src/config` vs "no unrelated changes" | Implementation risk | Whole-tree format may touch unrelated files. | Repo uses CSharpier. | Format, then `git diff --name-only`; revert unrelated; `git diff --check`. | No | Resolved |
| **TC-11** | OAuth error "Ed-Fi contract or documented product exception" | Ambiguity → resolved strict | No product exception documented. | Live OAuth branches in `IdentityModule`; `OpenIddictErrorHandlingMiddleware` unwired. | **[ARCH/D-04]** Convert all CMS-generated OAuth/OIDC non-success responses; preserve protocol success; record interop risk R-01 but do not use it to override. | Decided | Resolved |
| **TC-12** | Task 11 grep-only "static audit" vs unexpected exceptions bypassing the handler | Implementation risk | `TenantResolutionMiddleware`/config middleware run before `UseExceptionHandler`; their exceptions bypass `GlobalExceptionHandler`. | `Program.cs` order (§9). | **[ARCH]** Move the exception boundary ahead of tenant/config middleware; add a pipeline test that throws in tenant resolution and asserts a full Ed-Fi 500. | No | Resolved |
| **TC-13** | `GlobalExceptionHandler` forces every `BadHttpRequestException` to 400 | Implementation risk | The exception carries a `StatusCode` that may be 415/413/other; forcing 400 hides it. | `GlobalExceptionHandler.cs:40-49`. | **[ARCH]** Audit and preserve legitimate `BadHttpRequestException.StatusCode`; map documented statuses to their URIs; **do not invent** URNs — unclassified reachable statuses use RFC 9457 `about:blank` (D-08 **resolved**); test malformed input, unsupported media type, and any reachable non-400 status. | None (D-08 resolved) | Resolved |

**Bottom line:** No blocking true contradiction. **All decisions — D-01…D-08 — are resolved by the architect (§24).** D-08's taxonomy rule is fixed (ticket/KB/platform URI, else RFC 9457 `about:blank`); Phase 5 audits only *which* statuses are reachable to size the tests. No open decision remains.

---

## 6. Interpretation and precedence rules

1. Required Contract > background prose (TC-03).
2. More-specific *documented* URI > general URI; never invent URIs.
3. Current repository fact > ticket's historical baseline (TC-06).
4. Preserve compliant behavior — statuses and compliant bodies — unless the ticket requires a change (TC-04). **[ARCH]** Media type is normalized only in `FailureResults`/new writers; existing compliant inline bodies keep their media type.
5. **No protocol exceptions** — OAuth/OIDC error responses are converted (TC-11) **[ARCH]**.
6. **No uncontrolled 500s** — the exception boundary precedes tenant/config middleware (TC-12) **[ARCH]**.
7. No unrelated changes; diffs limited to CMS frontend, shared error model, and tests (TC-10).

---

## 7. Scope

**In scope [JIRA]:** all non-success responses from `…Frontend.AspNetCore` (Modules, Middleware, Infrastructure incl. `Infrastructure/Authorization`); the shared model `FailureResponse.cs`; authentication/authorization **response shaping** in `WebApplicationBuilderExtensions.cs` / `Program.cs` (only to shape errors and to reorder the exception boundary); the CMS unit tests and `FailureResponseTests.cs`.

**Out of scope [JIRA]:** DMS (`src/dms`); CMS **success** responses (incl. OAuth/OIDC success, RFC 7662 `200 {active:false}`, successful revocation); IdP/config behavior that does not itself emit a CMS HTTP error.

---

## 8. Non-goals

- Rewriting OAuth/OIDC **protocol success** contracts.
- Changing HTTP **status codes** of compliant responses (none proposed; any change is flagged).
- Refactoring domain result types/repositories beyond error shaping.
- **Repo-wide "single serialization path" rewrite** of the 12 CRUD modules purely for media type (removed per D-05/D-01) **[ARCH]**.
- Adding taxonomy URIs not documented by the KB.
- DMS-side changes (incl. DMS E2E `.feature` files referencing `data-validation-failed`).

---

## 9. Current request and error-response architecture **[REPO]**

**Shared model `FailureResponse`.** Static factory → `JsonNode`; `CreateBaseJsonObject` always emits `detail`, `type`, `title`, `status`, `correlationId`, `validationErrors` (`{}` when null), `errors` (`[]` when null). Factories: `ForUnauthorized` (401 `security:authentication`), `ForForbidden` (403 `security:authorization`), `ForBadRequest` (400 `bad-request`), `ForDataValidation` (400 `bad-request:data` ✅), `ForNotFound` (404 `not-found`), `ForConflict` (409 `conflict`), `ForNonUniqueIdentity` (409 `conflict:non-unique-identity`), `ForBadGateway` (502 `bad-gateway`), `ForUnknown` (500 `internal-server-error`). **No 405/415 factory.**

**Frontend helper `FailureResults`.** Wraps `FailureResponse` in `Results.Json`. `_errorContentType = "application/problem+json"` is passed for `BadGateway`/`InvalidClient`/`Unauthorized`/`Forbidden`, but **not** for `Unknown` (500) and `NotFound` (404) → those default to `application/json`. `Forbidden(detail, correlationId)` derives `errors[0]` via `GetIdentityErrorDetails(detail, "Forbidden")`, which parses `detail` as IdP JSON (`{error,error_description}`) — unsuitable for plain endpoint messages (see D-02).

**`GlobalExceptionHandler`.** Registered via `AddExceptionHandler<GlobalExceptionHandler>()` + `app.UseExceptionHandler(o => {})`. Sets `application/problem+json` + `TraceId` header. Maps `FluentValidation.ValidationException` → 400 `ForDataValidation`, default → 500 `ForUnknown` (no message leak), and **`BadHttpRequestException` → *always* 400 `ForBadRequest`** — this ignores `BadHttpRequestException.StatusCode` (TC-13). Otherwise compliant.

**Pipeline order — `Program.cs`:** `SecurityHeaders` → `RequestLogging` → **`TenantResolution`** → **`[ReportInvalidConfiguration]`** (conditional short-circuit, empty 500) → **`UseExceptionHandler`** → `UseRouting` → `UseCors` → `UseAuthentication` → `UseAuthorization` → `MapRouteEndpoints` → `MapOpenApi`. Consequences: (a) exceptions in `TenantResolution`/`RequestLogging` bypass `GlobalExceptionHandler` (TC-12); (b) auth challenge/forbid are not exceptions, so the handler never shapes them; (c) **no `UseStatusCodePages`/`AddProblemDetails`** → framework 401/403/404/405/415 have empty bodies.

**Auth — `WebApplicationBuilderExtensions.ConfigureIdentityProvider`.** Two JwtBearer configs (self-contained, Keycloak); both define only `OnAuthenticationFailed` (+ `OnTokenValidated` self-contained). **Neither defines `OnChallenge`/`OnForbidden`.** `ScopePolicyHandler` succeeds or does nothing (framework emits empty 403). Secured endpoints use `MapSecuredGet/Post/Put/Delete → RequireAuthorization(...)`.

> **Test-host caveat [REPO]:** CMS unit tests replace the auth scheme with `TestAuthHandler` (via `AddTestAuthentication()`, scope in `X-Test-Scope`). A JwtBearer-`OnChallenge`/`OnForbidden`-only strategy would therefore be **untested** and behave differently under `TestAuthHandler`. Framework auth shaping must be **scheme-independent** (§15) **[ARCH]**.

**OAuth middleware `OpenIddictErrorHandlingMiddleware`.** Converts `/connect/*` and `/.well-known/*` exceptions to OAuth `{error,error_description}` 400 (`application/json`). **Not wired** (`UseOpenIddictErrorHandling`/`UseEnhancedOpenIddict` never invoked; verified) — dead code with unit tests.

---

## 10. Required response contract (target)

```json
{
  "detail": "<human-readable, no sensitive detail>",
  "type": "<urn:ed-fi:api:… Ed-Fi taxonomy — or \"about:blank\" for an unclassified reachable status (D-08)>",
  "title": "<taxonomy title>",
  "status": <int == HTTP status>,
  "correlationId": "<HttpContext.TraceIdentifier, non-empty>",
  "validationErrors": { },
  "errors": [ ]
}
```

`validationErrors` = `{}` unless field-level validation data exists (then keyed by field/JSON-path). `errors` = `[]` unless safe developer/operator messages apply (never raw exception/provider/DB/config/token/secret text). Body `status` == HTTP status. Content type: **`application/problem+json`** for `FailureResults` and newly-changed writers (D-05). This shape is exactly what `FailureResponse.CreateBaseJsonObject` already produces.

---

## 11. Taxonomy matrix

| Condition | Status | `type` | `title` | CMS factory | KB-documented |
|---|---|---|---|---|---|
| Data validation | 400 | `urn:ed-fi:api:bad-request:data` | Data Validation Failed | `ForDataValidation` ✅ | Yes |
| Generic bad request / malformed body | 400 | `urn:ed-fi:api:bad-request` | Bad Request | `ForBadRequest` | Yes |
| Query/parameter validation | 400 | `urn:ed-fi:api:bad-request:parameter` | Parameter Validation Failed | *(none; add only if needed)* | Yes |
| Authentication | 401 | `urn:ed-fi:api:security:authentication` | Authentication Failed | `ForUnauthorized` | Yes |
| Authorization | 403 | `urn:ed-fi:api:security:authorization` | Authorization Failed | `ForForbidden` | Yes |
| Not found | 404 | `urn:ed-fi:api:not-found` | Not Found | `ForNotFound` | Yes |
| Method not allowed | 405 | `urn:ed-fi:api:method-not-allowed` | Method Not Allowed | **add (Phase 5)** | Yes (+ DMS convention) |
| Unsupported media type | 415 | `urn:ed-fi:api:unsupported-media-type` | Unsupported Media Type | **add (Phase 5)** | **No — Ed-Fi/DMS platform convention** (DMS Core `FailureResponse.cs:47`) |
| Conflict | 409 | `urn:ed-fi:api:conflict` (+ documented children) | Conflict | `ForConflict`/`ForNonUniqueIdentity` | Yes |
| Bad gateway (IdP) | 502 | `urn:ed-fi:api:bad-gateway` | Bad Gateway | `ForBadGateway` | platform |
| Internal server error | 500 | `urn:ed-fi:api:internal-server-error` | Internal Server Error | `ForUnknown` | ticket-mandated |
| **Unclassified reachable status** (D-08) | *(preserved)* | `about:blank` | *(standard HTTP reason phrase)* | **add `ForUnclassifiedStatus` (Phase 5)** | RFC 9457 §4.2.1 |

**Source of each 4xx/5xx `type` (no invention):** ticket-mandated (`bad-request:data`, `security:authorization`, `not-found`, `internal-server-error`), KB-documented (`bad-request`, `bad-request:parameter`, `security:authentication`, `method-not-allowed`, `conflict:*`), Ed-Fi/DMS platform convention (`unsupported-media-type` at DMS `FailureResponse.cs:47`; `bad-gateway`), or — for a reachable status matching none of these — RFC 9457 `about:blank` with the standard reason phrase as `title` (D-08). The 405/415 factories reuse the established URIs above; there is no invented URI anywhere in this plan.

---

## 12. CMS Error Producer Inventory

**Classification:** `CONFIRMED` (one of 7) · `AUDIT` (discovered) · `COMPLIANT` · `COMPLIANT-PARTIAL` · `FRAMEWORK` · `SUCCESS/OOS` · `DEAD`. Line numbers actual at `5922d4fb`.

### 12.1 Confirmed findings

| ID | Route / condition | File:method:line | Cur→Target status | Cur body | corrId | Info-disclosure | Target factory | Class | Phase | Commit |
|---|---|---|---|---|---|---|---|---|---|---|
| INV-01 | `POST /connect/register`, `AllowRegistration=false` | `IdentityModule.RegisterClient:128` | 403→403 | bare `Results.Forbid()` | none | none | `FailureResults.Authorization`(new, D-02) `security:authorization`, errors `["Registration is disabled."]` | CONFIRMED F1 | 2 | C03 |
| INV-02 | `POST /management/reload-claims`, flag off | `ClaimsManagementModule.ReloadClaims:64` | 404→404 | bare `Results.NotFound()` | none | none | `FailureResults.NotFound` | CONFIRMED F2 | 2 | C04 |
| INV-03 | `POST /management/upload-claims`, flag off | `UploadClaims:143` | 404→404 | bare `Results.NotFound()` | none | none | `FailureResults.NotFound` | CONFIRMED F3 | 2 | C04 |
| INV-04 | `GET /management/current-claims`, flag off | `GetCurrentClaims:251` | 404→404 | bare `Results.NotFound()` | none | none | `FailureResults.NotFound` | CONFIRMED F4 | 2 | C04 |
| INV-05 | `GET /management/current-claims`, `JsonException` | `GetCurrentClaims:284` | 500→500 | `{ error, message=ex.Message }` | none | **leaks `ex.Message`** | `FailureResults.Unknown` | CONFIRMED F5 | 2 | C05 |
| INV-06 | `GET /management/current-claims`, `InvalidOperationException` | `GetCurrentClaims:289` | 500→500 | `{ error, message=ex.Message }` | none | **leaks `ex.Message`** | `FailureResults.Unknown` | CONFIRMED F6 | 2 | C05 |
| INV-07 | Shared validation type | `FailureResponse.ForDataValidation:121` | 400 | already `…:bad-request:data` | yes | none | *(unchanged)* | **COMPLIANT (F7 satisfied)** | 0 | — |

### 12.2 Audit-discovered — ClaimsManagement non-success DTOs — **all converted [ARCH/D-03]**

Success DTOs unchanged. Every 400/500 branch → `FailureResponse`. Statuses preserved. No `ex.Message`/DB/backend payloads. `validationErrors` populated where field-level data exists; else `{}`.

| ID | Branch | File:line | Cur→Target status | Target factory | validationErrors | Notes |
|---|---|---|---|---|---|---|
| INV-08 | reload partial failures | `ReloadClaims:83` | 500→500 | `ForUnknown` | `{}` | backend failure summaries logged server-side only |
| INV-09 | reload `JsonException` | `ReloadClaims:99` | 400→400 | `ForBadRequest` (safe generic detail) | `{}` | drop `"…"+ex.Message` |
| INV-10 | reload `InvalidOperationException` | `ReloadClaims:116` | 500→500 | `ForUnknown` | `{}` | drop `ex.Message` |
| INV-11 | upload null `Claims` | `UploadClaims:150` | 400→400 | `ForDataValidation` | `{ "Claims": ["Claims JSON is required"] }` | missing-field validation |
| INV-12 | upload partial failures (mixed types) | `UploadClaims:181` | 400→400 | **type-dependent** (see §12.2.1) | keyed by `Path` for field-validation entries only | backend can return validation **and** database/unexpected/unknown/json/argument failures — classify per type |
| INV-13 | upload `JsonException` | `UploadClaims:197` | 400→400 | `ForBadRequest` (safe generic detail) | `{}` | drop `ex.Message` |
| INV-14 | upload `ArgumentException` | `UploadClaims:213` | 400→400 | `ForBadRequest` (safe generic detail) | `{}` | drop `ex.Message` |
| INV-15 | upload `InvalidOperationException` | `UploadClaims:226` | 500→500 | `ForUnknown` | `{}` | drop `ex.Message` |

#### 12.2.1 INV-12 precise mapping (mixed `ClaimsLoadStatus.Failures`) **[ARCH]**

**[REPO]** `IClaimsUploadService.UploadClaimsAsync` returns `ClaimsLoadStatus(bool Success, List<ClaimsFailure>)` where `ClaimsFailure = (string FailureType, string Message, string? Path, Exception? Exception)`. The service produces heterogeneous failures (`src/config/backend/.../Claims/ClaimsUploadService.cs`): `"Validation"` (from `claimsValidator`, **has `Path`**), `"Validation"` (from DB `ValidationFailure`, **no `Path`**), `"Structure"` (missing-property, no `Path`), `"Database"` (`databaseFailure.ErrorMessage` — **internal**), `"Unexpected"` (carries `Exception` — **internal**), `"Unknown"`, `"JsonError"`/`"ArgumentError"`/`"OperationError"` (carry `Exception`; `Message` is a safe fixed string). The current module code blanket-maps **every** `f.Message` into the DTO — which would leak `Database`/`Unexpected` internal text. The refined mapping (HTTP **400** preserved):

**Deny-by-default [ARCH]** — `FailureType` alone never proves a message safe (the data layer converts `ValidationFailure.Errors` into *pathless* `"Validation"` failures whose text is not service-owned). A `ClaimsFailure.Message` may appear in the response body **only** if it falls in one of the two proven-safe cases below; everything else is generic. HTTP **400** preserved throughout.

1. **Proven-safe — field-validation with a path** (`FailureType == "Validation"` **and** `Path` is non-empty): a validator field message → `validationErrors[sanitizedPath] += Message`.
2. **Proven-safe — the two recognized `"Structure"` messages** (fixed, service-owned literals: `"Missing required 'claimSets' property"` / `"Missing required 'claimsHierarchy' property"`): may be exposed (as an `errors` entry or a general `validationErrors` key).
3. **Everything else → generic (deny)** — pathless `"Validation"` (incl. data-layer `ValidationFailure.Errors`), `"Database"`, `"Unexpected"`, `"Unknown"`, `"JsonError"`, `"ArgumentError"`, `"OperationError"`, `"IOError"`, `"Configuration"`, and any unrecognized/future type: emit a **safe generic** `FailureResponse.ForBadRequest` with **no** failure text in the body; log the raw `Message`/`Exception` **server-side only** (already logged by the service).

**Emit path:** if any case-1/case-2 proven-safe entries exist, use `ForDataValidation` (or the explicit `validationErrors` overload) carrying **only** those entries; otherwise use generic `ForBadRequest`. For **mixed** result sets, use generic `ForBadRequest`, optionally retaining **only** the proven-safe path-bearing validation entries via the explicit `validationErrors` overload — never database/exception/pathless-`Validation` text.

Tests (Phase 3): (a) a field-validation case (paths → `validationErrors`); (b) a separate operational/database-failure case (→ generic bad-request, no internal text); (c) a **deny-by-default sentinel test** — inject a secret sentinel string into a **pathless `"Validation"`** message and assert the sentinel is **absent** from the serialized response body. INV-08 (reload partial failures) stays **500 → `ForUnknown`**, which exposes nothing, so this concern does not apply there.

### 12.3 Audit-discovered — OAuth/OIDC error branches — **all converted [ARCH/D-04]**

Convert to the Ed-Fi contract; **preserve** protocol success (token 200, `{active:…}` introspection, successful revocation 200). Remove `{error,error_description}` from CMS non-success responses.

| ID | Condition | File:line | Cur→Target status | Cur body | Target factory | Class | Phase |
|---|---|---|---|---|---|---|---|
| INV-16 | `POST /connect/token`, unsupported grant type | `GetClientAccessToken:224` | 400→400 | `{error,error_description}` | `ForBadRequest` (e.g. errors `["The specified grant type is not supported."]`) | AUDIT (OAuth) | 6 |
| INV-17 | `POST /connect/introspect`, missing token | `IntrospectToken:289` | 400→400 | `{error,error_description}` | `ForBadRequest` (errors `["The token parameter is missing."]`) | AUDIT (OAuth) | 6 |
| INV-18 | `POST /connect/revoke`, missing token | `RevokeToken:347` | 400→400 | `{error,error_description}` | `ForBadRequest` (errors `["The token parameter is missing."]`) | AUDIT (OAuth) | 6 |
| INV-24 | OAuth exception on `/connect/*` (unwired) | `OpenIddictErrorHandlingMiddleware:56` | 400 | `{error,error_description}` | **Remove dead code** (and its tests) so no non-compliant path can be wired later | DEAD | 6 |

> Statuses preserved (all 400). Introspection `{active:false}` (INV-34) is a *successful* RFC 7662 response — unchanged.

### 12.4 Audit-discovered — middleware / infrastructure

| ID | Condition | File:line | Cur→Target status | Cur body | Target | Class | Phase |
|---|---|---|---|---|---|---|---|
| INV-19 | Tenant header missing/empty (MT on) | `TenantResolutionMiddleware:56` | 400→400 | `{error,message}` | writer → `ForBadRequest` | AUDIT | 4 |
| INV-20 | Tenant not found | `TenantResolutionMiddleware:76` | 400→400 | `{error,message}` | writer → `ForBadRequest` (**keep 400** per D-06) | AUDIT | 4 |
| INV-21 | Tenant lookup failure | `TenantResolutionMiddleware:91` | 500→500 | `{error,message}` | writer → `ForUnknown` | AUDIT | 4 |
| INV-22 | Unexpected tenant result | `TenantResolutionMiddleware:119` | 400→400 | `{error,message}` | writer → `ForBadRequest` | AUDIT | 4 |
| INV-23 | Invalid configuration at startup | `ReportInvalidConfigurationMiddleware:19` | 500→500 | **empty** | writer → `ForUnknown` (config messages logged only) | AUDIT | 4 |
| INV-36 | **Uncontrolled 500** from exceptions in `TenantResolution`/config middleware (bypass `GlobalExceptionHandler`) | `Program.cs:61-70` | 500 (unshaped) → 500 (Ed-Fi) | framework default | **Move exception boundary ahead of tenant/config middleware** + pipeline test | AUDIT (TC-12) | 4 |

### 12.5 Framework-generated errors — **implemented now [ARCH/D-07]**, scheme-independent (§15)

| ID | Condition | Origin | Cur→Target status | Cur body | Target | Class | Phase |
|---|---|---|---|---|---|---|---|
| INV-25 | Unauthenticated → secured endpoint | auth challenge (no `OnChallenge`) | 401→401 | empty + `WWW-Authenticate` | Ed-Fi `security:authentication` body; **preserve `WWW-Authenticate`** | FRAMEWORK | 5 |
| INV-26 | Authenticated, insufficient role/scope | authorization (no `OnForbidden`) | 403→403 | empty | Ed-Fi `security:authorization` body | FRAMEWORK | 5 |
| INV-27 | No matching route | routing | 404→404 | empty | Ed-Fi `not-found` body | FRAMEWORK | 5 |
| INV-28 | Wrong HTTP method | routing | 405→405 | empty | Ed-Fi `method-not-allowed` body | FRAMEWORK | 5 |
| INV-29 | Unsupported request media type | model binding / framework | 415→415 | framework | Ed-Fi `unsupported-media-type` body | FRAMEWORK | 5 |
| INV-30 | `BadHttpRequestException` (malformed body / binding / size) | `GlobalExceptionHandler:40` | **forces 400** → status-aware | `ForBadRequest` only | **Branch on `BadHttpRequestException.StatusCode`**: 400→`ForBadRequest`, 415→`ForUnsupportedMediaType` (platform convention); any reachable status with no ticket/KB/platform URI → `ForUnclassifiedStatus` (`about:blank`, D-08 **resolved**). Audit reachable statuses in Phase 5. | COMPLIANT-PARTIAL (TC-13) | 5 |

#### 12.5.1 INV-30 `BadHttpRequestException` reachability audit **[REPO, C11]**

- **Production references:** `rg "BadHttpRequestException" src/config` finds no custom `throw` in production — it is thrown only by the framework (Kestrel / minimal-API model binding) and caught solely by `GlobalExceptionHandler`.
- **Request-body-size config:** no `MaxRequestBodySize`/`RequestSizeLimit` override in `src/config`, so Kestrel's default (~30 MB) applies; a body exceeding it makes the framework throw `BadHttpRequestException` with **413** that propagates to the handler.
- **Reachable statuses at this handler:** **400** (malformed body/form, unexpected end of content) and **413** (body-size limit). Both are mapped: 400 → `ForBadRequest`; 413 → `ForUnclassifiedStatus` (`about:blank`, "Payload Too Large") per D-08 (413 has no documented Ed-Fi URI — none invented).
- **415:** primarily a *framework result* shaped by `FrameworkErrorResponseMiddleware` (INV-29, C10); mapped **defensively** here (`ForUnsupportedMediaType`) in case a `BadHttpRequestException` ever carries 415.
- **Not handled here:** transport/parser rejections that Kestrel emits before the application pipeline (e.g. oversized request line/headers) never reach `GlobalExceptionHandler`; this spec makes no claim about them.
- Any *additional* reachable status follows D-08 (`about:blank` + reason phrase); no URN is invented.

### 12.6 Content-type consistency — **narrowed [ARCH/D-05]**

| ID | Scope | Cur CT | Target CT | Class | Phase |
|---|---|---|---|---|---|
| INV-31a | `FailureResults.Unknown` / `FailureResults.NotFound` | `application/json` | `application/problem+json` | COMPLIANT-shape, CT-inconsistent | 1 |
| ~~INV-31b~~ | ~~All inline `Results.Json(FailureResponse.ForX)` across 12 CRUD modules~~ | — | — | **REMOVED from scope** — existing compliant inline bodies keep their media type; no repo-wide rewrite | — |

### 12.7 Already-compliant and out-of-scope

| ID | Item | Class |
|---|---|---|
| INV-32 | `GlobalExceptionHandler` ValidationException/default paths (problem+json, no leak) | COMPLIANT (regression); BadHttpRequestException branch → INV-30 |
| INV-33 | All CRUD-module error branches wrap `FailureResponse.ForX`, correct taxonomy/status, `TraceIdentifier`; media type unchanged (D-05) | COMPLIANT |
| INV-34 | OAuth success: token 200, `{active:…}` introspection, revoke 200, register 200 | SUCCESS/OOS |
| INV-35 | Discovery/metadata/health modules — all 2xx; `MetadataModule` "Forbidden" is OpenAPI doc inside a 200 body | SUCCESS/OOS |

---

## 13. Proposed architecture

1. **Centralize *changed* error construction only.** New/changed error responses go through `FailureResults` (endpoints) or a new middleware writer (direct-to-response), each setting status, body, and `application/problem+json` in one place. Add `FailureResults` factories/overloads needed by the *changed* branches (`Authorization` overload with explicit `errors[]`, plus `Conflict`/`BadRequest`/`DataValidation` only where a *changed* branch needs them). **No repo-wide rewrite of already-compliant inline responses** (D-01/D-05) **[ARCH]**.
2. **Middleware writer.** `FailureResponseWriter` serializes a `FailureResponse` node with `application/problem+json` and `TraceIdentifier`, **deriving the HTTP status from the node's `status` member** (no independent status argument → no body/HTTP mismatch), guarding on `Response.HasStarted`.
3. **Exception boundary first.** Reorder `Program.cs` so `UseExceptionHandler` precedes `TenantResolution`/config middleware (INV-36) **[ARCH]**.
4. **Scheme-independent framework shaping [ARCH].** A **single** status-code callback / custom terminal middleware (placed after routing/auth) shapes **every** bodiless 401/403/404/405/415 response into an Ed-Fi body, **independent of route and auth scheme**. It uses **no status-code-page re-execution** (which can invoke routing/endpoints twice). The **only** safety boundary is `Response.HasStarted` plus body/content-type checks (empty body → shape; existing body → leave). Existing headers (notably `WWW-Authenticate` on 401) are preserved; 2xx/204 are never touched (INV-25…29).
5. **Status-aware `BadHttpRequestException`.** `GlobalExceptionHandler` branches on `.StatusCode` (INV-30) **[ARCH]**.
6. **OAuth conversion.** IdentityModule OAuth error branches → Ed-Fi contract; remove dead middleware (INV-16/17/18/24) **[ARCH]**.
7. Never change statuses of compliant responses; never invent URIs; never expose sensitive text.

---

## 14. Shared helper responsibilities

**`FailureResponse` (DataModel).** Owns body shape/taxonomy. Already emits mandatory `{}`/`[]`. Add (Phase 5), reusing established URIs — no invention:
- `ForMethodNotAllowed` → `urn:ed-fi:api:method-not-allowed`, 405 (KB-documented + DMS convention).
- `ForUnsupportedMediaType` → `urn:ed-fi:api:unsupported-media-type`, 415 (**Ed-Fi/DMS platform convention**, DMS Core `FailureResponse.cs:47`).
- `ForUnclassifiedStatus(int status, string reasonPhrase, string correlationId)` → `type: "about:blank"`, `title = reasonPhrase` (standard HTTP reason phrase), given `status`, `validationErrors: {}`, `errors: []` (**D-08**, RFC 9457 §4.2.1). Used only when a reachable status has no ticket/KB/platform URI.

**`FailureResults` (Frontend).** Owns `IResult` + status + content type. Make **all** methods use `application/problem+json` (fixes INV-31a). Add:
- `Authorization(string correlationId, string[] errors)` — **D-02** (implemented in C02): builds a 403 `security:authorization` body with the fixed safe detail (`_errorDetail` = "The request could not be processed. See 'errors' for details.") and the explicit `errors` array passed through verbatim, with **no IdP-JSON parsing** (so INV-01 yields `errors: ["Registration is disabled."]`, not the mangled `["Forbidden. …"]` the existing `GetIdentityErrorDetails` would produce). Add analogous explicit-`errors` overloads only where a *changed* branch needs them.
- 405/415 helpers (Phase 5) wrapping the new factories.
Preserve existing method signatures (additive overloads only).

**`FailureResponseWriter` (new, Frontend).** For middleware/terminal shaping that writes directly to `HttpResponse` (tenant, invalid-config, framework shaping). **Mismatch-proof by construction [ARCH]:** the writer takes the `FailureResponse` node and **derives the HTTP status code from the node's own `status` member** — it does not accept an independent status argument, so a body/HTTP status mismatch is structurally impossible. (If a future signature must accept a separate status, it validates `node["status"] == status` and throws on disagreement.) The writer sets `Content-Type: application/problem+json`, ensures `correlationId` = `context.TraceIdentifier`, and guards on `Response.HasStarted` (no-op if the response has already started).

**`GetIdentityErrorDetails` caveat.** Retained for IdP-JSON branches (`InvalidClient`/`Unauthorized`/`Forbidden` from token flow); **not** used for plain endpoint messages (use the D-02 overload).

---

## 15. Middleware and framework-response strategy

- **Exception boundary (INV-36).** Reorder to `SecurityHeaders → RequestLogging → UseExceptionHandler → TenantResolution → [ReportInvalidConfiguration] → UseRouting → UseCors → UseAuthentication → UseAuthorization → endpoints`. This keeps `RequestLoggingMiddleware` outermost (its `IExceptionHandlerFeature`-based 500 logging still observes handled exceptions on the way out) while ensuring tenant/config-middleware exceptions are shaped by `GlobalExceptionHandler`. Add a pipeline test that throws inside tenant resolution and asserts a complete Ed-Fi 500 body. **[ARCH]**
- **TenantResolutionMiddleware (INV-19…22).** Replace all four `WriteAsJsonAsync(new {error,message})` with `FailureResponseWriter` emitting `ForBadRequest`/`ForUnknown` (+ `TraceIdentifier`); **preserve statuses** (400/400/500/400); keep `SanitizeForLog`.
- **ReportInvalidConfigurationMiddleware (INV-23).** Write `ForUnknown(TraceIdentifier)` 500 (via the writer) instead of an empty body; config-failure messages remain `LogCritical`-only. This middleware **short-circuits deliberately** (sets the status and does not call `next`; it does not throw), so it must serialize its own body regardless of pipeline position. **After the INV-36 reorder it sits *after* `UseExceptionHandler`** (order: `… → UseExceptionHandler → TenantResolution → [ReportInvalidConfiguration] → …`); the exception handler therefore cannot help a non-throwing short-circuit, confirming the writer is required here.
- **Framework shaping (INV-25…29) — scheme-independent, no route exclusions [ARCH].** Add a **single** status-code callback / custom terminal middleware after routing/auth that shapes **every** bodiless 401/403/404/405/415 response into an Ed-Fi body, **independent of route and auth scheme** (so it also covers `TestAuthHandler`). Rules: (a) shape **every** matching bodiless response — **no** health/text/OpenAPI or other path-based exceptions; (b) never touch 2xx/204; (c) the **only** safety boundary is `Response.HasStarted` + body/content-type checks — an existing **non-empty** error body is left alone **only** because its producer is separately verified compliant (§12); any non-empty non-compliant body is inventoried and fixed at its producer, not here; (d) **do not** use status-code-page re-execution (which can invoke routing/endpoints twice) — write directly; (e) **preserve existing headers**, notably `WWW-Authenticate` on 401. Because it keys off the final status code (not JwtBearer events), it covers every configured scheme (self-contained, Keycloak, test); if JwtBearer `OnChallenge`/`OnForbidden` are additionally added for header fidelity, tests must demonstrate coverage for **every** scheme.

---

## 16. OAuth/OIDC error strategy and interoperability risk

**[ARCH/D-04] Decision: convert all CMS-generated OAuth/OIDC non-success responses to the Ed-Fi contract.** No product exception exists, so the ticket's "or documented exception" branch does not apply.

- **Convert:** INV-16 (`/connect/token` unsupported grant type), INV-17 (`/connect/introspect` missing token), INV-18 (`/connect/revoke` missing token) → `FailureResults`/`FailureResponse` (`ForBadRequest`, 400 preserved), carrying a safe developer message in `errors` and a `correlationId`. Remove the `{error,error_description}` bodies.
- **Preserve protocol success:** token 200 (`TokenResponse`), introspection `200 {active:true|false}` (a *successful* RFC 7662 result), successful revocation `200` (RFC 7009). These are **not** error responses and are unchanged.
- **Remove dead code (INV-24):** delete `OpenIddictErrorHandlingMiddleware` and its extension methods and unit tests, so no non-compliant OAuth path can be wired in later. (If the architect prefers retention, it must be rewritten to emit the Ed-Fi contract; default is removal.)
- **Risk R-01 (recorded, not overriding).** `{error,error_description}` is the OAuth 2.0 / RFC 7662 / RFC 7009 standard error shape; converting it can break standards-based OAuth clients that parse `error`/`error_description`. This risk is documented for operator awareness and release notes but, per the architect, does **not** override the ticket. **[INFER]**

---

## 17. Security, privacy, and logging rules

- **No sensitive detail in bodies.** No exception messages, provider/DB errors, configuration text, tokens, or secrets in `detail`/`errors`. INV-05/06/09/10/13/14/15 must stop leaking `ex.Message`; 500s use `ForUnknown` (empty `detail`, `errors:[]`). **[JIRA/ARCH]**
- **Continue server-side logging** (`LogError(ex,…)`, `LogWarning`, `LogCritical`); only bodies change.
- **Correlation.** Every error body `correlationId` = `HttpContext.TraceIdentifier` (or `context.TraceIdentifier` in middleware), non-empty.
- **`validationErrors` population.** Populate only with field/JSON-path keys and field-level messages (INV-11/12); never with backend payloads or exception text. Otherwise `{}`.
- Keep `TenantResolutionMiddleware.SanitizeForLog`.

---

## 18. Backward compatibility

- Success responses unchanged (INV-34/35). **[JIRA]**
- Already-compliant error responses keep statuses, bodies, **and media type** (INV-33) — no repo-wide rewrite (D-05). Only `FailureResults.Unknown`/`NotFound` media type changes to `application/problem+json` (INV-31a).
- Statuses preserved everywhere (D-06 keeps tenant-not-found at 400).
- **Client-visible payload changes** (documented for release notes): ClaimsManagement non-success DTOs → Ed-Fi contract (INV-08…15); OAuth error bodies → Ed-Fi contract (INV-16/17/18); framework 401/403/404/405/415 now carry bodies (INV-25…29). Tests asserting the old shapes are updated in lockstep (incl. removing `OpenIddictErrorHandlingMiddlewareTests` with INV-24).
- DMS untouched.

---

## 19. Detailed phased implementation plan

Each phase: Purpose · Included · Excludes · Files · Tasks · Tests · Static checks · Entry · Exit · Dependencies · Risks · Commits · Gate.

### Phase 0 — Baseline, decisions, inventory
- **Purpose:** ground truth; record already-satisfied items; capture architect decisions. **Included:** this spec; verify INV-07 already compliant + covered; confirm `ClaimsManagementModuleTests` exists; obsolete-URI + OAuth-wiring searches (V-01…V-04, V-14). **Excludes:** production code. **Files:** none. **Tests:** none executed. **Static:** V-01/02/03/14 (drafting-run; see §27). **Entry:** spec drafted. **Exit:** spec approved; D-08 audit scheduled. **Deps:** none. **Risks:** none. **Commit:** **C01** (approved spec, tracked path, no `-f`). **Gate:** architect approves.

### Phase 1 — Shared model, helpers, content type, writer
- **Purpose:** single construction path for *changed* responses; content-type consistency (D-05). **Included:** `FailureResults` all-methods `application/problem+json` (INV-31a); `Authorization(...errors[])` overload (D-02); `FailureResponseWriter`; add factories only as needed by changed branches. **Excludes:** call-site changes; 405/415 factories (Phase 5); repo-wide module rewrite (out of scope). **Files:** `FailureResults.cs`, new `FailureResponseWriter.cs`, `FailureResponse.cs` (only if a gap found). **Tests:** `FailureResponseTests` (regression incl. INV-07), new `FailureResultsTests` (shape + content type), new writer tests. **Static:** V-04. **Entry:** C01. **Exit:** helpers/writer exist + tested; no call sites changed. **Deps:** D-05. **Risks:** signature churn → additive overloads only. **Commit:** **C02**. **Gate:** architect review.

### Phase 2 — Confirmed findings F1–F6
- **Purpose:** fix the seven (F7 already done). **Included:** INV-01 (`FailureResults.Authorization`), INV-02/03/04 (`FailureResults.NotFound`), INV-05/06 (`FailureResults.Unknown`, no leak); add `HttpContext` to `ReloadClaims`/`UploadClaims` (GetCurrentClaims already has it). **Excludes:** ClaimsManagement DTO branches (Phase 3); OAuth (Phase 6). **Files:** `IdentityModule.cs`, `ClaimsManagementModule.cs`; tests `IdentityModuleTests.cs`, `ClaimsManagementModuleTests.cs`. **Tasks:** replace bare/ad-hoc results; extend `When_allow_registration_is_disabled` to assert full body; add ClaimsManagement 404 body-shape + 500 exception fixtures. **Tests:** V-05/06/07. **Entry:** C02. **Exit:** F1–F6 fixed; targeted tests pass. **Deps:** Phase 1. **Risks:** IdentityModule tests use their own `WebApplicationFactory` — assert body against that host. **Commits:** **C03** (registration), **C04** (claims 404), **C05** (claims 500). **Gate:** architect review after C05 (or each).

### Phase 3 — ClaimsManagement non-success DTO conversion (mandatory)
- **Purpose:** close audit gaps INV-08…15 per D-03. **Included:** convert every ClaimsManagement non-success DTO branch to `FailureResponse` per §12.2 and the **§12.2.1 type-dependent INV-12 mapping**; preserve statuses; populate `validationErrors` only for recognized field-validation failures with non-empty paths; route operational/database/unexpected failures to a safe generic `ForBadRequest` (400) with raw detail logged server-side only; remove all `ex.Message`. **Excludes:** success DTOs (unchanged); other modules (no rewrite). **Files:** `ClaimsManagementModule.cs`; `ClaimsManagementModuleTests.cs`. **Tasks:** apply the §12.2.1 classification (`FailureType`-driven); update/extend tests including a **field-validation case** (paths → `validationErrors`) **and a separate operational/database-failure case** (→ generic bad-request, asserting no internal/exception text) plus absence of the old DTO shape. **Tests:** V-07 (extended). **Static:** V-02. **Entry:** C05. **Exit:** no DTO/ad-hoc non-success bodies remain in ClaimsManagement; no internal text leaks. **Deps:** Phase 1; D-03 (resolved). **Risks:** batch-result consumers see new shape — documented in §18. **Commit:** **C06**. **Gate:** architect review.

### Phase 4 — Middleware and exception-pipeline
- **Purpose:** compliant middleware bodies + close the uncontrolled-500 gap. **Included:** INV-36 (reorder exception boundary + pipeline test), INV-19…22 (tenant), INV-23 (invalid-config). **Excludes:** framework auth/routing (Phase 5). **Files:** `Program.cs`, `TenantResolutionMiddleware.cs`, `ReportInvalidConfigurationMiddleware.cs`; tests `TenantResolutionMiddlewareTests.cs` (+ body-shape), new pipeline/invalid-config tests, `GlobalExceptionHandlerTests.cs` (regression). **Tasks:** reorder pipeline; replace `WriteAsJsonAsync(new{…})`/empty-500 with writer; add throwing-tenant pipeline test asserting Ed-Fi 500. **Tests:** V-08/09/10, V-20 (pipeline). **Static:** V-02. **Entry:** Phase 1. **Exit:** middleware compliant; no uncontrolled 500. **Deps:** Phase 1. **Risks:** reorder changing logging semantics → keep RequestLogging outermost; verify 500 logging still emits once. **Commits:** **C07** (reorder + pipeline test), **C08** (tenant), **C09** (invalid-config). **Gate:** architect review.

### Phase 5 — Framework-generated auth/routing/method/media/binding
- **Purpose:** shape INV-25…30 (implemented now, D-07). **Included:** scheme-independent status-code shaping for **every** bodiless 401/403/404/405/415 (no route exclusions, no re-execution), preserving `WWW-Authenticate`; add `FailureResponse.ForMethodNotAllowed` (KB+platform), `ForUnsupportedMediaType` (platform convention), and `ForUnclassifiedStatus` (`about:blank`, D-08) + `FailureResults` helpers; make `GlobalExceptionHandler` `BadHttpRequestException` status-aware (INV-30). **Excludes:** changing auth *decisions*. **Files:** `Program.cs` (+ single shaping middleware/callback), `WebApplicationBuilderExtensions.cs` (only if per-scheme header fidelity added), `FailureResponse.cs`, `FailureResults.cs`, `GlobalExceptionHandler.cs`; tests `AuthorizationTests.cs` (+ body-shape + 401), new framework-response + `BadHttpRequestException` tests. **Tasks:** implement the single status-code shaping step (boundary = `HasStarted` + body/content-type; no path exclusions); add 405/415/`about:blank` factories; **audit which `BadHttpRequestException`/framework statuses are reachable** (to size tests) and map each via the fixed D-08 rule (ticket/KB/platform URI, else `about:blank`). **Tests:** V-11 (401/403/404/405/415 bodies; 2xx/204 untouched; every scheme), V-12 (malformed→400, 415→`unsupported-media-type`, unclassified→`about:blank`). **Static:** V-02. **Entry:** D-07, Phase 1. **Exit:** framework errors carry Ed-Fi bodies; success responses unaffected. **Deps:** D-07 (D-08 rule fixed — no decision pending). **Risks (high, R-02):** clobbering success/204/CORS-preflight or double-write → single non-re-executing callback, `HasStarted`+body boundary, integration tests asserting 2xx/204 untouched. **Commits:** **C10** (auth+routing+media shaping), **C11** (`BadHttpRequestException` status-aware). **Gate:** architect review — explicit sign-off (risk).

### Phase 6 — OAuth/OIDC error conversion
- **Purpose:** INV-16/17/18 conversion; INV-24 removal (D-04). **Included:** convert token/introspect/revoke error branches to Ed-Fi contract (400 preserved); remove dead `OpenIddictErrorHandlingMiddleware` + extensions + tests. **Excludes:** OAuth success (preserved). **Files:** `IdentityModule.cs`; delete `Middleware/OpenIddictErrorHandlingMiddleware.cs`, prune `Extensions/OpenIddictIntegrationExtensions.cs`, delete `OpenIddictErrorHandlingMiddlewareTests.cs`; tests `IdentityModuleTests.cs`. **Tasks:** convert branches; remove dead code; add token/introspect/revoke error-path tests asserting the Ed-Fi contract. **Tests:** V-13/14. **Static:** V-02, V-15. **Entry:** D-04, Phase 1. **Exit:** OAuth error branches compliant; dead middleware gone. **Deps:** D-04. **Risks:** R-01 (documented). **Commits:** **C12** (OAuth conversion), **C13** (dead-code removal). **Gate:** architect review.

### Phase 7 — Static audit, regression, formatting, evidence
- **Purpose:** prove DoD. **Included:** re-run Task 11 grep + broader searches; targeted + full CMS frontend unit suite; `FailureResponseTests`; CSharpier; `git diff --check`; changed-file review; clean-worktree check. **Files:** verification + formatting-only diffs on touched files. **Tasks:** execute V-01…V-19 (§27). **Tests:** full CMS frontend unit suite. **Static:** all. **Entry:** Phases 2–6 complete. **Exit:** DoD (§28) green. **Deps:** all. **Risks:** CSharpier reformatting unrelated files (TC-10) — mitigate per §5/§23. **Commit:** **C14** (formatting/regression cleanup, if any). **Gate:** final architect acceptance.

---

## 20. File-impact forecast

| File | Phase(s) | Change |
|---|---|---|
| `datamodel/.../FailureResponse.cs` | 1, 5 | Add `ForMethodNotAllowed` / `ForUnsupportedMediaType` / `ForUnclassifiedStatus` (`about:blank`) factories (Phase 5); no F7 change. |
| `frontend/.../Infrastructure/FailureResults.cs` | 1, 5 | All methods `application/problem+json`; `Authorization(...errors[])` overload; 405/415 helpers. |
| `frontend/.../Infrastructure/FailureResponseWriter.cs` (new) | 1 | Middleware serialization. |
| `frontend/.../Modules/IdentityModule.cs` | 2, 6 | INV-01; INV-16/17/18. |
| `frontend/.../Modules/ClaimsManagementModule.cs` | 2, 3 | INV-02…06, INV-08…15; add `HttpContext` to `ReloadClaims`/`UploadClaims`. |
| `frontend/.../Middleware/TenantResolutionMiddleware.cs` | 4 | INV-19…22. |
| `frontend/.../Infrastructure/ReportInvalidConfigurationMiddleware.cs` | 4 | INV-23. |
| `frontend/.../Program.cs` | 4, 5 | INV-36 reorder; framework shaping registration. |
| `frontend/.../Infrastructure/GlobalExceptionHandler.cs` | 5 | INV-30 status-aware `BadHttpRequestException`. |
| `frontend/.../Infrastructure/WebApplicationBuilderExtensions.cs` | 5 | Only if per-scheme header fidelity is added. |
| `frontend/.../Middleware/OpenIddictErrorHandlingMiddleware.cs` + `Extensions/OpenIddictIntegrationExtensions.cs` | 6 | Delete/prune dead code (INV-24). |
| Test files | 1–6 | Extend/`add`: `FailureResponseTests`, new `FailureResultsTests`/writer tests, `IdentityModuleTests`, `ClaimsManagementModuleTests`, `TenantResolutionMiddlewareTests`, pipeline/invalid-config tests, `AuthorizationTests` + framework tests, `GlobalExceptionHandlerTests`; **delete** `OpenIddictErrorHandlingMiddlewareTests`. |
| CRUD module files (12) | — | **No change** (D-05). |

---

## 21. Test strategy

- **Conventions [REPO/AGENTS.md]:** NUnit + FluentAssertions + FakeItEasy; `Given_…` fixtures, Setup arrange+act, `It_…` methods. Module tests use `WebApplicationFactory<Program>` + `UseEnvironment("Test")` + `AddTestAuthentication()` + `X-Test-Scope`. `IdentityModule` tests build their own factory with fakes.
- **Body-shape assertions:** parse body; assert `type`, `title`, `status`, non-empty `correlationId`, `validationErrors` (`{}`/keyed), `errors` (`[]`/expected), `status`==HTTP status, and **absence** of `ex.Message`/`{error,message}`/`{error,error_description}`.
- **Extend, not recreate:** `ClaimsManagementModuleTests` (TC-07); `When_allow_registration_is_disabled` body assertion (TC-08).
- **Regression:** keep `FailureResponseTests.ForDataValidation_ShouldReturnCorrectJsonNode` (F7 guard).
- **Scheme-independent framework tests:** integration tests via `WebApplicationFactory` asserting 401/403/404/405/415 bodies (including under `TestAuthHandler`) and that 2xx/204 paths are untouched; assert `WWW-Authenticate` preserved on 401.
- **Pipeline test (INV-36):** register a throwing stand-in for tenant resolution and assert a full Ed-Fi 500.
- **OAuth tests:** token/introspect/revoke error-paths assert the Ed-Fi contract; introspection `{active:false}` stays 200.
- **No long/E2E/Docker suites** during implementation of unit-level changes; run targeted then full CMS **unit** suites.

---

## 22. Static audit strategy (executable)

Run from repo root (`C:\dev\ed-fi\Data-Management-Service-DMS-1218-V2`).

1. **Ticket grep (V-01):**
   `rg -n "Results\.(Forbid|NotFound|BadRequest|Unauthorized|Conflict|StatusCode)|TypedResults\.(Forbid|NotFound|BadRequest|Unauthorized|Conflict|StatusCode)|Results\.Json\(" src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore -g "*.cs"`
2. **Broader producers (V-02):**
   `rg -n "WriteAsJsonAsync|WriteAsync|Response\.StatusCode|new \{ error|new \{ active|error_description" src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore -g "*.cs"`
3. **Obsolete URI (V-03):** `rg -n "bad-request:data-validation-failed" src/config`
4. **OAuth wiring (V-14):** `rg -n "UseEnhancedOpenIddict|UseOpenIddictErrorHandling|AddEnhancedOpenIddict|OpenIddictErrorHandlingMiddleware" src/config`
5. **Framework coverage:** confirm presence of the scheme-independent shaping step and absence of `WriteAsJsonAsync(new{…})` error bodies.
6. **Result acceptance:** every remaining match is a compliant helper/writer call or intentionally-preserved compliant inline body; no ad-hoc `{error,message}`/`{error,error_description}` for CMS non-success responses.

---

## 23. Risks and mitigations

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-01 | OAuth conversion breaks OAuth/OIDC client interoperability | High | High | Documented in release notes; per ARCH the ticket governs; keep 400 status + safe `errors` + `correlationId`. |
| R-02 | Framework shaping clobbers success/204/CORS-preflight or double-writes | Medium | High | Target **only bodiless non-2xx** (401/403/404/405/415); **no route/path exclusions**; single status-code callback/middleware with **no re-execution**; boundary = `HasStarted` + body/content-type checks; preserve `WWW-Authenticate`; integration tests assert 2xx/204 untouched across every scheme. (2xx health/OpenAPI are naturally untouched because they are not error statuses.) |
| R-03 | ClaimsManagement DTO conversion regresses batch-result consumers | Medium | Medium | Documented (§18); success DTOs unchanged; statuses preserved. |
| R-04 | Exception-boundary reorder changes 500 logging semantics | Medium | Medium | Keep RequestLogging outermost; test single-emit 500 logging + full body. |
| R-05 | CSharpier reformats unrelated files | Medium | Low | Post-format `git diff --name-only` + revert unrelated; `git diff --check`. |
| R-06 | `ex.Message`/backend payload leak persists in a missed branch | Low | High | V-02 + explicit tests asserting no `message`/exception text. |
| R-07 | Undocumented reachable `BadHttpRequestException` status forces an invented URN | Low | Medium | **Resolved by D-08:** map ticket/KB/platform URIs; for any other reachable status use RFC 9457 `about:blank` + standard reason phrase — never invent. Audit reachable statuses in Phase 5. |
| R-08 | Middleware before boundary throws pre-reorder | Low | Medium | Reorder (INV-36); writer guarded; keep middleware exception-free. |

---

## 24. Decision log (architect-resolved)

| ID | Decision | Type | Architect resolution | Status |
|---|---|---|---|---|
| D-01 | Centralization scope | Architecture | Centralize **changed** helpers/writers only; **no** repository-wide rewrite. | **Resolved [ARCH]** |
| D-02 | Explicit `validationErrors`/`errors` overloads | Architecture | Approve explicit-`errors`/`validationErrors` overloads with **no implicit payload interpretation** (no `GetIdentityErrorDetails` for plain messages). | **Resolved [ARCH]** |
| D-03 | ClaimsManagement non-success DTOs | Product | **Convert every** ClaimsManagement non-success DTO to the Ed-Fi contract; success DTOs unchanged; statuses preserved; no `ex.Message`/DB/backend payloads; populate `validationErrors` when field-level data exists. | **Resolved [ARCH]** |
| D-04 | OAuth/OIDC error bodies | Product | **Convert every** CMS-generated OAuth/OIDC non-success response; preserve protocol success; remove `{error,error_description}` from non-success; remove/rewrite dead middleware. | **Resolved [ARCH]** |
| D-05 | Error content type | Architecture | `application/problem+json` for `FailureResults` and newly-changed writers; **preserve existing compliant inline media types**; remove INV-31b/C07 and the global single-serialization rewrite. | **Resolved [ARCH]** |
| D-06 | Tenant-not-found status | Architecture | **Keep 400**; use the appropriate `bad-request` response. | **Resolved [ARCH]** |
| D-07 | Framework 401/403/404/405/415 | Product/Arch | **Implement now**, via a **scheme-independent** authorization-result strategy (or demonstrate coverage for every configured scheme); preserve `WWW-Authenticate`. | **Resolved [ARCH]** |
| D-08 | Taxonomy for reachable `BadHttpRequestException` / framework statuses lacking a ticket/KB/platform URI (e.g. 413) | Architecture | **Resolved [ARCH]:** map ticket-mandated, KB-documented, and established Ed-Fi/DMS platform URIs (incl. 415 → `urn:ed-fi:api:unsupported-media-type`); for any reachable status with **none** of these, use RFC 9457 `type: "about:blank"` (§4.2.1), preserve the HTTP status, and use the standard reason phrase as `title`, still including all required members. **Never invent a URN.** Phase 5 audits *which* statuses are reachable (to size tests), but the mapping rule is fixed. | **Resolved** |

---

## 25. Spec challenge record

- **2026-07-23 — Architect challenge (R1 → R2).** Verdict: not approved. Eight required revisions: (1) OAuth must be converted, not exempted; (2) ClaimsManagement non-success DTOs must be converted (D-03 not optional); (3) framework errors must be implemented via a scheme-independent strategy (JWT-event-only is untested under `TestAuthHandler`); preserve `WWW-Authenticate`; (4) move the exception boundary ahead of tenant/config middleware + add a throwing-tenant pipeline test; (5) make `BadHttpRequestException` status-aware and test 400/415/other reachable statuses; (6) do **not** refactor all 12 CRUD modules for media type (remove INV-31b/C07); (7) move the definitive tracked spec to `reference/design/configuration-service/…` (no `git add -f`); (8) verification commands must be executable verbatim. Decisions D-01…D-07 resolved as in §24. **All applied in R2.** D-08 added (conditional).
- **2026-07-23 — Architect challenge (R2 → R3).** Verdict: prior eight blockers resolved; four narrow corrections required before C01: (1) reclassify 415 URI as an **Ed-Fi/DMS platform convention** (not KB-documented) and **resolve D-08 now** via RFC 9457 `about:blank` for statuses lacking a ticket/KB/platform URI; (2) framework shaping must cover **every** bodiless non-2xx response with **no route-based exclusions** and **no status-code-page re-execution** (single callback/middleware; `HasStarted` + body/content-type as the only boundary); (3) refine INV-12 so only recognized field-validation failures with non-empty paths populate `validationErrors` while operational/database/unexpected failures use a safe generic bad-request body (raw detail server-side only); (4) truthful/executable control evidence — accurate V-01/V-02 enumeration, split V-17, corrected §15 ordering statement, mismatch-proof `FailureResponseWriter`. **All applied in R3.** D-08 now **resolved**; no open decisions remain.
- **2026-07-23 — Architect challenge (R3 → R4).** Verdict: all architecture blockers resolved; one **blocking** information-disclosure fix plus control-document cleanups before C01: (1) INV-12 must be **deny-by-default** — `FailureType == "Validation"` without a path (incl. data-layer `ValidationFailure.Errors`) is **not** proven safe, so only path-bearing `"Validation"` and the two fixed `"Structure"` literals may be exposed; all else uses a safe generic bad-request body (raw text server-side only); add a secret-sentinel test on a pathless `"Validation"` message; (2) cleanups — complete V-02 enumeration (add the `FailureResults` `error_description` parser and `MetadataModule.WriteAsync`), advance the self-challenge/end markers to R4, remove stale "exclusions" wording in §29.2, allow `about:blank` in the §10 target JSON `type`, and add `ForUnclassifiedStatus` to the file-impact forecast. **All applied in R4.** No new architecture/product decision.
- **2026-07-23 — Architect approval (R4).** Verdict: **Approved.** No blocking findings remain; the spec adequately covers Jira scope, API contracts, edge cases, security, tests, risks, phased commits, and live verification. Directives: mark the document `Approved — 2026-07-23`; make no substantive changes; commit only the spec as **C01** with message `[DMS-1218] Add CMS error-response compliance implementation spec` using a normal `git add` (no `-f`); report the C01 SHA and stop for commit review before starting C02. Implementation guardrails remain binding (see §6, §17, §16, §12.2.1, §15, §11) — convert every CMS non-success response incl. OAuth errors; preserve protocol success and existing statuses; full contract + `HttpContext.TraceIdentifier`; INV-12 deny-by-default; scheme-independent framework shaping with no route exclusions/re-execution; preserve `WWW-Authenticate`; established taxonomy with `about:blank` only under D-08; DMS and unrelated CRUD modules untouched; maintain §26/§27 after every gate.
- **2026-07-23 — C01 commit review.** Approved. Commit `7e6d52401b4fe92eec25985ff9dbff0aec151f74` verified: parent `5922d4fb`, only the approved spec added, no production/test changes, worktree clean.
- **2026-07-23 — C02 commit review.** Approved. Commit `ded5a4fae4f2c904391f56a2bf8631e609ec3b07` verified: every `FailureResults` method emits `application/problem+json`; `Authorization(correlationId, errors[])` preserves caller errors without IdP parsing; `FailureResponseWriter` derives status from the node, overwrites `correlationId`, no-ops after the response starts; tests cover content type, contract shape, status sync, correlation replacement, explicit errors, missing status, `HasStarted`; no endpoint call-sites or Phase 5 factories changed; `git diff --check` clean. Non-blocking follow-up (applied in C03): align §14's `Authorization` signature with the shipped `Authorization(string correlationId, string[] errors)`.
- **2026-07-23 — C03 commit review.** Approved. Commit `ff18145dac82f21cde872be50196a27081e6a040`: disabled-registration branch returns the Ed-Fi authorization contract; test verifies all mandatory members, exact values, content type, non-empty correlationId; registration-enabled and OAuth paths untouched; §14 signature discrepancy resolved. V-05 accepted.
- **2026-07-23 — C04 commit review.** Approved. Commit `6d90dedbff2e1ec7c4aac1ae5b4ea3d753fe24f2`: all three disabled claims branches return the route-specific Ed-Fi 404 with `TraceIdentifier`; shared assertion verifies the full contract; existing 401/403 authorization tests confirm auth intact; spec ledger accurate. V-06 (22/22) accepted.
- **2026-07-23 — C05 commit review.** Approved. Commit `0b642bf3a4549da3f2f0f83e46ab3cd42ff0a216`: both `GetCurrentClaims` catches preserve `LogError` and return the safe 500 contract; tests reach the handler with authorization + flag enabled, verify every member, reject legacy fields, and prove both sentinels stay private. Phase 2 complete. V-07 (24/24) accepted.
- **2026-07-23 — C06 commit review.** Approved. Commit `5d207987063d7074d977ed6c11fc75250b8d6fb9`: the mapper implements the strict all-or-nothing deny policy; all INV-08…15 statuses preserved; internal text withheld; success responses unchanged; fixtures reach each branch. Phase 3 complete. Two non-blocking follow-ups required in C07 (applied): widen V-07's filter to the three test classes actually run; strengthen safe-validation assertions to exact messages and add a whitespace-padded path proving the key is trimmed.
- **2026-07-23 — C07 commit review.** Approved. Commit `b0ceb7e2623160b0d409db0efc32c9539f3616c4`: middleware order correct; the real tenant-resolution path is exercised; the test proves shaping, correlation, sentinel privacy, repo invocation, and exactly one failure log; C06 follow-ups correctly applied. Two C07 cleanups required in C08 (applied): make V-20's command match the reported combined filter; refactor `PipelineExceptionBoundaryTests` to a `Given_`/`Setup`/`It_` fixture keeping the containing type name so V-20 still matches.
- **2026-07-23 — C08 commit review.** Approved. Commit `46a40c50887934b247dcfbc26e1459160a9897cb`: all four tenant branches use `FailureResponseWriter` with `RequestAborted`; statuses 400/400/500/400; full contract + sentinel privacy covered; logging/sanitization/bypass/valid-tenant behavior intact; C07 convention cleanup applied. One non-blocking C09 correction (applied): make V-08/V-20 evidence describe the exact filter executed (no implied `ClaimsManagementModuleTests` rerun in C08).
- **2026-07-23 — C09 commit review.** Approved. Commit `496983352573fff551d9ff3ee9644d35aa0cd9c4`: middleware short-circuit + generic 500 contract + Critical logging + sentinel privacy + exact correlationId; corrected verification evidence. Phase 4 complete. One C10 cleanup (applied): `entry.State is not null` per AGENTS. *(Note: `is not null` is a compile error inside a FluentAssertions expression-tree predicate (CS8122); implemented via `List.Exists` per SonarAnalyzer S6605 so `is not null` is honored and compiles.)*
- **2026-07-23 — C10 commit review.** Production approved (`8d5586948e77286fdabe84bdf6a1e60c1df1f0a5`): middleware placement, status mappings, body guards, header preservation, no re-execution, factories, and full-suite evidence all align with the design. **Revision required (tests only):** `FrameworkErrorResponseTests` used a flat `[TestFixture]` with no `SetUp` and non-`It_` methods, violating AGENTS. Fix in a narrow follow-up (C10a): nested `Given_` fixtures, `[SetUp]` arrange+act, `It_` asserts, container name kept for the V-11 filter, shared helpers scoped for S3398, no production change; re-run V-11 + full suite. **Applied in C10a.**
- **2026-07-23 — C10/C10a commit review.** Approved. C10a (`acdcc27a3aa32919d8b76e6eea1ed47713004154`) changes only test/spec, retains all nine scenarios, and follows `Given_`/`Setup`/`It_`. One C11 control-document cleanup (applied): V-11's C10 "31" was actually a combined run including `ReportInvalidConfigurationMiddlewareTests` (12); the exact V-11 filter was 19 at C10 and is 27 at C10a (10 `AuthorizationTests` + 17 framework `It_`).
- **2026-07-23 — C11 commit review.** Production approved (`b5a7e92a15214e7966ded36d21e0f41a8649d621`): status-aware 400/415/413 mapping, mismatch-proof writing, trace correlation, and exception-message protection all match the spec. **Revision required (tests only):** `HandleAsync` asserted `handled` inside `[SetUp]` (assertions belong in `It_`). Fix in C11a: return `handled`, capture per fixture, add `It_reports_the_exception_as_handled`; keep production unchanged; make V-12 evidence exact (run isolated `~GlobalExceptionHandlerTests`, then the Pipeline/Framework regression separately). **Applied in C11a.**
- **2026-07-23 — C11a commit review.** Approved. Commit `934e1ad4e70864c23821bc70f1972199cf4b0cb8`: test/spec only; all `handled` assertions moved into `It_` tests; `[SetUp]` limited to arrange/act; five scenarios + contract assertions preserved; exact separate V-12 (18/18) and regression (29/29) evidence; clean worktree and `git diff --check`.
- **2026-07-23 — C12 commit review.** Production approved (`c5d1528343d7ad3780722b0e2d78027cd86e057f`): all three 400 branches use the Ed-Fi contract, correlationId from `TraceIdentifier`, protocol success untouched. **Revision required (tests only):** the new OAuth tests were monolithic `When_…`/non-`Given_` fixtures. Fix in C12a: rework the five scenarios into nested `Given_` fixtures with `[SetUp]` + `It_`; keep legacy tests untouched; truthfully update the V-13 filter; strengthen the shared assertion to reject `error`/`error_description` keys; no production change. **Applied in C12a.**
- **2026-07-23 — C12a commit review.** Approved. Commit `75844da3747a58bf3e583af696937733d67fd2fd`: test/spec only; five nested `Given_` fixtures with `[SetUp]` + `It_`; legacy identity tests preserved; both legacy OAuth error properties asserted absent; introspection/revocation successes covered; accurate V-13 filter/evidence; clean worktree. Directive for C13: delete only the dead frontend middleware, its frontend integration-extension file, and its tests; expand V-14 to include `AddOpenIddictEnhancements`/`UseOpenIddictEnhancements`; do not remove the live backend `IEnhancedTokenValidator`/`IOpenIdConnectConfigurationProvider`, implementations, or backend registrations. **Applied in C13.**
- **2026-07-23 — C13 commit review.** Approved. Commit `5395822e12850ea095d546234fa35907b6be7307`: deletes only the three approved dead frontend files; leaves no references to the middleware or its four integration aliases; preserves the live backend interfaces/implementations/registrations; records the expanded V-14 audit; clean worktree. One non-blocking C14 cleanup (applied): remove the duplicate C14 checklist row.
- **2026-07-23 — C14 (final phase).** Static audits re-run (V-01/V-02 acceptance met: 0 bare + 0 ad-hoc; V-03 no obsolete URI; V-14 no dead refs); `csharpier format src/config` reformatted 0 C# files (V-17); full frontend suite 600/600 (V-15); backend `FailureResponseTests` 12/12 (V-16); `git diff --check` clean (V-18); Definition of Done (§28) all satisfied; duplicate C14 row removed. Submitted for final review.
- Further architect notes to be appended here on re-review.

---

## 26. Commit-progress checklist

**Tracking rule (self-SHA):** a commit cannot contain its own SHA. Each commit's **Actual SHA** is recorded in the *next* progress update (or during the review immediately following the commit) — never by amending an approved commit. Each implementation commit (or tightly-related series) stops for architect review. The approved spec is **C01**, committed to this **tracked** path with a normal `git add` (no `-f`).

| ID | Planned message | Phase | Scope | Depends on | Required tests | State | Actual SHA | Review notes | Rework | Approval |
|---|---|---|---|---|---|---|---|---|---|---|
| C01 | `[DMS-1218] Add CMS error-response compliance implementation spec` | 0 | This spec (approved), tracked path | — | n/a | committed | `7e6d52401b4fe92eec25985ff9dbff0aec151f74` | Approved R4 2026-07-23; architect-reviewed and approved | — | 2026-07-23 |
| C02 | `[DMS-1218] Centralize CMS error helpers, writer, and content type` | 1 | `FailureResults` problem+json (`Unknown`/`NotFound`) + `Authorization(correlationId, errors[])` overload + `FailureResponseWriter`; helper/writer tests | C01 | FailureResponseTests, FailureResultsTests, FailureResponseWriterTests | committed | `ded5a4fae4f2c904391f56a2bf8631e609ec3b07` | Architect-approved 2026-07-23; 28 helper/writer + 9 backend + 538/538 frontend green; no endpoint call-sites changed | — | 2026-07-23 |
| C03 | `[DMS-1218] Return structured 403 for disabled client registration` | 2 | INV-01 (`IdentityModule:128` → `FailureResults.Authorization`) + full-contract assertion | C02 | RegisterEndpointTests | committed | `ff18145dac82f21cde872be50196a27081e6a040` | Architect-approved 2026-07-23; full 403 contract asserted; V-05 green | — | 2026-07-23 |
| C04 | `[DMS-1218] Return structured 404 for disabled claims endpoints` | 2 | INV-02/03/04 (`ClaimsManagementModule` reload/upload/current-claims disabled → `FailureResults.NotFound`; `HttpContext` added to `ReloadClaims`/`UploadClaims`) | C02 | ClaimsManagementModuleTests | committed | `6d90dedbff2e1ec7c4aac1ae5b4ea3d753fe24f2` | Architect-approved 2026-07-23; full 404 contract + route-specific detail; auth preserved; V-06 green (22/22) | — | 2026-07-23 |
| C05 | `[DMS-1218] Return structured 500 for current-claims exceptions` | 2 | INV-05/06 (`GetCurrentClaims` JsonException/InvalidOperationException catches → `FailureResults.Unknown`; `LogError` preserved) | C02 | ClaimsManagementModuleTests | committed | `0b642bf3a4549da3f2f0f83e46ab3cd42ff0a216` | Architect-approved 2026-07-23; full 500 contract + sentinel absence + no legacy `error`/`message`; V-07 green (24/24) | — | 2026-07-23 |
| C06 | `[DMS-1218] Convert claims-management non-success DTOs to Ed-Fi errors` | 3 | INV-08…15 (reload/upload non-success DTOs → `FailureResults` per §12.2.1 deny-by-default; new `FailureResults.BadRequest`/`DataValidation` helpers; `LogError` + success responses preserved) | C05 | ClaimsManagementModuleTests, FailureResultsTests | committed | `5d207987063d7074d977ed6c11fc75250b8d6fb9` | Architect-approved 2026-07-23; deny-by-default all-or-nothing; V-07 65/65; scoped audit clean. C07 follow-ups: V-07 filter widened; safe-validation messages/trim asserted | — | 2026-07-23 |
| C07 | `[DMS-1218] Move exception boundary ahead of tenant/config middleware` | 4 | INV-36 (`Program.cs` reorder: `UseExceptionHandler` before `TenantResolution`/`ReportInvalidConfiguration`) + pipeline test; + C06 follow-ups (V-07 filter, safe-validation message/trim assertions) | C02 | PipelineExceptionBoundaryTests, RequestLoggingMiddlewareTests | committed | `b0ceb7e2623160b0d409db0efc32c9539f3616c4` | Architect-approved 2026-07-23; reorder proven by pipeline test (tenant exception → Ed-Fi 500 + one HttpRequestFailed). C08 follow-ups: V-20 filter widened; `PipelineExceptionBoundaryTests` refactored to Given_/Setup/It_ | — | 2026-07-23 |
| C08 | `[DMS-1218] Return structured errors from tenant resolution` | 4 | INV-19…22 (`TenantResolutionMiddleware` 4 branches → `FailureResponseWriter` + `FailureResponse.ForBadRequest`/`ForUnknown`; statuses 400/400/500/400 preserved per D-06; `SanitizeForLog`/bypass/context behavior preserved) | C02, C07 | TenantResolutionMiddlewareTests | committed | `46a40c50887934b247dcfbc26e1459160a9897cb` | Architect-approved 2026-07-23; per-producer body contract + sentinel privacy; statuses preserved. C09 follow-up: V-08/V-20 evidence made filter-exact | — | 2026-07-23 |
| C09 | `[DMS-1218] Return structured 500 for invalid configuration` | 4 | INV-23 (`ReportInvalidConfigurationMiddleware` → `FailureResponseWriter` + `FailureResponse.ForUnknown`; `LogCritical` preserved, short-circuit preserved, config text server-side only) | C02, C07 | ReportInvalidConfigurationMiddlewareTests | committed | `496983352573fff551d9ff3ee9644d35aa0cd9c4` | Architect-approved 2026-07-23; 500 contract + sentinel privacy + Critical logging + no `next`. C10 follow-up: `entry.State is not null` (AGENTS) | — | 2026-07-23 |
| C10 | `[DMS-1218] Shape framework 401, 403, 404, 405, and 415 responses` | 5 | INV-25…29 (`FrameworkErrorResponseMiddleware` after `UseRouting`, before CORS/authn/authz; single non-re-executing status-code shaper; `FailureResponse.ForMethodNotAllowed`/`ForUnsupportedMediaType` added) | C02 | FailureResponseTests, AuthorizationTests, FrameworkErrorResponseTests | committed | `8d5586948e77286fdabe84bdf6a1e60c1df1f0a5` | Production approved 2026-07-23 (functionally sound); **test-convention rework required** (`FrameworkErrorResponseTests` lacked `Given_`/`Setup`/`It_`) — addressed in C10a | — | 2026-07-23 |
| C10a | `[DMS-1218] Align framework error tests with repository conventions` | 5 | C10 follow-up: refactor `FrameworkErrorResponseTests` to nested `Given_` fixtures + `[SetUp]` + `It_` (no production change) | C10 | FrameworkErrorResponseTests | committed | `acdcc27a3aa32919d8b76e6eea1ed47713004154` | Architect-approved 2026-07-23; 9 scenarios preserved, `Given_`/`Setup`/`It_`, no production change. C11 follow-up: V-11's stale "31" count corrected | — | 2026-07-23 |
| C11 | `[DMS-1218] Make bad HTTP request handling status-aware` | 5 | INV-30 (`GlobalExceptionHandler` `BadHttpRequestException` status-aware via `FailureResponseWriter`: 400→`ForBadRequest`, 415→`ForUnsupportedMediaType`, else `ForUnclassifiedStatus`/`about:blank` D-08; new `FailureResponse.ForUnclassifiedStatus`; `TraceId` header preserved; no message leak) | C02, C10 | GlobalExceptionHandlerTests, FailureResponseTests | committed | `b5a7e92a15214e7966ded36d21e0f41a8649d621` | Production approved 2026-07-23 (status-aware 400/415/413, mismatch-proof writing, trace correlation, message protection); **test-convention rework required** (assertion in `[SetUp]`) — addressed in C11a | — | 2026-07-23 |
| C11a | `[DMS-1218] Align exception handler tests with repository conventions` | 5 | C11 follow-up: move the `handled` assertion out of `[SetUp]` into an `It_reports_the_exception_as_handled` per fixture (no production change) | C11 | GlobalExceptionHandlerTests | committed | `934e1ad4e70864c23821bc70f1972199cf4b0cb8` | Architect-approved 2026-07-23; `[SetUp]` arrange/act only; V-12 isolated 18/18; regression 29/29 | — | 2026-07-23 |
| C12 | `[DMS-1218] Convert OAuth/OIDC error responses to Ed-Fi contract` | 6 | INV-16/17/18 (`IdentityModule` token unsupported-grant / introspect / revoke missing-token → `FailureResults.BadRequest`, 400 preserved; protocol success — token 200, introspection `{active:false}`, revoke 200 — untouched) | C02, C06 | IdentityModuleTests (Token/Introspect/Revoke) | committed | `c5d1528343d7ad3780722b0e2d78027cd86e057f` | Production approved 2026-07-23 (three 400 branches → Ed-Fi contract, TraceIdentifier correlation, success untouched); **test-convention rework required** — addressed in C12a | — | 2026-07-23 |
| C12a | `[DMS-1218] Align OAuth endpoint tests with repository conventions` | 6 | C12 follow-up: rework the five new OAuth scenarios into nested `Given_` fixtures (`OAuthEndpointErrorTests` container) with `[SetUp]` + `It_`; strengthen the shared assertion to reject `error`/`error_description` keys (no production change) | C12 | OAuthEndpointErrorTests | committed | `75844da3747a58bf3e583af696937733d67fd2fd` | Architect-approved 2026-07-23; nested `Given_` fixtures, legacy tests untouched, `error`/`error_description` absence asserted; V-13 29/29 | — | 2026-07-23 |
| C13 | `[DMS-1218] Remove unwired OpenIddict error middleware` | 6 | INV-24 (delete dead frontend `OpenIddictErrorHandlingMiddleware.cs`, `OpenIddictIntegrationExtensions.cs`, and `OpenIddictErrorHandlingMiddlewareTests.cs`; backend `IEnhancedTokenValidator`/`IOpenIdConnectConfigurationProvider` + their live backend registrations preserved) | C12 | (removal — full frontend suite regression) | committed | `5395822e12850ea095d546234fa35907b6be7307` | Architect-approved 2026-07-23; 3 dead frontend files removed; V-14 (all 7 names) → no references; backend types/registrations intact; full frontend suite 600/600 | — | 2026-07-23 |
| C14 | `[DMS-1218] Format and final regression cleanup` | 7 | Final static audit, formatting, regression, DoD, and duplicate-row cleanup | all | full CMS frontend suite + backend `FailureResponseTests` | in progress | recorded post-commit | — | — | — |

---

## 27. Live verification table (executable verbatim)

State: `not run` · `run (baseline)` · `passed` · `failed` · `n/a`. Commands run from repo root. Full project paths and exact filters. `dotnet test` and formatting are **not run** during drafting. A search is marked `passed` **only when its acceptance condition is already met** (e.g. "no matches"); a search that was executed at drafting-time but whose acceptance is met only *after* implementation is marked **`run (baseline)`** with a **complete** enumeration of matches — it is never labelled `passed` while non-compliant matches remain.

| VID | Phase/Commit | Command (verbatim) | Expected | State | Actual | Timestamp | SHA | Evidence | Rerun? |
|---|---|---|---|---|---|---|---|---|---|
| V-01 | 0/7 | `rg -n "Results\.(Forbid\|NotFound\|BadRequest\|Unauthorized\|Conflict\|StatusCode)\|TypedResults\.(Forbid\|NotFound\|BadRequest\|Unauthorized\|Conflict\|StatusCode)\|Results\.Json\(" src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore -g "*.cs"` | Post-impl acceptance: every match is a compliant helper/writer or preserved-compliant inline body | passed | **C14 re-run (post-implementation): acceptance met** — two focused risk-greps returned **0 bare framework error results** (`Results.Forbid()/NotFound()/…()` with empty parens) and **0 ad-hoc anonymous error JSON** (`Results.Json(new {…}, statusCode: 4xx/5xx)`); every remaining match is a compliant `FailureResponse.ForX`/`FailureResults` call or a success `Results.Ok/Json`. Baseline (C0) non-compliant matches (now all converted) were INV-01 (`Results.Forbid`), INV-02/03/04 (`Results.NotFound`), INV-05/06 (`Results.Json{error,message}`), INV-08…15 (`Results.Json` DTOs), INV-16/17/18 (`Results.Json` OAuth). This command does not surface middleware/framework producers (INV-19…29) — see V-02. | 2026-07-23 | C14 | Baseline @ `5922d4fb`; canonical `rg` acceptance re-run at C14 | Yes |
| V-02 | 0/7 | `rg -n "WriteAsJsonAsync\|WriteAsync\|Response\.StatusCode\|new \{ error\|new \{ active\|error_description" src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore -g "*.cs"` | Post-impl acceptance: no ad-hoc `{error,message}`/`{error,error_description}` error bodies remain | passed | **C14 re-run: acceptance met** — no ad-hoc `{error,message}`/`{error,error_description}` error bodies remain. The only surviving matches are compliant/non-producer: `FailureResults.cs` `GetIdentityErrorDetails` (a comment + the IdP-JSON **parser** that reads `error_description`, INV-33 helper), `GlobalExceptionHandler`/`FrameworkErrorResponseMiddleware`/`FailureResponseWriter` `WriteAsync` (compliant Ed-Fi writers), `RequestLoggingMiddleware` `Response.StatusCode` (read-only), and success `new { active }`/`MetadataModule` OpenAPI 200. Baseline (C0) producers now converted: TenantResolution ×4 (INV-19…22), ReportInvalidConfiguration (INV-23), ClaimsManagement (INV-05/06), IdentityModule OAuth (INV-16/17/18); dead `OpenIddictErrorHandlingMiddleware` (INV-24) removed at C13. | 2026-07-23 | C14 | Acceptance re-run at C14 | Yes |
| V-03 | 0/7 | `rg -n "bad-request:data-validation-failed" src/config` | No matches | passed | No matches in `src/config` (only `src/dms`, OOS) | 2026-07-23 | 5922d4fb | Confirms F7 already satisfied | Yes |
| V-04 | 1/C02, 5/C10 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResponseTests" --nologo --verbosity minimal` | Pass (incl. `…:data`; + 405/415/about:blank factories) | passed | C02: 9/9 (F7 `…:bad-request:data` intact). C10: 11/11 (+`ForMethodNotAllowed` 405, `ForUnsupportedMediaType` 415). C11: **12/12** (+`ForUnclassifiedStatus` → `about:blank`, title = reason phrase, empty detail/extensions) | 2026-07-23 | C11 (pre-commit) | Regression guard F7 + 405/415 + about:blank factories | Yes |
| V-21 | 1/C02 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResultsTests\|FullyQualifiedName~FailureResponseWriterTests" --nologo --verbosity minimal` | Pass; `application/problem+json` on `NotFound`/`Unknown`/`Authorization`; writer derives status from node + sets correlationId; D-02 errors verbatim | passed | 28/28 passed | 2026-07-23 | C02 (pre-commit) | New C02 helper/writer tests | Yes |
| V-05 | 2/C03 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~RegisterEndpointTests.When_allow_registration_is_disabled" --nologo --verbosity minimal` | Pass; 403 `security:authorization` body | passed | 1/1 passed; 403 + `application/problem+json` + type `security:authorization` + title "Authorization Failed" + safe detail + body status 403 + non-empty correlationId + empty validationErrors + errors `["Registration is disabled."]` | 2026-07-23 | C03 (pre-commit) | Body assertions added | Yes |
| V-06 | 2/C04 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ClaimsManagementModuleTests" --nologo --verbosity minimal` | Pass; 404 `not-found` body | passed | 22/22 passed; three full-access disabled-flag endpoints assert 404 + `application/problem+json` + route-specific detail (reload/upload/current-claims) + type `not-found` + title "Not Found" + status 404 + non-empty correlationId + empty validationErrors + empty errors; existing 401/403 auth tests still green | 2026-07-23 | C04 (pre-commit) | 404 body-shape fixtures added; existing fixtures extended, none created/replaced | Yes |
| V-07 | 2-3/C05-C06 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ClaimsManagementModuleTests|FullyQualifiedName~FailureResultsTests|FullyQualifiedName~FailureResponseWriterTests" --nologo --verbosity minimal` | Pass; 500/400 Ed-Fi bodies; no `ex.Message`/DTO shape | passed (C06 scope) | 65/65 passed (ClaimsManagementModuleTests + helper/writer tests). INV-05/06 500 contract; INV-08/10/15 → 500; INV-09/13/14 → generic 400; INV-11 → data-validation `Claims`; INV-12 deny-by-default (grouping, both structure literals, database/pathless/future/mixed sentinels all denied to generic 400, safe entries dropped in mixed); every sentinel absent; no legacy DTO/`error`/`message` shape. Scoped audit: no `ex.Message`/non-success DTO in module (only the 200 `Results.Json` success remains) | 2026-07-23 | C06 (pre-commit) | INV-05/06 (C05) + INV-08…15 (C06) | Yes |
| V-08 | 4/C08-C09 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ReportInvalidConfigurationMiddlewareTests\|FullyQualifiedName~TenantResolutionMiddlewareTests\|FullyQualifiedName~PipelineExceptionBoundaryTests" --nologo --verbosity minimal` | Pass; body-shape asserted; statuses preserved | passed | Exact C09 combined run: **59/59 passed**. Tenant assertions: missing/empty header → 400 `bad-request` "The 'Tenant' header is required when multi-tenancy is enabled"; not-found → 400 "Invalid tenant: …"; lookup failure → 500 `internal-server-error` (DB sentinel absent); unexpected result → 400 "Failed to validate tenant"; XSS name → sanitized, no markup; each asserts content type, exact detail, type/title, known `TraceIdentifier` as `correlationId`, empty extension members, no legacy `error`/`message` | 2026-07-23 | C09 (pre-commit) | Filter = the exact combined command executed | Yes |
| V-09 | 4/C09 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ReportInvalidConfigurationMiddlewareTests\|FullyQualifiedName~TenantResolutionMiddlewareTests\|FullyQualifiedName~PipelineExceptionBoundaryTests" --nologo --verbosity minimal` | Pass; 500 Ed-Fi body | passed | Exact C09 combined run: **59/59 passed**. Invalid-config assertions: 500 + `application/problem+json` + type/title `internal-server-error`/"Internal Server Error" + empty detail + status 500 + `TraceIdentifier` "trace-config" as correlationId + empty validationErrors/errors + no legacy `error`/`message` + config sentinel absent from body + 2 `Critical` logs (one carrying the sentinel) + next delegate not invoked | 2026-07-23 | C09 (pre-commit) | New test; same combined command as V-08/V-20 | Yes |
| V-10 | 4-5 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~GlobalExceptionHandlerTests" --nologo --verbosity minimal` | Pass | not run | | | | Regression + INV-30 | Yes |
| V-11 | 5/C10 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~AuthorizationTests|FullyQualifiedName~FrameworkErrorResponseTests" --nologo --verbosity minimal` | Pass; 401/403/404/405/415 bodies; 2xx untouched; `WWW-Authenticate` preserved | passed | **Exact V-11 filter (C10a): 27/27** = 10 `AuthorizationTests` + 17 `FrameworkErrorResponseTests` `It_` methods. Coverage: real-JWT 401 (+ `WWW-Authenticate: Bearer`); TestAuth 401 & 403 (scheme-independent); health-lookalike 404 (no route exclusion); wrong-method 405 (+ `Allow: GET`); text/plain → 415; existing structured 404 unchanged; health 200 unchanged; CORS preflight 204 empty/unchanged; each shaped response asserts content type + exact taxonomy/title/detail/status + non-empty correlationId + empty extension members. *(Correction: the "31" recorded during C10 was a broader combined run that also included `ReportInvalidConfigurationMiddlewareTests` (12); the exact V-11 filter at C10 was 19 = 10 + 9 original framework tests, before the C10a `It_` split raised framework to 17.)* | 2026-07-23 | C10a | Scheme-independent; nested `Given_` fixtures | Yes |
| V-12 | 5/C11 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~GlobalExceptionHandlerTests" --nologo --verbosity minimal` | Pass; malformed body → 400 `bad-request`; 415 → `unsupported-media-type` (Ed-Fi URI); any reachable status with no ticket/KB/platform URI (413) → `about:blank` + reason phrase; body `status` == HTTP status | passed | **Isolated `~GlobalExceptionHandlerTests` (C11a): 18/18.** Fixtures: 400→`bad-request` "The request was malformed or invalid."; 415→`unsupported-media-type`; **413→`about:blank`, title = `ReasonPhrases.GetReasonPhrase(413)` ("Payload Too Large")**; validation→`bad-request:data` with grouped validationErrors; generic→500 `internal-server-error`. Each asserts handled-flag (`It_reports_the_exception_as_handled`), exact status/type/title/detail, `application/problem+json`, correlationId == `TraceIdentifier` == `TraceId` header, empty extensions where appropriate, and sentinel absence. Pipeline+Framework regression run **separately**: 29/29 | 2026-07-23 | C11a (pre-commit) | Status-aware `BadHttpRequestException`; D-08 `about:blank` | Yes |
| V-13 | 6/C12a | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~RegisterEndpointTests\|FullyQualifiedName~TokenEndpointTests\|FullyQualifiedName~OAuthEndpointErrorTests" --nologo --verbosity minimal` | Pass; token/introspect/revoke errors = Ed-Fi contract; introspection `{active:false}`=200 | passed | **29/29 passed** (RegisterEndpointTests + TokenEndpointTests legacy + `OAuthEndpointErrorTests` nested `Given_` fixtures). Unsupported grant → 400 `bad-request` "The specified grant type is not supported."; introspect/revoke missing-token → 400 `bad-request` "The token parameter is missing." (assertion also rejects `error`/`error_description` keys); **introspect with token → 200 `{active:false}`** and **revoke with token → 200** preserved. Filter names the actual fixture/container classes (no `IdentityModuleTests` *type* exists) | 2026-07-23 | C12a (pre-commit) | Nested `Given_` fixtures under `OAuthEndpointErrorTests` | Yes |
| V-14 | 0/C13 | `rg -n "OpenIddictErrorHandlingMiddleware\|UseOpenIddictErrorHandling\|OpenIddictIntegrationExtensions\|AddEnhancedOpenIddict\|UseEnhancedOpenIddict\|AddOpenIddictEnhancements\|UseOpenIddictEnhancements" src/config -g "*.cs"` | Baseline: only definition + tests (not wired); post-C13 acceptance: **no references** | passed | **Baseline (C0):** matches only in the 3 dead files (middleware, extension incl. the `AddOpenIddictEnhancements`/`UseOpenIddictEnhancements` aliases, tests); no pipeline invocation. **After C13: 0 matches.** Backend `IEnhancedTokenValidator`/`IOpenIdConnectConfigurationProvider` (defined in Backend.OpenIddict; registered live in Postgres/Mssql/JwtAuthentication extensions) preserved | 2026-07-23 | C13 (pre-commit) | Expanded to all aliases; dead code fully removed | Yes |
| V-15 | 7 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --nologo --verbosity minimal` | All pass | passed (interim @ C13) | Latest interim run at C13: **600/600 passed** — confirms compilation and no regressions after removing the dead middleware + its tests. (Interim history: C02 538, C10a 594.) Formal acceptance re-run at Phase 7 | 2026-07-23 | C13 (pre-commit) | Full CMS frontend unit suite; re-run at Phase 7 | Yes |
| V-16 | 7 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResponseTests" --nologo --verbosity minimal` | Pass | passed | 12/12 passed (incl. `ForMethodNotAllowed`, `ForUnsupportedMediaType`, `ForUnclassifiedStatus`; F7 `…:bad-request:data` intact) | 2026-07-23 | C14 | Narrowly-necessary backend test | No |
| V-17a | 7/C14 | `dotnet csharpier format src/config` | Formatting completes with exit 0 | passed | Formatted 376 files, exit 0 | 2026-07-23 | C14 | TC-10 mitigation (step 1) | No |
| V-17b | 7/C14 | `git diff --name-only` | Only in-scope files listed; revert any unrelated formatter-only change | passed | After the src/config format, `git diff --name-only` lists **only** the tracked spec `.md`; **zero C# files reformatted** — every touched file was already CSharpier-clean, so nothing to revert | 2026-07-23 | C14 | TC-10 mitigation (step 2) | No |
| V-18 | 7/C14 | `git diff --check` | No whitespace errors | passed | Clean (no whitespace/conflict errors) | 2026-07-23 | C14 | — | No |
| V-19 | 7/C14 | `git status --porcelain` | Only in-scope files; clean worktree after commit | passed | Every commit C01–C14 ended with a clean worktree; all changes confined to `src/config` (frontend, datamodel, backend tests) + the tracked spec. Final clean worktree confirmed after the C14 commit | 2026-07-23 | C14 (post-commit) | DoD | No |
| V-20 | 4/C07-C09 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~ReportInvalidConfigurationMiddlewareTests\|FullyQualifiedName~TenantResolutionMiddlewareTests\|FullyQualifiedName~PipelineExceptionBoundaryTests" --nologo --verbosity minimal` | Pass; throwing tenant resolution → full Ed-Fi 500 | passed | Exact C09 combined run: **59/59 passed**. Pipeline: tenant repo throws → 500 `internal-server-error` contract, sentinel absent, `TraceId` header == body correlationId, repo reached, exactly one HttpRequestFailed logged. `PipelineExceptionBoundaryTests` is a nested `Given_…` fixture (FQN still matches this filter). (First proven in C07 at 56/56 via a filter that also included RequestLoggingMiddleware + ClaimsManagementModuleTests.) | 2026-07-23 | C09 (pre-commit) | Filter = the exact combined command executed | Yes |

---

## 28. Definition-of-done checklist

- [x] Confirmed findings F1–F6 fixed; F7 verified already-compliant with regression coverage.
- [x] `FailureResponse.ForDataValidation(...)` emits `urn:ed-fi:api:bad-request:data` (verified, unchanged).
- [x] Disabled registration → structured 403 (`security:authorization`), `errors: ["Registration is disabled."]`.
- [x] Disabled claims endpoints → structured 404 (`not-found`).
- [x] Current-claims exceptions → structured 500 (`internal-server-error`), no `ex.Message`.
- [x] ClaimsManagement non-success DTOs converted (INV-08…15); statuses preserved; `validationErrors` populated where field-level data exists; no leaks.
- [x] Tenant-resolution + invalid-config middleware emit contract bodies; exception boundary precedes them (no uncontrolled 500).
- [x] Framework 401/403/404/405/415 shaped scheme-independently for every bodiless response (no route exclusions, no re-execution); `WWW-Authenticate` preserved; 2xx/204 untouched.
- [x] `BadHttpRequestException` status-aware; ticket/KB/platform statuses mapped; unclassified reachable statuses use RFC 9457 `about:blank` (D-08); no URI invented.
- [x] OAuth/OIDC non-success responses converted; protocol success preserved; dead OpenIddict middleware removed.
- [x] No bare framework results, ad-hoc `{error,message}`, or `{error,error_description}` remain for CMS non-success responses.
- [x] No exception/provider/DB/config/token/secret detail in any error body.
- [x] `FailureResults`/new writers use `application/problem+json`; existing compliant inline bodies unchanged.
- [x] Unit tests assert full contract per changed branch; compliant behavior preserved.
- [x] Targeted + full CMS frontend unit suites pass.
- [x] `dotnet csharpier format src/config` run; no unrelated diffs; `git diff --check` clean.
- [x] Final changed-file inspection confirms scope; worktree clean.

---

## 29. Self-challenge (adversarial review, current at R4)

Labels: **[JIRA fact]**, **[Repo fact]**, **[Inference]**, **[Recommendation]**, **[Product/architect-owned decision]**.

### 29.1 Interpretations (post-architect)
The architect fixed the interpretation to the **strict reading**: every CMS non-success response — application, protocol (OAuth), and framework — uses the Ed-Fi contract, with the single carve-out that already-compliant inline bodies are not rewritten merely for media type. Two residual interpretation seams remain and are handled:
- **A — "framework shaping via JWT events".** Rejected: untested under `TestAuthHandler`, scheme-specific. **[Repo fact]** → replaced by a scheme-independent status-code strategy (§15).
- **B — "`BadHttpRequestException` is always 400".** Rejected: the exception carries a `StatusCode`. **[Repo fact]** → status-aware handling (INV-30), with D-08 for undocumented reachable statuses.

### 29.2 Potential over-expansion (now bounded)
- **Content-type module rewrite** removed (INV-31b/C07) per D-05 — the largest prior over-expansion. **[architect-owned decision]**
- **Framework status-code shaping (R-02)** is the widest-blast-radius change; bounded by the status+bodiless trigger, the `HasStarted` + body/content-type boundary, and 2xx/204-untouched tests — with **no** route/path exclusions and **no** re-execution.
- **Dead-middleware removal (INV-24)** is bounded to genuinely unreferenced code (verified unwired).

### 29.3 Potential under-implementation (guarded)
- **Framework 401/403 empty bodies (INV-25/26)** — the clearest "no `correlationId`" violation; implemented now (D-07), scheme-independent so it is actually exercised in tests.
- **Uncontrolled 500s (INV-36)** — exceptions in pre-boundary middleware; fixed by reorder + pipeline test.
- **`ex.Message` leaks beyond the two named findings (INV-09/10/13/14/15)** — enumerated and fixed regardless of DTO conversion outcome.
- **Non-400 `BadHttpRequestException` (INV-30)** — audited and tested, not silently forced to 400.

### 29.4 OAuth interoperability risk (R-01)
`{error,error_description}` is the OAuth/OIDC standard error shape; conversion may break standards-based clients. **[Inference]** Per architect, the ticket governs; risk is documented for release notes, status 400 preserved, `correlationId` added. **[architect-owned decision]**

### 29.5 Framework-response coverage risk (R-02)
Scheme-independent status-code shaping could affect success/204/CORS-preflight or double-write. **[Inference]** Per the architect, there are **no route/path exclusions** and **no re-execution**: a single status-code callback/middleware rewrites **only bodiless non-2xx** responses, keyed off the final status code with `HasStarted` + body/content-type as the sole boundary. 2xx responses (health, OpenAPI, success) are inherently untouched because they are not error statuses; an existing **non-empty** error body is left alone only because its producer is separately verified compliant (§12). `WWW-Authenticate` preserved. Integration tests assert 2xx/204 untouched and cover every configured scheme (self-contained, Keycloak, test).

### 29.6 Information-disclosure risk (R-06)
INV-05/06/09/10/13/14/15 leak `ex.Message`. **[Repo fact]** All stop; 500s use `ForUnknown`; server-side logging preserved; tests assert no `message`/exception text.

### 29.7 Changes made because of the architect challenge (R1 → R2)
1. OAuth: removed the exemption recommendation; now **convert** (INV-16/17/18), preserve protocol success, remove dead middleware (INV-24). (§16, §12.3)
2. ClaimsManagement DTOs: **convert all** non-success (INV-08…15) with a precise mapping; D-03 no longer a product option. (§12.2, Phase 3)
3. Framework errors: **scheme-independent** strategy (not JWT-event-only); preserve `WWW-Authenticate`; demonstrate coverage across schemes. (§15, Phase 5)
4. Pipeline: **move exception boundary** ahead of tenant/config middleware + throwing-tenant pipeline test (INV-36). (§9, §15, Phase 4)
5. `BadHttpRequestException`: **status-aware** mapping + reachable-status audit (INV-30, D-08). (§9, Phase 5)
6. Removed the **repo-wide media-type rewrite** (INV-31b/C07); `FailureResults`/writers use `application/problem+json`, compliant inline bodies unchanged. (§12.6, §13, §19)
7. **Moved** the definitive spec to the tracked `reference/design/configuration-service/…` path; C01 uses a normal `git add` (no `-f`); old ignored copy removed. (§1)
8. Verification commands rewritten to be **executable verbatim** with full project paths and exact filters; drafting-run searches recorded as `passed` with evidence. (§22, §27)

### 29.7b Changes made because of the second architect challenge (R2 → R3)
1. **415 + D-08:** reclassified `urn:ed-fi:api:unsupported-media-type` as an Ed-Fi/DMS platform convention (DMS Core `FailureResponse.cs:47`), not KB-documented (§3.1, §11); **resolved D-08** — unclassified reachable statuses use RFC 9457 `about:blank` + reason phrase, via a new `ForUnclassifiedStatus` factory (§11, §14, §24).
2. **Framework shaping:** removed all route/path exclusions and status-code-page re-execution; a single status-code callback/middleware shapes every bodiless non-2xx response, guarded only by `HasStarted` + body/content-type; non-empty non-compliant bodies are fixed at their producer (§13, §15, §29.5).
3. **INV-12:** added the precise §12.2.1 mixed-failure mapping (validation-with-path → `validationErrors`; operational/database/unexpected → safe generic `ForBadRequest`; never expose internal text), with separate field-validation and operational/database tests.
4. **Evidence:** made `FailureResponseWriter` mismatch-proof (HTTP status derived from the node); corrected the stale §15 statement about invalid-config middleware ordering; enumerated V-01/V-02 matches completely and marked them `run (baseline)` (not `passed`); split V-17 into V-17a/V-17b.

### 29.7c Changes made because of the third architect challenge (R3 → R4)
1. **INV-12 deny-by-default (blocking, information disclosure):** rewrote §12.2.1 so a backend failure message reaches the body **only** for path-bearing `"Validation"` failures or the two fixed `"Structure"` literals; pathless `"Validation"` (incl. data-layer `ValidationFailure.Errors`), `"Database"`, `"Unexpected"`, and any unrecognized/future type all use a safe generic `ForBadRequest` with raw text logged server-side only. `FailureType` alone never proves safety. Added a **secret-sentinel** test asserting a pathless-`"Validation"` sentinel is absent from the serialized body.
2. **Control-document cleanups:** completed the V-02 baseline enumeration (added the compliant `FailureResults` `error_description` parser and `MetadataModule.WriteAsync`); advanced the §29 heading and end marker to R4; removed the stale "exclusions" wording in §29.2; updated the §10 target JSON so `type` permits an Ed-Fi URI **or** `about:blank`; added `ForUnclassifiedStatus` to the §20 file-impact forecast.

### 29.8 Remaining human decisions
**None.** All decisions D-01…D-08 are resolved (§24). Phase 5 still performs a reachable-status audit, but only to *size the tests* — the D-08 mapping rule (ticket/KB/platform URI, else `about:blank`) is fixed and requires no further decision.

Follow-up questions for the architect may be relayed through the user.

---

*End of R4. Status: Approved — 2026-07-23. Implementation delivered across commits C01–C14 (with test-convention follow-ups C10a/C11a/C12a), each behind an architect approval gate; all inventory items resolved, Definition of Done (§28) satisfied, static audits and the full CMS frontend suite green. Submitted for final review.*
