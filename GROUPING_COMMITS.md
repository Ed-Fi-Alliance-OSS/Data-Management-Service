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
   - In `PostgresqlServiceExtensions.AddPostgresqlDatastore`:
     - Change repository registrations (`PostgresqlDocumentStoreRepository`, `SqlAction`, `GetDocumentById`, etc.) to scoped.
     - Add `services.AddScoped<IDbSession, DbSession>();`
   - Ensure any background singletons (e.g., `DbHealthCheck`) still create their own transient connections via `NpgsqlDataSource` and do not consume the scoped session.

4. **Add request middleware for lifetime management**
   - Build a middleware (e.g., `UnitOfWorkMiddleware`) that:
     1. Resolves `IDbSession` from scoped services.
     2. Invokes `await next()` inside a try block.
     3. If `next` completes without exception and a transaction exists, call `CommitAsync`.
     4. On exception, call `RollbackAsync` and rethrow.
   - Register middleware early in the pipeline (after authentication but before handler execution) so every route benefits.

---

## 2. Refactor Repositories to Use the Session

1. **PostgresqlDocumentStoreRepository**
   - Inject `IDbSession` instead of `NpgsqlDataSource`.
   - Replace manual `OpenConnectionAsync` / `BeginTransactionAsync` calls with:
     ```csharp
     var connection = await _session.OpenConnectionAsync();
     var transaction = await _session.BeginTransactionAsync();
     ```
   - Remove explicit commit/rollback logic—middleware handles it. Return result values only.
   - Guard for operations that do not need writes (e.g., pure GET): they can avoid calling `BeginTransactionAsync`, keeping the session read-only.

2. **Operations (`UpsertDocument`, `DeleteDocumentById`, etc.)**
   - Ensure they accept an `IDbSession` instance or they use the connection/transaction passed in from repository. If the repository no longer passes a transaction parameter, adjust signatures accordingly.
   - Remove all `await transaction.SaveAsync("beforeDelete")` / `RollbackAsync("beforeDelete")` in `DeleteDocumentById`. The scoped session will ensure that a failure before commit automatically rolls back.

3. **`SqlAction` changes**
   - Confirm all methods accept `NpgsqlConnection` / `NpgsqlTransaction`. With session in place, the repository will continue to pass the active objects—no signature changes needed.
   - Verify no helper method tries to dispose the connection/transaction.

4. **Unit tests / integration tests**
   - Update tests to use scoped session. Where tests previously passed a fake `NpgsqlConnection`, switch to a fake `IDbSession` or create a test implementation that wraps a real connection and transaction.
   - Add coverage for middleware: confirm it commits on success and rolls back on exception.

---

## 3. Connection Lifecycle & Middleware Ordering

1. **Ensure middleware order**
   - Register `UnitOfWorkMiddleware` after authentication and correlation ID middleware so request context is available.
   - For endpoints that do not need a transaction (e.g. a health endpoint), allow the middleware to detect “no transaction started” and skip commit/rollback.

2. **Disable Auto Reset**
   - With `NoResetOnClose=true` already applied via `NpgsqlDataSourceBuilder`, validate that the middleware disposes the session at end of request to avoid leaking state.
   - For now, we rely on the fact that the service does not issue per-request `SET` commands.

---

## 4. Remove Manual Savepoints in Delete Paths

1. **DeleteDocumentById.cs**
   - Remove `await transaction.SaveAsync("beforeDelete")` and `await transaction.RollbackAsync("beforeDelete")`.
   - When catch an FK violation, just rethrow (or return failure). The outer middleware will roll back the entire unit automatically.

2. **Any other savepoint usage**
   - Grep for `.SaveAsync(` to ensure all redundant savepoints are eliminated unless a true nested transaction is required.

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
