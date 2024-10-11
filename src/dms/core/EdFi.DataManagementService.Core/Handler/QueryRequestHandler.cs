// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

internal class QueryRequestHandler(IQueryHandler _queryHandler, ILogger _logger, ResiliencePipeline _resiliencePipeline) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering QueryRequestHandler - {TraceId}", context.FrontendRequest.TraceId);

        var queryResult = await _resiliencePipeline.ExecuteAsync(async t => await _queryHandler.QueryDocuments(
            new QueryRequest(
                ResourceInfo: context.ResourceInfo,
                QueryElements: context.QueryElements,
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
                => new FrontendResponse(
                    StatusCode: 200,
                    Body: success.EdfiDocs,
                    Headers: context.PaginationParameters.TotalCount
                        ? new() { { "Total-Count", (success.TotalCount ?? 0).ToString() } }
                        : []
                ),
            QueryFailureInvalidQuery => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            UnknownFailure failure
                => new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(failure.FailureMessage, context.FrontendRequest.TraceId),
                    Headers: []
                ),
            _
                => new(
                    StatusCode: 500,
                    Body: ToJsonError("Unknown QueryResult", context.FrontendRequest.TraceId),
                    Headers: []
                )
        };
    }
}
