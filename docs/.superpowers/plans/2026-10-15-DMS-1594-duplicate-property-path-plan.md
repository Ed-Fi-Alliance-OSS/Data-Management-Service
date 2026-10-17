# DMS-1594 Duplicate-Property Array Paths Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report the actual zero-based location of the first duplicate JSON property for resource writes and identity search through one scanner.

**Architecture:** A public Core utility scans original UTF-8 tokens with explicit object/array parentage. Core raw-string fallback encodes once; the frontend passes its existing span after complete parsing. Response construction and all pipeline ordering stay at their existing boundaries.

**Tech Stack:** .NET 10, C#, System.Text.Json/Utf8JsonReader, NUnit, FluentAssertions, FakeItEasy, existing ASP.NET Core/plugin and Reqnroll E2E fixtures.

**Spec:** [Approved DMS-1594 specification](../specs/2026-10-15-DMS-1594-duplicate-property-path-design.md).

## Global Constraints

- Work in the current repository and branch `DMS-1594`; implementation baseline is `8aed04344745201c5f4b560738184889510a8fab`, followed by documentation commits. Do not switch branches or merge unrelated work as part of execution.
- Follow root `AGENTS.md`: .NET 10 style, non-nullable variables, `is null`/`is not null`, System.Text.Json, NUnit `Given_` fixtures/`Setup`/`It_` assertions, FluentAssertions, and FakeItEasy where needed.
- Exact scanner contract: `bool TryFindFirstDuplicatePropertyPath(ReadOnlySpan<byte> json, out string path)`; false sets `string.Empty`.
- Preserve first duplicate in token encounter order; decoded case-sensitive names scoped to each object; raw `.property` and `[index]` formatting; both adapters and supplied-path precedence.
- Preserve the full duplicate `400` envelope/message/correlation, complete-parse precedence, identity authorization/capability ordering, provider paths verbatim, and identity `Cache-Control: no-store`.
- Log only `LoggingSanitizer.SanitizeFreeTextForLogging(path, 1024)` and `TraceId.Value`. The suffix may extend the logged value beyond 1,024 UTF-16 units; the response key is never sanitized or truncated.
- Preserve BOM/whitespace handling, buffer clearing/return, token-info raw handling, cancellation forwarding, and current JSON grammar. No new assembly, dependency, parser framework, configuration, or shared mutable scan state.
- No CMS, provider contract/package, OpenAPI, CDC, persistence, mapping/fingerprint, or general path-escaping changes. `RelationalMappingVersion` remains `v3`.
- Refresh DMS-1595 overlap before editing; preserve its media-type correction if present. At the inspected baseline, duplicate responses default to `application/json`. Do not introduce a resource-wide media-type policy.
- The human approved this plan and selected native execution on 2026-10-15. No commands were run during planning; implementation and verification are now authorized.

## Review Focus

1. Escaped-equivalent and punctuation-bearing property names must preserve decoded equality and existing wire formatting (Task 1, cases and exact assertions).
2. Empty/nested property containers and mixed arrays must not advance their owning element or consume another array's counter (Task 1, completion cases; Tasks 2/3, adapter matrix).
3. A duplicate followed by invalid trailing syntax must remain a malformed-body error through both byte and raw paths (Tasks 2/3).
4. A supplied path must beat a different raw duplicate, and earlier identity denials must beat duplicate reporting (Tasks 2/3).
5. Control-padded or very long/astral property names must produce safe bounded logs while preserving the complete response key (Task 2).

## File map and verification commands

Paths below are repository-relative. No project-file changes are needed for SDK-included source files.

| Task | Files and responsibility |
| --- | --- |
| 1 | Create `src/dms/core/EdFi.DataManagementService.Core/Utilities/DuplicatePropertyPathScanner.cs`; create `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Utilities/DuplicatePropertyPathScannerTests.cs`. |
| 2 | Modify `src/dms/core/EdFi.DataManagementService.Core/Middleware/DuplicatePropertiesMiddleware.cs`, `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/DuplicatePropertiesMiddlewareTests.cs`, `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Handler/IdentityHandlerRequestValidationTests.cs`, and `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/ServiceClaimAuthorizationMiddlewareTests.cs`. |
| 3 | Modify `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`, `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/AspNetCoreFrontendRequestBodyTests.cs`, `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Plugins/Identity/Given_ARequestThatIsMalformedBeforeTheProvider.cs`, `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Plugins/Identity/Given_TheIdentityFixtureDoesNotAdvertiseSearch.cs`, `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/CreateResourcesValidation.feature`, and `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/UpdateResourcesValidation.feature`. |

Use these PowerShell variables from the repository root during execution:

```powershell
$coreUnitProject = 'src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj'
$frontendUnitProject = 'src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj'
$apiIntegrationProject = 'src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj'
```

Use normal builds with `dotnet test` so required fixtures are staged. `PackedContractFixturePlugins.targets` stages the identity fixture after the integration project's build; do not hand-publish plugins or initially use `--no-build`. Identity plugin cases require no database lease. Resource E2E does require its normal provisioned stack.

### Task 1: Implement the parent-aware Core scanner

**Files:** Create the two Task 1 files above.

**Interfaces:** Consumes `ReadOnlySpan<byte>` holding original, fully parsed JSON. Produces `EdFi.DataManagementService.Core.Utilities.DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath(ReadOnlySpan<byte> json, out string path)` as a public static method. Internal frame/segment types are private; no string overload or DI registration.

- [ ] **Step 1: Write scanner contract and completion tests.** Use parameterized fixture `Given_Duplicate_Property_Path_Scanning(string json, bool expectedFound, string expectedPath)` with a local static `Cases` source and `[TestFixtureSource(nameof(Cases))]`. A `[SetUp]` scans the selected raw literal; separate `It_` methods assert the boolean and exact path. Assert the method directly, without parsing/reserializing the duplicate input:

```csharp
bool found = DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath(
    Encoding.UTF8.GetBytes(json), out string path);
found.Should().BeTrue();
path.Should().Be(expectedPath);
```

Include the three literal Jira inputs, stored locally in the test fixture rather than fetched at runtime:

| Raw literal | Expected path |
| --- | --- |
| `{"studentUniqueId":"1","addresses":[{"city":"a","streetNumberName":"b"},{"city":"x","city":"y"}]}` | `$.addresses[1].city` |
| `{"studentUniqueId":"1","addresses":[{"city":"a"}],"electronicMails":[{"x":"1","x":"2"}]}` | `$.electronicMails[0].x` |
| `[{"LastSurname":"a"},{"BirthLocation":{"City":"x","City":"y"}}]` | `$[1].BirthLocation.City` |

Also parameterize every row of the approved spec's four-row matrix with its resource/search wrapper and exact expected path. Keep this data local to each adapter test project as needed; do not add a shared test-data assembly.

- [ ] **Step 2: Add scanner preservation cases.** Methods `It_keeps_the_first_duplicate`, `It_compares_decoded_names_ordinally`, `It_retains_existing_property_formatting`, and `It_completes_only_direct_array_elements` pin these exact examples:

| Input | Result |
| --- | --- |
| `{"a":{"x":1,"x":2},"b":1,"b":2}` | true, `$.a.x` |
| `{"x":1,"\u0078":1}` | true, `$.x` |
| `{"a.b":1,"a.b":2}` | true, `$.a.b` (existing formatting) |
| `{"X":1,"x":2}` and `[{"x":1},{"x":2}]` | false, `string.Empty` |
| `[{"child":{},"list":[],"x":1,"x":2}]` | true, `$[0].x` |
| `{"matrix":[[],[{}, {"x":1,"x":2}]]}` | true, `$.matrix[1][1].x` |
| `{"a":[1,"s",true,false,null,{},[]],"b":[{"x":1,"x":2}]}` | true, `$.b[0].x` |
| `{"mixed":[1,"s",true,false,null,{},[],{"x":1,"x":2}]}` | true, `$.mixed[7].x` |

False-result assertions are `found.Should().BeFalse(); path.Should().BeEmpty();`. Add root-duplicate and valid empty-object/array controls. Interleave repeated scans of different bodies to confirm state is local, without timing-based concurrency tests.

- [ ] **Step 3: Run the scanner tests before creating the utility.**

```powershell
dotnet test $coreUnitProject --filter 'FullyQualifiedName~Given_Duplicate_Property_Path_Scanning'
```

Expected red: the new scanner symbol is missing. Once the signature exists, any remaining red must name an exact path/contract mismatch, not an environment failure.

- [ ] **Step 4: Implement the scanner.** Use `Utf8JsonReader` with the existing `CommentHandling = JsonCommentHandling.Skip` options. Implement the approved spec's container-frame/path-restoration algorithm: object ordinal name sets and pending property, array index, child entry path length, and one actual-parent completion operation. Return the first duplicate immediately; leave full syntax validation to callers. Document false/empty and the caller's validation responsibility.

- [ ] **Step 5: Format and verify the new utility and its fixture.** Run `dotnet csharpier format` separately for the two new files, then rerun Step 3. Expected green: a positive executed test count, zero failures, and no skipped new cases.

- [ ] **Step 6: Commit the two new files.** Review `git diff --check` and their diff, stage those exact paths, then commit with `fix(DMS-1594): add parent-aware duplicate property path scanner`. Existing adapters remain unchanged at this boundary.

### Task 2: Integrate raw fallback and safe duplicate logging

**Files:** Modify the four Task 2 files in the map.

**Interfaces:** Consume Task 1's exact scanner method. Preserve `DuplicatePropertiesMiddleware.Execute(RequestInfo requestInfo, Func<Task> next)` and `FrontendRequest` fields. Retain nullable absent-path metadata; use non-null `out string path` from the scanner. No constructor option is added for this story; preserve a separately landed DMS-1595 option.

- [ ] **Step 1: Add raw fallback and precedence regressions.** Add parameterized fixture `Given_Raw_Duplicate_Property_Paths(string json, string expectedPath)` nested inside the existing `DuplicatePropertiesMiddlewareTest` class, so its filter selects it. `[SetUp]` creates a raw `FrontendRequest`, middleware with a logger, and a `next` callback incrementing `_nextCalls`. Exercise all three story literals and both wrappers of every spec matrix row. Assert:

```csharp
_requestInfo.FrontendResponse.StatusCode.Should().Be(400);
JsonObject errors = _requestInfo.FrontendResponse.Body!["validationErrors"]!.AsObject();
errors.Select(item => item.Key).Should().Equal(expectedPath);
errors[expectedPath]!.AsArray().Select(item => item!.GetValue<string>())
    .Should().Equal("An item with the same key has already been added.");
_nextCalls.Should().Be(0);
```

Add `It_prefers_the_supplied_path` with metadata `$.supplied` and raw `{"other":1,"other":2}`, expecting only `$.supplied`. Add `It_continues_once_without_duplicates`, expecting `_nextCalls == 1`. Assert complete problem fields against the spec, including `errors: []` and the setup's `traceId`; do not rely on substring-only envelope checks.

- [ ] **Step 2: Add composed parse/authorization safeguards.** In `IdentityHandlerRequestValidationTests.cs`, use the existing `CreateRequestInfo` and `RunJsonBodyChain` with `IdentityOperation.Search` and raw `[{"x":1,"x":2},]`. `It_reports_malformed_json_before_a_duplicate` asserts status 400, the sole validation key `$.`, absence of `$[0].x`, and provider invocation count 0. In the service-claim fixture's no-identity-claim case, supply duplicate metadata and compose duplicate middleware in the next callback; `It_keeps_forbidden_before_duplicate_reporting` asserts 403 and no callback invocation. Keep existing exact pipeline-order and capability-denial tests in the verification slice.

- [ ] **Step 3: Add path logging regressions.** Capture Debug events with a fixture-local Serilog sink following `Identity/IdentityProviderBoundaryTests.cs`; inspect both `DuplicatePath` and rendered messages. Nest `Given_A_Duplicate_Path_Is_Logged` inside `DuplicatePropertiesMiddlewareTest` for filter coverage; setup supplies a precomputed path so these tests isolate logging. Define `originalPath` as `"$." + new string('\n', 1100) + new string('a', 1023) + "😀tail"`. `It_sanitizes_before_truncating_without_splitting_a_surrogate` asserts:

```csharp
loggedPath.Should().Be("$." + new string('a', 1022) + LoggingSanitizer.TruncationSuffix);
renderedMessage.Should().NotContain("\n").And.NotContain("\r");
validationErrors.Select(item => item.Key).Should().Equal(originalPath);
```

Add a separate shorter `"$.x\r\nforged\t\u202E"` case expecting logged `"$.xforged"` while the response key remains exact. Add an astral-boundary case `"$." + new string('a', 1021) + "😀tail"`, expecting `"$." + new string('a', 1021) + LoggingSanitizer.TruncationSuffix`; the sanitizer must drop the whole split pair. Reuse the sanitizer; do not duplicate its implementation or add a logging framework.

- [ ] **Step 4: Run the affected tests before changing middleware.**

```powershell
dotnet test $coreUnitProject --filter 'FullyQualifiedName~DuplicatePropertiesMiddlewareTest|FullyQualifiedName~IdentityHandlerRequestValidationTests|FullyQualifiedName~ServiceClaimAuthorizationMiddlewareTests|FullyQualifiedName~IdentityOperationCapabilityMiddlewareTests|FullyQualifiedName~PipelineOrderingTests'
```

Expected red: indexed raw examples have incorrect keys and the unsanitized logging assertions fail. Existing precedence controls may already pass; record that honestly.

- [ ] **Step 5: Wire Core and correct the local comment.** In `Execute`, retain supplied-path precedence; encode raw input only on fallback and use Task 1's scanner. Preserve `ForDataValidation`, the exact envelope/message, short-circuit, and `next` semantics. Add local `MaxLoggedDuplicatePathLength = 1024`; sanitize a separate log copy and log `TraceId.Value`. Remove only this middleware's walker/path renderer/segment type and now-unused scanner imports. Replace the false universal JsonNode-overwrite comment with the approved original-token rationale. Do not change parse middleware or pipeline composition.

- [ ] **Step 6: Format and rerun Step 4.** Format the four changed C# files with `dotnet csharpier format <file>`. Expected green: positive counts for the new fixtures, zero failures; existing malformed/empty/media, capability, and ordering cases continue passing.

- [ ] **Step 7: Commit the four changed files.** Inspect `git diff --check` and the exact paths; commit with `fix(DMS-1594): share raw duplicate scanning and bound path logs`.

### Task 3: Integrate frontend bytes and verify real HTTP boundaries

**Files:** Modify the Task 3 files in the map. Retain existing test hosts/helpers; do not add an HTTP test framework or shared fixture project.

**Interfaces:** Consume Task 1's scanner with the frontend's BOM-stripped `ReadOnlySpan<byte>`. Preserve `ExtractJsonBodyFrom(HttpRequest request)` and its private result record. Frontend tests capture `FrontendRequest` from the existing fake `IApiService`; identity tests use `IdentityHttpRun.StartAsync`, `PostAsync`, and `Stub.InvocationCount("search")`.

- [ ] **Step 1: Add byte adapter regressions.** Extend `Given_AspNetCoreFrontend_Request_Body_Extraction` with `It_carries_the_exact_indexed_duplicate_path` and parameterized raw literals: all three story examples plus both wrappers of all spec matrix rows. Resource bodies exercise both `Upsert` and `UpdateById`; search bodies exercise `IdentitySearch` with `Options.Create(new KestrelServerOptions())`. Configure the fake facade for the selected method, capture into a non-null initialized `FrontendRequest` field, and assert `DuplicatePropertyPath == expectedPath`, `Body is null`, `BodyParseErrorMessage is null`. Do not materialize a duplicate-bearing `ParsedBody` to compare it. Add `It_preserves_malformed_after_duplicate_reporting` using `{"x":1,"x":2,}`: parse error is present, duplicate metadata is absent. Preserve all existing extraction/cancellation tests.

- [ ] **Step 2: Add real search rejection and earlier-gate checks.** Extend the existing malformed-before-provider fixture's case dictionary with Jira's literal search body. Start its existing run with `settings: new Dictionary<string, string> { ["AppSettings:CorrelationIdHeader"] = "X-Correlation-ID" }` and add `X-Correlation-ID: dms-1594-http` to `run.Client.DefaultRequestHeaders`. Assert the sole key `$[1].BirthLocation.City`, exact duplicate message/full problem fields, `correlationId == "dms-1594-http"`, `Cache-Control: no-store`, and zero search invocations before its existing valid search control. Preserve the implementation base's media type. Add `It_keeps_unauthorized_before_duplicate_reporting` using `run.Host.CreateClient(IdentityTestClients.UnknownToken)` and the same raw body, expecting 401, no duplicate validation key, and zero search invocations. In `Given_TheIdentityFixtureDoesNotAdvertiseSearch`, add the duplicate search body: expect operation-unsupported 404 and zero search calls, while retaining its existing valid/advertised-operation controls.

- [ ] **Step 3: Add representative resource HTTP scenarios.** Append one `@DMS-1594 @e2e-ci-shard-1` scenario to each existing Create/Update validation feature. Reuse each feature's authorization/background and the education-content scaffold; POST to `/ed-fi/educationContents`, PUT to its existing `/ed-fi/educationContents/{id}` with that ID. Add raw `"items":[{"a":1,"b":2},{"x":1,"x":2}]`, expecting the sole key `$.items[1].x` and the complete duplicate problem from the spec. The custom property deliberately pins duplicate rejection before later ApiSchema validation; it does not introduce an accepted schema property. Use existing E2E correlation normalization conventions. No full matrix is repeated here.

- [ ] **Step 4: Run frontend and HTTP red checks before frontend wiring.**

```powershell
dotnet test $frontendUnitProject --filter 'FullyQualifiedName~Given_AspNetCoreFrontend_Request_Body_Extraction'
dotnet test $apiIntegrationProject --filter 'FullyQualifiedName~Given_ARequestThatIsMalformedBeforeTheProvider|FullyQualifiedName~Given_TheIdentityFixtureDoesNotAdvertiseSearch'
```

Expected red: frontend metadata and real search report the old indexed path. Do not mistake fixture staging/restore failures for a reproduced regression. Unit and identity HTTP red are sufficient before the code change; do not build the Docker stack twice merely to repeat red evidence.

- [ ] **Step 5: Wire frontend extraction.** Keep complete `JsonNode.Parse(body)` before the scanner call, the existing try/catch/finally, buffer/BOM/whitespace behavior, and metadata handoff. Pass the original span to Task 1's scanner and translate false to absent metadata. Remove frontend `FindDuplicatePropertyPath`, `BuildJsonPath`, and scanner-only `JsonPathSegment`; remove imports only if no remaining usages. Do not move rejection to the frontend or rewrite any provider paths.

- [ ] **Step 6: Format and verify the focused suites.** Format the four changed C# files separately. Rerun Step 4 and Task 2's test command. Then run existing projection/cache regressions:

```powershell
dotnet test $coreUnitProject --filter 'FullyQualifiedName~IdentityErrorProjectionTests'
dotnet test $frontendUnitProject --filter 'FullyQualifiedName~IdentityNoStoreTests'
```

Expected green: positive counts, no new skipped fixtures, zero failures. A zero-test result is not evidence.

- [ ] **Step 7: Run the focused resource E2E cases against rebuilt images.** Verify `docker ps`, then teardown the local E2E stack before rebuilding for changed DMS code, following `AGENTS.md`. From the repository root:

Execution correction: the root `E2ETest` target dispatches already-built test assemblies and rejects a missing assembly as a zero-test run. Build the E2E project in the selected configuration first:

```powershell
dotnet build src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj -c Release
```

Expected: build succeeds with the Release E2E assembly present. Then run the environment and test commands below.

```powershell
pwsh -File src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1
pwsh ./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine postgresql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter '(Category=@DMS-1594)&(Category!=@MssqlOnly)'
```

Omit `-SkipDockerBuild` so this run tests the changed frontend. The build entry point handles setup/provisioning, DMS restart, test database selection, and unsupported `NODE_OPTIONS`. Expected: exactly the two new resource cases execute and pass. Inspect `dms-local` container logs for API failures; provisioning/health/runtime failures are environment issues, not scanner results. Do not repeat across dialects absent new evidence of a dialect-specific issue.

- [ ] **Step 8: Inspect the final change and commit.** `rg -n 'FindDuplicatePropertyPath|BuildJsonPath|JsonPathSegment|TryFindFirstDuplicatePropertyPath'` over the two production files and the new utility must show one implementation and two callers, with no leftover private scanner helpers. Review `git diff --check`, test evidence, and scope exclusions. Commit the six changed files with `fix(DMS-1594): use shared byte scanning and verify indexed HTTP errors`.

## Coverage and execution handoff

| Spec requirement | Owning tasks |
| --- | --- |
| REQ-1/REQ-2 exact resource/search paths | Tasks 1–3; literal inputs in scanner and both adapters, representative HTTP boundaries. |
| REQ-3 actual-parent completion | Task 1 algorithm/cases; Tasks 2/3 matrix and nested/independent-array controls. |
| REQ-4 one walker/two entry paths | Task 1 public interface, Task 2 raw caller, Task 3 byte caller and usage check. |
| REQ-5 every matrix case in both shapes/adapters | Task 2 raw matrix; Task 3 byte matrix; Task 1 focused algorithm cases. |
| Failure precedence/compatibility/logging | Task 2 raw parse/service-claim/log tests; Task 3 byte parse/full HTTP gates/headers and existing projection/cache checks. |

Planning self-review completed against the approved spec: task interfaces agree, every requirement has an owner, all five Review Focus inputs have concrete assertions, and no infrastructure or product scope was added. No implementation or local tests were performed during planning.

**Review gate:** Closed by the human's approval and selection of native execution (`superpowers:executing-plans`). Preserve the current-branch instruction. Report any unavailable verification as outstanding evidence rather than claiming success.
