// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
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
    /// <summary>
    /// The response header carrying the token for the page after this one, as published by the Ed-Fi
    /// cursor-paging client contract.
    /// </summary>
    /// <summary>
    /// The response header name. Internal so OpenAPI assembly publishes the header this handler emits
    /// rather than a second spelling of it.
    /// </summary>
    internal const string NextPageTokenHeaderName = "Next-Page-Token";

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
            async ct => await queryHandler.QueryDocuments(CreateQueryRequest(requestInfo), ct),
            requestInfo
        );
        _logger.LogDebug(
            "QueryHandler returned {QueryResult}- {TraceId}",
            queryResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = queryResult switch
        {
            QuerySuccess success => CreateSuccessResponse(requestInfo, success),
            QueryFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            QueryFailureSecurityConfiguration failure => CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics
            ),
            QueryFailureNamespaceNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            // Returns 500 to match ODS/API behavior: after retries are exhausted for a deadlock,
            // the client receives a generic system error rather than a retryable status code.
            QueryFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            QueryFailureKnownError => new FrontendResponse(StatusCode: 400, Body: null, Headers: []),
            UnknownFailure failure => CreateUnknownFailureResponse(
                _logger,
                requestInfo,
                failure.FailureMessage
            ),
            _ => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError("Unknown QueryResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }

    private static FrontendResponse CreateSuccessResponse(RequestInfo requestInfo, QuerySuccess success)
    {
        var contentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType is not null
            ? ProfileHeaderParser.BuildProfileContentType(
                requestInfo.ResourceSchema.ResourceName.Value,
                requestInfo.ProfileContext.ProfileName,
                ProfileUsageType.Readable
            )
            : "application/json";

        Dictionary<string, string> headers = requestInfo.CollectionPaging.IncludesTotalCount
            ? new() { { "Total-Count", (success.TotalCount ?? 0).ToString() } }
            : [];

        if (TryCreateNextPageToken(requestInfo, success, out var nextPageToken))
        {
            headers.Add(NextPageTokenHeaderName, nextPageToken);
        }

        return new FrontendResponse(
            StatusCode: 200,
            Body: success.EdfiDocs,
            Headers: headers,
            ContentType: contentType
        );
    }

    /// <summary>
    /// Whether this page can hand the client a token for the page after it, and what that token is.
    /// </summary>
    /// <remarks>
    /// The one gate both resource families pass through: regular-resource and descriptor results reach
    /// it as the same <see cref="QuerySuccess"/>, so neither can acquire a header rule of its own. It
    /// asks what page selection chose, never what the response body contains — a page whose selected
    /// rows were all deleted before hydration still advances the walk past them, and a client that
    /// stopped on an empty body would stop early. A page that selected nothing, or one whose ordering
    /// key was not DocumentId, has nothing to anchor a continuation on. At
    /// <see cref="long.MaxValue"/> there is no next range to name, so the codec reports no token and
    /// the header is omitted.
    /// </remarks>
    private static bool TryCreateNextPageToken(
        RequestInfo requestInfo,
        QuerySuccess success,
        [NotNullWhen(true)] out string? nextPageToken
    )
    {
        nextPageToken = null;

        if (success.HighestSelectedDocumentId is not { } highestSelectedDocumentId)
        {
            return false;
        }

        if (!success.AllowsDocumentIdContinuation)
        {
            return false;
        }

        // A cursor request keeps its own upper bound, which is how a walk that entered through a
        // partition stays inside that partition. A traditional request carried no bound, so a walk
        // entered from one is unbounded above.
        long inclusiveMaximum = requestInfo.CollectionPaging is CollectionPaging.Cursor cursor
            ? cursor.Range.InclusiveMaximum
            : long.MaxValue;

        return PageTokenCodec.TryCreateNextPageToken(
                highestSelectedDocumentId,
                inclusiveMaximum,
                out nextPageToken
            ) && nextPageToken is not null;
    }

    private static IQueryRequest CreateQueryRequest(RequestInfo requestInfo)
    {
        var mappingSet = RequireMappingSet(requestInfo, "query");

        return new RelationalQueryRequest(
            ResourceInfo: requestInfo.ResourceInfo,
            AuthorizationContext: RelationalAuthorizationContext.Create(requestInfo.ClientAuthorizations),
            MappingSet: mappingSet,
            QueryElements: requestInfo.QueryElements,
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            Paging: requestInfo.CollectionPaging,
            TraceId: requestInfo.FrontendRequest.TraceId,
            TenantKey: requestInfo.FrontendRequest.Tenant ?? string.Empty,
            ReadableProfileProjectionContext: CreateReadableProfileProjectionContext(requestInfo),
            ChangeVersionRange: requestInfo.ChangeVersionRange,
            ResponseContentCoding: GetServedEtagContentCoding(requestInfo)
        );
    }
}
