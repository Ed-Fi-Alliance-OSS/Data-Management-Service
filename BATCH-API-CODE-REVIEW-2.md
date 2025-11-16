# Batch API Code Review (Round 2)

## Findings

1. **Per-operation ETags/`If-Match` values are impossible**  
   - `BatchHandler.CreateOperationRequestInfo` copies the batch HTTP headers verbatim into every synthetic per-operation request (`src/dms/core/EdFi.DataManagementService.Core/Handler/BatchHandler.cs:445-468`). There is no per-operation override, so the entire batch shares a single `If-Match` header.  
   - The backend optimistic-lock helpers only look at the `If-Match` header when deciding whether an update/delete may proceed (`src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/UpdateDocumentById.cs:191-200` and `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/DeleteDocumentById.cs:40-61`).  
   - Result: a batch that contains two updates (or deletes) with different `_etag` values has no way to express that distinction. Either the caller omits `If-Match` (disabling optimistic locking entirely) or one request’s token is reused for all operations, causing the rest to fail with 412. This regresses the concurrency guarantees we already enforce on `/data`.  
   - **Recommendation:** add a per-operation field (e.g., `ifMatch`) and teach `BatchHandler` to populate the `RequestInfo` headers from that field before running the validation pipelines.

2. **`naturalKey` payloads must mirror the full resource schema, contrary to the documented contract**  
   - `TryResolveNaturalKeyIdentity` pipes the caller’s `naturalKey` JSON directly into `IdentityExtractor` (`src/dms/core/EdFi.DataManagementService.Core/Handler/BatchHandler.cs:382-405`). That extractor expects every JSONPath listed in `IdentityJsonPaths` to exist (e.g., `$.studentReference.studentUniqueId`).  
   - The public contract in `BATCH-API-DESIGN.md:186-214` shows flattened natural keys such as `"naturalKey": { "studentUniqueId": "S-123", "schoolId": 255901001 }`. Association resources actually expose their identities through nested reference objects (`studentReference.studentUniqueId`, `schoolReference.schoolId`, etc.), so the documented payload will always trigger the `Invalid naturalKey...` 400 before we even authorize the operation.  
   - **Recommendation:** either support the documented shorthand by mapping simple property names to the corresponding JSON paths before calling `IdentityExtractor`, or update the request schema/docs to demand the full nested shape (and add validation that emits a helpful message when required references are missing).
