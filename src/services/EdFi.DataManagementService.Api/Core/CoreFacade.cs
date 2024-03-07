// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Api.Core.Handler;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core;

/// <summary>
/// The DMS core facade.
/// </summary>
public class CoreFacade(
    IApiSchemaProvider _apiSchemaProvider,
    IDocumentStoreRepository _documentStoreRepository,
    IDocumentValidator _documentValidator,
    ILogger<CoreFacade> _logger
) : ICoreFacade
{
    /// <summary>
    /// The pipeline steps to satisfy an upsert request
    /// </summary>
    private readonly Lazy<PipelineProvider> _upsertSteps =
        new(
            () =>
                new(
                    [
                        new CoreLoggingMiddleware(_logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new ValidateDocumentMiddleware(_logger, _documentValidator),
                        new ExtractDocumentInfoMiddleware(_logger),
                        new BuildResourceInfoMiddleware(_logger),
                        new UpsertHandler(_documentStoreRepository, _logger)
                    ]
                )
        );

    /// <summary>
    /// The pipeline steps to satisfy a get by id request
    /// </summary>
    private readonly Lazy<PipelineProvider> _getByIdSteps =
        new(
            () =>
                new(
                    [
                        new CoreLoggingMiddleware(_logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new BuildResourceInfoMiddleware(_logger),
                        new GetByIdHandler(_documentStoreRepository, _logger)
                    ]
                )
        );

    /// <summary>
    /// The pipeline steps to satisfy an update request
    /// </summary>
    private readonly Lazy<PipelineProvider> _updateSteps =
        new(
            () =>
                new(
                    [
                        new CoreLoggingMiddleware(_logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new ValidateDocumentMiddleware(_logger, _documentValidator),
                        new ExtractDocumentInfoMiddleware(_logger),
                        new BuildResourceInfoMiddleware(_logger),
                        new UpdateByIdHandler(_documentStoreRepository, _logger)
                    ]
                )
        );

    /// <summary>
    /// The pipeline steps to satisfy a delete by id request
    /// </summary>
    private readonly Lazy<PipelineProvider> _deleteByIdSteps =
        new(
            () =>
                new(
                    [
                        new CoreLoggingMiddleware(_logger),
                        new ProvideApiSchemaMiddleware(_apiSchemaProvider, _logger),
                        new ParsePathMiddleware(_logger),
                        new ValidateEndpointMiddleware(_logger),
                        new BuildResourceInfoMiddleware(_logger),
                        new DeleteByIdHandler(_documentStoreRepository, _logger)
                    ]
                )
        );

    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    public async Task<FrontendResponse> Upsert(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _upsertSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API GET by id requests
    /// </summary>
    public async Task<FrontendResponse> GetById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _getByIdSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    public async Task<FrontendResponse> UpdateById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _updateSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    public async Task<FrontendResponse> DeleteById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _deleteByIdSteps.Value.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }
}
