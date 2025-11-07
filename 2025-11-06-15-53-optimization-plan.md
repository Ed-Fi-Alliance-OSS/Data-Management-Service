Ed-Fi DMS (.NET) Performance Optimization Plan — 2025-11-06 15:53

Summary
- Focus: ASP.NET Core frontend, Core middleware pipeline, PostgreSQL backend bridge (app side only).
- Goal: Reduce per-request allocations and CPU, improve p95/p99 latency and RPS without changing functional behavior.
- Approach: Implement low-risk wins first, then deeper refactors; validate with counters and traces under representative load.

Baseline & Profiling
- Load gen: Use existing E2E flows or `getting-started.http` with realistic payloads and query sizes. Also integrate the dedicated suite at `/home/brad/work/dms-root/Suite-3-Performance-Testing/src/edfi-performance-test` for repeatable scenarios (bulk POST/PUT, high-frequency GET with pagination).
- Counters (host or container):
  - `dotnet-counters monitor --process-id <PID> System.Runtime[threadpool-queue-length,threadpool-completed-items-rate,cpu-usage,working-set,alloc-rate,gc-heap-size,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,exception-count] Microsoft.AspNetCore.Hosting[requests-per-second,total-requests]`
- Short trace (sampling + GC + ASP.NET):
  - `dotnet-trace collect -p <PID> --duration 00:00:30 --format Speedscope --providers Microsoft-Windows-DotNETRuntime:0x4800100F:4,Microsoft-AspNetCore-Hosting,Microsoft-System-Net-Http`
- GC dump (if alloc rate high):
  - `dotnet-gcdump collect -p <PID>` then `dotnet-gcdump analyze <file>`
- Activity timings: Add `ActivitySource` scopes for each core pipeline step with step name/status tags; export via OpenTelemetry or EventCounters for low-overhead step latency metrics.
- Native + managed sampling: Capture `perfcollect collect <label> --pid <PID>` while under load and review in `perf report`/PerfView for kernel-level hotspots.
- CI gates: Snapshot `dotnet-counters` for smoke scenarios and fail builds on regressions (RPS/latency/alloc-rate/GC pauses) beyond thresholds.

Top Findings (code review)
- Frontend JSON serialization hot path
  - `Results.Content` with per-request `JsonSerializerOptions` and `WriteIndented = true` causes extra CPU and large string allocations: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs:104`.
  - Always reads request body even for GET/DELETE via `ExtractJsonBodyFrom()`; unnecessary per-request work.
  - Case conversions and header materialization allocate per request (query param `.ToLower()`, `ExtractHeadersFrom()` full dictionary).
- Core middleware pipeline allocations
  - `PipelineProvider.RunInternal` builds a new closure for every step on every request.
  - High-volume Info logs in ASP.NET and Core pipelines do work even when not emitted; `LoggingSanitizer` runs regardless.
  - Query validation uses repeated `.ToLower()` and LINQ in hot paths.
- Backend bridge (app-side)
  - Query materializes DB json to `JsonNode`, then frontend re-serializes; double conversion and heavy allocations for large result sets.
- DI/Startup
  - Builds a ServiceProvider during registration to resolve a logger for a singleton health check; avoid nested container.

Optimization Plan (prioritized)

Phase 0 — Measurement & Baseline (now)
- Instrument core pipeline with `ActivitySource` and tags for step name/duration; minimal OpenTelemetry wiring in perf environments.
- Establish baseline with the perf suite, counters, traces and GC dumps above; record RPS, p50/p95/p99, alloc-rate, GC counts, threadpool queue length, and Npgsql pool stats.
- Capture native + managed profiles with `perfcollect` during load; archive traces alongside results for later comparison.

Phase 1 — Low-Risk, High-ROI (1–2 days)
- Frontend JSON write path
  - Replace `Results.Content(JsonSerializer.Serialize(...))` with `Results.Json(frontendResponse.Body, jsonOptions)`; remove `WriteIndented`.
  - Centralize `JsonSerializerOptions` in DI (`ConfigureHttpJsonOptions`) with `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, `DefaultIgnoreCondition = WhenWritingNull`, `NumberHandling = None`.
  - Enable `ResponseCompression` (Brotli/Gzip) for GET endpoints.
- Skip reading body for GET/DELETE
  - Only parse body for POST/PUT. Refactor `FromRequest(...)` to accept `bool readBody`; call with `true` for POST/PUT and `false` otherwise.
- Streamlined body ingestion for POST/PUT
  - Prefer `HttpRequest.BodyReader` + `Utf8JsonReader/JsonDocument` to avoid intermediate strings.
  - After `ParseBodyMiddleware`, null out `FrontendRequest.Body` to relieve LOH pressure on large requests.
- Lightweight request masking
  - When masking is enabled, traverse via `Utf8JsonReader` and write to a pooled buffer to avoid double serialize/deserialize.
- Logging cost reduction
  - Guard Info logs with `IsEnabled(LogLevel.Information)` and only sanitize when needed. Convert hottest sites to `LoggerMessage.Define`.
- Query validation micro-allocations
  - Use `StringComparer.OrdinalIgnoreCase`/`string.Equals(..., OrdinalIgnoreCase)` and precomputed `HashSet<string>` per resource.
- DI/Startup cleanup
  - Remove `BuildServiceProvider()` during registration; inject `ILogger<DbHealthCheck>` via ctor.

Expected impact
- 10–25% alloc-rate drop on typical requests; fewer gen-0 GCs; p95 latency reduction from avoiding JSON string materialization; improved RPS stability.

Phase 2 — Core Pipeline Allocation Cuts (2–3 days)
- Prebuilt pipeline delegate chain
  - Build a single `Func<RequestInfo, Task>` chain at pipeline creation time and invoke per request without per-step closures.
- Tighten JSON usage in hot steps
  - Keep `JsonNode` for mutation (POST/PUT), prefer `JsonDocument/JsonElement` for read-only flows (GET) to reduce allocations.
- Minimize LINQ on hot paths
  - Replace small LINQ chains with simple loops in hot middlewares (e.g., `ValidateQueryMiddleware`, `ParsePathMiddleware`).
- Cached schema and JSONPath
  - Cache compiled `JsonSchema` by `(resource, method)` and reuse `EvaluationOptions` in `DocumentValidator`.
  - Precompile/cache JSONPath (or lightweight delegates) at schema load and attach to `ResourceSchema` to avoid per-request parsing in `JsonHelpers` and extractors.
- Single-pass extraction
  - Combine identity, security element, and authorization pathway extraction into a single traversal; persist on `RequestInfo` for reuse.
- Struct-based DTOs and diagnostics
  - Convert frequently allocated DTOs (e.g., `DocumentIdentity`, `DocumentSecurityElements`) to `readonly struct` where practical.
  - Emit per-step `Activity` tags for diagnostics with minimal overhead.
- Polly/resilience tuning
  - Ensure policies are non-blocking; tune retry counts/timeouts to avoid holding DB connections longer than necessary.

Expected impact
- Additional 10–20% alloc/CPU reduction from closure removal and LINQ trimming; flatter traces and fewer short-lived objects.

Phase 3 — Streaming & JSON Zero-Copy (3–6 days)
- Stream query responses directly from Npgsql to response
  - In `SqlAction.GetAllDocumentsByResourceName`, iterate the reader and write objects with `Utf8JsonWriter` to the response stream without materializing arrays.
  - Add a streaming `IResult` helper (e.g., `Results.Stream(async (stream, ct) => { using var writer = new Utf8JsonWriter(stream); ... })`).
- Optional NDJSON for very large datasets
  - Support `Accept: application/x-ndjson` to stream line-delimited JSON; eliminate array framing and reduce memory.
- Query filter construction efficiency
  - Pool/batch small JSON docs used in `@>` predicates; consider server-side `jsonb_build_object` to avoid client-side stringification.
- Transaction scope tuning
  - For read-only GET/Query, prefer read-committed without explicit transaction or a read-only transaction to reduce locking.
- Pooling visibility
  - Expose Npgsql pool stats via a health/diagnostic endpoint to detect saturation under load.

Expected impact
- Large response scenarios: 2–5x memory reduction, 20–40% CPU reduction on serialization; improved tail latency.

Phase 4 — Authentication & Error Paths (1–2 days)
- Align `FrontendResponse.Body` types
  - Ensure error responses in `JwtAuthenticationMiddleware` and `JwtRoleAuthenticationMiddleware` use `JsonNode` bodies (or extend `IFrontendResponse` to support pre-serialized UTF‑8 payloads) for consistency.
- Masking cost controls
  - Keep body masking fully disabled unless explicitly configured; avoid any regex/minification work when Debug is off.
- Authorization optimizations
  - Confirm claim-set cache warmup at startup; add hit/miss metrics and background refresh to avoid expiry spikes.
  - Precompute resource+action → strategy mapping at schema load; cache `IAuthorizationFiltersProvider` lookups by strategy name.
  - Pool `AuthorizationPathway` instances for high-throughput POST/PUT scenarios.

Phase 5 — Kestrel, GC, and Hosting Tuning (0.5–1 day)
- Kestrel
  - Ensure HTTP/1.1 + HTTP/2 enabled; `options.AddServerHeader = false;`; tune keep-alive/ping timeouts per deployment.
- GC/ThreadPool
  - Confirm Server GC and Tiered PGO (.NET 8 defaults). Optionally set `DOTNET_ThreadPool_ForceMinWorkerThreads` in perf tests to stabilize warm-up.
- JSON options
  - Configure `HttpJsonOptions` globally to avoid per-call option creation; unify content-type handling (application/json; charset=utf-8).

Targeted Code Changes (by file)
- Frontend
  - src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs — use `Results.Json` with singleton options; remove `WriteIndented`; conditional body read; add BodyReader path.
  - src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/LoggingMiddleware.cs — guard Info logs; prefer Debug for high-volume paths.
  - src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs — remove nested ServiceProvider build; add `ResponseCompression` (GET only) and `HttpJsonOptions`; minimal OTel wiring for perf runs.
- Core
  - src/dms/core/EdFi.DataManagementService.Core/Pipeline/PipelineProvider.cs — prebuilt delegate chain.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/RequestResponseLoggingMiddleware.cs — log-level guards; LoggerMessage.Define.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/ParsePathMiddleware.cs — `OrdinalIgnoreCase` comparisons; avoid transient allocations.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/ValidateQueryMiddleware.cs — case-insensitive matching without `.ToLower()`; precomputed allowed fields.
  - src/dms/core/EdFi.DataManagementService.Core/Utilities/LoggingSanitizer.cs — avoid work when log level disabled; keep fast path.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/JwtAuthenticationMiddleware.cs and JwtRoleAuthenticationMiddleware.cs — align body types and consider pre-serialized UTF‑8 payload support.
  - src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs — cache compiled schemas; reuse EvaluationOptions; replace stringify+parse prunes with direct clone/deep-clone.
  - src/dms/core/EdFi.DataManagementService.Core/ApiSchema/Helpers/JsonHelpers.cs — cache/precompile JSONPath; expose typed accessors/delegates.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/ProvideEducationOrganizationHierarchyMiddleware.cs — leverage precompiled paths and single-pass extraction.
  - src/dms/core/EdFi.DataManagementService.Core/Middleware/ResourceActionAuthorizationMiddleware.cs — reduce info-level logs; instrument claim-set cache hit/miss metrics.
- Backend bridge (app side)
  - src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs — avoid `Deserialize<JsonNode>()`; return `JsonElement` or stream directly end-to-end. Pool filter JSON buffers or use `jsonb_build_object`.
  - src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/PostgresqlDocumentStoreRepository.cs — use read-only/no transaction for read paths where safe; expose pool stats via health/diagnostics.

Validation Plan
- Before/After (Phase 1 & 2)
  - Run steady 10–15 min loads; capture alloc-rate, gc-heap-size, GC counts, RPS. Expect >20% alloc-rate drop, fewer gen0s, ~0 queue length, better p95.
- Large GET (Phase 3)
  - 10k–100k documents; verify flat memory plateau; compare CPU and wall time vs baseline; validate streaming correctness.
- Instrumentation & CI
  - Persist traces (dotnet-trace, perfcollect) and counters per run; publish latency histograms. Wire CI gates to fail on regressions beyond thresholds.
  - Add a BenchmarkDotNet project for hot paths (schema validation, JSON extraction, query materialization) and publish results on net8.0.

Risks & Tradeoffs
- Changing JSON pipeline types (JsonNode → JsonElement/streaming) touches multiple layers; replace across the codebase and validate thoroughly.
- Lowering log verbosity reduces observability; retain structured error logs.
- Response compression reduces bandwidth but costs CPU; enable for GETs.

Next Steps
1) Implement Phase 1 (frontend JSON, GET/DELETE body skip, BodyReader, logging guards, DI cleanup); validate with counters/trace.
2) Implement Phase 2 pipeline chain and query validation/schema/JSONPath caching; rebaseline and review traces.
3) Implement Phase 3 streaming across all GET endpoints; validate performance and correctness under load.
4) Align authentication error payload types and finalize hosting/GC tuning; add BenchmarkDotNet microbenchmarks.
