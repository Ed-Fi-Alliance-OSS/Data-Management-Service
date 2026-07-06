# Plan: Reduce The Top 7 DMS Allocation Sources

## Goal

Reduce the allocation-driven GC slowdown observed in the 2026-07-06 full .NET monitored
30-minute, 20-client PostgreSQL volume run:

`/home/brad/work/dms-root/Suite-3-Performance-Testing/DmsTestResults/2026-07-06-15-31-perf-run-fix3-2-20c-30m-volume-full-dotnet`

The trace showed sustained DMS allocation around 350-370 MB/sec and GC duration around
267 ms/sec during the 5-minute trace window. PostgreSQL was not the bottleneck.

## Success Criteria

Keep allocation/GC validation separate from throughput validation so monitoring overhead does
not distort acceptance decisions.

Instrumented allocation gate:

- 8 to 10 minute, 20-client diagnostic run with full .NET monitoring has 0 load-test failures.
- Average DMS allocation rate is reduced by at least 35% from the monitored run.
- GC duration drops from about 267 ms/sec to below 100 ms/sec, with a stretch target below
  50 ms/sec.
- Top first EdFi allocation frames show the accepted phase's target frame moving materially
  down or out of the hot list.

Uninstrumented throughput gate:

- 30-minute, 20-client no-.NET-monitoring volume run has 0 load-test failures.
- P95 write latency improves materially from the prior no-monitoring run's 41.9 ms.
- Throughput improves materially from the prior no-monitoring run's 1467.10 RPS.
- DMS CPU, DMS RSS/working set, and load-generator CPU are recorded so a Locust or host
  resource ceiling is not mistaken for a DMS regression or improvement.
- PostgreSQL evidence remains clean: no sustained lock waits, no shared-block read pressure,
  and no query mean-time regression that explains application slowdown.

## Evidence To Preserve

Current top first EdFi allocation frames from the trace:

| Rank | Sampled MiB | Frame |
| ---: | ---: | --- |
| 1 | 50.8 | `CanonicalJsonSerializer.WriteCanonicalObject(...)` |
| 2 | 45.4 | `WebApplicationBuilderExtensions.<ConfigureDatastore>b__1_0(IServiceProvider)` |
| 3 | 39.4 | `SessionRelationalCommandExecutor.ExecuteReaderAsync(...)` |
| 4 | 35.4 | `HydrationExecutor.ExecuteAsync(...)` |
| 5 | 28.8 | `JwtValidationService.ValidateAndExtractClientAuthorizationsAsync(...)` |
| 6 | 20.3 | `JwtAuthenticationMiddleware.Execute(...)` |
| 7 | 13.1 | `AspNetCoreFrontend.ExtractJsonBodyFrom(...)` |

The plan below fixes these in dependency order, not strictly trace-rank order. The JWT
middleware and JWT validation items should be implemented together because caching
validated tokens changes both allocation profiles.

## Phase 0: Measurement Guardrails

Before changing behavior, add a repeatable measurement loop so each fix can be accepted or
rejected independently.

1. Add a short allocation-focused diagnostic run script that runs:
   - 20 clients.
   - 8 to 10 minutes.
   - `LOG_LEVEL=Warning`.
   - `dotnet-counters` for allocation rate, GC count, and GC pause.
   - A 2 to 5 minute `dotnet-trace` steady-state allocation sample.
2. Keep the existing 30-minute no-monitoring run as the throughput gate.
3. For every phase below, record:
   - Requests/sec.
   - P50/P95/P99 write latency.
   - `dotnet.gc.heap.total_allocated`.
   - `dotnet.gc.pause.time`.
   - Top first EdFi allocation frames.
   - DMS CPU and memory.
   - Load-generator CPU.
   - PostgreSQL wait and statement evidence.
4. Treat monitored-run RPS as diagnostic only. Use the no-monitoring run for throughput and
   latency acceptance.
5. Do not merge a phase that improves allocations but regresses correctness, authorization
   behavior, ETag stability, or relational backend parity.

## Phase 1: JWT Validation And Authentication Allocations

Targets:

- `src/dms/core/EdFi.DataManagementService.Core/Security/JwtValidationService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/JwtAuthenticationMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Model/ClientAuthorizations.cs`

Current allocation causes:

- Every request validates the same bearer token again.
- `TokenValidationParameters` is allocated per request.
- Claim extraction creates a `List<Claim>` and repeatedly scans it with `Find`.
- Comma-delimited claim parsing uses `Split`, LINQ, and `ToList`.
- `JwtAuthenticationMiddleware` slices the bearer token string on every request.

Implementation plan:

1. Add a per-process validated-token cache inside `JwtValidationService`, keyed by a stable
   cryptographic hash of the normalized token. Do not use the raw bearer token or full
   Authorization header as the cache key because those values are secrets.
2. Bound the cache by entry count, and expose the limit through configuration. The cache must not
   become an unbounded memory sink when many valid unique tokens are presented.
3. Cache only successful validations.
4. Expire each cache entry no later than the JWT `exp` value minus clock skew. Also cap cache
   lifetime with a short configurable upper bound so signing-key rollover is not masked for too
   long.
5. Include the OIDC issuer, configured audience, and signing-key key IDs in a validation
   fingerprint that participates in cache lookups, so metadata changes naturally miss the cache
   and force revalidation.
6. Store an immutable validation result containing the `ClientAuthorizations` and the minimal
   principal data required by current callers. Do not share a mutable `ClaimsPrincipal` instance
   across requests.
7. Cache `TokenValidationParameters` by the OIDC validation fingerprint so the object is rebuilt
   only when issuer, audience, signing keys, or relevant validation settings change.
8. Replace `principal.Claims.ToList().Find(...)` with a single pass over claims.
9. Replace `string.Split(...).Select(...).ToList()` for numeric ID claims with span-based
   parsing into pre-sized lists.
10. Change `ClientAuthorizations` from mutable `List<T>` properties to `IReadOnlyList<T>`
    properties backed by arrays, and update call sites to consume the read-only contract directly.
11. Change the validation API to accept the full Authorization header plus the bearer prefix
    offset. Hash the token span for cache lookup and allocate the token string only on cache miss
    before calling `JwtSecurityTokenHandler`.
12. Add tests for:
    - Expired tokens are never accepted from cache.
    - Invalid tokens are not cached.
    - The cache is bounded and evicts entries under pressure.
    - Token cache respects different tenants/clients/tokens.
    - Claim parsing handles empty, single-value, and multi-value claims.
    - Signing-key/configuration refresh changes the validation fingerprint and misses the cache.
    - Raw bearer tokens are not used as cache keys or emitted in logs.

Acceptance check:

- `JwtValidationService` and `JwtAuthenticationMiddleware` should fall materially in the top
  allocation list. The combined sampled MiB for ranks 5 and 6 should drop by at least 70% on
  the same workload because the Suite 3 volume run reuses tokens heavily.

## Phase 2: Request Body Extraction And Parsing

Targets:

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Frontend/FrontendRequest.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ParseBodyMiddleware.cs`

Current allocation causes:

- `AspNetCoreFrontend.ExtractJsonBodyFrom` reads every write body into a new `string`.
- `ParseBodyMiddleware` then reparses that string into `JsonNode`.
- This creates a large request-body string even though the core pipeline ultimately needs a
  parsed JSON document.

Implementation plan:

1. Add a nullable parsed-body property to `FrontendRequest` for ASP.NET callers while keeping the
   existing string `Body` property for tests and non-ASP.NET callers.
2. In the ASP.NET path, read JSON request bodies from `HttpRequest.BodyReader` into a pooled UTF-8
   buffer, detect empty and whitespace-only bodies before parsing, parse with `JsonNode.Parse`
   over the UTF-8 span, and never materialize the request body as a string.
3. Update `ParseBodyMiddleware` to consume the pre-parsed `JsonNode` when present and fall back to
   the existing string `Body` path for compatibility.
4. Preserve the exact `ParseBodyMiddleware` response behavior for:
   - Missing or whitespace-only bodies.
   - Invalid JSON exception messages.
   - `traceId`/correlation ID values.
   - Problem-details shape and status codes.
5. Ensure duplicate-property, content-type, coercion, validation, and profile middleware still
   see the same JSON semantics and error messages by carrying parse-failure details to
   `ParseBodyMiddleware` and generating the existing core error response shape there.
6. Avoid retaining pooled buffers beyond the request lifetime.
7. Keep form-url-encoded handling separate; do not route form bodies through the JSON path.
8. Add tests for:
   - Empty body behavior.
   - Whitespace-only body behavior.
   - Invalid JSON behavior and error response shape.
   - Invalid JSON correlation ID preservation.
   - Valid POST/PUT body parsing.
   - Existing unit tests that directly construct `FrontendRequest` with `Body: string`.

Acceptance check:

- `AspNetCoreFrontend.ExtractJsonBodyFrom` should drop out of the top seven allocation frames.
- `System.String` and `System.Char[]` allocation should decrease noticeably in the trace.

## Phase 3: Canonical JSON And ETag Allocation

Targets:

- `src/dms/core/EdFi.DataManagementService.Core/Utilities/CanonicalJsonSerializer.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Utilities/ResourceEtagFormatter.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/InjectVersionMetadataToEdFiDocumentMiddleware.cs`
- Relational read/write call sites that call `RelationalApiMetadataFormatter.FormatEtag(...)`

Current allocation causes:

- ETags are computed by walking `JsonNode`, sorting object properties, writing canonical JSON,
  and hashing the resulting stream.
- The top frame is `WriteCanonicalObject`, with most sampled allocation showing up as strings
  and byte arrays under canonical serialization.
- Some paths may recompute the same ETag for the same request document.
- Even when ETag calls are not duplicated, each canonical hash still pays serializer-level
  allocation costs for the writer/hash path, sorted-property handling, and final hash/base64
  materialization.

Implementation plan:

1. Add microbench-style unit tests around `ResourceEtagFormatter.FormatEtag` using representative
   Suite 3 write payloads.
2. Add temporary call-count instrumentation for `FormatEtag` in the volume run to identify
   duplicate ETag computation per request.
3. Cache the ETag computed by `InjectVersionMetadataToEdFiDocumentMiddleware` in `RequestInfo` and
   pass it through write request objects to downstream code.
4. Avoid recomputing ETags for current-state checks when the persisted current ETag is already
   available and semantically equivalent.
5. Keep the canonical algorithm stable: object properties must remain ordinal-sorted, arrays
   must preserve order, and server-generated fields must remain excluded.
6. Reduce serializer-level allocation in the canonical hash path regardless of duplicate-call
   findings:
   - Replace hash APIs that return a new `byte[]` with span-based hash finalization and direct
     base64 formatting so only the required ETag string is allocated.
   - Investigate replacing the `Stream` wrapper under `Utf8JsonWriter` with a pooled
     hash-forwarding `IBufferWriter<byte>` if the microbench/trace shows per-call byte-buffer
     churn from the writer path.
   - Keep sorted-property handling pooled, and measure whether `ArrayPool` pressure, array
     clearing, or recursive object traversal is still a material contributor before adding more
     caching.
   - Ensure production ETag code does not use `SerializeToString` or `SerializeToUtf8Bytes`.
7. Do not change the canonical output contract in this phase. Serializer internals may change,
   but the observable canonical bytes and ETags must remain stable.
8. Add tests proving ETags are unchanged for:
   - Different property order.
   - Nested objects.
   - Arrays.
   - Server-generated field exclusion.
   - Descriptor and resource responses.

Acceptance check:

- `CanonicalJsonSerializer.WriteCanonicalObject` sampled allocation drops by at least 40% on
  the same workload through the combined effect of duplicate-call removal and serializer-level
  allocation reduction. If duplicate-call instrumentation shows low duplication, the phase must
  still produce a meaningful per-call allocation reduction in the canonical hash microbench before
  it is accepted.
- All existing ETag tests continue to pass unchanged or with strictly equivalent expectations.

## Phase 4: Scoped Repository DI Resolution

Targets:

- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`

Current allocation causes:

- The trace attributes 45.4 MiB to the scoped factory registered in `ConfigureDatastore`:
  `IDocumentStoreRepository` resolves `RelationalDocumentStoreRepository` through
  `IServiceProvider.GetRequiredService`.
- The allocated type is dominated by DI service-cache dictionary entry arrays, indicating a
  high-frequency scoped resolution path.

Implementation plan:

1. Add a small diagnostic counter to confirm how often `IDocumentStoreRepository`,
   `IQueryHandler`, and `RelationalDocumentStoreRepository` are resolved per request.
2. Add a composition test proving supported request paths resolve only one of the two repository
   interfaces in a request scope.
3. Replace service-provider lambda registrations with direct typed registrations:
   - `AddScoped<IDocumentStoreRepository, RelationalDocumentStoreRepository>()`.
   - `AddScoped<IQueryHandler, RelationalDocumentStoreRepository>()`.
4. Avoid introducing singleton state for request-scoped connection, tenant, or data-store
   behavior.
5. Add tests for DI composition in PostgreSQL and MSSQL modes.

Acceptance check:

- The `ConfigureDatastore` factory frame should disappear from the allocation top seven.
- No supported request path may resolve both `IDocumentStoreRepository` and `IQueryHandler` in
  the same request scope after switching to direct typed registrations.
- Request behavior must remain identical for reads, writes, descriptors, and profile-aware
  paths.

## Phase 5: Relational Command Executor Allocations

Targets:

- `src/dms/backend/EdFi.DataManagementService.Backend/SessionRelationalCommandExecutor.cs`
- Common call sites using `IRelationalCommandExecutor.ExecuteReaderAsync(...)`

Current allocation causes:

- Generic async callback execution allocates state machines and callback/delegate objects in
  high-frequency lookup and authorization paths.
- `DbRelationalCommandReader` and command creation happen for every small read.
- Npgsql async reader frames are expected, but DMS can reduce its own wrapper overhead and
  reduce the number of small reads.

Implementation plan:

1. Identify the highest-volume `ExecuteReaderAsync` call sites in the trace and map them to
   read operations: UUID lookup, target lookup, authorization lookup, token-info lookup,
   descriptor/reference lookup.
2. Add specialized executor methods for common shapes:
   - Single scalar.
   - Optional single row.
   - Small list.
3. Use static delegates for specialized executor callbacks so call sites do not allocate
   closures.
4. Keep `Task<TResult>` in this phase; do not convert executor APIs to `ValueTask<TResult>` while
   the main cost is closure and wrapper allocation.
5. Batch adjacent small reads when they occur in the same write path and use the same connection
   and transaction.
6. Keep cancellation token propagation intact.
7. Add unit tests for command disposal, reader disposal, cancellation propagation, exception
   propagation, and both PostgreSQL/MSSQL dialect behavior.

Acceptance check:

- `SessionRelationalCommandExecutor.ExecuteReaderAsync` sampled allocation drops by at least
  30% without increasing PostgreSQL query time or round trips.

## Phase 6: Hydration Executor And Reader Materialization

Targets:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationExecutor.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/HydrationReader.cs`
- `HydrationBatchBuilder` and read materialization call sites

Current allocation causes:

- `HydrationExecutor.ExecuteAsync` builds batch SQL and result containers per execution.
- `CreateDescriptorRowsBuffer` allocates descriptor row holders even when descriptor projection
  is disabled by execution options.
- `HydrationReader` creates un-sized `List<T>` instances and `object?[]` per table row.
- `ReadTableRowsAsync` calls `IsDBNullAsync` for every column before `GetValue`, adding async
  overhead in a tight loop.

Implementation plan:

1. First implement the low-risk projection fix: when `IncludeDescriptorProjection` is false, use a
   cached empty descriptor row buffer instead of allocating descriptor row holders.
2. Cache hydration SQL batch text for stable combinations of read plan, keyset shape, dialect,
   and execution options. Bind only request-specific parameters per execution.
3. Pre-size `List<T>` instances from keyset counts and hydration plan metadata.
4. Replace `IsDBNullAsync` plus `GetValue` with `reader.GetValue(i)` and a `DBNull.Value`
   check, with PostgreSQL and MSSQL tests covering null materialization behavior.
5. Avoid `object?[]` row buffers on hot write paths that only need a subset of columns. Add
   typed lightweight row readers for those paths.
6. Keep PostgreSQL and MSSQL hydration tests in lockstep.
7. Add tests for:
   - Descriptor projection enabled.
   - Descriptor projection disabled.
   - Document-reference lookup enabled and disabled.
   - Empty result sets.
   - Multi-table resources.

Acceptance check:

- `HydrationExecutor.ExecuteAsync` sampled allocation drops by at least 30%.
- No read materialization regression in descriptor projection, resource links, profiles, or
  document-reference lookup behavior.

## Phase 7: Cross-Cutting Cache Payload Error

This was not one of the top seven allocation frames, but the monitored run repeatedly logged:

`Cache MaximumPayloadBytes (1048576) exceeded`

Treat this as the first prerequisite for reliable final performance validation. Final 30-minute
acceptance runs should not be interpreted while this error is still repeating every 10 minutes.

Likely target:

- `src/dms/core/EdFi.DataManagementService.Core/Security/CachedClaimSetProvider.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`

Implementation plan:

1. Confirm the payload is the claim-set cache entry by logging cache key and serialized payload
   size on cache write failure.
2. Replace the claim-set `HybridCache` entry with `IMemoryCache` plus per-cache-key
   `SemaphoreSlim` stampede protection so the full claim-set graph is not serialized through the
   default 1 MB `HybridCache` payload limit.
3. Keep the existing `ClaimSetsCacheExpirationSeconds` TTL, cache only successful non-null
   provider results, and remove per-key semaphores when entries are invalidated.
4. Add a regression test with a large claim-set payload that exceeds 1 MB under the current
   default.

Acceptance check:

- The 30-minute run has no repeating `HybridCache` max-payload errors.
- Throughput does not show 10-minute periodic dips tied to cache expiration.

## Final Verification

After all accepted phases:

1. Run focused unit tests for changed areas:
   - `CanonicalJsonSerializerTests`
   - `ResourceEtagFormatterTests`
   - `JwtValidationServiceTests`
   - `JwtAuthenticationMiddlewareTests`
   - `ParseBodyMiddlewareTests`
   - `AspNetCoreFrontend` tests
   - `SessionRelationalCommandExecutorTests`
   - Hydration executor/reader tests for PostgreSQL and MSSQL
2. Run the API-level integration tests that cover relational read/write, descriptor, profile,
   authorization, and token-info behavior.
3. Run an 8 to 10 minute allocation diagnostic volume test with full .NET monitoring.
4. Run the final 30-minute, 20-client volume test without .NET monitoring.
5. Confirm DMS logs have no repeating `HybridCache` max-payload errors during the final run.
6. Record DMS CPU, DMS RSS/working set, load-generator CPU, and PostgreSQL evidence for both
   diagnostic and final runs.
7. Compare against:
   - Current no-monitoring 30-minute result:
     `2026-07-06-14-30-perf-run-fix3-2-20c-30m-volume-pgsm-no-dotnet`
   - Current monitored 30-minute result:
     `2026-07-06-15-31-perf-run-fix3-2-20c-30m-volume-full-dotnet`
   - Any same-duration pre-regression baseline available at the time of verification.

## Recommended Implementation Order

1. HybridCache payload error fix before any other perf run.
2. JWT validation/authentication cache and low-allocation claim extraction.
3. Request body extraction/parsing without the intermediate string.
4. Duplicate ETag call removal without canonical serializer tuning.
5. Scoped repository DI resolution cleanup with direct typed registrations and proof that supported
   request paths do not resolve both repository interfaces in the same scope.
6. Hydration low-risk allocation fixes.
7. Relational command executor specialization and batching.

This order removes a known periodic cache error first, then attacks repeated per-request
allocations, then removes framework/container overhead, then addresses deeper relational
materialization changes where the correctness blast radius is larger.
