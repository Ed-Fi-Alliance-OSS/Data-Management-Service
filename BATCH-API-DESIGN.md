# Design: DMS Batch Operations Endpoint

> This document refines `BATCH-API-INITIAL-DESIGN.md` using the actual
> Data Management Service (DMS) architecture under `src/dms`. It specifies
> how to add a transactional batch endpoint that fits the existing
> frontend → core (middleware pipeline) → backend layering and patterns.

---

## 1. Overview

### 1.1 Problem

The current DMS API is very chatty:

- Each `POST`/`PUT`/`DELETE` call maps to a single database transaction.
- For PostgreSQL, each transaction incurs full WAL write and `fsync`.
- High-volume loads (e.g., ETL, nightly syncs, or batch ingest) generate
  many small transactions, leading to high WAL write waits and reduced
  throughput.

The existing core architecture is already well-factored:

- **Frontend**: `EdFi.DataManagementService.Frontend.AspNetCore`
  - Thin ASP.NET Core minimal API, forwarding to `IApiService`.
  - Uses `AspNetCoreFrontend` to translate HTTP to `FrontendRequest`
    and `IFrontendResponse`.
- **Core**: `EdFi.DataManagementService.Core`
  - `ApiService` orchestrates requests via a middleware pipeline
    (`PipelineProvider` + `RequestInfo`).
  - Per-HTTP-method pipelines (Upsert, Get, Query, UpdateById, DeleteById)
    compose cross-cutting middleware (logging, JWT auth, schema, validation,
    authorization) and then handler steps.
  - Core is backend-agnostic, talking to the DB via `IDocumentStoreRepository`
    and `IQueryHandler`.
- **Backend**: `EdFi.DataManagementService.Backend.Postgresql`
  - `PostgresqlDocumentStoreRepository` implements `IDocumentStoreRepository`
    and `IQueryHandler`.
  - Each operation opens its own connection and transaction and commits or
    rolls back based on the result.

Today, there is no notion of a multi-operation **unit of work**. Each API call
is its own unit.

### 1.2 Proposed Solution

Add a new **batch operations endpoint** that:

- Accepts an ordered array of operations (`create`, `update`, `delete`)
  across arbitrary Ed‑Fi resources.
- Executes all operations in a **single database transaction** for PostgreSQL.
- Reuses as much of the existing core middleware and backend logic as
  possible, so behavior (validation, authorization, ETag semantics, identity
  rules, referential integrity) remains identical to existing single-resource
  endpoints.
- Returns a structured per-operation result array on success.
- On any failure, **rolls back** the entire batch and returns an error
  detailing which operation failed and why.

At a high level:

1. ASP.NET Core frontend exposes `POST /batch`.
2. The frontend forwards the request to a new core entry point on
   `IApiService` (e.g., `ExecuteBatchAsync`).
3. Core runs a lightweight pipeline (logging, exception logging,
   JWT authentication, API schema availability) and hands control to a
   `BatchHandler`.
4. `BatchHandler`:
   - Parses the operations array.
   - Enforces `MAX_BATCH_SIZE`.
   - Opens a **unit-of-work transaction** from the backend.
   - For each operation:
     - Resolves resource and schema.
     - Validates the document with existing `IDocumentValidator`.
     - Extracts identities and security elements from the document.
     - Enforces authorization using the existing authorization subsystem.
     - Calls backend CUD methods that join the same DB transaction.
   - On first failure:
     - Stops processing.
     - Rolls back the DB transaction.
     - Builds a batch-level error response.
   - On success:
     - Commits the transaction.
     - Returns an array of per-operation results.

The primary change is **adding an explicit unit-of-work abstraction in
the backend**, and a new core handler to orchestrate multiple existing
per-document operations inside that unit of work.

---

## 2. Goals and Non-goals

### 2.1 Goals

- **Amortize WAL/fsync cost** by grouping many writes into a single
  PostgreSQL transaction.
- **Maintain semantic equivalence** with current single-resource endpoints:
  - Same JSON schema validation behavior.
  - Same identity rules (including `allowIdentityUpdates` constraints).
  - Same referential integrity behavior.
  - Same ETag/optimistic concurrency semantics.
  - Same authorization and claim-set behavior.
- **Preserve architecture layering**:
  - Frontend remains a thin ASP.NET Core adapter to `IApiService`.
  - Core remains backend-agnostic and uses `IDocumentStoreRepository`-style
    abstractions, plus a new unit-of-work interface.
  - Backend implements the transactional unit-of-work over Npgsql.
- **Support mixed-resource batches**:
  - A single batch can contain multiple resources (`Student`, `Section`,
    `StudentSchoolAssociation`, etc.) in any order.
- **Per-operation error detail**:
  - On failure, return the failing operation index, type, resource,
    and a specific error code / message aligned with existing error semantics.
- **Configurable limits**:
  - Enforce a `MAX_BATCH_SIZE` from configuration.

### 2.2 Non-goals

- No changes to existing `/data/...` endpoints.
- No change to existing external data model or resource schemas.
- No change to existing Kafka / OpenSearch integration. Those continue
  to see individual row-level changes from the database, regardless of
  whether they come from single or batch operations.
- No new partial-update client API. Partial updates remain an internal
  optimization in the PostgreSQL backend (see `PARTIAL-UPDATE-DESIGN.md`).
- No first-class batch read/query endpoint in this iteration.
- No guaranteed **idempotency** beyond what existing endpoints provide
  (ETags, uniqueness constraints, and deterministic referential IDs).

---

## 3. Public API Design (Frontend)

### 3.1 Endpoint and Routing

- **HTTP Method**: `POST`
- **Route**: `POST /batch`
  - Chosen to avoid collision with existing `/data/{**dmsPath}` route in
    `CoreEndpointModule`.
  - Sits alongside `/data`, `/metadata`, `/management`, and `/` (discovery)
    as a “non-resource” RPC endpoint.
- **Authentication**:
  - Same as `/data` endpoints: JWT bearer token in `Authorization` header.
  - Enforced via existing `JwtAuthenticationMiddleware` in core.
- **Authorization**:
  - Authorization is evaluated **per operation** using existing claim-set
    based authorization logic, as if each operation were its own HTTP call.

#### 3.1.1 Frontend Mapping

- Add a new `BatchEndpointModule` in
  `EdFi.DataManagementService.Frontend.AspNetCore/Modules` implementing
  `IEndpointModule`.
- Sample routing:

```csharp
public class BatchEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/batch", HandleBatchAsync)
            .WithName("BatchOperations")
            .WithSummary("Executes multiple create/update/delete operations in a single unit of work.");
    }

    private static async Task<IResult> HandleBatchAsync(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> options // frontend appsettings for trace id header, etc.
    )
    {
        var frontendRequest = AspNetCoreFrontend.FromRequest(
            httpContext.Request,
            dmsPath: "batch", // DMS path is not used by the batch pipeline for routing
            options,
            includeBody: true
        );

        var frontendResponse = await apiService.ExecuteBatchAsync(frontendRequest);
        return AspNetCoreFrontend.ToResult(frontendResponse, httpContext, dmsPath: "batch");
    }
}
```

> Note: `AspNetCoreFrontend.FromRequest` and `ToResult` are reused so that
> batch follows the same trace-id, header, and JSON serialization behavior
> as `/data` endpoints.

### 3.2 Request Schema

The request body is a **JSON array of operation objects**:

```json
[
  {
    "op": "create",
    "resource": "Student",
    "document": { ... }
  },
  {
    "op": "update",
    "resource": "Section",
    "documentId": "a1b2c3d4-e5f6-a7b8-c9d0-e1f2a3b4c5d6",
    "document": { ... }
  },
  {
    "op": "delete",
    "resource": "StudentSchoolAssociation",
    "naturalKey": {
      "studentUniqueId": "S-123",
      "schoolId": 255901001
    }
  }
]
```

#### 3.2.1 Operation object

| Field       | Type    | Required             | Description                                                                 |
|------------|---------|----------------------|-----------------------------------------------------------------------------|
| `op`       | string  | Yes                  | `"create"`, `"update"`, `"delete"` (case-insensitive).                      |
| `resource` | string  | Yes                  | Ed‑Fi resource name, e.g., `"Student"`, `"Section"`, `"StudentSchoolAssociation"`. |
| `document` | object  | `create`/`update`    | Full resource document for create/update (POST/PUT semantics).              |
| `documentId` | string  | `update`/`delete` ∗ | The resource `id`/document UUID. Must not be combined with `naturalKey`.    |
| `naturalKey` | object | `update`/`delete` ∗ | JSON object containing the full natural key (identity fields).              |

∗ For `update`/`delete`, **exactly one** of `documentId` or `naturalKey` must be provided.

#### 3.2.2 Resource Resolution

- `resource` maps to `ResourceSchema.ResourceName.Value`.
- Resolution rules:
  - All `ProjectSchema` instances from `ApiSchemaDocuments.GetAllProjectSchemas()` are searched.
  - `ProjectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resource))` is used.
  - If the resource is found in **exactly one** project:
    - That `ProjectSchema` and `ResourceSchema` are used.
  - If zero or multiple matches:
    - The batch fails with a 400-level error indicating ambiguous or unknown resource.
- First implementation does **not** expose a `project` field in the request.
  - If needed later, a `project` property can constrain the search to a specific project.

#### 3.2.3 Identity Via `naturalKey`

- `naturalKey` is an object whose properties match the resource’s identity properties:
  - For `Student`: e.g., `{ "studentUniqueId": "S-123" }`.
  - For composite identities: all components must be present.
- Internally:
  - Core constructs a `JsonObject` representing only the identity fields.
  - `IdentityExtractor.ExtractDocumentIdentity` is reused with that JSON to build a `DocumentIdentity`.
  - `ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, documentIdentity)` produces a deterministic `ReferentialId`.
  - The backend uses `FindDocumentByReferentialId` (via a new repository abstraction, see Section 5) to resolve a `DocumentUuid`.
- `update`/`delete` operations using `naturalKey` are executed as if they were `PUT`/`DELETE` by `documentId` using that resolved `DocumentUuid`.
- Validation:
  - If `naturalKey` refers to a non-existent document, the operation behaves like a 404 on the corresponding single-resource endpoint.
  - Optionally (and recommended), the identity fields in `document` for `update` must match the `naturalKey` values; mismatches yield a 400 validation error.

#### 3.2.4 Document Semantics

- `create`:
  - `document` is the **insert document** (same as POST `/data/...`).
  - Identity is inferred from the document body.
  - Clients must **not** supply `id` in the document; if present, it will be rejected (same as existing `RejectResourceIdentifierMiddleware` behavior).
- `update`:
  - `document` is a **full replacement** document (PUT semantics).
  - `_etag` is required and must be current; the backend enforces optimistic locking using existing helpers.
  - For `documentId`-based updates:
    - The `documentId` from the operation and any `id` in the document must match.
    - If `id` is omitted, core will inject the `documentId` into the document before validation, to preserve `ValidateMatchingDocumentUuidsMiddleware` semantics.
  - For `naturalKey`-based updates:
    - `naturalKey` is used to resolve the `DocumentUuid`.
    - The resolved `DocumentUuid` is injected into the document `id` field before update processing.
- `delete`:
  - `document` must be null or absent.
  - `documentId` or `naturalKey` identify the document to delete.

### 3.3 Success Response

On success (all operations executed and transaction committed), the endpoint returns:

- **Status**: `200 OK`
- **Body**: array of per-operation results, in the same order as the request.

Shape:

```json
[
  {
    "index": 0,
    "status": "success",
    "op": "create",
    "resource": "Student",
    "documentId": "generated-document-uuid"
  },
  {
    "index": 1,
    "status": "success",
    "op": "update",
    "resource": "Section",
    "documentId": "existing-document-uuid"
  },
  {
    "index": 2,
    "status": "success",
    "op": "delete",
    "resource": "StudentSchoolAssociation",
    "documentId": "deleted-document-uuid"
  }
]
```

Notes:

- `index` is zero-based index into the original operations array.
- For `create`, the `documentId` is the newly generated `DocumentUuid`.
- For `update` and `delete`, `documentId` is the existing `DocumentUuid` that was updated/deleted.
- Additional fields (e.g., `etag` for `update`, `location`) can be added over time if needed.

### 3.4 Error Responses

#### 3.4.1 Batch Too Large

- **Status**: `413 Payload Too Large`
- Trigger:
  - Request body parses as an array with `operations.Length > MAX_BATCH_SIZE`.
  - This check happens **before** opening a database transaction.

Body:

```json
{
  "error": "Batch size limit exceeded.",
  "message": "The number of operations (150) exceeds the maximum allowed (100). Please split the request into smaller batches."
}
```

- `MAX_BATCH_SIZE` is configurable (see Section 7).

#### 3.4.2 Batch Operation Failure (Transactional Rollback)

- **Status**: mirrors the underlying failing operation (e.g., 400, 403, 404, 409, 412)
- Trigger:
  - Any operation fails for reasons that map to 4xx or 409 semantics in the
    existing single-resource endpoints:
    - Validation errors.
    - Natural-key conflicts.
    - Referential integrity violations.
    - ETag mismatch.
    - Not found.
    - Forbidden (authorization failure).
  - The first failing operation causes:
    - Immediate rollback of the entire transaction.
    - No further operations are executed.

Body:

```json
{
  "detail": "Batch operation failed and was rolled back.",
  "type": "urn:ed-fi:api:batch-operation-failed",
  "title": "Batch Operation Failed",
  "status": 400,
  "correlationId": "<trace-id>",
  "validationErrors": {},
  "errors": [],
  "failedOperation": {
    "index": 1,
    "op": "create",
    "resource": "Student",
    "problem": {
      "detail": "Data validation failed. See 'validationErrors' for details.",
      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
      "title": "Data Validation Failed",
      "status": 400,
      "correlationId": "<trace-id>",
      "validationErrors": {
        "$.studentUniqueId": [
          "A student with natural key 'S-JANE-DOE-001' already exists."
        ]
      },
      "errors": []
    }
  }
}
```

Behavior:

- The outer object follows the standard DMS problem-details shape (`detail`, `type`, `title`, `status`, `correlationId`, `validationErrors`, `errors`).
- `failedOperation.index` is the zero-based index of the failing operation in the request array.
- `failedOperation.op` and `failedOperation.resource` echo the request operation.
- `failedOperation.problem` is the exact problem-details JSON that would have been returned if the same operation were invoked as a standalone request:
  - `type`, `title`, `detail`, `status`, `validationErrors`, and `errors` come from the existing handlers and `FailureResponse` helpers.
- The HTTP response status should normally match `failedOperation.problem.status` (e.g., 409 for identity conflicts, 412 for ETag mismatches), preserving consistency with non-batch endpoints.

#### 3.4.3 Authentication Failures

- If JWT validation fails at the top-level pipeline, the request fails **before**
  any batch processing:
  - **Status**: `401 Unauthorized`.
  - Body: existing `JwtAuthenticationMiddleware` problem details structure.

#### 3.4.4 Authorization Failures

- If a specific operation is not authorized:
  - That operation produces a `403`-equivalent result via core authorization logic.
  - The batch is rolled back and the response is the `400`-level batch error with:
    - `failedOperation.httpStatus = 403`.
    - `errorCode` and `message` derived from existing forbidden responses.

#### 3.4.5 Backend Not Supporting Batch Unit-of-Work

- If the configured backend does not implement the new unit-of-work interface:
  - **Status**: `501 Not Implemented`.
  - Body:

```json
{
  "error": "Batch operations not supported for configured backend.",
  "message": "The current document store does not support transactional batch operations."
}
```

This preserves clean separation between core and backend while allowing other
backends (e.g., MSSQL) to opt-in later.

---

## 4. Core Design (DMS Core)

### 4.1 New Core Entry Point

Extend `IApiService` in `EdFi.DataManagementService.Core.External.Interface`:

```csharp
public interface IApiService
{
    // existing methods...

    /// <summary>
    /// Executes a batch of create/update/delete operations as a single unit of work.
    /// </summary>
    Task<IFrontendResponse> ExecuteBatchAsync(FrontendRequest frontendRequest);
}
```

Implementation in `ApiService`:

```csharp
internal class ApiService : IApiService
{
    // existing fields...
    private readonly VersionedLazy<PipelineProvider> _batchSteps;

    public ApiService(/* existing deps */, IServiceProvider serviceProvider, /* ... */)
    {
        // existing initialization...

        _batchSteps = new VersionedLazy<PipelineProvider>(
            CreateBatchPipeline,
            () => _apiSchemaProvider.ReloadId
        );
    }

    public async Task<IFrontendResponse> ExecuteBatchAsync(FrontendRequest frontendRequest)
    {
        var requestInfo = new RequestInfo(frontendRequest, RequestMethod.POST);
        await _batchSteps.Value.Run(requestInfo);
        return requestInfo.FrontendResponse;
    }
}
```

### 4.2 Batch Pipeline Composition

The batch pipeline should:

1. Reuse cross-cutting concerns (`RequestResponseLoggingMiddleware`,
   `CoreExceptionLoggingMiddleware`, `JwtAuthenticationMiddleware`).
2. Ensure API schema is loaded and merged (`ApiSchemaValidationMiddleware`,
   `ProvideApiSchemaMiddleware`).
3. Delegate operation-level work to a dedicated `BatchHandler`.

Proposed `CreateBatchPipeline`:

```csharp
private PipelineProvider CreateBatchPipeline()
{
    var steps = GetCommonInitialSteps(); // logging, exception logging, JWT auth

    steps.AddRange(
        [
            new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
            new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
            new BatchHandler(
                _logger,
                _appSettings,
                _documentValidator,
                _decimalValidator,
                _equalityConstraintValidator,
                _claimSetProvider,
                _authorizationServiceFactory,
                _resiliencePipeline,
                _batchUnitOfWorkFactory // new abstraction, see Section 5
            )
        ]
    );

    return new PipelineProvider(steps);
}
```

> Note: `GetCommonInitialSteps()` already injects `JwtAuthenticationMiddleware`
> from `IServiceProvider` and is reused as-is.

### 4.3 BatchHandler Responsibilities

`BatchHandler` is a new `IPipelineStep` inside core. It orchestrates:

1. **Input parsing and validation**
2. **Batch size enforcement**
3. **Unit-of-work lifetime**
4. **Per-operation pipeline execution**
5. **Error aggregation and response shaping**

#### 4.3.1 Input Parsing

- Read `requestInfo.FrontendRequest.BodyStream`.
- Parse JSON as `JsonArray`.
- Validate:
  - Not null.
  - All elements are objects.
  - Each object has required fields for its `op` type.
- If parsing fails:
  - Return `400 Bad Request` with a generic JSON parsing error.

#### 4.3.2 Batch Size Enforcement

- Retrieve `maxBatchSize` from configuration (see Section 7).
- If `operations.Count > maxBatchSize`:
  - `requestInfo.FrontendResponse = 413` batch-too-large response (Section 3.4.1).
  - Return without opening a transaction.

#### 4.3.3 Unit-of-Work Acquisition

- Use a new core abstraction `IBatchUnitOfWorkFactory` (Section 5) injected into `BatchHandler`.
- Create a unit-of-work:

```csharp
await using var uow = await _batchUnitOfWorkFactory.BeginAsync(
    requestInfo.FrontendRequest.TraceId,
    requestInfo.FrontendRequest.Headers
);
```

- If no factory is registered for the current backend:
  - Fail with `501 Not Implemented`, as described in Section 3.4.5.

#### 4.3.4 Per-operation Processing Overview

For each operation `op` at `index`:

1. **Resolve resource & schemas**:
   - Using `requestInfo.ApiSchemaDocuments.GetAllProjectSchemas()`.
   - For each `ProjectSchema`, call `FindResourceSchemaNodeByResourceName`.
   - If exactly one match:
     - `projectSchema`, `resourceSchema`, `resourceInfo` are constructed.
   - Else fail with a 400-level “unknown or ambiguous resource” error.

2. **Build per-operation RequestInfo (`opRequest`)**:
   - `Method`:
     - `RequestMethod.POST` for `"create"`.
     - `RequestMethod.PUT` for `"update"`.
     - `RequestMethod.DELETE` for `"delete"`.
   - `FrontendRequest`:
     - Shares headers and trace ID with the top-level request.
     - `Path` synthesized as:
       - For create: `/{projectSchema.ProjectEndpointName}/{endpointName}`
       - For update/delete by documentId: `/{projectSchema.ProjectEndpointName}/{endpointName}/{documentId}`
       - For naturalKey-based update/delete: path uses the resolved `DocumentUuid` (below).
     - `Body`/`BodyStream` is a serialized view of `document` for create/update; null for delete.
   - `ApiSchemaDocuments`, `ProjectSchema`, and `ResourceSchema` are set from the resolved schema.

3. **Natural Key Resolution (for `update`/`delete` with `naturalKey`)**:
   - Use `resourceSchema.IdentityJsonPaths` and `naturalKey` to create a `DocumentIdentity`.
   - Compute `ReferentialId` with `ReferentialIdCalculator.ReferentialIdFrom`.
   - Call `uow.ResolveDocumentUuidAsync(resourceInfo, documentIdentity, traceId)`:
     - Internally uses backend’s `FindDocumentByReferentialId`.
   - If no document exists:
     - Treat as a 404 failure for this operation.
   - If found:
     - Synthesize the per-operation path with that `DocumentUuid`.
     - Inject `id` into the document (for update).

4. **Document shape & identity validation**:
   - Set `opRequest.ParsedBody` to `document` for create/update.
   - Use existing helpers in sequence (reused as callable services rather than as full pipeline steps):
     - Date/time coercion:
       - `CoerceDateFormatMiddleware` logic.
       - `CoerceDateTimesMiddleware`.
       - `CoerceFromStringsMiddleware` conditional on `AppSettings.BypassStringTypeCoercion`.
     - JSON schema validation:
       - `IDocumentValidator.Validate(opRequest)` and map errors consistently with `ValidateDocumentMiddleware`.
     - Decimal validation:
       - `ValidateDecimalMiddleware` logic.
     - Duplicate properties & array uniqueness checks:
       - `DuplicatePropertiesMiddleware`.
       - `ReferenceArrayUniquenessValidationMiddleware`.
       - `ArrayUniquenessValidationMiddleware`.
     - Identity consistency:
       - `ValidateMatchingDocumentUuidsMiddleware` semantics:
         - Ensure `document.id` (if present) matches path UUID.
     - Equality constraints:
       - `ValidateEqualityConstraintMiddleware` using `_equalityConstraintValidator`.

   This logic can be implemented by:

   - Either calling the existing middleware types in-process in the same
     order as the standard upsert/update pipeline (by constructing a
     per-operation `PipelineProvider` with a shortened set of steps that
     end in a no-op instead of a handler), or
   - Factoring the validation logic into shared helpers, called directly
     from `BatchHandler`.

   The design goal is **behavioral parity** with the existing pipelines,
   not exact reuse of the pipeline composition mechanism.

5. **Security extraction and authorization**:
   - Use `resourceSchema.ExtractSecurityElements` to populate
     `opRequest.DocumentSecurityElements`.
   - Use `resourceSchema.ExtractAuthorizationSecurableInfo` to populate
     `AuthorizationSecurableInfo`.
   - Use `resourceSchema.AuthorizationPathways` and
     `ProvideAuthorizationPathwayMiddleware` logic to build
     `AuthorizationPathways`.
   - Map `RequestMethod` to action (`Create`, `Read`, `Update`, `Delete`)
     using the same mapping as `ResourceActionAuthorizationMiddleware`.
   - Use the existing claim-set based authorization flow:
     - Retrieve `ClientAuthorizations` from the top-level `RequestInfo`
       (populated by `JwtAuthenticationMiddleware`).
     - Use `IClaimSetProvider` + `IAuthorizationServiceFactory` to evaluate
       resource/action permission and authorization strategies.
     - On authorization failure, treat as a 403 failure for this operation.

6. **Backend call inside unit-of-work**:
   - Construct the appropriate core request object:
     - `UpsertRequest` for creates.
     - `UpdateRequest` for updates.
     - `DeleteRequest` for deletes.
   - For each, instantiate `ResourceAuthorizationHandler` and
     `UpdateCascadeHandler` (for update/insert paths) as the existing
     handlers do.
   - Invoke the corresponding `uow` operation:

     - `uow.UpsertDocumentAsync(upsertRequest)`
     - `uow.UpdateDocumentByIdAsync(updateRequest)`
     - `uow.DeleteDocumentByIdAsync(deleteRequest)`

   - Interpret the resulting `UpsertResult`, `UpdateResult`, or `DeleteResult`
     identically to the existing handlers (`UpsertHandler`, `UpdateByIdHandler`,
     `DeleteByIdHandler`), but:
     - Do **not** immediately commit/rollback.
     - Map the result into an internal `BatchOperationOutcome` with:
       - `IsSuccess`.
       - `HttpStatusCode`.
       - `ErrorCode` / `Message` (for error cases).
       - `DocumentUuid` (for success).

7. **Short-circuit on failure**:
   - On the first failed operation:
     - Call `await uow.RollbackAsync()`.
     - Build the batch-level error response from `BatchOperationOutcome` and
       `index`.
     - Set `requestInfo.FrontendResponse`.
     - Return from `BatchHandler.Execute`.

8. **Commit on success**:
   - After all operations succeed:
     - Call `await uow.CommitAsync()`.
     - Build a `JsonArray` of per-operation success results.
     - Set `requestInfo.FrontendResponse` to `200 OK` with that array.

#### 4.3.5 Resilience and Retry

- The existing core uses a Polly `ResiliencePipeline` around backend calls in
  the standard handlers.
- For batch:
  - A single top-level `ResiliencePipeline.ExecuteAsync` will wrap the entire
    unit-of-work:
    - Any transient backend exception triggers retry for the whole batch
      (consistent with transactional semantics).
  - Backend-level transient errors during individual operations (`Deadlock`,
    `SerializationFailure`) are already mapped into retryable results by the
    backend operations; the unit-of-work layer will either:
    - Delegate these to the resilience pipeline as exceptions, or
    - Map them to operation failures, depending on implementation choices.
- Detailed retry semantics should be kept **conservative** in the first
  iteration (e.g., no automatic per-operation retries inside a batch), to
  avoid unexpected partial effects.

---

## 5. Backend Design (PostgreSQL Unit-of-Work)

### 5.1 New Backend Abstractions

To keep core backend-agnostic while allowing shared transactions, introduce:

```csharp
namespace EdFi.DataManagementService.Core.External.Interface;

public interface IBatchUnitOfWork : IAsyncDisposable
{
    Task<UpsertResult> UpsertDocumentAsync(IUpsertRequest request);
    Task<UpdateResult> UpdateDocumentByIdAsync(IUpdateRequest request);
    Task<DeleteResult> DeleteDocumentByIdAsync(IDeleteRequest request);

    /// <summary>
    /// Resolves a document UUID from its natural key (DocumentIdentity).
    /// Returns null if the document does not exist.
    /// </summary>
    Task<DocumentUuid?> ResolveDocumentUuidAsync(
        ResourceInfo resourceInfo,
        DocumentIdentity identity,
        TraceId traceId
    );

    Task CommitAsync();
    Task RollbackAsync();
}

public interface IBatchUnitOfWorkFactory
{
    Task<IBatchUnitOfWork> BeginAsync(TraceId traceId, IReadOnlyDictionary<string, string> headers);
}
```

- `IBatchUnitOfWork` lives in the core external interface assembly so that
  `ApiService`/`BatchHandler` can depend on it without knowing about Npgsql.

### 5.2 PostgreSQL Implementation

Add a concrete implementation in `EdFi.DataManagementService.Backend.Postgresql`:

```csharp
public sealed class PostgresqlBatchUnitOfWork(
    NpgsqlDataSource dataSource,
    ILogger<PostgresqlBatchUnitOfWork> logger,
    IUpsertDocument upsertDocument,
    IUpdateDocumentById updateDocumentById,
    IDeleteDocumentById deleteDocumentById,
    ISqlAction sqlAction,
    IOptions<DatabaseOptions> databaseOptions
) : IBatchUnitOfWork
{
    private readonly IsolationLevel _isolationLevel = databaseOptions.Value.IsolationLevel;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    // ctor will open connection and begin transaction (async factory method).

    public async Task<UpsertResult> UpsertDocumentAsync(IUpsertRequest request)
        => await upsertDocument.Upsert(request, _connection, _transaction);

    public async Task<UpdateResult> UpdateDocumentByIdAsync(IUpdateRequest request)
        => await updateDocumentById.UpdateById(request, _connection, _transaction);

    public async Task<DeleteResult> DeleteDocumentByIdAsync(IDeleteRequest request)
        => await deleteDocumentById.DeleteById(request, _connection, _transaction);

    public async Task<DocumentUuid?> ResolveDocumentUuidAsync(
        ResourceInfo resourceInfo,
        DocumentIdentity identity,
        TraceId traceId
    )
    {
        var referentialId = ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, identity);
        var partitionKey = PartitionUtility.PartitionKeyFor(referentialId);

        Document? doc = await sqlAction.FindDocumentByReferentialId(
            referentialId,
            partitionKey,
            _connection,
            _transaction,
            traceId
        );

        return doc?.DocumentUuid;
    }

    public Task CommitAsync() => _transaction.CommitAsync();
    public Task RollbackAsync() => _transaction.RollbackAsync();
    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
```

A corresponding `PostgresqlBatchUnitOfWorkFactory` creates the instance by:

1. Opening a connection: `await dataSource.OpenConnectionAsync()`.
2. Starting a transaction with `_isolationLevel`.
3. Constructing `PostgresqlBatchUnitOfWork`.

### 5.3 Registration and Backend Selection

- Register `IBatchUnitOfWorkFactory` in `PostgresqlServiceExtensions` alongside
  `PostgresqlDocumentStoreRepository`:

```csharp
services.AddSingleton<IBatchUnitOfWorkFactory, PostgresqlBatchUnitOfWorkFactory>();
```

- For backends that do **not** support batch:
  - Do **not** register `IBatchUnitOfWorkFactory`.
  - `BatchHandler` detects absence and returns `501 Not Implemented`.

### 5.4 Interaction With Existing Repository

- Existing `PostgresqlDocumentStoreRepository` remains unchanged for
  single-resource endpoints.
- Batch uses the lower-level operations (`IUpsertDocument`,
  `IUpdateDocumentById`, `IDeleteDocumentById`, and `ISqlAction`) directly
  via `IBatchUnitOfWork`, sharing the same connection and transaction across
  all operations.
- This preserves:
  - Existing referential integrity and cascading logic.
  - Existing optimistic locking behavior.
  - Existing education organization hierarchy updates.
  - Existing security document updates.

### 5.5 Transaction Scope and Error Handling

- All operations in a batch share the same:
  - `NpgsqlConnection`.
  - `NpgsqlTransaction`.
- If any operation returns a non-success `*Result`:
  - The core chooses to roll back the transaction.
  - No additional database calls are made in that batch.
- If an exception bubbles out of `IBatchUnitOfWork` calls:
  - `BatchHandler` treats it as a batch-level failure.
  - The resilience pipeline may decide to retry the entire batch.
  - On final failure, `RollbackAsync` is invoked before returning a 500-level
    batch error (with minimal information to avoid leaking internal details).

---

## 6. Identity, Natural Key, and UUID Handling

### 6.1 UUID-Based Operations

- For `update`/`delete` operations specifying `documentId`:
  - Core parses the string into `Guid` and wraps it in `DocumentUuid`.
  - Per-operation path is synthesized as `/{projectEndpointName}/{endpointName}/{documentId}`.
  - For `update`:
    - If the document includes `id`, it must match `documentId`.
    - If `id` is missing, core injects it before validation to maintain
      `ValidateMatchingDocumentUuidsMiddleware` behavior.
  - Backend executes as `UpdateDocumentById` / `DeleteDocumentById` exactly
    as for existing endpoints.

### 6.2 Natural Key-Based Operations

- For `update`/`delete` operations specifying `naturalKey`:
  - Build a `DocumentIdentity` matching the resource’s `IdentityJsonPaths`.
  - Use `ReferentialIdCalculator` to compute `ReferentialId`.
  - Call `IBatchUnitOfWork.ResolveDocumentUuidAsync` to resolve `DocumentUuid`.
  - If not found:
    - Treat as a 404 error for that operation.
  - If found:
    - Carry on as if the client had supplied that `documentId`:
      - Synthesize the path with that `documentId`.
      - Inject `id` into document for `update`.
      - Call backend `Update`/`Delete` via `IBatchUnitOfWork`.

### 6.3 Consistency Between `naturalKey` and Document

- For `update` with both `document` and `naturalKey`:
  - Recommended validation:
    - Extract `DocumentIdentity` from `document` using `IdentityExtractor`.
    - Compare to the identity from `naturalKey`.
    - If they differ, return a 400 validation error indicating mismatched
      identity values.
- This prevents confusing scenarios where `naturalKey` resolves one document
  but the document’s identity describes another.

---

## 7. Configuration and Limits

### 7.1 Core AppSettings

Extend `EdFi.DataManagementService.Core.Configuration.AppSettings` with:

```csharp
public class AppSettings
{
    // existing properties...

    /// <summary>
    /// Maximum number of operations allowed in a single batch request.
    /// </summary>
    public int BatchMaxOperations { get; set; } = 100;
}
```

- `BatchMaxOperations` is read by `BatchHandler` from `_appSettings.Value`.
- Default of 100, subject to tuning.

### 7.2 Frontend Request Size

- Current frontend configuration in `Program.cs` limits:
  - `FormOptions.ValueLengthLimit` and `MultipartBodyLengthLimit` to 10MB.
  - `KestrelServerOptions.Limits.MaxRequestBodySize` to 10MB.
- For batch:
  - Initial implementation can reuse the 10MB limit.
  - If batch performance testing shows the need for larger batches, we can:
    - Increase `MaxRequestBodySize` globally, or
    - Introduce a separate configuration for `/batch` requests.

### 7.3 Backend Isolation Level

- Batch uses the existing `DatabaseOptions.IsolationLevel`:
  - Defaults will be whatever is configured for other write operations.
  - For large batches, a serialized isolation level may reduce anomalies but
    increase contention; operational tuning can adjust as needed.

---

## 8. Observability and Metrics

To ensure we can validate performance benefits and detect issues, batch should
be observable.

### 8.1 Logging

- `RequestResponseLoggingMiddleware` remains at the top of the batch pipeline:
  - Logs request/response status codes and trace IDs.
- `BatchHandler` should:
  - Log batch size and per-operation execution summary at `Information` level.
  - Log the index and type of the first failing operation at `Warning` level.
  - Avoid logging full documents by default; respect `MaskRequestBodyInLogs`.

### 8.2 Metrics (Conceptual)

Add counters/timers (actual implementation depends on the existing telemetry
stack):

- `dms.batch.requests` (counter)
- `dms.batch.operations` (counter)
- `dms.batch.batch_size` (distribution)
- `dms.batch.duration_ms` (histogram)
- `dms.batch.errors` (counter, tagged by `errorCode`)

These can be emitted either via the existing logging-to-metrics bridge or
via direct metric exporters if configured.

---

## 9. OpenAPI and Discovery

### 9.1 Discovery Endpoint

The existing `DiscoveryEndpointModule` returns:

- `"dataManagementApi": "{rootUrl}/data"`

To advertise the batch endpoint:

- Add `"batchApi": "{rootUrl}/batch"` to the `urls` object in the discovery response.

### 9.2 OpenAPI Metadata

The current OpenAPI generation is tied to resource schemas; the batch endpoint
is **not** derived from those schemas.

Initial approach:

- Keep batch endpoint **out of** the auto-generated resource OpenAPI document.
- Optionally provide a separate static OpenAPI fragment for `/batch` that can
  be documented in the product documentation but not necessarily integrated
  into the existing OpenAPI metadata endpoints.

Future refinement:

- Integrate batch endpoint into OpenAPI metadata as a separate tag, using
  a static specification merged into the existing OpenAPI document.

---

## 10. Testing Strategy

### 10.1 Unit Tests

- Core:
  - `BatchHandler` tests covering:
    - Empty batch.
    - Batch size limit exceeded.
    - Successful mixed-operation batch.
    - Failure on create/update/delete with rollback.
    - `documentId` vs `naturalKey` resolution behavior.
    - Authorization failures.
    - ETag mismatches.
  - Natural key ↔ document identity consistency checks.
  - Behavior when `IBatchUnitOfWorkFactory` is not registered.
- Backend:
  - `PostgresqlBatchUnitOfWork` tests:
    - Multiple upserts in a single transaction commit as a whole.
    - Single operation failure leads to rollback when directed by core.
    - `ResolveDocumentUuidAsync` correctly finds/not-finds documents.

### 10.2 Integration Tests

- End-to-end tests in `EdFi.DataManagementService.Tests.E2E`:
  - Start dms-local stack and exercise `/batch` endpoint using realistic
    data models.
  - Validate:
    - That claim-set based authorization behaves identically to single-call
      endpoints.
  - Example scenarios:
    - Create a `Student`, then create a `StudentSchoolAssociation` that
      references that student via natural key, in the same batch.
    - Attempt to delete a `School` that has dependent entities; expect a
      409-style reference error for the batch.

### 10.3 Backwards Compatibility

- No changes to existing `/data` routes or handlers.
- Batch endpoint is additive and can be gated by:
  - Presence/absence of `IBatchUnitOfWorkFactory`.

---

## 11. Summary of Key Design Choices

- **Endpoint**: `POST /batch`, JSON array of operations.
- **Atomicity**: All operations in a batch run inside a single PostgreSQL
  transaction via `IBatchUnitOfWork`. Any failure rolls back the entire batch.
- **Core re-use**: The batch implementation reuses existing schema, validation,
  authorization, and backend logic as much as possible, favoring behavioral
  parity over code duplication.
- **Natural key support**: `update`/`delete` operations can target resources
  by `documentId` or by `naturalKey`; the latter is resolved to a `DocumentUuid`
  using existing referential ID mechanisms.
- **Configurable limits and observability**: Batch size is configurable, and
  logging/metrics are designed to validate performance gains and detect errors.

This design gives DMS a high-throughput, transactional batch endpoint that is
consistent with the existing architecture and behavior, and is focused on
solving the WAL write wait bottleneck for PostgreSQL-backed deployments.
