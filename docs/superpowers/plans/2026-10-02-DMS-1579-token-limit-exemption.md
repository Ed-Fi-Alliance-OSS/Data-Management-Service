# DMS-1579 Token-Limit Exemption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise the self-contained identity-provider default to 15 active tokens for Ed-Fi API clients while exempting non-API OpenIddict clients and keeping route-context E2E credentials reusable.

**Architecture:** Add a fail-closed `ApplicationInfo.IsTokenLimitExempt` fact populated by the PostgreSQL and SQL Server application lookups. `OpenIddictTokenManager` selects `-1` for an exempt application or the configured limit for a limited one, leaving existing repository locking and below-one insertion semantics intact. The Instance Management E2E helper caches valid DMS tokens per endpoint/client key and coalesces refreshes.

**Tech Stack:** .NET 10/C#, NUnit, FluentAssertions, FakeItEasy, Dapper, PostgreSQL, SQL Server, Reqnroll E2E tests.

**Spec:** `docs/superpowers/specs/2026-10-02-DMS-1579-token-limit-exemption-design.md`

## Global Constraints

- Story `.plans/ref/DMS-1579.md` and the approved spec are authoritative; do not implement unsupported pre-spec recommendations.
- An application is exempt only when its lookup proves there is no live `dmscs.ApiClient` row; `IsTokenLimitExempt` defaults to `false`.
- The limit remains enforced for every `ApiClient`-backed Ed-Fi API client; values below 1 disable enforcement globally.
- Preserve the existing atomic token-store lock/count/insert behavior and DMS canonical 429 transformation contract.
- Do not add schema/mapping/DDL changes or change `SchemaHashConstants.RelationalMappingVersion`.
- Use non-nullable C# variables, `is null`/`is not null`, and `System.Text.Json`; format changed C# with CSharpier.
- Do not modify Keycloak behavior or Keycloak-only Azure VM Compose files.

## Review Focus

- An incompletely populated `ApplicationInfo` must remain limited; Task 1 adds the default-false manager case.
- A case-insensitive SQL Server lookup must classify using stored `row.ClientId`, not request casing; Task 2 adds row/no-row and both lookup-method assertions.
- A positive default must reach the real manager/repository composition, not merely a fake store; Task 2 grants 15 tokens and rejects the 16th per dialect.
- A disabled configured limit must still reach an API-backed application unchanged; Tasks 1–2 retain explicit manager and dialect proof.
- Parallel route-context authentication for one client must issue one refresh and never return a near-expiry token; Task 4 adds deterministic tests.

---

## File Structure

| Area | Files | Responsibility |
| --- | --- | --- |
| Identity model and manager | `ApplicationInfo.cs`, `IdentityOptions.cs`, `OpenIddictServiceCollectionExtensions.cs`, manager and unit tests | Carry fail-closed classification, select the effective limit, and set/bind the 15 default. |
| Dialect repositories | PostgreSQL and SQL Server `OpenIddictDataRepository.cs` plus integration tests | Derive row-backed classification during both application lookup paths and prove manager/store behavior against each real database. |
| Route-context E2E helper | `TokenHelper.cs`, relevant step definitions, new helper tests | Reuse valid DMS bearer tokens without changing explicitly uncached identity test flows. |
| Deployment/docs | CMS JSON, Compose YAML, twelve tracked environment files, docs/PRD/changelog | Publish the new default and exact exemption boundary without dead Keycloak configuration. |

### Task 1: Model, manager, and binding defaults

**Files:**

- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Models/ApplicationInfo.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Models/IdentityOptions.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictServiceCollectionExtensionsTests.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictTokenManagerTests.cs`

**Interfaces:**

- Produces: `ApplicationInfo.IsTokenLimitExempt: bool`, whose CLR default is `false`.
- Produces: manager behavior that passes `-1` to `ITokenRepository.StoreTokenAsync(...)` only if `applicationInfo.IsTokenLimitExempt` is true; otherwise it passes `IdentityOptions.BearerTokenPerClientLimit` unchanged.
- Consumes: existing `TokenStoreOutcome` contract; `FailureTokenLimitExceeded` continues to contain the configured positive limit.

- [ ] **Step 1: Write failing options and manager unit tests**

In the existing option fixture, change the expected unconfigured default from `5` to `15`. In the manager fixture, add tests that assert: a default-constructed `ApplicationInfo` results in the configured limit passed to storage; an application with `IsTokenLimitExempt = true` passes `-1`; a limited application configured at `-1` still passes `-1`; and a default-options `LimitExceeded` result becomes `FailureTokenLimitExceeded(15)`.

- [ ] **Step 2: Run the focused unit tests and verify the expected failure**

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~OpenIddictTokenManagerTests|FullyQualifiedName~OpenIddictServiceCollectionExtensionsTests"`

Expected: FAIL because the property/effective-limit behavior and default 15 are not implemented.

- [ ] **Step 3: Add the fail-closed model fact and select the effective limit**

Add `public bool IsTokenLimitExempt { get; set; }` with XML documentation to `ApplicationInfo`; do not change `IsApproved` or its default. Set `BearerTokenPerClientLimit` and the binding fallback to `15`. In `GenerateJwtTokenAsync`, compute a non-nullable `int maxActiveTokens` as `-1` for exempt applications and the configured option otherwise, then pass it to the existing `StoreTokenAsync` invocation. Retain all outcome handling; limit rejection response/log keeps reporting the configured setting.

- [ ] **Step 4: Format and run the focused unit tests**

Run: `dotnet csharpier format src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit`

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~OpenIddictTokenManagerTests|FullyQualifiedName~OpenIddictServiceCollectionExtensionsTests"`

Expected: PASS; fake-store calls distinguish default-limited, explicitly exempt, and globally-disabled API-client behavior.

- [ ] **Step 5: Commit the model and manager change**

```powershell
git add src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit
git commit -m "feat: exempt non-API clients from token limit"
```

### Task 2: Derive exemption in both dialects and prove composed grants

**Files:**

- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/OpenIddict/Repositories/OpenIddictDataRepository.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/OpenIddict/Repositories/OpenIddictDataRepository.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/OpenIddictDataRepositoryTests.cs`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/OpenIddictDataRepositoryTests.cs`

**Interfaces:**

- Consumes: `ApplicationInfo.IsTokenLimitExempt` from Task 1.
- Produces: accurate classification from `GetApplicationByClientIdAsync` and `GetApplicationByIdAsync` for both dialects.
- Produces: per-dialect composed manager/repository proofs: default 15 blocks grant 16 for a row-backed API client, no-row applications bypass limit 1, and API-client `-1` remains disabled.

- [ ] **Step 1: Write failing per-dialect repository and composition tests**

In each existing `OpenIddictDataRepositoryTests` fixture, seed one OpenIddict application with an `ApiClient` row and one with no row. Assert both lookup-by-client-ID and lookup-by-application-ID return false for the row-backed client and true for the no-row client. Add a fixture that builds `OpenIddictTokenManager` over the real `OpenIddictTokenRepository` and deterministic signing/secret helpers: with default options, issue 15 successful API-client grants, assert grant 16 is `FailureTokenLimitExceeded(15)`, and assert 15 rows; with configured limit 1, issue at least three grants for the no-row client and assert three successes/rows. Keep the existing direct disabled-store tests and add/retain manager-level proof that an API-backed client configured `-1` succeeds.

- [ ] **Step 2: Run each focused integration suite and verify expected failures**

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj --filter "FullyQualifiedName~OpenIddictDataRepositoryTests"`

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj --filter "FullyQualifiedName~OpenIddictDataRepositoryTests"`

Expected: New classification and composed-grant tests FAIL before dialect mapping is implemented; MSSQL tests may skip only when `ConnectionStrings__MssqlAdmin` is not configured.

- [ ] **Step 3: Populate classification without altering token storage**

For PostgreSQL, extend each existing application lookup aggregation over the already-left-joined `dmscs.ApiClient` rows so zero non-null `ApiClient.Id` values maps to `IsTokenLimitExempt = true`; preserve existing scope/data-store/approval aggregation. For SQL Server, extend the existing API-client aggregation for `GetApplicationInfoAsync` to return the exemption fact using canonical `row.ClientId`, not the requested lookup input; map it into `ApplicationInfo`. Do not edit either `StoreTokenAsync` implementation, its lock behavior, or its below-one branch.

- [ ] **Step 4: Format and run the focused integration suites**

Run: `dotnet csharpier format src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration`

Run the two commands from Step 2 again.

Expected: PASS (or environment-only MSSQL skip); each dialect proves direct classification and manager-to-real-store behavior.

- [ ] **Step 5: Commit the dialect implementation and tests**

```powershell
git add src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration
git commit -m "feat: classify API clients for token limits"
```

### Task 3: Publish defaults and supported operator guidance

**Files:**

- Modify: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.json`
- Modify: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.Development.json.example`
- Modify: `eng/docker-compose/local-config.yml`
- Modify: `eng/docker-compose/published-config.yml`
- Modify: `eng/docker-compose/.env.config.e2e`, `.env.config.mssql.e2e`, `.env.config.mssql.multitenant.e2e`, `.env.cursorpartitions.e2e`, `.env.e2e`, `.env.example`, `.env.multitenancy`, `.env.routeContext.e2e`, `.env.smoke`, `.env.smoke.ds61`, `.env.template`, `.env.template.ds61`
- Modify: `docs/CONFIGURATION.md`, `docs/DOCKER.md`, `reference/design/configuration-service/TOKEN-CLEANUP.md`, `docs/PRD-v8.1.md`, `docs/changelog/8.1.0.md`

**Interfaces:**

- Consumes: configuration key `IdentitySettings:BearerTokenPerClientLimit` and the approved classification boundary from Tasks 1–2.
- Produces: all shipped OpenIddict configuration examples/defaults show `15`; documentation describes the `ApiClient`-backed boundary and below-one global disable semantics.

- [ ] **Step 1: Update configuration surfaces and documentation**

Replace each tracked default/override of `DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT` and CMS default from `5` to `15`. Update Compose/environment comments to state that the positive limit applies to `ApiClient`-backed Ed-Fi API clients, while non-API system/admin clients are exempt and below 1 disables enforcement globally. In `CONFIGURATION.md` and `DOCKER.md`, remove replica/restart sizing and unshared-DMS-client guidance. Qualify token-cleanup ceiling statements as limited-client behavior. Change FR-AUTHN-7 to “a single Ed-Fi API client.” Add a changelog entry in existing bold-lead/no-inline-ticket style with 15, the exemption, global disable, and retained canonical 429.

- [ ] **Step 2: Verify documentation/configuration completeness**

Run: `rg -n "BearerTokenPerClientLimit|DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT|single client|replica|restart" src/config eng/docker-compose docs reference`

Expected: Each applicable OpenIddict default is `15`; no stale API-token-limit replica sizing guidance remains; intentionally Keycloak-only `eng/azure-vm/compose/*` files are unchanged.

- [ ] **Step 3: Commit configuration and documentation**

```powershell
git add src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore eng/docker-compose docs/CONFIGURATION.md docs/DOCKER.md reference/design/configuration-service/TOKEN-CLEANUP.md docs/PRD-v8.1.md docs/changelog/8.1.0.md
git commit -m "docs: document API client token limit defaults"
```

### Task 4: Reuse route-context DMS tokens safely

**Files:**

- Modify: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/TokenHelper.cs`
- Create: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Tests/Given_Dms_Token_Cache.cs`
- Modify: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/RouteQualifierStepDefinitions.cs`
- Modify: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/ManagementEndpointStepDefinitions.cs`

**Interfaces:**

- Produces: `TokenHelper.GetReusableDmsTokenAsync(string tokenUrl, string clientKey, string clientSecret)` for generic route/tenant and wrong-role helpers.
- Retains: `TokenHelper.GetDmsTokenAsync(string tokenUrl, string clientKey, string clientSecret)` as the uncached operation for identity lifecycle/distinct-token tests.
- Cache key: token URL plus client key; never log the key, secret, or access token.

- [ ] **Step 1: Write deterministic token-cache tests**

Create NUnit fixtures in the existing E2E test project with an injectable token-acquisition delegate and time source/test hook. Assert one valid response is reused for repeated calls with the same endpoint/client key, calls with different endpoint or client key do not share, a near-expiry response is refreshed, concurrent requests for one stale key coalesce to one acquisition, and the existing uncached method still executes a fresh acquisition. Use deterministic completion signals rather than delays.

- [ ] **Step 2: Run the helper tests and verify expected failure**

Run: `dotnet test src/dms/tests/EdFi.InstanceManagement.Tests.E2E/EdFi.InstanceManagement.Tests.E2E.csproj --filter "FullyQualifiedName~Given_Dms_Token_Cache"`

Expected: FAIL because reusable acquisition, expiry validation, and refresh coalescing do not yet exist.

- [ ] **Step 3: Implement bounded reusable acquisition and migrate only generic callers**

Factor the current HTTP grant/parsing operation behind an internal testable acquisition seam that returns `TokenResponse`. Implement `GetReusableDmsTokenAsync` with a static cache keyed by token URL and client key, storing access token plus an `ExpiresIn`-derived expiry with a conservative refresh skew. Store/await one in-flight acquisition per cache key so simultaneous callers reuse its result; evict a failed acquisition so a later call can retry. Do not log cache material. Change `RouteQualifierStepDefinitions` generic application/tenant/discovery authentication and `ManagementEndpointStepDefinitions` wrong-role helper to the reusable method. Leave `IdentityStepDefinitions` on `GetDmsTokenAsync`.

- [ ] **Step 4: Format and run helper/unit-style tests**

Run: `dotnet csharpier format src/dms/tests/EdFi.InstanceManagement.Tests.E2E`

Run: `dotnet test src/dms/tests/EdFi.InstanceManagement.Tests.E2E/EdFi.InstanceManagement.Tests.E2E.csproj --filter "FullyQualifiedName~Given_Dms_Token_Cache"`

Expected: PASS; cache behavior is deterministic and identity callers remain uncached by source-level usage.

- [ ] **Step 5: Run the route-context E2E regression suite**

Run: `./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.routeContext.e2e' -TestFilter 'Category=@e2e-ci-shard-3'`

Expected: PASS with the self-contained provider configured at 15; repeated fixture credentials no longer accumulate grants per scenario.

- [ ] **Step 6: Commit the route-context compatibility work**

```powershell
git add src/dms/tests/EdFi.InstanceManagement.Tests.E2E
git commit -m "test: reuse route context API tokens"
```

### Task 5: Full regression review and handoff

**Files:**

- Modify only if a focused regression exposes a defect in Tasks 1–4.

**Interfaces:**

- Verifies: shipped default, classifier boundary, 429 contract, documentation, and no-mapping-change constraint.

- [ ] **Step 1: Run targeted configuration-service regression suites**

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj`

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj`

Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj`

Expected: PASS; MSSQL may skip only if its admin connection string is absent, never because of test failures.

- [ ] **Step 2: Verify the OAuth 429 boundary and scope exclusions**

Run: `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~OAuthManagerTests"`

Run: `git diff --check`

Run: `git diff -- src/dms/core/EdFi.DataManagementService.Core/Utilities/SchemaHashConstants.cs eng/azure-vm/compose`

Expected: OAuth manager tests PASS; no whitespace errors; no mapping-version or Azure Keycloak Compose modification.

- [ ] **Step 3: Review changed values and commit any final correction**

Run: `rg -n "BearerTokenPerClientLimit.{0,80}5|DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=.?-?1|DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=5" src/config eng/docker-compose docs reference`

Expected: No stale default 5 remains in applicable OpenIddict surfaces; intentional below-one examples are documented as operator overrides, not defaults.

If this review requires a correction, add only its related files and commit with a narrow conventional message. Otherwise, leave the completed task commits unchanged.

## Execution Notes

- No production DMS token cache is in scope. The cache exists solely in the Instance Management E2E test helper.
- Do not change `reference/adr-oauth-upstream-error-disclosure.md`: it has no classifier assumption.
- Do not change `eng/azure-vm/compose/*`: it is Keycloak-only and the setting would be dead configuration.
- If MSSQL integration setup is absent, record the skip as an environment limitation and preserve PostgreSQL plus unit evidence; do not weaken or remove SQL Server tests.
