// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace EdFi.DataManagementService.Core;

/// <summary>
/// The DMS API service.
/// </summary>
internal class ApiService(
    IApiSchemaProvider _apiSchemaProvider,
    IApiSchemaValidator _apiSchemaValidator,
    IDocumentStoreRepository _documentStoreRepository,
    IDocumentValidator _documentValidator,
    IQueryHandler _queryHandler,
    IMatchingDocumentUuidsValidator matchingDocumentUuidsValidator,
    IEqualityConstraintValidator _equalityConstraintValidator,
    ILogger<ApiService> _logger,
    IOptions<AppSettings> _appSettings,
    [FromKeyedServices("backendResiliencePipeline")] ResiliencePipeline _resiliencePipeline
) : IApiService
{
    /// <summary>
    /// The pipeline steps to satisfy an upsert request
    /// </summary>
    private readonly Lazy<PipelineProvider> _upsertSteps =
        new(() =>
        {
            var steps = new List<IPipelineStep>();
            steps.AddRange(
                [
                    new CoreExceptionLoggingMiddleware(_logger),
                    new ApiSchemaValidationMiddleware(_apiSchemaProvider, _apiSchemaValidator, _logger),
                    new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                    new ParsePathMiddleware(_logger),
                    new ParseBodyMiddleware(_logger),
                    new RequestDataBodyLoggingMiddleware(_logger, _appSettings.Value.MaskRequestBodyInLogs),
                    new DuplicatePropertiesMiddleware(_logger),
                    new ValidateEndpointMiddleware(_logger),
                    new RejectResourceIdentifierMiddleware(_logger),
                ]
            );

            // CoerceFromStringsMiddleware should be immediately before ValidateDocumentMiddleware
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
                    new ValidateEqualityConstraintMiddleware(_logger, _equalityConstraintValidator),
                    new BuildResourceInfoMiddleware(
                        _logger,
                        _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                    ),
                    new ExtractDocumentInfoMiddleware(_logger),
                    new DisallowDuplicateReferencesMiddleware(_logger),
                    new InjectLastModifiedDateToEdFiDocumentMiddleware(_logger),
                    new UpsertHandler(
                        _documentStoreRepository,
                        _logger,
                        _resiliencePipeline,
                        _apiSchemaProvider
                    ),
                ]
            );

            return new PipelineProvider(steps);
        });

    /// <summary>
    /// The pipeline steps to satisfy a get by id request
    /// </summary>
    private readonly Lazy<PipelineProvider> _getByIdSteps =
        new(
            () =>
                new(
                    [
                        new CoreExceptionLoggingMiddleware(_logger),
                        new ApiSchemaValidationMiddleware(_apiSchemaProvider, _apiSchemaValidator, _logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new BuildResourceInfoMiddleware(
                            _logger,
                            _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                        ),
                        new GetByIdHandler(_documentStoreRepository, _logger, _resiliencePipeline),
                    ]
                )
        );

    /// <summary>
    /// The pipeline steps to satisfy a query request
    /// </summary>
    private readonly Lazy<PipelineProvider> _querySteps =
        new(
            () =>
                new(
                    [
                        new CoreExceptionLoggingMiddleware(_logger),
                        new ApiSchemaValidationMiddleware(_apiSchemaProvider, _apiSchemaValidator, _logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new BuildResourceInfoMiddleware(
                            _logger,
                            _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                        ),
                        new ValidateQueryMiddleware(_logger),
                        new QueryRequestHandler(_queryHandler, _logger, _resiliencePipeline),
                    ]
                )
        );

    /// <summary>
    /// The pipeline steps to satisfy an update request
    /// </summary>
    private readonly Lazy<PipelineProvider> _updateSteps =
        new(() =>
        {
            var steps = new List<IPipelineStep>();
            steps.AddRange(
                [
                    new CoreExceptionLoggingMiddleware(_logger),
                    new ApiSchemaValidationMiddleware(_apiSchemaProvider, _apiSchemaValidator, _logger),
                    new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                    new ParsePathMiddleware(_logger),
                    new ParseBodyMiddleware(_logger),
                    new RequestDataBodyLoggingMiddleware(_logger, _appSettings.Value.MaskRequestBodyInLogs),
                    new DuplicatePropertiesMiddleware(_logger),
                    new ValidateEndpointMiddleware(_logger),
                ]
            );

            // CoerceFromStringsMiddleware should be immediately before ValidateDocumentMiddleware
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
                    new ValidateMatchingDocumentUuidsMiddleware(_logger, matchingDocumentUuidsValidator),
                    new ValidateEqualityConstraintMiddleware(_logger, _equalityConstraintValidator),
                    new BuildResourceInfoMiddleware(
                        _logger,
                        _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                    ),
                    new ExtractDocumentInfoMiddleware(_logger),
                    new DisallowDuplicateReferencesMiddleware(_logger),
                    new InjectLastModifiedDateToEdFiDocumentMiddleware(_logger),
                    new UpdateByIdHandler(
                        _documentStoreRepository,
                        _logger,
                        _resiliencePipeline,
                        _apiSchemaProvider
                    ),
                ]
            );
            return new PipelineProvider(steps);
        });

    /// <summary>
    /// The pipeline steps to satisfy a delete by id request
    /// </summary>
    private readonly Lazy<PipelineProvider> _deleteByIdSteps =
        new(
            () =>
                new(
                    [
                        new CoreExceptionLoggingMiddleware(_logger),
                        new ApiSchemaValidationMiddleware(_apiSchemaProvider, _apiSchemaValidator, _logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new BuildResourceInfoMiddleware(
                            _logger,
                            _appSettings.Value.AllowIdentityUpdateOverrides.Split(',').ToList()
                        ),
                        new DeleteByIdHandler(_documentStoreRepository, _logger, _resiliencePipeline),
                    ]
                )
        );

    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    public async Task<IFrontendResponse> Upsert(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest, RequestMethod.POST);
        await _upsertSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API GET requests
    /// </summary>
    public async Task<IFrontendResponse> Get(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest, RequestMethod.GET);

        Match match = UtilityService.PathExpressionRegex().Match(frontendRequest.Path);

        string documentUuid = string.Empty;

        if (match.Success)
        {
            documentUuid = match.Groups["documentUuid"].Value;
        }

        if (documentUuid != string.Empty)
        {
            await _getByIdSteps.Value.Run(pipelineContext);
        }
        else
        {
            await _querySteps.Value.Run(pipelineContext);
        }
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    public async Task<IFrontendResponse> UpdateById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest, RequestMethod.PUT);
        await _updateSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    public async Task<IFrontendResponse> DeleteById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest, RequestMethod.DELETE);
        await _deleteByIdSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for data model information from ApiSchema.json
    /// </summary>
    public IList<IDataModelInfo> GetDataModelInfo()
    {
        var apiSchemaDocument = new ApiSchemaDocument(_apiSchemaProvider.ApiSchemaRootNode, _logger);

        IList<IDataModelInfo> result = [];
        foreach (JsonNode projectSchemaNode in apiSchemaDocument.GetAllProjectSchemaNodes())
        {
            var projectName = projectSchemaNode?["projectName"]?.GetValue<string>() ?? string.Empty;
            var projectVersion = projectSchemaNode?["projectVersion"]?.GetValue<string>() ?? string.Empty;
            var description = projectSchemaNode?["description"]?.GetValue<string>() ?? string.Empty;

            result.Add(new DataModelInfo(projectName, projectVersion, description));
        }
        return result;
    }

    /// <summary>
    /// Get resource dependencies
    /// </summary>
    /// <returns>JSON array ordered by dependency sequence</returns>
    public JsonArray GetDependencies()
    {
        var dependencyCalculator = new DependencyCalculator(_apiSchemaProvider.ApiSchemaRootNode, _logger);
        return dependencyCalculator.GetDependenciesFromResourceSchema();
    }
}
