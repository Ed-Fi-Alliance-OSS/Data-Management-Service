## 2026-06-23 - Backend Plans AuthorizationParameterBudget

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationParameterBudget.cs`
- Mutants selected:
  - `Statement mutation`, id `96`, line `66`: replaced `ArgumentOutOfRangeException.ThrowIfNegative(nonAuthorizationParameterCount);` with `;`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/AuthorizationParameterBudgetTests.cs`
- Commands run and results:
  - `dotnet tool restore`: succeeded; restored `csharpier` 1.2.5 and `dotnet-stryker` 4.15.0.
  - `dotnet stryker --config-file stryker-config.json`: broad Backend Plans run completed in `00:29:49`; `Killed: 3171`, `Survived: 1219`, `Timeout: 20`; report `StrykerOutput/2026-06-23.19-28-51/reports/mutation-report.json`.
  - `dotnet csharpier format AuthorizationParameterBudgetTests.cs`: succeeded.
  - `dotnet test EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_AuthorizationParameterBudget"`: passed; `9` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "AuthorizationParameterBudget.cs"`: focused file run completed in `00:01:55`; `Killed: 9`, `Survived: 0`; report `StrykerOutput/2026-06-23.19-59-37/reports/mutation-report.json`.
- Verification:
  - Confirmed mutant id `96` at line `66` is `Killed` in the focused JSON report.
- Remaining notes:
  - Broad target re-run was skipped because the broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanNamingConventions Guard Clauses

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanNamingConventions.cs`
- Mutants selected:
  - `Statement mutation`, id `3308`, line `29`: replaced `ArgumentNullException.ThrowIfNull(columnsInAuthoritativeOrder);` with `;`
  - `Statement mutation`, id `3317`, line `58`: replaced `ArgumentNullException.ThrowIfNull(value);` with `;`
  - `Statement mutation`, id `3326`, line `88`: replaced `ArgumentNullException.ThrowIfNull(candidateName);` with `;`
  - `Statement mutation`, id `3357`, line `145`: replaced `ArgumentNullException.ThrowIfNull(orderedNames);` with `;`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanNamingConventions"`: passed; `20` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "PlanNamingConventions.cs"` from the test project directory: interrupted after Stryker still created the broad target mutant set; not used as verification.
  - `dotnet stryker --config-file stryker-config.PlanNamingConventions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanNamingConventions.cs`: completed in `00:03:33`; `Killed: 78`, `Survived: 18`, `Timeout: 6`; report `StrykerOutput/2026-06-23.20-07-05/reports/mutation-report.json`.
- Verification:
  - Confirmed mutant ids `3308`, `3317`, `3326`, and `3357` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanNamingConventions.tmp.json` was removed after verification.
  - `PlanNamingConventions.cs` still has remaining survived mutants outside this guard-clause cluster.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanNamingConventions Explicit Suffix Advancement

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanNamingConventions.cs`
- Mutants selected:
  - `Statement mutation`, id `3371`, line `163`: replaced `AdvanceSuffixFromExplicitName(name, nextSuffixByName);` with `;`
  - `LogicalNotExpression to un-LogicalNotExpression mutation`, id `3395`, line `204`: changed the explicit suffix parse guard to return when parsing succeeds.
  - `Arithmetic mutation`, id `3398`, line `209`: changed `explicitSuffix + 1` to `explicitSuffix - 1`.
  - `Negate expression`, id `3400`, line `212`: negated the suffix reservation update condition.
  - `LogicalNotExpression to un-LogicalNotExpression mutation`, id `3401`, line `212`: changed the dictionary miss check while updating suffix reservations.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanNamingConventions"`: passed; `22` tests.
  - `dotnet stryker --config-file stryker-config.PlanNamingConventions.tmp.json --test-case-filter "FullyQualifiedName~Given_PlanNamingConventions"` from the test project directory: failed immediately because Stryker.NET `4.15.0` did not recognize `--test-case-filter`.
  - `dotnet stryker --config-file stryker-config.PlanNamingConventions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanNamingConventions.cs`: completed in `00:03:30`; `Killed: 88`, `Survived: 16`, `Timeout: 1`; report `StrykerOutput/2026-06-23.20-13-55/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3371`, `3395`, `3398`, `3400`, and `3401` are `Killed` in the focused JSON report.
  - Mutant id `3402` at line `213` was also killed by the same assertions.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanNamingConventions.tmp.json` was removed after verification.
  - `PlanNamingConventions.cs` still has remaining survived mutants outside this explicit-suffix advancement cluster.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanNamingConventions Suffix Parsing Edge Cases

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanNamingConventions.cs`
- Mutants selected:
  - `Logical mutation`, id `3399`, line `212`: changed explicit suffix reservation update from missing-or-greater to missing-and-greater.
  - `Logical mutation`, id `3407`, line `230`: changed numeric suffix separator rejection from leading-or-trailing to leading-and-trailing.
  - `Equality mutation`, id `3410`, line `230`: changed leading separator rejection from `separatorIndex <= 0` to `separatorIndex < 0`.
  - `Logical mutation`, id `3416`, line `238`: changed invalid suffix parse rejection from parse-failed-or-less-than-one to parse-failed-and-less-than-one.
  - `Equality mutation`, id `3420`, line `244`: changed positive suffix check from `parsedSuffix < 1` to `parsedSuffix <= 1`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanNamingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanNamingConventions"`: passed; `26` tests.
  - `dotnet stryker --config-file stryker-config.PlanNamingConventions.tmp.json` from the test project directory: first attempt was interrupted after confirming the mutate filter reduced the run to `105` active mutants; no verification report used.
  - `dotnet stryker --config-file stryker-config.PlanNamingConventions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanNamingConventions.cs`: completed in `00:03:43`; `Killed: 93`, `Survived: 10`, `Timeout: 2`; report `StrykerOutput/2026-06-23.20-23-01/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3399`, `3407`, `3410`, `3416`, and `3420` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanNamingConventions.tmp.json` was removed after verification.
  - `PlanNamingConventions.cs` still has remaining survived or timeout mutants outside this suffix parsing edge-case cluster.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanSqlWriterExtensions Guard And Validation Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanSqlWriterExtensions.cs`
- Mutants selected:
  - `Statement mutation`, id `3460`, line `27`: replaced `ArgumentNullException.ThrowIfNull(writer);` in `AppendParameter` with `;`
  - `Statement mutation`, id `3465`, line `41`: replaced `ArgumentNullException.ThrowIfNull(writer);` in `AppendRelation` with `;`
  - `Statement mutation`, id `3466`, line `42`: replaced `ArgumentNullException.ThrowIfNull(relation);` with `;`
  - `Statement mutation`, id `3469`, line `69`: replaced `ArgumentNullException.ThrowIfNull(writer);` in the callback `AppendWhereClause` overload with `;`
  - `Statement mutation`, id `3470`, line `70`: replaced `ArgumentNullException.ThrowIfNull(appendPredicateSql);` with `;`
- Additional mutants killed by the same assertions:
  - `Statement mutation`, id `3498`, line `110`: replaced `ArgumentNullException.ThrowIfNull(predicates);` with `;`
  - `Statement mutation`, id `3525`, line `159`: replaced the null-or-whitespace `ArgumentException` throw with `;`
  - `String mutation`, id `3526`, line `159`: replaced the null-or-whitespace validation message with `""`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanSqlWriterExtensionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanSqlWriterExtensionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanSqlWriterExtensions"`: passed; `29` tests.
  - `dotnet stryker --config-file stryker-config.PlanSqlWriterExtensions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanSqlWriterExtensions.cs`: final run completed in `00:02:21`; `Killed: 46`, `Survived: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.20-34-07/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3460`, `3465`, `3466`, `3469`, and `3470` are `Killed` in the focused JSON report.
  - Confirmed additional mutant ids `3498`, `3525`, and `3526` are also `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanSqlWriterExtensions.tmp.json` was removed after verification.
  - Focused survivor id `3497`, line `109`, appears redundant: removing the list-overload `writer` null guard still calls the callback overload, which throws the same `ArgumentNullException` for `writer`.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans NamespacePrefixParameterizationValidator Guard And Shape Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/NamespacePrefixParameterizationValidator.cs`
- Mutants selected:
  - `Statement mutation`, id `2362`, line `24`: replaced `ArgumentNullException.ThrowIfNull(namespacePrefixParameterization);` with `;`
  - `Statement mutation`, id `2363`, line `25`: replaced the base-parameter-name validation with `;`
  - `String mutation`, id `2364`, line `27`: replaced the base-parameter-name validation context with `$""`
  - `Statement mutation`, id `2365`, line `29`: replaced `ArgumentNullException.ThrowIfNull(namespacePrefixParameterization.LikePatternsInOrder);` with `;`
  - `Statement mutation`, id `2366`, line `30`: replaced `ArgumentNullException.ThrowIfNull(namespacePrefixParameterization.ParameterNamesInOrder);` with `;`
- Additional mutants killed by the same validator assertions:
  - `Statement mutation`, ids `2378`, `2380`, and `2381`; `String mutation`, ids `2379`, `2392`, and `2398`; equality/logical mutants ids `2383`, `2384`, `2387`, `2388`, `2389`, and `2391`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespacePrefixParameterizationTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespacePrefixParameterizationTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_NamespacePrefixParameterization"`: passed; `23` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.NamespacePrefixParameterizationValidator.tmp.json` from the test project directory with a temporary focused config mutating only `**/NamespacePrefixParameterizationValidator.cs`: completed in `00:01:57`; `Killed: 17`, `Survived: 0`, `Timeout: 0`; report `StrykerOutput/2026-06-23.20-40-23/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2362`, `2363`, `2364`, `2365`, and `2366` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.NamespacePrefixParameterizationValidator.tmp.json` was removed after verification.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans AuthorizationClaimEducationOrganizationIdParameterizationValidator Guard And Dialect Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationClaimEducationOrganizationIdParameterizationValidator.cs`
- Mutants selected:
  - `Statement mutation`, id `15`, line `19`: replaced base parameter name validation with `;`
  - `String mutation`, id `16`, line `21`: replaced the base parameter name validation context with `$""`
  - `Statement mutation`, id `17`, line `23`: replaced `ArgumentNullException.ThrowIfNull(authorizationClaimParameterization.ClaimEducationOrganizationIds);` with `;`
  - `Statement mutation`, id `18`, line `24`: replaced `ArgumentNullException.ThrowIfNull(authorizationClaimParameterization.ParameterNamesInOrder);` with `;`
  - `Statement mutation`, id `30`, line `44`: replaced parameter name validation with `;`
- Additional mutants killed by the same assertions:
  - `String mutation`, id `31`, line `46`; `Statement mutation`, ids `32` and `33`; dialect equality/logical mutants ids `35`, `36`, and `37`; shape/message mutants ids `40`, `41`, `42`, `44`, `45`, `49`, and `51`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/AuthorizationClaimEducationOrganizationIdParameterizationFactoryTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/AuthorizationClaimEducationOrganizationIdParameterizationFactoryTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_AuthorizationClaimEducationOrganizationIdParameterization"`: passed; `11` tests.
  - `dotnet stryker --config-file stryker-config.AuthorizationClaimEducationOrganizationIdParameterizationValidator.tmp.json` from the test project directory with a temporary focused config mutating only `**/AuthorizationClaimEducationOrganizationIdParameterizationValidator.cs`: completed in `00:01:58`; `Killed: 18`, `Survived: 0`, `Timeout: 0`; report `StrykerOutput/2026-06-23.20-45-32/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `15`, `16`, `17`, `18`, and `30` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.AuthorizationClaimEducationOrganizationIdParameterizationValidator.tmp.json` was removed after verification.
  - Focused report still shows no-coverage mutants for empty-list, unsupported-dialect, and unsupported-kind exception messages; no survived mutants remain in this focused run.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans HydratedRowOrdering Guard And Ordering Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydratedRowOrdering.cs`
- Mutants selected:
  - `Statement mutation`, id `1033`, line `12`: replaced `ArgumentNullException.ThrowIfNull(rows);` with `;`
  - `Statement mutation`, id `1034`, line `13`: replaced `ArgumentNullException.ThrowIfNull(resolveOrdinal);` with `;`
  - `Statement mutation`, id `1040`, line `17`: replaced the `rows.Count < 2` early return with `;`
- Additional mutants killed by the same assertions:
  - `Equality mutation`, id `1036`, line `15`; `Negate expression`, id `1037`, line `15`; `Equality mutation`, id `1042`, line `22`; `Equality mutation`, id `1046`, line `26`; `Negate expression`, id `1048`, line `26`; `Statement mutation`, id `1050`, line `28`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydratedRowOrderingTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydratedRowOrderingTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_HydratedRowOrdering"`: passed; `5` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.HydratedRowOrdering.tmp.json` from the test project directory with a temporary focused config mutating only `**/HydratedRowOrdering.cs`: completed in `00:01:56`; `Killed: 9`, `Survived: 3`, `Timeout: 0`; report `StrykerOutput/2026-06-23.20-50-09/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `1033`, `1034`, and `1040` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.HydratedRowOrdering.tmp.json` was removed after verification.
  - Focused survivors id `1044` (post-increment to post-decrement), id `1047` (`<` to `<=`), and id `1051` (remove `return` after sorting) appear equivalent or side-effect-only for final row order with a pure ordinal resolver. Killing them would require asserting resolver call counts or redundant sort behavior rather than the externally visible hydrated row order.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans MssqlPlanDialect Guard Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/MssqlPlanDialect.cs`
- Mutants selected:
  - `Statement mutation`, id `1996`, line `31`: replaced `ArgumentNullException.ThrowIfNull(writer);` in `AppendPagingClause` with `;`
  - `Statement mutation`, id `2004`, line `44`: replaced `ArgumentNullException.ThrowIfNull(writer);` in `AppendCreateKeysetTempTable` with `;`
  - `Statement mutation`, id `2005`, line `45`: replaced `ArgumentNullException.ThrowIfNull(keyset);` with `;`
  - `Statement mutation`, id `2019`, line `64`: replaced `ArgumentNullException.ThrowIfNull(writer);` in `AppendDocumentMetadataSelect` with `;`
  - `Statement mutation`, id `2020`, line `65`: replaced `ArgumentNullException.ThrowIfNull(keyset);` with `;`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MssqlPlanDialect"`: passed; `5` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.MssqlPlanDialect.tmp.json` from the test project directory with a temporary focused config mutating only `**/MssqlPlanDialect.cs`: completed in `00:02:09`; `Killed: 27`, `Survived: 8`, `Timeout: 0`; report `StrykerOutput/2026-06-23.20-56-11/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `1996`, `2004`, `2005`, `2019`, and `2020` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.MssqlPlanDialect.tmp.json` was removed after verification.
  - Focused survivors remain in `MssqlPlanDialect.cs` outside this guard cluster: temp-table DDL string mutants ids `2012`, `2013`, `2014`, `2016`, and `2017`, plus comparison guard mutants ids `2024`, `2025`, and `2026`.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans MssqlPlanDialect Keyset Temp Table DDL Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/MssqlPlanDialect.cs`
- Mutants selected:
  - `String mutation`, id `2012`, line `50`: replaced the keyset temp-table `DROP TABLE` line text with `""`
  - `String mutation`, id `2013`, line `51`: replaced the `DROP TABLE` statement terminator text with `""`
  - `String mutation`, id `2014`, line `53`: replaced the `CREATE TABLE` text with `""`
  - `String mutation`, id `2016`, line `56`: replaced the opening column list text with `""`
  - `String mutation`, id `2017`, line `58`: replaced the keyset temp-table primary-key column definition text with `""`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MssqlPlanDialect"`: passed; `6` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.MssqlPlanDialect.tmp.json` from the test project directory with a temporary focused config mutating only `**/MssqlPlanDialect.cs`: completed in `00:02:22`; `Killed: 32`, `Survived: 3`, `Timeout: 0`; report `StrykerOutput/2026-06-23.21-01-01/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2012`, `2013`, `2014`, `2016`, and `2017` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.MssqlPlanDialect.tmp.json` was removed after verification.
  - Focused survivors remain in `MssqlPlanDialect.cs` outside this DDL-shape cluster: comparison null-guard mutants ids `2023`, `2024`, and `2025` at lines `80`-`82`.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.


## 2026-06-23 - Backend Plans DocumentMetadataColumns Select Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/IPlanSqlDialect.cs`
- Mutants selected:
  - `String mutation`, id `1344`, line `103`: replaced the `SELECT` line text with `""`
  - `String mutation`, id `1346`, line `103`: replaced the `d.` document-id alias text with `""`
  - `Statement mutation`, id `1355`, line `108`: removed the metadata-column line append
  - `Conditional (true) mutation`, id `1356`, line `108`: forced every metadata-column line to end with `,`
  - `Conditional (false) mutation`, id `1357`, line `108`: forced every metadata-column line to omit `,`
- Additional mutants killed by the same assertions:
  - All other focused mutants in `IPlanSqlDialect.cs`; focused report has `Killed: 25`, `Survived: 0`, `Timeout: 0`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MssqlPlanDialect"`: passed; `2` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.IPlanSqlDialect.tmp.json` from the test project directory with a temporary focused config mutating only `**/IPlanSqlDialect.cs`: completed in `00:02:18`; `Killed: 25`, `Survived: 0`, `Timeout: 0`; report `StrykerOutput/2026-06-23.22-20-23/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `1344`, `1346`, `1355`, `1356`, and `1357` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.IPlanSqlDialect.tmp.json` was removed after verification.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PgsqlPlanDialect Keyset Temp Table DDL Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PgsqlPlanDialect.cs`
- Mutants selected:
  - `String mutation`, id `3288`, line `49`: replaced the opening keyset temp-table column-list text with `""`
- Additional mutants killed by the same assertion:
  - Focused `PgsqlPlanDialect.cs` run also killed string mutants ids `3285`, `3286`, `3287`, and `3289` around the keyset temp-table DDL.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PgsqlPlanDialectTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PgsqlPlanDialectTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PgsqlPlanDialect"`: passed; `1` test.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.PgsqlPlanDialect.tmp.json` from the test project directory with a temporary focused config mutating only `**/PgsqlPlanDialect.cs`: completed in `00:02:32`; `Killed: 18`, `Survived: 7`, `Timeout: 0`; report `StrykerOutput/2026-06-23.22-25-52/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant id `3288` is `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PgsqlPlanDialect.tmp.json` was removed after verification.
  - Focused survivors in `PgsqlPlanDialect.cs` are null-guard statement mutants, which are skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanWriteBatchingConventions Exception Messages

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanWriteBatchingConventions.cs`
- Mutants selected:
  - `String mutation`, id `3540`, line `53`: replaced the `parametersPerRow` out-of-range validation message with `""`
  - `String mutation`, id `3548`, line `64`: replaced the row-width failure message prefix with `$""`
  - `String mutation`, id `3549`, line `65`: replaced the row-width failure message suffix with `$""`
- Additional mutants killed by the same assertions:
  - Focused `PlanWriteBatchingConventions.cs` run also killed statement mutants ids `3539` and `3547` around the two exception throws.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanWriteBatchingConventionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanWriteBatchingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanWriteBatchingConventions"`: passed; `11` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.PlanWriteBatchingConventions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanWriteBatchingConventions.cs`: completed in `00:02:25`; `Killed: 13`, `Survived: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.22-32-21/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3540`, `3548`, and `3549` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanWriteBatchingConventions.tmp.json` was removed after verification.
  - Focused survivor id `3532`, line `31`, is a null-guard statement mutant and is skipped by the loop prompt.
  - Focused no-coverage mutant id `3551`, line `91`, is the unsupported-dialect message in the private `GetLimits` switch.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PlanWriteBatchingConventions Unsupported Dialect

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PlanWriteBatchingConventions.cs`
- Mutants selected:
  - `String mutation`, id `3551`, line `91`: replaced the unsupported SQL dialect message with `""`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanWriteBatchingConventionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanWriteBatchingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PlanWriteBatchingConventions"`: passed; `12` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.PlanWriteBatchingConventions.tmp.json` from the test project directory with a temporary focused config mutating only `**/PlanWriteBatchingConventions.cs`: completed in `00:02:28`; `Killed: 14`, `Survived: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.22-55-54/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MssqlPlanDialectTests.cs src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PgsqlPlanDialectTests.cs src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PlanWriteBatchingConventionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MssqlPlanDialect|FullyQualifiedName~Given_PgsqlPlanDialect|FullyQualifiedName~Given_PlanWriteBatchingConventions"`: passed; `15` tests.
- Verification:
  - Confirmed selected mutant id `3551` is `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PlanWriteBatchingConventions.tmp.json` was removed after verification.
  - Focused survivor id `3532`, line `31`, is a null-guard statement mutant and is skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans SimpleUpdateSqlEmitter Validation And Shape Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SimpleUpdateSqlEmitter.cs`
- Mutants selected:
  - `Statement mutation`, id `6274`, line `42`: replaced the empty set-column validation throw with `;`
  - `String mutation`, id `6275`, line `43`: replaced the empty set-column validation message with `""`
  - `Statement mutation`, id `6280`, line `50`: replaced the set-column/parameter count validation throw with `;`
  - `String mutation`, id `6281`, line `51`: replaced the set-column/parameter count validation message with `$""`
  - `Statement mutation`, id `6287`, line `58`: replaced the empty key-column validation throw with `;`
  - `String mutation`, id `6288`, line `59`: replaced the empty key-column validation message with `""`
  - `Statement mutation`, id `6293`, line `66`: replaced the key-column/parameter count validation throw with `;`
  - `String mutation`, id `6294`, line `67`: replaced the key-column/parameter count validation message with `$""`
- Additional mutants killed by the same assertions:
  - Focused `SimpleUpdateSqlEmitter.cs` run also killed SQL-shape and branch mutants ids `6297`, `6299`, `6300`, `6301`, `6304`, `6306`, `6308`, `6310`, `6312`, `6313`, `6314`, `6317`, `6318`, `6320`, `6321`, `6324`, `6326`, `6327`, and `6328`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleUpdateSqlEmitterTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleUpdateSqlEmitterTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SimpleUpdateSqlEmitter"`: passed; `6` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "**/SimpleUpdateSqlEmitter.cs"` from the test project directory: interrupted before completion after it appeared to create the full Backend Plans mutant set; no report was used.
  - `dotnet stryker --config-file stryker-config.SimpleUpdateSqlEmitter.tmp.json` from the test project directory with a temporary focused config mutating only `**/SimpleUpdateSqlEmitter.cs`: completed in `00:02:46`; `Killed: 27`, `Survived: 3`, `Timeout: 1`; report `StrykerOutput/2026-06-23.23-07-05/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6274`, `6275`, `6280`, `6281`, `6287`, `6288`, `6293`, and `6294` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.SimpleUpdateSqlEmitter.tmp.json` was removed after verification.
  - Focused survivor ids `6267`, `6268`, and `6269`, plus timeout id `6270`, are null-guard statement mutants on lines `35`-`38` and are skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans SimpleDeleteSqlEmitter Validation And Shape Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SimpleDeleteSqlEmitter.cs`
- Mutants selected:
  - `Statement mutation`, id `6216`, line `36`: replaced the empty where-column validation throw with `;`
  - `String mutation`, id `6217`, line `37`: replaced the empty where-column validation message with `""`
  - `Statement mutation`, id `6222`, line `44`: replaced the where-column/parameter count validation throw with `;`
  - `String mutation`, id `6223`, line `45`: replaced the where-column/parameter count validation message with `$""`
- Additional mutants killed by the same assertions:
  - Focused `SimpleDeleteSqlEmitter.cs` run also killed SQL-shape mutants ids `6226`, `6228`, `6229`, `6232`, `6234`, `6235`, and `6236`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleDeleteSqlEmitterTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleDeleteSqlEmitterTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SimpleDeleteSqlEmitter"`: passed; `4` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.SimpleDeleteSqlEmitter.tmp.json` from the test project directory with a temporary focused config mutating only `**/SimpleDeleteSqlEmitter.cs`: completed in `00:02:28`; `Killed: 12`, `Survived: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-13-13/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6216`, `6217`, `6222`, and `6223` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.SimpleDeleteSqlEmitter.tmp.json` was removed after verification.
  - Focused survivor id `6211`, line `31`, is a null-guard statement mutant and is skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans RelationshipAuthorizationStrategyClassifier Custom View And People Strategy Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationStrategyClassifier.cs`
- Mutants selected:
  - `Logical mutation`, id `6024`, line `116`: changed people-strategy detection from supported-and-includes-people to supported-or-includes-people.
  - `String mutation`, id `6068`, line `263`: replaced the unknown custom-view basis hint with `$""`.
  - `Equality mutation`, id `6078`, line `317`: changed the custom-view delimiter loop guard from `>= 0` to `> 0`.
  - `Logical mutation`, id `6083`, line `325`: changed complete custom-view delimiter validation from basis-and-suffix to basis-or-suffix.
  - `Equality mutation`, id `6086`, line `325`: allowed a leading `With` delimiter as a valid basis boundary.
  - `Equality mutation`, id `6088`, line `325`: allowed a trailing `With` delimiter as a valid suffix boundary.
  - `Arithmetic mutation`, id `6089`, line `325`: changed the trailing delimiter boundary from subtracting the delimiter length to adding it.
  - `String mutation`, id `6090`, line `325`: replaced the delimiter-length string with `""`.
  - `Linq method mutation (Max() to Min())`, id `6100`, line `353`: selected the first instead of last valid delimiter for unknown basis reporting.
  - `Linq method mutation (Max() to Min())`, id `6101`, line `358`: selected the shortest instead of longest matching basis resource.
  - `Statement mutation`, id `6109`, line `385`: removed project endpoint-order tracking for custom-view basis tie-breaking.
  - `Linq method mutation (ThenBy() to ThenByDescending())`, id `6112`, line `391`: reversed project endpoint-order tie-breaking.
  - `Linq method mutation (Any() to All())`, id `6117`, line `424`: required all eligible subjects to be people subjects.
  - `Equality mutation`, id `6118`, line `425`: changed people-subject kind detection.
  - `Logical mutation`, id `6120`, line `426`: changed people-subject kind matching from alternatives to conjunction.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationStrategyClassifierTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationStrategyClassifierTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_RelationshipAuthorizationStrategyClassifier"`: passed; `32` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.RelationshipAuthorizationStrategyClassifier.tmp.json` from the test project directory with a temporary focused config mutating only `**/RelationshipAuthorizationStrategyClassifier.cs`: completed in `00:02:19`; `Killed: 59`, `Survived: 5`, `Timeout: 3`; report `StrykerOutput/2026-06-23.23-20-29/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6024`, `6068`, `6078`, `6083`, `6086`, `6088`, `6089`, `6090`, `6100`, `6101`, `6109`, `6112`, `6117`, `6118`, and `6120` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.RelationshipAuthorizationStrategyClassifier.tmp.json` was removed after verification.
  - Focused survivor ids `6026` and `6027`, lines `125`-`126`, are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `6076`, line `316`, appears equivalent for externally visible delimiter behavior because subsequent loop iterations still find valid `With` delimiters.
  - Focused survivor id `6110`, line `391`, appears equivalent because `ResolvePreferredBasisResource` is only called with non-empty candidate resources.
  - Focused survivor id `6111`, line `391`, appears equivalent under the current mapping-set contract because distinct projects receive distinct endpoint-order values before the final project-name ordering tie-breaker.
  - Focused timeouts ids `6077`, `6080`, and `6081`, lines `317` and `320`, are loop-control mutations that prevent delimiter scanning from terminating.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans SimpleInsertSqlEmitter Empty Column Validation

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SimpleInsertSqlEmitter.cs`
- Mutants selected:
  - `Statement mutation`, id `6243`, line `33`: replaced the empty-column validation throw with `;`
  - `String mutation`, id `6244`, line `33`: replaced the empty-column validation message with `""`
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleInsertSqlEmitterTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/SimpleInsertSqlEmitterTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SimpleInsertSqlEmitter"`: passed; `8` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.SimpleInsertSqlEmitter.tmp.json` from the test project directory with a temporary focused config mutating only `**/SimpleInsertSqlEmitter.cs`: first run completed in `00:02:23`; `Killed: 7`, `Survived: 5`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-26-41/reports/mutation-report.json`.
  - `dotnet stryker --config-file stryker-config.SimpleInsertSqlEmitter.tmp.json` from the test project directory after tightening the empty-column test input: completed in `00:02:23`; `Killed: 8`, `Survived: 4`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-29-46/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6243` and `6244` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.SimpleInsertSqlEmitter.tmp.json` was removed after verification.
  - Focused survivor ids `6238`, `6239`, `6254`, and `6255` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused no-coverage mutant id `6263`, line `72`, is in the null parameter-row branch and is skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans DescriptorQueryCapabilityCompiler Descriptor Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/DescriptorQueryCapabilityCompiler.cs`
- Mutants selected:
  - `Linq method mutation (OrderBy() to OrderByDescending())`, id `529`, line `133`: reversed deterministic case-insensitive collision group ordering.
  - `String mutation`, id `537`, line `151`: replaced the collision-group separator with `""`.
  - `Logical mutation`, id `550`, line `188`: changed query-field mismatch detection from path-count-or-path/type mismatch to path-count-and-path mismatch.
  - `Linq method mutation (OrderBy() to OrderByDescending())`, id `559`, line `208`: reversed unexpected field ordering.
  - `String mutation`, id `561`, line `211`: replaced unexpected field quoting with `$""`.
  - `Equality mutation`, id `580`, line `227`: changed unexpected-field presence check from `> 0` to `< 0`.
  - `Statement mutation`, id `584`, line `229`: removed unexpected-field summary append.
  - `String mutation`, id `585`, line `229`: replaced unexpected-field message prefix with `$""`.
  - `String mutation`, id `586`, line `229`: replaced unexpected-field join separator with `""`.
  - `String mutation`, id `588`, line `233`: replaced the mismatch reason prefix with `""`.
  - `String mutation`, id `590`, line `239`: replaced formatted query-field path separator with `""`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MappingSetCompiler"`: passed; `17` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.DescriptorQueryCapabilityCompiler.tmp.json` from the test project directory with a temporary focused config mutating only `**/DescriptorQueryCapabilityCompiler.cs`: completed in `00:03:47`; `Killed: 78`, `Survived: 5`, `Timeout: 4`; report `StrykerOutput/2026-06-23.23-36-25/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `529`, `537`, `550`, `559`, `561`, `580`, `584`, `585`, `586`, `588`, and `590` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.DescriptorQueryCapabilityCompiler.tmp.json` was removed after verification.
  - Focused no-coverage mutants ids `505`, `506`, `507`, `508`, and `509` are unsupported-storage and missing-descriptor-metadata diagnostics outside this selected cluster.
  - Focused survived/timeout mutants ids `517`, `519`, `521`, `523`, `525`, `526`, and `527` are missing descriptor-column diagnostics and are skipped by the loop prompt because they require null descriptor metadata columns.
  - Focused survivor id `542`, line `161`, appears equivalent after the collision check because the pre-`ToDictionary` ordering of query-field mappings is not externally observable.
  - Focused timeouts ids `482`, `483`, and `489`, lines `21`, `22`, and `24`, are descriptor field-definition string mutations outside this selected diagnostics cluster.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans PageDocumentIdAuthorizationSpecAdapter Failure Branches

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageDocumentIdAuthorizationSpecAdapter.cs`
- Mutants selected:
  - `String mutation`, id `2463`, line `21`: replaced the missing claim EducationOrganization parameterization message with `""`.
  - `Statement mutation`, id `2481`, line `72`: removed the non-root EdOrg subject rejection.
  - `String mutation`, id `2482`, line `73`: replaced the non-root EdOrg subject message prefix with `$""`.
  - `String mutation`, id `2483`, line `74`: replaced the non-root EdOrg subject message suffix with `""`.
  - `Statement mutation`, id `2487`, line `94`: removed the mismatched people stored-anchor root rejection.
  - `String mutation`, id `2488`, line `95`: replaced the mismatched people stored-anchor root message with `$""`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageDocumentIdAuthorizationSpecAdapterTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageDocumentIdAuthorizationSpecAdapterTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageDocumentIdAuthorizationSpecAdapter"`: passed; `14` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.PageDocumentIdAuthorizationSpecAdapter.tmp.json` from the test project directory with a temporary focused config mutating only `**/PageDocumentIdAuthorizationSpecAdapter.cs`: completed in `00:01:54`; `Killed: 10`, `Survived: 2`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-43-26/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2463`, `2481`, `2482`, `2483`, `2487`, and `2488` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.PageDocumentIdAuthorizationSpecAdapter.tmp.json` was removed after verification.
  - Focused survivor ids `2462`, line `16`, and `2476`, line `63`, are null-guard statement mutants and are skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans NamespaceAuthorizationSqlCompiler SQL Block Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/NamespaceAuthorizationSqlCompiler.cs`
- Mutants selected:
  - `String mutation`, id `2169`, line `121`: replaced the unsupported value-source message with `""`.
  - `Statement mutation`, id `2173`, line `140`: removed the stored-check `SELECT CASE` append.
  - `String mutation`, id `2174`, line `140`: replaced the stored-check `SELECT CASE` text with `""`.
  - `Statement mutation`, id `2208`, line `185`: removed the stored-uninitialized branch newline.
  - `Statement mutation`, id `2217`, line `195`: removed the stored-target-missing branch newline.
  - `Statement mutation`, id `2222`, line `200`: removed the stored mismatch branch newline.
  - `Statement mutation`, id `2223`, line `203`: removed the stored-check `END` append.
  - `String mutation`, id `2225`, line `203`: replaced the stored-check `END` text with `""`.
  - `Statement mutation`, id `2227`, line `212`: removed the proposed-check `SELECT CASE` append.
  - `String mutation`, id `2228`, line `212`: replaced the proposed-check `SELECT CASE` text with `""`.
  - `Statement mutation`, id `2238`, line `228`: removed the proposed-missing branch newline.
  - `Statement mutation`, id `2249`, line `242`: removed the proposed mismatch branch newline.
  - `Statement mutation`, id `2250`, line `245`: removed the proposed-check `END` append.
  - `String mutation`, id `2252`, line `245`: replaced the proposed-check `END` text with `""`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationSqlCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationSqlCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_NamespaceAuthorizationSqlCompiler"`: passed; `14` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.NamespaceAuthorizationSqlCompiler.tmp.json` from the test project directory with a temporary focused config mutating only `**/NamespaceAuthorizationSqlCompiler.cs`: completed in `00:02:00`; `Killed: 116`, `Survived: 6`, `NoCoverage: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-49-10/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2169`, `2173`, `2174`, `2208`, `2217`, `2222`, `2223`, `2225`, `2227`, `2228`, `2238`, `2249`, `2250`, and `2252` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.NamespaceAuthorizationSqlCompiler.tmp.json` was removed after verification.
  - Focused survivors ids `2142`, `2143`, and `2144`, lines `57`-`59`, are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivors ids `2145` and `2146`, lines `60` and `64`, are bare-parameter-name validation mutants outside this selected SQL block-shape cluster.
  - Focused survivor id `2153`, line `81`, and no-coverage id `2287`, line `281`, are unsupported-dialect defensive-message paths that are unreachable through the public compiler constructor because `SqlDialectFactory.Create` rejects unsupported dialect values first.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-23 - Backend Plans NamespaceAuthorizationSqlCompiler Parameter Name Validation

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/NamespaceAuthorizationSqlCompiler.cs`
- Mutants selected:
  - `Statement mutation`, id `2145`, line `60`: removed `DocumentIdParameterName` bare-parameter-name validation.
  - `Statement mutation`, id `2146`, line `64`: removed `ProposedNamespaceParameterName` bare-parameter-name validation.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationSqlCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationSqlCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_NamespaceAuthorizationSqlCompiler"`: passed; `16` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.NamespaceAuthorizationSqlCompiler.tmp.json` from the test project directory with a temporary focused config mutating only `**/NamespaceAuthorizationSqlCompiler.cs`: completed in `00:02:02`; `Killed: 118`, `Survived: 4`, `NoCoverage: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-23.23-54-22/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2145` and `2146` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.NamespaceAuthorizationSqlCompiler.tmp.json` was removed after verification.
  - Focused survivors ids `2142`, `2143`, and `2144`, lines `57`-`59`, are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `2153`, line `81`, and no-coverage id `2287`, line `281`, remain unsupported-dialect defensive-message paths that are unreachable through the public compiler constructor because `SqlDialectFactory.Create` rejects unsupported dialect values first.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans TokenInfoEducationOrganizationSqlCompiler Final Select Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs`
- Mutants selected:
  - `Statement mutation`, id `7513`, line `445`: removed final `SELECT`.
  - `String mutation`, id `7514`, line `445`: replaced final `SELECT` with `""`.
  - `Statement mutation`, id `7516`, line `448`: removed target EducationOrganizationId column append.
  - `String mutation`, ids `7518` and `7519`, lines `449` and `451`: removed target EducationOrganizationId alias pieces.
  - `Statement mutation`, id `7520`, line `452`: removed target NameOfInstitution column append.
  - `String mutation`, ids `7522` and `7523`, lines `453` and `455`: removed target NameOfInstitution alias pieces.
  - `Statement mutation`, id `7524`, line `456`: removed target Discriminator column append.
  - `String mutation`, ids `7526` and `7527`, lines `457` and `459`: removed target Discriminator alias pieces.
  - `Statement mutation`, id `7528`, line `460`: removed ancestor Discriminator column append.
  - `String mutation`, ids `7530` and `7531`, lines `461` and `463`: removed ancestor Discriminator alias pieces.
  - `Statement mutation`, id `7532`, line `464`: removed ancestor EducationOrganizationId column append.
  - `String mutation`, id `7534`, line `465`: removed ancestor EducationOrganizationId alias separator.
  - `Statement mutation`, ids `7535` and `7537`, lines `470` and `471`: removed final result source and target join lines.
  - `String mutation`, ids `7536` and `7538`, lines `470` and `471`: removed final result source and target join text.
  - `Statement mutation`, ids `7540`, `7543`, and `7546`, lines `474`, `475`, and `476`: removed target join predicate pieces.
  - `Statement mutation`, ids `7547`, `7550`, `7553`, and `7556`, lines `479`, `482`, `483`, and `484`: removed ancestor link join and predicate pieces.
- Additional mutants killed by the same assertion:
  - Focused `TokenInfoEducationOrganizationSqlCompiler.cs` run also killed final ancestor join and ordering mutants ids `7557`, `7558`, `7560`, `7562`, `7563`, `7565`, `7566`, `7567`, `7568`, `7570`, `7571`, `7572`, `7573`, `7574`, `7575`, `7576`, `7577`, and `7580`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_TokenInfoEducationOrganizationSqlCompiler"`: passed; `8` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.TokenInfoEducationOrganizationSqlCompiler.tmp.json` using focused relative mutate paths was interrupted or produced an all-ignored report; relative paths did not match Stryker's absolute source keys in this solution-context run.
  - `dotnet stryker --config-file stryker-config.TokenInfoEducationOrganizationSqlCompiler.tmp.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:20`; `Killed: 108`, `Survived: 79`, `Timeout: 0`; report `StrykerOutput/2026-06-24.00-08-20/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `7513`, `7514`, `7516`, `7518`, `7519`, `7520`, `7522`, `7523`, `7524`, `7526`, `7527`, `7528`, `7530`, `7531`, `7532`, `7534`, `7535`, `7536`, `7537`, `7538`, `7540`, `7543`, `7546`, `7547`, `7550`, `7553`, and `7556` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.TokenInfoEducationOrganizationSqlCompiler.tmp.json` was removed after verification.
  - Focused survivors in `TokenInfoEducationOrganizationSqlCompiler.cs` remain outside this selected final SELECT cluster, including earlier CTE shape and validation diagnostics.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.
