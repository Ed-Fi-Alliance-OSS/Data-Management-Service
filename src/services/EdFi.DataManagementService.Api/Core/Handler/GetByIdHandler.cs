// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Api.Backend.GetResult;

namespace EdFi.DataManagementService.Api.Core.Handler;

/// <summary>
/// Handles a get by id request that has made it through the middleware pipeline steps.
/// </summary>
public class GetByIdHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByIdHandler - {TraceId}", context.FrontendRequest.TraceId);

        GetResult result = await _documentStoreRepository.GetDocumentById(new(
            DocumentUuid: context.PathComponents.DocumentUuid,
            ResourceInfo: context.ResourceInfo,
            TraceId: context.FrontendRequest.TraceId
        ));

        context.FrontendResponse = result switch
        {
            GetSuccess success => new(StatusCode: 200, Body: success.EdfiDoc.ToString()),
            GetFailureNotExists => new(StatusCode: 404, Body: null),
            UnknownFailure failure => new(StatusCode: 500, Body: failure.FailureMessage),
            _ => new(StatusCode: 500, Body: "Unknown GetResult")
        };
    }
}
