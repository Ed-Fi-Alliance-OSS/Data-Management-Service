// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Core.External.Backend.GetResult;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles a get by id request that has made it through the middleware pipeline steps.
/// </summary>
internal class GetByIdHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByIdHandler - {TraceId}", context.FrontendRequest.TraceId);

        GetResult result = await _documentStoreRepository.GetDocumentById(
            new GetRequest(
                DocumentUuid: context.PathComponents.DocumentUuid,
                ResourceInfo: context.ResourceInfo,
                TraceId: context.FrontendRequest.TraceId
            )
        );

        _logger.LogDebug(
            "Document store GetDocumentById returned {GetResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            GetSuccess success => new FrontendResponse(StatusCode: 200, Body: success.EdfiDoc.ToJsonString(), Headers: []),
            GetFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            UnknownFailure failure => new FrontendResponse(StatusCode: 500, Body: failure.FailureMessage.ToJsonError(), Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown GetResult".ToJsonError(), Headers: [])
        };
    }
}
