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
     - `void MarkForRollback()`, `bool ShouldRollback { get; }`
     - `Task CommitAsync()`, `Task RollbackAsync()`
   - Implement lazy connection opening—only hit the pool when a handler actually needs the database.

2. **Implement `DbSession` (scoped lifetime)**
   - Constructor accepts `NpgsqlDataSource`, `IsolationLevel`, `ILogger`.
   - On first call to `BeginTransactionAsync`:
     - Open connection via `_dataSource.OpenConnectionAsync()`
     - `Transaction = await connection.BeginTransactionAsync(_isolationLevel)`
   - `CommitAsync`/`RollbackAsync` should no-op if no transaction was started.
   - Ensure `DisposeAsync`/`Dispose` clean up `Transaction` and `Connection`. Wrap in try/catch and log failures so the middleware can still throw if needed.

3. **Register session in DI**
   - Leave the PostgreSQL repositories registered as singletons so their caches and pipeline wiring stay valid.
   - Expose a scoped `IDbSession` in Core (e.g., `DmsCoreServiceExtensions`) that resolves the singleton `NpgsqlDataSource`, reads the configured isolation level, and creates a new `DbSession` per request scope.
   - Provide an `IDbSessionAccessor` (or use `IHttpContextAccessor` equivalent) if pipeline steps need to resolve the current session after `UnitOfWorkMiddleware` attaches it.
   - Ensure any background singletons (e.g., `DbHealthCheck`) continue to open their own transient connections via `NpgsqlDataSource` and do not consume the request-scoped session.

4. **Add request middleware for lifetime management**
   - Implement `UnitOfWorkMiddleware` as a DMS Core pipeline step (`IPipelineStep` in `src/dms/core/EdFi.DataManagementService.Core/Middleware`) and register it via Core DI so `ApiService` can `GetRequiredService<UnitOfWorkMiddleware>()`.
   - The middleware should:
     1. Resolve `IDbSession` from scoped services.
     2. Attach the session to the current `RequestInfo` instance.
     3. Invoke `await next()` inside a try block.
     4. If `next` completes without exception and a transaction exists, call `CommitAsync`.
     5. On exception, call `RollbackAsync` and rethrow.
     6. Dispose the session once processing completes (the middleware owns the per-request lifetime).
   - Append the middleware to the end of `GetCommonInitialSteps()` in `ApiService` (`src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`) so all pipelines (upsert, query, delete, etc.) execute within the unit of work after authentication/logging. Resolve it from `_serviceProvider` (e.g., `_serviceProvider.GetRequiredService<UnitOfWorkMiddleware>()`) so constructor-injected services are honored. The middleware should inspect `IDbSession.ShouldRollback` after `next()` returns—if true, call `RollbackAsync` instead of `CommitAsync`.
   - Wiring checklist:
     1. **Session factory in backend** — In `PostgresqlServiceExtensions.AddPostgresqlDatastore`, keep every repository as a singleton, and add `services.AddSingleton<IDbSessionFactory>(sp => new DbSessionFactory(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IOptions<DatabaseOptions>>(), sp.GetRequiredService<ILoggerFactory>()));`. The factory encapsulates backend-specific knowledge (connection pool, isolation level, logging) and exposes `Create()` for new sessions.
     2. **Scoped session in core** — In `DmsCoreServiceExtensions`, register `services.AddScoped<IDbSession>(sp => sp.GetRequiredService<IDbSessionFactory>().Create());` so each request scope gets its own `DbSession` while backend singletons still rely on the shared data source.
     3. **Middleware registration** — Also in `DmsCoreServiceExtensions`, add `services.AddTransient<UnitOfWorkMiddleware>();`. Give the middleware a constructor that accepts `IDbSession`, `ILogger<UnitOfWorkMiddleware>`, and any required accessors for updating `RequestInfo`.
     4. **Pipeline usage** — Update `ApiService.GetCommonInitialSteps()` to append the middleware via `_serviceProvider.GetRequiredService<UnitOfWorkMiddleware>()`. Other steps can continue to be constructed with `new` because they have no additional dependencies.
     5. **Middleware execution** — Inside the middleware, once `await next()` returns, inspect `IDbSession.ShouldRollback`; call `RollbackAsync` if true, otherwise `CommitAsync`, then dispose the session before returning.

---

## 2. Refactor Repositories to Use the Session

1. **Flow the session through `RequestInfo`**
   - Extend `RequestInfo` (`src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs`) with an `IDbSession` property (default null).
   - `UnitOfWorkMiddleware` sets this property before invoking `next()` and clears it during disposal so downstream steps always see the current session.
   - Update pipeline handlers (`UpsertHandler`, `UpdateByIdHandler`, `DeleteByIdHandler`, query handler, etc.) to pass `requestInfo.DbSession` to backend interfaces.

2. **PostgresqlDocumentStoreRepository**
   - Keep the repository registered as a singleton; do not inject `IDbSession` directly.
   - Update `IDocumentStoreRepository`, `IQueryHandler`, and companion interfaces so each operation accepts an `IDbSession` parameter supplied by handlers (e.g., `UpsertDocument(IDbSession session, IUpsertRequest request)`, `GetDocumentById(IDbSession session, IGetRequest request)`).
   - Mirror the same signature change in the backing operation interfaces (`IUpsertDocument`, `IUpdateDocumentById`, `IDeleteDocumentById`, `IGetDocumentById`, `IQueryDocument`) so the repository can forward the session.
   - Leave `IAuthorizationRepository` on the current pattern for now; it will be reworked separately.
   - Use `await dbSession.OpenConnectionAsync()` / `await dbSession.BeginTransactionAsync()` within each method, and remove explicit commit/rollback logic—the middleware now owns transaction boundaries.
   - Guard for read-only operations (GET/query) by only starting a transaction when necessary.
   - When returning a failure result (e.g., `UpsertFailureIdentityConflict`, `DeleteFailureReference`), call `dbSession.MarkForRollback()` before returning so the middleware knows to roll back.

3. **Operations (`UpsertDocument`, `DeleteDocumentById`, etc.)**
   - Continue to accept `NpgsqlConnection` / `NpgsqlTransaction` parameters; the repository supplies them via the new session.
   - Retain targeted savepoints when the operation needs to recover from partial failures (e.g., the FK handling in `DeleteDocumentById` still uses `SaveAsync` / `RollbackAsync` to surface referencing resources). Remove only the catch-all commit/rollback logic now owned by the middleware.

4. **`SqlAction` changes**
   - Confirm all methods accept `NpgsqlConnection` / `NpgsqlTransaction`. With session in place, the repository will continue to pass the active objects—no signature changes needed.
   - Verify no helper method tries to dispose the connection/transaction.

5. **Unit tests / integration tests**
   - Update pipeline tests to assert that `UnitOfWorkMiddleware` is appended to `GetCommonInitialSteps()` and that it populates `RequestInfo.DbSession`.
   - Add coverage verifying that failure paths correctly call `MarkForRollback()` and that `UnitOfWorkMiddleware` commits only when `ShouldRollback` is false.
   - Adjust repository tests to supply a fake `IDbSession` (or a wrapper exposing a real connection/transaction).
   - Add middleware coverage that verifies commit-on-success and rollback-on-exception behaviors.

---

## 3. Connection Lifecycle & Middleware Ordering

1. **Ensure middleware order**
   - Register `UnitOfWorkMiddleware` after authentication and correlation ID middleware so request context is available.
   - For endpoints that do not need a transaction (e.g. a health endpoint), allow the middleware to detect “no transaction started” and skip commit/rollback.

2. **Disable Auto Reset**
   - With `NoResetOnClose=true` already applied via `NpgsqlDataSourceBuilder`, validate that the middleware disposes the session at end of request to avoid leaking state.
   - For now, we rely on the fact that the service does not issue per-request `SET` commands.

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
