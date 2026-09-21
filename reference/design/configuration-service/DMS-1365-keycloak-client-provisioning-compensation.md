# DMS-1365 — Keycloak client creation: role-assignment result handling and compensating deletion

## 1. Document control and approval state

| Field | Value |
|---|---|
| Ticket | [DMS-1365](https://edfi.atlassian.net/browse/DMS-1365) — Bug, Open, fix version Ed-Fi API v8.1, parent epic DMS-1072 |
| Branch / worktree | `DMS-1365` at `C:\dev\ed-fi\Data-Management-Service\src\Data-Management-Service-DMS-1365`, fast-forwarded to `origin/main` `797b9e9d6` (clean apart from this file) |
| Revision | **R3** — revised after the second Codex review (2026-09-21), then **approved for Step 0.1 by the third review with three corrections incorporated** (§16). R2 followed the first review; R1 was the initial specification. |
| Status | **Approved specification (Step 0.1). No production code, test code, or formatting changes have been made. Phase 1 remains subject to explicit go-ahead.** |
| Recovery strategy | **Option A — compensating deletion** (settled by the architect before R1; not reopened) |
| Tag legend | **[JIRA]** verbatim/derived from the ticket · **[REPO]** verified in the working tree at `797b9e9d6` · **[LIB]** verified in Keycloak.Net.Core 1.0.29 source at the packaged commit `d0720818` · **[ASSUME]** assumption to confirm · **[DESIGN]** decision proposed here · **[R2]** changed in the second revision · **[R3]** changed in this revision |

**R2 → R3 change record [R3].** (1) Recovery (§9): deletion is authorized only by an exact match on the provider UUID this attempt received; without that UUID every metadata match is investigative context, and deletion requires provider audit evidence or explicit operator confirmation of ownership by the failed attempt, otherwise stop. The concurrent-registration and client-ID-reuse hazards are spelled out. §9.3 says "this attempt did not perform role assignment". (2) The outer catches distinguish *preflight* failures ("creation not attempted") from *creation-attempted-without-identifier* failures ("creation outcome unconfirmed") by tracking the phase; a preflight-exception fixture pins the distinction. Unconditional "role absent" / "scope absent" / "orphan remains" claims are replaced by "reported unsuccessful" / "unconfirmed" wording; stateful fixtures assert only their arranged state. (3) Logging: the compensation helper's events are asserted at their specified levels (Information / Warning / Error) with a level-aware test helper; the reused `DeleteClientAsync` Error diagnostic is allowed; the successful-cleanup template carries both identifiers (I-6); the module helper logs both the UUID and the client ID. (4) Executable plan: detached mutation worktree; `RegisterEndpointTests` is the registration fixture; the self-contained lane derives its expected claim type from `Authentication:RoleClaimAttribute` or the OpenIddict default, never from the Keycloak setting; sanitization fixtures use input that `SanitizeForLog` actually changes. (5) Per Q-8 the repository's unrecognized-result `default` arm is documented as unreachable and left to code review; no mutation experiment is claimed for it.

**R1 → R2 change record [R2].** (1) Creation outcomes without a usable provider identifier are now classified as *uncertain*, with the compensation guarantee bounded to failures after an identifier was returned; role-assignment and deletion exceptions are described as *unconfirmed*, with stateful fixtures for "took effect, then threw". (2) Recovery (§9) now covers the three production callers — `ApplicationModule.InsertApplication`, `ApiClientModule.InsertApiClient`, and `IdentityModule.RegisterClient` — separates repository compensation from module cleanup, and fixes the identification procedure for malformed identifiers. (3) Module fixtures now assert captured, sanitized module logs so they fail when result inspection or failure logging is removed; the update-scope guard is reframed as a fail-fast audit correction with its own regression evidence. (4) The insert paths stop logging whole `FailureUnknown` records; the "exactly two log events" invariant is replaced. (5) Unrecognized cleanup results are handled and tested; the `Combine(...)` helper is withdrawn in favor of an explicit result switch. (6) Mutation evidence runs in a disposable worktree; E2E counts are observations; the per-lane role-claim configuration is verified, not assumed. The architect's answers to R1's Q-1…Q-7 are recorded in §16.

## 2. Objective

Make `KeycloakClientRepository.CreateClientAsync` report a failed service-account role assignment as a provider failure, delete the client it created for that attempt whenever it holds a usable identifier, check every cleanup outcome, log the failing phase and any cleanup failure with sanitized context, and keep the insert workflows (`POST /v3/applications`, `POST /v3/apiClients`) on their existing structured **502** / sanitized **500** contracts with no credentials, no `201`, and no database row for a client whose provisioning failed. Audit and correct the other discarded provider booleans on the create path (`CreateRoleAsync`, `CreateClientScopeAsync`) without disturbing the update path they share. **[JIRA]**

## 3. Facts verified in the repository and library

### 3.1 The defect **[REPO]**

`src/config/backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakClientRepository.cs`, `CreateClientAsync` (lines 34–129):

1. Builds mappers, then resolves the realm role: `GetRolesAsync`; if absent, `await CreateRoleAsync(...)` (bool **discarded**, line 78) then `GetRoleByNameAsync`.
2. `await CheckAndCreateClientScopeAsync(scope)` — a `Task` helper (lines 315–345) that calls `CreateClientScopeAsync` and **discards** its bool.
3. `CreateClientAndRetrieveClientIdAsync` → `createdClientUuid`. Null/empty → logs and returns `FailureUnknown`.
4. **After** the client exists: if `clientRole != null` → `GetUserForServiceAccountAsync`, then `_ = await AddRealmRoleMappingsToUserAsync(...)` (line 99, **discarded**), then `Success(Guid.Parse(createdClientUuid))`. If `clientRole` is null → `FailureUnknown("Role … not found.")` — **the client already exists and is orphaned** in this branch too.
5. `catch (FlurlHttpException)` → `FailureIdentityProvider`; `catch (Exception)` → `FailureUnknown`. Neither catch knows whether a client was created, so any exception after step 3 (service-account lookup, role mapping, `Guid.Parse` `FormatException`) also **orphans** the client. Neither catch logs the client identifier.

`DeleteClientAsync` (lines 244–259): `true` → `Success`; `false` → `FailureUnknown`; Flurl 404 → `FailureClientNotFound`; other Flurl → `FailureIdentityProvider`. It has **no** `catch (Exception)`, so a non-Flurl exception propagates to the caller. It logs at Error for the Flurl case only.

### 3.2 Keycloak.Net.Core 1.0.29 semantics **[LIB]**

Source at `github.com/silentpartnersoftware/Keycloak.Net` commit `d0720818`, `src/Keycloak.Net.Core/**/KeycloakClient.cs`:

* `AddRealmRoleMappingsToUserAsync`, `CreateRoleAsync`, `CreateClientScopeAsync`, `DeleteClientAsync`, `UpdateClientAsync` all `return response.ResponseMessage.IsSuccessStatusCode;`.
* `CreateClientAndRetrieveClientIdAsync` awaits `InternalCreateClientAsync` (the POST, returning `HttpResponseMessage`) and then evaluates `response.Headers.Location!.PathAndQuery` **before** checking `IsSuccessStatusCode`; there is **no null check** on `Location`. It returns the last path segment on success, otherwise `null`.
* `GetUserForServiceAccountAsync` and `GetRoleByNameAsync` are `GetJsonAsync<T>` deserializations (a Keycloak 404 surfaces as `FlurlHttpException`, as the repository's own comments and the `INV-66` fixtures already assume).
* `GetBaseUrl` configures only the JSON serializer and authentication; no `AllowAnyHttpStatus`/`AllowHttpStatus`. Under Flurl.Http 4 defaults a non-2xx response **throws** `FlurlHttpException`, so a literal `false` is reachable only when Keycloak answers a non-2xx that Flurl is configured to allow (it is not). The `false` path is therefore a defensive contract that Jira requires us to honor; the exception path is the one real Keycloak exercises. The spec handles **both**.

**Consequence for creation outcomes [R2].** Three identifier-less creation outcomes exist and none proves that no client was created:

| Creation outcome | Mechanism | Was a client created? |
|---|---|---|
| `FlurlHttpException` with a status (4xx/5xx) | Keycloak answered non-2xx | almost certainly **not** (Keycloak rejects before persisting), but not provable from here |
| `FlurlHttpException` without a status (`StatusCode == null`, timeout / connection reset) | transport failure; the request may have reached Keycloak | **unknown** |
| 2xx with no `Location` header | `NullReferenceException` on `Location!` (non-Flurl) → today's outer `catch (Exception)` → `FailureUnknown` | **probably yes**, identifier unknown |
| non-2xx without a throw → `null` return | requires an allowed status; practically unreachable | unknown |

### 3.3 Production callers of `CreateClientAsync` **[REPO] [R2]**

| Caller | Key / secret | Role argument | Scope | Persists a DB row? | Cleanup on DB failure today |
|---|---|---|---|---|---|
| `ApplicationModule.InsertApplication` (lines 47–199) | module-generated `Guid` key, module-generated secret | `IdentitySettings.ClientRole` (`dms-client`) | claim set name | `ApplicationRepository.InsertApplication` (creates the Application and its first ApiClient row) | 5 awaited `DeleteClientAsync` calls, result discarded, unwrapped |
| `ApiClientModule.InsertApiClient` (lines 109–292) | same | same | application's claim set | `ApiClientRepository.InsertApiClient` | 3 awaited `DeleteClientAsync` calls, result discarded, unwrapped |
| `IdentityModule.RegisterClient` (lines 39–110, `POST /connect/register`) | **caller-chosen** `ClientId` and `ClientSecret` from the form | `IdentitySettings.ConfigServiceRole` (`cms-client`) | `AuthorizationScopes.AdminScope` (`edfi_admin_api/full_access`) | **no** — by design; uniqueness is checked against `GetAllClientsAsync` | none needed (no DB step) |

`RegisterClient` maps `FailureIdentityProvider` to `FailureResults.BadGateway(error.FailureMessage, trace)` — **the provider message becomes the 502 detail**. Every new `IdentityProviderError` message introduced by this ticket must therefore be fixed text containing no caller-derived value (the existing `ExceptionToKeycloakError` already passes `ex.Message` through here; that is pre-existing and out of scope). Its uniqueness precheck (`GetAllClientsAsync` then `IsUnique`) is **not atomic** with creation: two concurrent registrations of the same client ID can both pass the precheck; Keycloak then creates one client and answers the other with a 409, which surfaces as an identifier-less `FailureIdentityProvider` (P5a). **[R3]** The existing registration fixtures live in `IdentityModuleTests.cs` under the class **`RegisterEndpointTests`** (token fixtures under `TokenEndpointTests` and `OAuthEndpointErrorTests`) **[R3]**; they cover registration success (`Given_valid_client_details`), a 502 with `IdentityProviderError` (`When_provider_has_bad_credentials`, `When_provider_has_not_real_admin_role`, `When_provider_has_invalid_realm`, `When_provider_is_unreachable`) and duplicate/disabled registration; they use a fake repository and are unaffected by repository changes but are part of the regression run (§12).

Insert modules: `ClientCreateResult.FailureUnknown` → `logger.LogError("Failure creating client {Failure}", failure)` (logs the whole record, whose `FailureMessage` may be `ex.Message`) → `FailureResults.Unknown`. `FailureIdentityProvider` → logs `SanitizeForLog(error.FailureMessage)` → `FailureResults.BadGateway("Identity provider error during client creation", trace)`. Neither arm calls `DeleteClientAsync` (correct: no UUID). `Success` → repository insert; on `FailureVendorNotFound` / `FailureDataStoreNotFound` / `FailureProfileNotFound` / `FailureApplicationNotFound` (409 unresolved-reference), `FailureDuplicateApplication` (400 via thrown `ValidationException`), and `FailureUnknown` (500, also logged as the whole record) → cleanup call. A `FailureIdentityProvider`/`FailureUnknown` cleanup result is silently ignored (orphan, unlogged); a thrown cleanup escapes to `GlobalExceptionHandler`, which replaces the intended 409/400 with a generic sanitized 500.

Existing fixtures: `Given_an_application_insert_whose_cleanup_client_is_already_missing` (`FailureClientNotFound` keeps 409); `ApiClientModuleTests.Given_IdentityProvider_Failures.It_returns_bad_gateway_for_identity_provider_failure_on_insert` (status only); `ApplicationModuleTests.FailureUnknownTests` (status only, **no** 502 insert fixture). No fixture exercises a cleanup `FailureIdentityProvider`/`FailureUnknown`/throw on either insert path, none captures module logs, and none asserts the role argument passed to `CreateClientAsync`.

### 3.4 Update path sharing **[REPO]**

`UpdateClientAsync` (lines 368–627) calls `CheckAndCreateClientScopeAsync(scope)` inside its preflight `try`, then `FindClientScopeAsync`; a `null`/empty-id target → `FailureIdentityProvider(new IdentityProviderError($"Scope {scope} not found"))` (502). Today a `false` from `CreateClientScopeAsync` during an update flows into that second lookup: if the scope is still absent the update returns 502 with no mutation; if another request created the scope in between, the lookup finds it and the update proceeds. The update path performs no role work (INV-65) and must remain identity-preserving with fail-closed scope convergence (DMS-1218 R8). `UpdateClientNamespaceClaimAsync` (DMS-1356) touches neither scopes nor roles and is untouched.

### 3.5 Configuration and E2E environment **[REPO]**

* Keycloak lane: realm role name = `IdentitySettings.ClientRole` (`dms-client`; compose `IdentitySettings__ClientRole` ← `DMS_CONFIG_IDENTITY_CLIENT_ROLE`); role claim type = `KeycloakContext.RoleClaimType` = `IdentitySettings.RoleClaimType` (compose `IdentitySettings__RoleClaimType` ← `DMS_CONFIG_IDENTITY_ROLE_CLAIM_TYPE`, `.env.config.e2e` value `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`), emitted by the client's `oidc-usermodel-realm-role-mapper` (`multivalued: true`).
* Self-contained lane: `JwtTokenGenerator.cs` lines 138–144 emit roles as an array under `configuration["Authentication:RoleClaimAttribute"]` **or** the default `http://schemas.microsoft.com/ws/2008/06/identity/claims/role`. **No** compose file, env file, or appsettings sets `Authentication:RoleClaimAttribute`, so the two lanes agree today only because neither customizes the claim type. The role name comes from the OpenIddict role table populated with the same `IdentitySettings.ClientRole`. **[R2]**
* E2E obtains tokens through the CMS `/connect/token` (Keycloak lane: `KeycloakTokenManager` forwards to Keycloak and returns Keycloak's JWT). Steps available: `the response body credentials are captured as {slot}`, `a token is requested with the credentials captured as {slot} and scope {scope}`; JWT parsing via `JwtSecurityTokenHandler.ReadJwtToken` in `JwtTokenValidator`. `build-config.ps1 E2ETests` propagates `DMS_CONFIG_DATASTORE`, `DMS_CONFIG_MULTI_TENANCY`, `POSTGRES_*`, `MSSQL_*`; `start-local-config.ps1` sets `DMS_CONFIG_IDENTITY_PROVIDER`. Role name and claim type are **not** propagated.
* CI (`on-config-pullrequest.yml`): PostgreSQL × {keycloak, self-contained} full suite; MSSQL lanes filtered to `@MssqlRepresentative`.

### 3.6 Test conventions **[REPO]**

`KeycloakClientRepositoryTests`: FakeItEasy facade, `A.Fake<ILogger<…>>`, `CreateFlurlHttpException(status)`, stateful fakes with `ReturnsLazily`, `Given_*` fixtures with an `Act` `[SetUp]` and `It_*` tests; `TestHelpers/LoggerExtensions.VerifyLogError(logger, message)` (Error level only; a level-aware sibling is added in Step 1.2). `LoggingUtility.SanitizeForLog` removes line endings and every character outside letters, digits, space, `_ - . : / \`; it keeps `/`, so it does not remove tags as units: `"SENTINEL_x\r\n<b>{y}</b>"` is logged as exactly `SENTINEL_xby/b` **[R3]**. Module tests: `WebApplicationFactory<Program>` with fake repositories registered through `ConfigureServices`, sentinel strings with `NotContain`, `AssertContract` (ApiClient) / `AssertSanitizedInternalServerError` helpers. A recording `TestLogger<T>` (`Entries` of `LogEntry(Level, EventId, State, Exception, Scopes)`) exists as `internal` in `Middleware/RequestLoggingMiddlewareTests.cs` and is reusable within the assembly. `record` result unions are not sealed; DMS-1218 fixtures already subclass them for "test-defined future variant" cases.

## 4. Assumptions **[ASSUME]**

| # | Assumption | Resolution |
|---|---|---|
| A-1 | `JwtSecurityToken.Claims` yields raw payload claim types (no inbound mapping) and flattens a JSON-array claim into one `Claim` per value. | Standard library behavior; the E2E step tolerates one or many claims of the type; validated live in Step 3.1. |
| A-2 | The real Keycloak in the E2E stack accepts the realm role mapping for a service-account user immediately after client creation (the existing suite already relies on this). | Live E2E, Step 3.1. |
| A-3 | Registering `ILogger<ApplicationModule>` / `ILogger<ApiClientModule>` as a concrete singleton in the test `ConfigureServices` overrides the open-generic `Logger<T>` registration for those two types only. | Standard DI resolution (last registration of the closed type wins); confirmed in Step 2.1 by the first log-asserting fixture. |
| A-4 | No other caller depends on `CheckAndCreateClientScopeAsync` returning `Task` (it is `private`). | Verified: two call sites, both in the repository. |

## 5. Decisions **[DESIGN]**

### D-1 A rejected or failed role assignment is a provider failure and never `Success` (confirmed, Q-5)

`AddRealmRoleMappingsToUserAsync` returning `false` → `FailureIdentityProvider(new IdentityProviderError("The identity provider did not assign the realm role to the client's service account."))` — fixed text, nothing caller-derived (it reaches the `RegisterClient` 502 detail, §3.3). A `FlurlHttpException` from the same call → `FailureIdentityProvider(ExceptionToKeycloakError(ex))`. Any other exception → `FailureUnknown(ex.Message)`. The insert modules then answer 502 / 500 exactly as today; `RegisterClient` answers 502 / 500 as today. **[JIRA §3]**

*Recorded difference:* the update path maps `false` from `UpdateClientAsync`/scope assignments to `FailureUnknown` (INV-65 fixtures). Reconciling that convention is out of scope; the create path follows Jira's explicit wording.

### D-2 The repository owns compensation for failures after a usable identifier was returned **[R2]**

`ClientCreateResult` failure variants carry no UUID and gain none (the same reasoning that rejected UUID-carrying failure variants in DMS-1218 INV-65: three providers, every caller). Therefore **every** failure that occurs after `CreateClientAndRetrieveClientIdAsync` returned a **non-empty identifier** is compensated **inside** `CreateClientAsync` by calling the repository's own `DeleteClientAsync(createdClientUuid)` with the raw provider string (so an identifier that does not parse as a `Guid` can still be deleted), and the combined outcome is classified by D-3.

**Bounded guarantee.** The compensation guarantee covers *only* the post-identifier phases. Identifier-less outcomes (§3.2 table) are **uncertain** and are handled as follows, with no reconciliation infrastructure (no Option B):

* the method tracks its **phase** in a local variable (`preflight` until immediately before the create call is issued, then `creation`, then the post-identifier phases); the two outer-catch phrases are fixed text chosen from it, and the post-identifier compensation handling stays separate (Q-12). **[R3]**
* the existing null/empty-identifier branch stays `FailureUnknown`; its log line states that creation was attempted and its outcome is unconfirmed, and carries the sanitized `ClientId`;
* the outer `catch (FlurlHttpException)` / `catch (Exception)` keep their classifications; their log lines carry the sanitized `ClientId` and one of two fixed phrases chosen from the tracked phase — **"creation not attempted"** when the exception arose in preflight (role or scope resolution), **"creation outcome unconfirmed"** when it arose from the create call itself. Only the latter sends an operator to §9.3. **[R3]**
* no delete is attempted, because there is no identifier to delete by and a lookup-by-clientId sweep would be new reconciliation behavior.

The modules keep their separate responsibility: cleanup **after a successful provisioning** when the database insert fails. The two never overlap: a repository-compensated failure reaches a module as a UUID-less failure, whose arms do not call `DeleteClientAsync`. **No duplicate compensation.**

Compensated failure points (all after an identifier exists):

| Failure point | Provisioning classification | Role state at this point |
|---|---|---|
| identifier does not parse as `Guid` (`Guid.TryParse` false) | `FailureUnknown("The identity provider returned a client identifier that is not a UUID.")` | not attempted |
| `GetUserForServiceAccountAsync` throws Flurl / other | `FailureIdentityProvider(ExceptionToKeycloakError)` / `FailureUnknown(ex.Message)` | not attempted |
| service-account user is null or its `Id` is null/empty | `FailureUnknown("The identity provider returned no service account for the client.")` | not attempted |
| `AddRealmRoleMappingsToUserAsync` returns `false` | `FailureIdentityProvider` (D-1) | **reported unsuccessful** by the provider (not proven absent) |
| `AddRealmRoleMappingsToUserAsync` throws Flurl / other | `FailureIdentityProvider` / `FailureUnknown` | **unconfirmed** — the mapping may have been applied before the response was lost |

Pre-creation failures are moved ahead of creation by D-4 and need no compensation.

### D-3 Combined classification precedence (confirmed, Q-1) **[R2]**

Cleanup runs through `DeleteClientAsync`, wrapped in `try/catch (Exception)` because that method has no generic catch. The final result is selected by an **explicit switch over the `ClientDeleteResult`** (plus the catch), not by a combining helper:

| Cleanup outcome | Provider state after (best knowledge) **[R3]** | Final result when the provisioning failure is `FailureIdentityProvider` | … when it is `FailureUnknown` |
|---|---|---|---|
| `Success` | deletion **reported successful** | `FailureIdentityProvider` (502) | `FailureUnknown` (500) |
| `FailureClientNotFound` (Flurl 404) | provider reports the client **absent** | 502 | 500 |
| `FailureIdentityProvider` (non-404 Flurl with a status) | deletion **reported unsuccessful**; the client is expected to remain | 502 (provider-attributable throughout) | 500 |
| `FailureIdentityProvider(Unreachable)` (no status) | deletion **unconfirmed** | 502 | 500 |
| `FailureUnknown` (`false`) | deletion **reported unsuccessful** | **`FailureUnknown` (500)** | 500 |
| any other `ClientDeleteResult` (unrecognized, future variant) | **unknown** | **`FailureUnknown` (500)**, Error log naming the result type, no recreation | 500 |
| thrown exception | deletion **unconfirmed** — it may have completed before the exception | **`FailureUnknown` (500)**, exception logged | 500 |

Unit fixtures arrange a specific provider state for each row and assert **that arranged state**; they do not claim that every real occurrence of the row has it.

Justification against Jira: Jira fixes "provider-side failure → structured 502" and "unknown or unexpected failure → sanitized 500". A request in which every observed failure is provider-attributable stays 502; a request whose cleanup produced an outcome the repository itself classifies as unknown, an unrecognized result, or an unexpected exception is no longer purely provider-attributable and takes Jira's 500 semantic. This reuses `DeleteClientAsync`'s existing classification (DMS-1218 R7), adds no result variants, and mirrors DMS-1356's rule that an unexplained state is reported as 500. The response body never changes shape and never carries provider text; the orphan (or unconfirmed state) is reported through the Error log (D-7) and §9.

### D-4 Preflight: resolve the role and the scope before creating the client (confirmed, Q-4) **[R2]**

Move the `clientRole is null` check ahead of `CreateClientAndRetrieveClientIdAsync`, and check the two discarded booleans. This is an **intentional fail-fast audit correction**, not a behavior-neutral change:

| Call | Today | Proposed |
|---|---|---|
| `CreateRoleAsync` returns `false` | ignored; `GetRoleByNameAsync` then either throws a Flurl 404 → 502 or, if the role now exists (created concurrently), proceeds | `FailureIdentityProvider("The identity provider did not create the realm role.")`, **no** `GetRoleByNameAsync`, no client created (role creation reported unsuccessful, not proven absent) |
| role still null after creation + lookup | `FailureUnknown("Role … not found.")` **after** the client was created (orphan) | same classification and message, **before** creation — no orphan |
| `CreateClientScopeAsync` returns `false` (create path) | ignored; the client is created with `DefaultClientScopes = [scope]` naming a scope that may not exist — Keycloak either rejects the create (Flurl → 502, no client) or creates a client without its claim-set scope and the call returns `Success` | `CheckAndCreateClientScopeAsync` returns `Task<bool>`; `false` → `FailureIdentityProvider("The identity provider did not create the client scope.")`, no client created |
| `CreateClientScopeAsync` returns `false` (update path) | flows into `FindClientScopeAsync`: absent → 502 "Scope not found", no mutation; **present because a concurrent request created it** → the update proceeds | `false` → the same `FailureIdentityProvider` 502 **immediately**, no mutation, no second lookup. In the concurrent-creation race the update now fails fast where it previously proceeded; an identical retry finds the scope and converges. Accepted as the consistent audit correction; pinned by an update fixture (Step 1.1) |

Audit outcome (AC8): the only discarded provider booleans in `src/config` are the three above (`_ = await` at line 99; bare `await` at lines 78 and 321). `UpdateClientAsync`, `DeleteClientAsync`, `UpdateDefaultClientScopeAsync`, `DeleteDefaultClientScopeAsync` are already checked. `KeycloakTokenManager` uses `HttpClient` directly and inspects status codes. `OpenIddictClientRepository.CreateClientAsync` is transactional and has no equivalent defect. **[REPO]**

### D-5 No recreation, no placeholder secret, no shared-object deletion

Compensation calls only `DeleteClientAsync` on the identifier this request received. It never calls `CreateClientAndRetrieveClientIdAsync` again, never `UpdateClientAsync`/`GenerateClientSecretAsync`, never deletes realm roles or client scopes (they are realm-shared; the role or scope may have been created by this request but is shared from that moment on). The secret submitted to Keycloak stays the one the caller supplied to the repository; on any failure no caller returns credentials. **[JIRA §3]**

### D-6 Module cleanup hardening (confirmed, Q-2) **[R2]**

Each insert workflow gets one private static helper `CleanUpProvisionedClientAsync(IIdentityProviderRepository, Guid clientUuid, string clientId, ILogger)` that awaits `DeleteClientAsync`, catches every exception, and logs: `Success` → Debug; `FailureClientNotFound` → Warning ("already absent"); `FailureIdentityProvider` / `FailureUnknown` / any other result / thrown → Error with the client UUID, the sanitized client ID, the outcome type name, the sanitized reason, and the statement that the provider client is orphaned or its deletion is unconfirmed and requires manual review (§9.2). The secret is never passed to the helper. **The helper never changes the response** for the explicitly handled database-insert results: 409 unresolved-reference, 400 duplicate, and 500 unknown stand.

This is an **intentional compatibility decision**: the caller's actionable information is the database outcome, and INV-44's contract for these branches is preserved. INV-44 does not itself require swallowing cleanup exceptions; the decision is made here. The decision makes **no** claim about ambiguous database commits or about a *thrown* `InsertApplication`/`InsertApiClient` (those propagate to `GlobalExceptionHandler` exactly as today and are out of scope). For the `FailureUnknown` branch the response is 500 either way.

### D-7 Logging **[R2]**

All caller-derived values pass through `SanitizeForLog`; the `Client` object, request bodies, and secrets are never logged.

Repository (`ILogger<KeycloakClientRepository>`):

* provisioning failure, Error: `"Client provisioning failed during {Phase} for client {ClientId} (provider client {ClientUuid}); deleting the created client"` — `Phase` ∈ {`role-assignment`, `service-account-lookup`, `client-identifier-parse`}; `ClientId` the caller's key (sanitized — for registration it is caller-chosen), `ClientUuid` the raw provider identifier (sanitized);
* cleanup reported successful, **Information**: `"Deleted provider client {ClientUuid} (client {ClientId}) after failed provisioning"`; provider reports the client absent, **Warning**: `"Provider client {ClientUuid} (client {ClientId}) was already absent during cleanup after failed provisioning"` **[R3]**;
* cleanup not confirmed, **Error**: `"Could not confirm deletion of provider client {ClientUuid} (client {ClientId}) after failed provisioning; cleanup outcome {Outcome}; the client may remain and must be reviewed manually"`, with the exception (if any) as the log exception argument and `Outcome` the result type name or exception type name;
* identifier-less outcomes, Error: the existing outer-catch and empty-identifier lines carry `{ClientId}` and, from the tracked phase, either "creation not attempted" or "creation outcome unconfirmed" **[R3]**;
* preflight failures, Error: phase (`role-creation`, `role-lookup`, `scope-creation`) with sanitized role/scope names.

`DeleteClientAsync` keeps its own Flurl Error diagnostic (it logs before classifying a 404 as `FailureClientNotFound`), so a compensated failure may produce **more than two** events and an already-absent cleanup produces one Error from `DeleteClientAsync` beside the compensation helper's Warning. The requirement is that the compensation helper's provisioning-failure event and cleanup-outcome event are **identifiable** by their phase/outcome tokens, appear **at the level specified above**, and carry both identifiers — not a fixed count, and not "no Error anywhere". **[R3]**

Modules (insert paths only): the two `LogError("Failure creating client {Failure}", failure)` templates per module (the `ClientCreateResult.FailureUnknown` arm and the `Insert*Result.FailureUnknown` arm) are replaced with `"Failure creating client: {FailureMessage}"` / `"Failure inserting the client row: {FailureMessage}"` using `SanitizeForLog(failure.FailureMessage)`, so no record `ToString()` (which can carry `ex.Message`) is logged. The cleanup helper (D-6) logs **both** `{ClientUuid}` and the module's `{ClientId}` (the generated key, sanitized) at every level, so §9.2 can identify the client by client ID or resolve it directly by UUID **[R3]**. Other module log templates are untouched. Response bodies unchanged.

### D-8 Documentation location (confirmed, Q-3)

This document is the design record (linked from `reference/design/configuration-service/README.md`). The operator procedures in §9 go to **`docs/OPERATIONS.md`** as a new top-level section "Configuration Service: identity-provider clients left behind by failed provisioning"; `eng/docker-compose/KEYCLOAK-SETUP.md` receives a one-line pointer.

## 6. Scope and non-goals

**In scope:** `KeycloakClientRepository.CreateClientAsync` and `CheckAndCreateClientScopeAsync`; the two insert-workflow cleanup branches and their insert-path `FailureUnknown` log templates; fixtures in `KeycloakClientRepositoryTests`, `ApplicationModuleTests`, `ApiClientModuleTests`; one E2E scenario plus one step and two env propagations; operator documentation.

**Non-goals:** any change to `IIdentityProviderRepository` or its result unions; `OpenIddictClientRepository`; `IdentityModule` (its behavior is covered by the repository change and the existing `RegisterEndpointTests` in `IdentityModuleTests.cs`); `UpdateClientAsync` beyond the one guard in D-4; `UpdateClientNamespaceClaimAsync`, `VendorModule`, delete workflows, locks (`InsertApiClient` stays a non-participating writer, DMS-1218 S-2); retry/reconciliation infrastructure including any lookup-by-clientId sweep (Option B); repairing pre-existing orphans; unifying the Keycloak/OpenIddict role-claim-type configuration; schema, `RelationalMappingVersion`, dependency or formatting sweeps; reconciling the update path's `false → FailureUnknown` convention with D-1.

## 7. Outcome tables

### 7.1 Repository provisioning outcomes (`CreateClientAsync`) **[R2]**

| # | Phase / event | Returned result | HTTP (insert modules / register) | Provider state | DB state | Log |
|---|---|---|---|---|---|---|
| P1 | `GetRolesAsync` (or any preflight call) throws Flurl / other | `FailureIdentityProvider` / `FailureUnknown` (existing) | 502 / 500 | creation **not attempted** | none | Error, `ClientId`, "creation not attempted" **[R3]** |
| P2 | `CreateRoleAsync` `false` | `FailureIdentityProvider` (new) | 502 | creation not attempted; role creation reported unsuccessful | none | Error `role-creation` |
| P3 | role null after create + lookup | `FailureUnknown` (existing message, **now before creation**) | 500 | creation not attempted | none | Error `role-lookup` |
| P4 | `CreateClientScopeAsync` `false` | `FailureIdentityProvider` (new) | 502 | creation not attempted; scope creation reported unsuccessful | none | Error `scope-creation` |
| P5a | create POST throws Flurl with status | `FailureIdentityProvider` (existing) | 502 | creation attempted; **unconfirmed** (a 4xx/5xx makes creation unlikely but not disproved) | none | Error, `ClientId`, "creation outcome unconfirmed" |
| P5b | create POST throws Flurl without status (transport) | `FailureIdentityProvider(Unreachable)` (existing) | 502 | creation attempted; **unconfirmed** | none | same |
| P5c | 2xx without `Location` → `NullReferenceException` | `FailureUnknown` (existing) | 500 | creation attempted; **unconfirmed** (a 2xx makes creation likely) | none | same |
| P5d | null/empty identifier without throw | `FailureUnknown` (existing) | 500 | creation attempted; **unconfirmed** | none | same |
| P6 | identifier not a `Guid` | per D-3, base `FailureUnknown` | 500 | §7.2 | none | `client-identifier-parse` + cleanup |
| P7 | service-account lookup throws / user or `Id` missing | per D-3, base `FailureIdentityProvider` / `FailureUnknown` | 502 / 500 | §7.2 | none | `service-account-lookup` + cleanup |
| P8 | role mapping `false` | per D-3, base `FailureIdentityProvider` | 502 (500 by D-3) | §7.2; role assignment **reported unsuccessful** | none | `role-assignment` + cleanup |
| P9 | role mapping throws Flurl / other | per D-3, base `FailureIdentityProvider` / `FailureUnknown` | 502 / 500 | §7.2; role assignment **unconfirmed** | none | `role-assignment` + cleanup |
| P10 | success | `Success(uuid)` | modules continue; 201 on DB success; register 200 | client with role mapping, scope, mappers, supplied secret, `Enabled = isApproved` | row inserted by the insert modules; none for register | Debug |

### 7.2 Repository cleanup outcomes (P6–P9) **[R2]**

| Cleanup outcome | Provider state after **[R3]** | Result (base 502) | Result (base 500) | Compensation log event (level) |
|---|---|---|---|---|
| `Success` | deletion reported successful (any role mapping goes with the client) | 502 | 500 | Information, UUID + ClientId |
| `FailureClientNotFound` | provider reports the client absent | 502 | 500 | Warning, UUID + ClientId (the reused `DeleteClientAsync` also logs its Flurl exception at Error — allowed) |
| `FailureIdentityProvider`, status ≠ null | deletion reported unsuccessful; the client is expected to remain with role assignment reported unsuccessful (P8) or unconfirmed (P9), enabled per request, secret known only to the caller of the failed request | 502 | 500 | Error, "may remain", UUID + ClientId |
| `FailureIdentityProvider(Unreachable)` | deletion **unconfirmed** | 502 | 500 | Error, same |
| `FailureUnknown` | deletion reported unsuccessful | **500** | 500 | Error, same |
| unrecognized result | **unknown** | **500** | 500 | Error naming the result type |
| thrown | deletion **unconfirmed** (may have completed) | **500** | 500 | Error with exception |

In every row the caller receives a UUID-less failure, the insert modules do **not** insert a row and do **not** call `DeleteClientAsync`, and no caller returns credentials.

### 7.3 Module cleanup outcomes (after `Success` and a handled database-insert failure) **[R2]**

| DB insert result | Response | Cleanup `Success` / `FailureClientNotFound` | Cleanup `FailureIdentityProvider` / `FailureUnknown` / unrecognized | Cleanup throws (today) | Cleanup throws (proposed) |
|---|---|---|---|---|---|
| `Failure*NotFound` | 409 unresolved-reference | 409, Debug / Warning | 409, **Error log** (today: silent) | generic 500 via `GlobalExceptionHandler` | **409**, Error log |
| `FailureDuplicateApplication` | 400 validation | 400 | 400, Error log | generic 500 | **400**, Error log |
| `FailureUnknown` | sanitized 500 | 500 | 500, Error log | generic 500 | 500 (same body), Error log |

Provider state after a failed or unconfirmed module cleanup: a **fully provisioned** client is expected to remain (deletion reported unsuccessful) or may remain (unconfirmed) — `ClientRole` assigned, claim-set scope, mappers, enabled per request — whose credentials were never returned to the caller. Database state: no row for the handled results above (a `FailureUnknown` insert is treated as "not inserted" for the response, but the row's existence is **not** asserted by this ticket; §9.2 tells operators to check).

## 8. Behavioral contract (target) — `CreateClientAsync`

```
phase ← preflight
try
  mappers ← build
  role    ← resolve realm role (P1–P3)                    // returns a failure result before creation
  scope   ← ensure client scope; false → P4
  phase ← creation
  identifier ← CreateClientAndRetrieveClientIdAsync         // throws → outer catch (P5a/P5b/P5c)
  if identifier is null/empty → P5d (log: attempted, outcome unconfirmed)
  failure ← provision service-account role (P6–P9)         // catches its own exceptions
  if no failure → Success(parsedUuid)
  return compensate(identifier, clientId, failure)           // D-3 explicit switch over DeleteClientAsync
catch FlurlHttpException → FailureIdentityProvider   // log ClientId + ("creation not attempted" | "creation outcome unconfirmed") from phase
catch Exception          → FailureUnknown            // same
```

Helper extraction (a role-resolution method, a provisioning method, a compensation method) is for readability; the reviewable contract is the table above and the invariants below, not a prescribed decomposition.

Invariants: (I-1) `Success` is returned only after the role-mapping call reported success; (I-2) at most one `CreateClientAndRetrieveClientIdAsync` per call; (I-3) `DeleteClientAsync` is called at most once and only with the identifier this call received; (I-4) no `UpdateClientAsync`/`GenerateClientSecretAsync` on any path; (I-5) no realm-role or client-scope deletion on any path; (I-6) every post-identifier failure produces an identifiable provisioning-failure log event and an identifiable cleanup-outcome log event at the D-7 level, each carrying `ClientId` and `ClientUuid`; (I-7) every identifier-less failure log carries `ClientId` and states whether creation was attempted.

## 9. Remaining state and operator recovery **[R3]**

To be written into `docs/OPERATIONS.md`. Three situations, distinguished by the log line an operator finds. One rule governs all three:

> **Deletion authorization rule.** A Keycloak client may be deleted as recovery for a failed attempt **only** when its internal id equals the provider UUID that the failed attempt's log line carries (`{ClientUuid}`), verified in the same realm. When the log carries no UUID, or the UUID does not match, every other match — client ID, display name, creation time, role-mapping state, request correlation id — is **investigative context, not authorization**. Deletion then requires either provider audit evidence that ties the client's creation to the failed attempt (Keycloak admin events for `CLIENT` `CREATE` in the realm, when admin events are enabled, correlated by time and representation with the failed request's log) or the explicit, recorded confirmation of an operator, and that confirmation must **document how ownership by the failed attempt was established** — it is not permission to delete on matching metadata alone. If ownership remains uncertain, **stop and do not delete**. Deployments should enable admin-event auditing for the realm in advance (`docs/OPERATIONS.md` will recommend it): enabling it after an incident cannot establish historical ownership. **[Q-11]**

Why the rule is strict: two concurrent `/connect/register` requests for the same client ID can both pass the uniqueness precheck (§3.3); Keycloak creates one client and the other request fails without an identifier. Recovering the failed request by client ID alone would delete the **successful** registration's client. Likewise a client deleted by its owner and re-registered under the same client ID before recovery runs would be destroyed by a client-ID-only match. Only the UUID identifies the object this attempt created.

### 9.1 Repository compensation could not be confirmed (insert or register)

Log: `Client provisioning failed during {Phase} for client {ClientId} (provider client {ClientUuid})` followed by `Could not confirm deletion of provider client {ClientUuid} (client {ClientId}) … outcome {Outcome}`. This case always carries **the identifier the provider returned**. When `{Phase}` is `client-identifier-parse` that value is **malformed**: it is not a UUID, the authorization rule's exact-UUID match cannot be satisfied by it, and recovery follows the ownership-evidence fallback of §9.3 (search by client ID as investigation; delete only with audit evidence or documented operator confirmation). **[R3 approval corrections]**

State: the caller received 502/500 and no credentials. In Keycloak the client with internal id `{ClientUuid}` is expected to remain when the outcome is `FailureIdentityProvider` with a status or `FailureUnknown`, and may or may not remain when it is `Unreachable` or a thrown exception. Its realm-role assignment was reported unsuccessful (`role-assignment` `false`), is unconfirmed (`role-assignment` exception), or was not attempted by this request (other phases). For the insert callers the secret was generated server-side and never returned, so nobody can use the client. For `/connect/register` the secret was **caller-chosen**, so if the role mapping did land (P9) the client is a functional `cms-client` credential known to the caller — treat this as the urgent case.

Procedure:
1. Resolve the client **directly by UUID**: `GET {keycloak}/admin/realms/{realm}/clients/{ClientUuid}` (or Admin console → Clients → open by internal id). A 404 means the deletion completed; stop. If found, confirm `clientId` equals `{ClientId}` — a mismatch means the identifier is not what the log expected; stop and investigate.
2. Insert callers only: confirm the database holds no row for it. `SELECT "Id","ApplicationId","ClientUuid" FROM dmscs."ApiClient" WHERE "ClientId" = '<ClientId>'` (MSSQL `dmscs.ApiClient`); add `OR "ClientUuid" = '<ClientUuid>'` **only** when the value is a well-formed UUID, because PostgreSQL rejects a malformed literal against the `uuid` column. If a row exists, **stop and investigate** — it may be an ambiguous commit or another request's client; do not assume either.
3. Register caller: absence from `dmscs."ApiClient"` is expected and authorizes nothing. The UUID match in step 1 is the authorization; record it.
4. Delete the client by internal id (`DELETE …/clients/{ClientUuid}`). A 404 means it was already removed.
5. Re-issue the original request. It provisions a **new** client (insert callers: fresh key and secret; register: the same caller-chosen key, which uniqueness now allows again). **There is no automatic retry convergence**; each attempt is independent.

### 9.2 Module cleanup after a failed database insert could not be confirmed

Log (module): `Could not confirm deletion of provider client {ClientUuid} (client {ClientId}) after the database insert failed; outcome {Outcome} …`. This case **always carries the UUID** (the module holds the `Success` UUID).

State: the caller received 409/400/500 and no credentials. In Keycloak a **fully provisioned** client is expected to remain (or may remain, for unconfirmed outcomes): `dms-client` role assigned, claim-set scope, mappers, enabled per request. Its secret was never returned, so it is unusable clutter, but it is a complete credential. Database: for 409/400 no row was written; for 500 (`FailureUnknown`) the insert's commit state is **not** established by this ticket.

Procedure: §9.1 steps 1, 2 and 4 (resolve by UUID, check the database by client ID and — when well-formed — UUID, delete by UUID). In step 2, if a row **does** exist after a 500, the insert may have committed: do not delete the Keycloak client; treat the row as a live ApiClient and let the caller reset its credentials or delete it through the API.

### 9.3 Creation outcome unconfirmed (no identifier)

Log: the create-failure line carrying `{ClientId}` and **"creation outcome unconfirmed"** with no `ClientUuid`. (A line saying "creation not attempted" needs no recovery: nothing was created by this request.)

State: unknown whether this request created a client (§3.2 table: unlikely after a status-bearing refusal, likely after a 2xx without `Location`, unknown after a transport failure). **This attempt did not perform role assignment**; a client found under `{ClientId}` may nonetheless belong to another attempt and carry roles.

Procedure: search by client ID (`GET …/clients?clientId={ClientId}`) as **investigation only**. If nothing is found, stop. If a client is found, the deletion authorization rule applies in full: without a UUID from this attempt, delete only with provider audit evidence tying the creation to this failed request, or an operator's recorded ownership confirmation; otherwise leave it and record the finding. For insert callers, also run §9.1 step 2 by client ID; a row means the client is in use and must not be deleted.

Because compensation never deletes realm roles or client scopes, no repair of those is ever needed.

## 10. Phased implementation plan

Each step: implement only that step → run its focused tests + `dotnet csharpier check src/config` → self-review the diff → one local commit → report (SHA, files and why, behavior, AC addressed, tests + results, concerns) → **stop for explicit approval**. Mutation evidence (V-3) is produced in a disposable worktree, never in the reviewed one.

### Phase 0 — Approval and baseline

**Step 0.1 — Specification approval.** This document approved by the user and Codex; committed as `[DMS-1365] Add provisioning compensation specification` **only after approval** (Q-7). Checkpoint: explicit approval to start Phase 1.

**Step 0.2 — Baseline runs (no code change).** `Backend.Tests.Unit` whole project; `Frontend.AspNetCore.Tests.Unit` filtered to `FullyQualifiedName~ApplicationModuleTests|FullyQualifiedName~ApiClientModuleTests|FullyQualifiedName~RegisterEndpointTests` (the registration fixtures are the class `RegisterEndpointTests`, not a class named after the file **[R3]**); record pass counts on `797b9e9d6`. No commit.

### Phase 1 — Repository

**Step 1.1 — Preflight audit fixes (D-4).**
1. Behavior: `CreateRoleAsync` `false` → `FailureIdentityProvider`; role-null → `FailureUnknown` before creation; `CheckAndCreateClientScopeAsync` → `Task<bool>`; scope `false` → `FailureIdentityProvider` on create **and** update paths. AC8; AC5 unchanged success path.
2. Files: `KeycloakClientRepository.cs` (`CreateClientAsync` lines 70–83; lines 315–345; `UpdateClientAsync` line 423 gains `if (!await …) { log; return FailureIdentityProvider }`).
3. Minimal: reorder + two bool checks + one return type; fixed-text messages.
4. Failure handling: all before any mutation.
5. Tests (`KeycloakClientRepositoryTests`, new `CreateClientTestBase`: stateful fake with `_providerClients` (mutated by create/delete fakes), `_roleMappings` recorder, `_callOrder` recorder; helper `ActCreateAsync(...)`):
   * `Given_a_client_creation_whose_role_creation_is_rejected` — `FailureIdentityProvider`; `GetRoleByNameAsync` and `CreateClientAndRetrieveClientIdAsync` `MustNotHaveHappened`; `VerifyLogError("role-creation")`. **Defect-detecting**: today `GetRoleByNameAsync` is still called.
   * `Given_a_client_creation_whose_role_cannot_be_found_after_creation` — `FailureUnknown`; no client created. **Defect-detecting**: today the client is created first.
   * `Given_a_client_creation_whose_scope_creation_is_rejected` — `FailureIdentityProvider`; no client created. **Defect-detecting**: today `Success`.
   * `Given_a_client_creation_whose_scope_already_exists` — `CreateClientScopeAsync` `MustNotHaveHappened`; `Success`. (Successful-path; passes today.)
   * `Given_a_client_creation_whose_role_is_created_on_demand` — `_callOrder` has `create-role` before `create-client`; `Success`. (Passes today; pins the order after the reorder.)
   * `Given_an_update_whose_scope_creation_is_rejected` (`InPlaceUpdateTestBase`) — **audit-contract fixture**: `FailureIdentityProvider`; `AssertClientIdentityPreserved`; `UpdateClientAsync`, `UpdateDefaultClientScopeAsync`, `DeleteDefaultClientScopeAsync` `MustNotHaveHappened`; `GetClientScopesAsync` `MustHaveHappenedOnceExactly` (the fail-fast skips the second lookup). The no-mutation assertions are the behavioral guarantee; the call-count assertion documents the audit contract and is labeled as such in the fixture summary.
   * **Update regression evidence**: the entire existing `InPlaceUpdateTestBase` and `NamespaceClaimUpdateTestBase` suites, including `Given_a_scope_convergence_failure_followed_by_an_identical_retry`, run green unchanged.
6. Command: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --filter FullyQualifiedName~KeycloakClientRepositoryTests`. Mutation evidence for the three booleans (V-3).
7. Commit: `[DMS-1365] Check role and scope creation results before creating the Keycloak client`. Checkpoint.

**Step 1.2 — Role-assignment result and compensating deletion (D-1, D-2, D-3, D-5, D-7).**
1. Behavior: §7.1 P5–P9, §7.2. AC1, AC2, AC7.
2. Files: `KeycloakClientRepository.cs` post-creation block (lines 85–110) and the outer catches' log lines.
3. Minimal: no interface change, no new result variants; reuse `DeleteClientAsync` and `ExceptionToKeycloakError`; explicit switch for D-3.
4. Failure handling: §7.2, invariants I-1…I-7.
5. Tests (same base). A level-aware sibling `VerifyLog<T>(this ILogger<T>, LogLevel, string token)` is added beside `VerifyLogError` in `TestHelpers/LoggerExtensions.cs`, and `VerifyNoLog(level, token)` for negative checks scoped to a token **[R3]**. Every compensation fixture asserts: `CreateClientAndRetrieveClientIdAsync` `MustHaveHappenedOnceExactly`; no `UpdateClientAsync`/`GenerateClientSecretAsync`; the created `Client.Secret` equals the supplied secret; no `DeleteDefaultClientScopeAsync`; `_callOrder` contains no `create-client` after `delete-client`; `VerifyLogError` for the phase token; the compensation helper's cleanup event **at its D-7 level** (Information for reported-successful, Warning for absent, Error for the rest) carrying both identifiers; no log entry's formatted state contains the supplied secret. The reused `DeleteClientAsync` Flurl Error diagnostic is **allowed** and never asserted against **[R3]**.
   * `Given_a_client_creation_that_succeeds` — `Success(uuid)`; `_roleMappings == [(serviceAccountUserId, roleName)]`; `DeleteClientAsync` `MustNotHaveHappened`; `Enabled == isApproved`; `DefaultClientScopes == [scope]`. (Successful-path.)
   * `Given_a_registration_shaped_client_creation_that_succeeds` — caller-chosen key/secret, `ConfigServiceRole`-style role, admin scope, empty namespace/ed-org strings → `Success`; same mapping assertion. (Successful-path; documents the third caller.)
   * `Given_a_client_creation_whose_role_assignment_is_rejected` — `false` → `FailureIdentityProvider`; `DeleteClientAsync("edfi", identifier)` once; `_providerClients` empty. **Defect-detecting**.
   * `…role_assignment_throws_at_keycloak` (Flurl 500, mapping not recorded) → `FailureIdentityProvider`, compensated. **Defect-detecting**.
   * `…role_assignment_takes_effect_then_throws` **[R2]** — the fake records the mapping **then** throws Flurl → `FailureIdentityProvider`; `DeleteClientAsync` once; `_providerClients` empty and `_roleMappings` cleared with the client; log says role state unconfirmed. **Defect-detecting**.
   * `…role_assignment_throws_unexpectedly` → `FailureUnknown`, compensated.
   * `…service_account_lookup_fails_at_keycloak` → `FailureIdentityProvider`, compensated.
   * `…service_account_has_no_identifier` → `FailureUnknown`, compensated.
   * `…created_identifier_is_not_a_uuid` → `FailureUnknown`; `DeleteClientAsync` called with the raw identifier. **Defect-detecting**.
   * `Given_a_rejected_role_assignment_whose_cleanup_finds_the_client_already_absent` (404) → `FailureIdentityProvider`; the compensation helper's **Warning** ("already absent", both identifiers) is present; **no** compensation-helper Error (`VerifyNoLog(Error, "Could not confirm deletion")`). `DeleteClientAsync`'s own Flurl Error is expected and not asserted against. **[R3]**
   * `…cleanup_fails_at_keycloak` (403) → `FailureIdentityProvider`; `_providerClients` still holds the client; Error carries UUID, ClientId, "may remain".
   * `…cleanup_is_unreachable` (Flurl, no status) → `FailureIdentityProvider`; Error says unconfirmed.
   * `…cleanup_reports_no_change` (`false`) → **`FailureUnknown`**; client remains.
   * `…cleanup_throws_unexpectedly` → **`FailureUnknown`**; client remains; exception logged.
   * `…cleanup_takes_effect_then_throws` **[R2]** — the fake removes the client **then** throws → **`FailureUnknown`**; `_providerClients` empty; Error says deletion unconfirmed (never "remains").
   * Unrecognized `ClientDeleteResult` at the repository level — compensation calls the repository's own `DeleteClientAsync`, which only ever yields the four known variants, so this arm is **not reachable** from a unit fixture without adding a test seam. No seam is added (Q-8). The arm is implemented as the `default` of the explicit switch (`FailureUnknown`, Error log naming the result type, no recreation) and is left to **code review**; no mutation experiment is claimed for it. The equivalent module-level arm **is** tested (Steps 2.1/2.2, test-defined subclass). Recorded as a limitation (§13). **[R3]**
   * `Given_a_client_creation_whose_preflight_call_throws_at_keycloak` **[R3]** — `GetRolesAsync` throws Flurl 500 → `FailureIdentityProvider`; `CreateClientAndRetrieveClientIdAsync` and `DeleteClientAsync` `MustNotHaveHappened`; Error log carries `ClientId` and **"creation not attempted"** and not "unconfirmed". Paired with the two create-call fixtures below, which assert "creation outcome unconfirmed" and not "not attempted" — together they pin the phase distinction. **Defect-detecting**: today neither log line carries `ClientId` or a phase statement.
   * `Given_a_failed_provisioning_whose_base_failure_is_unknown_and_cleanup_fails_at_keycloak` (parse failure + 403) → `FailureUnknown`.
   * `Given_a_client_creation_whose_create_call_throws_without_a_status` **[R2]** → `FailureIdentityProvider(Unreachable)`; `DeleteClientAsync` `MustNotHaveHappened`; Error log carries `ClientId` and "creation outcome unconfirmed" (and not "not attempted"). (Documents the bounded guarantee.)
   * `Given_a_client_creation_whose_create_call_throws_unexpectedly` **[R2]** (`NullReferenceException`, the P5c shape) → `FailureUnknown`; no delete; same log assertion.
   * `Given_a_client_creation_whose_create_call_returns_no_identifier` **[R3]** (P5d) → `FailureUnknown`; no delete; log carries `ClientId` and "creation outcome unconfirmed".
   * Sanitization **[R3]**: `Given_a_client_creation_whose_client_id_needs_sanitizing` — the caller-supplied key is `"key\r\n<b>{x}</b>"` and the role assignment is rejected; every compensation log entry's formatted state contains exactly `keybx/b` as the client ID and contains none of `\r`, `\n`, `<`, `>`, `{`, `}`. **Defect-detecting** for the sanitization of the new log lines (an unsanitized `{ClientId}` reproduces the raw characters).
6. Command as 1.1; mutation evidence V-3.
7. Commit: `[DMS-1365] Fail Keycloak client creation and delete the client when the role assignment fails`. Checkpoint.

### Phase 2 — Insert workflows

**Step 2.1 — `ApplicationModule.InsertApplication`.**
1. Behavior: exact 502/500 contracts pinned; cleanup hardening D-6; sanitized insert-path logs D-7. AC3, AC4, AC5.
2. Files: `ApplicationModule.cs` lines 114–115 and 188–191 (log templates), lines 150–191 (five cleanup calls → helper), new private static helper; `ApplicationModuleTests.cs`; a `TestLogger<T>` registration via `collection.AddSingleton<ILogger<ApplicationModule>>(_moduleLogger)` in a fixture-local `SetUpClient` overload (or by promoting the existing `TestLogger<T>` to `Infrastructure/` if a second file needs it — decided at implementation, both minimal).
3. Minimal: one helper, five call replacements, two log-template edits; no response changes.
4. Failure handling: §7.3.
5. Tests. Every fixture records the `CreateClientAsync` key and secret arguments via `.Invokes`. The **secret** must appear in no captured log entry and no response on any failure path. The **sanitized client ID** is permitted — and, for the cleanup fixtures, required — in diagnostic log entries; it must not appear in a failure response body. Raw-character assertions (`\r`, `\n`, `<`, `>`, `{`, `}`) apply to the **diagnostic values** inside log entries, never to the whole serialized JSON response, which necessarily contains braces; response assertions reject the diagnostic sentinel text and credentials. **[R3 approval corrections]**
   * `Given_an_application_insert_whose_provider_creation_fails_at_the_identity_provider` — `FailureIdentityProvider(new IdentityProviderError(Sentinel))` → 502 `application/problem+json`, body deep-equals the `FailureResults.BadGateway("Identity provider error during client creation")` payload with `validationErrors: {}`, `errors: []`, non-blank `correlationId`; `NotContain(Sentinel)`, no `key`/`secret` members; `InsertApplication` `MustNotHaveHappened`; `DeleteClientAsync` `MustNotHaveHappened`. (Pins existing behavior; passes today.)
   * `…fails_unknown` — `FailureUnknown(Sentinel)` with `Sentinel = "SENTINEL_UNKNOWN_must_not_leak\r\n<b>{raw}</b>"` **[R3]** → exact sanitized 500; the response body does not contain `SENTINEL_UNKNOWN`; the captured Error entry's diagnostic value is exactly `SENTINEL_UNKNOWN_must_not_leakbraw/b` (the sanitized form — the log is meant to hold the diagnostic reason), the entry contains none of `\r`, `\n`, `<`, `>`, `{`, `}` and not the record shape `FailureUnknown {`, and no entry contains the secret. No insert, no delete. **Defect-detecting** for D-7: logging the record reproduces the raw characters and the record shape; logging the raw message reproduces the raw characters.
   * `Given_an_application_insert_whose_cleanup_succeeds` **[R2]** — insert `FailureVendorNotFound`, cleanup `Success` → 409 unchanged; a Debug entry naming the UUID; **no** Error entry.
   * `…cleanup_client_is_already_missing` (existing) — extended with a Warning-entry assertion.
   * `…cleanup_fails_at_the_identity_provider` — insert `FailureVendorNotFound`, cleanup `FailureIdentityProvider(Sentinel)` with a raw-character sentinel as above → **409** body unchanged; `DeleteClientAsync(createdUuid)` once; an **Error** entry containing the UUID, the sanitized client ID (the captured key), `FailureIdentityProvider`, and the reason in its exact sanitized form with none of the raw characters; response body does not contain the sentinel text. **Defect-detecting**: without result inspection no Error entry exists; without sanitization the raw characters appear. **[R3]**
   * `…cleanup_fails_unknown` — insert `FailureUnknown(SentinelA)`, cleanup `FailureUnknown(SentinelB)`, both raw-character sentinels → sanitized 500; Error entries with UUID, sanitized client ID, and both reasons in exact sanitized form; response body contains neither sentinel; no raw characters in any diagnostic value. **Defect-detecting**.
   * `…cleanup_returns_an_unrecognized_result` **[R2]** — test-defined `ClientDeleteResult` subclass → 409 unchanged; Error entry naming the result type. **Defect-detecting**.
   * `…cleanup_throws` — insert `FailureDuplicateApplication`, cleanup throws `InvalidOperationException(Sentinel)` → **400** validation body (today: 500); Error entry with the exception; response `NotContain(Sentinel)`. **Defect-detecting**.
   * `Given_a_successful_application_insert` — `Configure<IdentitySettings>(o => o.ClientRole = "role-under-test")`; `CreateClientAsync` received `"role-under-test"`; 201; body has `key`/`secret`; `Location` header. (Successful-path; passes today; guards against dropping the role argument.)
6. Command: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit --filter FullyQualifiedName~ApplicationModuleTests`. Mutation evidence: remove the helper's result inspection → cleanup-outcome fixtures fail; remove the `SanitizeForLog` template change → the `fails_unknown` log assertion fails.
7. Commit: `[DMS-1365] Pin the application insert provisioning contracts and check provider cleanup outcomes`. Checkpoint.

**Step 2.2 — `ApiClientModule.InsertApiClient`.** Same shape against lines 219–220, 284–288 (templates) and 262–288 (three cleanup calls): `…provider_creation_fails_at_the_identity_provider` (upgrade to exact body via `AssertContract` + sentinel + no insert/delete + no credentials), `…fails_unknown`, `…cleanup_succeeds`, `…cleanup_client_is_already_missing`, `…cleanup_fails_at_the_identity_provider` (insert `FailureApplicationNotFound` → 409 kept), `…cleanup_fails_unknown`, `…cleanup_returns_an_unrecognized_result`, `…cleanup_throws` (insert `FailureDataStoreNotFound` → 409 kept), `Given_a_successful_api_client_insert` (role argument, `isApproved` argument, 201 credentials). Commit: `[DMS-1365] Pin the API client insert provisioning contracts and check provider cleanup outcomes`. Checkpoint.

### Phase 3 — Real-Keycloak E2E

**Step 3.1 — Role-claim scenario.**
1. Behavior: a token for a newly created client (application-created and apiClient-created) carries the configured role claim type with the configured role value. AC5, AC6.
2. Files: `Features/Token.feature` (new `Scenario: 05 A newly created client's token carries the configured client role`); `StepDefinitions/StepDefinitions.cs` (new step `Then the token carries the configured client role claim`); `JwtTokenValidator.cs` (`TryGetClaimValues(token, claimType, out IReadOnlyList<string>)`); `build-config.ps1` `E2ETests` (propagate `DMS_CONFIG_IDENTITY_ROLE_CLAIM_TYPE` and `DMS_CONFIG_IDENTITY_CLIENT_ROLE`, assigned unconditionally like the neighbors).
3. Effective configuration per lane **[R3]**: the step derives the expected claim type **from the provider under test**, read from `DMS_CONFIG_IDENTITY_PROVIDER`: Keycloak lane → `DMS_CONFIG_IDENTITY_ROLE_CLAIM_TYPE` (fallback: the `.env.config.e2e` URI); self-contained lane → `Authentication__RoleClaimAttribute` if set, otherwise the OpenIddict default URI, and **never** the Keycloak setting. The expected role is `DMS_CONFIG_IDENTITY_CLIENT_ROLE` (fallback `dms-client`) on both lanes because both populate their role store from `IdentitySettings.ClientRole`. `build-config.ps1` propagates `DMS_CONFIG_IDENTITY_ROLE_CLAIM_TYPE`, `DMS_CONFIG_IDENTITY_CLIENT_ROLE` and, when present in the env file, `Authentication__RoleClaimAttribute`. Evidence for each lane run: `docker inspect ed-fi-api-config-service` output showing `IdentitySettings__RoleClaimType`, `IdentitySettings__ClientRole`, `AppSettings__IdentityProvider` and the absence/presence of `Authentication__RoleClaimAttribute`, recorded in "As implemented". Matching defaults are *observed*, not assumed.
4. Scenario: Background token → POST vendor → POST dataStore → POST application (claim set `ClaimSet05Role`) → `credentials captured as "app"` → POST apiClient → `credentials captured as "client"` → token for `"app"` with scope `ClaimSet05Role` → 200 → role step → token for `"client"` → 200 → role step.
5. Assertion: values of claims whose `Type` equals the expected claim type `.Should().NotBeEmpty(<because naming the claim type>)` and `.Should().Contain(roleValue)` (`Ordinal`). Token issuance alone never passes.
6. Untagged: runs on the Keycloak **and** self-contained PostgreSQL lanes (Q-6); not `@MssqlRepresentative`. **Honesty note**: this scenario does not fail against today's defect (real Keycloak assigns the role); it is AC5/AC6 successful-path evidence and guards the D-4 reorder. Commands: `./build-config.ps1 Build -Configuration Release`; `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak -E2ETestFilter '<Token feature class name, verified at run time>'`; then `-IdentityProvider self-contained`. Per project memory: rebuild the image after a teardown rather than `-SkipDockerBuild`.
7. Commit: `[DMS-1365] Prove a newly created client's token carries the configured role claim`. Checkpoint.

### Phase 4 — Documentation

**Step 4.1** — `docs/OPERATIONS.md` new section with §9.1–9.3; one-line pointer in `eng/docker-compose/KEYCLOAK-SETUP.md`; `reference/design/configuration-service/README.md` link; this document gains "As implemented" (commit SHAs, verification results, lane configuration evidence). Commit: `[DMS-1365] Document recovery for identity-provider clients left by failed provisioning`. Checkpoint.

## 11. Acceptance-criteria traceability **[R2]**

| AC | Criterion (abridged) | Steps | Concrete evidence |
|---|---|---|---|
| 1 | Repository fixture: `false` role assignment → provider failure, phase logged, recovery performed | 1.2 | `Given_a_client_creation_whose_role_assignment_is_rejected` — `FailureIdentityProvider`, `VerifyLogError("role-assignment")`, `DeleteClientAsync` once, `_providerClients` empty |
| 2 | Compensating deletion: successful deletion, deletion failure result, deletion exception — returned failure, logging, **remaining state** | 1.2, 4.1 | `…is_rejected` (reported successful → arranged state deleted), `…cleanup_finds_the_client_already_absent`, `…cleanup_fails_at_keycloak` (arranged state remains), `…cleanup_is_unreachable` (unconfirmed), `…cleanup_reports_no_change` (remains, 500), `…cleanup_throws_unexpectedly` (arranged remains, 500, log says unconfirmed), `…cleanup_takes_effect_then_throws` (arranged gone, 500, log says unconfirmed), `…base_failure_is_unknown_and_cleanup_fails_at_keycloak`; each asserts result type, level-specific log events with both identifiers, `_providerClients` contents; the unrecognized-result arm is a documented code-review-only limitation; §9 recovery with the UUID-only deletion authorization rule documented in Step 4.1 |
| 3 | Both insert workflows: no 201/credentials, no row persisted as provisioned, appropriate cleanup | 2.1, 2.2 | `…provider_creation_fails_*` (no insert, no delete, no credentials); `…cleanup_succeeds` / `…already_missing` / `…fails_*` / `…unrecognized` / `…throws` with **captured module log** assertions and one delete call |
| 4 | Exact 502 / sanitized 500 bodies; sentinel provider and exception messages absent | 2.1, 2.2 | deep-equal body assertions; `NotContain(Sentinel)` for provider message, unknown message, cleanup message, thrown exception message |
| 5 | Successful creation assigns the configured role, returns success, preserves insert behavior | 1.1, 1.2, 2.1, 2.2, 3.1 | `Given_a_client_creation_that_succeeds`, `…registration_shaped…succeeds` (`_roleMappings`); `Given_a_successful_application_insert` / `…api_client_insert` (role argument, 201, credentials); E2E scenario 05. All successful-path (pass today by design) |
| 6 | Keycloak E2E: real token contains configured role claim name and value | 3.1 | Token.feature scenario 05 on the Keycloak lane with lane configuration evidence; also run on self-contained |
| 7 | Deleted clients not recreated; no placeholder secrets; DMS-1218 R7 intact | 1.2, 2.1, 2.2 | every 1.2 fixture: create once, no `UpdateClientAsync`/`GenerateClientSecretAsync`, `Client.Secret` == supplied, no create after delete; R7: existing `Given_an_api_client_delete_*`, `Given_an_application_delete_*`, `Given_a_client_delete_*` untouched and green; R8/DMS-1356: `InPlaceUpdateTestBase`, `NamespaceClaimUpdateTestBase` green plus `Given_an_update_whose_scope_creation_is_rejected` |
| 8 | Discarded-boolean audit documented; equivalent defects corrected with regression coverage | 1.1, 4.1 | §5 D-4 audit table with the rationale that the corrections are fail-fast; fixtures `…role_creation_is_rejected`, `…scope_creation_is_rejected`, `…scope_already_exists`, `…role_is_created_on_demand`, `Given_an_update_whose_scope_creation_is_rejected`; whole update suites as regression evidence |

## 12. Pre-push validation plan **[R2]**

| V | Check | Command / method | Result handling |
|---|---|---|---|
| V-1 | Backend unit, whole project | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | all green; counts recorded |
| V-2 | Frontend unit, whole project (includes `IdentityModuleTests`) | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit` | all green |
| V-3 | Mutation evidence | `git worktree add --detach ../DMS-1365-mutation <reviewed commit SHA>` (the branch itself is already checked out and cannot be added again) **[R3]**; in that worktree revert **one** production edit at a time (Step 1.1's three boolean checks individually, Step 1.2's compensation, Step 1.2's phase-tracked log wording, Step 2.1/2.2's result inspection and template change), `dotnet build` (never `--no-build` after a restore, per project memory), run the step's fixtures, record the failing fixture names; `git worktree remove` afterwards. The reviewed worktree and index are never touched. The repository's unrecognized-result `default` arm is **not** part of this evidence (Q-8) | recorded per step |
| V-4 | Formatting | `dotnet csharpier check src/config` | clean (scoped; whole-tree has known pre-existing drift) |
| V-5 | Backend integration | not touched by this ticket; report as not run unless the local Postgres lane is available, MSSQL reported skipped without `ConnectionStrings__MssqlAdmin` | honest report |
| V-6 | E2E Keycloak, full suite | `./build-config.ps1 Build -Configuration Release`; `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak` | **observe** passed/failed/skipped; all failures investigated; the only skips expected are `@SelfContainedOnly` |
| V-7 | E2E self-contained, full suite | same, `-IdentityProvider self-contained` | observe; expected skips are Keycloak-only scenarios |
| V-8 | E2E MSSQL representative, Keycloak | `-EnvironmentFile ./.env.config.mssql.e2e -E2ETestFilter "TestCategory=MssqlRepresentative"` | observe; scenario 05 is not in this subset, stated explicitly |
| V-9 | Lane configuration evidence | `docker inspect ed-fi-api-config-service --format '{{range .Config.Env}}{{println .}}{{end}}'` filtered to `IdentitySettings__RoleClaimType`, `IdentitySettings__ClientRole`, `AppSettings__IdentityProvider`, `Authentication__RoleClaimAttribute` on each lane | recorded |
| V-10 | Diff review | `git diff origin/main --stat`; grep the diff for `Secret`, `clientSecret`, `LogError(`, `{Failure}` | no credential in any log template or response; no whole-record logging on the insert paths; no unrelated files |

## 13. Risks, limitations and mitigations **[R2]**

| Risk / limitation | Mitigation |
|---|---|
| Identifier-less creation outcomes cannot be compensated (bounded guarantee) | Documented (§3.2, §9.3); logs carry `ClientId`; no sweep introduced |
| Role-assignment or deletion exceptions leave unconfirmed state | Log wording says "unconfirmed"/"may remain"; stateful took-effect-then-threw fixtures; §9 verifies in Keycloak by client ID |
| The repository's unrecognized-`ClientDeleteResult` arm cannot be reached through the facade fake without a new seam | Documented limitation (Q-8): the `default` arm is reviewed by code review only; the module-level equivalent is tested with a test-defined subclass |
| Recovery for identifier-less outcomes may find a client that belongs to another attempt (concurrent registration, client-ID reuse) | UUID-only deletion authorization rule (§9); without a UUID, deletion requires provider audit evidence or recorded operator confirmation, otherwise stop |
| Keycloak admin events may be disabled in a deployment, leaving no provider audit evidence | Then only operator confirmation can authorize deletion of an identifier-less finding; documented in `docs/OPERATIONS.md` (Q-11) |
| D-4's update-path fail-fast changes the concurrent-scope-creation race from "proceeds" to "502, retry converges" | Intentional; pinned by `Given_an_update_whose_scope_creation_is_rejected`; whole update suites as regression |
| `RegisterClient` surfaces `IdentityProviderError.FailureMessage` in its 502 detail | All new messages are fixed text; `IdentityModuleTests` in V-2 |
| Overriding `ILogger<TModule>` in the test host may not take effect (A-3) | First log-asserting fixture proves it; fallback is the `TestLogger<T>` promoted to `Infrastructure/` with an `ILoggerProvider` |
| Log message strings become test-coupled | Fixtures assert phase/outcome tokens and identifiers, not whole sentences |
| Lanes could diverge on role claim type if `Authentication:RoleClaimAttribute` is ever set | Step 3.1 resolves per lane and records the effective configuration; unification is a recorded non-goal |

## 14. Open questions

None. Q-1 through Q-12 are answered in §16. Questions arising during implementation are raised at the step checkpoint that exposes them, and a change to an approved decision requires a specification revision and re-approval before the affected step continues.

## 15. Review record

Three critical review rounds by Codex (architect) on 2026-09-21. Round 1 challenged the certainty of provider-state claims, recovery coverage of all callers, mutation-insensitive module fixtures, whole-record logging, defensive completeness, and verification reproducibility. Round 2 challenged registration-recovery identity evidence, the preflight/creation distinction, logging/test contradictions, and executable-plan details. Round 3 approved the specification for Step 0.1 subject to three corrections (client-ID logging permission, exact sanitization values with response-scoped assertions, malformed-identifier recovery), which are incorporated in this revision. Accepted limitations, stated explicitly: identifier-less creation cannot be automatically compensated; the currently unreachable repository `default` arm receives code review rather than test infrastructure; the real-Keycloak role-claim test must run before completion is claimed.

## 16. Decision log (architect, 2026-09-21, from the R1 review)

| Q | Decision |
|---|---|
| Q-1 | Unknown cleanup results and unexpected exceptions produce sanitized 500; provider-attributable cleanup failures preserve the original provider 502; an original unknown failure stays 500. |
| Q-2 | For the explicitly handled database-insert failure results, preserve 409/400/500 and log cleanup failures; stated as an intentional compatibility decision; no guarantees about ambiguous database commits or thrown insert operations. |
| Q-3 | `docs/OPERATIONS.md` with a setup-guide pointer; recovery limitations corrected. |
| Q-4 | Include the shared-helper guard as an intentional fail-fast audit correction with update regression coverage. |
| Q-5 | `false` role assignment → `FailureIdentityProvider`; unsuccessful recovery can alter the final classification under Q-1. |
| Q-6 | Run the scenario on both lanes without a provider-only hook; verify effective configuration; report real-Keycloak execution explicitly. |
| Q-7 | Commit the specification only after the revised version is approved. |

**From the R2 review (2026-09-21) [R3]:**

| Q | Decision |
|---|---|
| Q-8 | No production test seam. Keep the defensive `default` arm and test the module-level handling directly; module tests do not cover the repository branch, and forcing it through a source modification is a reachability experiment, not mutation evidence. Document the limitation; code review suffices for the currently unreachable default. |
| Q-9 | Yes, during identifier-less **creation** failures, with the preflight/creation phase distinction. No special missing-`Location` exception classifier. |
| Q-10 | No. Client ID, display name, timestamp and role state cannot establish ownership. Use the exact provider UUID or corroborating evidence linking creation to the failed attempt; otherwise stop without deleting. |

**From the R3 review (2026-09-21, approval for Step 0.1):**

| Q | Decision |
|---|---|
| Q-11 | Yes, with a qualification: recorded operator confirmation is acceptable only when it documents how ownership by the failed attempt was established; it is not permission to delete on matching metadata alone. If ownership remains uncertain, stop. Recommend enabling admin-event auditing in advance; enabling it later cannot establish historical ownership. |
| Q-12 | Yes: a local tracked phase set immediately before the create call, fixed log phrases, separate post-identifier compensation handling, no additional abstraction. |
| Approval | R3 approved for Step 0.1 with three corrections incorporated: permit the sanitized client ID in diagnostic logs and prohibit the secret throughout, adding `clientId` to the cleanup helper; exact sanitized values (`keybx/b`, `SENTINEL_xby/b`, `…braw/b`) with raw-character assertions on diagnostic values rather than whole JSON responses; §9.1 carries the returned provider identifier, and a malformed one follows the ownership-evidence fallback. Commit the specification only. **Phase 1 remains subject to explicit go-ahead.** |

Further R2-review directions adopted: preflight failures log "creation not attempted"; unconditional "absent"/"remains" claims replaced; compensation events asserted at their levels with the reused `DeleteClientAsync` diagnostic allowed; both identifiers in the successful-cleanup template and in the module helper; detached mutation worktree; `RegisterEndpointTests`; per-provider claim-type derivation; sanitization fixtures with input `SanitizeForLog` changes.

Further R1-review directions adopted: bounded compensation guarantee; recovery for all three callers with registration separated; malformed-UUID identification by client ID; captured module logs in the insert fixtures; audit-contract vs behavioral evidence distinction; sanitized insert-path `FailureUnknown` logging; identifiable rather than counted log events; unrecognized cleanup outcome policy; no `Combine(...)` helper; mutation checks in a disposable worktree; E2E counts as observations; per-lane role-claim configuration verification.
