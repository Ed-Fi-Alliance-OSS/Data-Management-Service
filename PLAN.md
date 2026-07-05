# Performance Allocation Investigation Plan

## Context

The latest 20-client, 30-minute Suite 3 DMS volume diagnostic run captured PostgreSQL telemetry, .NET counters, and `dotnet-trace` samples in:

`/home/brad/work/dms-root/Suite-3-Performance-Testing/DmsTestResults/2026-07-04-00-13-fastpath-trace-hostpg-pg18-20c-30m-volume-pgsm-dotnettrace`

The run reproduced high application allocation pressure while Kestrel queues stayed at zero. The 30-minute counters showed roughly `525 MiB/sec` average allocation rate and about `536.6s` total GC pause time. The symbolized trace pointed to logging/enrichment, string building, relationship authorization SQL/parameter construction, JSON path work, and collection resizing as the next app-side areas to investigate.

Treat the trace run as diagnostic, not as the clean performance baseline, because the workload had one `DELETE /data/ed-fi/locations/{id}` 404. Use the previous clean fast-path run as the comparison baseline:

`/home/brad/work/dms-root/Suite-3-Performance-Testing/DmsTestResults/2026-07-04-04-24-single-doc-fastpath-rerun-hostpg-pg18-20c-30m-volume-pgsm-dotnet`

## Goals

1. Reduce DMS allocation rate in hot request paths without changing API behavior.
2. Reduce GC pause time during the Suite 3 20-client volume workload.
3. Preserve the PostgreSQL single-document hydration fast path and avoid reintroducing `"page"` temp-table work for `PageKeysetSpec.Single`.
4. Keep validation comparable: one implementation variable per Suite 3 rerun.

## Evidence Rules

- Start each workstream with focused measurement before changing code.
- Separate observations from inferences in notes and reports.
- Prefer counters and request latency over average-only summaries.
- Reject comparisons when the run has failures, changed duration/client count, changed PostgreSQL settings, or mixed implementation changes.
- Keep PostgreSQL evidence in each validation run so app-side changes do not hide a database regression.

## Phase 0 - Reproduce And Narrow

1. Capture a short symbolized steady-state trace with rundown enabled and no Speedscope conversion during a 20-client Suite 3 volume run.
2. Generate `topN-exclusive.txt` and `topN-inclusive.txt` serially from the trace.
3. Capture `dotnet-counters` for the same run window.
4. Record a short hotspot table with:
   - allocation rate
   - GC pause time
   - Gen0/Gen1/Gen2 collection counts
   - lock contention rate
   - Kestrel request and connection queue lengths
   - Locust throughput, failure count, median, P95, and P99
5. Confirm that PostgreSQL still reports zero temp files/bytes and that `pg_stat_monitor` top statements do not show the old `"page"` temp-table path.

Expected output: a short markdown note under the new run folder that identifies the top app-side allocation candidates before implementation starts.

## Workstream 1 - Investigate Logging Allocation

Primary files:

- `src/dms/core/EdFi.DataManagementService.Core/Middleware/RequestResponseLoggingMiddleware.cs`
- Serilog integration and host logging configuration files found under `src/dms`
- Any request/response body logging middleware and logging scope creation used in the DMS request pipeline

Trace leads:

- `SafeAggregateSink.Emit`
- `SerilogLoggerScope.EnrichAndCreateScopeItem`
- `SerilogLogger.PrepareWrite`
- `StringBuilder.ToString`
- `ConnectionLogScope.GetEnumerator`
- `RequestResponseLoggingMiddleware.Execute`

Investigation steps:

1. Verify runtime log levels and sinks used by the local Suite 3 Docker configuration. Confirm whether request start/completion logs are emitted at `Information` during the volume workload.
2. Inspect request pipeline logging for per-request sanitization, template binding, scope allocation, and message construction that occurs before checking `ILogger.IsEnabled`.
3. Check whether request/response body logging is disabled in the performance profile; if disabled, verify the middleware does not still allocate buffers, scopes, or strings.
4. Use a focused allocation trace or small benchmark around the logging middleware to compare:
   - current request start/completion logging
   - logging disabled below `Information`
   - completion-only logging
   - no per-request logging scopes
5. If logging is a confirmed contributor, implement the smallest behavior-preserving optimization first:
   - avoid sanitizer/string work unless the log level is enabled
   - use source-generated `LoggerMessage` delegates for hot log templates
   - remove avoidable scope/enrichment creation in hot paths
   - ensure dropped log events do not allocate request-specific state
6. Add or update unit tests for logging behavior only where behavior changes are observable.

Validation:

- Focused test: unit tests for logging middleware and any changed logging configuration.
- Local check: `dotnet csharpier format` on changed files.
- Performance check: 30-minute Suite 3 20-client volume run with counters only.
- Success signal: lower allocation rate and GC pause time with no increase in failure rate, median, P95, or P99 latency.

## Workstream 2 - Pre-size Or Pool High-Churn Collections And Builders

Execution status: completed on 2026-07-05. Implemented conservative request-hot-path pre-sizing and materialization reductions in relational write, relationship authorization parameter construction, JSON path canonicalization, and document reconstitution paths. Validation artifacts are in:

`/home/brad/work/dms-root/Suite-3-Performance-Testing/DmsTestResults/2026-07-05-01-15-workstream2-presize-20c-30m-volume-pgsm-dotnet`

Measured against the clean warning-level baseline:

`/home/brad/work/dms-root/Suite-3-Performance-Testing/DmsTestResults/2026-07-04-23-54-loglevel-warning-clean-rerun-20c-30m-volume-pgsm-dotnet`

Result: clean 30-minute 20-client Suite 3 volume run with zero failures. Throughput improved from `1162.87` to `1298.71` requests/sec, median latency improved from `10` to `9` ms, P95 improved from `78` to `66` ms, and GC pause per 1k requests improved by about `19.1%`. Allocation rate did not improve: average allocation rose from about `387.5 MB/sec` to `446.8 MB/sec`, and allocation per request rose about `3.2%` by rate-normalized calculation. Treat Workstream 2 as a throughput/latency improvement, not a confirmed allocation-rate fix.

Primary files:

- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalCommandAccess.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/PostgresqlRelationalCommandExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNoProfilePersister.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationshipAuthorizationCommandParameterBuilder.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalJsonPathSupport.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/PersonJoinPathResolver.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/DocumentReconstituter.cs`

Trace leads:

- `List<T>.Resize`
- `List<T>.AddWithResize`
- `Buffer.MemmoveInternal`
- `StringBuilder.ToString`
- `GC.AllocateUninitializedArray`
- `SessionRelationalCommandFactory.AddParameters`

Investigation steps:

1. Inventory hot-path list and array materialization that executes per request, especially collection expressions such as `[.. source.Select(...)]`, `ToArray()`, `ToDictionary()`, and `new Dictionary<...>()` in write authorization and hydration.
2. For each candidate, classify it as:
   - request hot path
   - startup/model compilation path
   - error path
   - test-only path
3. Prioritize request hot paths where the final count is known before filling the collection.
4. Replace high-confidence hot-path LINQ materialization with explicit loops and pre-sized `List<T>`, `Dictionary<TKey,TValue>`, or arrays when it reduces allocation or resizing without reducing readability.
5. Prefer pre-sizing over pooling first. Use pooling only for very high-volume temporary buffers where ownership and cleanup are simple.
6. Be conservative with `StringBuilder` pooling. Only pool builders where the maximum size is bounded or reset/discard rules are explicit.
7. Review parameter construction in relational command execution:
   - keep behavior identical for provider-specific parameter configuration
   - avoid duplicate `AddParameters` implementations if a shared helper can reduce maintenance risk
   - do not attempt to reuse `DbParameter` instances across commands
8. Review relationship authorization runtime parameter building:
   - avoid `SelectMany(...).ToDictionary(...)` when a nested loop can fill a pre-sized dictionary
   - avoid spread copies when appending known parameter groups
   - avoid converting `IReadOnlyList<long>` to arrays unless the PostgreSQL provider requires arrays

Validation:

- Unit tests for changed parameter construction and authorization paths:
  - backend unit tests around `RelationalCommandAccess`
  - backend unit tests around relationship authorization command/parameter builders
  - existing reconstitution tests if `DocumentReconstituter` changes
- Performance check: 30-minute Suite 3 20-client volume run with counters only.
- Success signal: reduced `List<T>.Resize`, `AddWithResize`, and allocation-array frames in a follow-up trace; lower allocation rate and GC pause time in counters.

## Workstream 3 - Review JSON Path And Authorization SQL Construction For Per-Request Recompilation

Execution status: implementation completed on 2026-07-05; performance validation pending. Implemented a bounded cached compile path for single-record relationship authorization SQL plans and switched the stored/proposed runtime request paths to it. The cached plan excludes request-specific claim EdOrg values and proposed runtime values, while the key includes dialect, check-spec identity, claim parameterization shape, emitted AUTH1 index, document parameter name, and reserved write parameter names.

Validation completed:

- `dotnet csharpier format` on changed files
- `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj --filter "FullyQualifiedName~Given_SingleRecordRelationshipAuthorizationSqlCompiler"`: 70 passed
- `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj`: 1842 passed
- `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj`: 1688 passed

Next validation: one clean 30-minute Suite 3 20-client volume run with PostgreSQL evidence and .NET counters only. Use a 5-minute symbolized trace only if counters do not clearly show allocation or GC movement.

Primary files:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/SingleRecordRelationshipAuthorizationSqlCompiler.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/SingleRecordRelationshipAuthorizationExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteNoProfilePersister.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationshipAuthorizationProposedValueExtractor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalJsonPathSupport.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/PersonJoinPathResolver.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Schema/JsonPathExpressionCompiler.cs`

Trace leads:

- `SingleRecordRelationshipAuthorizationSqlCompiler`
- `PersonJoinPathResolver.BuildResourceLookup`
- `JsonPath.Append`
- `JsonPath.Evaluate`
- `RelationalJsonPathSupport.ParseConcretePath`
- `RelationalJsonPathSupport.BuildCanonical`

Investigation steps:

1. Map which authorization SQL and JSON path objects are built at startup/model compilation versus per request.
2. Confirm whether `SingleRecordRelationshipAuthorizationSqlCompiler` is constructed per request in:
   - `SingleRecordRelationshipAuthorizationExecutor.ExecuteAsync`
   - `RelationalWriteNoProfilePersister.BuildProposedRelationshipAuthorizationCommandParts`
3. Determine the stable cache key for compiled authorization SQL plans. Candidate key fields include:
   - mapping set key
   - dialect
   - check specs
   - claim education organization parameterization shape
   - emitted AUTH1 index
   - proposed-value binding shape
   - reserved write parameter names
4. Prefer storing compiled authorization plans in runtime mapping metadata if the inputs are schema-derived and stable. Use request-time caches only if the key is compact and correctness is obvious.
5. Separate compiled SQL text and parameter metadata from request-specific parameter values.
6. Review JSON path parsing/evaluation:
   - cache restricted/concrete parsed paths when the source path is schema-derived
   - avoid reparsing canonical strings on every request
   - avoid rebuilding canonical strings where the model already carries segments
   - keep request-body JSON value extraction behavior unchanged
7. Review `PersonJoinPathResolver.BuildResourceLookup` call sites. If resource lookup is rebuilt during request handling, move it to model compilation or mapping-set initialization.
8. Add tests that prove cached authorization SQL remains correct across:
   - PostgreSQL and SQL Server dialects
   - stored and proposed checks
   - direct EdOrg checks
   - direct and transitive People checks
   - parameter-name collisions
   - authorization failure payload behavior

Validation:

- Unit tests:
  - `EdFi.DataManagementService.Backend.Plans.Tests.Unit`
  - `EdFi.DataManagementService.Backend.Tests.Unit`
  - targeted integration tests only if cache placement changes cross-module contracts
- Performance check: one 30-minute Suite 3 20-client run after this workstream only.
- Success signal: follow-up trace shows reduced SQL compiler and JSON path self-time; counters show lower allocation rate without PostgreSQL statement-shape regressions.

## Suggested Implementation Order

1. Logging allocation investigation and smallest logging optimization.
2. High-churn collection pre-sizing in request-local parameter and authorization builders.
3. Authorization SQL/JSON path cache design after the first two lower-risk changes are measured.

This order keeps early changes easy to validate and avoids introducing cache correctness risk before confirming how much logging and collection churn contribute.

## Suite 3 Validation Template

For each implemented workstream, run one clean validation with:

- test type: `volume`
- duration: `30` minutes
- clients: `20`
- spawn rate: `20`
- DMS/CMS topology: same as the fast-path baseline
- PostgreSQL: same host PostgreSQL target and settings as the baseline
- telemetry: PostgreSQL evidence plus 30-minute `.NET` counters
- trace: only add a 5-minute symbolized trace when method attribution is needed; do not convert to Speedscope by default

Report:

- result folder path
- branch and commit
- changed variable
- failure count
- total requests and throughput
- median, P95, P99, max latency
- allocation rate
- GC pause time
- collection counts
- Kestrel queue lengths
- PostgreSQL temp files/bytes
- top PostgreSQL statements
- whether `pg_stat_monitor` captured rows

## Exit Criteria

The plan is complete when at least one isolated change has a clean 30-minute Suite 3 20-client validation run showing:

- zero workload failures
- no regression in median, P95, or P99 latency beyond normal run variance
- no regression in throughput beyond normal run variance
- lower allocation rate than the clean fast-path baseline
- lower GC pause time than the clean fast-path baseline
- no reappearance of the `"page"` temp-table path for single-document hydration
