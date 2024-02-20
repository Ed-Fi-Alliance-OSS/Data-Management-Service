// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Core.Handler;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core;

/// <summary>
/// The facade a frontend uses to access DMS Core.
///
/// The intent of this design is to provide a web framework-independent interface
/// for 1) ease of testing and 2) ease of supporting future frontends e.g.
/// AWS Lambda, Azure functions, etc.
/// </summary>
public class CoreFacade(IApiSchemaLoader _apiSchemaLoader, ILogger<CoreFacade> _logger) : ICoreFacade
{
    /// <summary>
    /// The pipeline steps to satisfy an upsert request
    /// </summary>
    private readonly PipelineProvider _upsertSteps = new PipelineProvider()
        .StartWith(new ApiSchemaLoadingMiddleware(_apiSchemaLoader, _logger))
        .AndThen(new ParsePathMiddleware(_logger))
        .AndThen(new ValidateEndpointMiddleware(_logger))
        .AndThen(new ValidateDocumentMiddleware(_logger))
        .AndThen(new BuildResourceInfoMiddleware(_logger))
        .AndThen(new UpsertHandler(_logger));

    /// <summary>
    /// The pipeline steps to satisfy a get by id request
    /// </summary>
    private readonly PipelineProvider _getByIdSteps = new PipelineProvider()
        .StartWith(new ApiSchemaLoadingMiddleware(_apiSchemaLoader, _logger))
        .AndThen(new ParsePathMiddleware(_logger))
        .AndThen(new ValidateEndpointMiddleware(_logger))
        .AndThen(new BuildResourceInfoMiddleware(_logger))
        .AndThen(new GetByIdHandler(_logger));

    /// <summary>
    /// The pipeline steps to satisfy an update request
    /// </summary>
    private readonly PipelineProvider _updateSteps = new PipelineProvider()
        .StartWith(new ApiSchemaLoadingMiddleware(_apiSchemaLoader, _logger))
        .AndThen(new ParsePathMiddleware(_logger))
        .AndThen(new ValidateEndpointMiddleware(_logger))
        .AndThen(new ValidateDocumentMiddleware(_logger))
        .AndThen(new BuildResourceInfoMiddleware(_logger))
        .AndThen(new UpdateByIdHandler(_logger));

    /// <summary>
    /// The pipeline steps to satisfy a delete by id request
    /// </summary>
    private readonly PipelineProvider _deleteByIdSteps = new PipelineProvider()
        .StartWith(new ApiSchemaLoadingMiddleware(_apiSchemaLoader, _logger))
        .AndThen(new ParsePathMiddleware(_logger))
        .AndThen(new ValidateEndpointMiddleware(_logger))
        .AndThen(new BuildResourceInfoMiddleware(_logger))
        .AndThen(new DeleteByIdHandler(_logger));

    /// <summary>
    /// DMS entry point for API upsert requests
    /// </summary>
    public async Task<FrontendResponse> Upsert(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _upsertSteps.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API GET by id requests
    /// </summary>
    public async Task<FrontendResponse> GetById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _getByIdSteps.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API PUT requests, which are "by id"
    /// </summary>
    public async Task<FrontendResponse> UpdateById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _updateSteps.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }

    /// <summary>
    /// DMS entry point for all API DELETE requests, which are "by id"
    /// </summary>
    public async Task<FrontendResponse> DeleteById(FrontendRequest frontendRequest)
    {
        PipelineContext pipelineContext = new(frontendRequest);
        await _deleteByIdSteps.Run(pipelineContext);
        return pipelineContext.FrontendResponse;
    }
}
