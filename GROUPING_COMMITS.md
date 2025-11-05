# Grouping Commits Implementation Plan

This document outlines the steps to move the Data Management Service to a scoped, request-level "unit of work" so we can batch commands and reduce the volume of tiny PostgreSQL commits. It also captures related batching optimizations.

---

## 1. Establish a Scoped Database Session

1. **Create an `IDbSession` abstraction**
   - Interface should expose:
     - `Task<NpgsqlConnection> OpenConnectionAsync()`
     - `Task<NpgsqlTransaction> BeginTransactionAsync()`
     - `NpgsqlConnection Connection { get; }`
     - `NpgsqlTransaction? Transaction { get; }`
     - `bool HasActiveTransaction { get; }` (true between `BeginTransactionAsync` and either `CommitAsync` or `RollbackAsync`)
     - `Task CommitAsync()`, `Task RollbackAsync()`
   - Implement lazy connection opening—only hit the pool when a handler actually needs the database.
   - Define a companion `IDbSessionFactory` interface in Core (e.g., `src/dms/core/EdFi.DataManagementService.Core/Backend/IDbSessionFactory.cs`):
     ```csharp
     namespace EdFi.DataManagementService.Core.Backend;

     public interface IDbSessionFactory
     {
         Task<IDbSession> CreateAsync(TraceId traceId, CancellationToken cancellationToken = default);
     }
     ```
     This keeps the factory contract backend-neutral while allowing implementations to include trace IDs in connection telemetry.

2. **Implement `DbSession` (per-request, factory-created)**
   - `DbSession` is a disposable object produced by the factory for each request; it is not registered in DI on its own.
   - Constructor accepts `NpgsqlDataSource`, `IsolationLevel`, `ILogger`.
   - On first call to `BeginTransactionAsync`:
     - Open connection via `_dataSource.OpenConnectionAsync()`
     - `Transaction = await connection.BeginTransactionAsync(_isolationLevel)`
   - `CommitAsync`/`RollbackAsync` should no-op if no transaction was started.
   - Both methods must flip `HasActiveTransaction` back to false after executing so the middleware can detect whether a commit is still pending.
   - Ensure `DisposeAsync`/`Dispose` clean up `Transaction` and `Connection`. Wrap in try/catch and log failures so the middleware can still throw if needed.

3. **Register session factory in DI**
   - Leave the PostgreSQL repositories registered as singletons so their caches and pipeline wiring stay valid.
   - Add an `IDbSessionFactory` implementation (backend-specific) that wraps `NpgsqlDataSource`, honors the configured isolation level, and produces new `DbSession` instances on demand.
   - Register the factory as a singleton in `PostgresqlServiceExtensions`; the middleware and any other callers obtain sessions exclusively through this factory.
   - Ensure background singletons (e.g., `DbHealthCheck`) continue to open their own transient connections via `NpgsqlDataSource` and do not consume sessions meant for API requests.

4. **Add request middleware for lifetime management**
   - Implement `UnitOfWorkMiddleware` as a DMS Core pipeline step (`IPipelineStep` in `src/dms/core/EdFi.DataManagementService.Core/Middleware`). The middleware must remain stateless between requests.
   - The middleware should:
     1. Resolve an `IDbSessionFactory` from DI in its constructor.
     2. In `Execute`, create a fresh session by calling `var session = await _sessionFactory.CreateAsync(requestInfo.FrontendRequest.TraceId);`.
     3. Attach the session to the current `RequestInfo`.
     4. Invoke `await next()` inside a try block.
     5. If `next` completes without exception and `session.HasActiveTransaction` is true, call `CommitAsync`; otherwise do nothing.
     6. On exception, call `RollbackAsync` (harmless if no transaction began) and rethrow.
     7. In a `finally`, clear `RequestInfo.DbSession` and dispose the session (`await session.DisposeAsync()`).
   - Append the middleware to the end of `GetCommonInitialSteps()` in `ApiService` (`src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`) by resolving it from `_serviceProvider` (e.g., `_serviceProvider.GetRequiredService<UnitOfWorkMiddleware>()`). Because the middleware remains stateless and obtains its session per `Execute`, constructing it at pipeline-build time is safe.
   - Wiring checklist:
    1. **Session factory in backend** — In `PostgresqlServiceExtensions.AddPostgresqlDatastore`, keep every repository as a singleton and register `services.AddSingleton<IDbSessionFactory>(sp => new DbSessionFactory(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IOptions<DatabaseOptions>>(), sp.GetRequiredService<ILoggerFactory>()));`. The factory encapsulates backend-specific knowledge (connection pool, isolation level, logging) and exposes `Task<IDbSession> CreateAsync(TraceId traceId)` for new sessions.
    2. **Middleware registration** — In `DmsCoreServiceExtensions`, register `services.AddTransient<UnitOfWorkMiddleware>();`. The middleware constructor should accept only singleton-safe dependencies (e.g., `IDbSessionFactory`, `ILogger<UnitOfWorkMiddleware>`).
    3. **Pipeline usage** — Update `ApiService.GetCommonInitialSteps()` to append the middleware via `_serviceProvider.GetRequiredService<UnitOfWorkMiddleware>()`. Other steps can continue to be constructed with `new` because they have no additional dependencies.
    4. **Middleware execution** — Inside the middleware, once `await next()` returns, call `CommitAsync` only when `session.HasActiveTransaction` is true; otherwise assume the repository rolled back already. Always dispose the session before returning.

---

## 2. Refactor Repositories to Use the Session

1. **Flow the session through `RequestInfo`**
   - Extend `RequestInfo` (`src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs`) with an `IDbSession` property (default null).
   - `UnitOfWorkMiddleware` sets this property before invoking `next()` and clears it during disposal so downstream steps always see the current session.
   - Update pipeline handlers (`UpsertHandler`, `UpdateByIdHandler`, `DeleteByIdHandler`, query handler, authorization handlers, etc.) to pass `requestInfo.DbSession` to backend interfaces.

2. **PostgresqlDocumentStoreRepository**
   - Keep the repository registered as a singleton; do not inject `IDbSession` directly.
   - Update `IDocumentStoreRepository`, `IQueryHandler`, and companion interfaces so each operation accepts an `IDbSession` parameter supplied by handlers (e.g., `UpsertDocument(IDbSession session, IUpsertRequest request)`, `GetDocumentById(IDbSession session, IGetRequest request)`). This change cascades through every handler (`UpsertHandler`, `UpdateByIdHandler`, `DeleteByIdHandler`, `QueryRequestHandler`), the resilience wrappers, and all unit tests that mock these interfaces; note each location so nothing is missed during implementation.
   - Mirror the same signature change in the backing operation interfaces (`IUpsertDocument`, `IUpdateDocumentById`, `IDeleteDocumentById`, `IGetDocumentById`, `IQueryDocument`) so the repository can forward the session.
   - Leave `IAuthorizationRepository` on the current pattern for now; it will be reworked separately.
   - Use `await dbSession.OpenConnectionAsync()` / `await dbSession.BeginTransactionAsync()` within each method; this keeps connection ownership centralized in the session.
   - For read-heavy paths, open the transaction once per request (e.g., call `await dbSession.BeginTransactionAsync()` before the first authorization or data query) so authorization checks and the resource payload share the same snapshot. If a lightweight helper (such as `EnsureReadOnlyTransactionAsync`) is added to `IDbSession`, use it here.
   - On any failure path (e.g., `UpsertFailureWriteConflict`, foreign-key violations, optimistic-lock failures), explicitly call `await dbSession.RollbackAsync()` before returning so the transaction is clean for Polly retries and `HasActiveTransaction` is cleared. Let the middleware commit only when the repository leaves an active transaction after a success path.

3. **Operations (`UpsertDocument`, `DeleteDocumentById`, etc.)**
   - Continue to accept `NpgsqlConnection` / `NpgsqlTransaction` parameters; the repository supplies them via the new session.
   - Retain targeted savepoints when the operation needs to recover from partial failures (e.g., the FK handling in `DeleteDocumentById` still uses `SaveAsync` / `RollbackAsync` to surface referencing resources). Remove only the catch-all commit/rollback logic now owned by the middleware.

4. **`SqlAction` changes**
   - Confirm all methods accept `NpgsqlConnection` / `NpgsqlTransaction`. With session in place, the repository will continue to pass the active objects—no signature changes needed.
   - Verify no helper method tries to dispose the connection/transaction.

5. **Unit tests / integration tests**
   - Update pipeline tests to assert that `UnitOfWorkMiddleware` is appended to `GetCommonInitialSteps()` and that it populates `RequestInfo.DbSession`.
   - Add coverage verifying that failure paths explicitly roll back and that `UnitOfWorkMiddleware` commits only when `HasActiveTransaction` is true.
   - Adjust repository tests to supply a fake `IDbSession` (or a wrapper exposing a real connection/transaction).
   - Add middleware coverage that verifies commit-on-success and rollback-on-exception behaviors.

---

## 3. Connection Lifecycle, Authorization Reads, & Middleware Ordering

1. **Ensure middleware order**
   - Register `UnitOfWorkMiddleware` after authentication and correlation ID middleware so request context is available.
   - For endpoints that do not need a transaction (e.g. a health endpoint), allow the middleware to detect “no transaction started” and skip commit/rollback.

2. **Disable Auto Reset**
   - With `NoResetOnClose=true` already applied via `NpgsqlDataSourceBuilder`, validate that the middleware disposes the session at end of request to avoid leaking state.
   - For now, we rely on the fact that the service does not issue per-request `SET` commands.

3. **Authorization repositories**
   - The current authorization repository opens standalone connections; add TODO comments noting that it should consume `IDbSession` once the rewrite lands so authorization checks and data queries share the same snapshot.
   - When that rewrite happens, mirror the document repository approach: accept the session, reuse the shared transaction, and avoid committing or rolling back independently.

4. **Read-only transactions**
   - Extend `IDbSession` with a way to request a read-only transaction (e.g., `BeginTransactionAsync(TransactionUsage.Read)` or `EnsureReadOnlyTransactionAsync()`).
   - Handlers that perform GETs should call the read-only path before the first authorization lookup or data query so every statement in the request observes the same snapshot.

---

## 4. Audit Savepoint Usage

1. **DeleteDocumentById.cs**
   - Keep the targeted `transaction.SaveAsync("beforeDelete")` / `transaction.RollbackAsync("beforeDelete")` pair so FK violations can be rolled back to the savepoint and `FindReferencingResourceNamesByDocumentUuid` can still run.
   - Remove only the outer commit/rollback handling—the middleware owns the final transaction outcome.

2. **Any other savepoint usage**
   - Grep for `.SaveAsync(` to ensure that remaining savepoints are intentional (e.g., surface partial-failure details) and not compensating for missing middleware rollback logic.

---

## 5. Batch Dependent Statements

1. **UpsertDocument.AsInsert**
   - Combine the initial reference validation and document insert into a single `NpgsqlBatch` or stored procedure:
     - Batch statement 1: pre-flight reference check.
     - Batch statement 2: insert document & alias (existing call).
   - If we choose the stored procedure route:
     - Create a plpgsql function that accepts the upsert payload and returns `(document_id, invalid_ids uuid[])`.
     - Replace multiple round-trips with a single command invocation.

2. **Alias insert**
   - When `superclassIdentity` is present, queue that insert into the same batch rather than executing separately.

3. **InsertReferences call**
   - Keep as final statement for now; once the function API is expanded, it could accept a boolean to run the pre-check and the insert logic in one round-trip.

4. **Implementation strategy**
   - Start with `NpgsqlBatch`: easier to test, no database schema change.
   - Measure performance; if it still spends time on round-trips, evaluate moving logic into a single plpgsql function.

---

## 6. Validation & Rollout

1. **Smoke tests**
   - Run existing integration/e2e tests to ensure transaction boundaries remain correct.
   - Add a regression case that performs an upsert followed by a query within a single request to confirm they share the unit-of-work connection.

2. **Monitoring**
   - After deployment, monitor `pg_stat_monitor` to confirm the utility command counts drop dramatically (e.g., UNLISTEN * should no longer fire 70k times per minute).
   - Confirm the WAL write/fsync rate drops proportionally.

---

## 7. Follow-up Enhancements

1. **Request batching API**
   - Once the unit-of-work is in place, expose higher-level APIs that can bundle multiple document operations into one request (e.g., bulk document imports).
   - Implement C# clients that send multiple upserts, leveraging the shared transaction automatically.

2. **Further Npgsql tuning**
   - After the main change lands, revisit `NpgsqlDataSourceBuilder` to test multiplexing and `MaxAutoPrepare` settings under load.
   - Consider enabling `CommandTimeout` overrides or statement-level cancellation for long-running queries.

3. **Database-level stored procedures**
   - If `NpgsqlBatch` yields marginal improvement, plan the stored procedure approach and perform side-by-side benchmarks.

---

This plan should reduce WAL flush pressure dramatically, remove redundant session resets, and make future batching features straightforward to implement.
