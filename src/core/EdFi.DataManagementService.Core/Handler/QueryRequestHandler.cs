// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;

namespace EdFi.DataManagementService.Core.Handler;

internal class QueryRequestHandler(IQueryHandler _queryHandler, ILogger _logger, ResiliencePipeline _resiliencePipeline) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering QueryRequestHandler - {TraceId}", context.FrontendRequest.TraceId);

        var queryResult = await _resiliencePipeline.ExecuteAsync(async t => await _queryHandler.QueryDocuments(
            new QueryRequest(
                ResourceInfo: context.ResourceInfo,
                SearchParameters: new Dictionary<string, string>(),
                PaginationParameters: context.PaginationParameters,
                TraceId: context.FrontendRequest.TraceId
            )
        ));

        _logger.LogDebug(
            "QueryHandler returned {QueryResult}- {TraceId}",
            queryResult.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = queryResult switch
        {
            QuerySuccess success
                =>
                new FrontendResponse(
                    StatusCode: 200,
                    Body: new JsonArray(success.EdfiDocs).ToString(),
                    Headers:
                        context.PaginationParameters.totalCount ? new()
                        {
                        {"Total-Count", (success.TotalCount ?? 0).ToString()}
                    } : []
                ),
            QueryFailureInvalidQuery => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            UnknownFailure failure
                => new FrontendResponse(StatusCode: 500, Body: failure.FailureMessage.ToJsonError(), Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown QueryResult".ToJsonError(), Headers: [])
        };
    }
}
