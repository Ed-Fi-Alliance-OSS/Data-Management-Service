# DMS-1606 OAuth Token Errors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved OAuth JSON error contract across DMS token endpoints, with safe diagnostics and unit plus real-stack coverage of all 17 story rows.

**Architecture:** Keep classification in the existing OAuth manager and serialization in one token-local Core helper returning `HttpResponseMessage`. A frontend writer relays only approved error headers; endpoint metadata selects token formatting for limiter and writable exception paths. A closed, test-only forwarding proxy supplies deterministic provider failures in the existing Docker E2E stack.

**Tech Stack:** .NET 10 / C#, System.Text.Json, ASP.NET Core, NUnit, FluentAssertions, FakeItEasy, Reqnroll/Playwright, PowerShell 7, Docker Compose; existing PostgreSQL/SQL Server and self-contained/Keycloak lanes.

**Spec:** [Approved DMS-1606 specification](../../../reference/design/DMS-1606-oauth-token-errors.md), originally committed as `a37fc047376613433cb665d87c621aa5d8fd7620`; written-artifact approval received 2026-10-20. Read the spec with this plan; its matrix and exclusions are normative.

## Global Constraints

- “Work stays in this repository and branch.” Branch `DMS-1606`; inspected product baseline `334ce4443a172d8b6ef145c5367d68068f52c351`, current documentation HEAD `a37fc047376613433cb665d87c621aa5d8fd7620`. Recheck state before execution; preserve unrelated user changes.
- “Every in-scope error has HTTP media type `application/json` (UTF-8 charset allowed), a root `error` string and a root nonempty `error_description` string.” Emit exactly these two body members.
- “Every token 401 has exactly the DMS-owned challenge `Basic realm="EdFi.DataManagementService"`.” Other statuses have no challenge. Correlation is `X-Correlation-ID = Uri.EscapeDataString(normalizedTraceId.Value)`; decoding once recovers the log value.
- “Only grant_type is sent in the upstream form; successful 200 content remains passthrough.” Preserve original caller authentication context, validation precedence, credential encoding and the existing lenient Basic recognition.
- “Do not add a new response-description size policy under this story.” Provider descriptions must satisfy the decoded-character rule in spec §5; wire validation must not change scalar log summaries.
- “Keep the shared `FailureResponse` API and all non-token consumers.” Preserve exclusions, current logging levels and selected-summary limits; no CMS, schema, new grants, global formatting framework or unrelated cleanup. “`RelationalMappingVersion` remains v3.”
- Follow root `AGENTS.md`: modern .NET 10, non-nullable working variables, `is null`/`is not null`, System.Text.Json only, CSharpier, sanitized logging of `.Value`, NUnit Given_/SetUp/It_ fixtures and deterministic tests. Preserve existing nullable ingress contracts rather than changing `IOAuthManager`.
- **This is a plan awaiting review. No implementation, build, local test or Docker action is authorized in this planning turn.** Commands below are future execution instructions. Obtain plan approval before implementation and create the requested continuation handoff after that approval.

## Review Focus

1. Caller body authentication becomes Basic upstream: provider 400 invalid_client must still remain 400, while caller-header authentication becomes 401. Pin both through the actual HTTP relay in Task 3.
2. An escaped quote, backslash, control or non-ASCII character is still invalid after JSON decoding: use a fixed description without changing a recognized provider code. Pin decoded boundary characters and non-string values in Task 2.
3. A literal percent or Unicode correlation ID must decode exactly once to the cached normalized log ID, including input-header ingestion being disabled. Pin actual frontend delivery and log equality in Tasks 3–4.
4. A selected endpoint can fail before its handler or after response start: writable failures get OAuth JSON; partial/disconnected responses and non-token controls retain existing handling. Pin metadata, limiter, 413, 500 and response-write failure branches in Task 4.
5. Proxy fixtures can accidentally intercept setup or leave DMS pointed at a deleted container: normal clients must still issue usable tokens, and cleanup must restore settings on success and failure. Pin the launcher lifecycle and live forwarding in Task 6.

## File map and execution order

Paths below are repository-relative. Existing production files remain in place; do not split `OAuthManager` or build shared DMS/CMS infrastructure.

| Task | Create | Modify / existing tests | Responsibility |
|---|---|---|---|
| 1 | `src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthErrorResponse.cs`; `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth/OAuthErrorResponseTests.cs` | `src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthManager.cs`; `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth/OAuthManagerTests.cs` | Wire factory and local request rows 1–6, 8–10. |
| 2 | None | Same manager/tests | Provider rows 12–17, disclosure safeguards and dual-URN dependency. |
| 3 | `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/TokenEndpointMetadata.cs`; `Infrastructure/TokenErrorResponseWriter.cs` in that same project | `Modules/TokenEndpointModule.cs`, `Program.cs`; frontend unit `Modules/TokenEndpointModuleTests.cs`, `IdentityCorsExposeHeadersTests.cs` | Parsing rows 7/11, metadata, delivery, CORS and route compatibility. |
| 4 | None | Frontend `Infrastructure/WebApplicationBuilderExtensions.cs`, `Infrastructure/LoggingMiddleware.cs`; frontend unit `RateLimitTests.cs`, `Infrastructure/LoggingMiddlewareTests.cs`, `MethodNotAllowedEndpointTests.cs` | Selected-endpoint limiter/exception paths and exclusions. |
| 5 | None | `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Security/TokenEndpoint.feature`; `StepDefinitions/TokenEndpointStepDefinitions.cs` in that project | Ordinary-provider contract cases and reusable precise assertions. |
| 6 | `eng/docker-compose/tests/oauth-token-contract/TokenProxy/{TokenProxy.csproj,Program.cs,Dockerfile}`; `oauth-token-proxy.yml`, `Invoke-TokenContractE2E.ps1`, `TokenContractE2E.Tests.ps1` in the parent `oauth-token-contract` directory | Same E2E feature/steps; E2E `README.md`; `.github/workflows/on-dms-pullrequest.yml` | Closed provider fixtures, forwarding, lifecycle and dedicated E2E invocation. |
| 7 | `reference/adr-oauth-token-errors-DMS-1606.md` | `reference/adr-oauth-token-client-credentials-DMS-1604.md`, `reference/adr-oauth-upstream-error-disclosure.md`, `docs/changelog/8.1.0.md` | Migration, supersession and final verification evidence. |

Full frontend unit project root: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`. All `Infrastructure/...` and `Modules/...` entries in Tasks 3–4 refer to the corresponding frontend production/unit roots above. Task 6 is bounded verification wiring for the approved E2E requirement, not a production service or an additional database integration suite.

Execute 1 → 2 → 3 → 4 → 5 → 6 → 7. For every task, add the tests first, run the stated focused command and observe the intended assertion failure, implement, rerun, then commit only that task's files. An environment/setup failure or zero matched tests is not a red/green result. Existing test helpers may be extended within their files; no shared test package is needed.

## Task 1: Token wire factory and local request classification

**Files:** Core production/test files listed for Task 1 in the file map.

**Interfaces:**
- Consumes unchanged `IOAuthManager.GetAccessTokenAsync(IHttpClientWrapper httpClient, string grantType, string? authHeaderString, TokenRequestCredentials bodyCredentials, string upstreamUri, TraceId traceId) -> Task<HttpResponseMessage>`.
- Produces public static `OAuthErrorResponse.Create(HttpStatusCode statusCode, string error, string description, TraceId traceId) -> HttpResponseMessage`, `CorrelationHeaderName = "X-Correlation-ID"`, and `BasicChallenge = "Basic realm=\"EdFi.DataManagementService\""` in the existing Core.OAuth namespace. This factory receives already classified errors and safe descriptions; it does not parse provider bodies or depend on ASP.NET.
- Service-owned description constants live in this helper and are reused by manager/frontend. Keep existing safe local descriptions verbatim. New `MissingGrantDescription = "The grant_type request parameter must be supplied"`; use the existing missing-credentials text for both missing and partial pairs. Additional texts are fixed in later tasks.

- [ ] **1. Add `Given_An_OAuth_Error_Response` factory tests and local manager fixtures.** In a private async assertion helper in each test file, parse with System.Text.Json and pin actual wire values, not factory constants alone:

```csharp
body.EnumerateObject().Select(p => p.Name).Should()
    .BeEquivalentTo("error", "error_description");
body.GetProperty("error").GetString().Should().Be(expectedError);
body.GetProperty("error_description").GetString().Should().Be(expectedDescription);
response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
response.Headers.GetValues("X-Correlation-ID").Should().ContainSingle()
    .Which.Should().Be(Uri.EscapeDataString(expectedNormalizedTraceId));
```

  Assert status and exactly one challenge equal to the literal realm on 401; assert challenge absence otherwise. Parameterize statuses 400/401/413/429/500/502/503 to catch serialization or header drift. Manager fixtures cover rows 1–6, 8–10 with exact expected status/code pairs from spec §3: no pair → 401 invalid_client; partial → 400 invalid_request; non-Basic → 401 invalid_client; empty/malformed/mixed/cap/missing grant → 400 invalid_request; present unsupported grant → 400 unsupported_grant_type. Assert no outbound call for local failures, plus successful upstream header/body preservation.

  Add the spec's absent/null/empty/blank variants, both partial sides, 2048/2049 for each credential, case-insensitive accepted grant, whitespace unsupported grant and Basic substring limitation. Competing-failure fixtures pin credential-before-grant, mixed/partial-before-cap, cap-before-grant, grant-before-header-add. For example, malformed `Basicxyz:secret` plus `password` returns unsupported_grant_type; with client_credentials it returns invalid_request.
- [ ] **2. Run red:** `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter 'FullyQualifiedName~OAuthErrorResponseTests|FullyQualifiedName~OAuthManagerTests'`. Expect missing factory and/or the old local envelope/classification assertions to fail for the stated reasons.
- [ ] **3. Implement the factory and local manager branches.** Serialize exactly two fixed wire names with System.Text.Json/StringContent UTF-8 application/json, attach encoded correlation and status-derived Basic challenge. Split the missing-pair branch by nonblank normalized values; add `string.IsNullOrEmpty(grantType)` before unsupported-grant comparison. Retain check order and header-add FormatException handling, withholding its message. Migrate only the local branches now; keep existing provider behavior until Task 2. Update affected old tests, without broadly replacing unrelated response assertions.
- [ ] **4. Run green:** repeat Step 2; require all selected tests passing, including current provider regressions. Format touched C# with `dotnet csharpier format <each touched file>`.
- [ ] **5. Commit** the four Task 1 paths explicitly: `feat(DMS-1606): format local token failures as OAuth errors`.

## Task 2: Provider classification and safe fallback diagnostics

**Files:** `OAuthManager.cs`, `OAuthManagerTests.cs`, and the Task 1 factory if adding its fixed description constants.

**Interfaces:** Consumes Task 1 factory unchanged. Keep existing guarded `JsonObjectOrNull`, selected-summary functions, token-limit parser and outward `IOAuthManager` signature. Produces private `IsUsableOAuthDescription(string description) -> bool` and `IsRecognizedTokenError(string error) -> bool` inside `OAuthManager`; retrieve actual JSON strings using `JsonValue.TryGetValue<string>`, independently of `ScalarValueOrNull` used by logging. No new public parser abstraction.

- [ ] **1. Extend manager fixtures for rows 12–17 and exact selected logs.** Cases and their literal assertions:

```csharp
// Given_An_Upstream_401_With_An_Unusable_Body (including "", HTML, [], "text", 7, true, null)
_response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
body["error"]!.GetValue<string>().Should().Be("invalid_client");
body["error_description"]!.GetValue<string>().Should()
    .Be("The upstream identity service rejected the request credentials.");
// Given_An_Upstream_400_Invalid_Client: callerHeader=true/false
_response.StatusCode.Should().Be(callerHeader ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest);
body["error"]!.GetValue<string>().Should().Be("invalid_client");
```

  Use the Task 1 full wire assertion on each case. All upstream 401 object codes, including unknown/non-string values, yield invalid_client. For provider 400, test each exact lowercase code `invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`, `unsupported_grant_type`, `invalid_scope`. Except the caller-header invalid_client exception, preserve 400/code. Reject unknown, uppercase, padded, non-string, missing, malformed/non-object and DMS-extension codes to 502 server_error.

  For descriptions, test accepted decoded boundaries U+0020/U+0021/U+0023/U+005B/U+005D/U+007E and rejected U+001F/U+0022/U+005C/U+007F/non-ASCII, including their JSON escape representations, missing/null/empty/object/array/number/boolean. An unusable description keeps the recognized classification and uses fixed text. Assert a long otherwise valid ASCII description is preserved; add no size cap.

  Pin fallback Information events with exact normalized `.Value`, including malformed 401 bodies with no fields and newly handled 400 description fallback. Retain sanitize-before-truncate 2048-character per-summary bounds, 20 other names, withheld URI/nested sentinel values, and scalar logging behavior. Keep generic-429 Warning, 409 trace-only Information, existing bounded 502 Warning and transport Error tests. Do not assert an all-level no-body policy.

  For row 15 feed both exact URNs `urn:ed-fi:api:security:authentication:too-many-tokens` and `urn:ed-fi:management-api:security:authentication:too-many-tokens`, including actual CMS formatter output. Assert limit 7 gives exactly `Too many access tokens have been requested (limit is 7). Access tokens should be reused until they expire.` in error_description. Near-miss URNs, wrong status, wrong errors shape, hostile text, nonpositive/overflow limits get generic 429. Row 16 yields 503 temporarily_unavailable with no Retry-After/provider body. Row 17 includes other status and a deterministic fake-send exception; their safe bodies are equal for the same trace.
- [ ] **2. Run red:** repeat the Core command from Task 1. Require failing new assertions for malformed 401, provider 400, infrastructure codes and/or the missing management URN, not an unrelated exception.
- [ ] **3. Implement classification and migrate remaining manager errors.** Capture `bool callerUsedAuthorization = authHeaderString is not null` before constructing upstream Basic. Status 401 always selects invalid_client, using a validated description or the existing fixed credential rejection. Status 400 uses the six-code ordinal allowlist, caller-context exception and safe 502 fallback. Reuse the guarded object parser so JSON shape/parse failures cannot reclassify 401 as transport failures. Validate decoded description characters with the exact ranges above.

  Add fixed factory texts: `ProviderRejectedDescription = "The upstream identity service rejected the token request."` for other recognized-400 fallbacks; invalid_client fallback uses the existing credential rejection. `RateLimitDescription = "The number of allowed requests has been exceeded. Retry the request later."`; `TemporarilyUnavailableDescription = "The service is temporarily unable to handle the request. Retry the request later."`; `GatewayDescription = "The upstream identity service did not return a usable response. Contact your system administrator with the X-Correlation-ID response header, decoded once using UTF-8 percent decoding, which identifies the corresponding server log entry."`.

  Keep canonical parsing/reconstruction and the narrow DMS-1586 dual-URN fix. At execution, compare current refs: reuse it if integrated; otherwise apply that inspected narrow parser compatibility change with its regression tests, without merging unrelated changes. Leave URI/nested/body disclosure and logging levels within spec §5. Rename only touched stale templates/comments as needed for 400 summary reuse; no new logging subsystem.
- [ ] **4. Check usages** with `rg -n 'GenerateProblemDetailResponse|FailureResponse|ScalarValueOrNull|TooManyTokensType' src/dms/core/EdFi.DataManagementService.Core/OAuth src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth`. Remove only the manager's now-unused private problem formatter/import/constants; keep shared FailureResponse and summary/parser helpers. Correct the stale 5xx→503 and problem-correlation comments.
- [ ] **5. Run green:** Core command from Task 1, then CSharpier on touched files. Expected all existing and new Core OAuth tests pass; no outbound secrets in new responses or selected-summary logs.
- [ ] **6. Commit** explicit Task 2 paths: `feat(DMS-1606): classify provider token errors with safe fallbacks`.

## Task 3: Parsing, response delivery and token endpoint metadata

**Files:** Frontend Task 3 files from the map, plus Task 1 factory for new fixed text.

**Interfaces:**
- `internal sealed class TokenEndpointMetadata` with singleton `public static readonly TokenEndpointMetadata Instance = new();`; attach with `.WithMetadata(TokenEndpointMetadata.Instance)` to both supported POST mappings, at root and qualified routes. Consumers use `GetEndpoint()?.Metadata.GetMetadata<TokenEndpointMetadata>() is not null`.
- `internal static TokenErrorResponseWriter.WriteAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken = default) -> Task`. It copies status/media/body and only X-Correlation-ID / WWW-Authenticate on manager-owned error responses. Keep existing successful-response forwarding unchanged; arbitrary upstream headers never become an allowlist source.
- Extend existing private real-manager test seam to `SendThroughRealManager(HttpRequestMessage request, Func<HttpRequestMessage, Task<HttpResponseMessage>> upstreamResponse)` while keeping its one-argument success overload. The recording handler records the same calls, then invokes the supplied delegate; no production seam changes.

- [ ] **1. Add frontend fixtures for direct rows 7/11 and delivery regressions.** Test duplicate form parameters (case variants, empty duplicates, duplicate plus Bearer) before manager invocation, and malformed/mistyped/empty/null JSON with manager not called in Production and Development. All failures assert the Task 1 exact shape, status/code/description/media, encoded ID and challenge rules.

  Extend the real-manager seam and send provider 400 invalid_client with caller Basic versus complete body credentials; assert actual HTTP 401 plus exactly `Basic realm="EdFi.DataManagementService"`, versus 400 with no challenge. Also test manager/provider 401 and 502, hostile upstream challenge/correlation/extra headers, unchanged 200 body/media and existing success header behavior. Run both form and JSON and configured root/qualified/multitenant mappings; verify metadata is present only on selected supported POST endpoints.

  Through actual ingestion/log capture, cover correlation `a%2Fb+c/d`, Unicode/emoji and normalization/removal/truncation at surrogate boundaries. Assert `Uri.UnescapeDataString(responseHeader) == loggedTraceId`, plus exact encoding where expected; literal `%2F` must survive one decode as `%2F`, not `/`. With input ingestion disabled or absent, output decodes to the generated/cached server ID. Reuse existing ingestion rather than inventing TraceIds or changing normalization.

  Add allowed-Origin CORS assertion that exposed headers contain both literal names with identity feature enabled and disabled; update existing tests that currently expect only X-EdFi-Warning. Disallowed/no Origin retains policy behavior, Location remains feature-gated and existing warning exposure remains.
- [ ] **2. Run red:** `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter 'FullyQualifiedName~TokenEndpointModuleTests|FullyQualifiedName~IdentityCorsExposeHeadersTests'`. Expect direct old envelopes/empty body and missing challenge/correlation/CORS assertions to fail.
- [ ] **3. Implement metadata, writer and parser changes.** Route both direct duplicate and JSON-null/parse failure responses through `OAuthErrorResponse.Create` and the writer. Add `MalformedJsonDescription = "The token request body must be a valid JSON object with string-valued fields"`; retain existing duplicate text. Keep manual JsonException handling and debug trace-only message, with no parse exception/body logging. `GenerateToken` relays manager errors through the writer; successful response handling remains as before. Dispose locally created HttpResponseMessages after writing. Add the two fixed names to the existing exposed-header list in Program; preserve origins and middleware order.
- [ ] **4. Run green:** repeat Step 2 and format touched C#. Require actual delivered headers, exact bodies, success and CORS controls passing.
- [ ] **5. Commit** explicit Task 3 paths: `feat(DMS-1606): deliver OAuth token errors and diagnostic headers`.

## Task 4: Selected-endpoint limiter and writable exception failures

**Files:** Frontend Task 4 files from the map; factory for fixed descriptions. Reuse Task 3 metadata/writer.

**Interfaces:** Keep `WriteRateLimitRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken) -> ValueTask` and `LoggingMiddleware.Invoke(HttpContext context, ILogger<LoggingMiddleware> logger) -> Task` unchanged. Token selection is metadata-only; TraceId comes from the existing cached ingestion/extraction.

- [ ] **1. Add tests** `Given_A_Selected_Token_Endpoint_Rejected_By_The_Limiter`, `Given_A_Selected_Token_Endpoint_Throwing_Before_Response_Start`, and corresponding no-metadata controls, in the existing limiter/logging test files. Use a synthetic selected Endpoint carrying Task 3 metadata for isolated middleware tests; use real routing in HTTP exclusion checks.

```csharp
// Limiter with RetryAfter = TimeSpan.FromMilliseconds(1100)
context.Response.StatusCode.Should().Be(429);
context.Response.Headers.RetryAfter.ToString().Should().Be("2");
body["error"]!.GetValue<string>().Should().Be("temporarily_unavailable");
// Writable unexpected exception: invoke completes after logging and writing.
await act.Should().NotThrowAsync();
context.Response.StatusCode.Should().Be(500);
body["error"]!.GetValue<string>().Should().Be("server_error");
```

  Pin limiter no-metadata/no-RetryAfter cases and unchanged non-token problem shape. For token 413 assert invalid_request, exact fixed description and Information completion status; for token 500 assert server_error, failure event with original exception/normalized ID and no second normal completion event. Compare encoded response ID with the log scope/event for both. Use existing fake response-start feature and cancellation signals, not timer delays, to prove started responses are not rewritten, canceled requests propagate, and failed response writes retain existing logged/wrapped failure handling. Preserve non-token 500 body and wrapping behavior.

  Through HTTP assert text/plain POST /oauth/token stays 404, non-POST stays 405/Allow, lookalike token paths remain ordinary routes, and SecurityHeadersMiddleware still supplies no-store on token errors. Metadata absent due to startup configuration rejection remains outside conversion. No additional CORS availability is expected on limiter/outer errors occurring before CORS runs.
- [ ] **2. Run red:** `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj --filter 'FullyQualifiedName~RateLimitTests|FullyQualifiedName~LoggingMiddlewareTests|FullyQualifiedName~MethodNotAllowedEndpointTests'`. Expect token format and handled-500 assertions to fail; existing non-token controls pass.
- [ ] **3. Implement narrow metadata branches.** Limiter keeps its existing Retry-After calculation and uses the factory/writer only for token metadata. LoggingMiddleware writes OAuth 413/500 only while not started and not request-aborted. Add `PayloadTooLargeDescription = "The token request body exceeds the allowed size."` and `ServerErrorDescription = "The server encountered an unexpected condition that prevented it from fulfilling the request."` to the factory. Return after successfully writing/logging the token 500; preserve current cancellation, started/write-failure and all non-token paths. Do not catch and retry partial writes or buffer responses. On 413 keep the logged status equal to what was actually written.
- [ ] **4. Run green:** repeat Step 2, then the whole frontend unit project (same command without `--filter`) and format touched C#. Existing ingress/logging/security/CORS tests must remain green; no fixed-delay test additions.
- [ ] **5. Commit** explicit Task 4 paths: `feat(DMS-1606): format selected token boundary failures`.

## Task 5: Ordinary-provider E2E contract and precise assertions

**Files:** E2E feature and step definitions from the map.

**Interfaces:** Retain existing raw SendTokenRequest behavior and provisioned-client substitutions. Add private async `AssertTokenError(int status, string code, string description) -> Task` over `_apiResponse`, exposed as Reqnroll `Then the token error is {int} {string} with description {string}`. Add a separate correlation assertion using response headers and DMS request-log capture through the existing container name/settings, without printing credentials. Store and compare complete OAuth bodies; replace the old token-only “apart from correlationId” assertion with exact equality, since correlation has moved out of the body. Check usages before removing that old binding/helper.

- [ ] **1. Write feature cases tagged `@DMS-1606` plus existing `@e2e-ci-shard-1` for ordinary-provider rows 1–11 and genuine rejected-credential 401.** Update old DMS-1604 expectations/rule wording that claim missing/partial/null-grant parity. Keep all successful token-to-resource scenarios. Exercise both request formats where relevant, 2049-unit body fields using a bounded synthetic placeholder rather than a huge feature literal, duplicate form fields, malformed Basic-like input, missing versus unsupported grant and malformed JSON. No live token-expiry/limit/lock races.

  Each scenario uses strict assertions on media type, exact two-member set, code, literal description, status, single Basic challenge on 401 only, nonempty encoded response ID with round-trip to the request log and no old fields. A comparison of Basic/body failures is additional evidence, not their only oracle. Use a unique server-generated ID when input ingestion is disabled; poll logs for that ID under a bounded deadline instead of waiting an arbitrary delay. Never dump entire container logs into a failed secret assertion.
- [ ] **2. Run red** against the pre-change image using the future command below, before rebuilding; demonstrate an old envelope/partial/null-grant failure. Record the image/code revision so this is not mistaken for evidence against the new build. If no old image is available, do not fabricate this red result; the unit red evidence already pins these behavior changes.
- [ ] **3. Implement the new step assertions and update feature expectations.** Use parsed media type and JSON rather than substring/NotBeEmpty checks. Add only token-scoped request/response/log helpers, preserving generic response comparison steps used by other features. Rebuild and recreate the E2E stack after source changes per AGENTS.md.
- [ ] **4. Run green:** from repo root, `pwsh ./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter '(Category=@DMS-1606)&(Category!=@MssqlOnly)'`. At this task all tagged cases use the ordinary provider. Require nonzero executed scenario count, no skipped selected cases, matching provisioned DB and successful token/resource controls. Use `-SkipDockerBuild` only after images and test assemblies are rebuilt from this revision. Record TRX path and selected row coverage; classify setup failures separately.
- [ ] **5. Commit** the feature/steps: `test(DMS-1606): assert ordinary token OAuth contracts end to end`.

## Task 6: Closed proxy fixtures and dedicated real-stack lane

**Files:** Task 6 files in the map. Keep fixture-only scenarios in the existing token feature with `@DMS-1606 @TokenContractProxy`, without ordinary shard tags. Local rows retain ordinary shard tags. Add `@TokenContractQualified` to the focused qualified-route controls. Add a scoped BeforeScenario hook in `TokenEndpointStepDefinitions.cs`: when a proxy-tagged scenario is selected without `DMS_TOKEN_CONTRACT_PROXY_ACTIVE=1`, fail with the launcher command; never silently pass/skip. Document ordinary unfiltered runs excluding this category.

**Interfaces:**
- Test-only ASP.NET Core net10.0 executable, no new package references, listens on 8080 in Compose project `dms-token-contract`, network alias `dms-token-proxy` on external `dms`, host-only diagnostic port `127.0.0.1:18987`. `GET /health` reports readiness; `POST /token` either selects a closed fixture by decoded Basic client ID or forwards to mandatory `TOKEN_PROXY_UPSTREAM_URI`. No control endpoint or mutable response mode. Disable request/body/header logging and verbose framework exception output.
- Reserved IDs are exact `dms1606-401-object`, `dms1606-401-html`, `dms1606-400-invalid-client`, `dms1606-400-invalid-scope`, `dms1606-400-unknown`, `dms1606-429-legacy`, `dms1606-429-management`, `dms1606-429-generic`, `dms1606-409`, `dms1606-500`, `dms1606-abort`. Decode the Basic ID once using the same percent-encoding inverse needed for DMS-generated Basic; secrets never select behavior or reach logs. Other IDs forward unchanged to the fixed real provider. No user-supplied target URI or arbitrary status/body scripting.
- Launcher parameters: `-IdentityProvider <self-contained|keycloak>` (default self-contained), `-DatabaseEngine <postgresql|mssql>` (default postgresql), `-EnvironmentFile <path>` (default `.env.e2e` resolved by existing repo conventions), `-Configuration <Debug|Release>` (default Release), `-SkipDockerBuild`, `-UsePrebuiltOutput` switches forwarded to the root E2ETest flow. It owns derived files under `eng/docker-compose/.derived/` and its Compose project only. Refuse concurrent use of this shared DMS stack; do not delete the external network or unrelated containers.

- [ ] **1. Write proxy scenarios and launcher lifecycle tests before implementation.** In the feature, select exact fixtures using synthetic body credentials or explicit Basic; use Task 5 exact assertions. Cover object and malformed 401; recognized 400 invalid_scope; invalid_client with both caller mechanisms; unknown-code 400 fallback; both canonical 429s with limit 7; generic hostile-body 429; 409; 5xx and connection abort. Thus rows 12–17 have actual DMS-container E2E representatives, including 14a/b/c. Keep ordinary success/resource cases in the same dedicated run to prove forwarding works.

  Add Pester tests in `TokenContractE2E.Tests.ps1` using the repo's existing AST-extracted-function/mock-command practice. Pin provider-specific overlay selection, real target preservation, JWT authority/metadata unchanged, both engine exclusion filters, propagation of build/probe/test exit codes, zero-tests rejection and cleanup/restoration after readiness/build/test failure. Tests assert the child receives exact arguments and previous environment is restored; do not merely search script text. Add failure-after-start cleanup and a no-secret-output assertion. The live lane separately proves actual reachability and forwarding.
- [ ] **2. Run red:** `Invoke-Pester ./eng/docker-compose/tests/oauth-token-contract/TokenContractE2E.Tests.ps1 -Output Detailed`; expect missing launcher/lifecycle implementation. Select the new malformed-401 fixture scenario without the marker and assert the guard fails with launcher guidance; with the marker against the ordinary provider, it must fail its expected fixed-description assertion. Record these as expected failures, not product test results.
- [ ] **3. Implement the minimal proxy, Compose file and launcher.** Fixed fixture payloads: 401 object `{ "error":"invalid_client", "error_description":"Fixture rejected credentials" }`; HTML 401 `<html>withheld-provider-sentinel</html>`; 400 codes with `Fixture rejected request`; unknown400 `fixture_unknown`; legacy/management canonical429 bodies matching the current narrow parser (status 429, one exact guidance error at limit 7); generic429 with hostile/nested sentinel; 409 body sentinel; 500 body sentinel; abort via `HttpContext.Abort()` before headers. Normal forwarding preserves request body/content type/Authorization and response status/body/content type, excluding hop-by-hop headers. Supply no fixed successful token: real issuance/resource authorization must work.

  Build the proxy with the repository-compatible .NET 10 SDK/runtime image convention; do not add it to production publishing or the DMS solution. Read the selected real provider endpoint from the resolved existing env configuration before replacing it with `http://dms-token-proxy:8080/token`. Feed the real endpoint only to the proxy. Derived overlay changes only the selected `SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT` or `KEYCLOAK_OAUTH_TOKEN_ENDPOINT` and enables a fixed test correlation input header for deterministic ASCII round-trip cases; existing JWT/CMS settings remain intact. Keep host readiness probes on the published localhost port; DMS uses the network alias. Verify any host consumers of the selected endpoint still use their existing reachable endpoint—do not assume container DNS resolves on the host.

  Create/check the existing external network only if absent, start proxy, poll `/health` with a bounded deadline and cancellation, then call repo-root `build-dms.ps1 E2ETest` using `-EnvironmentOverlayFile` and `'(Category=@DMS-1606)&(Category!=@TokenContractQualified)&(Category!=@MssqlOnly)'` (use PostgresqlOnly exclusion for mssql). Set the test-process marker within try/finally. Preserve generated-DDL provisioning, database name propagation, provider selection and NODE_OPTIONS handling in the existing flow. Require nonzero expected scenarios and every row in the resulting TRX; an ignored selected case fails the lane.

  Add a second focused pass with derived `ROUTE_QUALIFIER_SEGMENTS=schoolYear`, selecting `'(Category=@TokenContractQualified)&(Category!=@MssqlOnly)'` (engine exclusion as above): assert root and `/2025/oauth/token` error/challenge/correlation equivalence and successful token issuance on the qualified mapping. Tag these cases `@DMS-1606 @TokenContractProxy @TokenContractQualified`, without ordinary shard tags. Its steps post explicit paths without changing the resource-test BaseURL or adding tenant-security semantics. Retain each pass's TRX under distinct `token-contract-root` / `token-contract-qualified` names immediately after the pass, including failed passes before cleanup or another invocation can overwrite them.

  In finally, capture sanitized relevant diagnostics before removing proxy, restore previous process variables and restart DMS with `eng/docker-compose/start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile <original-resolved-env> -IdentityProvider <selected-provider> -DatabaseEngine <selected-engine> -AddExtensionSecurityMetadata`, using the same schema-environment guard and provisioned E2E database context as the existing E2ETest restart. Retain any prerequisite resolved env file until restart finishes; remove only owned derived files and Compose resources. Keep the primary failure exit code even if cleanup also fails, and report both. If startup had not reached a usable provisioned DMS environment, report that state and stop the failed fixture-dependent DMS container rather than leaving it alive with a deleted target. Do not claim cleanup/restoration succeeded without its result.
- [ ] **4. Run green:** repeat Pester; then `pwsh ./eng/docker-compose/tests/oauth-token-contract/Invoke-TokenContractE2E.ps1 -Configuration Release -IdentityProvider self-contained -DatabaseEngine postgresql -EnvironmentFile './.env.e2e'`. Require all 17 rows, 14 subcases, canonical/generic429, deterministic transport failure, normal success/resource use, qualified controls and cleanup evidence. Rerun the wrapper with `-IdentityProvider keycloak` on PostgreSQL to prove provider selection/normal forwarding; use rebuilt images. SQL Server ordinary regression remains in Task 7; fixture protocol adds no database-specific behavior.
- [ ] **5. Wire the dedicated lane once in existing PR CI.** After the ordinary PostgreSQL E2E step in `run-e2e-tests`, invoke the launcher only for shard 1, forwarding provider, Release, `-SkipDockerBuild -UsePrebuiltOutput` and existing image environment. Keep the existing ordinary run and aggregate gate; no new job matrix/infrastructure. Retain distinct contract TRX and sanitized diagnostics with existing artifact conventions, including launcher failures. Update E2E README with the launcher, exclusion of both `@TokenContractProxy` and `@TokenContractQualified` for ordinary unfiltered runs, cleanup behavior and expected row coverage. Add `eng/docker-compose/tests/oauth-token-contract/TokenContractE2E.Tests.ps1` to the explicit Pester path list in this workflow's `run-bootstrap-pester-tests` job. Do not add fixture tags to ordinary shards or count them as tested there.
- [ ] **6. Commit** explicit Task 6 fixture, feature/steps, README and workflow paths: `test(DMS-1606): cover provider failures in the real DMS stack`.

## Task 7: Contract documentation, supersession and final regression evidence

**Files:** Documentation files listed in the map; inspect all earlier task diffs. No new production component.

**Interfaces:** New ADR is `reference/adr-oauth-token-errors-DMS-1606.md`, describing final implementation and linking the approved spec, story, relevant accepted ADRs and the local ODS parity report. Do not treat ignored analysis artifacts as deliverables required by a fresh checkout; include essential pinned ODS source links and intentional differences in the ADR itself. Jira ownership/workflow edits remain with ticket owners.

- [ ] **1. Write the ADR and migration notes.** Cover exact JSON shape, six recognized provider codes, DMS extensions, fixed realm, statuses and scope boundaries, decoded description validation, encoded header/log matching with a literal-percent example, no provider-header forwarding, canonical429 reuse guidance, and root/qualified behavior. Explain client-visible changes from old problem fields and missing/partial/null-grant behavior, with a before/after response example. Document retained DMS404 versus ODS415 and the other intentional parity differences using the supporting report rather than claiming universal parity.
- [ ] **2. Add dated supersession links** to the two older OAuth ADRs for changed envelopes/classification/400 mapping/correlation channel only. Cross-reference DMS-1549: one owned malformed401 fix, transport502 unchanged, selected-summary policy supersedes old problem-envelope/raw-Information-body expectations. Update 8.1.0 Breaking changes with actual migration and header decoding. Leave accepted request/disclosure decisions outside these clauses intact; no Jira updates.
- [ ] **3. Run final focused regression commands after implementation**, from repo root:

```powershell
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj
Invoke-Pester ./eng/docker-compose/tests/oauth-token-contract/TokenContractE2E.Tests.ps1 -Output Detailed
```

  Expected: nonzero test counts, all pass, no selected test skipped. Task 6's green real-stack evidence is reused unless changes invalidate it. Run ordinary E2E regressions with rebuilt images/assemblies and teardown/setup between configurations:

```powershell
pwsh ./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter '(Category=@e2e-ci-shard-1)&(Category!=@MssqlOnly)'
pwsh ./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider keycloak -EnvironmentFile './.env.e2e' -TestFilter '(Category=@e2e-ci-shard-1)&(Category!=@MssqlOnly)'
pwsh ./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine mssql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter '(Category=@MssqlRepresentative|Category=@MssqlOnly|Category=@DMS-1604)&(Category!=@TokenContractProxy)&(Category!=@TokenContractQualified)&(Category!=@PostgresqlOnly)'
```

  Normal provider runs must prove success/resource use and the ordinary row cases without the proxy. For unsupported runtime, Docker readiness, DB provisioning or stale connection failures, record the actual environment blocker; do not substitute in-process tests for required E2E or call the story complete. Do not add a database integration suite for this token-only change.
- [ ] **4. Inspect scope and usage, format and review.** `rg -n 'ForUnauthorized|ForTooManyTokens|GenerateProblemDetailResponse' src/dms` confirms shared consumers remain; inspect changed OAuth comments/tests for stale problem/correlation/503 assertions. Run CSharpier only on touched C# files, `git diff --check`, and review exact changes for secrets, prohibited dependencies, global response changes or mapping-version edits. Verify workflow artifacts preserve the two contract pass results and failure evidence. Record actual commands, counts, TRX locations and runtime dependency issues in the implementation handoff/PR; never claim future commands ran during planning.
- [ ] **5. Commit** the four documentation paths: `docs(DMS-1606): publish token error contract and migration`.

## AC coverage, dependencies and completion gates

| Criterion | Owning tasks | Required evidence |
|---|---|---|
| REQ-1: OAuth JSON contract | 1–4; precise E2E assertions 5–6 | Exact two-member body, string values, media, encoded ID; selected boundary controls and exclusions. |
| REQ-2: 401 Basic challenge | 1–3; 5–6 | Actual frontend challenge delivery, every401/single fixed realm; none on non401; original caller-context400 exception. |
| REQ-3: all inventory outcomes | 1 local; 2 provider; 3 direct parse; 4 extra selected boundaries | Spec matrix 1–17, 14a/b/c, order-of-validation regressions and safe fallbacks. |
| REQ-4: unit + E2E per row | 1–4 unit/HTTP; 5 rows1–11 and real401; 6 rows12–17 | No row waived; closed fixture lane, live provider success, qualified and non-token controls; no timing races. |
| REQ-5: changelog + ADR | 7 | Explicit migration, infrastructure extensions, parity differences and limited supersession. |

Rechecked locally during planning: `origin/main` remains `2098235995a5053ad7a35f070c6d9e94e5e99ae2`; its narrow DMS-1586 parser fix is absent from the product baseline and required in Task 2. No fetch, merge or branch change was performed. DMS-1604 is present; DMS-1598/CMS PR1373 is not a demonstrated prerequisite. Supporting human/nano/review/ODS artifacts stay supporting evidence; the approved spec resolved their contradictions.

Portable ODS evidence entry points are the [TokenController](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Controllers/TokenController.cs), [TokenError](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Models/Tokens/TokenError.cs) and [request provider](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Providers/ClientCredentialsTokenRequestProvider.cs) at inspected main `7fcefbdaccab498893098068cb07f2b80fbc222e`. Those three file blobs also matched the earlier ADR pin `e453cd2cad8a0653c65453948d3d235aec7c517c` and release v7.3.2 `e489317ea77f245aff99d57374165c238848f9a0`. This is source evidence, not a deployed/live compatibility result; see the supporting report for serializer/middleware evidence.

No new product deviation or unresolved product choice is introduced by this plan. Exact new names/texts/fixture IDs above are implementation details of the approved design. Proxy reachability/abort semantics/restoration and all passing test evidence remain unverified execution obligations. Stop and obtain explicit agreement if implementation would change the story/design rather than silently relaxing coverage or expanding scope.

**Plan status:** created and self-reviewed; awaiting user approval. All task checkboxes are intentionally unchecked. Self-review mapped every spec section/AC/row, checked producer/consumer signatures and the five Review Focus tests, retained both URNs, separated caller/upstream auth, and checked bounded fixture ownership and artifact retention. No implementation or local tests ran to create this document.

**Next gate:** user reviews this plan and selects the later execution approach. Recommend Native (`superpowers:executing-plans`) because the tasks share one classification/serialization contract and evolve the same manager and HTTP/E2E tests; preserving that context avoids repeated integration work. Subagent-driven execution remains available if the user prefers its separate task reviews. After plan approval, create the requested continuation handoff with approved paths/revisions, branch, dependency reconciliation, zero implementation progress and pending verification. Do not implement as part of this handoff.
