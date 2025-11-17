High‑level goals for 4.3.4

- For each operation in the batch, we want:
  - The same sequence of validation / coercion / authorization steps as today.
  - To avoid re-running logging/JWT/schema load per operation (those belong at the batch level).
  - To not call the existing terminal handlers (UpsertHandler, UpdateByIdHandler, DeleteByIdHandler), because they go through IDocumentStoreRepository which opens its own transaction.
  - To end up with a RequestInfo that looks exactly like it would at the point where the existing handler is called.
- Then BatchHandler can:
  - Check whether any step short-circuited with a response.
  - If not, synthesize UpsertRequest / UpdateRequest / DeleteRequest and call IBatchUnitOfWork.

So think of each batch operation as a virtual single-resource request that runs only the “pre” half of the normal pipeline.

———

### Sub‑pipelines per operation (max reuse, minimal refactor)

This is the simplest way to implement what you described in 4.3.4 without touching most middleware classes.

Idea

- Factor the pipeline configuration in ApiService into:
  - “Full” pipelines: what we have today, ending in Upsert/Update/Delete handlers.
  - “Validation-only” pipelines: same steps as today minus:
    - GetCommonInitialSteps() (logging + exception logging + JWT).
    - ApiSchemaValidationMiddleware / ProvideApiSchemaMiddleware (already run at batch level).
    - The terminal handler.
- BatchHandler then:
  - Creates a fresh RequestInfo per operation (call it opInfo).
  - Copies shared batch-level state into it:
    - ClientAuthorizations (from JWT middleware run once at the batch level).
    - ApiSchemaDocuments and ApiSchemaReloadId (from ProvideApiSchemaMiddleware).
  - Synthesizes a path like /ed-fi/students/{documentId} or /ed-fi/students based on op.
  - Sets Method (POST/PUT/DELETE).
  - Runs the appropriate validation-only pipeline:
    - POST → upsertValidationPipeline.
    - PUT → updateValidationPipeline.
    - DELETE → deleteValidationPipeline.
  - When Run returns:
    - If opInfo.FrontendResponse != No.FrontendResponse, treat that as per-operation error (400/403/404/etc).
    - Otherwise, build the backend request and call the batch unit of work.

What changes concretely (conceptually)

In ApiService:

- Extract the “core” step lists into helpers:

private List<IPipelineStep> GetUpsertCoreSteps() =>
[
new ParsePathMiddleware(_logger),
new ParseBodyMiddleware(_logger),
new RequestInfoBodyLoggingMiddleware(_logger, _appSettings.Value.MaskRequestBodyInLogs),
new DuplicatePropertiesMiddleware(_logger),
new ValidateEndpointMiddleware(_logger),
new RejectResourceIdentifierMiddleware(_logger),
new CoerceDateFormatMiddleware(_logger),
new CoerceDateTimesMiddleware(_logger),
// ... CoerceFromStrings
new ValidateDocumentMiddleware(_logger, _documentValidator),
new ValidateDecimalMiddleware(_logger, _decimalValidator),
new ExtractDocumentSecurityElementsMiddleware(_logger),
new ValidateEqualityConstraintMiddleware(_logger, _equalityConstraintValidator),
new ProvideEducationOrganizationHierarchyMiddleware(_logger),
new ProvideAuthorizationSecurableInfoMiddleware(_logger),
new BuildResourceInfoMiddleware(_logger, overrides),
new ExtractDocumentInfoMiddleware(_logger),
new ReferenceArrayUniquenessValidationMiddleware(_logger),
new ArrayUniquenessValidationMiddleware(_logger),
new InjectVersionMetadataToEdFiDocumentMiddleware(_logger),
new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
new ProvideAuthorizationPathwayMiddleware(_logger),
];

- Then build pipelines from that:

private PipelineProvider CreateUpsertPipeline()
{
var steps = GetCommonInitialSteps();
steps.Add(new ApiSchemaValidationMiddleware(...));
steps.Add(new ProvideApiSchemaMiddleware(...));
steps.AddRange(GetUpsertCoreSteps());
steps.Add(new UpsertHandler(...));
return new PipelineProvider(steps);
}

private PipelineProvider CreateUpsertValidationPipelineForBatch()
{
// Batch pipeline already ran common + schema steps
var steps = GetUpsertCoreSteps();
return new PipelineProvider(steps);
}

Similarly for update/delete core steps (pulling everything before the handler into a GetUpdateCoreSteps() and GetDeleteCoreSteps()).

In BatchHandler:

- Keep a VersionedLazy<PipelineProvider> (like you do elsewhere) for each validation pipeline:

readonly VersionedLazy<PipelineProvider> _batchUpsertValidation;
readonly VersionedLazy<PipelineProvider> _batchUpdateValidation;
readonly VersionedLazy<PipelineProvider> _batchDeleteValidation;

- For each operation:

var opInfo = new RequestInfo(frontendReq, method)
{
ClientAuthorizations = batchRequestInfo.ClientAuthorizations,
ApiSchemaDocuments = batchRequestInfo.ApiSchemaDocuments,
ApiSchemaReloadId = batchRequestInfo.ApiSchemaReloadId,
};

await _batchUpsertValidation.Value.Run(opInfo);

if (opInfo.FrontendResponse != No.FrontendResponse)
{
// map to batch failure
}
else
{
// build UpsertRequest from opInfo and call uow.UpsertDocumentAsync(...)
}

Why this is elegant

- Maximum reuse:
  - Every rule, log message, and edge case in middleware stays in exactly one place.
  - Changing resource validation/authorization behavior automatically applies to both single-resource and batch.
- Minimal churn:
  - You only touch ApiService (pipeline composition) and add BatchHandler; you do not need to rewrite middlewares.
- Pre/post separation:
  - “Pre” work (coercion, validation, auth) runs inside the validation pipelines.
  - “Post” work (building the final HTTP response, logging the HTTP status, etc.) happens at:
    - Top level: RequestResponseLoggingMiddleware, CoreExceptionLoggingMiddleware, and the batch response builder in BatchHandler.
    - You deliberately leave per-operation HTTP logging out to avoid log spam.

———


### How the middleware pre/post phases play in

With sub-pipelines:

- Batch request pipeline:
  - Pre: logging, exception logging, JWT, schema validation.
  - Post: final batch response logging in RequestResponseLoggingMiddleware.
- Per-operation pipelines:
  - Pre: all the resource-specific middlewares (parse path/body, coercion, validation, authorization). They run exactly once per operation in the same order as today.
  - Post: nothing; the “response” is handled by BatchHandler after it sees whether the pipeline short-circuited and what the backend result was.

So you’re respecting the middleware pattern but using it at two levels:

1. HTTP-level pipeline (one per batch).
2. Operation-level pipelines (one per operation, validation-only).

This keeps things elegant, maximizes reuse, and avoids restructuring individual middleware classes.

———

• Here’s how I’d do it in concrete terms, using what you already have.

  ———

  1. Core step helpers in ApiService

  We factor just the per-operation steps into helpers, keeping:

  - Logging / exception / JWT (GetCommonInitialSteps) at the outer (HTTP/batch) level.
  - Schema load (ApiSchemaValidationMiddleware, ProvideApiSchemaMiddleware) at the outer level.
  - Handlers (Upsert/Update/Delete) out of the “core” lists.

  Signatures:

  private List<IPipelineStep> GetUpsertCoreSteps();
  private List<IPipelineStep> GetUpdateCoreSteps();
  private List<IPipelineStep> GetDeleteCoreSteps();

  Concrete contents (mirroring your existing pipelines):

  private List<IPipelineStep> GetUpsertCoreSteps()
  {
      var steps = new List<IPipelineStep>
      {
          new ParsePathMiddleware(_logger),
          new ParseBodyMiddleware(_logger),
          new RequestInfoBodyLoggingMiddleware(_logger, _appSettings.Value.MaskRequestBodyInLogs),
          new DuplicatePropertiesMiddleware(_logger),
          new ValidateEndpointMiddleware(_logger),
          new RejectResourceIdentifierMiddleware(_logger),
          new CoerceDateFormatMiddleware(_logger),
          new CoerceDateTimesMiddleware(_logger),
      };

      // CoerceFromStringsMiddleware should be immediately before ValidateDocumentMiddleware
      if (!_appSettings.Value.BypassStringTypeCoercion)
      {
          steps.Add(new CoerceFromStringsMiddleware(_logger));
      }

      steps.AddRange(
      [
          new ValidateDocumentMiddleware(_logger, _documentValidator),
          new ValidateDecimalMiddleware(_logger, _decimalValidator),
          new ExtractDocumentSecurityElementsMiddleware(_logger),
          new ValidateEqualityConstraintMiddleware(_logger, _equalityConstraintValidator),
          new ProvideEducationOrganizationHierarchyMiddleware(_logger),
          new ProvideAuthorizationSecurableInfoMiddleware(_logger),
          new BuildResourceInfoMiddleware(
              _logger,
              _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
          ),
          new ExtractDocumentInfoMiddleware(_logger),
          new ReferenceArrayUniquenessValidationMiddleware(_logger),
          new ArrayUniquenessValidationMiddleware(_logger),
          new InjectVersionMetadataToEdFiDocumentMiddleware(_logger),
          new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
          new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
          new ProvideAuthorizationPathwayMiddleware(_logger),
      ]);

      return steps;
  }

  private List<IPipelineStep> GetUpdateCoreSteps()
  {
      var steps = new List<IPipelineStep>
      {
          new ParsePathMiddleware(_logger),
          new ParseBodyMiddleware(_logger),
          new RequestInfoBodyLoggingMiddleware(_logger, _appSettings.Value.MaskRequestBodyInLogs),
          new DuplicatePropertiesMiddleware(_logger),
          new ValidateEndpointMiddleware(_logger),
          new CoerceDateFormatMiddleware(_logger),
          new CoerceDateTimesMiddleware(_logger),
      };

      if (!_appSettings.Value.BypassStringTypeCoercion)
      {
          steps.Add(new CoerceFromStringsMiddleware(_logger));
      }

      steps.AddRange(
      [
          new ValidateDocumentMiddleware(_logger, _documentValidator),
          new ValidateDecimalMiddleware(_logger, _decimalValidator),
          new ExtractDocumentSecurityElementsMiddleware(_logger),
          new ValidateMatchingDocumentUuidsMiddleware(_logger, _matchingDocumentUuidsValidator),
          new ValidateEqualityConstraintMiddleware(_logger, _equalityConstraintValidator),
          new ProvideEducationOrganizationHierarchyMiddleware(_logger),
          new ProvideAuthorizationSecurableInfoMiddleware(_logger),
          new BuildResourceInfoMiddleware(
              _logger,
              _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
          ),
          new ExtractDocumentInfoMiddleware(_logger),
          new ReferenceArrayUniquenessValidationMiddleware(_logger),
          new ArrayUniquenessValidationMiddleware(_logger),
          new InjectVersionMetadataToEdFiDocumentMiddleware(_logger),
          new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
          new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
          new ProvideAuthorizationPathwayMiddleware(_logger),
      ]);

      return steps;
  }

  private List<IPipelineStep> GetDeleteCoreSteps()
  {
      return
      [
          new ParsePathMiddleware(_logger),
          new ValidateEndpointMiddleware(_logger),
          new BuildResourceInfoMiddleware(
              _logger,
              _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
          ),
          new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
          new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
          new ProvideAuthorizationPathwayMiddleware(_logger),
          new ProvideAuthorizationSecurableInfoMiddleware(_logger),
      ];
  }

  Then the existing pipelines become:

  private PipelineProvider CreateUpsertPipeline()
  {
      var steps = GetCommonInitialSteps();
      steps.Add(new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger));
      steps.Add(new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache));
      steps.AddRange(GetUpsertCoreSteps());
      steps.Add(new UpsertHandler(...));
      return new PipelineProvider(steps);
  }

  private PipelineProvider CreateUpdatePipeline()
  {
      var steps = GetCommonInitialSteps();
      steps.Add(new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger));
      steps.Add(new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache));
      steps.AddRange(GetUpdateCoreSteps());
      steps.Add(new UpdateByIdHandler(...));
      return new PipelineProvider(steps);
  }

  private PipelineProvider CreateDeleteByIdPipeline()
  {
      var steps = GetCommonInitialSteps();
      steps.Add(new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger));
      steps.Add(new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache));
      steps.AddRange(GetDeleteCoreSteps());
      steps.Add(new DeleteByIdHandler(...));
      return new PipelineProvider(steps);
  }

  For batch we define validation-only variants that just use the core step lists:

  private PipelineProvider CreateBatchUpsertValidationPipeline()
      => new(GetUpsertCoreSteps());

  private PipelineProvider CreateBatchUpdateValidationPipeline()
      => new(GetUpdateCoreSteps());

  private PipelineProvider CreateBatchDeleteValidationPipeline()
      => new(GetDeleteCoreSteps());

  These PipelineProviders are what BatchHandler will run per operation.

  ———

  2. How BatchHandler composes per-operation RequestInfo

  BatchHandler sits in a batch pipeline that looks like:

  private PipelineProvider CreateBatchPipeline()
  {
      var steps = GetCommonInitialSteps(); // logging + exception + JWT once per /batch request
      steps.Add(new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger));
      steps.Add(new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache));
      steps.Add(new BatchHandler(
          _logger,
          _appSettings,
          _resiliencePipeline,
          _batchUnitOfWorkFactory,
          new VersionedLazy<PipelineProvider>(CreateBatchUpsertValidationPipeline, () => _apiSchemaProvider.ReloadId),
          new VersionedLazy<PipelineProvider>(CreateBatchUpdateValidationPipeline, () => _apiSchemaProvider.ReloadId),
          new VersionedLazy<PipelineProvider>(CreateBatchDeleteValidationPipeline, () => _apiSchemaProvider.ReloadId)
      ));
      return new PipelineProvider(steps);
  }

  So by the time BatchHandler.Execute runs:

  - batchInfo.ClientAuthorizations is set (JWT middleware).
  - batchInfo.ApiSchemaDocuments and batchInfo.ApiSchemaReloadId are set (schema middleware).
  - No resource-specific work has been done yet.

  Inside BatchHandler.Execute(RequestInfo batchInfo, Func<Task> next):

  1. Parse the batch body into an operation list
      - Read batchInfo.FrontendRequest.BodyStream / .Body and parse to JsonArray.
      - Enforce BulkMaxOperations.
  2. Open a unit of work
      - using var uow = await _batchUnitOfWorkFactory.BeginAsync(batchInfo.FrontendRequest.TraceId, batchInfo.FrontendRequest.Headers);
  3. For each operation: build a per-operation RequestInfo

     Pseudocode for a single operation op:

     // 1. Determine HTTP method
     var method = op.Op switch
     {
         "create" => RequestMethod.POST,
         "update" => RequestMethod.PUT,
         "delete" => RequestMethod.DELETE,
         _ => throw ...
     };

     // 2. Resolve project + endpoint name from resource name
     // (uses the same ApiSchemaDocuments that schema middleware prepared)
    var projectSchema = FindProjectSchemaForEndpoint(batchInfo.ApiSchemaDocuments, op.Endpoint);
    var resourceSchema = new ResourceSchema(
        projectSchema.FindResourceSchemaNodeByEndpointName(op.Endpoint)
    );
    var endpointName = projectSchema.GetEndpointNameFromResourceName(resourceSchema.ResourceName);

     // 3. Resolve documentId if naturalKey is used
     DocumentUuid? documentUuid = null;
     if (method is RequestMethod.PUT or RequestMethod.DELETE)
     {
         if (op.DocumentId is not null)
         {
             documentUuid = new DocumentUuid(Guid.Parse(op.DocumentId));
         }
         else if (op.NaturalKey is not null)
         {
            var identity = BuildDocumentIdentityFromNaturalKey(projectSchema, op.Endpoint, op.NaturalKey);
            var referentialId = ReferentialIdCalculator.ReferentialIdFrom(new ResourceInfo(...), identity);
             documentUuid = await uow.ResolveDocumentUuidAsync(
                referentialId,
                batchInfo.FrontendRequest.TraceId
             );
             if (documentUuid is null)
             {
                 // treat as not-found for this operation, mark batch failure
             }
         }
     }

     // 4. Build per-operation path
     string path = method switch
     {
         RequestMethod.POST => $"/{projectSchema.ProjectEndpointName.Value}/{endpointName.Value}",
         RequestMethod.PUT or RequestMethod.DELETE
             => $"/{projectSchema.ProjectEndpointName.Value}/{endpointName.Value}/{documentUuid!.Value.Value}",
         _ => throw ...
     };

     // 5. Build per-operation FrontendRequest
     var bodyJson = op.Document is null ? null : op.Document.ToJsonString();
     var opFrontend = new FrontendRequest(
         Path: path,
         Body: bodyJson,
         Headers: batchInfo.FrontendRequest.Headers, // share headers
         QueryParameters: new Dictionary<string,string>(),
         TraceId: batchInfo.FrontendRequest.TraceId
     );

     // 6. Build per-operation RequestInfo
     var opInfo = new RequestInfo(opFrontend, method)
     {
         // Reuse schema and auth context from batch-level pipeline
         ApiSchemaDocuments = batchInfo.ApiSchemaDocuments,
         ApiSchemaReloadId = batchInfo.ApiSchemaReloadId,
         ClientAuthorizations = batchInfo.ClientAuthorizations,
     };
  4. Run the validation-only pipeline for that operation

     PipelineProvider pipeline = method switch
     {
         RequestMethod.POST => _batchUpsertValidationPipeline.Value,
         RequestMethod.PUT  => _batchUpdateValidationPipeline.Value,
         RequestMethod.DELETE => _batchDeleteValidationPipeline.Value,
         _ => throw ...
     };

     await pipeline.Run(opInfo);
      - All the “pre” middlewares run:
          - ParsePathMiddleware parses the synthetic path and sets PathComponents.
          - ParseBodyMiddleware parses Body to ParsedBody.
          - ValidateEndpointMiddleware uses opInfo.ApiSchemaDocuments to set ProjectSchema and ResourceSchema.
          - Coercion, validation, equality, security extraction, BuildResourceInfo, ExtractDocumentInfo, authorization strategies, and ProvideAuthorizationPathwayMiddleware all populate the same fields they do today.
      - If any middleware decides this operation is invalid/unauthorized, it sets opInfo.FrontendResponse and does not call next.
  5. Check for short-circuit vs success

     if (opInfo.FrontendResponse != No.FrontendResponse)
     {
         // Interpret opInfo.FrontendResponse.StatusCode (400, 403, 404, 409, etc.)
         // Build batch-level failure (index, op, resource, httpStatus, errorCode, message)
         await uow.RollbackAsync();
         batchInfo.FrontendResponse = BuildBatchFailureResponse(...);
         return;
     }
      - At this point, for successful operations:
          - opInfo.ParsedBody is the coerced/validated document.
          - opInfo.ResourceInfo describes the resource.
          - opInfo.DocumentInfo contains DocumentIdentity, ReferentialId, references, descriptors.
          - opInfo.DocumentSecurityElements, AuthorizationSecurableInfo, and AuthorizationPathways are all populated.
          - The caller is authorized for the action under the current claim set.
  6. Build backend request and call IBatchUnitOfWork

     Depending on method:

     if (method == RequestMethod.POST)
     {
         var upsertRequest = new UpsertRequest(
             ResourceInfo: opInfo.ResourceInfo,
             DocumentInfo: opInfo.DocumentInfo,
             EdfiDoc: opInfo.ParsedBody,
             Headers: opInfo.FrontendRequest.Headers,
             TraceId: opInfo.FrontendRequest.TraceId,
             DocumentUuid: new DocumentUuid(FastGuid.NewPostgreSqlGuid()),
             DocumentSecurityElements: opInfo.DocumentSecurityElements,
             UpdateCascadeHandler: new UpdateCascadeHandler(_apiSchemaProvider, _logger),
             ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                 opInfo.AuthorizationStrategyEvaluators,
                 opInfo.AuthorizationSecurableInfo,
                 _authorizationServiceFactory,
                 _logger
             ),
             ResourceAuthorizationPathways: opInfo.AuthorizationPathways
         );

         var upsertResult = await uow.UpsertDocumentAsync(upsertRequest);
         // interpret result as today, map to per-op success/failure
     }
     else if (method == RequestMethod.PUT)
     {
         var updateRequest = new UpdateRequest(
             DocumentUuid: documentUuid!,
             ResourceInfo: opInfo.ResourceInfo,
             DocumentInfo: opInfo.DocumentInfo,
             EdfiDoc: opInfo.ParsedBody,
             Headers: opInfo.FrontendRequest.Headers,
             DocumentSecurityElements: opInfo.DocumentSecurityElements,
             TraceId: opInfo.FrontendRequest.TraceId,
             UpdateCascadeHandler: new UpdateCascadeHandler(_apiSchemaProvider, _logger),
             ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                 opInfo.AuthorizationStrategyEvaluators,
                 opInfo.AuthorizationSecurableInfo,
                 _authorizationServiceFactory,
                 _logger
             ),
             ResourceAuthorizationPathways: opInfo.AuthorizationPathways
         );

         var updateResult = await uow.UpdateDocumentByIdAsync(updateRequest);
         // interpret as in UpdateByIdHandler
     }
     else if (method == RequestMethod.DELETE)
     {
         var deleteRequest = new DeleteRequest(
             DocumentUuid: documentUuid!,
             ResourceInfo: opInfo.ResourceInfo,
             TraceId: opInfo.FrontendRequest.TraceId,
             ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                 opInfo.AuthorizationStrategyEvaluators,
                 opInfo.AuthorizationSecurableInfo,
                 _authorizationServiceFactory,
                 _logger
             ),
             ResourceAuthorizationPathways: opInfo.AuthorizationPathways,
             DeleteInEdOrgHierarchy: opInfo.ProjectSchema.EducationOrganizationTypes.Contains(
                 opInfo.ResourceSchema.ResourceName
             ),
             Headers: opInfo.FrontendRequest.Headers
         );

         var deleteResult = await uow.DeleteDocumentByIdAsync(deleteRequest);
         // interpret as in DeleteByIdHandler
     }
  7. After all operations succeed
      - await uow.CommitAsync();
      - Build the array of per-operation success objects and set batchInfo.FrontendResponse with status 200.

  ———

  Why this gives you maximum reuse

  - All the “interesting” behavior lives in the same middlewares and handlers as today.
  - The only new abstractions are:
      - The three Get*CoreSteps helpers.
      - Three validation-only PipelineProviders for batch.
      - BatchHandler orchestrating per-op RequestInfo and calls to IBatchUnitOfWork.
  - Middleware pre/post split is honored:
      - Pre-phase (coerce/validate/authorize) runs in the per-op pipelines.
      - Post-phase (producing HTTP response and logging it) is centralized at the batch level.

-----


 Here’s how I’d wire batch into IApiService and the ASP.NET Core frontend

  ———

  ### 1. IApiService contract

  Add a new method to IApiService (core external interface):

  /// <summary>
  /// Executes a batch of create/update/delete operations as a single unit of work.
  /// </summary>
  Task<IFrontendResponse> ExecuteBatchAsync(FrontendRequest frontendRequest);

  Implementation in ApiService:

  Fields:

  private readonly VersionedLazy<PipelineProvider> _batchSteps;

  Constructor wiring:

  _batchSteps = new VersionedLazy<PipelineProvider>(
      CreateBatchPipeline,
      () => _apiSchemaProvider.ReloadId
  );

  Method:

  public async Task<IFrontendResponse> ExecuteBatchAsync(FrontendRequest frontendRequest)
  {
      var requestInfo = new RequestInfo(frontendRequest, RequestMethod.POST);
      await _batchSteps.Value.Run(requestInfo);
      return requestInfo.FrontendResponse;
  }

  - Top-level method is treated as a POST (consistent with /batch semantics).
  - CreateBatchPipeline is the HTTP-level / batch-level pipeline we sketched before:
      - GetCommonInitialSteps() (logging, exception logging, JWT).
      - ApiSchemaValidationMiddleware + ProvideApiSchemaMiddleware.
      - BatchHandler (which loops operations and runs the validation-only sub-pipelines).

  Conceptually:

  private PipelineProvider CreateBatchPipeline()
  {
      var steps = GetCommonInitialSteps();

      steps.AddRange(
      [
          new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
          new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
          new BatchHandler(
              _logger,
              _appSettings,
              _resiliencePipeline,
              _batchUnitOfWorkFactory,
              new VersionedLazy<PipelineProvider>(CreateBatchUpsertValidationPipeline, () => _apiSchemaProvider.ReloadId),
              new VersionedLazy<PipelineProvider>(CreateBatchUpdateValidationPipeline, () => _apiSchemaProvider.ReloadId),
              new VersionedLazy<PipelineProvider>(CreateBatchDeleteValidationPipeline, () => _apiSchemaProvider.ReloadId)
          )
      ]);

      return new PipelineProvider(steps);
  }

  Where CreateBatchUpsertValidationPipeline() / CreateBatchUpdateValidationPipeline() / CreateBatchDeleteValidationPipeline() just wrap GetUpsertCoreSteps() / GetUpdateCoreSteps() / GetDeleteCoreSteps() in a
  PipelineProvider.

  ———

  ### 2. Frontend wiring (ASP.NET Core)

  You already have:

  - AspNetCoreFrontend.Upsert, .Get, .UpdateById, .DeleteById.
  - CoreEndpointModule mapping /data/{**dmsPath} to those methods.
  - WebApplicationExtensions.MapRouteEndpoints that discovers all IEndpointModule types via reflection.

  We’ll follow the same pattern for /batch.

  #### 2.1 Add a batch adapter in AspNetCoreFrontend

  To avoid duplicating HTTP↔FrontendRequest logic, add a static method that’s analogous to Upsert/UpdateById, but calls the new IApiService.ExecuteBatchAsync:

  public static async Task<IResult> Batch(
      HttpContext httpContext,
      IApiService apiService,
      IOptions<AppSettings> options
  )
  {
      // Reuse the same header/query/trace-id extraction logic as existing endpoints.
      // dmsPath is not used by batch pipelines for routing; it’s just needed
      // to compute Location headers, which batch does not set.
      var frontendRequest = FromRequest(
          httpContext.Request,
          dmsPath: "batch",
          options,
          includeBody: true
      );

      var frontendResponse = await apiService.ExecuteBatchAsync(frontendRequest);

      return ToResult(
          frontendResponse,
          httpContext,
          dmsPath: "batch"
      );
  }

  Notes:

  - This assumes we slightly relax the visibility of FromRequest and ToResult (e.g., keep them internal within the same assembly or just leave them as-is if Batch is defined inside AspNetCoreFrontend and can call the
    private methods).
  - dmsPath: "batch" is arbitrary; the batch pipeline doesn’t use ParsePathMiddleware on this top-level path at all. All “real” routing happens on the synthesized per-operation paths inside BatchHandler.

  #### 2.2 New endpoint module: /batch

  Add BatchEndpointModule in EdFi.DataManagementService.Frontend.AspNetCore/Modules:

  using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

  namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

  public class BatchEndpointModule : IEndpointModule
  {
      public void MapEndpoints(IEndpointRouteBuilder endpoints)
      {
          endpoints
              .MapPost("/batch", Batch) // the static method above
              .WithName("BatchOperations")
              .WithSummary("Executes multiple create/update/delete operations as a single batch.");
      }
  }

  Because WebApplicationExtensions.MapRouteEndpoints scans for IEndpointModule implementations in the frontend assembly, this module will automatically be registered when the app starts:

  public static void MapRouteEndpoints(this WebApplication application)
  {
      var moduleInterface = typeof(IEndpointModule);
      var moduleClasses = Assembly
          .GetExecutingAssembly()
          .GetTypes()
          .Where(p => moduleInterface.IsAssignableFrom(p) && p.IsClass);

      List<IEndpointModule> modules = [];

      foreach (var moduleClass in moduleClasses)
      {
          if (Activator.CreateInstance(moduleClass) is IEndpointModule module)
          {
              modules.Add(module);
          }
      }
      application.UseEndpoints(endpoints =>
      {
          foreach (var routeBuilder in modules)
          {
              routeBuilder.MapEndpoints(endpoints);
          }
      });
  }

  So adding BatchEndpointModule is all that’s needed on the frontend side.

  ———

  ### 3. Call flow summary

  Putting it together:

  1. Incoming HTTP: POST /batch with array of operations in the body.
  2. ASP.NET Core endpoint:
      - BatchEndpointModule maps /batch to AspNetCoreFrontend.Batch.
      - AspNetCoreFrontend.Batch:
          - Builds a FrontendRequest using the same rules as /data endpoints (headers, trace id, query string handling).
          - Calls IApiService.ExecuteBatchAsync.
  3. Core:
      - ApiService.ExecuteBatchAsync wraps the FrontendRequest in a RequestInfo (method POST) and runs _batchSteps.
      - _batchSteps pipeline:
          - RequestResponseLoggingMiddleware + CoreExceptionLoggingMiddleware + JwtAuthenticationMiddleware.
          - ApiSchemaValidationMiddleware + ProvideApiSchemaMiddleware.
          - BatchHandler.
  4. BatchHandler:
      - Parses the batch body, enforces max operations.
      - Opens IBatchUnitOfWork.
      - For each operation:
          - Synthesizes an operation-specific FrontendRequest (Path, Body, Method).
          - Creates opInfo : RequestInfo with:
              - ApiSchemaDocuments, ApiSchemaReloadId, and ClientAuthorizations copied from the batch-level RequestInfo.
          - Runs the appropriate validation-only pipeline (_batchUpsertValidation, _batchUpdateValidation, _batchDeleteValidation) built from Get*CoreSteps.
          - If any step sets opInfo.FrontendResponse, maps that to a per-operation failure, rolls back, and returns.
          - Otherwise builds UpsertRequest / UpdateRequest / DeleteRequest and calls the unit of work.
      - On complete success:
          - Commits the transaction and returns a 200 with per-operation results.
  5. Frontend response:
      - AspNetCoreFrontend.Batch uses ToResult to translate the IFrontendResponse into an IResult, including JSON body and headers.

  This wiring keeps:

  - Minimal change to existing contracts (IApiService + one new method).
  - Minimal change to frontend patterns (one new module + one static adapter method).
  - All validation/authorization logic shared between single-resource and batch operations via the core step helpers and validation-only pipelines.
