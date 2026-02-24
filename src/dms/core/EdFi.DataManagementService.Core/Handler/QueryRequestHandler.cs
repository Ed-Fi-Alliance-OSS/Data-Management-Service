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
using Polly.Timeout;
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

        int attemptCount = 0;
        try
        {
            var queryResult = await _resiliencePipeline.ExecuteAsync(async _ =>
            {
                attemptCount++;
                return await queryHandler.QueryDocuments(
                    new QueryRequest(
                        ResourceInfo: requestInfo.ResourceInfo,
                        QueryElements: requestInfo.QueryElements,
                        AuthorizationSecurableInfo: requestInfo.AuthorizationSecurableInfo,
                        AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
                        PaginationParameters: requestInfo.PaginationParameters,
                        TraceId: requestInfo.FrontendRequest.TraceId
                    )
                );
            });

            if (queryResult is QueryFailureRetryable)
            {
                _logger.LogError(
                    "All deadlock retry attempts exhausted for query after {AttemptCount} attempts - {TraceId}",
                    attemptCount,
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }
            else if (attemptCount > 1)
            {
                _logger.LogWarning(
                    "Deadlock resolved after {RetryCount} retries for query - {TraceId}",
                    attemptCount - 1,
                    requestInfo.FrontendRequest.TraceId.Value
                );
            }

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
                QueryFailureRetryable => new FrontendResponse(
                    StatusCode: 503,
                    Body: ToJsonError(
                        "Request could not be completed due to database contention",
                        requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                ),
                QueryFailureKnownError => new FrontendResponse(StatusCode: 400, Body: null, Headers: []),
                UnknownFailure failure => new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                    Headers: []
                ),
                _ => new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError("Unknown QueryResult", requestInfo.FrontendRequest.TraceId),
                    Headers: []
                ),
            };
        }
        catch (TimeoutRejectedException ex)
        {
            _logger.LogError(
                ex,
                "Operation timed out after {AttemptCount} attempts for query - {TraceId}",
                attemptCount,
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: ToJsonError(
                    "Request timed out due to database contention",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
        }
    }
}
