// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.GetResult;

namespace EdFi.DataManagementService.Core.Handler;
internal class GetByResourceNameHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByResourceNameHandler - {TraceId}", context.FrontendRequest.TraceId);

        int offset = int.TryParse(context.FrontendRequest.QueryParameters["offset"], out int o) ? o : 0;
        int limit = int.TryParse(context.FrontendRequest.QueryParameters["limit"], out int l) ? l : 25;

        GetResult result = await _documentStoreRepository.GetDocumentByResourceName(
            new GetRequest(
                DocumentUuid: context.PathComponents.DocumentUuid,
                ResourceInfo: context.ResourceInfo,
                TraceId: context.FrontendRequest.TraceId
            )
            , offset, limit);

        _logger.LogDebug(
            "Document store GetByResourceNameHandler returned {GetResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            GetSuccess success => new FrontendResponse(StatusCode: 200, Body: success.EdfiDoc.ToString(), Headers: []),
            GetFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            UnknownFailure failure => new FrontendResponse(StatusCode: 500, Body: failure.FailureMessage, Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown GetResult", Headers: [])
        };
    }
}
