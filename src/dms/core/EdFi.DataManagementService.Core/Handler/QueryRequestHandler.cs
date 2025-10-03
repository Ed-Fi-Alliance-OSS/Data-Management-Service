// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

internal class QueryRequestHandler(
    IServiceProvider _serviceProvider,
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering QueryRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolve query handler from service provider within request scope
        var queryHandler = _serviceProvider.GetRequiredService<IQueryHandler>();

        var queryResult = await _resiliencePipeline.ExecuteAsync(async t =>
            await queryHandler.QueryDocuments(
                new QueryRequest(
                    ResourceInfo: requestInfo.ResourceInfo,
                    QueryElements: requestInfo.QueryElements,
                    AuthorizationSecurableInfo: requestInfo.AuthorizationSecurableInfo,
                    AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
                    PaginationParameters: requestInfo.PaginationParameters,
                    TraceId: requestInfo.FrontendRequest.TraceId
                )
            )
        );

        _logger.LogDebug(
            "QueryHandler returned {QueryResult}- {TraceId}",
            queryResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = queryResult switch
        {
            QuerySuccess success => new FrontendResponse(
                StatusCode: 200,
                Body: success.EdfiDocs,
                Headers: requestInfo.PaginationParameters.TotalCount
                    ? new() { { "Total-Count", (success.TotalCount ?? 0).ToString() } }
                    : []
            ),
            QueryFailureKnownError => new FrontendResponse(StatusCode: 400, Body: null, Headers: []),
            UnknownFailure failure => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown QueryResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }
}
