# DMS and CMS mutation testing

Run Stryker.NET directly from the owning unit-test directory. Named configurations
select one production assembly and a behavior-focused source scope. The five existing
`stryker-config.json` files remain available for exploratory runs and are unchanged.

These configurations run manually. PR CI runs the ordinary NUnit tests, including
the regression tests added through this work; it does not invoke Stryker or enforce
a mutation-score gate.

The DDL configuration allows an additional 100 seconds and a timeout ratio of 3.0
for its expensive golden fixtures. This budget was verified with the full target;
other targets retain their existing timing settings.

## Prerequisites and commands

- .NET 10 SDK and access to the repository's configured NuGet feeds.
- Restore local tools with `dotnet tool restore`. All three tool manifests pin
  `dotnet-stryker` 5.0.0. Verify with `dotnet tool list --local` in the directory used
  for the run; `--version` is a dashboard option, not a tool-version query.
- Run the owning test project normally in Release before interpreting mutation results.
- Leave `UPDATE_GOLDENS` unset during mutation runs; expected fixtures are read-only inputs.
- Run one Stryker process at a time in this checkout. `--concurrency 2` limits the
  workers inside that process; adjust for available resources.

From a test directory in the table below:

```bash
dotnet tool restore
dotnet test --configuration Release
dotnet stryker --config-file stryker-validation.json --concurrency 2 --break-on-initial-test-failure
```

Replace the configuration filename for the intended target. The named configurations
use Release, net10.0, VSTest, Standard mutations, per-test coverage, and native HTML,
JSON, and progress reporters. High/low/break thresholds are 80/60/0: a zero exit code
does not establish adequate tests. Grant/deny decisions, data preservation, validation,
and generated contracts determine which survivors need stronger assertions.

To investigate a smaller source scope without filtering away the owning tests:

```bash
dotnet stryker --config-file stryker-validation.json --concurrency 2 --break-on-initial-test-failure \
  --mutate 'Validation/DecimalValidator.cs'
```

The override replaces the declared mutation scope. Record that result as a focused
investigation, then rerun without the override to establish a full-target result.

## Targets

Paths are relative to the repository root. Area 6 has two targets because its behavior
spans Core and Backend assemblies. The results below separate verified improvements
from instrumentation and remaining coverage limits.

| Area / target | Owning unit-test directory | Configuration | Status |
| --- | --- | --- | --- |
| 1 / `dms-request-auth` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-request-auth.json` | Qualified; six actionable survivors killed |
| 2 / `cms-permission-inheritance` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-claims.json` | Qualified; four actionable mutations killed |
| 3 / `dms-query-auth` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-query-auth.json` | Verified; sixty-three intended improvements; cache limitations documented |
| 4 / `dms-write-execution` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-write-execution.json` | Verified; thirty-six intended mutations killed; instrumentation limits documented |
| 5 / `cms-token-admission` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-openiddict.json` | Qualified; twenty-three intended mutations killed |
| 6 / `dms-profile-core` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-profile.json` | Verified; one hundred forty-four intended mutations killed; limitations documented |
| 6 / `dms-profile-backend` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-profile.json` | Verified; twenty-six intended mutations killed; limitations documented |
| 7 / `dms-write-plans` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-write-plans.json` | Verified; three required-locator mutations killed |
| 8 / `dms-model-constraints` | `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit` | `stryker-model-constraints.json` | Verified; twelve intended assertion improvements; timeout variation documented |
| 9 / `dms-validation` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-validation.json` | Qualified with documented compile limitations |
| 10 / `dms-ddl` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit` | `stryker-ddl.json` | Verified; forty-two intended assertion improvements; timing controls complete |

## Reading and recording results

Use the output directory printed by the current invocation. Native reports live at
`StrykerOutput/<timestamp>/reports/mutation-report.{html,json}` and are ignored by Git.
Failed runs, absent reports, empty mutant sets, and incorrect source selection do not
establish baselines. Check the selected assembly and reported source files before
recording the native mutation score.

For a specific report path, inspect surviving, uncovered, and timed-out mutations:

```bash
jq -r '
  .files | to_entries[] | .key as $file | .value.mutants[]?
  | select(.status == "Survived" or .status == "NoCoverage" or .status == "Timeout")
  | [$file, .location.start.line, .mutatorName, .replacement, .status] | @tsv
' StrykerOutput/<timestamp>/reports/mutation-report.json
```

Record source location, original expression and replacement when comparing runs;
mutant IDs are local to a report. `Killed` means a test detected the change. Investigate
`Timeout` separately, especially loop changes. `CompileError` and `Ignored` are not
assertion-based kills. Equivalent changes, unsupported input states, and incidental
diagnostic messages may remain only with a concrete explanation. Keep mutations of
SQL, claim names, and response contracts enabled.

After changing tests, run the relevant normal tests, format changed C# with
`dotnet csharpier format <file>`, rerun the focused mutation scope, and finally run the
full named configuration. Refresh baselines after changing the tool or source scope.
Do not change production behavior merely to improve the mutation score.

For comparable timeout results, run before/after pairs sequentially on a lightly loaded
host. If a non-loop mutation times out, inspect a focused rerun before calling it a
detection. Stryker exposes `additional-timeout` and `timeout-ratio` as configuration-file
options; record any override alongside the result. See the [timeout configuration](https://stryker-mutator.io/docs/stryker-net/configuration/#additional-timeout-number).

## Golden-output isolation and requalification

The first DDL after-run exposed shared golden-output directories: parallel Stryker
workers could delete or overwrite another worker's `actual/` files. Plans and Model
fixtures also used shared paths. The results below replace their earlier provisional
reports, including unexplained null-guard kills and golden-test regressions. Those
earlier observations are not evidence of assertion improvements.

Golden fixtures now use unique temporary output directories, retain their original
read-only inputs/expected files, and clean up their owned output after comparison.
DDL output remains available through the fixture's tests and is removed in
OneTimeTearDown. Runner/comparer defaults retain the existing explicit `actual/`
workflow; automated golden fixtures supply an isolated directory. A regression
checks that a second run cannot overwrite a first run's deliberately different SQL,
and that each comparison sees only its own output.

Release validation with isolation passes DDL 1,504 tests plus one existing skip,
Model 1,048, and Plans 2,745. Fresh full-target comparisons and DDL timing controls
are complete. Both sides receive identical isolation support; baseline behavioral
tests are restored from `origin/main`
(`6a7a262d8`, with no production C# difference from the PR's merged base).
Earlier native reports remain available for diagnosis and are not silently discarded.

The fresh Model pair confirms 328 → 340 assertion-based kills, with all twelve intended
improvements and no lost kills. Its native score is 61.76% → 55.68% because baseline
timeouts fell from 58 to eight: 46 became survivors and four became kills from the new
cases. All four pass-entry null-guard removals now survive in the full after-run.
The remaining eight timeouts introduce nonterminating loops. A reduction in timeouts
alone is not a test improvement; inspect assertion kills and timeout counts alongside
the native score.

## Results

CMS Backend's test project references Claims directly because Stryker 5.0.0 could not
select it through a transitive reference. The corresponding PostgreSQL and SQL Server
integration lock files record that project reference. A forced locked restore of the
complete CMS solution passes; no package versions changed. The original project-selection
failure produced no baseline.

Implementation started from `dfbd0530896805cd3415b679dc62510a9871268a` on 2026-11-16.
Tool restoration and local-manifest resolution were checked at the repository root,
DMS Core test directory, and CMS Backend test directory: all resolve 5.0.0. Installed
SDK: 10.0.102; runtime: 10.0.2. All eleven full targets are qualified, with the
limitations below. Together they confirm 364 additional assertion-based kills.

| Area | Native score before → after | Confirmed new assertion kills | Runtime before / after | Reports, relative to owning test directory |
| --- | --- | --- | --- | --- |
| 1. Request authorization | 63.31% → 67.63% | 6 | 05m43s / 05m45s | `StrykerOutput/2026-11-16.17-20-46/reports/` → `StrykerOutput/2026-11-16.17-29-57/reports/` |
| 2. CMS permission inheritance | 83.33% → 91.67% | 4 | 06m25s / 06m20s | `StrykerOutput/2026-11-16.17-39-28/reports/` → `StrykerOutput/2026-11-16.17-47-25/reports/` |
| 3. Query authorization | 79.32% → 82.95% | 63 | 29m15s / 23m48s | `StrykerOutput/2026-11-16.23-35-01/reports/` → `StrykerOutput/2026-11-16.23-49-50/reports/` |
| 4. Transactional writes | 66.74% → 74.22% | 36 | 04m17s / 11m09s | `StrykerOutput/2026-11-16.18-50-04/reports/` → `StrykerOutput/2026-11-16.22-36-19/reports/` |
| 5. CMS token admission | 65.72% → 72.96% | 23 | 11m46s / 14m47s | `StrykerOutput/2026-11-16.18-21-43/reports/` → `StrykerOutput/2026-11-16.19-33-56/reports/` |
| 6a. Core profiles | 66.40% → 83.14% | 144 | 06m29s / 12m26s | `StrykerOutput/2026-11-16.19-24-29/reports/` → `StrykerOutput/2026-11-16.22-38-23/reports/` |
| 6b. Backend profiles | 57.14% → 59.04% | 26 | 05m01s / 08m55s | `StrykerOutput/2026-11-16.19-55-14/reports/` → `StrykerOutput/2026-11-16.22-46-36/reports/` |
| 7. Write plans | 68.60% → 69.48% | 3 | 27m36s / 28m24s | `StrykerOutput/2026-11-17.00-02-08/reports/` → `StrykerOutput/2026-11-16.23-59-38/reports/` |
| 8. Relational model | 61.76% → 55.68%; timeouts 58 → 8 | 12 | 35m54s / 40m57s | `StrykerOutput/2026-11-16.22-57-24/reports/` → `StrykerOutput/2026-11-16.23-01-24/reports/` |
| 9. Validation | 65.85% → 71.95% | 5 | 04m49s / 04m58s | `StrykerOutput/2026-11-16.16-53-30/reports/` → `StrykerOutput/2026-11-16.17-05-10/reports/` |
| 10. DDL | 87.27% → 89.22%; timeouts 30 → 0 | 42 | 1h03m51s / 54m52s | `StrykerOutput/2026-11-16.22-57-23/reports/` → `StrykerOutput/2026-11-17.00-44-05/reports/` |

**Tooling pilots (separate source scopes)**

| Pilot | Observation | Runtime | Native reports, relative to owning test directory |
| --- | --- | --- | --- |
| Core `DecimalValidator.cs` | 3 executable mutants killed; **31 compile errors roll back `Validate`**. The reported 100% does not qualify decimal comparisons. | 4m56s | `StrykerOutput/2026-11-16.16-47-59/reports/`; confirmed in full validation rerun `2026-11-16.17-05-10` |
| Plans `PlanWriteBatchingConventions.cs` | 93.33% in both pilot runs: 14 killed, one defensive survivor, one compile error, five ignored. Full write-plan results remain separate. | 4m31s / 4m36s | `StrykerOutput/2026-11-16.16-58-30/reports/` → `StrykerOutput/2026-11-16.17-10-23/reports/` |

The batching pilots predate golden-output isolation; the fresh full write-plan pair
confirms the same 14 kills and remaining statuses. The pilots established .NET 10,
NUnit/VSTest, Release builds, and native HTML/JSON generation using Stryker 5.0.0.

Request authorization, Claims, validation, and the batching pilot used concurrency 2.
The initial isolated DDL pair used concurrency 8. Its final full rerun uses concurrency
4, `DOTNET_PROCESSOR_COUNT=8`, `additional-timeout: 100000`, and `timeout-ratio: 3.0`;
the focused timing controls use concurrency 2 and `DOTNET_PROCESSOR_COUNT=4`. Model,
query authorization, and write plans use concurrency 4 and `DOTNET_PROCESSOR_COUNT=4`.
Other full runs used concurrency 4 or 8. Only one Stryker process runs in each checkout
at a time. Runtime observations include concurrent load on a 24-core host and are not
performance benchmarks.

Release suite validation: Core 10,135 passed; Backend 4,897; Plans 2,745; Model 1,048;
DDL 1,504 with one existing skip; CMS Backend 7,327 with 16 existing environment skips.
All 58 changed C# files pass CSharpier. No production behavior, mapping version, or
CI workflows changed.

Plans reports 2,745 local test executions but 2,718 distinct test IDs: existing numeric
argument cases and named authorization cases reuse IDs. A normal TRX report contains
the same 2,718 IDs as both Stryker after-reports; all new cases are present.

### 1. Request authorization

The full baseline contains 88 killed, 38 survived, 13 uncovered, 16 compile errors,
and 41 ignored mutants in the three declared files. The full rerun has 94 killed,
32 survived, 13 uncovered, the same 16 compile errors and 41 ignored, with no timeouts.
All six actionable survivors below are confirmed killed by matching file, location,
operator, and replacement. The focused
authorization tests passed 154 cases after strengthening the assertions, and the
complete Core suite passed 10,027 tests before the mutation rerun.

Verified changes in `ResourceActionAuthorizationMiddleware.cs`:

- Lines 148, 271, and 461: removing the exception-handler return or changing a
  no-strategies failure to success can continue the pipeline while retaining a 500
  response. Provider-failure and GET/PUT/DELETE strategy-metadata tests now assert no
  continuation, in addition to their response contracts.
- Lines 113 and 246: bypassing the no-matching-claim rejection changes the response
  to an action-specific denial. The unmatched-claim test now pins the generic error
  body, empty policy output, headers, and zero continuations.
- Line 468: reversing the ordinal claim-URI sort survived. A case-variant matching
  catalog now verifies the complete error, distinct URI order, and chosen claim name.

Policy conversion also has explicit Create-only, Update-only, and both-permitted
cases, with distinct complete evaluator arrays. Existing tests cover both denied
(the first middleware refuses before conversion), missing strategy metadata,
cancellation propagation, and preservation of the shared authenticated record.

The 45 remaining survived/uncovered mutations retain these dispositions:

- Ordinary entry/retrieval/debug logging and `Debug.Assert` behavior are diagnostic;
  the latter's argument mutations are not executed in Release.
- The POST entry in `_methodToActionNameMapping` is used only for missing-metadata
  diagnostics. POST authorization selects its two action constants directly.
- Removing the early empty-resource-claims return (line 104), or changing its helper
  result to true (line 214), still reaches the empty matching-claims rejection and
  the identical generic 403 without continuation. These are equivalent for callers.
- The resolver's three survivors have the dispositions described below.
- The filter-conversion catch body requires malformed internal strategy evidence
  (for example, a null non-nullable strategy collection). Valid evidence only creates
  evaluator records. Its four uncovered mutations are outside the input contract.
- The resolver result switch is exhaustive over its closed variants; matched-claim
  URIs cannot all be null after matching the non-null resource URI. Their defensive
  throw mutations are unreachable for supported inputs. `UNSUPPORTED` HTTP methods
  terminate in MethodNotAllowedMiddleware before reaching resource authorization.

### Claim-set pilot

The focused resolver run has 17 killed, 3 survived, 6 compile errors, 2 ignored, and
no uncovered or timed-out mutants. Existing tests detect changes to ambiguity,
matching duplicate metadata, unrelated duplicates, scope precedence, literal legacy
names, and case-independent resolution. The remaining changes are equivalent for
supported metadata and current consumers:

- `ClaimSetResolver.cs:65`, removing the empty-candidate early return, still reaches
  `NoMatch` after matching zero records. Enumerating the already-loaded catalog is
  unnecessary work but does not alter authorization.
- Both changes at line 126 (`condition` → `true`, `Length > 0` → `Length >= 0`) return
  `[""]` instead of `[]` when both scope values and the legacy name are absent.
  CMS's `ClaimSetCommandValidator` and copy validator prohibit empty metadata names,
  so either candidate list matches no supported claim set. Every other consumer of
  `GetCandidates` joins the values for diagnostics, producing the same empty text.
  Do not invent an empty-name metadata record or assert a private collection shape
  just to kill these mutants.

The six compile errors include invalid `Count` → `Sum` access and comparisons rolled
back with it. This is a single-file baseline, not a result for the authorization
middleware or the full target.

### 2. CMS permission inheritance

The baseline instruments the Claims assembly and contains 40 killed, 6 survived,
2 uncovered, 6 compile errors, and 24 ignored mutants in the declared two files,
with no timeouts. The full rerun contains 44 killed, 4 survived, no uncovered mutants,
the same 6 compile errors and 24 ignored, and no timeouts. Existing literal
expectations and bounded property scenarios kill
the executable inheritance, default/override-precedence, ordering, ID-allocation,
and collision/deduplication changes. Expected semantic permissions are computed
independently, and the property tests check that the input hierarchy is unchanged.

Four response-model changes are now caught by stronger assertions: returning true for a null
authorization (`AuthorizationMetadataResponse.cs:26`), returning false for the same
instance (line 31), and two `&&` → `||` changes in action equality (line 73).
New tests exercise null, reflexivity, unequal action names/strategies, and casing.
All four changes are confirmed killed by source location, operator, and replacement
in the full rerun. The focused normal run passed 211 tests, and both changed C# files
pass CSharpier.

The other four changes preserve the public contracts:

- `AuthorizationMetadataResolver.cs:180`, default strategy count `> 0` → `>= 0`,
  copies an empty default list over an already-empty strategy list. Permissions and
  authorization references are unchanged.
- `AuthorizationMetadataResponse.cs:61`, additive hash accumulation → subtraction,
  remains commutative, so equal permissions retain equal hashes.
- Line 79, ascending → descending strategy-name order during hashing, still gives
  each equivalent strategy multiset one canonical sequence.
- Line 80, removing strategy contributions to the action hash, increases collisions
  but retains equality correctness. Existing forced-collision tests verify that
  distinct permissions do not merge. Do not assert unequal hashes for unequal values;
  the hash contract allows collisions.

The resolver's six compile errors include invalid `Count` → `Sum` member access and
definite-assignment failures around `out var defaultAction`, plus adjacent mutations
rolled back with them. These are instrumentation limits, not successful test kills.

The first run also warned about four changing test identities in the unrelated
connection-string validator tests: randomized encrypted input appeared in their
discovered names. Those cases now use stable argument display names while retaining
the encrypted values and original assertions. The full rerun has no warnings or
errors, with the full owning test suite and per-test coverage still enabled.

### 3. Query authorization

The fresh seven-file pair confirms 1,374 → 1,437 assertion-based kills, all 63 intended
improvements, and no lost kills. The final report has 187 survivors, 109 uncovered,
461 compile errors, 345 ignored, and three loop timeouts. This replaces the earlier
five-file/expanded-scope comparisons and shared-output observations; it uses the same
full scope on both sides.

The regressions assert complete page and count SQL for direct/transitive paths in both
dialects, exact paging parameters, and no unused count parameters. They reject malformed
custom-view roots, empty paths, invalid direct/intermediate targets, and mismatched
self-person anchors. Empty custom-view collections do not retain unused namespace
parameters or reject an otherwise valid ownership filter. Mixed configuration failures
on both sides of a namespace strategy retain the intended terminal precedence.

Relationship cases cover mixed strategy/subject failures, missing transitive first-hop
bindings, retained person metadata for ineligible create-new subjects, and a shared
update failure when required people views are absent. Cached SQL cases check zero and
negative AUTH1 indices in both overloads/dialects and reuse with equivalent reserved-name
arrays/lists. Existing namespace/ownership parameterization and parameter-ceiling
contracts remain enabled. All 2,745 Plans tests pass.

Nonterminating page-compiler collision loops and the relationship compiler's parameter-
suffix loop are the three remaining timeout detections, distinct from assertion kills.

Page/compiler dispositions:

- Null container/entry guards in the planner, ownership helper, and page compiler
  reject inputs outside their non-nullable contracts. Removing the required-parameter
  guards at page compiler lines 495 and 503 still reaches the corresponding
  parameterization validator's null guard, so neither removal drops a filter. The ownership helper's
  unsupported-kind message mutations preserve rejection of an unsupported enum value.
- Page compiler lines 152 and 247, `<` → `<=`, add one empty outer-loop iteration;
  the inner loop does not execute and no extra element is accessed. Lines 1738–1739
  change only list capacity, not the collected collision diagnostics.
- Reversing the final predicate parameter-name sort at line 139 affects only groups
  that are rejected as duplicate semantic predicates; the diagnostic formatter sorts
  those names again. Conversely, removing that later sort at line 1748 is equivalent
  because the earlier sort already orders the same group. Neither individual mutation
  changes the result. The duplicate-parameter secondary sort at line 303 cannot break
  a tie: case-insensitive grouping already merges every such tie.
- Removing the namespace-list coalescing at line 940 is equivalent because normalization
  always supplies an array. Changes to tie breakers at lines 942, 948, and 952 reorder
  AND predicates only within the same configured position; every predicate and binding
  remains, and ordering between distinct CMS positions is still enforced.
- Page compiler context-label mutations (lines 489, 508, 541) affect internal
  diagnostics. Closed target/filter variants and normalized parameter/cursor invariants
  make defensive fallback messages unreachable. Transitive-person target-null messages
  follow `ValidateTransitivePersonPath`, which already rejects those missing targets.
- Planner line 98's default configured index is overwritten on its only production
  construction path. Classifying an empty strategy list at line 258 produces the same
  empty plan. Every current operation enforces custom-view checks, so the old
  not-enabled arms and default unknown-operation branch are unreachable for supported
  operations. Ownership is the classifier's sole known-but-not-enabled strategy, and
  the planner removes it into an enforced bucket for every current operation. The
  namespace `<=` mutation at line 315 requires two distinct configured entries to share
  a CMS position; callers assign positions from their unique list indices.


Relationship-specific dispositions (source locations refer to the unchanged production files):

- Planner empty-list fast paths (472, 703, 766, 1453) and `Count > 0` → `>= 0`
  changes (647, 928, 977, 1125) retain the same selected subjects or failures. At
  991, failed check-spec creation already returns a null check spec, so removing
  `continue` adds no executable check. Stored planning cannot produce the proposed
  binding failures selected by the conditional at 576. Self-person create-new subjects
  are removed before the anchor choice at 1206, leaving only the existing-target arm.
- Contributor-kind fallbacks select the same Student/Contact/Staff kind that the
  corresponding selector supplies. Successful selectors provide contributors, making
  their `FirstOrDefault` → `First` changes equivalent on supported inputs. Missing
  subject/person and unknown path-kind messages are defensive diagnostics. Changes to
  reason separators and secondary contributor ordering affect failure diagnostics,
  not the selected subjects or grant/deny result.
- Compiler null guards and cache-input validation repeated during normalization can
  survive without allowing invalid SQL. Transitive-path validation at 464 and 1166
  repeats validation on another path in the same compilation. Generated proposed
  parameter names always begin with `relationshipAuthorization_`, so dropping the
  separate `documentUuid`/`resourceKeyId` reservation at 719 cannot create a collision.
  Collision checks at 1847–1875 defend names already uniquely allocated and validated.
  Placeholder claim values (695–702) are used only to compile cached SQL shape; actual
  request claims are bound separately.
- Both caches retain survivors in capacity accounting, concurrent insertion, and
  failed-entry removal (planner 1997–2079; compiler 565–653). These are **remaining
  cache-lifetime/performance coverage gaps**, not equivalent mutants or authorization
  kills. Normal cache reuse, mapping-set isolation, changed claims/shape/parameter
  inventories, and equivalent reserved-name collections are exercised through public
  methods. No timing sleeps or tests of private counters were introduced.
- Cache hash-contribution removals preserve full structural equality and merely add
  collisions. Removing a structural equality comparison is different: the unchanged
  hash precheck masks it unless two unequal keys have the same hash. Those survivors
  remain an explicit **collision-path coverage limitation**, not a proof of equivalence.
  These runs do not establish exhaustive cache-key collision safety.

### 4. Transactional write execution

The eleven-file target improves from 321 to 357 assertion-based kills: 36 intended
improvements, 99 survivors, 25 uncovered, 389 compile errors, 109 ignored, and no
timeouts. No previously killed mutation regressed. The complete Backend Release
suite passes 4,897 tests, including the profile regressions described below.

Regressions protect these observable contracts:

- No-op comparison includes collection row identity and root locator values; stream
  ETags reject nonpositive versions before composition.
- Target capture, stored relationship authorization, descriptor hydration, and deferred
  preconditions share the attempt's session. Combined capture/authorization commands
  at the parameter ceiling remain combined; one over the ceiling authorizes separately
  before hydration. Both dialects are covered.
- Missing PUT targets, absent claims, descriptor/type validation, malformed namespace
  payloads, and missing document references retain their required failure precedence.
- Custom-view order and deferred-value provenance are checked in DML mode as well as
  authorization-only mode. Missing insert IDs and missing/duplicate persistence
  observations cannot return success.
- Mixed inline/reserved collection keys and create-command UUID parameters count toward
  command limits. SQL Server structured claims carry the complete table-valued list and
  provider metadata (`dms.BigIntTable`/`Id`); the callback reaches the supplied provider.
- A structured-claim relationship check runs before DML on the same session. Successful
  standalone authorization must continue to persistence. A relationship denial with no
  emitted custom-view probe does not invent a required probe.

Stryker rolls back **all mutations in `DefaultRelationalWriteExecutor.ExecuteAsyncInternal`**
after generated-code assignment errors: 148 executor compile errors. Consequently,
the native score does not qualify commit/rollback or authorization order inside that
method. Ordinary orchestration regressions cover captured-target branching, matching,
stale/wildcard/absent-target preconditions, rejection before persistence, rollback before
commit, commit-exception handling, committed ETags, and success-side effects after commit.
The composite-phase mutations supply independent evidence for the production command
path. Production code was not rewritten to work around instrumentation.

Remaining dispositions:

- Optional writer-wait telemetry and its recording call (executor 57/680) are an
  observability coverage limitation. Logger-factory fallback, synchronization-context
  capture, and non-nullable argument guards do not establish write-correctness gaps.
- The enqueue success guard (700–701) is equivalent for executor-produced outcomes:
  only an applied write has a non-null persisted result.
- The original non-null standalone-denial body at second phase 668 is unreachable:
  `ExecuteStandaloneRelationshipAsync` returns null after success and throws on failure
  for relationship-only calls. Its inverted condition at 667 is now killed through the
  success/persistence case; the unused original body remains a cleanup observation.
- `EtagComposer.cs` and `ServedEtagComposer.cs` produce no Standard-level mutants.
  Inclusion in the configuration alone is not mutation coverage. Logging, defensive
  unsupported intermediate shapes, and redundant validation remain outside the claimed
  improvements; compile errors are never counted as assertions.

### 5. CMS token admission

The full OpenIddict baseline contains 209 killed, 63 survived, 46 uncovered, 87
compile-error, and 47 ignored mutants across the admission policy and token manager.
The policy retains its pilot result. Fifty of the manager's 85 compile errors are
block removals; others include impossible type/enum intersections and pattern-variable
assignment failures. These are not detected behavioral regressions. The warnings about
`SigningKeyBearerEvents` concern a file outside this target.

The new manager cases address these concrete gaps (production source lines):

- 196 and 390: independently missing/whitespace client ID or secret; grant responses
  distinguish missing credentials, and rejected requests do not query credentials,
  mint, or store a token. Revocation fixtures cover each missing component.
- 256: tabs around comma-separated scope names, asserting the exact granted scopes
  in both response and token through the existing `ScopeGrant` fixtures.
- 526 and 573: introspection preserves a verifier outage as temporarily unavailable,
  and a signed token without a JWT ID is inactive without querying token status.
- 1244: configured namespace, education-organization, and claim-set mappings reach
  the issued JWT rather than being silently replaced by an empty mapper list.
- 118–166 and 834–852: production-certificate signing and revocation use the configured
  password/key, public-certificate revocation takes the unencrypted loading path, and
  a wrong password cannot store a token. Missing encryption configuration or a missing
  database private key also prevents storage. Revocation assertions require the
  repository call for the authenticated application and exact JWT ID; a generic
  completed response would not establish successful verification.

All 28 cases in the changed fixtures pass normally. After merging `main`, the full
CMS Backend suite passed 7,326 tests with 16 existing environment-dependent skips
in 3m46s. The first full rerun confirms 21 intended mutations changed to killed:
lines 118 (three), 124 (three), 129, 144, 196, 199, 256, 390, 526, 573,
834, 850 (five), and 1244. It contains 230 killed, 61 survived, 27 uncovered,
87 compile-error, and 47 ignored mutations, with no timeouts. Two response-contract
survivors at 349 and 606 now have stronger assertions for `token_type: Bearer` and
space-delimited introspection scope claims. The final full rerun confirms both are
killed, bringing the total to 23 intended kills since baseline: 232 killed, 59 survived,
27 uncovered, 87 compile-error, and 47 ignored mutations, with no timeouts.
Repository and hasher fakes preserve deterministic state transitions;
certificate tests use isolated temporary directories, dispose their keys, and use
broad validity windows without sleeps. The manager reads `UtcNow` directly, so this
work does not claim a newly injectable production clock.

The next full token run (`2026-11-16.19-14-24`) stopped in its initial normal test
run, before mutation, because `Given_a_cancellation_callback_that_waits_for_a_fence`
observed no callback. The fixture awaited a delay canceled by the same token as its
callback; the delay continuation could dispose the callback registration before
cancellation invoked it. It now waits for an explicit callback-finished signal before
disposing the registration, preserving the deadlock/fence assertions without increasing
delays or excluding the test. All 94 job-executor cases pass, and the repaired callback
fixture passed ten consecutive runs (two tests each). The full CMS suite then passed
7,327 tests with 16 existing skips in 3m51s. The fresh full run
`2026-11-16.19-33-56` then passed its initial tests and completed in 14m47s at
72.96%. The failed invocation establishes no baseline.

Token-manager dispositions retained without suppressing mutation types:

- Ordinary logging statements/templates, logging-only conditional text, and diagnostic
  exception wording do not alter admission, revocation, or the public response. The
  token-status exceptions still preserve their dependency category and inner failure.
- `ValidateTokenWithRevocationAsync` (69–83) has no repository call sites. Its static
  service-provider convenience wrapper remains uncovered; this is not evidence about
  the active authentication middleware.
- `Permissions` is non-null in the application contract; authenticated stored secrets
  and the caller-matching token client ID cannot reach their empty fallback branches.
  The private signing helper receives the non-null joined scope string.
- Removing the missing-private-key throw (152) still fails on the absent key, and
  removing the wrapping throw (163) still fails before token storage. Their exception
  wording differs, while the public generic failure and zero storage calls remain.
- The empty-password production signing-certificate branch (124) retains a coverage
  limitation: successful signing is tested with a password-protected private key;
  public-only certificates are tested for revocation. This is not an admission kill.
- The import catch (811–813) follows `DetectFormat`, which already imports the same
  unchanged bytes using the same format. Malformed keys take the earlier unknown-format
  rejection. Resource failures between those imports remain unforced coverage limits.
- Successful verification-key results never consume their empty error message, and
  successful JWT validation requires an audience before its diagnostic `FirstOrDefault`
  call. Key-record initializer strings are replaced by supplied repository/JSON values.
  A null protocol-mapper fallback changing from `[]` to an empty string still adds no
  mapped claims. These mutations do not broaden a grant.
- The missing-certificate-path throw/message (120–121) are uncovered defensive
  configuration branches; the later file loader still rejects that absent path before
  storage. No claim is made that the diagnostic wording is protected.
- `ReadKeyId`'s defensive catch/empty-header returns (1362–1373) precede full JWT
  validation; malformed input is still rejected by that validation. No private parsing
  shape is asserted solely to raise the score.

### Admission-policy pilot

The OpenIddict pilot produced 13 killed, 2 compile-error, and 2 ignored mutations in
`ClientAdmissionPolicy.cs`, with no survivors, uncovered mutants, or timeouts. Existing
tests detect the executable role-configuration, administrative-role, and scope
admission changes. Stryker attempted invalid `scopes.Count` → `scopes.Sum` member
access at line 108 and rolled back its neighboring equality mutation as well.

Executable mutations and coverage in this assembly establish that its assembly-level
`ExcludeFromCodeCoverage` attribute does not prevent this Stryker run. No attribute or
coverage-setting change is needed. This pilot does not establish a token-manager or
full-target result; run the named configuration without the `--mutate` override for
that scope.

### 6a. Core profiles

The five-file target improves from 571 to 715 assertion-based kills: 144 intended
improvements, 112 survivors, 33 uncovered, 176 compile errors, 292 ignored, and no
timeouts. No prior kill regressed. All 10,135 Core Release tests pass.

The new cases assert complete nested object, collection, and extension projections;
identity references and multiple server-generated members; removal of emptied scopes;
and deep-clone independence. Include/exclude filters cover multiple values, casing,
and absent/null/empty data, preserving the distinct read/write missing-value contracts.
Write shaping asserts complete descendant scope-state inventories, parent identities,
request paths, and unchanged inputs for omitted, null, empty, hidden, and populated data.

Creatability cases cover root versus nested identity visibility, stored existence,
new and existing parents, immediate-parent dependencies, multi-level collections and
extensions, and allowed versus blocked siblings in both orders. A child can demote only
its own parent. Negating the still-creatable checks at 391/487 must not stop traversal
before a later blocked sibling; both mutations are now killed.

Definition validation covers recursive server-generated names, missing extension
namespaces, and unknown properties/objects/collections nested under objects,
collections, and extensions. Read/write labels, paths, severity, IncludeAll/IncludeOnly
parents, and complete failure/dependency results are asserted. Repeated IncludeAll
returns at validator 1017/1229/1294 already receive that behavior from their callers.

### Core profile survivor dispositions

The remaining counts include the following distinct groups; they are not all test
failures, and a lower uncovered count does not remove instrumentation limitations.

- `ProfileVisibilityClassifier.cs:214/245`: `IsHidden` is constructed as `Node is null`,
  so changing OR to AND compares equivalent predicates. Calling item-filter resolution
  for non-collections returns null because their canonical paths lack `[*]`; hidden
  scopes have no navigable parent rule. Invalid filter enums and malformed scope paths
  are outside compiled profile metadata.
- `WritableRequestShaper.cs:175/198/417/440`: the schema traversal and classifier use
  the same scope catalog; a known object cannot be a collection and vice versa. The
  final missing-scope sweep at 78/634 is a fallback after recursive emission already
  visits supported non-collection scopes. Collection descendants without concrete
  parent items have no address and are intentionally skipped. Deep identity leaves
  with more than one relative segment in `TryCopyIdentityLeaf` remain an explicit
  coverage limitation; the current reference-identity regressions use direct leaves.
- `CreatabilityAnalyzer.cs:198/264/419/1052/1067/1074`: missing roots, missing catalog
  children, and inconsistent ancestor shapes require incomplete intermediate metadata.
  Identity paths come from root-anchored schema JSON paths, so the non-`$.` fallback
  at line 70 is not exercised by schema-derived input.
- Removing the already-demoted guards at 247/249/403/405 or the breaks after rejection
  at 326/332/388/483/489/546 repeats failed decisions or collects further diagnostics;
  those paths cannot restore a demoted flag. Exhaustive multiplicity/order of redundant
  failure diagnostics is not qualified. Negating the still-creatable checks at 391/487
  is different: it can stop before a blocked sibling; the new order cases kill both.
- Creatability's non-collection/collection `ScopeKind` fallbacks at 368/464/527 agree
  with the enclosing branch. The root update OR mutation at 553 only performs an
  extra scan when a create is already non-creatable; it cannot change false to true.
  Empty dependency-list versus null changes are normalized by the failure factory.
- Validator log levels/counts and message templates are diagnostic. Removing early
  exits after reporting an invalid member can add more errors but does not accept the
  profile. Extension resolution runs independently before member validation; IncludeAll
  child validation returns an empty list, explaining the repeated guards at 941/969.
  Invalid-schema fallbacks remain separate from valid profile input coverage.

### 6b. Backend profiles

The three-file target improves from 783 to 809 assertion-based kills: 26 intended
improvements, 350 survivors, 212 uncovered, 358 compile errors, 368 ignored, and one
loop timeout. No prior kill regressed. The full Backend Release suite passes 4,897 tests.

Binding regressions assert complete visible-present/absent and hidden stored-data
classifications, descendant state application, scope/ancestor mismatch rejection, and
rejection of hidden metadata without a binding. A later separate-table sibling cannot
steal an inlined scope's ownership. Identity cases protect numeric-width normalization,
composite-key ordering/length, unresolved-instance separation, and child scope plus
parent partition matching. Hidden-member expansion for one stored child cannot erase
another child's metadata.

Merge fixtures compare complete current and merged rows: stable identity, parent key,
ordinal, resolved reference/descriptor IDs, and natural-key values. The aligned-extension
fixture reorders visible children while retaining a hidden parent's stored ordinals.
An existing non-creatable inline scope remains updatable. Two descriptor-identity parts
are canonicalized together for request, stored-cache, and omitted-row cache-miss paths;
resetting the builder must not discard the earlier component. Those last cases kill
`RelationalWriteProfileMerge.cs:3170/3280/3318` and raise the final score to 59.04%.

Dispositions and limits:

- The timeout at merge 2390 changes `index += 3` to `index -= 3` in wildcard scanning,
  producing a nonterminating loop. It is separate from assertion-based kills.
- Stryker rolls back `ProfileCollectionWalker.WalkChildren`, `ExtractRequiredInt32`,
  `ExtractRequiredInt64`, and merge `TryBuildStoredDocumentReferenceIdentityJson` after
  generated-code compile errors. Ordinary walker/merge tests remain the backstop;
  helper/index results do not qualify the rolled-back methods.
- Removing hash contributions changes collision frequency, not equality or dictionary
  lookup correctness. Stored physical identity columns are non-null by contract, so
  null-literal identity branches do not justify fabricated rows.
- Root merged-versus-flattened projections read the same immutable physical key.
  Scalar-match and positional descriptor fallbacks can resolve the same IDs under
  valid stored ordinal/count invariants; arbitrary broken ordinals are not a supported
  preservation scenario.
- Deeper ancestor canonicalization fallbacks remain an explicit coverage limitation.
  The new two-component descriptor and parent-partition cases do not establish every
  reference identity/collision variant. Defensive malformed intermediate inventories
  and incidental diagnostic ordering are not counted as correctness improvements.

### 7. Write plans

The fresh six-file pair with isolated golden output improves from 68.60% to 69.48%:
236 → 239 killed, 48 → 45 survived, and unchanged 60 uncovered, 161 compile errors,
and 121 ignored mutations. There are no timeouts or lost kills. The three new kills
protect required-locator bindings at `WritePlanCompiler.cs:613/620/625`; the new
non-writable-locator cases detect all three. The full run also confirms the batching
pilot's 14 kills and remaining statuses.

Two cases assert complete binding inventories, column/parameter/source alignment,
insert/update/delete SQL, and collection preallocation/merge plans after required root
and parent locator columns are marked non-writable. The compiler must still bind those
structural columns (`WritePlanCompiler.cs:620/625`), in both dialects. All 2,745 Plans
tests pass, including the batching transition cases.

Stryker rolls back key-unification `ValidatePrecomputedBindingProducerAccounting` and
`CreateKeyUnificationMemberWritePlan` after generated-code definite-assignment errors.
Ordinary producer-accounting and complete member-contract tests remain essential.

Remaining survivor dispositions:

- Emitter and batching null guards receive non-null lists from the compiler. The
  insert emitter's null-row `continue` is outside the non-null row-list contract.
- `KeyUnificationWritePlanCompiler.cs:377` repeats the nullable-boolean condition
  already required by `KeyUnificationPresenceConventions.IsSyntheticPresenceColumn`
  before entering that validation. Public compilation rejects non-nullable boolean
  and nullable non-boolean candidates earlier as unsupported null-source bindings.
- Producer binding indices are derived from the same binding inventory; the range
  guards at 288/294 are not reachable with out-of-range producer metadata. Constructor
  and upstream model validation still apply.
- Removing the duplicate-inventory precheck does not accept duplicate physical keys:
  `BuildWriteSourceLookupMap` independently rejects them with `TryAdd`. Ordering and
  inventory-name mutations affect the diagnostic summary, not the accepted model.
- Empty key-unification inventories return the same empty set with or without the
  early return. Remaining invalid-column/plan-shape guards need no fabricated producer
  shapes solely to raise the score.
- `GetImmediateParentScopeLocatorIndex` receives an existing locator column. The
  current stable-identity producer emits one immediate-parent locator per non-root
  scope, so the loop returns at index zero; increment/boundary mutations cannot change
  valid derived output. The legacy parent-key scan similarly returns before the ordinal.
- Delete-by-parent uses separate-table one-to-one scopes. For the supported root and
  collection-aligned extension producers, the explicit parent locator and the legacy
  parent-key inventory name the same physical key. Forcing either selection branch at
  line 800 therefore leaves the emitted predicate unchanged for those models.

### Batching pilot

The initial pilot killed 14 mutants and left one survivor: removing the public
binding-list overload's `ArgumentNullException.ThrowIfNull` at line 35. A null list is
outside its non-nullable contract; the following `Count` access still fails. Retain
this defensive exception-type distinction without adding a score-only test. One
`columnBindings.Count` → `columnBindings.Sum` mutant cannot compile, and five redundant
block mutants are ignored. No batching arithmetic or range-check mutant survived.
The rerun after adding the transition cases produced the same 14 killed, one defensive survivor, one compile
error, and five ignored mutants, with no timeouts or uncovered mutants.

Added cases exercise the row-cap/parameter-budget transition (SQL Server widths 2/3;
PostgreSQL widths 65/66), with exact limits and remainder rounding, plus zero and
negative widths for both dialects. Existing cases already cover one parameter, exactly
the dialect ceiling, and one above it. This pilot does not establish a result for the
other five files in the write-plan target.

### 8. Relational model

The fresh five-file pair with isolated output confirms twelve new assertion-based
kills and no lost kills. The final report has 340 killed, 102 survived, 175 uncovered,
217 compile errors, 184 ignored, and eight loop timeouts. Baseline non-loop timeouts
are explained above and are not counted as test improvements. All 1,048 Model tests
pass, including thirteen new cases.

A three-hop identity-reference chain, visited before its mutable origin, requires
another fixed-point pass (`TransitiveIdentityMutabilityPass.cs:92`). Both dialects check
the resulting foreign-key actions, immutable origins, ordinary-reference controls,
and disconnected resources. Storage-validator cases exercise local/target endpoint
validation for externally defined `dms.Document`, exact schema/name matching, and
compact descriptor storage. Three-column same-site unification verifies every alias
shares its stored canonical column and presence gate, stays non-writable, and retains
its source path (`KeyUnificationPass.cs:564`).

Tool limitations remain separate from test gaps. Stryker rolls back the requiredness
helper's `GetRequiredProperties` and `IsXNullable` methods after generated-code
definite-assignment errors; only one executable helper mutation remains. It also rolls
back key-unification equality-constraint parsing, member base-token construction, and
ambiguous-endpoint diagnostics. These compile errors do not qualify the corresponding
requiredness or canonicalization behavior; existing ordinary model tests remain essential.

All eight remaining after-run timeouts introduce nonterminating loops: four union-find mutations
at `KeyUnificationPass.cs:1300–1309`, and four fixed-point mutations at
`TransitiveIdentityMutabilityPass.cs:63/66/68/91`. Keep them separate from assertion kills.
No production mapping changes or `RelationalMappingVersion` bump are part of this work.

Additional source-based dispositions for the Model baseline:

- Key-unification early-return removals at 63/196/406/1643 perform extra empty work
  but retain the same model. Single versus SingleOrDefault at 410/596 sees an existing
  root/member supplied by the model inventory. Unsupported endpoint kinds are checked
  both before edge construction and again by `ValidateUnificationMembers`.
- Union-find root selection at 1331 changes the internal representative, not the
  connected members. Members are independently sorted by source path and column name
  before canonical-column allocation. The first-element shortcut in the component
  comparer is repeated by its final full-sequence comparison; empty components cannot
  arise from the nonempty union-find groups.
- Ordering/deduplication of diagnostic records and the skipped-endpoint wording do not
  change stored columns, aliases, presence gates, or constraints. The final model
  canonicalizer also orders emitted constraints. These are retained without assertions
  on incidental traversal order.
- Reference identity lists are nonempty in schema-derived bindings: the extractor
  rejects an empty `referenceJsonPaths` array. The zero-identity count/name fallbacks
  at `ReferenceConstraintPass.cs:156/249` therefore do not describe accepted schema
  input. In the fresh production pipeline, all-or-none and reference-FK additions mark
  the same receiver table; removing either individual mark leaves the other in place.
- Unknown/ambiguous binding inventories, out-of-range table indices, and missing
  source paths require inconsistent intermediate models. Canonical/presence-name
  suffix fallback after a second name collision remains an explicit unexercised
  collision path; ordinary deterministic naming and first-collision tests remain.

### 9. Validation

The full five-file baseline contains 54 killed, 26 survived, 2 uncovered, 48 compile
errors, and 18 ignored mutants within its declared scope. The rerun has 59 killed,
22 survived, 1 uncovered, the same 48 compile errors and 18 ignored, and no timeouts.
The 23 remaining survived/uncovered mutants are accounted for in the groups below.
Other assembly files appear in the report because Stryker instruments before applying the source filter; their
compile errors and ignored mutants are not part of this target's executable score.

All five actionable findings below were confirmed killed in the full-target rerun
by matching source file, location, operator, and replacement:

- `ValidateDecimalMiddleware.cs:33`: removing `await next()` survived the baseline. Accepted
  positive/negative range endpoints must call the continuation exactly once.
- `ValidateDecimalMiddleware.cs:38` and `ValidateEqualityConstraintMiddleware.cs:49`:
  replacing the failure detail with an empty string survived the baseline. Check the response
  contract and that rejected requests do not continue.
- `EqualityConstraintValidator.cs:85`: `Append` → `Prepend` was uncovered in the baseline. Multiple
  constraints sharing one error path must retain each error in constraint order.
- `ResourceQueryFilterValidator.cs:243`: changing the date-time flags from bitwise OR
  to AND survived on a UTC host. An unzoned timestamp still means UTC on a non-UTC
  host. The new Unix-only test serializes access to process `TZ` and restores it in
  `finally`; it does not depend on the machine's configured local zone.

Concrete non-actionable groups retained without suppressing mutation types:

- Debug logging statements/templates in both middleware files (five each), and
  `Trace.Assert` statements/messages in the equality validator (six), are diagnostic
  behavior. JsonPath supplies non-null match collections for supported paths.
- Removing the final `return` in either middleware's rejection branch is equivalent:
  execution has already reached the end of the method.
- Four `ThrowIfNull` removals in the internal query validator exercise unsupported
  null container arguments. The caller supplies non-null parameter/schema collections.
- The uncovered string-type error branch at `ResourceQueryFilterValidator.cs:164`
  requires a null value in `IReadOnlyDictionary<string, string>`; HTTP query values and
  the internal contract are non-null strings. Do not manufacture null input just for
  this mutant.
- Compile-error mutants remain a tool limitation, not test kills. Besides the decimal
  rollback, Stryker generates invalid `Count` → `Sum` member access, duplicate empty
  switch labels, an unsupported `FirstOrDefault` → `First` overload, and invalid
  definite-assignment variants. Some valid adjacent comparisons are rolled back with
  them; ordinary behavioral tests remain essential.

### Decimal pilot limitation

The initial Release suites passed: Core 9,976 tests and Plans 2,707 tests. After the
changes, all 10,021 Core tests and 2,712 Plans tests passed. Focused runs passed 86
validation tests and 17 batching tests. CSharpier checked all six changed C# files.
The decimal pilot produced both native reports and killed all three executable `AddError` mutants.
However, generated code failed definite-assignment analysis at `DecimalValidator.cs:61`
(`value`), and Stryker's safe mode rolled back the entire `Validate` method: 31 mutants
are `CompileError`, including `<` → `<=`, `>` → `>=`, range arithmetic, and invalid-value
handling. One redundant block mutation is ignored by Stryker. The reported 100% is not
evidence that those rules are mutation-tested. Keep this limitation visible when
interpreting the full validation target; boundary regression tests provide ordinary
test coverage without changing production code to accommodate the mutator.

Stryker also attempts discovery in `Backend.Tests.Common`, a helper library referencing
NUnit without a test adapter. Its no-tests warning is separate from discovery and
execution of the owning Core suite. The helper library does not need a test adapter.

### 10. DDL

The final four-file report has 778 killed, 75 survived, 19 uncovered, 73 compile-error,
and 211 ignored mutations, with no timeouts. Forty-two additional mutations are
killed by assertions, with no lost kills. The native score is 87.27% → 89.22%; the
baseline includes 30 timeouts, so its score alone is not the comparison of test quality.

The earlier full pair had 30 and 60 timeouts. Focused before/after runs selected their
union of 65 mutations by source-character spans, plus 18 overlapping mutations. With
concurrency 2, `additional-timeout: 100000`, and `timeout-ratio: 3.0`, all 83 receive
definite outcomes: 36 killed / 45 survived / two uncovered before, and 48 killed /
35 survived after, with zero timeouts. The fifteen apparent lost kills are confirmed
killed. The 18 overlapping outcomes agree with their full reports. For comparison of
assertion improvements, the baseline timing outcomes are checked against these
controls by source, location, operator, and replacement; native JSON and scores remain
unchanged. Five baseline timeouts were already killed by existing assertions and are
excluded from the 42 improvements.

The focused native reports are `StrykerOutput/2026-11-17.00-09-22/reports/` and
`StrykerOutput/2026-11-17.00-14-17/reports/`, relative to the DDL test directory.
Their runtimes are 28m03s and 26m23s. Each parent directory contains the exact
`diagnostic-config.json` and `run.log`. The final full run at
`StrykerOutput/2026-11-17.00-44-05/` retains its `run-config.json` and log. The named
DDL configuration now uses this verified timeout budget; source scope and mutation
types are unchanged.

New regressions cover catalog-literal escaping, empty key/index inventories, mismatched
foreign-key column counts, nonpositive binary lengths, unnamed/default-free SQL Server
columns, integer table-valued types, and non-nullable joins beside a nullable join of a
different kind. They assert complete sequence/index SQL and ordered composite foreign-key
columns. Nullable-join conjunction mutations at `TrackedChangeTriggerBodyEmitter.cs:593/602`
are confirmed killed. Final schema-escaping assertions protect both the nested EXEC
string and the change-version function's catalog lookup (`MssqlDialect.cs:90/323`).
All 1,504 normal DDL tests pass with one existing skip, including 34 new boundary
cases and the separate golden-output isolation regression.

Remaining survivor dispositions and limits:

- Most dialect survivors remove null guards on non-nullable identifier/type/list
  arguments. Valid model generation supplies those values; these mutants do not
  justify fabricating unsupported null inputs to increase the score.
- Incidental exception wording is separate from rejection and parameter-name contracts.
  The latter are asserted; diagnostic sentence fragments are not pinned.
- Removing `break` at tracked-change lines 252/285 scans past an already found join.
  Derived join inventories have unique names, so later iterations cannot replace
  the selected join with another match.
- Empty person-join paths and partially missing target metadata at lines 319/351,
  unknown value-source enum branches, and malformed descriptor tombstone roles/paths
  at lines 905/916 remain intermediate-model validation limits. Valid schema-derived
  plans do not produce those states. The defensive metadata checks remain in production.
- Existing database integration tests remain the backstop for executing emitted SQL;
  this mutation loop exercises the C# emitters and their output contracts.

## References

- [Stryker.NET configuration](https://stryker-mutator.io/docs/stryker-net/configuration/)
- [Stryker.NET mutations](https://stryker-mutator.io/docs/stryker-net/mutations/)
- [Repository conventions](AGENTS.md) and [CMS conventions](src/config/AGENTS.md)
