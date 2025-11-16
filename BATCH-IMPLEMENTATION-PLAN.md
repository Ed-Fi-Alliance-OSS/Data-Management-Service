[X] **1. Public API & Frontend wiring**
      [X] Extend IApiService with ExecuteBatchAsync() and add a VersionedLazy<PipelineProvider> field in ApiService to drive the /batch pipeline, mirroring how the existing _upsertSteps etc. operate.
      [X] Add a static Batch adapter in AspNetCoreFrontend that reuses FromRequest/ToResult, then create BatchEndpointModule that maps POST /batch to that adapter so routing stays consistent with other modules.
      [X] Update DiscoveryEndpointModule to expose urls.batchApi so clients can discover the new surface, and ensure the module only advertises batch when the backend actually supports it (or always advertise if we keep
        the contract unconditional).
[X] **2. Core pipeline refactor & composition**
      [X] Factor the per-operation middleware lists in ApiService into GetUpsertCoreSteps(), GetUpdateCoreSteps(), GetDeleteCoreSteps() so they can be shared between the “full” pipelines and the new validation-only
        pipelines described in BATCH-PER-OPERATION-PROCESSING.md.
      [X] Introduce three VersionedLazy<PipelineProvider> instances for the batch validation-only pipelines that omit GetCommonInitialSteps(), schema loading, and terminal handlers.
      [X] Add CreateBatchPipeline() that chains GetCommonInitialSteps(), ApiSchemaValidationMiddleware, ProvideApiSchemaMiddleware, and the new BatchHandler. _batchSteps.Value.Run() inside ExecuteBatchAsync becomes the
        execution path for the endpoint.
[X] **3. Batch handler, models, and per-operation flow**
      [X] Create new model types (BatchOperation, BatchOperationResult, etc.) plus parsing helpers to read the incoming JSON array, validate op/resource/documentId/naturalKey requirements, and enforce documentId XOR
        naturalKey for update/delete operations.
      [X] Implement BatchHandler : IPipelineStep that:
          [X] Reads the JSON payload, enforces BatchMaxOperations, and returns a 413 response on overflow before touching the database.
          [X] Attempts to resolve an IBatchUnitOfWork via the injected factory; if unavailable, returns the 501 problem payload described in Section 3.4.5.
          [X] For each operation, copies schema/auth context from the batch-level RequestInfo, synthesizes an operation-specific FrontendRequest/RequestInfo, and runs the method-appropriate validation pipeline. Short-
            circuits (validation or auth failure) are mapped into the standardized failedOperation problem response with rollback.
          [X] Handles natural-key resolution through DocumentIdentity + ReferentialIdCalculator + IBatchUnitOfWork.ResolveDocumentUuidAsync, injects id for updates, and checks identity mismatches before entering the
            validation pipeline.
          [X] Builds UpsertRequest / UpdateRequest / DeleteRequest from the populated RequestInfo (including new ResourceAuthorizationHandler, UpdateCascadeHandler, etc.), executes the matching unit-of-work method, and
            interprets *Result outcomes using the same mapping logic as today’s handlers to populate per-operation status or failure metadata.
          [X] Rolls back on the first failure (propagating the underlying HTTP status), logs the failing op index/type at Warning, and commits plus returns the ordered success array when all operations succeed.
          [X] Wraps the unit-of-work execution in the existing _resiliencePipeline so transient backend failures retry the entire batch once, matching the transactional semantics noted in Section 4.3.5.
[X] **4. Backend abstractions and PostgreSQL implementation**
      [X] Add IBatchUnitOfWork and IBatchUnitOfWorkFactory to the external interface assembly so the core can use them without a direct dependency on Npgsql types.
      [X] Implement PostgresqlBatchUnitOfWork that owns a single NpgsqlConnection/NpgsqlTransaction, delegates to the existing IUpsertDocument, IUpdateDocumentById, IDeleteDocumentById, and ISqlAction implementations, and
        supports ResolveDocumentUuidAsync, CommitAsync, RollbackAsync, and DisposeAsync.
      [X] Create PostgresqlBatchUnitOfWorkFactory that opens the connection, starts a transaction at the configured isolation level, and builds the unit of work; register it in PostgresqlServiceExtensions alongside the
        other backend services.
      [X] Ensure the factory isn’t registered for unsupported datastores (MSSQL today) so the handler can detect the absence and emit 501.
[X] **5. Configuration, limits, and observability**
      [X] Extend EdFi.DataManagementService.Core.Configuration.AppSettings with BatchMaxOperations (default 100) and plumb it through IOptions<AppSettings> so BatchHandler can enforce the limit and log the chosen value
        at startup.
      [X] Add the corresponding key to appsettings*.json so operators can tune it, and document recommended request size considerations (10 MB limit today) or hook into existing request-size configuration if needed.
      [X] Update logging inside BatchHandler to emit batch size and per-operation summaries per Section 8.1; consider exposing metric hooks (even if initially no-ops) so we can quickly add counters later.
[ ] **6. Testing strategy**
      [ ] Unit-test the per-operation validation pipelines (e.g., BatchHandlerTests) to cover happy path, size limit, schema errors, authorization failures, natural-key resolution (both success and not-found), immutable
        identity mismatches, and backend result mapping; use fakes for IBatchUnitOfWork, the validation pipelines, and serializers to keep tests fast.
      [ ] Add backend unit tests for PostgresqlBatchUnitOfWork to confirm it reuses the same transaction for multiple calls, propagates commit/rollback, and resolves document UUIDs correctly via ISqlAction.
      [ ] Expand discovery and endpoint registration tests (if present) to assert /batch is mapped.
      [ ] Plan higher-level integration tests (or end-to-end harness) that exercise /batch in a running stack to verify cross-resource sequences, rollback on failure, and identity-lookup flows, as outlined in Section 10.2.
