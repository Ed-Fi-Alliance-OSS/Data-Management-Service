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

## 2026-06-24 - Backend Plans TokenInfoEducationOrganizationSqlCompiler Validation Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs`
- Mutants selected:
  - Dialect validation mutants ids `7302` and `7303`, lines `71`-`72`, and ids `7306` and `7307`, lines `79`-`80`.
  - EducationOrganization union view count mutants ids `7313`, `7317`, and `7318`, lines `107`-`110`.
  - Union member validation mutants ids `7328`, `7329`, `7332`, and `7333`, lines `139`-`147`.
  - Output column diagnostics mutants ids `7336`, `7337`, `7343`, and `7344`, lines `182`, `185`, `208`, and `211`.
  - Union arm projection diagnostics mutants ids `7347`, `7351`, `7352`, `7353`, `7356`, `7360`, `7361`, and `7362`, lines `222`-`251`.
  - NameOfInstitution duplicate-column diagnostic mutant id `7368`, line `272`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_TokenInfoEducationOrganizationSqlCompiler"`: first run failed because two expected messages used `EducationOrganizationId` instead of the compiler's `EducationOrganizationIdColumn` diagnostic label.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_TokenInfoEducationOrganizationSqlCompiler"` after fixing expectations: passed; `22` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:14`; `Killed: 133`, `Survived: 75`, `Timeout: 0`; report `StrykerOutput/2026-06-24.00-28-09/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `7302`, `7303`, `7306`, `7307`, `7313`, `7317`, `7318`, `7328`, `7329`, `7332`, `7333`, `7336`, `7337`, `7343`, `7344`, `7347`, `7351`, `7352`, `7353`, `7356`, `7360`, `7361`, `7362`, and `7368` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors in `TokenInfoEducationOrganizationSqlCompiler.cs` are outside this selected validation-diagnostics cluster. Remaining survivors include null-guard/parameterization guard mutants at lines `41`-`49` and SQL CTE text-shape mutants at lines `289`-`440`.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans TokenInfoEducationOrganizationSqlCompiler CTE SQL Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs`
- Mutants selected:
  - BuildSql CTE separator statement/string mutants ids `7374` and `7375`, line `289`; ids `7377` and `7378`, line `292`; ids `7380` and `7381`, line `294`.
  - Concrete EducationOrganization projection CTE shape mutants: equality/negate mutants ids `7390`, `7391`, and `7392`, line `308`; statement/string mutants ids `7394`, `7395`, `7396`, `7397`, `7402`, `7403`, `7405`, and `7406`, lines `310`-`325`.
  - Claimed EducationOrganization CTE shape statement/string mutants ids `7419`, `7420`, `7422`, `7423`, `7424`, `7425`, `7426`, `7427`, `7428`, `7433`, `7434`, and `7436`, lines `344`-`364`.
  - Accessible targets CTE shape statement/string mutants ids `7441`, `7442`, `7444`, `7446`, `7447`, `7449`, `7450`, `7451`, `7452`, `7454`, `7456`, `7461`, `7462`, `7463`, `7464`, `7466`, `7467`, `7468`, `7469`, and `7471`, lines `372`-`400`.
  - Ancestor links CTE shape statement/string mutants ids `7476`, `7477`, `7479`, `7480`, `7481`, `7482`, `7484`, `7485`, `7486`, `7487`, `7489`, `7491`, `7496`, `7497`, `7498`, `7499`, `7504`, `7508`, `7509`, `7510`, and `7511`, lines `408`-`440`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/TokenInfoEducationOrganizationSqlCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_TokenInfoEducationOrganizationSqlCompiler"`: passed; `23` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:10`; `Killed: 203`, `Survived: 5`, `Timeout: 0`; report `StrykerOutput/2026-06-24.00-33-57/reports/mutation-report.json`.
- Verification:
  - Confirmed selected CTE SQL shape mutant ids `7374`, `7375`, `7377`, `7378`, `7380`, `7381`, `7390`, `7391`, `7392`, `7394`, `7395`, `7396`, `7397`, `7402`, `7403`, `7405`, `7406`, `7419`, `7420`, `7422`, `7423`, `7424`, `7425`, `7426`, `7427`, `7428`, `7433`, `7434`, `7436`, `7441`, `7442`, `7444`, `7446`, `7447`, `7449`, `7450`, `7451`, `7452`, `7454`, `7456`, `7461`, `7462`, `7463`, `7464`, `7466`, `7467`, `7468`, `7469`, `7471`, `7476`, `7477`, `7479`, `7480`, `7481`, `7482`, `7484`, `7485`, `7486`, `7487`, `7489`, `7491`, `7496`, `7497`, `7498`, `7499`, `7504`, `7508`, `7509`, `7510`, and `7511` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `7293`, `7294`, `7295`, `7297`, and `7298`, lines `41`-`49`, are null/spec parameterization guard mutants and are skipped by the loop prompt.
  - Focused `TokenInfoEducationOrganizationSqlCompiler.cs` has no remaining actionable non-null survived, no-coverage, or timeout mutants.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans AuthorizationClaimEducationOrganizationIdParameterization Factory Validation

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationClaimEducationOrganizationIdParameterization.cs`
- Mutants selected:
  - `Statement mutation`, id `2`, line `55`: removed base parameter-name validation.
  - `Statement mutation`, id `6`, line `64`: removed empty claim EdOrg id list rejection.
  - `String mutation`, id `7`, line `65`: replaced the empty-list validation message with `""`.
  - `String mutation`, id `10`, line `97`: replaced the unsupported SQL dialect message with `$""`.
- Additional mutants killed by the same assertions:
  - Focused `AuthorizationClaimEducationOrganizationIdParameterization.cs` run also killed ordering and threshold branch mutants ids `3`, `4`, `8`, and `9`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/AuthorizationClaimEducationOrganizationIdParameterizationFactoryTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/AuthorizationClaimEducationOrganizationIdParameterizationFactoryTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_AuthorizationClaimEducationOrganizationIdParameterizationFactory"`: passed; `9` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/AuthorizationClaimEducationOrganizationIdParameterization.cs" --concurrency 8` from the test project directory: completed in `00:01:57`; `Killed: 8`, `Survived: 2`, `Timeout: 0`; report `StrykerOutput/2026-06-24.00-39-15/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2`, `6`, `7`, and `10` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivor id `1`, line `54`, is a null-guard statement mutant and is skipped by the loop prompt.
  - Focused survivor id `13`, line `106`, appears equivalent because any valid bare base parameter name plus the factory-generated `_<index>` suffix still matches the same bare parameter-name pattern.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans AUTH1 Payload And Namespace Mapper Boundaries

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production files:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationAuth1FailurePayload.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans/NamespaceAuthorizationAuth1FailurePayload.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans/NamespaceAuthorizationFailureMapper.cs`
- Mutants selected:
  - Namespace payload constructor validation mutants ids `2049` and `2050`, lines `49` and `52`.
  - Namespace payload unsupported-kind encoding message mutant id `2084`, line `137`.
  - Namespace mapper emitted-index boundary mutant id `2104`, line `36`.
  - Namespace mapper unsupported value-source message mutant id `2117`, line `106`.
  - Relationship subject ordinal validation mutants ids `5176`, `5177`, `5182`, and `5183`, lines `34`-`46`.
  - Relationship payload emitted-index and empty-subject validation mutants ids `5187`, `5190`, and `5191`, lines `74`-`79`.
  - Relationship parser/extractor boundary mutants ids `5208`, `5234`, `5242`, `5249`, `5294`, `5298`, `5300`, `5308`, `5314`, `5323`, `5326`, and `5329`, lines `129`, `183`, `213`, `228`, `334`, `344`, `347`, `356`, `365`, and `373`.
  - Relationship unsupported-kind encoding message mutant id `5282`, line `300`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationAuth1FailurePayloadTests.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/NamespaceAuthorizationFailureMapperTests.cs`
- Commands run and results:
  - `dotnet csharpier format RelationshipAuthorizationAuth1FailurePayloadTests.cs NamespaceAuthorizationAuth1FailurePayloadTests.cs NamespaceAuthorizationFailureMapperTests.cs`: succeeded.
  - `dotnet test EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_RelationshipAuthorizationAuth1FailurePayloadCodec|FullyQualifiedName~Given_NamespaceAuthorizationAuth1FailurePayloadCodec|FullyQualifiedName~Given_NamespaceAuthorizationFailureMapper"`: passed; final run `69` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.Auth1Transport.tmp.json --concurrency 8` from the test project directory with a temporary focused config mutating the three selected AUTH1 files: first run completed in `00:01:57`; `Killed: 141`, `Survived: 11`, `NoCoverage: 4`, `Timeout: 1`.
  - `dotnet stryker --config-file stryker-config.Auth1Transport.tmp.json --concurrency 8` after adding relationship parser/extractor boundary assertions: completed in `00:01:59`; `Killed: 146`, `Survived: 9`, `NoCoverage: 1`, `Timeout: 1`; report `StrykerOutput/2026-06-24.00-49-17/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `2049`, `2050`, `2084`, `2104`, `2117`, `5176`, `5177`, `5182`, `5183`, `5187`, `5190`, `5191`, `5208`, `5234`, `5242`, `5249`, `5282`, `5294`, `5298`, `5300`, `5308`, `5314`, `5323`, `5326`, and `5329` are `Killed` in the focused JSON report.
- Remaining notes:
  - Temporary focused config `stryker-config.Auth1Transport.tmp.json` was removed after verification.
  - Focused survivors ids `2052`, `2096`, `2097`, `2098`, `5185`, and `5198` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused no-coverage id `2116`, line `91`, is an unsupported namespace failure-kind mapping message that is unreachable because unknown namespace AUTH1 failure kinds fail compatibility before mapping.
  - Focused survivors ids `5272`, `5275`, and `5278`, lines `282` and `288`, appear equivalent for relationship positive-count parsing because accepting `0` still fails on the subject-failure section count before producing a payload.
  - Focused timeout id `5310`, line `360`, is a loop-control mutation that prevents SQL Server payload extraction from advancing.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans MappingSetLookupExtensions Read Lookup Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/MappingSetLookupExtensions.cs`
- Mutants selected:
  - `String mutation`, id `1899`, line `52`: replaced the mapping-set key portion of the missing relational read-plan diagnostic with `$""`.
  - `Statement mutation`, id `1902`, line `58`: removed the unknown storage-kind read-plan lookup exception.
  - `String mutation`, id `1903`, line `59`: replaced the unknown storage-kind resource/key diagnostic prefix with `$""`.
  - `String mutation`, id `1904`, line `60`: replaced the unknown storage-kind value diagnostic with `$""`.
  - `String mutation`, id `1905`, line `61`: replaced the unknown storage-kind suffix with `""`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetLookupExtensionsTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/MappingSetLookupExtensionsTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_MappingSetLookupExtensions"`: passed; `32` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/MappingSetLookupExtensions.cs" --concurrency 8` from the test project directory: completed in `00:01:49`; `Killed: 22`, `Survived: 1`, `Timeout: 0`; report `StrykerOutput/2026-06-24.00-55-09/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `1899`, `1902`, `1903`, `1904`, and `1905` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivor id `1887`, line `31`, is a null-guard statement mutant and is skipped by the loop prompt.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Stored Target CTE Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - `Statement mutation` and `String mutation` ids `6507`-`6518`, lines `560`-`568`: removed or altered stored SQL assembly calls for the target CTE, subject failures CTE, failed subjects CTE, failure payload CTE, and final stored select.
  - `Boolean mutation` ids `6520` and `6521`, line `568`: changed the stored final select flags for content version and target source.
  - `Statement mutation` and `String mutation` ids `6538`, `6539`, `6541`, `6542`, `6544`, `6546`-`6549`, `6555`, `6557`-`6559`, `6566`, `6567`, and `6568`-`6598`, lines `601`-`635`: removed or altered the `WITH target` header, ordered root-column projection, root/document joins, and document-id filter.
  - `Equality mutation`, `PostIncrementExpression to PostDecrementExpression mutation`, `Conditional mutation`, and `Arithmetic mutation` ids `6551`, `6553`, `6560`, `6561`, `6563`, and `6564`, lines `613`-`617`: changed target CTE root-column loop ordering and comma behavior.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; final run `38` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: first focused run completed in `00:02:50`; `Killed: 409`, `Survived: 199`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-01-42/reports/mutation-report.json`.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` after adding CTE transition assertions: completed in `00:02:46`; `Killed: 436`, `Survived: 172`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-05-26/reports/mutation-report.json`.
- Verification:
  - Confirmed selected stored SQL assembly mutants ids `6507`-`6518`, `6520`, and `6521` are `Killed` in the final focused JSON report, with id `6519` a compile error.
  - Confirmed selected target CTE shape and ordering mutants in lines `601`-`635` are `Killed` in the final focused JSON report, with append-to-prepend and invalid count mutants either compile errors or ignored by Stryker.
- Remaining notes:
  - Focused survivors remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected stored target CTE cluster, including normalization/validation, proposed SQL assembly, final select internals, and subject failure payload shape.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Validation Boundaries

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Document parameter-name validation `Statement mutation` id `6339`, lines `68`-`71`.
  - Emitted AUTH1 index validation mutants ids `6341`, `6344`, and `6345`, lines `73`-`78`.
  - Empty check-spec and empty-subject validation mutants ids `6349`, `6350`, `6352`, `6356`, and `6357`, lines `84`-`93`.
  - Subject root-table and parameter collision validation `Statement mutation` ids `6359` and `6363`, lines `103` and `110`.
  - Homogeneous target/value-source normalization mutants ids `6365`, `6369`, `6371`, and `6375`, lines `129`-`140`.
  - Stored and proposed root-target normalization mutants ids `6381`, `6382`, `6386`, `6387`, `6390`, `6393`, and `6394`, lines `161`-`186`.
  - Proposed binding validation mutants ids `6404`, `6405`, `6415`, `6416`, `6419`, `6420`, `6423`, `6424`, and `6425`, lines `198`-`234`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; `54` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:53`; `Killed: 471`, `Survived: 159`, `NoCoverage: 32`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-14-47/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6339`, `6341`, `6344`, `6345`, `6349`, `6350`, `6352`, `6356`, `6357`, `6359`, `6363`, `6365`, `6369`, `6371`, `6375`, `6381`, `6382`, `6386`, `6387`, `6390`, `6393`, `6394`, `6404`, `6405`, `6415`, `6416`, `6419`, `6420`, `6423`, `6424`, and `6425` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `6337` and `6338`, lines `66`-`67`, are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivors remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected validation-boundary cluster, including proposed SQL assembly, final select internals, and subject failure payload shape.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Proposed CTE And Payload Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Proposed SQL assembly statement/string/boolean mutants ids `6523`, `6524`, `6526`, `6527`, `6528`, `6529`, `6530`, `6532`, and `6535`, lines `577`-`587`.
  - Failure payload CTE projection and source mutants ids `7082`, `7084`, `7088`, `7090`, `7091`, `7092`, `7093`, and `7094`, lines `1404`-`1423`.
  - Final SELECT/auth result statement/string mutants ids `7102`, `7104`, `7106`, `7107`, `7108`, `7110`, `7112`, `7128`, and `7129`, lines `1440`-`1465`.
  - PostgreSQL and SQL Server payload ordering/abort mutants ids `7140`, `7141`, `7143`, `7144`, `7145`, `7147`, `7148`, `7149`, `7151`, `7152`, `7153`, `7155`, `7156`, `7157`, `7159`, `7170`, `7171`, `7173`, `7174`, `7175`, `7177`, `7178`, `7179`, `7181`, `7182`, `7183`, `7185`, `7186`, `7187`, `7189`, `7192`, `7196`, `7235`, `7236`, `7238`, `7239`, `7241`, and `7243`, lines `1476`-`1541`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: first run failed because the expected final SQL suffix omitted the terminal newline; final run passed with `56` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:53`; `Killed: 543`, `Survived: 87`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-22-25/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6523`, `6524`, `6526`, `6527`, `6528`, `6529`, `6530`, `6532`, `6535`, `7082`, `7084`, `7088`, `7090`, `7091`, `7092`, `7093`, `7094`, `7102`, `7104`, `7106`, `7107`, `7108`, `7110`, `7112`, `7128`, `7129`, `7140`, `7141`, `7143`, `7144`, `7145`, `7147`, `7148`, `7149`, `7151`, `7152`, `7153`, `7155`, `7156`, `7157`, `7159`, `7170`, `7171`, `7173`, `7174`, `7175`, `7177`, `7178`, `7179`, `7181`, `7182`, `7183`, `7185`, `7186`, `7187`, `7189`, `7192`, `7196`, `7235`, `7236`, `7238`, `7239`, `7241`, and `7243` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected proposed CTE/payload cluster, including stored subject failure SQL, stored/proposed People path SQL, parameter naming/collision diagnostics, and unsupported-dialect diagnostics.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Parameter Allocation And Validation

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Proposed value parameter reservation mutants ids `6482`, `6488`, `6490`, `6492`, and `6494`, lines `475`, `487`, `494`, `504`, and `512`: removed document-id, claim-base, claim-concrete, binding-seed, and bare-name validation reservations from the proposed parameter allocator.
  - Proposed value suffix allocation mutants ids `6500`, `6503`, and `6504`, lines `526`-`529`: changed the occupied-name loop condition, decremented the suffix, or replaced the next suffixed candidate with an empty string.
  - Stored reserved-parameter validation mutant id `7273`, line `1586`: removed validation of explicit reserved parameter names for stored specs.
- Additional mutants killed by the same assertions:
  - Reservation and allocation mutants ids `6485`, `6487`, `6495`, `6497`, `6498`, and `6499`.
  - Reserved-parameter null-coalescing mutant id `7271`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; final run `62` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:49`; `Killed: 553`, `Survived: 80`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-34-16/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6482`, `6488`, `6490`, `6492`, `6494`, `6500`, `6503`, `6504`, and `7273` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivor id `6484`, line `479`, appears equivalent for the current generated parameter-name shape because proposed value parameter names always start with `relationshipAuthorization_`, so the bare default reserved names `documentUuid` and `resourceKeyId` cannot collide with allocator candidates.
  - Focused timeout id `6502`, line `528`, is a loop-control mutation: removing the suffix increment causes the occupied-name loop to stop making progress when the first suffixed candidate is already reserved.
  - Focused survivors remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected parameter-allocation cluster, including stored/proposed subject failure SQL, People path SQL, duplicate defensive validation, and unsupported-dialect diagnostics.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Stored Subject Failure SQL Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Stored subject failure `UNION ALL` and emitted-subject index mutants ids `6626`, `6627`, `6628`, `6630`, `6631`, `6633`, and `6634`, lines `669`, `671`, and `681`.
  - Stored EdOrg subject failure SELECT shape mutants ids `6646`, `6647`, `6649`, `6650`, `6651`, `6652`, `6660`, and `6671`, lines `732`, `736`, `737`, `741`, and `755`.
  - Stored direct People subject failure SELECT shape mutants ids `6676`, `6677`, `6679`, `6680`, `6681`, `6682`, `6690`, `6691`, `6693`, `6697`, `6698`, and `6699`, lines `770`, `774`, `775`, `785`, `786`, `799`, and `800`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; `62` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:03:04`; `Killed: 580`, `Survived: 52`, `NoCoverage: 29`, `Timeout: 2`; report `StrykerOutput/2026-06-24.01-41-14/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6626`, `6627`, `6628`, `6630`, `6631`, `6633`, `6634`, `6646`, `6647`, `6649`, `6650`, `6651`, `6652`, `6660`, `6671`, `6676`, `6677`, `6679`, `6680`, `6681`, `6682`, `6690`, `6691`, `6693`, `6697`, `6698`, and `6699` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected stored subject-failure SQL cluster, including stored/proposed People path SQL, proposed subject failure SQL, null guards, duplicate defensive validation, and unsupported-dialect diagnostics.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Proposed Subject Failure SQL Shape

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Proposed subject failure `UNION ALL` and emitted-subject index mutants ids `6829`, `6830`, `6831`, `6833`, and `6834`, lines `1020` and `1022`.
  - Proposed direct subject failure SELECT shape mutants ids `6845`, `6847`, `6848`, `6850`, `6851`, `6852`, `6853`, `6861`, and `6872`, lines `1073`-`1102`.
  - Proposed transitive People invalid-data SELECT and wrapper shape mutants ids `6875`, `6876`, `6878`, `6879`, `6880`, `6881`, `6889`, `6903`, `6904`, `6905`, `6915`, `6916`, `6918`, and `6919`, lines `1118`-`1163`.
  - Proposed transitive People multi-hop join loop and SQL-shape mutants ids `6957`, `6961`, `6966`, `6968`, `6969`, `6970`, `6972`, `6973`, `6974`, `6976`, and `6977`, lines `1259` and `1274`-`1279`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; `62` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:53`; `Killed: 621`, `Survived: 22`, `NoCoverage: 19`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-50-02/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6829`, `6830`, `6831`, `6833`, `6834`, `6845`, `6847`, `6848`, `6850`, `6851`, `6852`, `6853`, `6861`, `6872`, `6875`, `6876`, `6878`, `6879`, `6880`, `6881`, `6889`, `6903`, `6904`, `6905`, `6915`, `6916`, `6918`, `6919`, `6957`, `6961`, `6966`, `6968`, `6969`, `6970`, `6972`, `6973`, `6974`, `6976`, and `6977` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors and no-coverage mutants remain in `SingleRecordRelationshipAuthorizationSqlCompiler.cs` outside this selected proposed subject-failure SQL cluster, including early diagnostics, stored People success SQL, unsupported People path-kind messages, defensive null/message paths, and duplicate/unsupported-dialect validation diagnostics.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Stored People Path Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - People stored-anchor diagnostic string mutant id `6445`, line `305`: replaced the root-table description with `""`.
  - Self People path validation statement mutant id `6457`, line `354`: removed self root DocumentId validation.
  - Direct People path diagnostic string mutant id `6458`, line `368`: replaced the root-table description with `""`.
  - Stored self People authorization success wrapper mutants ids `6718`, `6720`, `6722`, and `6724`, lines `855` and `867`: removed or emptied the `EXISTS (...)` wrapper.
  - Stored transitive People authorization success wrapper mutants ids `6732` and `6734`, line `885`: removed or emptied the outer `EXISTS (` wrapper.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; `65` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:51`; `Killed: 630`, `Survived: 13`, `NoCoverage: 19`, `Timeout: 1`; report `StrykerOutput/2026-06-24.01-58-49/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6445`, `6457`, `6458`, `6718`, `6720`, `6722`, `6724`, `6732`, and `6734` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `6335`, `6337`, and `6338` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivors ids `6456`, `6459`, `6461`, and `6748` appear redundant for the public `Compile` behavior: the same malformed People path inputs are rejected by earlier or later validation on the same compile path.
  - Focused no-coverage mutants ids `6432`, `6433`, `6438`, `6439`, `6460`, `6716`, `6746`, `6766`, `6767`, `6947`, `6948`, `6963`, `6964`, `7087`, `7261`, `7284`, `7285`, `7290`, and `7291` remain in defensive or unsupported-path diagnostics outside this selected cluster.
  - Focused timeout id `6502`, line `528`, remains a loop-control mutation in proposed value suffix allocation.
  - Broad target re-run was skipped because the previous broad run took about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans SingleRecordRelationshipAuthorizationSqlCompiler Transitive Path And Final Select Assertions

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- Mutants selected:
  - Proposed transitive People path validation `Statement mutation` id `6946`, line `1235`: removed `ValidateTransitivePersonPath` before proposed transitive path SQL generation.
  - Stored final-select `Statement mutation` id `7127`, line `1462`: removed the `return` after writing `FROM target;`, allowing an extra standalone semicolon.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: passed; `66` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs" --concurrency 8` from the test project directory: completed in `00:02:59`; `Killed: 629`, `Survived: 11`, `NoCoverage: 17`, `Timeout: 4`; report `StrykerOutput/2026-06-24.02-07-08/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `6946` and `7127` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `6335`, `6337`, and `6338` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivors ids `6456`, `6459`, `6461`, `6484`, `6748`, `7269`, `7279`, and `7287` appear redundant or unreachable through public `Compile` behavior because earlier validation, constructor dialect validation, or proposed-parameter allocation prevents the mutated branch from changing externally visible behavior.
  - Focused no-coverage mutants ids `6432`, `6433`, `6438`, `6439`, `6460`, `6716`, `6746`, `6766`, `6767`, `6947`, `6948`, `6963`, `6964`, `7087`, `7261`, `7284`, `7285`, `7290`, and `7291` remain in static metadata mismatch, unsupported-dialect, duplicate-parameter, or defensive path diagnostics outside public compiler inputs.
  - Focused timeout ids `6331`, `6332`, `6430`, and `6502` remain in static constant/type-check or loop-control mutations; id `6502` is the previously noted proposed value suffix allocation timeout.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans ReadPlanProjectionContractValidator Projection Gating

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs`
- Mutants selected:
  - Non-relational short-circuit `Statement mutation` id `3787`, line `26`: removed early `return`.
  - Required projection gate/logical mutants ids `3788`, `3792`, `3796`, and `3797`, lines `30`, `33`, and `36`.
  - Missing projection diagnostic mutants ids `3801`, `3802`, `3803`, `3805`, `3807`, `3808`, and `3810`, lines `44`-`53`.
  - Unexpected reference projection-plan gate mutants ids `3811`, `3812`, `3815`, `3817`, and `3818`, lines `57`-`62`.
  - Unexpected descriptor projection-plan gate mutants ids `3819`, `3820`, `3823`, `3825`, and `3826`, lines `67`-`72`.
- Additional mutants killed by the same assertions:
  - Storage-kind equality mutant id `3785`, count boundary mutants ids `3790` and `3794`, and hydration-map statement mutants ids `3827`, `3828`, and `3829`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanProjectionMutationHelper.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanProjectionMutationHelper.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_ReadPlanCompiler"`: passed; `92` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs" --concurrency 8` from the test project directory: completed in `00:05:08`; `Killed: 239`, `Survived: 26`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-14-20/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3787`, `3788`, `3792`, `3796`, `3797`, `3801`, `3802`, `3803`, `3805`, `3807`, `3808`, `3810`, `3811`, `3812`, `3815`, `3817`, `3818`, `3819`, `3820`, `3823`, `3825`, and `3826` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `3783` and `3784`, lines `21`-`22`, are null-guard statement mutants and are skipped by the loop prompt.
  - Focused no-coverage id `3809`, line `50`, is the switch discard-arm message for an invalid projection-gating state; the preceding boolean gate prevents that switch arm from being reached through public inputs.
  - Remaining focused survived and no-coverage mutants in `ReadPlanProjectionContractValidator.cs` are outside this selected projection-gating cluster.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans ReadPlanProjectionContractValidator DocumentReferenceLookup Contracts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs`
- Mutants selected:
  - DocumentReferenceLookup presence/absence mutants ids `4171`, `4172`, `4176`, and `4177`, lines `864` and `874`.
  - DocumentReferenceLookup fixed result-shape mutants ids `4180`, `4181`, `4182`, `4183`, `4184`, and `4185`, lines `883`-`888`.
  - Empty and duplicate lookup source mutants ids `4188`, `4189`, `4196`, `4197`, and `4198`, lines `894` and `907`-`909`.
  - Missing lookup source hydration table/column diagnostic mutants ids `4199`, `4200`, `4201`, `4202`, and `4203`, lines `918`-`930`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_ReadPlanCompiler"`: passed; `99` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs" --concurrency 8` from the test project directory: completed in `00:05:07`; `Killed: 260`, `Survived: 25`, `NoCoverage: 61`, `Timeout: 1`; report `StrykerOutput/2026-06-24.02-22-48/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `4171`, `4172`, `4176`, `4177`, `4180`, `4181`, `4182`, `4183`, `4184`, `4185`, `4188`, `4189`, `4196`, `4197`, `4198`, `4199`, `4200`, `4201`, `4202`, and `4203` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors/no-coverage remain in `ReadPlanProjectionContractValidator.cs` outside this selected DocumentReferenceLookup contract cluster, including null guards, reference projection diagnostics, descriptor projection diagnostics, formatting helpers, and document-reference lookup coverage/order count diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans ReadPlanProjectionContractValidator Descriptor Source Contracts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs`
- Mutants selected:
  - Descriptor source missing hydration table diagnostic mutant id `3956`, line `375`.
  - Descriptor source ordinal validation mutants ids `3957`, `3959`, `3990`, `3994`, and `3995`, lines `379`-`448`.
  - Descriptor source extra-count diagnostics ids `3966`, `3967`, and `3968`, lines `405`-`407`.
  - Descriptor source mismatch diagnostic index mutant id `3976`, line `417`.
  - Descriptor missing-source count diagnostics ids `3982`, `3983`, `3985`, `3986`, and `3987`, lines `429`-`431`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanProjectionMutationHelper.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanProjectionMutationHelper.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_ReadPlanCompiler"`: passed; final run `103` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs" --concurrency 8` from the test project directory: first focused run completed in `00:05:07`; `Killed: 274`, `Survived: 23`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-32-36/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanCompilerTests.cs src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/ReadPlanProjectionMutationHelper.cs`: succeeded after strengthening the missing-source separator assertion.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_ReadPlanCompiler"`: passed; `103` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/ReadPlanProjectionContractValidator.cs" --concurrency 8` from the test project directory: final focused run completed in `00:05:03`; `Killed: 276`, `Survived: 21`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-39-07/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3956`, `3957`, `3959`, `3966`, `3967`, `3968`, `3976`, `3982`, `3983`, `3985`, `3986`, `3987`, `3990`, `3994`, and `3995` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivors/no-coverage remain in `ReadPlanProjectionContractValidator.cs` outside this selected descriptor source contract cluster, including null guards, reference projection diagnostics, formatting helpers, descriptor-source matching logic reported with no covered tests, and document-reference lookup diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans PageReconstitutionContext Document Link Lookup Conflicts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs`
- Mutants selected:
  - `Logical mutation`, id `3174`, lines `487`-`488`: changed conflicting lookup-row detection from `DocumentUuid`-or-`ResourceKeyId` mismatch to requiring both mismatches.
  - `Negate expression`, id `3175`, lines `487`-`488`: negated the conflicting lookup-row detection.
  - `Equality mutation`, id `3176`, line `487`: changed `DocumentUuid` mismatch detection to equality.
  - `Equality mutation`, id `3177`, line `488`: changed `ResourceKeyId` mismatch detection to equality.
  - `Statement mutation`, id `3179`, lines `491`-`494`: removed the conflicting lookup-row exception.
  - `String mutation`, ids `3180` and `3181`, lines `492`-`493`: replaced the conflicting lookup-row diagnostic text.
  - `Statement mutation`, id `3182`, line `497`: removed the `continue` for duplicate identical lookup rows.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageReconstitutionContext"`: passed; `14` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs" --concurrency 8` from the test project directory: completed in `00:01:58`; `Killed: 76`, `Survived: 40`, `NoCoverage: 54`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-47-31/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3174`, `3175`, `3176`, `3177`, `3179`, `3180`, `3181`, and `3182` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors/no-coverage remain in `PageReconstitutionContext.cs` outside this selected document-link lookup conflict cluster, including null guards, row-node ordinal conversion diagnostics, page-shape validation diagnostics, and scope-key formatting diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans PageReconstitutionContext Page Shape Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs`
- Mutants selected:
  - Ordinal conversion diagnostics: equality mutant id `3047`, line `99`; statement mutant id `3051`, line `101`; string mutants ids `3053`, `3054`, and `3059`, lines `120`-`125`.
  - Existing page-shape diagnostics strengthened: string mutants ids `3097`, `3102`, and `3104`, lines `305`, `313`, and `315`.
  - Missing document, row, descriptor, and scope-key diagnostic mutants ids `3118`, `3119`, `3125`, `3126`, `3127`, `3131`, `3132`, and `3136`, lines `365`-`404`.
  - Metadata/root/table validation mutants ids `3142`, `3143`, `3144`, `3148`, `3152`, `3159`, `3162`, `3201`, `3202`, `3204`, `3209`, `3210`, `3211`, `3212`, `3216`, `3217`, and `3218`, lines `425`-`591`.
  - Parent/root mismatch and invalid root-document-id diagnostics: string/statement mutants ids `3231`, `3233`, `3236`, `3237`, `3238`, `3239`, `3256`, `3257`, `3258`, and `3264`, lines `630`-`710`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageReconstitutionContext"`: passed; final run `24` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs" --concurrency 8` from the test project directory: first focused run completed in `00:01:58`; `Killed: 117`, `Survived: 34`, `NoCoverage: 19`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-54-01/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs && dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageReconstitutionContext"`: succeeded; `24` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs" --concurrency 8` from the test project directory: final focused run completed in `00:02:00`; `Killed: 125`, `Survived: 26`, `NoCoverage: 19`, `Timeout: 0`; report `StrykerOutput/2026-06-24.02-57-21/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3047`, `3051`, `3053`, `3054`, `3059`, `3097`, `3102`, `3104`, `3118`, `3119`, `3125`, `3126`, `3127`, `3131`, `3132`, `3136`, `3142`, `3143`, `3144`, `3148`, `3152`, `3159`, `3162`, `3201`, `3202`, `3204`, `3209`, `3210`, `3211`, `3212`, `3216`, `3217`, `3218`, `3231`, `3233`, `3236`, `3237`, `3238`, `3239`, `3256`, `3257`, `3258`, and `3264` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivors ids `3030`, `3032`, `3033`, `3034`, `3061`, `3071`, `3072`, `3073`, `3074`, `3075`, `3077`, `3079`, `3080`, `3082`, `3083`, `3084`, `3085`, `3086`, `3121`, and `3134` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivors/no-coverage remain outside this selected page-shape diagnostics cluster, including null ordinal formatting, attaching an already-parented row, descriptor lookup conflicts, unavailable parent-table/order diagnostics, missing scope definitions, and null scope-key formatting.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans PageReconstitutionContext Descriptor And Scope Validation

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs`
- Mutants selected:
  - Already-attached child row diagnostics mutants ids `3064`, `3065`, and `3066`, lines `133`-`135`.
  - Descriptor URI conflict diagnostics mutants ids `3192`, `3193`, and `3194`, lines `522`-`524`.
  - Missing root-scope locator diagnostics mutants ids `3250`, `3262`, and `3263`, lines `678` and `701`-`702`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageReconstitutionContext|FullyQualifiedName~Given_RowNode"`: passed; `27` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs" --concurrency 8` from the test project directory: completed in `00:01:58`; `Killed: 134`, `Survived: 25`, `NoCoverage: 11`, `Timeout: 0`; report `StrykerOutput/2026-06-24.03-03-41/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3064`, `3065`, `3066`, `3192`, `3193`, `3194`, `3250`, `3262`, and `3263` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors/no-coverage remain outside this selected descriptor and scope-validation cluster, including null guards, null ordinal formatting, unavailable parent-table/order diagnostics, missing immediate-parent locator diagnostics, and null/unknown scope-key formatting.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans PageReconstitutionContext Parent Availability And Scope Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs`
- Mutants selected:
  - `String mutation`, id `3090`, line `294`: replaced the missing physical-row-identity scope description with `""`.
  - `Statement mutation`, id `3224`, line `614`: removed the unavailable immediate-parent table exception.
  - `String mutation`, ids `3225` and `3226`, lines `615`-`616`: replaced unavailable immediate-parent table diagnostic text with `$""`.
  - `String mutation`, id `3227`, line `624`: replaced the missing immediate-parent locator scope description with `""`.
  - `Statement mutation`, id `3244`, line `660`: removed the unavailable parent table during child-ordering exception.
  - `String mutation`, ids `3245` and `3246`, lines `661`-`662`: replaced unavailable parent table during child-ordering diagnostic text with `$""`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/PageReconstitutionContextTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_PageReconstitutionContext|FullyQualifiedName~Given_RowNode"`: passed; `31` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PageReconstitutionContext.cs" --concurrency 8` from the test project directory: completed in `00:02:03`; `Killed: 142`, `Survived: 23`, `NoCoverage: 5`, `Timeout: 0`; report `StrykerOutput/2026-06-24.03-10-05/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `3090`, `3224`, `3225`, `3226`, `3227`, `3244`, `3245`, and `3246` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `3030`, `3032`, `3033`, `3034`, `3061`, `3071`, `3072`, `3073`, `3074`, `3075`, `3077`, `3079`, `3080`, `3082`, `3083`, `3084`, `3085`, `3086`, `3121`, `3134`, and `3186` are null-guard or duplicate defensive null-validation mutants and are skipped by the loop prompt.
  - Focused survivors/no-coverage ids `3045`, `3056`, `3058`, `3265`, and `3268` are null/null-formatting or null-ToString formatting paths and are skipped by the loop prompt.
  - Focused no-coverage ids `3220` and `3221` are the defensive `ImmediateParentTable` null-coalescing diagnostic inside a method called only after `ImmediateParentTable is not null`; they appear unreachable through public `Build` behavior.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans HydrationBatchBuilder Batch Boundaries And Zero-Limit Detection

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs`
- Mutants selected:
  - Batch statement-boundary mutants ids `1057`, `1059`, `1063`, `1065`, `1068`, `1073`, and `1079`, lines `72`, `76`, `82`, `87`, `93`, `102`, and `114`.
  - Single/query keyset SQL-shape mutants ids `1094`, `1110`, `1111`, and `1112`, lines `163`, `200`, `202`, and `203`.
  - Zero-limit detection mutants ids `1132`, `1134`, `1135`, `1136`, `1137`, `1138`, `1139`, `1141`, and `1142`, lines `238` and `245`-`253`.
  - Semicolon normalization mutants ids `1214`, `1215`, `1216`, `1217`, `1218`, `1219`, `1222`, and `1225`, lines `478` and `490`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydrationBatchBuilderTests.cs`
- Commands run and results:
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs" --concurrency 8` from the test project directory: baseline focused run completed in `00:01:55`; `Killed: 62`, `Survived: 27`, `NoCoverage: 18`; report `StrykerOutput/2026-06-24.03-14-24/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydrationBatchBuilderTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_HydrationBatchBuilder"`: passed; final run `58` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs" --concurrency 8` from the test project directory: final focused run completed in `00:01:52`; `Killed: 88`, `Survived: 10`, `NoCoverage: 9`; report `StrykerOutput/2026-06-24.03-21-27/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed selected mutant ids `1057`, `1059`, `1063`, `1065`, `1068`, `1073`, `1079`, `1094`, `1110`, `1111`, `1112`, `1132`, `1134`, `1135`, `1136`, `1137`, `1138`, `1139`, `1141`, `1142`, `1214`, `1215`, `1216`, `1217`, `1219`, and `1222` are `Killed` in the final focused JSON report.
  - Mutant ids `1218` and `1225` survive but are not actionable for this loop: changing `trimmed.Length > 0` to `trimmed.Length >= 0` only changes behavior for empty SQL strings passed into private semicolon helpers, while the public HydrationBatchBuilder behavior consumes generated non-empty SQL statements.
- Remaining notes:
  - Focused survivors/no-coverage remain outside this selected batch-boundary and zero-limit cluster, including null guards, argument validation messages, missing/conflicting parameter diagnostics, scalar null binding, and invalid structured-parameter diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans HydrationBatchBuilder Parameter Binding Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs`
- Mutants selected:
  - Single-keyset scalar binding `Statement mutation` id `1083`, line `133`: removed adding the `DocumentId` parameter.
  - Conflicting query-parameter metadata mutants ids `1158`, `1159`, and `1160`, lines `302`-`304`: removed or blanked the conflict diagnostic.
  - Missing required parameter-value diagnostic string mutants ids `1176` and `1178`, lines `353`-`354`.
  - Unsupported binding-kind diagnostic `String mutation` id `1184`, line `378`.
  - Scalar non-null binding `Null coalescing mutation` id `1187`, line `387`: replaced scalar values with `DBNull.Value`.
  - SQL Server structured value diagnostic mutants ids `1206`, `1207`, and `1208`, lines `449`-`451`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydrationBatchBuilderParameterBindingTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/HydrationBatchBuilderParameterBindingTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_HydrationBatchBuilder_Query_Parameter_Binding"`: passed; `10` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationBatchBuilder.cs" --concurrency 8` from the test project directory: completed in `00:01:53`; `Killed: 99`, `Survived: 6`, `NoCoverage: 2`, `Timeout: 0`; report `StrykerOutput/2026-06-24.03-27-59/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `1083`, `1158`, `1159`, `1160`, `1176`, `1178`, `1184`, `1187`, `1206`, `1207`, and `1208` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `1054`, `1055`, `1081`, and `1082` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused no-coverage ids `1085` and `1099` are private switch default diagnostics for sealed `PageKeysetSpec` variants and appear unreachable through public `Build`/`AddParameters` inputs.
  - Focused survivors ids `1218` and `1225` are the previously noted empty-SQL semicolon helper equivalences.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans CompiledReconstitutionPlan Lookup And Locator Contracts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs`
- Mutants selected:
  - `ScopeKey` equality-contract mutants ids `106`, `108`, `109`, `114`, `115`, and `121`, lines `27`-`41`.
  - Single root-scope locator diagnostics mutants ids `133`, `134`, and `135`, lines `130`-`132`.
  - Single immediate-parent locator diagnostics mutants ids `137`, `139`, `140`, and `141`, lines `141`-`145`.
  - Duplicate compiled table diagnostics mutants ids `146`, `147`, and `148`, lines `171`-`173`.
  - Missing compiled table diagnostics mutants ids `152`, `153`, and `154`, lines `196`-`198`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`
- Commands run and results:
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: baseline focused run completed in `00:02:04`; `Killed: 72`, `Survived: 35`, `NoCoverage: 37`; report `StrykerOutput/2026-06-24.03-32-14/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; `16` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: final focused run completed in `00:02:04`; `Killed: 91`, `Survived: 32`, `NoCoverage: 21`; report `StrykerOutput/2026-06-24.03-35-37/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `106`, `108`, `109`, `114`, `115`, `121`, `133`, `134`, `135`, `137`, `139`, `140`, `141`, `146`, `147`, `148`, `152`, `153`, and `154` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivors ids `104`, `156`, and `158` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `127` is the `ScopeKey.GetHashCode` `hash.Add(part)` statement; killing it would require asserting unequal hash values, which is not a stable equality contract.
  - Focused survivors/no-coverage remain outside this selected lookup and locator contract cluster, including property-order construction, topology diagnostics, parent resolution diagnostics, fallback identity metadata resolution, and multiple-ordinal diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans CompiledReconstitutionPlan Property Order And Sibling Topology

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs`
- Mutants selected:
  - Property-order table-scope and scalar-source mutants ids `170`, `173`, and `179`, lines `294`, `296`, and `309`.
  - Reference identity field property-order mutant id `181`, line `313`.
  - Sibling subtree topology ordering mutant id `203`, lines `395`-`396`.
  - Inspected property-order survivors ids `171` and `188`, lines `294` and `334`, and treated them as not actionable: adding the root `$` scope has no property segment to add, and the duplicate-path guard is redundant with `PropertyOrderNode.GetOrAddChild`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; final run `19` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: first focused run completed in `00:02:04`; `Killed: 95`, `Survived: 28`, `NoCoverage: 21`; report `StrykerOutput/2026-06-24.03-42-43/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded after adding the nested reference identity field assertion.
  - `git diff --check`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; `19` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: final focused run completed in `00:02:03`; `Killed: 96`, `Survived: 27`, `NoCoverage: 21`; report `StrykerOutput/2026-06-24.03-45-47/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `170`, `173`, `179`, `181`, and `203` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivors/no-coverage remain outside this selected property-order and sibling-topology cluster, including null guards, topology failure diagnostics, parent resolution diagnostics, fallback identity metadata resolution, and multiple-ordinal diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans CompiledReconstitutionPlan Topology And Hydration Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs`
- Mutants selected:
  - Parent-kind description string mutants ids `220` and `221`, lines `465` and `476`.
  - Unsupported table-kind diagnostic string mutants ids `226` and `227`, lines `494`-`495`.
  - Missing parent diagnostics mutants ids `245` and `246`, lines `544`-`545`.
  - Ambiguous parent diagnostics mutants ids `250`, `251`, and `255`, lines `552`-`555`.
  - Resource display name diagnostic mutant id `257`, line `560`.
  - Duplicate hydration column diagnostics mutants ids `261` and `262`, lines `577`-`578`.
  - Duplicate table diagnostics mutants ids `264`, `265`, and `266`, lines `584`-`586`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; `25` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: completed in `00:02:05`; `Killed: 111`, `Survived: 19`, `NoCoverage: 14`, `Timeout: 0`; report `StrykerOutput/2026-06-24.03-52-55/reports/mutation-report.json`.
- Verification:
  - Confirmed selected mutant ids `220`, `221`, `226`, `227`, `245`, `246`, `250`, `251`, `255`, `257`, `261`, `262`, `264`, `265`, and `266` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `104`, `156`, and `158` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `127` remains the `ScopeKey.GetHashCode` `hash.Add(part)` statement and is still treated as not a stable equality-contract assertion target.
  - Focused survivors/no-coverage remain outside this selected topology and hydration diagnostics cluster, including column-group diagnostic strings, defensive topology count checks, parent-candidate filtering, fallback identity metadata resolution, and multiple-ordinal diagnostics.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans CompiledReconstitutionPlan Identity Metadata Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs`
- Mutants selected:
  - Root table count diagnostics mutants ids `198`, `199`, and `200`, lines `382`-`384`.
  - Missing locator and physical identity column-group diagnostics mutants ids `161`, `162`, `163`, `287`, and `288`, lines `244`, `250`, `256`, and `686`-`687`.
  - Empty explicit root-scope locator fallback mutants ids `291`, `292`, and context string mutant id `295`, lines `696` and `705`.
  - Empty explicit immediate-parent locator fallback mutants ids `298` and `299`, line `714`.
  - Multiple ordinal column diagnostics mutants ids `320`, `321`, and `322`, lines `748`-`750`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; `32` tests.
  - `dotnet tool restore` from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: succeeded.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: completed in `00:02:05`; `Killed: 128`, `Survived: 11`, `NoCoverage: 5`, `Timeout: 0`; report `StrykerOutput/2026-06-24.03-59-46/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed selected mutant ids `161`, `162`, `163`, `198`, `199`, `200`, `287`, `288`, `291`, `292`, `295`, `298`, `299`, `320`, `321`, and `322` are `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivors ids `104`, `156`, and `158` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `127` remains the `ScopeKey.GetHashCode` `hash.Add(part)` statement and is still treated as not a stable equality-contract assertion target.
  - Focused survivors ids `160`, `171`, `188`, `286`, and `301`, plus no-coverage ids `210`, `211`, `212`, `281`, and `282`, appear defensive, equivalent, or unreachable through the public builder paths covered in this loop.
  - Focused survivor ids `232` and `235` remain parent-candidate filtering paths outside this selected identity metadata diagnostics cluster.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans CompiledReconstitutionPlan Parent Kind Filtering

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs`
- Mutants selected:
  - `Statement mutation`, id `232`, line `515`: removed the `continue` that excludes parent candidates with disallowed table kinds.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`
- Commands run and results:
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/CompiledReconstitutionPlanTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_CompiledReconstitutionPlanTests"`: passed; `33` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/CompiledReconstitutionPlan.cs" --concurrency 8` from the test project directory: completed in `00:02:15`; `Killed: 129`, `Survived: 10`, `NoCoverage: 5`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-06-03/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed selected mutant id `232` is `Killed` in the focused JSON report.
- Remaining notes:
  - Focused survivor id `235`, line `520`, removes the self-parent candidate skip. It appears defensive/equivalent for supported table scopes because a non-root table's expected parent scope is an ancestor or base extension scope, not its own restricted scope; root-extension tables are not allowed root-extension parents.
  - Focused survivors ids `104`, `156`, and `158` are null-guard statement mutants and are skipped by the loop prompt.
  - Focused survivor id `127` remains the `ScopeKey.GetHashCode` `hash.Add(part)` statement and is still treated as not a stable equality-contract assertion target.
  - Focused survivors/no-coverage ids `160`, `171`, `188`, `210`, `211`, `212`, `281`, `282`, `286`, and `301` remain defensive, equivalent, or unreachable through public builder behavior already inspected in the previous loop entry.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans RelationshipAuthorizationPlanningHelpers Ordering And Diagnostics

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationPlanningHelpers.cs`
- Mutants selected:
  - `String mutation`, id `5995`, line `21`: replaced the unsupported person securable-element kind diagnostic with `""`.
  - `Statement mutation`, id `5998`, line `28`: removed the blank `planningContext` validation.
  - `String mutation`, ids `6000` and `6001`, lines `36` and `39`: replaced missing and multiple root-scope locator diagnostics with `$""`.
  - `Linq method mutation`, ids `6004`, `6005`, `6006`, `6008`, and `6011`, line `50`: changed `OrderFailures` tie-breakers from ascending to descending.
  - `Null coalescing mutation`, ids `6013` and `6014`, lines `51` and `52`: ignored configured strategy and relationship local order values.
  - `Linq method mutation`, id `6021`, line `65`: changed unresolved contributor ordering from minimum contribution order to maximum contribution order.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationPlanningHelpersTests.cs`
- Commands run and results:
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationPlanningHelpers.cs" --concurrency 8` from the test project directory: baseline focused run completed in `00:02:05`; `Killed: 8`, `Survived: 11`, `NoCoverage: 3`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-13-01/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationPlanningHelpersTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_RelationshipAuthorizationPlanningHelpers"`: passed; `10` tests.
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationPlanningHelpers.cs" --concurrency 8` from the test project directory: final focused run completed in `00:01:59`; `Killed: 20`, `Survived: 2`, `NoCoverage: 0`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-17-37/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed selected mutant ids `5995`, `5998`, `6000`, `6001`, `6004`, `6005`, `6006`, `6008`, `6011`, `6013`, `6014`, and `6021` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivor ids `5997` and `6003` are null-guard statement mutants and are skipped by the loop prompt.
  - Briefly inspected `RelationshipAuthorizationAuth1FailurePayload.cs` with a focused baseline report `StrykerOutput/2026-06-24.04-10-11/reports/mutation-report.json`; remaining focused mutants are null guards, equivalent positive-count parsing behavior, or a timeout from removing the SQL Server payload loop increment, so no tests were changed there.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans JsonScopeAttachmentResolver Parser And Attachment Contracts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/JsonScopeAttachmentResolver.cs`
- Mutants selected:
  - Unsupported kind and attachment diagnostic string/statement mutants ids `1381`, `1382`, `1386`, `1387`, and `1388`, lines `26`-`45`.
  - Attachment failure boolean mutant id `1400`, line `77`; unsupported scope segment string mutant id `1427`, line `117`.
  - Canonical/explicit segment selection mutants ids `1430`, `1433`, `1434`, and `1436`, lines `127`-`133`.
  - Missing immediate parent diagnostic mutants ids `1447`, `1448`, and `1449`, lines `156`-`158`.
  - Segment equality and prefix mutants ids `1510`, `1513`, `1516`, and `1521`, lines `231`-`250`.
  - Restricted canonical parser/tokenizer mutants ids `1532`, `1533`, `1534`, `1535`, `1536`, `1537`, `1539`, `1540`, `1541`, `1542`, `1543`, `1545`, `1548`, `1549`, `1550`, `1551`, `1553`, `1554`, `1555`, `1557`, `1559`, `1560`, `1561`, `1563`, `1564`, `1565`, `1566`, `1568`, `1570`, `1571`, `1573`, `1574`, `1575`, `1576`, `1577`, `1578`, `1579`, `1580`, `1581`, `1582`, `1583`, `1584`, `1585`, and `1586`, lines `266`-`333`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/JsonScopeAttachmentResolverTests.cs`
- Commands run and results:
  - `dotnet tool restore && dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/JsonScopeAttachmentResolver.cs" --concurrency 8` from the test project directory: baseline focused run completed in `00:01:55`; `Killed: 42`, `Survived: 7`, `NoCoverage: 60`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-22-03/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/JsonScopeAttachmentResolverTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_JsonScopeAttachmentResolver"`: passed; final run `19` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/JsonScopeAttachmentResolver.cs" --concurrency 8` from the test project directory: final focused run completed in `00:02:00`; `Killed: 104`, `Survived: 2`, `Timeout: 3`; report `StrykerOutput/2026-06-24.04-28-51/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed all selected mutant ids listed above are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivor id `1431`, line `128`, appears equivalent because removing the root fast path still parses canonical `$` to the same empty segment list.
  - Focused survivor id `1506`, line `224`, depends on an explicitly constructed empty extension project segment; the canonical parser rejects empty property segments before this shape is produced.
  - Focused timeouts ids `1547`, `1556`, and `1569`, lines `282`, `293`, and `314`, are parser loop-control or throw-removal mutations that prevent invalid-token/property parsing from terminating.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.

## 2026-06-24 - Backend Plans RelationshipAuthorizationFailureMapper No-Claims And Ordinal Contracts

- Target project: `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`
- Selected production file: `src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationFailureMapper.cs`
- Mutants selected:
  - No-claims metadata validation and value-source consistency mutants ids `5426`, `5428`, `5433`, `5434`, `5435`, `5436`, `5443`, `5445`, and `5447`, lines `34`-`51`.
  - No-claims strategy ordering, local-order tie-break, and sorted claim-id mutants ids `5451`, `5452`, and `5469`, lines `68` and `125`.
  - AUTH1 unknown strategy ordinal validation mutants ids `5503`, `5508`, and `5510`, lines `190`-`191`.
  - People and non-person no-claims metadata fallback mutants ids `5544`, `5545`, `5546`, `5547`, `5555`, `5556`, `5557`, `5558`, `5559`, `5560`, `5561`, `5574`, `5585`, and `5586`, lines `337`-`394`.
  - Strategy hint selection mutants ids `5593` and `5594`, line `416`.
- Test files changed:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`
- Commands run and results:
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/MssqlPlanDialect.cs" --concurrency 8` from the test project directory: exploratory focused run completed in `00:02:05`; only null-guard survivors remained.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/PgsqlPlanDialect.cs" --concurrency 8` from the test project directory: exploratory focused run completed in `00:02:03`; only null-guard survivors remained.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationFailureMapper.cs" --concurrency 8` from the test project directory: baseline focused run completed in `00:01:51`; `Killed: 62`, `Survived: 44`, `NoCoverage: 23`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-39-06/reports/mutation-report.json`.
  - `dotnet csharpier format src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/RelationshipAuthorizationAuth1FailurePayloadTests.cs`: succeeded.
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~Given_RelationshipAuthorizationFailureMapper"`: passed; final run `27` tests.
  - `dotnet stryker --config-file stryker-config.json --mutate "/home/brad/work/dms-root/Data-Management-Service/src/dms/backend/EdFi.DataManagementService.Backend.Plans/RelationshipAuthorizationFailureMapper.cs" --concurrency 8` from the test project directory: final focused run completed in `00:01:54`; `Killed: 101`, `Survived: 17`, `NoCoverage: 11`, `Timeout: 0`; report `StrykerOutput/2026-06-24.04-48-23/reports/mutation-report.json`.
  - `git diff --check`: succeeded.
- Verification:
  - Confirmed selected mutant ids `5426`, `5428`, `5433`, `5434`, `5435`, `5436`, `5443`, `5445`, `5447`, `5451`, `5452`, `5469`, `5503`, `5508`, `5510`, `5544`, `5545`, `5546`, `5547`, `5555`, `5556`, `5557`, `5558`, `5559`, `5560`, `5561`, `5574`, `5585`, `5586`, `5593`, and `5594` are `Killed` in the final focused JSON report.
- Remaining notes:
  - Focused survivors ids `5423`, `5424`, `5425`, `5472`, `5473`, `5474`, and `5475` are null guard or argument validation statement mutants and are skipped by the loop prompt.
  - Focused survivor/no-coverage ids `5454`, `5460`, and `5468` are defensive malformed no-claims grouping and empty-result checks; normal planner-produced no-claims failures share valid strategy identities.
  - Focused survivors/no-coverage ids `5550`, `5551`, `5553`, `5554`, `5562`, and `5573` are deeper fallback branches outside this selected no-claims priority cluster.
  - Focused survivors/no-coverage ids `5485`, `5488`, `5602`, `5608`, `5609`, `5611`, `5612`, `5613`, `5614`, and `5615` remain in AUTH1 mixed value-source validation and unsupported enum diagnostic paths outside this selected cluster.
  - Broad target re-run was skipped because recent broad Backend Plans runs take about `30` minutes. Next broad command to run from `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit`: `dotnet stryker --config-file stryker-config.json`.
