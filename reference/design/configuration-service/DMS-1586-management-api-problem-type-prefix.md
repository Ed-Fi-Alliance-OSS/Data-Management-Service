# DMS-1586 — Use the `urn:ed-fi:management-api:` problem-type prefix in the Configuration Service

**Implementation specification (Configuration Service / CMS, with a narrow DMS compatibility change)**

---

## 1. Document control

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1586-management-api-problem-type-prefix.md` |
| Jira ticket | [DMS-1586](https://edfi.atlassian.net/browse/DMS-1586) |
| Branch | `DMS-1586` (worktree `C:/dev/simpat/Data-Management-Service-DMS-1586`) |
| Base | `a077929f1` (`origin/main` when the worktree was created); all line numbers below are against this commit |
| Status | **Draft R2 — architect (Codex) review of R1 applied; awaiting review of R2.** Q-1 to Q-7 are resolved (§11). Step 1.1 is not approved, and no production or test code is changed until it is |
| Gate model | One step = one local commit = one explicit approval. Phase approval does not approve unseen steps. Corrections land as focused corrective commits and are resubmitted |

### 1.1 Revision history

| Rev | Date | Change |
|---|---|---|
| R1 | 2026-10-14 | Initial draft (`29e86dd9e`) |
| R2 | 2026-10-14 | Architect review of R1: removed the editor/IDE prescription from §5; the step 1.1 parser sketch uses a non-nullable `is not string type` guard; step 1.1 adds missing, null and non-integer `status` and missing, null and non-array `errors` cases under both accepted URNs; §9.3 makes the E2E image lifecycle explicit (no `-SkipDockerBuild` after a teardown, plus image verification); section references corrected (§11 challenge list, §12 validation log); Q-1 to Q-7 dispositions recorded; the step 2.1 reflection check is described as a factory-inventory guard |

---

## 2. Requirement

### 2.1 Explicit requirement (from the ticket)

> CMS and ODS Admin API v3 both implement the Ed-Fi Management API v3 surface. CMS uses `urn:ed-fi:api:*` problem types; ODS Admin API and the published specification use `urn:ed-fi:management-api:*`. Change CMS to `urn:ed-fi:management-api:`.

The retrieved ticket has no separately enumerated acceptance criteria. Everything in §2.2 beyond R-1 is **derived** (compatibility and regression requirements inferred from the repository), and is labelled as such.

### 2.2 Requirements and derived acceptance checks

| ID | Kind | Requirement | Observable acceptance check |
|---|---|---|---|
| R-1 | **Explicit** | Every Ed-Fi-taxonomy problem type CMS emits uses the `urn:ed-fi:management-api:` prefix | Each `FailureResponse` factory except `ForUnclassifiedStatus` returns exactly the "new `type`" value in §4.1 |
| R-2 | Derived (regression) | Only the prefix changes. Every suffix, HTTP status, `title`, `detail`, `correlationId`, `validationErrors`, `errors` and extension member is unchanged | Existing exact assertions on those members stay byte-for-byte unchanged and pass; the step 2.2 diff is prefix-substitution-only (§7.2 equivalence check) |
| R-3 | Derived (regression) | The RFC 9457 `about:blank` fallback is unchanged | `ForUnclassifiedStatus` still returns `type: "about:blank"`, reason-phrase title, empty detail, supplied status; `SigningKeyDependencyResponse` 503 unchanged |
| R-4 | Derived (completeness) | No CMS emitter is left on the old prefix | `git grep -n "urn:ed-fi:api" -- src/config` returns nothing after step 2.2; the step 2.1 reflection guard classifies every public factory (an inventory check; that every emitter routes through the factories rests on the §4.2 audit) |
| R-5 | Derived (compatibility) | The DMS `/oauth/token` proxy keeps recognising a CMS token-limit 429 from both an old CMS (`urn:ed-fi:api:security:authentication:too-many-tokens`) and a new CMS (`urn:ed-fi:management-api:security:authentication:too-many-tokens`) | Parameterized DMS tests: both upstream types produce DMS's token-limit 429 with the limit rebuilt; the cross-service test feeding CMS `FailureResponse.ForTooManyTokens` into the parser passes before and after step 2.2 |
| R-6 | Derived (security / regression) | The DMS parser stays exact: ordinal comparison, every existing body rule kept, near-miss and unrelated types rejected, malformed bodies fall back to the generic 429, upstream text never reaches the DMS response or log | Deviation fixtures (existing, now run under both accepted types) plus new near-miss type cases all yield `urn:ed-fi:api:too-many-requests` with empty `errors`; the secret-embedding fixture passes under both types |
| R-7 | Derived (scope) | DMS's own public problem-details types are unchanged; nothing else in `src/dms` changes | `git diff a077929f1 --stat -- src/dms` lists only `OAuthManager.cs` and `OAuthManagerTests.cs`; DMS `Response/FailureResponse.cs` untouched |
| R-8 | Derived (regression) | CMS unit, integration (PostgreSQL and SQL Server jobs API) and E2E expectations reflect the new types and pass | §9 validation results |
| R-9 | Derived (communication) | Clients that compare CMS `type` strings are told to update; earlier design decisions that kept the old prefix are marked superseded | `docs/changelog/8.1.0.md` breaking-change entry; annotations listed in step 3.2 |
| R-10 | Derived (scope) | No database schema, migration, `RelationalMappingVersion`, route or unrelated error-behavior change | `git diff a077929f1 --stat` shows no SQL, migration, `SchemaHashConstants.cs` or module routing changes |

### 2.3 Assumptions

- **A-1. Target prefix is `urn:ed-fi:management-api:`.** The ticket says so, and the ODS Admin API v3 runtime confirms it: `Application/EdFi.Ods.AdminApi.V3/Infrastructure/ErrorHandling/AdminApiProblemTypes.cs` (ODS-Admin-API, last changed in `11ba0906`, 2026-09-04) defines `urn:ed-fi:management-api:{bad-request, bad-request:data, bad-request:validation, bad-request:version-mismatch, not-found, internal-server-error, service-unavailable}`, and its Bruno v3 E2E tests assert those values. **Discrepancy:** the published `api-specifications/management/management-api-3.0.0.yaml` (Ed-Fi-API-Specifications `main`) does **not** say `management-api`. Its only URNs sit in the `problemDetails.type` description, which lists `urn:ed-fi:admin-api:*`; that text comes from ODS Admin API's `ProblemDetailsSchemaFilter`, which was not updated when the runtime moved to `management-api`. The ticket's statement about the published spec is therefore inaccurate, but its target value matches the runtime peer. See Q-1.
- **A-2. Release line.** `8.1.0` is unreleased (only `dms-pre-8.1.0-beta.*` / `cs-pre-8.1.0-beta.*` tags exist; `docs/changelog/8.1.0.md` is the only changelog and is still being edited). DMS-1586 ships in 8.1.0. See Q-4.
- **A-3.** CMS and DMS ship as separately deployable images of the same release, so a mixed-version window (old DMS + new CMS or the reverse) is possible during an upgrade (§6).
- **A-4.** `main` is squash-merged (each recent commit carries a single PR number), so the local commit series is a review device, not the merged history.

### 2.4 Non-goals

- Aligning CMS **suffixes** with ODS Admin API (`bad-request:validation`, `bad-request:version-mismatch`, `service-unavailable`), or removing CMS-only suffixes (`security:*`, `conflict*`, `bad-request:parameter`, `bad-gateway`, `method-not-allowed`, `unsupported-media-type`). Recorded as Q-2.
- Any change to DMS's own problem types (`src/dms/core/EdFi.DataManagementService.Core/Response/FailureResponse.cs`), DMS E2E expectations, DMS identity routes (`urn:ed-fi:api:identities:*`), or the `eng/` PowerShell fixtures (all DMS-owned; see §5.3).
- OAuth-format responses (`/connect/revoke`, introspection, `OAuthErrorResponseWriter`), which carry RFC 6749 `error` fields and no `type`.
- Database schemas, migrations, `SchemaHashConstants.RelationalMappingVersion`, routes, status codes, titles, details, content types.
- Rewriting historical design documents that merely *use* old CMS types (`CMS-Profiles-Design.md`, `DMS-1338-…`, `DMS-1356-…`, `DMS-1532-…`, `reference/DMS-1039/…`). Only documents that **decided** to keep the old prefix get an annotation (step 3.2).
- Correcting the upstream published specification (out of repository; noted in Q-1).
- Push, PR, merge, deploy or Jira updates (separately authorized).

---

## 3. Design summary

1. **DMS first (Phase 1).** `OAuthManager.TokenLimitFromUpstreamBody` matches the upstream `type` against exactly two ordinal constants instead of one. Nothing else in the parser changes. This lands **before** the CMS flip because the existing cross-service test (`Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service`) feeds the real CMS `FailureResponse.ForTooManyTokens` output into the parser. Flipping CMS first would turn that test red and, in a deployment, degrade every token-limit 429 to the generic one.
2. **CMS contract fixture (step 2.1, test-only, green at the old values).** A table-driven fixture pins every factory's exact `type`, status and title with non-null-tolerant access. A reflection guard makes adding an unclassified factory fail.
3. **Atomic CMS flip (step 2.2).** `_typePrefix` becomes `"urn:ed-fi:management-api"`. Every `src/config` occurrence of the old prefix (production comments, unit, integration and E2E expectations) is updated in the same commit, so no commit leaves a suite asserting the wrong contract. The commit is reviewable because, apart from one added DMS assertion, it is provably a pure prefix substitution (§7.2).
4. **Documentation (Phase 3)** and **final validation (Phase 4)**.

---

## 4. Inventory

### 4.1 CMS `FailureResponse` factories

Source: `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/FailureResponse.cs`. All types derive from `_typePrefix` (line 20) except `ForUnclassifiedStatus`.

| # | Factory (line) | Status | `title` | `detail` | Old `type` | New `type` |
|---|---|---|---|---|---|---|
| F-1 | `ForUnauthorized` (58) | 401 | caller-supplied | caller-supplied | `urn:ed-fi:api:security:authentication` | `urn:ed-fi:management-api:security:authentication` |
| F-2 | `ForTooManyTokens` (86) | 429 | `Too Many Tokens` | `The caller has authenticated too many times in too short of a time period.` | `urn:ed-fi:api:security:authentication:too-many-tokens` | `urn:ed-fi:management-api:security:authentication:too-many-tokens` |
| F-3 | `ForForbidden` (99) | 403 | caller-supplied | caller-supplied | `urn:ed-fi:api:security:authorization` | `urn:ed-fi:management-api:security:authorization` |
| F-4 | `ForBadRequest` (114) | 400 | `Bad Request` | caller-supplied | `urn:ed-fi:api:bad-request` | `urn:ed-fi:management-api:bad-request` |
| F-5 | `ForNotFound` (124) | 404 | `Not Found` | caller-supplied | `urn:ed-fi:api:not-found` | `urn:ed-fi:management-api:not-found` |
| F-6 | `ForMethodNotAllowed` (134) | 405 | `Method Not Allowed` | `The request construction was invalid.` | `urn:ed-fi:api:method-not-allowed` | `urn:ed-fi:management-api:method-not-allowed` |
| F-7 | `ForUnsupportedMediaType` (144) | 415 | `Unsupported Media Type` | `The value specified in the 'Content-Type' header is not supported by this host.` | `urn:ed-fi:api:unsupported-media-type` | `urn:ed-fi:management-api:unsupported-media-type` |
| F-8 | `ForUnclassifiedStatus` (159) | supplied | reason phrase | `""` | `about:blank` | **`about:blank` (unchanged)** |
| F-9 | `ForConflict` (169) | 409 | `Conflict` | caller-supplied | `urn:ed-fi:api:conflict` | `urn:ed-fi:management-api:conflict` |
| F-10 | `ForDependentItemExists` (179) | 409 | `Dependent Item Exists` | caller-supplied | `urn:ed-fi:api:conflict:dependent-item-exists` | `urn:ed-fi:management-api:conflict:dependent-item-exists` |
| F-11 | `ForUnresolvedReference` (189) | 409 | `Unresolved Reference` | caller-supplied | `urn:ed-fi:api:conflict:unresolved-reference` | `urn:ed-fi:management-api:conflict:unresolved-reference` |
| F-12 | `ForDataValidation` (199) | 400 | `Data Validation Failed` | `Data validation failed. See 'validationErrors' for details.` | `urn:ed-fi:api:bad-request:data` | `urn:ed-fi:management-api:bad-request:data` |
| F-13 | `ForParameterValidation` (220) | 400 | `Parameter Validation Failed` | `Parameter validation failed. See 'errors' for details.` | `urn:ed-fi:api:bad-request:parameter` | `urn:ed-fi:management-api:bad-request:parameter` |
| F-14 | `ForBadGateway` (230) | 502 | `Bad Gateway` | caller-supplied | `urn:ed-fi:api:bad-gateway` | `urn:ed-fi:management-api:bad-gateway` |
| F-15 | `ForUnknown` (240) | 500 | `Internal Server Error` | `""` | `urn:ed-fi:api:internal-server-error` | `urn:ed-fi:management-api:internal-server-error` |

Body shape (`CreateBaseJsonObject`, lines 32–56) is unchanged: `detail`, `type`, `title`, `status`, `correlationId`, `validationErrors` (`{}` default), `errors` (`[]` default), in that order. The `ForTooManyTokens` `errors[0]` sentence is a cross-service parsing contract and is not touched.

### 4.2 CMS emission paths (all route through `FailureResponse`)

The repository audit (`git grep 'urn:ed-fi:' -- src/config`; `git grep -E '\["type"\]' -- src/config` excluding tests) found **no** CMS production code that builds a `type` outside `FailureResponse`. The only other `["type"]` writes are OpenAPI schema nodes in `MetadataModule.cs`. Paths that reach the factories:

- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/FailureResults.cs` (module `IResult` helpers, including 429 `TooManyTokens`)
- `FailureResponseWriter.cs`, `GlobalExceptionHandler`, framework-error / JSON-charset / tenant-resolution / invalid-configuration / pipeline-exception middleware (frontend `Infrastructure` and `Middleware`)
- Module branches that call `Results.Json(FailureResponse.ForX(...), contentType: "application/problem+json")` (`ApiClientModule`, `ApplicationModule`, `ClaimSetModule`, `DataStoreContextModule`, `OwnershipTokenModule`, `VendorModule`, …)
- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/SigningKeys/SigningKeyDependencyResponse.cs` → `ForUnclassifiedStatus(503, …)` → `about:blank` (unchanged, R-3)
- Not affected: `OAuthErrorResponseWriter.cs` (RFC 6749 `error` / `error_description`; no `type` member)

Because every path delegates to the factories, the one-line prefix change is complete for production behavior. R-4's grep and reflection guard defend that claim.

### 4.3 Affected files

Counts are occurrences of `urn:ed-fi:api` at the base commit. **Every** `src/config` occurrence is a CMS-owned type (verified line by line during the audit), which is what makes the scoped substitution in step 2.2 safe.

**CMS production (step 2.2)**

| File | Occurrences | Nature |
|---|---|---|
| `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/FailureResponse.cs` | 2 | line 20 `_typePrefix` (behavior); line 215 doc comment |
| `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Infrastructure/ParameterValidationException.cs` | 1 | doc comment (line 13) |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/FailureResults.cs` | 4 | doc comments (42, 54, 67, 107) |
| `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/PagingQueryValidators.cs` | 1 | doc comment (57) |

**CMS unit tests (step 2.1 adds one fixture; step 2.2 updates expectations)**

| File (under `src/config/`) | Occurrences |
|---|---|
| `backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Infrastructure/FailureResponseTests.cs` | 14 |
| `frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Infrastructure/FailureResponseWriterTests.cs` | 3 (line 131 is an input body that models a CMS response; updated for consistency, behavior-neutral) |
| `frontend/…Tests.Unit/Infrastructure/FailureResultsTests.cs` | 4 |
| `frontend/…Tests.Unit/Infrastructure/GlobalExceptionHandlerTests.cs` | 11 |
| `frontend/…Tests.Unit/Infrastructure/ReportInvalidConfigurationMiddlewareTests.cs` | 1 |
| `frontend/…Tests.Unit/Jobs/JobModuleTests.cs` | 2 |
| `frontend/…Tests.Unit/Middleware/FrameworkErrorResponseTests.cs` | 8 |
| `frontend/…Tests.Unit/Middleware/JsonCharsetValidationTests.cs` | 5 |
| `frontend/…Tests.Unit/Middleware/PipelineExceptionBoundaryTests.cs` | 1 |
| `frontend/…Tests.Unit/Middleware/TenantResolutionMiddlewareTests.cs` | 2 |
| `frontend/…Tests.Unit/Modules/ActionModuleTests.cs` | 2 |
| `frontend/…Tests.Unit/Modules/ApiClientModuleTests.cs` | 42 |
| `frontend/…Tests.Unit/Modules/ApplicationModuleTests.cs` | 25 |
| `frontend/…Tests.Unit/Modules/AuthorizationStrategiesModuleTests.cs` | 2 |
| `frontend/…Tests.Unit/Modules/ClaimSetModuleTests.cs` | 16 |
| `frontend/…Tests.Unit/Modules/ClaimsManagementModuleTests.cs` | 6 |
| `frontend/…Tests.Unit/Modules/DataStoreContextModuleTests.cs` | 2 |
| `frontend/…Tests.Unit/Modules/DataStoreDerivativeModuleTests.cs` | 5 |
| `frontend/…Tests.Unit/Modules/DataStoreModuleTests.cs` | 2 |
| `frontend/…Tests.Unit/Modules/IdentityModuleTests.cs` | 12 (includes the 429 `too-many-tokens` body at 1048) |
| `frontend/…Tests.Unit/Modules/InformationModuleTests.cs` | 1 |
| `frontend/…Tests.Unit/Modules/ProfileModuleTests.cs` | 5 |
| `frontend/…Tests.Unit/Modules/TenantModuleTests.cs` | 1 |
| `frontend/…Tests.Unit/Modules/VendorModuleTests.cs` | 5 |

**CMS integration tests (step 2.2): jobs API on both providers**

| File | Line | Assertion |
|---|---|---|
| `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/Jobs/JobApiIntegrationTests.cs` | 541 | `Given_jobs_of_two_tenants_and_without_a_tenant`: other-tenant read → `not-found` |
| `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/Jobs/JobApiIntegrationTests.cs` | 446 | same fixture, SQL Server |

**CMS E2E (step 2.2)**: `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/`

| File | Occurrences | Types |
|---|---|---|
| `Features/ApiClients.feature` | 9 | `conflict:unresolved-reference`, `bad-request:data` |
| `Features/Applications.feature` | 11 | `conflict:unresolved-reference`, `bad-request:data`, `security:authentication`, `conflict` |
| `Features/ClaimSets.feature` | 8 | `not-found`, `bad-request:data`, `bad-request` |
| `Features/Connect.feature` | 7 | `bad-request:data`, `security:authentication` (CMS `/connect/token`) |
| `Features/DataStoreContexts.feature` | 1 | `bad-request:data` |
| `Features/DataStoreDerivatives.feature` | 9 | `conflict:unresolved-reference`, `bad-request:data` |
| `Features/DataStores.feature` | 10 | `bad-request:data`, `not-found` |
| `Features/Jobs.feature` | 2 | `not-found` (line 27, `@MssqlRepresentative`), `bad-request` (line 53, `@MssqlMultitenantRepresentative @MultitenantOnly`) |
| `Features/Tenants.feature` | 3 | `bad-request:data`, `conflict:unresolved-reference` |
| `Features/Token.feature` | 1 | `security:authentication` |
| `Features/Vendors.feature` | 6 | `bad-request:data` |
| `StepDefinitions/ClaimsManagementStepDefinitions.cs` | 1 | `bad-request:data` (line 357) |

**DMS (Phase 1, plus one assertion in step 2.2)**

| File | Change |
|---|---|
| `src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthManager.cs` | lines 162–166 constant and doc; line 415 comparison |
| `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth/OAuthManagerTests.cs` | lines 1740–2229 token-limit fixtures |

**Documentation (Phase 3)**

| File | Change |
|---|---|
| `docs/CMS-BACKGROUND-JOBS.md` | line 111 type; lines 147–149: the "deliberate runtime difference" becomes parity with upstream |
| `docs/CONFIGURATION.md` | lines 997–1000: state that CMS `/connect/token` answers with the `management-api` type, DMS `/oauth/token` keeps the DMS type, and DMS accepts either upstream type |
| `docs/changelog/8.1.0.md` | new Breaking-changes entry; line 18 disambiguated (CMS vs DMS type); line 229 type updated |
| `reference/design/configuration-service/DMS-1218-cms-error-response-compliance.md` | superseded annotations at §10 (line 197) and §11 (line 210) |
| `reference/design/jobs-DMS-1437/spec.md` | superseded annotation at the Q1 disposition (line 53) and at line 368 |
| `reference/design/jobs-DMS-1437/admin-api-v3-provenance.md` | superseded annotation at line 72 |
| this spec | status and validation evidence (Phase 4) |

### 4.4 Inspected and deliberately unchanged

| Location | Why unchanged |
|---|---|
| `src/dms/core/EdFi.DataManagementService.Core/Response/FailureResponse.cs` (incl. line 45 `too-many-tokens`) | DMS's own public types (R-7) |
| `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/TokenEndpointModuleTests.cs:570–614` | Stubs the upstream with **DMS's** `FailureResponse.ForTooManyTokens`, which still emits the legacy type; this remains a legacy-upstream proxy test and keeps passing because the legacy type stays accepted |
| `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Response/FailureResponseTests.cs:318` | DMS's own type |
| All `src/dms/tests/**` E2E and `EdFi.InstanceManagement.Tests.E2E` problem types | DMS resource, descriptor, identity and authorization types |
| `eng/docker-compose/tests/StockImageProofDecisions.Tests.ps1:233,247` | DMS write-path custom-validation fixture |
| `eng/verification/tests/IdentityWireCompatibility.Tests.ps1:413,432,433` | DMS identity OpenAPI fixtures |
| `docs/OWASP-AUTH-COVERAGE.md:250,394`, `docs/IDENTITY-MANAGEMENT.md`, `docs/DATA-STRICTNESS.md`, `docs/UNIQUEID-VALIDATION.md`, `docs/API-CLIENT-AND-INSTANCE-CONFIGURATION.md:157`, `docs/CONFIGURATION.md:786,884,902`, `docs/changelog/8.1.0.md:176` | Describe DMS responses |
| `src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthManager.cs` 401 / 409 / default arms | Read RFC 6749 fields or status only; never a CMS `type` |
| Historical design docs listed in §2.4 | Records of their time; they recorded no decision about the prefix |

---

## 5. Phases and steps

Conventions for every step:

- Changed C# files are formatted with `dotnet csharpier format <changed .cs files>` (AGENTS.md).
- Before committing: review `git diff` for accidental replacements, unrelated formatting, weakened assertions and out-of-scope files; run the focused checks listed; commit locally with the stated message ending in the `Co-Authored-By` trailer.
- Report after committing: phase and step, commit SHA, each edited file with a one-line explanation, test commands and results (skips reported as skips), remaining risks, and any deviation from this spec. **Then stop for approval.**

### Phase 0 — Specification

**Purpose:** agree the scope, sequencing and validation before any change. **Dependencies:** none. **Completion:** spec committed; architect approval recorded in the conversation; Q-1 and Q-3 resolved.

#### Step 0.1 — Commit this spec

- Edit: create this file only.
- Tests: none.
- Commit: `[DMS-1586] Add implementation spec for the management-api problem-type prefix`
- Checkpoint: Codex review per the challenge list in §11; revise as R2 if needed (spec-only commits).

### Phase 1 — DMS token-proxy compatibility

**Purpose:** make DMS accept both CMS token-limit types before CMS changes, so that no commit and no supported deployment order loses the token-limit 429. **Dependencies:** Phase 0 approved. **Completion:** DMS parser accepts exactly the two types; all OAuth tests green; DMS public types unchanged.

#### Step 1.1 — Accept both upstream token-limit URNs in `OAuthManager`

Production (`src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthManager.cs`):

1. Replace the single constant at line 166 with two, keeping the doc-comment style:
   ```csharp
   /// <summary>
   /// The <c>type</c> a Configuration Service token-limit rejection carries. Before DMS-1586 the
   /// Configuration Service used the <c>urn:ed-fi:api</c> prefix; from DMS-1586 on it uses the
   /// Management API's <c>urn:ed-fi:management-api</c> prefix. Both are matched exactly, so a DMS
   /// deployed beside either version still recognizes the rejection. Neither value is DMS's own
   /// response type, which <see cref="Response.FailureResponse.ForTooManyTokens"/> sets and
   /// DMS-1586 does not change.
   /// </summary>
   private const string LegacyTooManyTokensType = "urn:ed-fi:api:security:authentication:too-many-tokens";

   private const string ManagementApiTooManyTokensType =
       "urn:ed-fi:management-api:security:authentication:too-many-tokens";
   ```
2. Line 415: guard on a scalar `type` with a non-nullable pattern variable, then accept either constant:
   ```csharp
   if (ScalarValueOrNull(obj["type"]) is not string type)
   {
       return null;
   }

   if (
       !string.Equals(type, LegacyTooManyTokensType, StringComparison.Ordinal)
       && !string.Equals(type, ManagementApiTooManyTokensType, StringComparison.Ordinal)
   )
   {
       return null;
   }
   ```
   `ScalarValueOrNull` returns null for an absent member, a JSON `null`, an object and an array, so those bodies keep the existing fallback exactly as today.
3. Update the line 402–407 comment only to say "a type other than the two accepted ones". The status, `errors`, message-prefix and suffix, and `NumberStyles.None` limit rules, the fallback logging and the rebuilt response stay **unchanged**.

Tests (`src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth/OAuthManagerTests.cs`):

1. In `TooManyTokensUpstream` (line 1748), replace `Type` with `LegacyType` and `ManagementApiType`; `BodyWithMessage(string message, string type)` and `BodyWithLimit(string limit, string type)` take the upstream type explicitly.
2. Add `private const string DmsTooManyTokensType = "urn:ed-fi:api:security:authentication:too-many-tokens";` with a comment saying it is DMS's own response type, unchanged by DMS-1586. Output assertions use it instead of the upstream constant, so the tests prove DMS answers with its own type rather than echoing the upstream one.
3. Parameterize with `[TestFixture(TooManyTokensUpstream.LegacyType)]` and `[TestFixture(TooManyTokensUpstream.ManagementApiType)]` (constructor argument):
   - `Given_An_Upstream_Token_Limit_Rejection`: 429, `type == DmsTooManyTokensType`, title, detail, rebuilt message with limit 5, DMS correlation id.
   - `Given_An_Upstream_Token_Limit_Rejection_With_A_Different_Limit` (limit 37).
   - `Given_An_Upstream_Token_Limit_Rejection_Embedding_A_Secret`: no sentinel in the response or the log, member names under the trace id, generic fallback. This covers malformed-body fallback and non-leakage under both types.
4. `Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service`: `It_recognizes_the_token_limit_rejection` asserts `DmsTooManyTokensType`. The fixture still uses the real CMS factory, so it passes before and after step 2.2.
5. `Given_An_Upstream_429_Whose_Body_Deviates.DeviatingBodies()`: type-independent cases (unparseable, non-object, `null`) once; every type-dependent case (other status, empty, multi-element, non-string and object errors, unrecognized message, non-numeric, absent, zero, negative, overflowing and whitespace-padded limits) once **per accepted type**, with the type in the case name. Add near-miss type cases, each in an otherwise canonical body:
   - management-api type with a trailing space
   - management-api type upper-cased (ordinal comparison)
   - parent type `urn:ed-fi:management-api:security:authentication`
   - extended type `urn:ed-fi:management-api:security:authentication:too-many-tokens:extra`
   - `urn:ed-fi:management-api:too-many-requests`
   - the stale published-spec spelling `urn:ed-fi:admin-api:security:authentication:too-many-tokens` (Q-1)
   - `type` member absent
   - `type` as a JSON array holding the management-api value
   - `type` as JSON `null`

   Add `status` and `errors` shape cases, each **once per accepted type**, in an otherwise canonical body. They pin rules that already exist at `OAuthManager.cs` lines 420–432 (`status` must be a JSON integer equal to 429; `errors` must be a JSON array of exactly one entry); production validation does not change:
   - `status` member absent
   - `status` as JSON `null`
   - `status` as the string `"429"`
   - `status` as the non-integer number `429.5`
   - `errors` member absent
   - `errors` as JSON `null`
   - `errors` as a string holding the canonical message
   - `errors` as an object `{ "0": "<canonical message>" }`

   The helpers gain the `type` parameter; `BodyWithStatus` takes the raw JSON token for `status` (`null`, `"429"`, `429.5`), and a body builder that omits a named member covers the absent cases. Every deviating case, old and new, asserts the unchanged generic contract through `ShouldBeTheGenericRateLimitContract`: 429, `urn:ed-fi:api:too-many-requests`, `Too Many Requests`, empty `errors`, DMS correlation id.
6. `Given_An_Upstream_Status_The_New_Arm_Does_Not_Claim`: run each status with both upstream types (still 502 `urn:ed-fi:api:bad-gateway`).

Mutation reasoning (each must fail if the behavior is reverted): drop the management-api constant → the ManagementApi parameterizations of fixtures 3 fail (generic type instead of `DmsTooManyTokensType`). Drop the legacy constant → the Legacy parameterizations fail. Switch to `OrdinalIgnoreCase`, `StartsWith` or `Contains` → the near-miss cases fail. Echo the upstream type → `DmsTooManyTokensType` assertions under ManagementApi fail. Relax the `status` or `errors` shape checks → the matching shape cases fail under the affected type.

- Focused checks:
  - `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~OAuth.OAuthManagerTests"`
  - `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~TokenEndpointModuleTests"`
  - `dotnet csharpier check` on the two files.
- Commit: `[DMS-1586] Accept the management-api token-limit type in the DMS token proxy`
- Checkpoint: reviewer confirms that no rule other than the type set changed (diff limited to lines 162–166, 402–417 and the tests).

### Phase 2 — CMS problem-type prefix

**Purpose:** satisfy R-1 to R-4 and R-8 at the unit and integration level. **Dependencies:** step 1.1 committed and approved. **Completion:** zero `urn:ed-fi:api` occurrences under `src/config`; CMS unit suites green; both jobs integration lanes run; E2E feature expectations updated (executed in Phase 4).

#### Step 2.1 — Add an exhaustive CMS factory contract fixture (test-only, at current values)

In `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Infrastructure/FailureResponseTests.cs`, add nested fixture `Given_each_failure_factory`:

- A `TestCaseSource` with one case per factory F-1 to F-15: factory name, a `Func<JsonNode>` invoking it with fixed arguments, expected `type`, `status` and `title`. Expected values are the **current** (`urn:ed-fi:api:…`) types, and `about:blank` for F-8.
- `It_has_the_exact_type`, `It_has_the_expected_status`, `It_has_the_expected_title`: use `node["type"]!.GetValue<string>()` (non-null-tolerant). The existing `result["type"]?.…Should()` lines would silently skip if `type` were missing; this fixture cannot.
- `It_classifies_every_public_factory`: reflection over `typeof(FailureResponse).GetMethods(BindingFlags.Public | BindingFlags.Static)` returning `JsonNode` must equal the case-name set, so a newly added factory fails until it is classified. This is a factory-inventory guard: it proves every public factory is classified, not that every CMS emitter routes through the factories. That second claim rests on the §4.2 audit and the R-4 grep.

The fixture passes at the base commit; that is the point. Step 2.2 flips its expectations together with production, which proves the fixture detects the change. Before committing, a temporary local mutation (changing `_typePrefix`) is run to show the fixture fails, then reverted; the result is reported, not committed.

- Focused check: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~FailureResponseTests"`.
- Commit: `[DMS-1586] Pin every Configuration Service problem type in a contract fixture`
- Checkpoint: reviewer confirms the table matches §4.1 and that the reflection guard is wanted (Q-6).

#### Step 2.2 — Switch CMS to the `urn:ed-fi:management-api` prefix

1. `FailureResponse.cs` line 20: `private static readonly string _typePrefix = "urn:ed-fi:management-api";`. No other production statement changes.
2. In every file listed in §4.3 under *CMS production*, *CMS unit tests*, *CMS integration tests* and *CMS E2E* (and only those files), replace the literal `urn:ed-fi:api` with `urn:ed-fi:management-api`. The edit is driven by the explicit file list, not a glob, and touches doc comments, expectations and the step 2.1 table alike. `about:blank` assertions are untouched by construction.
3. `OAuthManagerTests.Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service`: add `It_is_fed_the_management_api_type`, asserting that the CMS-built upstream body's `type` equals `TooManyTokensUpstream.ManagementApiType`. The cross-service test then demonstrably exercises the new value, not the legacy one. This is the only non-mechanical edit in the step and the only `src/dms` file touched.

Why one commit (Q-3): splitting by test suite would leave intermediate commits whose integration or E2E expectations assert a contract production no longer emits. One atomic flip keeps every commit coherent, and §7.2 keeps it reviewable.

- Focused checks:
  - §7.2 equivalence check (every changed `src/config` file equals its base content with the prefix substituted)
  - `git grep -n "urn:ed-fi:api" -- src/config` → no output
  - `git diff HEAD~1 --stat -- src/dms` → only `OAuthManagerTests.cs`
  - backend unit project (full)
  - frontend unit project (full): `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj`
  - DMS `OAuth.OAuthManagerTests` filter
  - PostgreSQL jobs API: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj --filter "FullyQualifiedName~Jobs.JobApiIntegrationTests"`
  - SQL Server jobs API: same filter on `EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj` with `ConnectionStrings__MssqlAdmin` set (§9.2)
  - `dotnet csharpier check src/config` plus the DMS test file
- Commit: `[DMS-1586] Use the urn:ed-fi:management-api problem-type prefix in the Configuration Service`
- Checkpoint: reviewer checks the equivalence output, the empty grep, the single `src/dms` file, and that no `title`, `detail`, `status` or `errors` literal moved.

### Phase 3 — Documentation

**Purpose:** R-9. **Dependencies:** step 2.2 approved, so the docs describe shipped behavior. **Completion:** client-facing note present; superseded decisions annotated; no DMS-type documentation altered.

#### Step 3.1 — Current documentation and changelog

- `docs/changelog/8.1.0.md`, *Breaking changes*: add an entry, **The Configuration Service problem types use the Management API prefix.** It says every Configuration Service problem-details `type` now starts with `urn:ed-fi:management-api:` instead of `urn:ed-fi:api:`, with suffixes, statuses, titles and bodies unchanged and `about:blank` unchanged. Clients that compare `type` strings, including scripts against `/connect/token`, must update. This matches ODS Admin API v3. DMS's own `urn:ed-fi:api:*` types are unchanged. Upgrade DMS no later than the Configuration Service (§6). Link DMS-1586.
- `docs/changelog/8.1.0.md` line 18: say that the Configuration Service answers `urn:ed-fi:management-api:security:authentication:too-many-tokens`, while DMS `/oauth/token` keeps `urn:ed-fi:api:security:authentication:too-many-tokens` and accepts either upstream type. Line 229: `urn:ed-fi:management-api:bad-request:data`. (Edited in place because 8.1.0 is unreleased; Q-4.)
- `docs/CONFIGURATION.md` lines 997–1000: same CMS-vs-DMS distinction as line 18.
- `docs/CMS-BACKGROUND-JOBS.md` line 111: `urn:ed-fi:management-api:not-found`. Lines 147–149: replace "One runtime difference is deliberate…" with a sentence saying the not-found type matches upstream's `urn:ed-fi:management-api:not-found` (since DMS-1586).
- Check: `git diff HEAD~1 --stat` lists only these three docs; rendered tables are intact.
- Commit: `[DMS-1586] Document the Configuration Service problem-type prefix change`

#### Step 3.2 — Mark earlier prefix decisions as superseded

Insert one blockquote per location; original text is kept verbatim as historical record:

> **Superseded by [DMS-1586](https://edfi.atlassian.net/browse/DMS-1586):** the Configuration Service now uses the `urn:ed-fi:management-api:` prefix for these types; suffixes are unchanged and `about:blank` is unchanged. See `reference/design/configuration-service/DMS-1586-management-api-problem-type-prefix.md`.

Locations: DMS-1218 spec §10 (after line 206) and §11 (after line 210); jobs spec after line 53 (Q1 "keep `urn:ed-fi:api:not-found`") and after line 368; `admin-api-v3-provenance.md` after line 72.

- Check: diff consists of insertions only.
- Commit: `[DMS-1586] Mark earlier Configuration Service prefix decisions as superseded`

### Phase 4 — Final validation

**Purpose:** prove R-1 to R-10 on the final tree before asking to push. **Dependencies:** Phase 3 approved. **Completion:** §9 suite executed with results recorded; final acceptance diff review presented.

#### Step 4.1 — Full validation run (no commit unless a defect is found)

Run §9 in order and report each command's actual result, with skips and environment blockers listed separately. A defect found here is fixed in a focused corrective commit under its own approval.

#### Step 4.2 — Record evidence in this spec

Update §1 status, fill §12 (validation log) and the commit list. Commit: `[DMS-1586] Record validation evidence in the spec`. Then present the final commit list, edited-file summary, requirement-to-test evidence and limitations, and **request push approval** (no push, PR, merge or Jira update without it).

---

## 6. Cross-service compatibility and upgrade sequencing

| DMS | CMS | Token-limit 429 at DMS `/oauth/token` | Other effects |
|---|---|---|---|
| old | old | Token-limit 429 (DMS type, limit rebuilt) | none |
| **new** | old | Token-limit 429 (legacy upstream type accepted) | none |
| **new** | **new** | Token-limit 429 (management-api upstream type accepted) | CMS clients see new types |
| old | **new** | **Degrades** to generic 429 `urn:ed-fi:api:too-many-requests`, empty `errors` (limit not shown), plus one `Unrecognized 429 body` warning per occurrence; still 429, no upstream text leaked | CMS clients see new types |

**Deployment-order limitation:** upgrade DMS before, or together with, the Configuration Service. In the reverse window, callers still get 429 and retrying behavior is unaffected, but the "limit is N" message is lost until DMS is upgraded. Nothing else in DMS reads a CMS `type`: the 401 arm reads RFC 6749 fields, the 409 arm reads the status only, and the default arm logs a sanitized body. No other cross-service parsing exists (§4.4).

**Clients of the Configuration Service** (Admin App, automation, the E2E suites) that compare `type` strings must accept the new values. The change moves CMS *toward* ODS Admin API v3 parity: Ed-Fi Admin App already handles `urn:ed-fi:management-api:*` for Admin API v3.

**No persistence impact:** problem types are not stored. There is no schema, migration or `EffectiveSchemaHash` change and no reprovisioning.

---

## 7. Review aids

### 7.1 Scope guards (run at every step from 2.2 on)

```powershell
git grep -n "urn:ed-fi:api" -- src/config             # expect: no output after 2.2
git diff a077929f1 --stat -- src/dms                  # expect: OAuthManager.cs, OAuthManagerTests.cs only
git diff a077929f1 --stat -- '*.sql' 'src/dms/core/EdFi.DataManagementService.Core/Utilities/SchemaHashConstants.cs'   # expect: nothing
```

### 7.2 Prefix-substitution equivalence check (step 2.2)

For every `src/config` file changed by step 2.2, the base content with `urn:ed-fi:api` replaced by `urn:ed-fi:management-api` must equal the new content:

```powershell
git diff --name-only HEAD~1 HEAD -- src/config | ForEach-Object {
    $expected = (git show "HEAD~1:$_" | Out-String) -replace 'urn:ed-fi:api', 'urn:ed-fi:management-api'
    $actual = git show "HEAD:$_" | Out-String
    if ($expected -ne $actual) { "NOT A PURE PREFIX SUBSTITUTION: $_" }
}
```

Expected output: nothing. The only other changed file is `OAuthManagerTests.cs`, reviewed by hand.

---

## 8. Risks and failure cases

| Risk | Effect | Mitigation |
|---|---|---|
| A CMS emitter outside `FailureResponse` keeps the old prefix | Mixed prefixes from CMS | §4.2 audit; R-4 grep; step 2.1 reflection guard; E2E sweep in Phase 4 |
| Substitution reaches DMS-owned text | DMS contract change | Explicit file list (no glob); §7.1 `src/dms` stat; §7.2 equivalence check |
| Parser loosened beyond two exact values | Spoofed or partial token-limit bodies believed | Near-miss cases in step 1.1; ordinal comparison kept |
| Old DMS deployed with new CMS | Generic 429 instead of token-limit 429 | §6 ordering note in changelog; still a 429, no leak |
| Upstream spec text says `admin-api` | Reviewer or client confusion | Q-1; changelog cites ODS Admin API runtime parity; `admin-api` spelling explicitly rejected by DMS tests |
| Clients hard-code `urn:ed-fi:api:*` CMS types | Client-side mismatch | Breaking-change entry |
| SQL Server integration tests skip when `ConnectionStrings__MssqlAdmin` is unset | False confidence | Skips reported as skips; MSSQL instance started per §9.2 |
| Keycloak E2E lanes are not run locally | Provider-specific expectation miss | CMS E2E types are provider-independent (same `FailureResponse`); Keycloak legs run in PR CI before merge; reported as a remaining gate |
| E2E environment state left over between runs, or a lane running a stale image | Flaky E2E, or results against old code | §9.3 lifecycle: teardown (plus `-d -v` after SQL Server lanes), rebuild on every lane, image-ID verification recorded in §12 |

---

## 9. Final validation plan (Phase 4)

### 9.1 Unit (no infrastructure)

| # | Command | Expected coverage |
|---|---|---|
| V-1 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj` | F-1 to F-15 exact types, reflection guard, unchanged bodies |
| V-2 | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj` | Representative endpoint, module and middleware responses (FailureResults, GlobalExceptionHandler, framework errors, JSON charset, tenant resolution, Identity 429, Jobs) |
| V-3 | `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj` | Includes `OAuth.OAuthManagerTests`: both URNs, near misses, malformed bodies, cross-service fixture |
| V-4 | `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~TokenEndpointModuleTests"` | Legacy-upstream proxy path unchanged |
| V-5 | `dotnet csharpier check src/config` and the two DMS files | Formatting |

### 9.2 Integration (both providers)

| # | Command | Prerequisite |
|---|---|---|
| V-6 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj` (full; jobs fixture `Given_jobs_of_two_tenants_and_without_a_tenant` must be reported) | PostgreSQL reachable per the project's `appsettings.json` (`localhost:5432`, `edfi_configurationservice`) or its environment override |
| V-7 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj` (full; same fixture) | `ConnectionStrings__MssqlAdmin` pointing at SQL Server 2025 (container `dms-mssql-integration-2025` on `14333`, or LocalDB, which is acceptable for this non-engine ticket); unset means the fixtures are **ignored**, which is reported as a blocker, not a pass |

### 9.3 CMS E2E (Docker; run from the repository root)

Prerequisite: a Release build (`./build-config.ps1 Build -Configuration Release`).

**Image lifecycle.** `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1` stops the `cs-local` stack, deletes its volumes **and deletes the locally built `ed-fi-api-config-local` image**. Every lane therefore runs **without** `-SkipDockerBuild`; `build-config.ps1 E2ETest` then calls `eng/docker-compose/start-local-config.ps1 -r`, which rebuilds the image from the current tree. For each lane, in order:

1. **Teardown.** `pwsh src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1`. Its own `compose down` names only `postgresql.yml`, so after an SQL Server lane also run `./start-local-config.ps1 -d -v -EnvironmentFile <that lane's env file>` from `eng/docker-compose`. Then confirm `docker ps -a --filter "label=com.docker.compose.project=cs-local"` lists nothing.
2. **Run** the lane command below, which rebuilds the image.
3. **Verify the image.** `docker image inspect ed-fi-api-config-local --format "{{.Id}} {{.Created}}"` must show a creation time after the lane started, and `docker inspect ed-fi-api-config-service --format "{{.Image}}"` must show the same image ID. Record both values in §12.

| # | Command | Coverage |
|---|---|---|
| V-8 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained` | PostgreSQL, single-tenant: all 11 affected feature files and the ClaimsManagement step |
| V-9 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.e2e" -E2ETestFilter "TestCategory=MssqlRepresentative"` | SQL Server representative, including `Jobs.feature` not-found |
| V-10 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.multitenant.e2e" -E2ETestFilter "TestCategory=MssqlMultitenantRepresentative"` | SQL Server multi-tenant, including `Jobs.feature` missing-tenant `bad-request` |
| — | Keycloak legs (`-IdentityProvider keycloak`) | PR CI matrix; listed as an outstanding merge gate unless run locally |

DMS E2E is not required: no DMS response changes, and no DMS E2E scenario exercises the CMS token-limit 429 (Q-7).

### 9.4 Static acceptance checks

§7.1 scope guards on the final tree; a final `git diff a077929f1 --stat` review against §4.3.

---

## 10. Acceptance traceability

| Req | Steps | Tests and checks | Observable result |
|---|---|---|---|
| R-1 | 2.1, 2.2 | `Given_each_failure_factory` (V-1); module and middleware expectations (V-2); E2E features (V-8 to V-10) | Each CMS problem type starts `urn:ed-fi:management-api:` |
| R-2 | 2.2 | §7.2 equivalence check; unchanged title, status, detail and errors assertions (V-1, V-2); `It_has_a_single_error_naming_the_limit` | Bodies identical apart from the prefix |
| R-3 | 2.1, 2.2 | `Given_an_unclassified_status_failure`; F-8 table row; `SigningKeyBearerEventsTests`, `GlobalExceptionHandlerTests` and `FrameworkErrorResponseTests` `about:blank` assertions (unchanged) | `about:blank` still returned |
| R-4 | 2.1, 2.2 | Reflection guard; `git grep` empty; E2E sweep | No CMS output on the old prefix |
| R-5 | 1.1, 2.2 | Parameterized token-limit fixtures; `Given_A_Token_Limit_Rejection_Built_By_The_Configuration_Service` incl. `It_is_fed_the_management_api_type` (V-3) | DMS token-limit 429 for both upstream types |
| R-6 | 1.1 | `Given_An_Upstream_429_Whose_Body_Deviates` (both types and near misses); `…_Embedding_A_Secret` (both types); `Given_An_Unrecognized_429_Body_Reaching_The_Log`; `Given_An_Upstream_Status_The_New_Arm_Does_Not_Claim` | Generic 429, empty errors, no leak, exact match only |
| R-7 | 1.1, 2.2 | §7.1 `src/dms` stat; V-3, V-4 unchanged DMS-type assertions | DMS public types unchanged |
| R-8 | 2.2, 4.1 | V-1 to V-10 | Suites green on the final tree |
| R-9 | 3.1, 3.2 | Doc diff review | Changelog entry; superseded annotations |
| R-10 | all | §7.1 SQL and `SchemaHashConstants` guard | No schema or mapping change |

---

## 11. Open questions and dispositions

All questions were resolved in the architect's review of R1 (2026-10-14).

| ID | Question | Disposition |
|---|---|---|
| Q-1 | The published `management-api-3.0.0.yaml` lists `urn:ed-fi:admin-api:*` (stale `ProblemDetailsSchemaFilter` text), while the ODS Admin API v3 runtime (`AdminApiProblemTypes.cs`) and its Bruno tests use `urn:ed-fi:management-api:*`, as does the ticket. Confirm `management-api`. | **Confirmed:** use `urn:ed-fi:management-api:`. The architect independently verified the runtime constants and the YAML discrepancy. The discrepancy note (§2.3 A-1) stays; DMS does not accept `admin-api` (near-miss test). Correcting the upstream spec is outside this ticket |
| Q-2 | Suffix parity with Admin API (`bad-request:validation`, `version-mismatch`, `service-unavailable` vs CMS-only suffixes) | **Accepted:** suffixes are preserved; parity is out of scope |
| Q-3 | Step 2.2 as one atomic flip (production plus every `src/config` expectation) versus per-suite steps with stale intermediate commits | **Approved:** one atomic CMS switch commit including the directly affected expectations, reviewed with the §7.2 equivalence check |
| Q-4 | Changelog: add to unreleased `8.1.0.md` and correct its lines 18 and 229 in place, versus a new changelog file | **Accepted:** `8.1.0.md`, in place |
| Q-5 | How long DMS keeps accepting the legacy upstream type | **Accepted:** indefinitely |
| Q-6 | Keep the reflection completeness guard in step 2.1 | **Keep, as a factory-inventory guard.** It does not independently prove that every CMS emitter uses the factories; that rests on the §4.2 audit and the R-4 grep |
| Q-7 | Run a DMS E2E shard as well | **Not required:** the step 1.1 OAuth tests and V-4 token-endpoint tests cover the affected path |

DMS-first sequencing (Phase 1 before Phase 2) and the §6 deployment-order note are accepted. DMS's public types, and every CMS field other than the taxonomy prefix, stay unchanged.

### Review challenge list (for Codex)

1. Does the plan still satisfy R-1 exactly, including both the CMS `/connect/token` errors and the jobs API?
2. Is there any CMS emitter outside `FailureResponse` that §4.2 missed?
3. Could any `src/config` occurrence be DMS-owned, which would make the scoped substitution wrong?
4. Does step 1.1 change any parser rule other than the accepted type set?
5. Are the regression tests revert-sensitive (§5, step 1.1 mutation reasoning; step 2.1 local mutation)?
6. Is the rollout limitation (old DMS + new CMS) acceptable and documented clearly enough?
7. Is anything in scope unnecessary?

---

## 12. Validation log

*(Filled in at step 4.2.)*
