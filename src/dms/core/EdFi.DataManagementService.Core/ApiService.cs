// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.OpenApi;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.ResourceLoadOrder;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The DMS API service.
/// </summary>
internal class ApiService : IApiService
{
    private readonly IApiSchemaProvider _apiSchemaProvider;
    private readonly IDocumentStoreRepository _documentStoreRepository;
    private readonly IClaimSetProvider _claimSetProvider;
    private readonly IDocumentValidator _documentValidator;
    private readonly IQueryHandler _queryHandler;
    private readonly IMatchingDocumentUuidsValidator _matchingDocumentUuidsValidator;
    private readonly IEqualityConstraintValidator _equalityConstraintValidator;
    private readonly IDecimalValidator _decimalValidator;
    private readonly ILogger<ApiService> _logger;
    private readonly IOptions<AppSettings> _appSettings;
    private readonly IAuthorizationServiceFactory _authorizationServiceFactory;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ResourceLoadOrderCalculator _resourceLoadCalculator;
    private readonly IUploadApiSchemaService _apiSchemaUploadService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ClaimSetsCache _claimSetsCache;
    private readonly ICompiledSchemaCache _compiledSchemaCache;
    private readonly IBatchUnitOfWorkFactory? _batchUnitOfWorkFactory;

    /// <summary>
    /// The pipeline steps to satisfy an upsert request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _upsertSteps;

    /// <summary>
    /// The pipeline steps to satisfy a get by id request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _getByIdSteps;

    /// <summary>
    /// The pipeline steps to satisfy a query request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _querySteps;

    /// <summary>
    /// The pipeline steps to satisfy an update request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _updateSteps;

    /// <summary>
    /// The pipeline steps to satisfy a delete by id request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _deleteByIdSteps;

    /// <summary>
    /// The pipeline steps to satisfy a batch request
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _batchSteps;

    /// <summary>
    /// Validation-only upsert pipeline for batch operations.
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _batchUpsertValidationSteps;

    /// <summary>
    /// Validation-only update pipeline for batch operations.
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _batchUpdateValidationSteps;

    /// <summary>
    /// Validation-only delete pipeline for batch operations.
    /// </summary>
    private readonly VersionedLazy<PipelineProvider> _batchDeleteValidationSteps;

    /// <summary>
    /// The OpenAPI specification derived from core and extension ApiSchemas
    /// </summary>
    private readonly VersionedLazy<JsonNode> _resourceOpenApiSpecification;

    /// <summary>
    /// The OpenAPI specification derived from core and extension ApiSchemas
    /// </summary>
    private readonly VersionedLazy<JsonNode> _descriptorOpenApiSpecification;

    public ApiService(
        IApiSchemaProvider apiSchemaProvider,
        IDocumentStoreRepository documentStoreRepository,
        IClaimSetProvider claimSetProvider,
        IDocumentValidator documentValidator,
        IQueryHandler queryHandler,
        IMatchingDocumentUuidsValidator matchingDocumentUuidsValidator,
        IEqualityConstraintValidator equalityConstraintValidator,
        IDecimalValidator decimalValidator,
        ILogger<ApiService> logger,
        IOptions<AppSettings> appSettings,
        IAuthorizationServiceFactory authorizationServiceFactory,
        [FromKeyedServices("backendResiliencePipeline")] ResiliencePipeline resiliencePipeline,
        ResourceLoadOrderCalculator resourceLoadCalculator,
        IUploadApiSchemaService apiSchemaUploadService,
        IServiceProvider serviceProvider,
        ClaimSetsCache claimSetsCache,
        ICompiledSchemaCache compiledSchemaCache
    )
    {
        _apiSchemaProvider = apiSchemaProvider;
        _documentStoreRepository = documentStoreRepository;
        _claimSetProvider = claimSetProvider;
        _documentValidator = documentValidator;
        _queryHandler = queryHandler;
        _matchingDocumentUuidsValidator = matchingDocumentUuidsValidator;
        _equalityConstraintValidator = equalityConstraintValidator;
        _decimalValidator = decimalValidator;
        _logger = logger;
        _appSettings = appSettings;
        _authorizationServiceFactory = authorizationServiceFactory;
        _resiliencePipeline = resiliencePipeline;
        _resourceLoadCalculator = resourceLoadCalculator;
        _apiSchemaUploadService = apiSchemaUploadService;
        _serviceProvider = serviceProvider;
        _claimSetsCache = claimSetsCache;
        _compiledSchemaCache = compiledSchemaCache;
        _batchUnitOfWorkFactory = _serviceProvider.GetService<IBatchUnitOfWorkFactory>();

        // Initialize VersionedLazy instances with schema version provider
        _upsertSteps = new VersionedLazy<PipelineProvider>(
            CreateUpsertPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _getByIdSteps = new VersionedLazy<PipelineProvider>(
            CreateGetByIdPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _querySteps = new VersionedLazy<PipelineProvider>(
            CreateQueryPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _updateSteps = new VersionedLazy<PipelineProvider>(
            CreateUpdatePipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _deleteByIdSteps = new VersionedLazy<PipelineProvider>(
            CreateDeleteByIdPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _batchSteps = new VersionedLazy<PipelineProvider>(
            CreateBatchPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _batchUpsertValidationSteps = new VersionedLazy<PipelineProvider>(
            CreateBatchUpsertValidationPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _batchUpdateValidationSteps = new VersionedLazy<PipelineProvider>(
            CreateBatchUpdateValidationPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _batchDeleteValidationSteps = new VersionedLazy<PipelineProvider>(
            CreateBatchDeleteValidationPipeline,
            () => _apiSchemaProvider.ReloadId
        );

        _resourceOpenApiSpecification = new VersionedLazy<JsonNode>(
            CreateResourceOpenApiSpecification,
            () => _apiSchemaProvider.ReloadId
        );

        _descriptorOpenApiSpecification = new VersionedLazy<JsonNode>(
            CreateDescriptorOpenApiSpecification,
            () => _apiSchemaProvider.ReloadId
        );
    }

    private List<IPipelineStep> GetCommonInitialSteps()
    {
        return
        [
            new RequestResponseLoggingMiddleware(_logger),
            new CoreExceptionLoggingMiddleware(_logger),
            _serviceProvider.GetRequiredService<JwtAuthenticationMiddleware>(),
        ];
    }

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

        if (_appSettings.Value.BypassStringTypeCoercion)
        {
            _logger.LogDebug("Bypassing CoerceFromStringsMiddleware");
        }
        else
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
            ]
        );

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

        if (_appSettings.Value.BypassStringTypeCoercion)
        {
            _logger.LogDebug("Bypassing CoerceFromStringsMiddleware");
        }
        else
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
            ]
        );

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

    private PipelineProvider CreateUpsertPipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
            ]
        );

        steps.AddRange(GetUpsertCoreSteps());
        steps.Add(
            new UpsertHandler(
                _documentStoreRepository,
                _logger,
                _resiliencePipeline,
                _apiSchemaProvider,
                _authorizationServiceFactory
            )
        );

        return new PipelineProvider(steps);
    }

    private PipelineProvider CreateGetByIdPipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
                new ParsePathMiddleware(_logger),
                new ValidateEndpointMiddleware(_logger),
                new BuildResourceInfoMiddleware(
                    _logger,
                    _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                ),
                new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
                new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
                new ProvideAuthorizationSecurableInfoMiddleware(_logger),
                new GetByIdHandler(
                    _documentStoreRepository,
                    _logger,
                    _resiliencePipeline,
                    _authorizationServiceFactory
                ),
            ]
        );

        return new PipelineProvider(steps);
    }

    private PipelineProvider CreateQueryPipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
                new ParsePathMiddleware(_logger),
                new ValidateEndpointMiddleware(_logger),
                new ProvideAuthorizationSecurableInfoMiddleware(_logger),
                new BuildResourceInfoMiddleware(
                    _logger,
                    _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                ),
                new ValidateQueryMiddleware(_logger, _appSettings.Value.MaximumPageSize),
                new ResourceActionAuthorizationMiddleware(_claimSetProvider, _logger),
                new ProvideAuthorizationFiltersMiddleware(_authorizationServiceFactory, _logger),
                new QueryRequestHandler(_queryHandler, _logger, _resiliencePipeline),
            ]
        );

        return new PipelineProvider(steps);
    }

    private PipelineProvider CreateUpdatePipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
            ]
        );

        steps.AddRange(GetUpdateCoreSteps());
        steps.Add(
            new UpdateByIdHandler(
                _documentStoreRepository,
                _logger,
                _resiliencePipeline,
                _apiSchemaProvider,
                _authorizationServiceFactory
            )
        );
        return new PipelineProvider(steps);
    }

    private PipelineProvider CreateDeleteByIdPipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
            ]
        );

        steps.AddRange(GetDeleteCoreSteps());
        steps.Add(
            new DeleteByIdHandler(
                _documentStoreRepository,
                _logger,
                _resiliencePipeline,
                _authorizationServiceFactory
            )
        );

        return new PipelineProvider(steps);
    }

    private PipelineProvider CreateBatchUpsertValidationPipeline() => new(GetUpsertCoreSteps());

    private PipelineProvider CreateBatchUpdateValidationPipeline() => new(GetUpdateCoreSteps());

    private PipelineProvider CreateBatchDeleteValidationPipeline() => new(GetDeleteCoreSteps());

    private PipelineProvider CreateBatchPipeline()
    {
        var steps = GetCommonInitialSteps();
        steps.AddRange(
            [
                new ApiSchemaValidationMiddleware(_apiSchemaProvider, _logger),
                new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger, _compiledSchemaCache),
                new BatchHandler(
                    _serviceProvider.GetRequiredService<ILogger<BatchHandler>>(),
                    _appSettings,
                    _resiliencePipeline,
                    _batchUnitOfWorkFactory,
                    _apiSchemaProvider,
                    _authorizationServiceFactory,
                    _batchUpsertValidationSteps,
                    _batchUpdateValidationSteps,
                    _batchDeleteValidationSteps
                ),
            ]
        );

        return new PipelineProvider(steps);
    }

    /// <summary>
    /// Parses the excluded domains configuration setting into an array of domain names
    /// </summary>
    private string[] GetExcludedDomainsFromConfiguration()
    {
        return string.IsNullOrWhiteSpace(_appSettings.Value.DomainsExcludedFromOpenApi)
            ? []
            : _appSettings
                .Value.DomainsExcludedFromOpenApi.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .ToArray();
    }

    private JsonNode CreateResourceOpenApiSpecification()
    {
        string[] excludedDomains = GetExcludedDomainsFromConfiguration();

        OpenApiDocument openApiDocument = new(_logger, excludedDomains);
        return openApiDocument.CreateDocument(
            _apiSchemaProvider.GetApiSchemaNodes(),
            OpenApiDocument.OpenApiDocumentType.Resource
        );
    }

    private JsonNode CreateDescriptorOpenApiSpecification()
    {
        string[] excludedDomains = GetExcludedDomainsFromConfiguration();

        OpenApiDocument descriptorOpenApiDocument = new(_logger, excludedDomains);
        return descriptorOpenApiDocument.CreateDocument(
            _apiSchemaProvider.GetApiSchemaNodes(),
            OpenApiDocument.OpenApiDocumentType.Descriptor
        );
    }

    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    public async Task<IFrontendResponse> Upsert(FrontendRequest frontendRequest)
    {
        RequestInfo requestInfo = new(frontendRequest, RequestMethod.POST);
        await _upsertSteps.Value.Run(requestInfo);
        return requestInfo.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API GET requests
    /// </summary>
    public async Task<IFrontendResponse> Get(FrontendRequest frontendRequest)
    {
        RequestInfo requestInfo = new(frontendRequest, RequestMethod.GET);

        Match match = UtilityService.PathExpressionRegex().Match(frontendRequest.Path);

        string documentUuid = string.Empty;

        if (match.Success)
        {
            documentUuid = match.Groups["documentUuid"].Value;
        }

        if (documentUuid != string.Empty)
        {
            await _getByIdSteps.Value.Run(requestInfo);
        }
        else
        {
            await _querySteps.Value.Run(requestInfo);
        }
        return requestInfo.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    public async Task<IFrontendResponse> UpdateById(FrontendRequest frontendRequest)
    {
        RequestInfo requestInfo = new(frontendRequest, RequestMethod.PUT);
        await _updateSteps.Value.Run(requestInfo);
        return requestInfo.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    public async Task<IFrontendResponse> DeleteById(FrontendRequest frontendRequest)
    {
        RequestInfo requestInfo = new(frontendRequest, RequestMethod.DELETE);
        await _deleteByIdSteps.Value.Run(requestInfo);
        return requestInfo.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for batch operations.
    /// </summary>
    public async Task<IFrontendResponse> ExecuteBatchAsync(FrontendRequest frontendRequest)
    {
        RequestInfo requestInfo = new(frontendRequest, RequestMethod.POST);
        await _batchSteps.Value.Run(requestInfo);

        if (requestInfo.FrontendResponse == No.FrontendResponse)
        {
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 501,
                Body: new JsonObject
                {
                    ["detail"] = "Batch endpoint is not yet implemented.",
                    ["status"] = 501,
                    ["correlationId"] = frontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }

        return requestInfo.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for data model information from ApiSchema.json
    /// </summary>
    public IList<IDataModelInfo> GetDataModelInfo()
    {
        ApiSchemaDocuments apiSchemaDocuments = new(_apiSchemaProvider.GetApiSchemaNodes(), _logger);

        IList<IDataModelInfo> result = [];
        foreach (ProjectSchema projectSchema in apiSchemaDocuments.GetAllProjectSchemas())
        {
            result.Add(
                new DataModelInfo(
                    projectSchema.ProjectName.Value,
                    projectSchema.ResourceVersion.Value,
                    projectSchema.Description
                )
            );
        }
        return result;
    }

    /// <summary>
    /// DMS entry point to get resource dependencies
    /// </summary>
    /// <returns>JSON array ordered by dependency sequence</returns>
    public JsonArray GetDependencies()
    {
        return new JsonArray(
            _resourceLoadCalculator
                .GetLoadOrder()
                .Select(loadOrder =>
                    JsonValue.Create(
                        new
                        {
                            resource = loadOrder.Resource,
                            order = loadOrder.Group,
                            operations = loadOrder.Operations,
                        }
                    )
                )
                .ToArray<JsonNode?>()
        );
    }

    /// <summary>
    /// DMS entry point to get the OpenAPI specification for resources, derived from core and extension ApiSchemas.
    /// Servers array should be provided by the front end.
    /// </summary>
    public JsonNode GetResourceOpenApiSpecification(JsonArray servers)
    {
        JsonNode specification = _resourceOpenApiSpecification.Value.DeepClone();
        specification["servers"] = servers;

        // Add OAuth2 Security Section
        AddOAuth2SecuritySection(specification);

        return specification;
    }

    /// <summary>
    /// DMS entry point to get the OpenAPI specification for descriptors, derived from core and extension ApiSchemas
    /// Servers array should be provided by the front end.
    /// </summary>
    public JsonNode GetDescriptorOpenApiSpecification(JsonArray servers)
    {
        JsonNode specification = _descriptorOpenApiSpecification.Value.DeepClone();
        specification["servers"] = servers;

        // Add OAuth2 Security Section
        AddOAuth2SecuritySection(specification);

        return specification;
    }

    /// <summary>
    /// Adds the OAuth2 security section to the OpenAPI specification.
    /// </summary>
    private void AddOAuth2SecuritySection(JsonNode specification)
    {
        string schemeName = "oauth2_client_credentials";

        string tokenUrl = _appSettings.Value.AuthenticationService!;
        if (specification["components"] is not JsonObject components)
        {
            components = new JsonObject();
            specification["components"] = components;
        }

        if (components["securitySchemes"] is not JsonObject securitySchemes)
        {
            securitySchemes = new JsonObject();
            components["securitySchemes"] = securitySchemes;
        }

        var oauth2Scheme = new JsonObject
        {
            ["type"] = "oauth2",
            ["description"] = "Ed-Fi DMS OAuth 2.0 Client Credentials Grant Type authorization",
            ["flows"] = new JsonObject
            {
                ["clientCredentials"] = new JsonObject
                {
                    ["tokenUrl"] = tokenUrl,
                    ["scopes"] = new JsonObject(),
                },
            },
        };

        securitySchemes[schemeName] = oauth2Scheme;

        specification["security"] = new JsonArray { new JsonObject { [schemeName] = new JsonArray() } };
    }

    /// <summary>
    /// Reloads the API schema from the configured source
    /// </summary>
    public async Task<IFrontendResponse> ReloadApiSchemaAsync()
    {
        // Check if management endpoints are enabled
        if (!_appSettings.Value.EnableManagementEndpoints)
        {
            _logger.LogWarning("API schema reload requested but management endpoints are disabled");
            return new FrontendResponse(StatusCode: 404, Body: null, Headers: []);
        }
        _logger.LogInformation("API schema reload requested");

        try
        {
            var (success, _) = await _apiSchemaProvider.ReloadApiSchemaAsync();

            if (success)
            {
                _logger.LogInformation(
                    "API schema reload completed successfully. All caches will be refreshed on next access."
                );

                return new FrontendResponse(
                    StatusCode: 200,
                    Body: JsonNode.Parse("""{"message": "Schema reloaded successfully"}"""),
                    Headers: []
                );
            }
            else
            {
                _logger.LogError("API schema reload failed");
                return new FrontendResponse(
                    StatusCode: 500,
                    Body: JsonNode.Parse("""{"error": "Schema reload failed"}"""),
                    Headers: []
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred during API schema reload");
            return new FrontendResponse(
                StatusCode: 500,
                Body: JsonNode.Parse($"{{ \"error\": \"Error during schema reload: {ex.Message}\" }}"),
                Headers: []
            );
        }
    }

    /// <summary>
    /// Uploads ApiSchemas from the provided content
    /// </summary>
    public async Task<IFrontendResponse> UploadApiSchemaAsync(UploadSchemaRequest request)
    {
        // Check if management endpoints are enabled
        if (!_appSettings.Value.EnableManagementEndpoints)
        {
            _logger.LogWarning("API schema upload requested but management endpoints are disabled");
            return new FrontendResponse(StatusCode: 404, Body: null, Headers: []);
        }

        UploadSchemaResponse uploadResponse = await _apiSchemaUploadService.UploadApiSchemaAsync(request);

        if (uploadResponse.Success)
        {
            return new FrontendResponse(
                StatusCode: 200,
                Body: JsonNode.Parse(
                    $$"""
                    {
                        "message": "Schema uploaded successfully",
                        "reloadId": "{{uploadResponse.ReloadId}}",
                        "schemasProcessed": {{uploadResponse.SchemasProcessed}}
                    }
                    """
                ),
                Headers: []
            );
        }

        var errorBody = new JsonObject { ["error"] = uploadResponse.ErrorMessage };

        // Add detailed failure information if available
        if (uploadResponse.Failures != null && uploadResponse.Failures.Count > 0)
        {
            var failuresArray = new JsonArray();
            foreach (var failure in uploadResponse.Failures)
            {
                var failureObj = new JsonObject
                {
                    ["type"] = failure.FailureType,
                    ["message"] = failure.Message,
                };

                if (failure.FailurePath != null)
                {
                    failureObj["path"] = JsonValue.Create(failure.FailurePath.Value);
                }

                if (failure.Exception != null)
                {
                    failureObj["exception"] = failure.Exception.Message;
                }

                failuresArray.Add(failureObj);
            }
            errorBody["failures"] = failuresArray;
        }

        return new FrontendResponse(StatusCode: 400, Body: errorBody, Headers: []);
    }

    /// <summary>
    /// Reloads the claimsets cache by clearing it and forcing an immediate reload from CMS
    /// </summary>
    public async Task<IFrontendResponse> ReloadClaimsetsAsync()
    {
        // Check if claimset reload endpoints are enabled
        if (!_appSettings.Value.EnableClaimsetReload)
        {
            _logger.LogWarning("Claimsets reload requested but claimset reload is disabled");
            return new FrontendResponse(StatusCode: 404, Body: null, Headers: []);
        }

        _logger.LogInformation("Claimsets reload requested");

        try
        {
            // Clear the existing cache
            _claimSetsCache.ClearCache();
            _logger.LogInformation("Claimsets cache cleared successfully");

            // Force immediate reload by calling GetAllClaimSets(), which will fetch from CMS and populate the cache
            var claimSets = await _claimSetProvider.GetAllClaimSets();
            _logger.LogInformation(
                "Claimsets reloaded successfully. Retrieved {ClaimSetCount} claimsets from CMS",
                claimSets.Count
            );

            return new FrontendResponse(
                StatusCode: 200,
                Body: JsonNode.Parse("""{"message": "Claimsets reloaded successfully"}"""),
                Headers: []
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred during claimsets reload");
            return new FrontendResponse(
                StatusCode: 500,
                Body: JsonNode.Parse($"{{ \"error\": \"Error during claimsets reload: {ex.Message}\" }}"),
                Headers: []
            );
        }
    }

    /// <summary>
    /// Views current claimsets from the provider
    /// </summary>
    public async Task<IFrontendResponse> ViewClaimsetsAsync()
    {
        // Check if claimset reload endpoints are enabled
        if (!_appSettings.Value.EnableClaimsetReload)
        {
            _logger.LogWarning("Claimsets view requested but claimset reload is disabled");
            return new FrontendResponse(StatusCode: 404, Body: null, Headers: []);
        }

        _logger.LogInformation("Claimsets view requested");

        try
        {
            var claimSets = await _claimSetProvider.GetAllClaimSets();

            _logger.LogInformation("Retrieved {ClaimSetCount} claimsets", claimSets.Count);

            var claimSetsJson = JsonSerializer.Serialize(
                claimSets,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }
            );

            return new FrontendResponse(StatusCode: 200, Body: JsonNode.Parse(claimSetsJson), Headers: []);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred during claimsets view");
            return new FrontendResponse(
                StatusCode: 500,
                Body: JsonNode.Parse($"{{ \"error\": \"Error retrieving claimsets: {ex.Message}\" }}"),
                Headers: []
            );
        }
    }
}
