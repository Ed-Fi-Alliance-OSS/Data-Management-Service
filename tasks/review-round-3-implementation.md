# Review Round 3 Implementation — DMS-1457

> [!WARNING]
> The files in this `tasks` directory are ephemeral, committed only for the duration
> of the autonomous coding run. `plan.md`, `todo.md`, `declined-findings.md`,
> `review-round-1.md`, `review-round-2.md`, `review-round-3-implementation.md`,
> and any other `review-round-*.md` files must be removed with `git rm` before
> final merge by a human.

## PR description additions needed

- Add this exact public-doc TODO block at the end of the PR description:

```markdown
TODO: Public documentation needs to cover the use of the correlation ID, including
the maximum length and the truncation of longer values. That work belongs in the
Ed-Fi documentation repository, not this one.
```

- Add an explicit note that the `traceId`/`correlationId` field-name inconsistency is knowingly left alone in this change and tracked separately as DMS-1518.
- Add a one-line note that the hardcoded `"correlationid"` `MapFallback` default was removed, so the fallback 404 now honors the configured/disabled correlation-header behavior.

## Files changed and why

### Production code
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/RequestResponseLoggingMiddleware.cs`
  - Swapped trace-id sanitization from `SanitizeForLogging` to `SanitizeForCorrelationId`.
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`
  - Swapped only trace-id arguments to the correlation-ID sanitizer.
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ResolveMappingSetMiddleware.cs`
  - Swapped only trace-id arguments to the correlation-ID sanitizer.
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateResourceKeySeedMiddleware.cs`
  - Swapped only trace-id arguments to the correlation-ID sanitizer.
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/CustomResourceValidationMiddleware.cs`
  - Swapped the trace-id log sanitizer and updated the nearby comment to describe the correlation-ID allowlist.
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs`
  - Swapped all trace-id log sanitization call sites to `SanitizeForCorrelationId`.
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs`
  - Swapped all trace-id log sanitization call sites to `SanitizeForCorrelationId`.
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorReadHandler.cs`
  - Swapped the trace-id log sanitization call site to `SanitizeForCorrelationId`.
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDeleteExecution.cs`
  - Swapped all trace-id log sanitization call sites to `SanitizeForCorrelationId`.
- `src/dms/backend/EdFi.DataManagementService.Backend.External/LogSanitizer.cs`
  - Rewrote the `ReplaceLineEndings` comment to explain why it is not redundant for the broader correlation-ID allowlist.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/LoggingMiddleware.cs`
  - Simplified the unreachable constructor fallback path by removing the cached max-length field and using `AppSettings.DefaultCorrelationIdMaxLength` directly in the `OptionsValidationException` fallback.

### Tests
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Utilities/LoggingSanitizerTests.cs`
  - Added regression assertions proving the strict log allowlist strips punctuation that the correlation-ID allowlist preserves, and added Unicode line/paragraph separator coverage.
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Middleware/RequestResponseLoggingMiddlewareTests.cs`
  - Updated the expected logged trace ID so braces remain searchable after correlation-ID sanitization.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/MapFallbackCorrelationIdTests.cs`
  - Replaced self-referential expectations with literals and strengthened the disabled-header test to assert the normalized server trace identifier.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/Modules/HealthCheckEndpointModuleTests.cs`
  - Replaced the self-referential normalization expectation with the literal `12{34}`.
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/TraceIdConstructionGuardTests.cs`
  - Added the source-text guard test that fails if a new production `TraceId` construction bypasses `AspNetCoreFrontend.NormalizeTraceId`.
- `src/dms/tests/EdFi.DataManagementService.Tests.Integration/Tests/Given_CorrelationIdNormalization_Parity.cs`
  - Added the CI-selection categories and remarks, replaced the self-referential expectation with the literal `12{34}`, and added a guardrail test that keeps the fixture selected in both API integration lanes.

### Documentation / task artifacts
- `docs/LOGGING.md`
  - Scoped the correlation-normalization section to DMS and documented the 413 no-body exception.
- `tasks/review-round-1.md`
- `tasks/review-round-2.md`
  - Added the requested before-merge warning banner.

## Verification

1. `dotnet tool restore`
   - `Restore was successful.`
2. `dotnet csharpier format src/dms`
   - `Formatted 2779 files in 35108ms.`
3. `dotnet csharpier check src/dms`
   - `Checked 2779 files in 31020ms.`
4. `pwsh ./build-dms.ps1 Build`
   - `Build succeeded.`
   - `0 Warning(s)`
   - `0 Error(s)`
   - `Build Succeeded`
5. `pwsh ./build-dms.ps1 UnitTest`
   - Completed with exit code 0.
   - Key lines observed from the run:
     - `Passed!  - Failed:     0, Passed:   523, Skipped:     0, Total:   523, Duration: 5 m 12 s - EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:  5093, Skipped:     0, Total:  5093, Duration: 39 s - EdFi.DataManagementService.Core.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:  3785, Skipped:     0, Total:  3785, Duration: 32 s - EdFi.DataManagementService.Backend.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:   894, Skipped:     0, Total:   894, Duration: 50 s - EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:  2356, Skipped:     0, Total:  2356, Duration: 1 m 40 s - EdFi.DataManagementService.Backend.Plans.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:  1296, Skipped:     1, Total:  1297, Duration: 47 s - EdFi.DataManagementService.Backend.Ddl.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:   201, Skipped:     0, Total:   201, Duration: 1 s - EdFi.DataManagementService.Backend.Cdc.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:   169, Skipped:     0, Total:   169, Duration: 1 s - EdFi.DataManagementService.SchemaTools.Tests.Unit.dll (net10.0)`
     - `Passed!  - Failed:     0, Passed:    66, Skipped:     0, Total:    66, Duration: 22 s - EdFi.DataManagementService.Tests.Unit.dll (net10.0)`
   - TRX aggregate check after the run:
     - `total=15053 passed=15050 failed=0 files=11`
6. Targeted regression checks
   - `dotnet test ...Core.Tests.Unit... --filter "...LoggingSanitizerTests|...Given_RequestResponseLoggingMiddleware" -m:1`
     - `Passed!  - Failed:     0, Passed:    23, Skipped:     0, Total:    23`
   - `dotnet test ...Frontend.AspNetCore.Tests.Unit... --filter "...MapFallback...|...TraceId_Construction_Guardrails|...Trace_Id_Extraction|...LoggingMiddlewareTests|...HealthCheckEndpointModuleTests" -m:1`
     - `Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11`
   - `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj --list-tests --filter "Category=ApiIntegration&Category=PostgresqlIntegration" -m:1`
     - Selected `It_applies_the_same_normalized_correlation_id_to_401_404_and_429_responses`
     - Selected `It_keeps_the_correlation_id_parity_fixture_selected_by_both_api_ci_lanes`
   - `dotnet test ...Tests.Integration.csproj --filter "FullyQualifiedName~Given_CorrelationIdNormalization_Parity|FullyQualifiedName~CorrelationIdNormalizationParityCiSelectionGuardrail" -m:1`
     - `Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2`
7. `git diff --stat -- src/config`
   - empty output
8. Critical-finding grep re-run
   - Command:
     - `grep -rn "SanitizeForLogging(.*[Tt]race[Ii]d\|SanitizeForLogging(.*\.TraceId\.Value\|SanitizeForLogging(.*traceId" src/dms --include=*.cs | grep -v Tests || true`
   - Output:
     - `No matches`
9. Changed-test sanity check
   - Reviewed the diffs for every changed `*Tests*.cs` file. No assertion was removed, skipped, or weakened; changed expectations were tightened to literal normalized values or updated to the now-correct punctuation-preserving behavior.

## Items not completed

- None. H1, H2, H3, and M1-M7 were completed in this round.
