# DMS-1586 — Use the `urn:ed-fi:management-api:` problem-type prefix in the Configuration Service

**Implementation specification (Configuration Service / CMS, with a narrow DMS compatibility change)**

---

## 1. Document control

| Field | Value |
|---|---|
| Spec file | `reference/design/configuration-service/DMS-1586-management-api-problem-type-prefix.md` |
| Jira ticket | [DMS-1586](https://edfi.atlassian.net/browse/DMS-1586) |
| Branch | `DMS-1586`; [PR #1365](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1365) |
| Base | `a077929f1` (`origin/main` when the branch was created); all line numbers below are against this commit |
| Status | **R4 — implementation complete and reviewed; draft PR open.** §12.1 to §12.6 hold the local validation of the final implementation tree. §12.10 holds the CI runs on the PR head `c7341a0e3`. §12.7 lists what remains before merge |

### 1.1 Revision history

| Rev | Date | Change |
|---|---|---|
| R1 | 2026-10-14 | Initial draft (`29e86dd9e`) |
| R2 | 2026-10-14 | Architect review of R1: removed the editor/IDE prescription from §5; the step 1.1 parser sketch uses a non-nullable `is not string type` guard; step 1.1 adds missing, null and non-integer `status` and missing, null and non-array `errors` cases under both accepted URNs; §9.3 makes the E2E image lifecycle explicit (no `-SkipDockerBuild` after a teardown, plus image verification); section references corrected (§11 challenge list, §12 validation log); Q-1 to Q-7 dispositions recorded; the step 2.1 reflection check is described as a factory-inventory guard |
| R3 | 2026-10-14 | Step 4.2: validation log filled (§12). Four reviewer-ordered corrections: the DMS OAuth test filter is corrected and must execute a nonzero number of tests (step 1.1, step 2.2, V-3); §7.2 is replaced with a byte-safe check; formatting validation (step 2.2, V-5) covers only the changed C# files; V-6 and V-7 are the focused jobs API runs, not the full provider suites |
| R4 | 2026-10-14 | Updated after the PR opened. The status now reflects the completed implementation and review. §2.1 quotes the ticket's description and its Executive Summary scope statement, and notes that the Acceptance Criteria field is empty. §12.10 adds the CI runs on `c7341a0e3`, and §12.7 and §12.9 are reconciled with them. Removed the local worktree path, the review prompt list and the per-step approval instructions. §12 is condensed: TRX run ids, local instance and container names, the binary hash and the image digest are removed. Earlier validation results (trees, commands and counts) are unchanged |

---

## 2. Requirement

### 2.1 Explicit requirement (from the ticket)

Description:

> CMS and ODS Admin API v3 both implement the Ed-Fi Management API v3 surface, but their error responses use different type URN prefixes: cms uses urn:ed-fi:api and ODS Admin API and the published specification uses urn:ed-fi:management-api.
>
> Change CMS to use **urn:ed-fi:management-api:**

Executive Summary (scope statement):

> Align cms error responses type URN prefixes to match management api sepcification

The ticket's Acceptance Criteria field is empty. R-1 restates the description and the scope statement. The ticket names the published specification as the source of `urn:ed-fi:management-api`, but the published YAML spells the prefix `urn:ed-fi:admin-api` (A-1, Q-1). R-1 therefore takes its value from the ticket's explicit instruction and from the ODS Admin API v3 runtime, which agree. Everything in §2.2 beyond R-1 is **derived** (compatibility and regression requirements inferred from the repository), and is labelled as such.

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
- Each step is one commit, with the message given below. Before committing, the diff is reviewed for accidental replacements, unrelated formatting, weakened assertions and out-of-scope files, and the focused checks listed are run.

### Phase 0 — Specification

**Purpose:** agree the scope, sequencing and validation before any change. **Dependencies:** none. **Completion:** spec committed and reviewed; Q-1 to Q-7 resolved (§11).

#### Step 0.1 — Commit this spec

- Edit: create this file only.
- Tests: none.
- Commit: `[DMS-1586] Add implementation spec for the management-api problem-type prefix`
- Checkpoint: design review of R1; its findings were applied as R2 (a spec-only commit).

### Phase 1 — DMS token-proxy compatibility

**Purpose:** make DMS accept both CMS token-limit types before CMS changes, so that no commit and no supported deployment order loses the token-limit 429. **Dependencies:** Phase 0 complete. **Completion:** DMS parser accepts exactly the two types; all OAuth tests green; DMS public types unchanged.

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
  - `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Core.Tests.Unit.OAuthManagerTests"`. The run must execute a nonzero number of tests. The class is `EdFi.DataManagementService.Core.Tests.Unit.OAuthManagerTests` (no `.OAuth` namespace segment), so the R2 filter `OAuth.OAuthManagerTests` matched no tests and still exited 0
  - `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~TokenEndpointModuleTests"`
  - `dotnet csharpier check` on the two files.
- Commit: `[DMS-1586] Accept the management-api token-limit type in the DMS token proxy`
- Checkpoint: reviewer confirms that no rule other than the type set changed (diff limited to lines 162–166, 402–417 and the tests).

### Phase 2 — CMS problem-type prefix

**Purpose:** satisfy R-1 to R-4 and R-8 at the unit and integration level. **Dependencies:** step 1.1 committed. **Completion:** zero `urn:ed-fi:api` occurrences under `src/config`; CMS unit suites green; both jobs integration lanes run; E2E feature expectations updated (executed in Phase 4).

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
  - DMS OAuth tests: the step 1.1 filter `FullyQualifiedName~Core.Tests.Unit.OAuthManagerTests`, with a nonzero executed count
  - PostgreSQL jobs API: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj --filter "FullyQualifiedName~Jobs.JobApiIntegrationTests"`
  - SQL Server jobs API: same filter on `EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj` with `ConnectionStrings__MssqlAdmin` set (§9.2)
  - `dotnet csharpier check` on the C# files the step changes (V-5); files that are already unformatted at the base commit are left untouched
- Commit: `[DMS-1586] Use the urn:ed-fi:management-api problem-type prefix in the Configuration Service`
- Checkpoint: reviewer checks the equivalence output, the empty grep, the single `src/dms` file, and that no `title`, `detail`, `status` or `errors` literal moved.

### Phase 3 — Documentation

**Purpose:** R-9. **Dependencies:** step 2.2 committed, so the docs describe shipped behavior. **Completion:** client-facing note present; superseded decisions annotated; no DMS-type documentation altered.

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

**Purpose:** prove R-1 to R-10 on the final tree. **Dependencies:** Phase 3 complete. **Completion:** §9 suite executed with results recorded; final acceptance diff review presented.

#### Step 4.1 — Full validation run (no commit unless a defect is found)

Run §9 in order and report each command's actual result, with skips and environment blockers listed separately. A defect found here is fixed in a focused corrective commit.

#### Step 4.2 — Record evidence in this spec

Update §1 status, fill §12 (validation log) and the commit list. Commit: `[DMS-1586] Record validation evidence in the spec`.

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

For every `src/config` file changed by step 2.2, the base content with `urn:ed-fi:api` replaced by `urn:ed-fi:management-api` must equal the new content. The check compares raw blob bytes. The R2 version (`git show | Out-String`) decoded the blobs through the console encoding and reported false mismatches on the 12 changed files that contain non-ASCII bytes. Latin-1 decoding maps every byte to exactly one character, so the ASCII-only replacement stays byte-exact:

```powershell
$commit = '8b69d5ab5'
function Get-BlobBytes([string] $spec) {
    $psi = [Diagnostics.ProcessStartInfo]::new('git', "cat-file blob `"$spec`"")
    $psi.WorkingDirectory = (Get-Location).Path
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $ms = [IO.MemoryStream]::new()
    $p.StandardOutput.BaseStream.CopyTo($ms)
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "git cat-file failed for $spec" }
    , $ms.ToArray()
}
$latin1 = [Text.Encoding]::Latin1
git diff --name-only "$commit~1" $commit -- src/config | ForEach-Object {
    $expected = $latin1.GetString((Get-BlobBytes "$commit~1:$_")).Replace('urn:ed-fi:api', 'urn:ed-fi:management-api', [StringComparison]::Ordinal)
    $actual = $latin1.GetString((Get-BlobBytes "${commit}:$_"))
    if ($expected -cne $actual) {
        if (($expected -creplace '\s+', '') -ceq ($actual -creplace '\s+', '')) { "WHITESPACE-ONLY: $_" }
        else { "NOT A PURE PREFIX SUBSTITUTION: $_" }
    }
}
```

Expected output: no `NOT A PURE PREFIX SUBSTITUTION` line. A `WHITESPACE-ONLY` line is acceptable only for a file that CSharpier re-wrapped because the longer prefix pushed a line past the width limit, and the reviewer checks each one. The result at `8b69d5ab5` is in §12.5. The only other changed file is `OAuthManagerTests.cs`, reviewed by hand.

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
| V-3 | `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~Core.Tests.Unit.OAuthManagerTests"` (nonzero executed count required) | DMS OAuth tests only, not the full DMS Core suite: both URNs, near misses, malformed bodies, cross-service fixture |
| V-4 | `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~TokenEndpointModuleTests"` | Legacy-upstream proxy path unchanged |
| V-5 | `$files = git diff --name-only --diff-filter=d a077929f1 HEAD -- '*.cs'; dotnet csharpier check @files` | Formatting of the C# files this ticket changes. Files already unformatted at the base commit are outside this ticket and stay untouched |

### 9.2 Integration (both providers)

| # | Command | Prerequisite |
|---|---|---|
| V-6 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.csproj --filter "FullyQualifiedName~Jobs.JobApiIntegrationTests"` (jobs fixture `Given_jobs_of_two_tenants_and_without_a_tenant` must be reported) | PostgreSQL reachable per the project's `appsettings.json` (`localhost:5432`, `edfi_configurationservice`) or its environment override |
| V-7 | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.csproj --filter "FullyQualifiedName~Jobs.JobApiIntegrationTests"` (same fixture) | `ConnectionStrings__MssqlAdmin` pointing at SQL Server 2025 (container `dms-mssql-integration-2025` on `14333`, or LocalDB, which is acceptable for this non-engine ticket); unset means the fixtures are **ignored**, which is reported as a blocker, not a pass |

The full provider suites are not part of this validation. The only provider-dependent expectations this ticket changes are the two jobs API assertions listed in §4.3, and the focused runs cover both providers.

### 9.3 CMS E2E (Docker; run from the repository root)

Prerequisite: a Release build (`./build-config.ps1 Build -Configuration Release`).

**Image lifecycle.** `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1` stops the `cs-local` stack, deletes its volumes **and deletes the locally built `ed-fi-api-config-local` image**. Every lane therefore runs **without** `-SkipDockerBuild`; `build-config.ps1 E2ETest` then calls `eng/docker-compose/start-local-config.ps1 -r`, which rebuilds the image from the current tree. For each lane, in order:

1. **Teardown.** `pwsh src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1`. Its own `compose down` names only `postgresql.yml`, so after an SQL Server lane also run `./start-local-config.ps1 -d -v -EnvironmentFile <that lane's env file>` from `eng/docker-compose`. Then confirm `docker ps -a --filter "label=com.docker.compose.project=cs-local"` lists nothing.
2. **Run** the lane command below, which rebuilds the image.
3. **Verify the image.** `docker image inspect ed-fi-api-config-local --format "{{.Id}} {{.Created}}"` must show a creation time after the lane started, and `docker inspect ed-fi-api-config-service --format "{{.Image}}"` must show the same image ID. Record the result in §12.

| # | Command | Coverage |
|---|---|---|
| V-8 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained` | PostgreSQL, single-tenant: all 11 affected feature files and the ClaimsManagement step |
| V-9 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.e2e" -E2ETestFilter "TestCategory=MssqlRepresentative"` | SQL Server representative, including `Jobs.feature` not-found |
| V-10 | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.multitenant.e2e" -E2ETestFilter "TestCategory=MssqlMultitenantRepresentative"` | SQL Server multi-tenant, including `Jobs.feature` missing-tenant `bad-request` |
| — | Keycloak legs (`-IdentityProvider keycloak`) | Not run locally; run by CI on the PR head (§12.10) |

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

---

## 12. Validation log

§12.1 to §12.6 and §12.8 record the local validation at step 4.2 (2026-10-14). They are historical: every run was local, on a Windows 11 ARM64 host, against the trees named in each row. Counts come from each run's TRX file. The TRX files are not committed. §12.10 records CI on the PR head. §12.7 and §12.9 give the current merge readiness and acceptance status, drawing on both.

### 12.1 Tree under test

- Step 4.1 ran on `97b0760e4` (the final implementation tree) with a clean working tree.
- `git diff --name-only 8b69d5ab5 97b0760e4 -- src` is empty: steps 3.1 and 3.2 changed documentation only. The unit and integration results recorded at step 2.2 (`8b69d5ab5`) therefore apply to the final tree and were not repeated.

### 12.2 Results

| # | Lane | Command | Tree | Total | Passed | Failed | Not executed |
|---|---|---|---|---|---|---|---|
| V-1 | CMS backend unit (full project) | §9.1 V-1 | `8b69d5ab5` | 2920 | 2920 | 0 | 0 |
| V-2 | CMS frontend unit (full project) | §9.1 V-2 | `8b69d5ab5` | 2667 | 2667 | 0 | 0 |
| V-3 | DMS **OAuth tests** (`OAuthManagerTests` filter; not the full DMS Core suite) | §9.1 V-3 | `8b69d5ab5` | 194 | 194 | 0 | 0 |
| V-4 | DMS `TokenEndpointModuleTests` | §9.1 V-4 | `97b0760e4` | 20 | 20 | 0 | 0 |
| V-5 | `dotnet csharpier check` on the changed C# files | §9.1 V-5 | `97b0760e4` | 33 files | 33 clean | 0 | — |
| V-6 | PostgreSQL jobs API (local PostgreSQL container) | §9.2 V-6 | `8b69d5ab5` | 30 | 30 | 0 | 0 |
| V-7 | SQL Server jobs API (SQL Server LocalDB, `ConnectionStrings__MssqlAdmin` set) | §9.2 V-7 | `8b69d5ab5` | 26 | 26 | 0 | 0 |
| V-8 | CMS E2E, PostgreSQL, self-contained, Docker | §9.3 V-8 | `97b0760e4` | 465 | 285 | 0 | 180 (§12.4) |
| V-9 | CMS E2E, SQL Server representative | §12.3 (deviation) | `97b0760e4` | 34 | 34 | 0 | 0 |
| V-10 | CMS E2E, SQL Server multi-tenant representative | §12.3 (deviation) | `97b0760e4` | 10 | 10 | 0 | 0 |
| — | CMS E2E Keycloak legs (`-IdentityProvider keycloak`) | §9.3 | — | — | — | — | **Not run locally**; run by CI on `c7341a0e3` (§12.10) |

Every filtered run (V-3, V-4, V-6, V-7, V-9, V-10) executed a nonzero number of tests. The first V-3 attempt at step 2.2 used the R2 filter `FullyQualifiedName~OAuth.OAuthManagerTests`, which matched no tests and exited 0. It was rerun with the corrected filter, and only that rerun is recorded.

**V-8 image lifecycle (§9.3).** Teardown exited 0. Afterwards `docker ps -a --filter "label=com.docker.compose.project=cs-local"` listed nothing and no `ed-fi-api-config-local` image existed. The lane ran `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained` without `-SkipDockerBuild`. The rebuilt `ed-fi-api-config-local` image was created after the lane started, and the `ed-fi-api-config-service` container ran that image.

### 12.3 Deviation: SQL Server E2E lanes V-9 and V-10

The validation host is ARM64 and cannot run the amd64 SQL Server container image, so V-9 and V-10 did not run `build-config.ps1 E2ETest` with the SQL Server environment files. Each lane instead ran as follows:

- **Database:** SQL Server 2025 LocalDB. The `edfi_configurationservice` database was dropped and recreated at the start of each lane.
- **Configuration Service:** a host process started from the Release build of `EdFi.DmsConfigurationService.Frontend.AspNetCore.dll`, built after the `97b0760e4` commit. It was configured from the lane's environment file (`./.env.config.mssql.e2e` or `./.env.config.mssql.multitenant.e2e`) the way `local-config.yml` configures the container, with `AppSettings__MultiTenancy` false for V-9 and true for V-10.
- **Identity setup:** the OpenIddict signing key was inserted through `OpenIddict-Crypto.psm1`. `setup-openiddict.ps1 -InsertData` ran unchanged, with its `docker exec … sqlcmd` call routed to the host `sqlcmd`. The three expected clients were present before the tests started.
- **Tests:** `dotnet test` on the Release build of the E2E project, filtered by `TestCategory=MssqlRepresentative` (V-9) and `TestCategory=MssqlMultitenantRepresentative` (V-10).

These lanes are **host-process CMS testing against SQL Server 2025**. They do not validate the SQL Server Docker deployment (image build, compose wiring or container startup). The reviewer accepted them for this ticket because the change is a database-independent string prefix. **Cleanup limitation:** each lane started on a freshly created database, but cleanup between features within a lane was not verified. CI later ran the SQL Server Docker deployment path on `c7341a0e3` (§12.10).

### 12.4 V-8 tests not executed (180)

| Count | Tests | Reason | Kind |
|---|---|---|---|
| 154 | `Keycloak.*` characterization fixtures (25 classes) | `OneTimeSetUp` ignores them unless `CONFIGURATION_SERVICE_IDENTITY_PROVIDER=keycloak` | Intentional provider skip |
| 3 | `Revocation.feature` scenarios 08–10 (`@KeycloakOnly`) | `BeforeScenario("KeycloakOnly")` ignores them under the self-contained provider | Intentional provider skip |
| 10 | `Tenants.feature` scenarios 01–08 and `Jobs.feature` scenarios 05–06 (`@MultitenantOnly`) | `BeforeScenario("MultitenantOnly")` ignores them on a single-tenant stack. All 10 also carry `@MssqlMultitenantRepresentative`, the V-10 filter, which ran 10 tests | Intentional configuration skip |
| 9 | `SecretResolution.*` (2 classes) | They run only under `Invoke-SecretResolutionE2E.ps1`, which deploys the fixture plugin | Intentional harness skip |
| 2 | `Management.Given_a_suite_run_against_a_selected_deployment` | The preflight variables are set only by an isolated run | Intentional harness skip |
| 2 | `ApiClients.feature`: `_09VerifyUpdatedApiClientHasCorrectValues`, `_15VerifyDeletedApiClientNoLongerExists` | **No matching step definition** for `Given a PUT request is made to {string} with` and `Given a DELETE request is made to {string}`. Reqnroll reports the scenario as not executed rather than failed | **Existing coverage gap**, not a skip |

The two undefined-step scenarios are not provider or configuration skips. They cannot run in any lane until the bindings exist, and the lane outcome stays green because of how they are reported. DMS-1586 did not introduce them: its `ApiClients.feature` edits are on other lines, and it added or removed no step bindings. They are recorded here for a follow-up and are not fixed in this ticket.

### 12.5 Static acceptance checks (final tree `97b0760e4`)

| Check | Result |
|---|---|
| `git grep -n "urn:ed-fi:api" -- src/config` | No output |
| `git diff a077929f1 --stat -- src/dms` | `OAuthManager.cs` and `OAuthManagerTests.cs` only |
| `git diff a077929f1 --stat -- '*.sql' '…/SchemaHashConstants.cs'` | No output |
| §7.2 byte-safe check at `8b69d5ab5` | 42 files: 36 byte-exact; 6 whitespace-only CSharpier re-wraps (`FailureResponseTests.cs`, `FailureResultsTests.cs`, `DataStoreDerivativeModuleTests.cs`, `TenantModuleTests.cs`, and `Jobs/JobApiIntegrationTests.cs` for both providers); 0 mismatches. 12 of the 42 files contain non-ASCII bytes |
| `git diff a077929f1 97b0760e4 --stat` | 51 files, 1255 insertions, 326 deletions: 42 under `src/config`, 2 under `src/dms`, 3 under `docs`, 4 under `reference/design` (this spec, the DMS-1218 spec, the jobs spec and the provenance note). This matches §4.3 |
| `git diff --name-only 8b69d5ab5 97b0760e4 -- src` | No output |

### 12.6 Deviations from the R2 plan (all accepted at review)

1. V-9 and V-10 are host-process CMS lanes on LocalDB, not SQL Server Docker lanes (§12.3).
2. V-3 is the OAuth-filtered run (194 tests), not the full DMS Core project. The R2 filter matched no tests. The corrected filter and the nonzero-count rule are now in step 1.1, step 2.2 and §9.1.
3. The encoding-sensitive §7.2 text check was replaced with the byte-safe check.
4. CSharpier re-wrapped 6 test files in step 2.2 after the longer prefix. The changes are whitespace-only, as the §7.2 check shows.
5. V-5 checks only the changed C# files. 8 untouched `src/config` files already fail `csharpier check` at the base commit and were left as they are.
6. V-6 and V-7 are the focused jobs API runs. The full provider suites were not run.
7. V-1, V-2, V-3, V-6 and V-7 were recorded at step 2.2 and reused, because no `src` file changed afterwards (§12.1).

### 12.7 Merge readiness

Current as of the PR head `c7341a0e3` and the commit that adds R4. That commit changes only this document.

- **Covered by CI on `c7341a0e3` (§12.10):** the CMS E2E Keycloak legs on PostgreSQL and SQL Server, and the SQL Server Docker deployment path (representative and multi-tenant lanes under both identity providers). Neither is outstanding.
- **Remaining before merge:**
  - PR #1365 is a draft. Every job in its `pull_request` runs was skipped, so the CI evidence comes from manual (`workflow_dispatch`) runs. The PR-event checks must run and pass on the head that merges.
  - The PR needs review approval.
  - The DMS manual run has one failed job, the CDC connector template smoke tests. The evidence points to the CI test harness, not to this change, but the failure has not been reproduced on `main` (§12.10). The run's DMS CI Gate job passed.
- **Out of scope, recorded for follow-up:**
  - The two `ApiClients.feature` undefined-step scenarios (§12.4).
  - The stale `urn:ed-fi:admin-api` text in the published specification (Q-1).

### 12.8 Commit list

| # | Step | Commit | Subject |
|---|---|---|---|
| 1 | 0.1 | `29e86dd9e` | `[DMS-1586] Add implementation spec for the management-api problem-type prefix` |
| 2 | 0.1 (R2) | `84f347504` | `[DMS-1586] Revise the implementation spec after architect review (R2)` |
| 3 | 1.1 | `6821c3b82` | `[DMS-1586] Accept the management-api token-limit type in the DMS token proxy` |
| 4 | 2.1 | `30cdd0592` | `[DMS-1586] Pin every Configuration Service problem type in a contract fixture` |
| 5 | 2.2 | `8b69d5ab5` | `[DMS-1586] Use the urn:ed-fi:management-api problem-type prefix in the Configuration Service` |
| 6 | 3.1 | `53e9ff236` | `[DMS-1586] Document the Configuration Service problem-type prefix change` |
| 7 | 3.2 | `97b0760e4` | `[DMS-1586] Mark earlier Configuration Service prefix decisions as superseded` |
| 8 | 4.2 | `c7341a0e3` | `[DMS-1586] Record validation evidence in the spec` |
| 9 | R4 | the commit adding R4 | `[DMS-1586] Update the spec for the open PR and its CI results` |

### 12.9 Acceptance evidence

| Req | Evidence | Result |
|---|---|---|
| R-1 | V-1 `Given_each_failure_factory` exact types; V-2 module and middleware expectations; V-8 to V-10 E2E features | Met |
| R-2 | §7.2 byte-safe check with 0 mismatches; unchanged `title`, `status`, `detail` and `errors` assertions pass in V-1 and V-2 | Met |
| R-3 | `about:blank` assertions in V-1 and V-2 pass unchanged | Met |
| R-4 | Old-prefix grep under `src/config` is empty; the V-1 reflection guard passes | Met |
| R-5 | V-3 parameterized token-limit fixtures under both types, plus the cross-service fixture with `It_is_fed_the_management_api_type`; V-4 legacy-upstream proxy path | Met |
| R-6 | V-3 deviating-body, near-miss, shape and secret-embedding fixtures under both types | Met |
| R-7 | The `src/dms` scope guard lists two files; V-3 and V-4 DMS-type assertions unchanged | Met |
| R-8 | Local: V-1 to V-10 green with 0 failures (§12.2). CI on `c7341a0e3`: Configuration Service run 38670012218 green, including the Keycloak and SQL Server Docker E2E legs (§12.10) | Met |
| R-9 | `53e9ff236` changelog and docs; `97b0760e4` superseded annotations | Met |
| R-10 | SQL and `SchemaHashConstants` guard empty | Met |

### 12.10 CI on the PR head `c7341a0e3`

Both runs were started manually (`workflow_dispatch`) on branch `DMS-1586`, and both report head SHA `c7341a0e3ff92ddf8e29bd9715812d5e8ba5cfe4`. The PR's own `pull_request` runs (38670007326 Configuration Service, 38670007097 DMS) skipped every job. Test counts come from each job's `dotnet test` console summary. They are not directly comparable with the TRX counts in §12.2.

**Configuration Service, [run 38670012218](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38670012218): success, 17 of 17 jobs.**

| Job | Identity provider | Database and deployment | Result |
|---|---|---|---|
| Run Unit Tests | — | — | Success |
| Run Integration Tests | — | PostgreSQL; SQL Server 2025 (`mcr.microsoft.com/mssql/server:2025-latest` service container) | Success: PostgreSQL 1106 passed, SQL Server 1133 passed, 0 failed |
| Run E2E Tests (self-contained) | self-contained | PostgreSQL, Docker | Success: 285 passed, 178 skipped, 0 failed (463) |
| Run E2E Tests (keycloak) | Keycloak | PostgreSQL, Docker | Success: 431 passed, 32 skipped, 0 failed (463) |
| Run E2E Tests - MSSQL (self-contained) | self-contained | SQL Server, Docker (`.env.config.mssql.e2e`, `MssqlRepresentative`) | Success: 34 passed, 0 failed |
| Run E2E Tests - MSSQL (keycloak) | Keycloak | SQL Server, Docker (`.env.config.mssql.e2e`, `MssqlRepresentative`) | Success: 34 passed, 0 failed |
| Run E2E Tests - MSSQL Multitenant (self-contained) | self-contained | SQL Server, Docker (`.env.config.mssql.multitenant.e2e`, `MssqlMultitenantRepresentative`) | Success: 10 passed, 0 failed |
| Run E2E Tests - MSSQL Multitenant (keycloak) | Keycloak | SQL Server, Docker (`.env.config.mssql.multitenant.e2e`, `MssqlMultitenantRepresentative`) | Success: 10 passed, 0 failed |
| Fresh-build detection, Fresh Rebuild Config Docker Image, lock-file checks, secrets contract package, BIDI scans, event upload, Config CI Gate | — | — | Success |

The SQL Server E2E jobs start the compose stack with the `dms-mssql` container, so they cover the SQL Server Docker deployment path that the local V-9 and V-10 lanes did not (§12.3). The skipped tests in the CI E2E jobs were not itemized.

**DMS, [run 38670021353](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38670021353): failure, caused by one job. DMS CI Gate: success.**

Every other job passed. They include the unit tests, PostgreSQL DMS E2E shards 1 to 4, SQL Server DMS E2E (representative) under both identity providers, the Instance Management E2E suites, and the backend, API and CDC integration suites.

The failed job is **Run Backend CDC Connector Template Smoke Tests**: 4 of 8 tests failed, all of them cases of `It_publishes_a_real_tracking_restamp_after_projector_drain`. Each could not start `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/bin/Debug/net10.0/dms-document-cache` ("No such file or directory"). Evidence about the cause:

- The job runs only on `workflow_dispatch`. It executes the prebuilt integration-test-assemblies artifact, and the workflow that builds that artifact does not reference the DocumentCacheAdmin CLI build output.
- DMS-1586 changes no workflow, CLI or backend file: `git diff a077929f1 c7341a0e3 -- .github src/dms/clis src/dms/backend` is empty.
- In [run 38666867786](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/38666867786) on `DMS-1403` (`f14ac1f2c`), the same four tests failed with the same error. That branch changes CDC tests and the DMS workflow, so it corroborates the failure but is not an independent baseline.
- Among the 40 most recent DMS workflow runs, this job ran only in the two manual runs above. No run on `main` was found.

The failure is attributed to the CI test harness, not to DMS-1586. It has not been reproduced on `main`.
