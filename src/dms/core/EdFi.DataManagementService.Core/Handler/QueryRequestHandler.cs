// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.QueryResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

internal class QueryRequestHandler(ILogger _logger, ResiliencePipeline _resiliencePipeline) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering QueryRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolve query handler from the per-request scoped service provider
        var queryHandler = requestInfo.ScopedServiceProvider.GetRequiredService<IQueryHandler>();

        var queryResult = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "query",
            requestInfo.FrontendRequest.TraceId,
            r => IsRetryableResult(r),
            r => r is QuerySuccess,
            async ct => await queryHandler.QueryDocuments(CreateQueryRequest(requestInfo)),
            requestInfo
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
            QueryFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            // Returns 500 to match ODS/API behavior: after retries are exhausted for a deadlock,
            // the client receives a generic system error rather than a retryable status code.
            QueryFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
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

    private static ReadableProfileProjectionContext? CreateReadableProfileProjectionContext(
        RequestInfo requestInfo
    )
    {
        var readContentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType;

        if (readContentType is null)
        {
            return null;
        }

        return new ReadableProfileProjectionContext(
            readContentType,
            IReadableProfileProjector.ExtractIdentityPropertyNames(
                requestInfo.ResourceSchema.IdentityJsonPaths
            )
        );
    }

    private static IQueryRequest CreateQueryRequest(RequestInfo requestInfo)
    {
        return requestInfo.MappingSet is not null
            ? new RelationalQueryRequest(
                ResourceInfo: requestInfo.ResourceInfo,
                MappingSet: requestInfo.MappingSet,
                QueryElements: requestInfo.QueryElements,
                AuthorizationSecurableInfo: requestInfo.AuthorizationSecurableInfo,
                AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
                PaginationParameters: requestInfo.PaginationParameters,
                TraceId: requestInfo.FrontendRequest.TraceId,
                ReadableProfileProjectionContext: CreateReadableProfileProjectionContext(requestInfo)
            )
            : new QueryRequest(
                ResourceInfo: requestInfo.ResourceInfo,
                QueryElements: requestInfo.QueryElements,
                AuthorizationSecurableInfo: requestInfo.AuthorizationSecurableInfo,
                AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
                PaginationParameters: requestInfo.PaginationParameters,
                TraceId: requestInfo.FrontendRequest.TraceId
            );
    }
}
