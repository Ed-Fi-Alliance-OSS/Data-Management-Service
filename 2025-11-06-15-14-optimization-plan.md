# DMS .NET Optimization Plan (2025-11-06)

## 1. Current Performance Risks in the .NET Stack
- Response serialization re-writes the payload on every request and forces pretty-printing, multiplying CPU work and allocations (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs:127`).
- Request bodies are read into managed strings and then reparsed into `JsonNode`, creating duplicate large-object allocations and keeping both representations alive (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs:26`, `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParseBodyMiddleware.cs:71`).
- JSON schema validation recompiles the schema document for every request, paying serialization and parsing costs repeatedly (`src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:34`).
- Logging middleware performs expensive regex minification and full JSON masking, triggering multiple serialize/deserialize passes on the request body (`src/dms/core/EdFi.DataManagementService.Core/Middleware/RequestInfoBodyLoggingMiddleware.cs:19`).
- Multiple middleware steps re-run JSONPath evaluations that parse the same expressions on every request, building new `JsonPath` instances and traversing the DOM repeatedly (`src/dms/core/EdFi.DataManagementService.Core/ApiSchema/Helpers/JsonHelpers.cs:21`, `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProvideEducationOrganizationHierarchyMiddleware.cs:77`).
- Authorization middleware fetches and filters the entire claim-set list per request and logs at information level, even when the caller is already authorized (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ResourceActionAuthorizationMiddleware.cs:135`).
- Query projections deserialize PostgreSQL `jsonb` payloads into `JsonNode`, then serialize again when responding, producing large transient structures (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:234`).
- Query filter construction repeatedly materializes small JSON documents and stringifies them to pass into `@>` predicates, driving allocator churn under heavy query workloads (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:165`).

## 2. Phase 0 – Measurement & Baseline (1 week)
- Introduce per-request and per-pipeline-step timing via `ActivitySource`/`Activity` scopes to capture latencies without high logging overhead. Surface metrics through `EventCounter` or OpenTelemetry exporters.
- Drive repeatable load scenarios (bulk POST/PUT, high-frequency GET with pagination) using the existing performance suite at `/home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test`, and capture baselines with `dotnet-counters monitor --process-id <pid> System.Runtime[gc-heap-size,cpu-usage,threadpool-queue-length]`.
- Collect CPU flame graphs and hot path traces with `dotnet-trace collect --process-id <pid> --providers Microsoft-Windows-DotNETRuntime:0x1F000200000:5`. Plot with `dotnet-trace report`.
- Capture native+managed samples with `perfcollect collect <label> --pid <pid>` (wraps Linux `perf`) while load runs; review with `perf report` or PerfView for kernel-level hotspots.
- Measure allocation profiles for representative requests using `dotnet-gcdump collect --process-id <pid>` followed by `dotnet-gcdump report`.
- Document baseline throughput (RPS), median/95th latency, and GC pause times to compare against post-optimization runs.

## 3. Phase 1 – High-Impact Quick Wins (2–3 weeks)
1. **Slim response serialization** (Impact: Critical, Effort: Low)  
   - Replace `Results.Content` + `JsonSerializer.Serialize` with `Results.Extensions.Json` (or a cached `JsonSerializerOptions` without indentation) to bypass redundant formatting (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs:127`).  
   - Cache the relaxed encoder options once and reuse.
2. **Streamlined body ingestion** (Impact: High, Effort: Medium)  
   - Switch to reading via `HttpRequest.BodyReader` and parse directly into `JsonDocument`/`Utf8JsonReader`, eliminating the intermediate string (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs:26`).  
   - Null out `FrontendRequest.Body` after `ParseBodyMiddleware` to free the string when large payloads are processed (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ParseBodyMiddleware.cs:75`).
3. **Fast schema validation path** (Impact: Critical, Effort: Medium)  
   - Introduce a concurrent cache keyed by `(resource, method)` that stores compiled `JsonSchema` instances (`src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:34`).  
   - Precompute `EvaluationOptions` and reuse them to avoid per-request allocations.
4. **Lightweight request logging** (Impact: High, Effort: Medium)  
   - Guard body logging behind configuration and remove regex minification/masking by default (`src/dms/core/EdFi.DataManagementService.Core/Middleware/RequestInfoBodyLoggingMiddleware.cs:19`).  
   - When masking is required, traverse with `Utf8JsonReader` and write to a pooled buffer to stop double-serialization.
5. **Trim excess JSON cloning** (Impact: Medium, Effort: Low)  
   - Replace `JsonNode.Parse` rehydration in pruning helpers with direct `Clone`/`DeepClone` on existing nodes to avoid string serialization churn (`src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:108`).
6. **Tune log levels** (Impact: Medium, Effort: Low)  
   - Downgrade high-volume informational logs (e.g., claim-set retrieval) to debug-level to keep production pipelines from spending CPU on string formatting (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ResourceActionAuthorizationMiddleware.cs:140`).

## 4. Phase 2 – Pipeline-Oriented Optimizations (4–6 weeks)
1. **Precompiled JSONPath accessors** (Impact: Critical, Effort: Medium)  
   - At schema load time, compile all JSONPaths into lightweight delegates or cached `JsonPath` objects so each middleware step can walk the DOM without re-parsing expressions (`src/dms/core/EdFi.DataManagementService.Core/ApiSchema/Helpers/JsonHelpers.cs:25` & `:98`).  
   - Store these delegates inside `ResourceSchema` to share across requests.
2. **Single-pass document extraction** (Impact: High, Effort: Medium)  
   - Combine identity, security element, and authorization pathway extraction into one traversal to prevent repeated `SelectNodesFromArrayPathCoerceToStrings` invocations (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ExtractDocumentSecurityElementsMiddleware.cs:30`, `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProvideEducationOrganizationHierarchyMiddleware.cs:77`).  
   - Persist the extracted data in `RequestInfo` so later steps reuse existing results.
3. **Struct-based request models** (Impact: Medium, Effort: Medium)  
   - Convert hot-path DTOs (`DocumentIdentity`, `DocumentSecurityElements`) to `readonly struct` where practical to shrink allocations.
4. **Async pipeline diagnostics** (Impact: Medium, Effort: Low)  
   - Emit per-step duration counters via `Activity` tags or `EventSource` to target the heaviest middleware without verbose logging.
5. **Resilience pipeline tweaks** (Impact: Medium, Effort: Low)  
   - Ensure Polly `ResiliencePipeline` policies use non-blocking fallbacks and tune retry counts for low-latency operations to avoid holding connections unnecessarily (`src/dms/core/EdFi.DataManagementService.Core/Handler/QueryRequestHandler.cs:28`).

## 5. Phase 3 – Backend & Query Improvements (4 weeks)
1. **Zero-copy JSON projection** (Impact: High, Effort: Medium)  
   - Keep PostgreSQL `jsonb` payloads as `JsonElement`/`JsonDocument` and stream them directly to the response writer, avoiding `Deserialize<JsonNode>()` per row (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:234`).  
   - Where possible, use `IAsyncEnumerable<byte[]>` to flush large result sets.
2. **Query filter batching** (Impact: Medium, Effort: Medium)  
   - Replace `CreateJsonFromPath(...).ToString()` with pre-baked `JsonDocument` buffers stored in object pools to suppress repeated stringification (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:185`).  
   - Investigate using `jsonb_build_object` SQL helpers instead of client-side JSON construction.
3. **Transaction scope tuning** (Impact: Medium, Effort: Low)  
   - Switch read-only GET/Query operations to use `BeginTransactionAsync(IsolationLevel.ReadCommitted, true)` or avoid explicit transactions to reduce locking overhead (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/PostgresqlDocumentStoreRepository.cs:52`).
4. **Connection pooling visibility** (Impact: Low, Effort: Low)  
   - Expose Npgsql pool stats via health endpoints to catch saturation early during load tests.

## 6. Phase 4 – Authorization & Security Path Hardening (3 weeks)
1. **Claim-set cache warm path** (Impact: Medium, Effort: Low)  
   - Confirm cache hydration happens at startup and provide metrics/hits to guarantee `_claimSetProvider.GetAllClaimSets()` rarely hits the backing service (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ResourceActionAuthorizationMiddleware.cs:135`).  
   - Add background refresh to limit latency spikes on cache expiry.
2. **Pre-evaluated authorization strategies** (Impact: Medium, Effort: Medium)  
   - Precompute the mapping of resource+action → strategy list when schemas load to avoid per-request list allocations.  
   - Cache `IAuthorizationFiltersProvider` lookups in a dictionary keyed by strategy name.
3. **Authorization pathway pooling** (Impact: Low, Effort: Medium)  
   - Object-pool `AuthorizationPathway` instances for high-throughput POST/PUT scenarios to limit per-request allocations (`src/dms/core/EdFi.DataManagementService.Core/Middleware/ProvideAuthorizationPathwayMiddleware.cs:28`).

## 7. Benchmarking & Regression Strategy
- Add a dedicated `BenchmarkDotNet` project targeting hot code paths (schema validation, JSON extraction, query materialization) to quantify gains on `net8.0`.  
  ```csharp
  [MemoryDiagnoser]
  [SimpleJob(RuntimeMoniker.Net80)]
  public class DocumentValidatorBenchmarks { /* baseline vs cached schema */ }
  ```
- Integrate the `/home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test` suite into CI to execute targeted scenarios and publish latency histograms.
- Track GC metrics and allocation rates before/after each phase to ensure regressions are caught early.
- For deeper investigations, archive `perfcollect` captures alongside managed traces so native regressions are visible in historical comparisons.
- Integrate result validation into CI by capturing `dotnet-counters` snapshots for smoke scenarios and failing the build on regressions beyond agreed thresholds.
