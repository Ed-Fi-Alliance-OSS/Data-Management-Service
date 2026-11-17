# DMS and CMS mutation testing

Run Stryker.NET directly from the owning unit-test directory. Named configurations
select one production assembly and a behavior-focused source scope. The five existing
`stryker-config.json` files remain available for exploratory runs and are unchanged.

These configurations run manually. PR CI runs the ordinary NUnit tests, including
the regression tests added through this work; it does not invoke Stryker or enforce
a mutation-score gate.

## Prerequisites and commands

- .NET 10 SDK and access to the repository's configured NuGet feeds.
- Restore local tools with `dotnet tool restore`. All three tool manifests pin
  `dotnet-stryker` 5.0.0. Verify with `dotnet tool list --local` in the directory used
  for the run; `--version` is a dashboard option, not a tool-version query.
- Run the owning test project normally in Release before interpreting mutation results.
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

Paths are relative to the repository root. Configurations are introduced as each area
begins; pending rows are planned targets, not qualified runs.

| Area / target | Owning unit-test directory | Configuration | Status |
| --- | --- | --- | --- |
| 1 / `dms-request-auth` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-request-auth.json` | Qualified; six actionable survivors killed |
| 2 / `cms-permission-inheritance` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-claims.json` | Qualified; four actionable mutations killed |
| 3 / `dms-query-auth` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-query-auth.json` | Verified; 63 intended mutations killed across the two stages; cache limitations remain |
| 4 / `dms-write-execution` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-write-execution.json` | Twenty-five mutations killed; remaining composite-path triage in progress |
| 5 / `cms-token-admission` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-openiddict.json` | Qualified; twenty-three intended mutations killed |
| 6 / `dms-profile-core` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-profile.json` | One hundred twenty-one mutations killed; remaining triage in progress |
| 6 / `dms-profile-backend` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-profile.json` | Sixteen mutations killed; collection/merge triage remains |
| 7 / `dms-write-plans` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-write-plans.json` | Verified; two required-locator mutations killed; compiler limitations documented |
| 8 / `dms-model-constraints` | `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit` | `stryker-model-constraints.json` | Full baseline complete; mutability and storage-validation regressions in progress |
| 9 / `dms-validation` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-validation.json` | Qualified with documented compile limitations |
| 10 / `dms-ddl` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit` | `stryker-ddl.json` | Configuration prepared; baseline pending |

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

## Results

CMS Backend's Release suite passed 5,375 tests before the Claims baseline. Stryker
5.0.0 could not select the transitively referenced Claims assembly, so the test
project now references Claims directly. After merging `main` at `b127848d2`, the
PostgreSQL and SQL Server integration-test lock files also record that reference:
`main` added references from those suites to the Backend unit-test project. A forced
locked restore of the complete CMS solution now passes. No package versions changed.
The initial project-selection failure produced no baseline.

Implementation started from `dfbd0530896805cd3415b679dc62510a9871268a` on 2026-11-16.
Tool restoration and local-manifest resolution were checked at the repository root,
DMS Core test directory, and CMS Backend test directory: all resolve 5.0.0. Installed
SDK: 10.0.102; runtime: 10.0.2. Full-scope qualification remains in progress.

| Scope | Stryker | Before → after native score | Runtime before / after | Report locations, relative to owning test directory |
| --- | --- | --- | --- | --- |
| Decimal pilot (`Validation/DecimalValidator.cs`) | 5.0.0 | 100.00% → same 3 killed in full-target rerun; **31 compile errors** | 4m56s / included in full target | `StrykerOutput/2026-11-16.16-47-59/reports/` (pilot); `StrykerOutput/2026-11-16.17-05-10/reports/` (full rerun) |
| Batching pilot (`PlanWriteBatchingConventions.cs`) | 5.0.0 | 93.33% → 93.33% | 4m31s / 4m36s | `StrykerOutput/2026-11-16.16-58-30/reports/` → `StrykerOutput/2026-11-16.17-10-23/reports/` |
| Full `dms-validation` (five configured files) | 5.0.0 | 65.85% → 71.95% | 4m49s / 4m58s | `StrykerOutput/2026-11-16.16-53-30/reports/` → `StrykerOutput/2026-11-16.17-05-10/reports/` |
| Claim-set pilot (`Security/ClaimSetResolver.cs`) | 5.0.0 | 85.00% baseline | 4m58s | `StrykerOutput/2026-11-16.17-15-38/reports/` |
| Full `dms-request-auth` (three configured files) | 5.0.0 | 63.31% → 67.63% | 5m43s / 5m45s | `StrykerOutput/2026-11-16.17-20-46/reports/` → `StrykerOutput/2026-11-16.17-29-57/reports/` |
| Full `cms-permission-inheritance` (two configured Claims files) | 5.0.0 | 83.33% → 91.67% | 6m25s / 6m19s | `StrykerOutput/2026-11-16.17-39-28/reports/` → `StrykerOutput/2026-11-16.17-47-25/reports/` |
| Initial query-authorization cluster (five files, before relationship expansion) | 5.0.0 | 82.50% baseline | 7m11s | `StrykerOutput/2026-11-16.17-53-57/reports/` |
| Full `dms-query-auth` (seven configured files) | 5.0.0 | 82.20% → 83.01% | 8m09s / 8m58s | `StrykerOutput/2026-11-16.18-11-59/reports/` → `StrykerOutput/2026-11-16.18-35-24/reports/` |
| Admission-policy pilot (`Services/ClientAdmissionPolicy.cs`) | 5.0.0 | 100.00% baseline; 2 compile errors | 6m00s | `StrykerOutput/2026-11-16.18-01-26/reports/` |
| Full `cms-token-admission` (two configured OpenIddict files) | 5.0.0 | 65.72% → 72.96% | 11m46s / 14m47s | `StrykerOutput/2026-11-16.18-21-43/reports/` → `StrykerOutput/2026-11-16.19-33-56/reports/` |
| Write-executor pilot (`DefaultRelationalWriteExecutor.cs`) | 5.0.0 | 77.42% baseline; 148 compile errors | 4m19s | `StrykerOutput/2026-11-16.18-45-09/reports/` |
| Full `dms-write-execution` (eleven configured files) | 5.0.0 | 66.74% → 71.93%; further triage in progress | 4m17s / 4m35s | `StrykerOutput/2026-11-16.18-50-04/reports/` → `StrykerOutput/2026-11-16.20-05-20/reports/` |
| Full `dms-profile-core` (five configured files) | 5.0.0 | 66.40% → 80.47%; further triage in progress | 6m29s / 10m12s | `StrykerOutput/2026-11-16.19-24-29/reports/` → `StrykerOutput/2026-11-16.21-05-31/reports/` |
| Full `dms-profile-backend` (three configured files) | 5.0.0 | 57.14% → 58.31%; further triage in progress | 5m01s / 5m26s | `StrykerOutput/2026-11-16.19-55-14/reports/` → `StrykerOutput/2026-11-16.20-19-25/reports/` |
| Full `dms-write-plans` (six configured files) | 5.0.0 | 69.77% → 70.06% | 15m18s / 16m20s | `StrykerOutput/2026-11-16.20-28-52/reports/` → `StrykerOutput/2026-11-16.20-57-58/reports/` |
| Full `dms-model-constraints` (five configured files) | 5.0.0 | 53.76% baseline; regression work in progress | 26m52s | `StrykerOutput/2026-11-16.20-34-18/reports/` |

Request authorization, Claims, validation, and batching used `--concurrency 2`.
The initial query-authorization cluster and admission pilot used `--concurrency 4`.
The expanded query-authorization and full OpenIddict runs use `--concurrency 8` on the 24-core local
host. Only one Stryker process runs in each checkout at a time. Model and DDL
baselines use separate sibling worktrees, `mutation-test-spike-model` and
`mutation-test-spike-ddl`, created from PR commit `ea98de68a`, with concurrency 4.
Their normal suites passed 1,035 and 1,469 tests respectively (one existing DDL skip).
Runtime observations include concurrent host load; they are not performance benchmarks.

### Query-authorization initial cluster

The initial five-file scope contains 559 killed, 79 survived, 40 uncovered, 2 timed-out,
163 compile-error, and 128 ignored mutants. The namespace SQL helper's 41 executable
mutants and parameter-budget helper's 6 executable mutants were all killed. Their
remaining compile errors include invalid `Count` → `Sum` access and adjacent changes
rolled back by Stryker; the results do not prove every boundary was instrumented.

The main test gaps are in `PageDocumentIdSqlCompiler.cs`:

- Lines 1045–1129: removing direct-path returns, subquery projections, joins, view
  membership, aliases, or closing delimiters can survive fragment-only assertions.
  New direct/transitive cases assert complete page and count SQL for both dialects,
  along with the exact paging parameters and absence of count parameters.
- Lines 1015–1040, 1077, and 1082: malformed custom-view root, empty path, direct
  source, or intermediate join targets need explicit rejection cases.
- Line 470: treating an empty custom-view list as active can retain unused namespace
  parameters when all checks are empty. Line 514's `Any` → `All` rejects an empty
  custom-view list alongside a real ownership filter. Both empty-list contracts are
  now tested.
- Line 1275: removing self-person anchor validation can authorize through a different
  column from the selected subject. New table/column mismatch cases cover both dialects.
- `RelationalAuthorizationPlanner.cs:647`: `Min` → `Max` can select a later
  configuration failure, incorrectly letting an intervening namespace terminal win.
  A request with failures on both sides of the namespace strategy now checks precedence.

The two timeouts correspond to nonterminating loops: negating the outer collision
scan at line 152 loops forever for an empty predicate list; removing the inner index
increment at line 164 loops forever for duplicate semantic predicates. These are
timeout detections, not assertion-based kills. No increased test delays are needed.

All 2,730 Plans tests passed in Release after the first cluster's changes. The
configuration also includes `RelationshipAuthorizationPlanner.cs` and
`SingleRecordRelationshipAuthorizationSqlCompiler.cs`. The expanded baseline contains
1,423 killed, 187 survived, 122 uncovered, 4 timed-out, 461 compile-error, and 345 ignored
mutants in those seven files. Matching source location/operator/replacement verifies
49 intended first-cluster mutations are now killed. The old five-file score and new
seven-file score have different denominators and are not a before/after comparison.

The expanded report also changes the status of the duplicate-parameter secondary
sort (page compiler line 303) to killed by an existing golden test, although that sort
cannot change a valid result. Do not attribute this change to the new tests; verify
its status in the final rerun. That rerun reports line 303 as survived again and
line 139's redundant secondary sort as killed; both retain the same source-based
equivalence analysis and neither is credited to the added tests. A third page-loop mutation (line 157) and the relationship
compiler's parameter-suffix loop (line 768) time out in this run.

Relationship regressions now cover a mixed invalid-strategy/subject-planning failure,
a missing transitive first-hop binding with the correct root-table location, retained
person metadata for ineligible create-new subjects, and one shared update failure
when required people views are missing. Cached SQL tests cover zero AUTH1 indices in
both overloads and dialects, rejection of negative indices, and reuse with equivalent
reserved names supplied as separate arrays or lists. The complete Plans suite passed
2,738 tests before the last five added cases; those five subsequently passed focused
runs. The final seven-file rerun passed its initial test run and reports 1,437 killed,
186 survived, 109 uncovered, 4 timed-out, 461 compile-error, and 345 ignored mutants.
Fourteen intended relationship mutations changed to killed: planner lines 458, 522,
1281 (two), 1842, and 1843; compiler lines 131, 136, 2044, 2045, 2084 (two), 2086,
and 2092. The 49 initial-cluster fixes remain killed. No source filters or mutation
types were suppressed to obtain these results.

Initial-cluster exceptions retained after reviewing the expanded report:

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

### Admission-policy instrumentation pilot

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


### Transactional-write executor pilot

The executor-only pilot produced 24 killed, 7 survived, 148 compile-error, and 7
ignored mutants. The normal Backend suite passed 4,827 tests before the pilot.
Stryker rolled back **all mutations in `ExecuteAsyncInternal`** after generated-code
assignment errors involving `createdSession`, `secondCommand`, and exception-mapping
locals. The 77.42% score does **not** qualify commit/rollback or authorization ordering
inside that method. Existing regression tests for those contracts remain available;
no production refactor was made to inflate the instrumented score.

The executable survivors are the optional writer-wait telemetry dependency and its
recording call (57, 680), logger-factory fallback (93), synchronization-context capture
on the outer await (129), two non-nullable argument guards (629–630), and the enqueue
success guard (700–701). The last is equivalent for executor-produced outcomes:
only an applied write has a non-null persisted result; other successful outcomes have
none. Removing wait telemetry is a remaining observability coverage gap, not a write
correctness kill. The full configuration includes the composite first/second commands,
guarded no-op comparison, and all seven behavior-bearing ETag files; its separate
baseline contains 321 killed, 115 survived, 45 uncovered, 389 compile-error, and
109 ignored mutations, with no timeouts. `EtagComposer.cs` and `ServedEtagComposer.cs`
produce no Standard-level mutants; their inclusion alone does not establish mutation
coverage. A helper-library discovery warning concerns `Backend.Tests.Common`,
which is not the owning test suite.

The composite regressions now independently exercise collection row identity and root
locator changes; reject nonpositive stream-ETag versions before composition; reject
missing insert IDs and missing/duplicate persistence observations; retain stored
relationship authorization in one command; and preserve missing-PUT and no-claims
denial precedence in ordered segments. Both dialects also classify unmatchable
references in that segmented path. Custom-view order and deferred-value provenance
are checked in DML mode as well as authorization-only mode. A mixed inline/reserved
collection-key case verifies the wider row binding cannot overflow a command budget.

The complete Backend suite passed 4,846 tests after the first changes; the additional
mixed-key case passed a focused run. The full rerun has 342 killed, 108 survived,
31 uncovered, 389 compile-error, and 109 ignored mutations, with no timeouts.
Twenty-one intended mutations are confirmed killed: stream ETag lines 35/37/40;
first phase 588/1421/1441/1592; guarded no-op 70/77; second command 931–934,
1006/1098/1225 (two)/1249/1400/1419/1420.

One result assertion still allowed removal of the first phase's stored-relationship
return at 689: equivalence through the base result type did not establish the concrete
failure. The fixture now supplies valid no-claims metadata and checks the concrete
relationship-denial type, stored value source, empty claim IDs, and failed strategy.
The additional full rerun confirms that return mutation is now killed, taking the
total to 343 killed, 107 survived, 31 uncovered, 389 compile-error, and 109 ignored,
with no timeouts (71.31%). Other composite-path survivors still need triage.
The Model and DDL suites also passed their baseline normal runs: 1,035 and 1,469 tests
respectively, with one existing DDL skip. The full Model baseline and subsequent
normal regressions are recorded below; the DDL mutation baseline remains in progress.

### Token-manager baseline and regressions

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

### DMS Core profile triage

The full five-file baseline contains 571 killed, 212 survived, 77 uncovered,
176 compile-error, and 292 ignored mutations, with no timeouts. Each configured
file contains executable mutants. The first regression cluster covers:

- Complete nested object, collection, and extension projections, including removal
  of emptied descendants, preservation of multiple server-generated members on
  surviving scopes, and deep-clone independence.
- Include/exclude collection filters with multiple values, case-insensitive matches,
  and absent/null/empty values. Read projection and write classification have distinct
  existing missing-value contracts; the assertions preserve each contract.
- Complete extension scope-state inventories for omitted, null, empty, populated,
  and hidden data, including descendant states, collection ancestor identities,
  request paths, and initially false creatability flags.
- Nested validation failures' complete paths, read/write labels, and severity under
  include/exclude selection, plus explicit extension child names in the classifier.
- Parent and descendant creatability dependencies, including the stored-existence
  and creatability flags, target kind, and rejection of a new parent whose visible
  collection child requires hidden data. Both item orders and create/update attempts
  are covered without modifying the input scope/item records.

All 10,056 Core unit tests pass, including 29 added cases; the full Backend suite
passes 4,851 tests, including four new composite descriptor-hydration cases for
profiled writes and deferred ETag checks. Formatting passes for all eight changed
test files. The full Core rerun confirms 92 changes from survived/uncovered to
killed: 27 in creatability, 29 in definition validation, 2 in visibility classification,
21 in read projection, and 13 in write shaping. The report contains 663 killed,
152 survived, 45 uncovered, 176 compile-error, and 292 ignored mutations, with no
timeouts. The compile-error count did not change.

After that first rerun, triage identified identity-reference projection, nested
extension fall-through, deep request paths and absent-child states in write shaping,
recursive server-generated-name/extension validation, and creatability ancestor
matching and dependency gaps. In that report, the name-walk and extension-walk
removals at `ProfileDataValidator.cs:398/864` were not credited among the 92 kills.

Some survivors already have narrow explanations: the projector's `Count > 0` to
`>= 0` changes at 113/185/292 receive either a nonempty extension object or null;
the helper never returns an empty object. Its root metadata condition at 90 falls
through to the same server-generated preservation helper. Invalid enum defaults
and non-object query-array elements are outside the API document shapes. Further
triage is still required before treating the target as qualified. The Backend
profile baseline is recorded below; the Core result does not prove stored-row
preservation. The descriptor-hydration rerun confirms three more write-execution
kills: the profile/deferred-precondition condition and single-document hydration
selection at `RelationalWriteFirstPhase.cs:1110/1114`. The full eleven-file report
contains 346 killed, 104 survived, 31 uncovered, 389 compile-error, and 109 ignored
mutations, with no timeouts. The write target has 25 intended kills since baseline.

A follow-up adds ten Core cases and extends the existing nested projections to
protect identity-reference preservation, extension filtering under IncludeAll,
server-generated-field rejection inside nested rules, and complete collection
request paths through object/collection/extension scopes. All 10,066 Core tests
pass. The full follow-up report confirms another 18 kills: 10 in definition
validation (including the recursive name walk at line 398), 3 in read projection,
and 5 in write shaping. It contains 681 killed, 134 survived, 45 uncovered,
176 compile-error, and 292 ignored mutations, with no timeouts or loss of a previous
kill. That report establishes 110 intended kills since baseline.

Eight further Core cases now pass in the full 10,074-test suite. They cover missing
extension namespaces under nested objects/collections (including extension parents),
read/write error paths and warning severity, and a nested required member sharing a
root identity member's name. The latter verifies that implicit identity visibility
remains root-scoped. Two more parent-context cases pass in the full 10,076-test suite:
a blocked new child demotes only its own collection parent, in either input order.

The full rerun in the isolated model worktree confirms eleven additional kills:
`CreatabilityAnalyzer.cs:87/88/513` and `ProfileDataValidator.cs:816/818/820/822/862/864/866/868`.
It contains 692 killed, 123 survived, 45 uncovered, 176 compile-error, and 292 ignored
mutations, with no timeouts or loss of previous kills. The total is now 121 intended
kills. Remaining behavioral triage includes absent descendant states and additional
creatability ancestor/dependency paths. The report and run log were copied into the
owning Core test directory in the PR checkout.

### DMS Backend profile baseline

The full three-file baseline contains 783 killed, 368 survived, 220 uncovered,
358 compile-error, 368 ignored, and one timed-out mutation. The timeout changes
`CountWildcardSegments` at `RelationalWriteProfileMerge.cs:2390` from `index += 3`
to `index -= 3`; record it separately from assertion-based kills.

Stryker rolls back `ProfileCollectionWalker.WalkChildren`, `ExtractRequiredInt32`,
and `ExtractRequiredInt64`, and the merge synthesizer's
`TryBuildStoredDocumentReferenceIdentityJson`, after generated-code compile errors.
The baseline therefore does not qualify mutations inside those methods. Existing
ordinary walker/merge tests remain the backstop; production code is not rewritten
to bypass the instrumentation limitation.

The full Backend suite passes 4,874 tests, including 23 added cases. Eleven binding
regressions assert complete dispositions for visible-present/absent and hidden
stored data, descendant state application, rejection of scope/ancestor-instance
mismatches, and rejection of hidden metadata with no matching binding. Ten identity
cases protect parent partition equality, numeric-width normalization, composite-key
ordering and length, unresolved identity instance separation, and child scope plus
parent matching. Two additional walker cases assert complete hidden-member sets on
both stored children, so expanding a later child cannot erase the earlier child's
hidden metadata. The full rerun confirms 16 new kills: nine in binding classification
and seven in the collection walker, including loss of earlier hidden-path expansions
and incorrect equality across parent partitions. It contains 799 killed, 358 survived,
214 uncovered, 358 compile-error, 368 ignored, and the same single loop timeout.
No previously killed mutation regressed. The merge synthesizer's counts are unchanged;
this result does not close its remaining canonicalization/preservation gaps.

Two existing composite authorization tests now also inspect the complete SQL Server
table-valued claim list and invoke the emitted parameter callback to verify it reaches
the supplied provider configurator with `dms.BigIntTable`/`Id` metadata. The Backend
suite still passes all 4,874 tests. Their write-execution mutation rerun is pending.

Some identity-helper survivors are outside the correctness contract: removing hash
contributions or changing constant hash prefixes increases collisions but does not
change dictionary equality or lookup results. Physical identity columns are non-null
in supported stored rows, so null-literal identity branches do not justify fabricated
rows. Remaining triage includes nested preservation modes, ancestor canonicalization,
physical-row projection and owner-scope selection. The rolled-back main walker remains
an instrumentation limitation, independently of the tested index/helper behavior.

### CMS permission-inheritance triage

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

### Request authorization triage

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

### Claim-set pilot triage

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

### Decimal pilot instrumentation limitation

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

### Batching pilot triage

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

### Full write-plan verification

The six-file target contains 235 killed, 44 survived, 60 uncovered, 161 compile-error,
121 ignored, and five timed-out mutations. This is independent of the batching
pilot's 93.33% result. Stryker rolls back key-unification
`ValidatePrecomputedBindingProducerAccounting` and `CreateKeyUnificationMemberWritePlan`
after generated-code definite-assignment errors; their ordinary producer-accounting
and complete member-contract tests remain essential.

Two new cases retain complete binding inventories, column/parameter/source alignment,
insert/update/delete SQL, and collection preallocation/merge plans after required root
and parent locator columns are marked non-writable. They use the derived stable-key
fixture for both dialects. The compiler must still bind those structural columns.
Both `requiredBindingColumns.Add` removals at `WritePlanCompiler.cs:620/625` are now
killed by those new cases in the full rerun. All 2,745 normal Plans tests pass.

The rerun contains 241 killed, 43 survived, 60 uncovered, 161 compile-error, and 121
ignored mutations, with no timeouts. Besides the two intended kills, two pre-existing
tests now kill the presence-binding diagnostic at `KeyUnificationWritePlanCompiler.cs:355`
and the collection-key count mutation at `WritePlanCompiler.cs:392`. Three prior
timeouts now report killed; two report survived. The null-parameter guard removal at
`SimpleUpdateSqlEmitter.cs:38` changed from killed to survived. These additional status
changes are run observations, not credited as improvements from the new locator cases.

The baseline's five timeouts are `SimpleUpdateSqlEmitter.cs:37` (a null guard),
`WritePlanCompiler.cs:207/211` (descriptor inventory validation/label), and two condition
mutations at line 225. These do not introduce loops. The rerun resolves them to three
killed and two survived; do not attribute the timeout status changes to new assertions.

Initial dispositions supported by source inspection:

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

The raw score includes instrumentation and run-status limitations; the confirmed
improvement is preservation of the two required locator binding groups. No mutation
types or source scope were removed.

### Relational-model baseline

The five-file target contains 328 killed, 109 survived, 180 uncovered, 217 compile-error,
184 ignored, and eight timed-out mutations. The native report was produced in the
isolated model worktree and copied into the owning test directory in the PR checkout.

The baseline exposes a missing fixed-point regression: changing `changed = true` to
`false` at `TransitiveIdentityMutabilityPass.cs:92` survives. A resource visited before
its identity-reference target needs another pass to inherit mutability. New cases
observe the resulting foreign-key actions over a three-hop chain, with both mutable
and immutable origins and ordinary-reference/disconnected controls in both dialects.
Pass-level storage-validator cases also exercise the calls that validate local and
target endpoints of the externally defined `dms.Document` table, exact schema/name
matching, and compact descriptor storage. All eleven new cases pass in the full
1,046-test Model suite (25 seconds); full mutation verification is pending.

Tool limitations remain separate from test gaps. Stryker rolls back the requiredness
helper's `GetRequiredProperties` and `IsXNullable` methods after generated-code
definite-assignment errors; only one executable helper mutation remains. It also rolls
back key-unification equality-constraint parsing, member base-token construction, and
ambiguous-endpoint diagnostics. These compile errors do not qualify the corresponding
requiredness or canonicalization behavior; existing ordinary model tests remain essential.

All eight baseline timeouts introduce nonterminating loops: four union-find mutations
at `KeyUnificationPass.cs:1300–1309`, and four fixed-point mutations at
`TransitiveIdentityMutabilityPass.cs:63/66/68/91`. Keep them separate from assertion kills.
No production mapping changes or `RelationalMappingVersion` bump are part of this work.

### Validation baseline triage

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

## References

- [Stryker.NET configuration](https://stryker-mutator.io/docs/stryker-net/configuration/)
- [Stryker.NET mutations](https://stryker-mutator.io/docs/stryker-net/mutations/)
- [Repository conventions](AGENTS.md) and [CMS conventions](src/config/AGENTS.md)
