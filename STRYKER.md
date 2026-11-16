# DMS and CMS mutation testing

Run Stryker.NET directly from the owning unit-test directory. Named configurations
select one production assembly and a behavior-focused source scope. The five existing
`stryker-config.json` files remain available for exploratory runs and are unchanged.

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
| 1 / `dms-request-auth` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-request-auth.json` | Prepared; full target pending |
| 2 / `cms-permission-inheritance` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-claims.json` | Pending |
| 3 / `dms-query-auth` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-query-auth.json` | Pending |
| 4 / `dms-write-execution` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-write-execution.json` | Pending |
| 5 / `cms-token-admission` | `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit` | `stryker-openiddict.json` | Pending |
| 6 / `dms-profile-core` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-profile.json` | Pending |
| 6 / `dms-profile-backend` | `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit` | `stryker-profile.json` | Pending |
| 7 / `dms-write-plans` | `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit` | `stryker-write-plans.json` | Pilot verified; full target pending |
| 8 / `dms-model-constraints` | `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit` | `stryker-model-constraints.json` | Pending |
| 9 / `dms-validation` | `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` | `stryker-validation.json` | Qualified with documented compile limitations |
| 10 / `dms-ddl` | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit` | `stryker-ddl.json` | Pending |

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

Implementation started from `dfbd0530896805cd3415b679dc62510a9871268a` on 2026-11-16.
Tool restoration and local-manifest resolution were checked at the repository root,
DMS Core test directory, and CMS Backend test directory: all resolve 5.0.0. Installed
SDK: 10.0.102; runtime: 10.0.2. Full-scope qualification remains in progress.

| Scope | Stryker | Before → after native score | Runtime before / after | Report locations, relative to owning test directory |
| --- | --- | --- | --- | --- |
| Decimal pilot (`Validation/DecimalValidator.cs`) | 5.0.0 | 100.00% → same 3 killed in full-target rerun; **31 compile errors** | 4m56s / included in full target | `StrykerOutput/2026-11-16.16-47-59/reports/` (pilot); `StrykerOutput/2026-11-16.17-05-10/reports/` (full rerun) |
| Batching pilot (`PlanWriteBatchingConventions.cs`) | 5.0.0 | 93.33% → 93.33% | 4m31s / 4m36s | `StrykerOutput/2026-11-16.16-58-30/reports/` → `StrykerOutput/2026-11-16.17-10-23/reports/` |
| Full `dms-validation` (five configured files) | 5.0.0 | 65.85% → 71.95% | 4m49s / 4m58s | `StrykerOutput/2026-11-16.16-53-30/reports/` → `StrykerOutput/2026-11-16.17-05-10/reports/` |

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
