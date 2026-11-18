# DMS and CMS mutation-testing implementation plan

## Objective and current state

Implement focused Stryker.NET runs for the ten areas identified in the codebase review and strengthen tests around meaningful surviving mutants. Prioritize observable authorization, data-integrity, validation, and generated-contract behavior. Run Stryker directly and use its native reports.

This plan is based on source inspection at commit `dfbd05308`. No mutation baseline or runtime measurements had been collected when this plan was written. The priorities below describe expected value, not demonstrated test defects. Implementation progress is tracked below; measured runs and remaining mutation limitations are recorded in `STRYKER.md`.

Initial rollout issues:

1. Five test projects already have `stryker-config.json`: DMS Core, Plans, RelationalModel, Ddl, and CMS Backend. All use `Release`, `net10.0`, HTML/JSON/progress reporters, and zero high/low/break thresholds. A successful run can contain survivors, and the reporting bands do not distinguish weak results.
2. CMS Backend's configuration mutates only `EdFi.DmsConfigurationService.Backend.csproj`. Authorization resolution lives in `EdFi.DmsConfigurationService.Claims`; client admission and token issuance live in `EdFi.DmsConfigurationService.Backend.OpenIddict`. Neither is covered by that configuration. DMS Core's current include list also omits `Profile/`, and DMS Backend has no configuration.
3. The root tool manifest pins Stryker **5.0.0**; the DMS and CMS manifests each pin **4.15.0** and declare `isRoot: true`. Running from the test directories therefore encounters a different manifest from running at the repository root.

## Delivery sequence

Keep the original priority numbers as stable area identifiers. Create each configuration when work on its area begins.

| Phase | Work | Dependency and completion condition |
| --- | --- | --- |
| 1 | Align tool versions; pilot area 9 decimal validation and area 7 batching | Direct commands produce mutants in the intended files and usable reports; complete the baseline, triage, test-improvement, and rerun loop. |
| 2 | Complete all ten areas | Work through areas 1, 2, 3, and 5; then 4 and 6; then finish 7 and 9 and complete 8 and 10. Qualify each configuration as it is introduced. |
| 3 | Verify full targets and finish operating documentation | All eleven targets meet their area acceptance criteria and have recorded results and reproducible commands. |

Run Stryker sequentially from the owning test directories. Do not run two Stryker processes against the same checkout concurrently.

## Rollout foundation

### 1. Standardize and qualify the toolchain

- [x] Align `.config/dotnet-tools.json`, `src/dms/.config/dotnet-tools.json`, and `src/config/.config/dotnet-tools.json` on **5.0.0**, the version already selected at the root. Preserve unrelated tools and manifest boundaries.
- [x] Restore tools and check `dotnet tool list --local` from the root, a DMS test directory, and the CMS Backend test directory. Verify the commands and configuration options against `dotnet stryker --help` for that version.
- [x] Use the two pilots to qualify .NET 10, the existing NUnit/VSTest setup, Release builds, and HTML/JSON report generation. Resolve toolchain failures before expanding to the remaining areas.
- [x] Qualify DDL fixture timing with focused controls and a full rerun; retain `additional-timeout: 100000` and `timeout-ratio: 3.0` in that named configuration to avoid timing failures being mistaken for assertion kills.

Tool-version changes require fresh baselines: the set of generated mutants and runner behavior may change.

### 2. Make assembly and scope selection explicit

The following aliases identify existing production and sibling unit-test directories. Source paths in the area sections are relative to the production directory; test filenames are relative to the test directory.

| Alias | Production directory | Unit-test directory |
| --- | --- | --- |
| DMS Core | `src/dms/core/EdFi.DataManagementService.Core` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` |
| DMS Plans | `src/dms/backend/EdFi.DataManagementService.Backend.Plans` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` |
| DMS Backend | `src/dms/backend/EdFi.DataManagementService.Backend` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` |
| DMS Model | `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel` | `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit` |
| DMS DDL | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit` |
| CMS Claims | `src/config/backend/EdFi.DmsConfigurationService.Claims` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` |
| CMS OpenIddict | `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` |

Create self-contained named configurations in these test directories. Each configuration selects exactly one production project by its `.csproj` filename and the relative DMS or CMS solution path. Area 6 requires two runs because it spans two assemblies.

| Target ID | Test directory alias | New configuration | Area |
| --- | --- | --- | --- |
| `dms-request-auth` | DMS Core | `stryker-request-auth.json` | 1 |
| `cms-permission-inheritance` | CMS Claims | `stryker-claims.json` | 2 |
| `dms-query-auth` | DMS Plans | `stryker-query-auth.json` | 3 |
| `dms-write-execution` | DMS Backend | `stryker-write-execution.json` | 4 |
| `cms-token-admission` | CMS OpenIddict | `stryker-openiddict.json` | 5 |
| `dms-profile-core` | DMS Core | `stryker-profile.json` | 6 |
| `dms-profile-backend` | DMS Backend | `stryker-profile.json` | 6 |
| `dms-write-plans` | DMS Plans | `stryker-write-plans.json` | 7 |
| `dms-model-constraints` | DMS Model | `stryker-model-constraints.json` | 8 |
| `dms-validation` | DMS Core | `stryker-validation.json` | 9 |
| `dms-ddl` | DMS DDL | `stryker-ddl.json` | 10 |

- [x] Populate each named configuration with the files listed under its area. Refresh the baseline when the declared scope changes. Leave the five existing default configurations unchanged.
- [x] Use `Release`, `net10.0`, HTML/JSON/progress reporters, Standard mutation level, and per-test coverage selection, after verifying the pinned CLI supports these values. Keep the full owning unit-test suite available; narrow mutated source before filtering tests.
- [x] Set high 80 / low 60 / break 0 in the new configurations. The reporting bands aid interpretation; completion depends on the behavior-specific acceptance criteria, not a score threshold.
- [x] Exclude generated output and assembly boilerplate. Retain mutations of SQL fragments, claim names, and response contents because they represent relevant contracts.
- [x] When qualifying each configuration, inspect the report for the intended assembly, source files, and executable mutants. CMS Claims is currently referenced transitively; change project references only if needed to make that target work. Verify OpenIddict instrumentation despite its assembly-level `ExcludeFromCodeCoverage` attribute before changing coverage settings or attributes.

A source include pattern cannot select code from another production assembly.

### Weekend CI policy

- [x] Run all eleven named targets on Saturday and preserve native reports, exact status counts, source-level diagnostics, and logs for each target.
- [x] Treat `Survived` and `NoCoverage` findings as informational while tests and survivor dispositions are improved. Triage them against the behavior-specific acceptance criteria; a successful job does not establish adequate test coverage.
- [x] Continue failing on a nonzero Stryker exit, missing/invalid/ambiguous reports, pending mutants, or reports with no executable mutants. Report timeouts, compile errors, and ignored mutants separately from assertion-based kills.
- [x] Retain the named configurations' high 80 / low 60 / break 0 thresholds. Do not require zero survivors or introduce an exemption allowlist during this phase.

### 3. Document commands and results

- [x] Maintain one `STRYKER.md` containing prerequisites, test-directory/configuration pairs, direct commands, and a compact results table with scope, tool version, before/after score, runtime, and report location.
- [x] Use Stryker's own HTML/JSON reports and reported scores. Inspect the report from the current run; failed runs and missing, empty, or incorrectly scoped reports do not establish a baseline.
- [x] Summarize meaningful survivor groups, test improvements, verification, and justified exceptions in each test-improvement PR. Use source locations and mutation descriptions to identify findings; mutant IDs are run-local.
- [x] Keep raw reports under ignored `StrykerOutput/`. Record focused investigations separately from full-target results so a pilot score is never presented as an area's score.

## Implementation work by area

### Area 1 — DMS request authorization

**Scope:** DMS Core `Middleware/ResourceActionAuthorizationMiddleware.cs`, `Middleware/ProvideAuthorizationFiltersMiddleware.cs`, and `Security/ClaimSetResolver.cs`.

**Existing tests:** `Middleware/ResourceActionAuthorizationMiddlewareTests.cs`, `Middleware/ProvideAuthorizationFiltersMiddlewareTests.cs`, and `Security/ClaimSetResolverTests.cs`.

- [x] Baseline claim-set resolution first, then the middleware and strategy conversion.
- [x] Challenge zero/one/multiple matching claim sets, duplicate metadata, unrelated duplicates, scope candidates versus legacy-name fallback, and casing rules.
- [x] Exercise all four Create/Update grant combinations for POST. Assert the distinct policies delivered to the backend, including denied actions and permitted actions with invalid or missing strategy metadata.
- [x] For GET/PUT/DELETE, assert the selected action, strategy collection, response status/body/headers, and whether the next pipeline step executes.
- [x] Preserve cancellation behavior and prevent mutation-induced changes to a shared authenticated record through request-local claim-set resolution.

**Acceptance:** all actionable survivors affecting grant/deny decisions or denied-request continuation are killed; response-only and equivalent mutants are explicitly triaged. Status-code assertions alone are insufficient when the pipeline or policy payload is wrong.

### Area 2 — CMS permission inheritance

**Scope:** CMS Claims `AuthorizationMetadata/AuthorizationMetadataResolver.cs` and `AuthorizationMetadata/AuthorizationMetadataResponse.cs`.

**Existing tests:** `AuthorizationMetadataResolverTests.cs`, `AuthorizationMetadataResolverPropertyTests.cs`, `AuthorizationMetadataPropertyScenarios.cs`, and `AuthorizationMetadataResponseTests.cs`.

- [x] Prove the new configuration mutates the resolver assembly, rather than only the Backend response factory.
- [x] Exercise nearest grant and nearest default independently, explicit strategy overrides, ancestor traversal, empty action sets, and leaves with no applicable claim-set association.
- [x] Assert complete semantic permissions and valid claim-to-authorization references, including reuse of equivalent authorizations and separation of distinct authorizations under forced hash collisions.
- [x] Preserve deterministic response ordering and ID allocation where those are existing contracts, and verify the input hierarchy is not modified.
- [x] Reuse the existing property scenarios with reproducible seeds and bounded generation if mutation runs reveal nondeterministic failures. Keep the expected permission model independent of the implementation traversal.

**Acceptance:** no actionable survivor changes effective permissions, default/override precedence, or authorization references. Property-test results are reproducible. Existing CMS Backend hierarchy-management tests remain available through its original exploratory configuration.

### Area 3 — DMS authorization SQL and page filtering

**Scope:** DMS Plans `RelationalAuthorizationPlanner.cs`, `PageDocumentIdSqlCompiler.cs`, `NamespacePrefixSqlHelper.cs`, `OwnershipTokenSqlHelper.cs`, and `AuthorizationParameterBudget.cs`. Expand to relationship-specific planner/compiler files after this initial cluster is triaged.

**Existing tests:** `RelationalAuthorizationPlannerTests.cs`, `PageDocumentIdSqlCompilerTests.cs`, `PageDocumentIdSqlCompilerOwnershipTests.cs`, `NamespaceAuthorizationSqlCompilerTests.cs`, `OwnershipAuthorizationSqlCompilerTests.cs`, and `AuthorizationParameterBudgetTests.cs`.

- [x] Exercise namespace, relationship, ownership, and supported composed strategies, including unsupported or malformed shapes that must not become unrestricted access.
- [x] Assert empty ownership-token collections exclude all records and bind no unused token parameters; distinguish null stored ownership from a matching token.
- [x] Check equivalent authorization/filter predicates in page and total-count SQL, with the intentional differences in pagination and parameter inventories.
- [x] Verify parameter names, values, types, ordering where contractual, escaping, and composition at SQL Server's usable parameter ceiling: below, equal to, and above the limit. Test PostgreSQL's distinct parameter shapes as well.
- [x] Strengthen fragment-only assertions where a mutation could preserve one expected fragment while omitting another required predicate or introducing an extra branch.

**Acceptance:** actionable survivors affecting authorization scope, query/count agreement, or parameter budgets are killed for both dialects. Stryker mutates the C# generator; it does not systematically rewrite the SQL grammar inside a string. Database integration tests remain the SQL-execution backstop.

**Verification:** fresh seven-file reports with isolated golden output improve from 79.32% to 82.95%, confirming all 63 intended assertion improvements with no lost kills. The three remaining timeouts introduce nonterminating loops. All 2,745 Plans tests pass. Cache capacity/concurrency and structural-collision limitations are documented in `STRYKER.md`.

### Area 4 — DMS transactional write execution

**Scope:** DMS Backend `DefaultRelationalWriteExecutor.cs`, `RelationalWriteFirstPhase.cs`, `RelationalWriteSecondCommandPhase.cs`, `RelationalWriteGuardedNoOp.cs`, and behavior-bearing files under `Etag/`.

**Existing tests:** `DefaultRelationalWriteExecutorTests.cs`, `CompositeRelationalWriteFirstPhaseTests.cs`, `CompositeRelationalWriteAuthorizationOnlyCommandTests.cs`, `CompositeRelationalWriteDmlCommandTests.cs`, and `RelationalWriteGuardedNoOpTests.cs`.

- [x] Begin with the executor and ETag/no-op helpers; then add the composite command phases. The executor's sequential test seams do not by themselves qualify the production composite-command implementation.
- [x] Verify target capture fixes the create/update branch for the attempt and that all required work uses the attempt's session.
- [x] Challenge matching, stale, wildcard, and absent-target preconditions; assert whether authorization, merge, or persistence should occur for each result.
- [x] Assert proposed authorization precedes disclosure of missing document references, while preserving the intended precedence of descriptor/type validation.
- [x] Verify denial and pre-commit failures roll back and do not persist or return success; commit exceptions do not enter a rollback handler intended for pre-commit work.
- [x] Exercise no-op versus applied-write paths, valid lock proof, committed ETag/content version, and success-side effects only after successful commit.
- [x] Use recording doubles and explicit async signals for required ordering. Do not use longer delays to stabilize tests.

**Progress:** the eleven-file scope improved from 66.74% to 74.22%, with 36 intended kills. Complete command-order and structured-claim regressions pass; Stryker rolls back the main executor method after instrumentation compile errors, so normal orchestration tests remain essential. See `STRYKER.md`.

**Acceptance:** critical transaction, authorization-precedence, and precondition mutants are killed. Evidence covers both executor orchestration and the composite phases, with no success result before commit.

### Area 5 — CMS client admission and token grants

**Scope:** CMS OpenIddict `Services/ClientAdmissionPolicy.cs` and `Services/OpenIddictTokenManager.cs`.

**Existing tests:** `ClientAdmissionPolicyTests.cs`, `OpenIddictTokenManagerTests.cs`, and `OpenIddictUnapprovalTokenRevocationTests.cs`.

- [x] Start with the pure admission policy; expand to token grant, application recheck, introspection, and revocation behavior in the token manager after baseline timing is known.
- [x] Exercise approved, unapproved, unknown, and missing-API-client-row states; administrative-role membership; configured-role collisions; and empty or non-administrative registered scopes.
- [x] Distinguish omitted/empty requested scope from a nonempty request with no allowed scopes. Assert the granted intersection and agreement between token claims and the response.
- [x] Assert rejected credentials, invalid scope, and changed application identity/approval/secret do not reach token minting or storage.
- [x] Cover the manager's required revalidation around issuance, including repository rejection after a later state change; do not claim unit doubles prove database locking behavior.
- [x] Use deterministic clock and signing/repository fakes. Diagnose timing-sensitive or expensive tests before excluding any; retain tests that assert observable security behavior.

**Verification:** the full two-file run improved from 65.72% to 72.96%, with 23 intended kills and no timeouts. The pure policy has no surviving executable mutants. The full CMS suite passes 7,327 tests with 16 existing environment skips. Signing uses isolated keys and broad validity windows; production still reads `UtcNow` directly, as documented in `STRYKER.md`.

**Acceptance:** no actionable survivor admits a prohibited client, broadens granted scopes, or stores a token after rejection. Any separate Keycloak behavior remains outside this target and retains its existing tests.

### Area 6 — DMS profile filtering and write preservation

**Core scope:** `Profile/ReadableProfileProjector.cs`, `Profile/WritableRequestShaper.cs`, `Profile/ProfileVisibilityClassifier.cs`, `Profile/CreatabilityAnalyzer.cs`, and `Profile/ProfileDataValidator.cs`.

**Backend scope:** `Profile/RelationalWriteProfileMerge.cs`, `Profile/ProfileCollectionWalker.cs`, and `Profile/ProfileBindingClassificationCore.cs`.

**Existing tests:** Core `Profile/ReadableProfileProjectorTests.cs`, `Profile/WritableRequestShaperTests.cs`, `Profile/CreatabilityAnalyzerTests.cs`, and `Profile/ProfileDataValidatorTests.cs`; Backend `Profile/RelationalWriteProfileMergeSynthesizerTests.cs`, `Profile/ProfileCollectionWalkerTests.cs`, and related binding/collection tests.

- [x] Establish independent Core and Backend baselines, starting with read projection and then stored-data preservation.
- [x] Exercise include/exclude member rules at root, nested object, collection, and extension levels. Verify required identity and server metadata survive appropriate projection.
- [x] Distinguish omitted, explicitly null, empty, visible, and hidden members/scopes. Verify shaping does not mutate the original request.
- [x] Assert hidden stored values and rows are retained by writes; preserve decisions must emit the stored row where omission would delete it.
- [x] Exercise collection matching by semantic identity, ancestor context, creatability restrictions, and changes confined to visible data.
- [x] Verify a three-level descriptor ancestor cache miss with complete current/merged row assertions; confirm the raw-versus-canonical ancestor-selection mutants are killed.
- [x] Verify profile-policy failure occurs at the intended point relative to proposed authorization and does not reach DML.

**Verification:** Core improved from 66.40% to 83.14%, with 144 intended kills; all 10,135 normal tests pass. Backend improved from 57.14% to 60.42%, with 45 intended kills; all 4,898 normal tests pass. The review follow-up's nested descriptor merge kills both ancestor-selection survivors at merge line 2929 and 17 additional mutations, with no lost kills in the full target rerun. Complete row preservation, owner-scope selection, two-descriptor identity, and sibling-order regressions are verified. Concrete remaining coverage and instrumentation limits are documented in `STRYKER.md`.

**Acceptance:** both targets have complete reports; actionable data-exposure, unintended-deletion, and creatability mutants are killed. Assert the complete projected document or merged row set where possible, not only its count.

### Area 7 — DMS write-plan bindings and batching

**Scope:** DMS Plans `PlanWriteBatchingConventions.cs`, `WritePlanCompiler.cs`, `KeyUnificationWritePlanCompiler.cs`, `SimpleInsertSqlEmitter.cs`, `SimpleUpdateSqlEmitter.cs`, and `SimpleDeleteSqlEmitter.cs`.

**Existing tests:** `PlanWriteBatchingConventionsTests.cs`, `Given_WritePlanCompiler_*`, `WritePlanCompilerMssqlContractTests.cs`, and the `Simple*SqlEmitterTests.cs` fixtures.

- [x] Use `PlanWriteBatchingConventions.cs` as the initial tooling pilot.
- [x] Exercise row widths 0, 1, exactly the usable dialect parameter ceiling, and one above it; include division remainders and row-cap boundaries. Assert both returned limits and batch-size arithmetic.
- [x] Verify stored/writable column selection, required keys and precomputed bindings, exact column-to-parameter alignment, and omission of non-writable aliases.
- [x] Challenge key-unification producer accounting, reference-derived sources, stable collection identity, and the choice between in-place updates and collection merge/delete behavior.
- [x] Assert complete SQL and binding contracts for both dialects where their shape is intentional; verify key predicates and that update assignments exclude keys.

**Verification:** the fresh six-file pair with isolated output improves from 68.60% to 69.48%, with three intended required-locator kills and no lost kills or timeouts. All 2,745 normal Plans tests pass. The full target confirms the batching pilot's 14 kills; key-unification instrumentation limitations and survivor dispositions remain documented in `STRYKER.md`.

**Acceptance:** batching pilot and full declared scope each have their own recorded results; the pilot score is never presented as the area's score. All actionable binding, key-predicate, and batching-boundary mutants are killed.

**Requalification complete:** the results above use identical per-run golden-output isolation on both sides and replace the earlier provisional reports.

### Area 8 — DMS relational identity and constraint derivation

**Scope:** DMS Model `SetPasses/KeyUnificationPass.cs`, `SetPasses/ReferenceConstraintPass.cs`, `SetPasses/TransitiveIdentityMutabilityPass.cs`, `SetPasses/ValidateForeignKeyStorageInvariantPass.cs`, and `Schema/JsonSchemaRequirednessConventions.cs`.

**Existing tests:** `KeyUnificationPassTests.cs`, `KeyUnificationReferenceFieldGroupingTests.cs`, `ConstraintDerivationRelationalModelSetPassTests.cs`, `CoreReferenceRequirednessTests.cs`, `ExtensionReferenceRequirednessTests.cs`, `ForeignKeyStorageValidatorTests.cs`, and `RelationalModelSetPassesTests.cs`.

**Verification:** fresh isolated-output reports confirm 328 → 340 assertion-based kills, all twelve intended improvements, and no lost kills. The native score is 61.76% → 55.68% because timeouts decrease from 58 to eight; 46 timing outcomes become survivors and four are now killed by the new cases. Only eight known loop mutations still time out. All 1,048 Model tests pass. Requiredness instrumentation and naming-collision limits remain documented in `STRYKER.md`.

- [x] Start with requiredness and presence-gating decisions, then expand to cross-resource passes.
- [x] Exercise required/optional references, `x-nullable`, unified canonical columns, non-writable aliases, synthetic/reference presence gates, and storage-safe foreign-key endpoints.
- [x] Challenge extension/base-resource handling, composite identity column ordering, and reference constraints across multiple resources.
- [x] Exercise identity mutability propagation through multiple hops, non-identity references, abstract targets, cycles, and disconnected immutable resources.
- [x] Assert structural model invariants in addition to golden output. Classify loop-induced timeouts separately from assertion-based kills.

**Existing coverage retained:** Core/extension requiredness fixtures cover optional, required, and `x-nullable` references; abstract-reference fixtures check ordered identity columns and cascade actions in both dialects; SQL Server pruning fixtures exercise mutable cycles and disconnected graphs.

**Acceptance:** actionable nullability, identity, and constraint mutants are killed and model output remains deterministic. Do not change `RelationalMappingVersion` for test work. Any discovered production mapping fix is a separate change subject to the repository's release/reprovisioning policy.

### Area 9 — DMS decimal, equality, and query validation

**Scope:** DMS Core `Validation/DecimalValidator.cs`, `Validation/EqualityConstraintValidator.cs`, `Validation/ResourceQueryFilterValidator.cs`, `Middleware/ValidateDecimalMiddleware.cs`, and `Middleware/ValidateEqualityConstraintMiddleware.cs`.

**Existing tests:** `Middleware/ValidateDecimalMiddlewareTests.cs`, `Middleware/ValidateEqualityConstraintMiddlewareTests.cs`, and `Validation/ResourceQueryFilterValidatorTests.cs`. Add direct validator fixtures when they make the boundary contract easier to express.

- [x] Use `DecimalValidator.cs` as the first pilot. Existing decimal middleware examples cover `10`, `100000`, `1e400`, and `null`; verify exact allowed endpoints and immediately outside them on both sides.
- [x] Cover different total-digit/decimal-place combinations, unmatched paths, invalid values, and multiple invalid array matches. Assert exact error paths and aggregation, not only a 400 status or title.
- [x] Challenge equality constraints with identical and conflicting values, missing paths, multiple matches, and multiple constraints contributing errors to the same path. Follow the supported schema contract for absence/null cases.
- [x] Exercise recognized/unrecognized query fields, field-name matching, excluded/reserved parameters, invalid typed values, and multiple errors for one path.
- [x] Assert rejected input prevents downstream execution and accepted boundary input continues the pipeline.

**Acceptance:** actionable comparison-boundary, error-aggregation, and validation-bypass mutants are killed. Add tests for observable validation rules; do not invent unsupported constraints merely to increase the score.

### Area 10 — DMS DDL and tracked-change triggers

**Scope:** DMS DDL `TrackedChangeTriggerBodyEmitter.cs`, `AuthTriggerBodyEmitter.cs`, `MssqlDialect.cs`, and `PgsqlDialect.cs`.

**Existing tests:** `TrackedChangeTriggerBodyEmitterTests.cs`, `MssqlDialectTests.cs`, `PgsqlDialectTests.cs`, `CoreDdlEmitterTests.cs`, and `RelationalModelDdlEmitterTests.cs`.

- [x] Start with nullable join selection and dialect helpers, then expand to trigger emission and authorization-hierarchy trigger bodies.
- [x] Verify nullable descriptor/person joins retain rows, non-nullable joins use the intended form, and old/new image selection differs correctly for tombstones and key changes.
- [x] Assert quoting/escaping for identifiers and literals, composite key column alignment, foreign-key action rendering, and the SQL Server mapping of logical `Restrict` to `NO ACTION`.
- [x] Exercise authorization hierarchy insert/update/delete emission, including removal of old ancestor relationships and insertion of new relationships.
- [x] Strengthen output assertions where a single matching fragment leaves required joins, predicates, actions, or emitted statements unobserved. Use complete expected output for stable contracts.

**Verification:** the final four-file run reports 89.22% with no timeouts. Baseline timing controls separate five pre-existing kills from 42 new assertion-based kills; no prior kills are lost. All 1,504 DDL tests pass with one existing skip. Exact schema escaping, nullable join selection, and boundary contracts are verified; `STRYKER.md` records native scores, focused controls, and remaining metadata-validation limits.

**Acceptance:** actionable emitter-branch and contract-output mutants are killed for both dialects. Retain existing database integration tests as validation of executable SQL; no database-backed mutation loop is required for this phase.

## Shared test-improvement and verification loop

Apply this sequence to every area and to each smaller cluster within an area:

1. Run the owning unit-test project normally in Release. Diagnose failures before interpreting mutation output.
2. Capture a fresh baseline for the declared source scope. Confirm source selection, discovered tests, report completeness, and runtime.
3. Triage `Survived` and `NoCoverage` by behavior. Investigate `Timeout` separately; distinguish compile errors, ignored mutants, and tool limitations from assertion-based kills.
4. Strengthen the nearest existing tests or add focused regression tests. Prefer complete permission results, command bindings, documents, row sets, and required operation order over private implementation assertions.
5. Follow repository conventions: NUnit, FluentAssertions, FakeItEasy where appropriate, `Given_` fixtures and `It_` methods. Format changed C# with `dotnet csharpier format <file-or-directory>`.
6. Run the relevant normal tests, then rerun Stryker for the selected production files. Verify the same mutation by file, location/content, operator, and replacement, not just ID.
7. Run the full named target after the cluster is complete. Record the result and run the owning normal test project again if broader test changes or unresolved concerns require it.

Equivalent mutants, unreachable branches, defensive cases outside the supported input contract, and incidental diagnostic text may remain with concrete explanations. Do not rewrite production code or add brittle assertions solely to kill them. A real production defect gets a separate fix with a failing regression test.

Example direct pilot commands, after adding the named configuration and aligning tools:

```bash
cd src/dms/core/EdFi.DataManagementService.Core.Tests.Unit
dotnet tool restore
dotnet test EdFi.DataManagementService.Core.Tests.Unit.csproj --configuration Release
dotnet stryker --config-file stryker-validation.json --mutate "Validation/DecimalValidator.cs"
```

Remove the `--mutate` override to run the full named target after completing its test improvements.

## Completion checklist

- [x] All three manifests resolve one qualified Stryker version.
- [x] All eleven named targets select their intended assembly and files, covering all ten areas.
- [x] CMS Claims and OpenIddict are independently instrumented; both DMS profile assemblies and write execution are included.
- [x] Every target has fresh full-scope before/after results, runtime evidence, and a concise summary of findings.
- [x] Behavior-specific acceptance criteria are met; remaining survivors/timeouts/tool limitations have concrete dispositions.
- [x] Ordinary unit tests and formatting checks pass for changed code.
- [x] `STRYKER.md` documents working commands and results for all eleven targets; native reports remain outside source control.

## References

- [Stryker.NET repository](https://github.com/stryker-mutator/stryker-net)
- [Configuration and project selection](https://stryker-mutator.io/docs/stryker-net/configuration/)
- [Supported mutations](https://stryker-mutator.io/docs/stryker-net/mutations/)
- [Repository instructions](AGENTS.md)
- [CMS instructions](src/config/AGENTS.md)
