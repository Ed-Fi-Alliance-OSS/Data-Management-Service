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
