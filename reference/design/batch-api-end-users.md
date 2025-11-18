# Batch Operations Endpoint – Guidance for DMS API Consumers

## Audience and Scope

This document is written for technical users of the DMS API (client developers, integrators, ETL authors) who already use the `/data` resource endpoints and want to understand:

- What the `/batch` endpoint does and how to call it.
- When to use `/batch` instead of individual resource requests.
- The benefits and drawbacks of batching, including performance trade‑offs.
- How the DMS architecture maps each operation in a batch to the same behavior as the normal POST/PUT/DELETE endpoints.

---

## Concept: Requests vs Operations

The batch endpoint introduces a new distinction:

- **Request** – One HTTP call: `POST /batch` with a JSON array body.
- **Operation** – One logical create/update/delete of a single resource, equivalent to one call to a normal `/data/{project}/{resource}` endpoint.

In the traditional API:

- **1 HTTP request = 1 operation** (e.g., one POST or one PUT).

With `/batch`:

- **1 HTTP request = N operations**, where N is the length of the operations array.

Example:

```json
POST /batch
[
  { "op": "create", "resource": "students", "document": { ... } },
  { "op": "update", "resource": "sections", "documentId": "…", "document": { ... } },
  { "op": "delete", "resource": "studentSchoolAssociations", "naturalKey": { ... } }
]
```

This is conceptually the same as three individual POST/PUT/DELETE calls to `/data`, but executed in a single request and database transaction.

---

## When to Use `/batch` vs Individual Endpoints

### Good Use Cases for `/batch`

Use `/batch` when you:

- Perform **high‑volume or repeated bulk loads**, such as:
  - Initial loads and nightly ETL jobs.
  - Synchronizations from SIS/LMS or downstream data warehouses.
- Need **atomicity** across a set of operations:
  - “All of these related changes must succeed or none should be committed.”
- Want to **increase throughput and reduce HTTP/DB overhead**:
  - The server can amortize transaction setup and WAL/fsync costs across many operations.

### Prefer Individual Endpoints When

Use normal `/data` endpoints (single POST/PUT/DELETE) when you:

- Are building **interactive or low‑volume** scenarios (UI calls, ad‑hoc changes).
- Need operations to be **independently committed**:
  - If operation 5 fails, you still want 1–4 committed.
- Want the simplest possible error handling:
  - One HTTP request maps directly to one resource operation and one response.

As a rule of thumb:

- **Bulk load / sync → `/batch`**.
- **Interactive / one‑off changes → `/data`**.

---

## Endpoint Overview

- **HTTP method:** `POST`
- **Route:** `/batch`
- **Authentication & authorization:** Same as `/data`:
  - JWT bearer token in `Authorization` header.
  - Claim‑set based authorization evaluated per operation.
- **Transaction behavior:**
  - All operations in a batch run in a **single database transaction**.
  - If any operation fails, **the entire batch is rolled back**.

### Request Shape

The body is a JSON array of **operation objects**. Each operation mirrors a single POST/PUT/DELETE call to `/data`.

Core fields:

| Field        | Type   | Required                      | Notes                                                                 |
|-------------|--------|-------------------------------|-----------------------------------------------------------------------|
| `op`        | string | Yes                           | `"create"` (POST), `"update"` (PUT), `"delete"` (DELETE).             |
| `resource`  | string | Yes                           | Endpoint segment, e.g., `"students"`, `"sections"`.                   |
| `document`  | object | `create`/`update`             | The same JSON payload you send to the corresponding `/data` endpoint. |
| `documentId`| string | `update`/`delete` (optional)  | Document GUID, same as the `{id}` in `/data/.../{id}` URLs.          |
| `naturalKey`| object | `update`/`delete` (optional)  | Identity fields for the resource (see below).                         |
| `ifMatch`   | string | Optional on `update`/`delete` | Per‑operation ETag (optimistic concurrency) override.                 |

For `update` and `delete`:

- You must provide **either** `documentId` **or** `naturalKey`, but not both.

### Single Endpoint for Any Resource

`/batch` is a **single endpoint for all resources**:

- You can mix any Ed‑Fi resources in one batch:
  - `students`, `sections`, `studentSchoolAssociations`, descriptors, etc.
- You can mix operation types in any order:
  - Any combination of logical POST/PUT/DELETE operations.

Each operation carries the `resource` name and payload. No resource‑specific batch endpoints are required.

---

## Identity Options: `documentId` vs `naturalKey`

For `update` and `delete`, you can identify the target document in two ways:

1. **`documentId` (GUID)** – the system‑assigned `id`:
   - Same value you’d put into the `/data/.../{id}` URL.
   - Best choice when you already have the GUID from a prior read.

2. **`naturalKey` (domain identity)** – the business identity fields:
   - Example for `Student`: `{ "studentUniqueId": "S-123" }`.
   - Example for composite identities: include all key parts.
   - For nested identities, you can use either:
     - Full nested JSON, or
     - A flattened form using the leaf property names, which DMS normalizes.

For many clients, **natural keys are easier to obtain and store** than internal GUIDs. The batch endpoint lets you:

- Issue **update/delete operations by natural key**, without first querying for the `id`.
- Maintain consistency: for updates, the natural key must match the identity inside the `document` when identity updates are not allowed.

Internally, the batch handler resolves natural keys to document IDs using the same identity and referential ID logic the single‑resource API already uses.

---

## Request and Response Behavior

### Success (200)

On success, the response is a JSON array of per‑operation results:

```json
[
  {
    "index": 0,
    "status": "success",
    "op": "create",
    "resource": "students",
    "documentId": "f9c1a9e3-3c77-4ae0-9b5e-7f9a4e28f1b2"
  },
  {
    "index": 1,
    "status": "success",
    "op": "update",
    "resource": "sections",
    "documentId": "4ea5b45a-…"
  }
]
```

Key points:

- `index` matches the zero‑based position of the operation in the request array.
- `documentId` is the final document GUID (e.g., for newly created resources).
- Each successful operation behaves as if you had called the corresponding `/data` endpoint directly.

### Failure (4xx/5xx)

If **any operation fails**, DMS:

- Stops processing further operations.
- Rolls back the entire transaction.
- Returns a structured error describing:
  - The failing operation (`index`, `op`, `resource`).
  - The same **problem details** you’d get from the single `/data` endpoint call.

Example shape (simplified):

```json
{
  "detail": "Batch operation failed and was rolled back.",
  "type": "urn:ed-fi:api:batch-operation-failed",
  "title": "Batch Operation Failed",
  "status": 409,
  "correlationId": "…",
  "failedOperation": {
    "index": 3,
    "op": "update",
    "resource": "students",
    "problem": {
      "type": "urn:ed-fi:api:data-conflict:write-conflict",
      "title": "Write Conflict",
      "status": 409,
      "detail": "The item could not be modified because of a write conflict. Retry the request.",
      "correlationId": "…"
    }
  }
}
```

This design makes error handling predictable: the nested `problem` object is the same structure you already use today.

### Optimistic Concurrency (`If-Match`)

- You can still use ETags for concurrency control.
- The top‑level `If-Match` header applies to all operations by default.
- Each operation can supply its own `ifMatch` value to override the header, allowing multiple updates with different ETags in one batch.

---

## Performance Characteristics

Because `/batch` decouples **requests** from **operations**, throughput is better expressed in operations per second, not just requests per second.

On our test platform:

- Traditional single‑operation endpoints:
  - **~2200 requests/sec**.
  - Each request performs **one operation**, so **~2200 operations/sec**.
- Batch endpoint:
  - **~100 requests/sec**.
  - Each request carries **~90 operations**.
  - Effective throughput: **~9000 operations/sec**.

This illustrates two key points:

- **Higher work throughput:** ~4x more operations per second.
- **Fewer HTTP calls:** ~22x fewer HTTP requests for the same work.

The main reasons are:

- Fewer round‑trips over the network.
- Fewer connections and transactions to create at the DB layer.
- Write‑ahead logging (WAL) and `fsync` costs are amortized across many operations in one transaction.

### Batch Size Limits

- The server enforces a configurable limit, `BatchMaxOperations` (default: **100** operations per request).
- If you exceed the limit, the server returns HTTP 413 with a message instructing you to split the work into smaller batches.

Recommended practice:

- Start with batch sizes in the **50–100** range.
- Measure end‑to‑end latency and throughput in your environment.
- Adjust up or down based on database capacity and workload characteristics.

---

## Benefits and Drawbacks of Batching

### Benefits

- **Throughput:** Much higher operations‑per‑second for bulk loads.
- **Efficiency:** Fewer HTTP calls, connections, and transactions.
- **Atomicity:** All operations in a batch succeed or none do.
- **Consistency:** Validation, authorization, and concurrency rules are identical to `/data`.
- **Flexibility:** Any mix of resources and operation types in one request.
- **Usability:** Ability to target updates/deletes by natural key, which clients often already have.

### Drawbacks and Trade‑offs

- **All‑or‑nothing:** If one operation fails, the entire batch is rolled back:
  - Good for consistency, but not ideal if you want partial success.
- **Longer per‑request latency:** A single `/batch` request may take longer than a single POST/PUT/DELETE, since it does more work.
- **Error handling complexity:** Clients must inspect the `failedOperation` node and index when handling errors.
- **Locking and contention:** Very large batches can hold locks longer and may increase contention if many clients run large batches concurrently.

When in doubt, start with modest batch sizes and monitor performance and error patterns.

---

## How the DMS Design Enables Easy Batching

The batch implementation builds directly on the existing DMS layering and pipeline design, which makes it straightforward to add batching without changing resource semantics.

### Frontend: One Endpoint, Shared Mapping

- ASP.NET Core exposes a single `POST /batch` endpoint via `BatchEndpointModule`.
- That module forwards requests into the existing `AspNetCoreFrontend` helper:
  - The helper constructs a `FrontendRequest` (path, headers, body, trace ID).
  - It calls `IApiService.ExecuteBatchAsync`, just as `/data` calls other `IApiService` entry points.
- As a result, `/batch` shares the same:
  - JSON serialization.
  - Trace‑ID handling.
  - Header, logging, and error handling conventions.

### Core: Reusing the Pipeline per Operation

Inside the core:

- `ApiService` defines a **batch pipeline** that:
  - Reuses common middleware for logging, exception logging, JWT authentication, and schema provision.
  - Invokes a dedicated `BatchHandler` to process the operations.
- `BatchHandler`:
  - Parses the operations array and enforces the `BatchMaxOperations` limit.
  - For each operation:
    - Resolves the resource schema using the same API schema provider used by `/data`.
    - Constructs an internal `RequestInfo` that looks like a normal single‑resource request with a synthesized path (e.g., `/ed-fi/students/{id}`).
    - Runs that request through **validation‑only pipelines** that reuse the same middleware sequence as the standard POST/PUT/DELETE pipelines (schema validation, type coercion, identity checks, security extraction, authorization, etc.).
  - After validation, it builds the appropriate backend request (upsert/update/delete) and delegates to the batch unit of work.

This means every operation in a batch:

- Is validated and authorized **exactly as if** it came from the individual `/data` endpoint.
- Produces the same problem‑details shapes on failure.

### Backend: Shared Transaction via `IBatchUnitOfWork`

The batch pipeline uses a backend abstraction:

- `IBatchUnitOfWork` provides:
  - `UpsertDocumentAsync`, `UpdateDocumentByIdAsync`, `DeleteDocumentByIdAsync`.
  - `ResolveDocumentUuidAsync` for looking up document IDs by referential identity (used for `naturalKey`), all within a single connection/transaction.
  - `CommitAsync` and `RollbackAsync` to determine the final outcome.

For PostgreSQL:

- `PostgresqlBatchUnitOfWork` implements `IBatchUnitOfWork` using:
  - A single `NpgsqlConnection` and `NpgsqlTransaction` for all operations.
  - The same `IUpsertDocument`, `IUpdateDocumentById`, and `IDeleteDocumentById` implementations used by the existing single‑request paths.

Because the core already separated:

- Frontend HTTP concerns.
- Core validation/authorization pipelines.
- Backend persistence operations.

…the batch feature could be implemented as:

- A new endpoint and handler that **compose existing pieces**, rather than re‑implementing business logic.

For API consumers, this is why:

- The batch API uses the same payloads and validation rules as `/data`.
- The responses and error shapes are familiar.
- You gain performance and atomicity benefits without relearning resource semantics.

---

## Practical Adoption Tips

- Start by batching simple, homogeneous operations:
  - For example, multiple `create` operations for a single resource.
- Then expand to mixed operations:
  - Combine `create` + `update` + `delete` where atomicity is important.
- Prefer `naturalKey` for updates/deletes when:
  - Your upstream system naturally works with business identifiers rather than GUIDs.
- Log and monitor:
  - Batch sizes, latencies, and any `Batch Operation Failed` errors, using the `index` field to locate problematic items.

Used this way, `/batch` gives you a higher‑throughput, easier‑to‑operate way to perform the same logical work you already do with the DMS `/data` endpoints, while preserving the same validation, authorization, and data semantics.

