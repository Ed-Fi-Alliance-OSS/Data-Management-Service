// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.PartitionResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Serves a partitions request by calculating boundaries and encoding each as a page token.
/// </summary>
/// <param name="_maximumPageSize">
/// The configured maximum page size, from which the minimum partition size is derived. Supplied at
/// composition so the size a partition is measured against is the same page size a walk of that
/// partition will use.
/// </param>
/// <remarks>
/// This is the only place partition token text is created. The backend contracts, planners, and SQL
/// compilers receive and return typed <see cref="External.Model.CursorRange" /> values, which is what
/// keeps a compiler from ever seeing client-supplied text.
/// </remarks>
internal class PartitionRequestHandler(
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    int _maximumPageSize
) : IPipelineStep
{
    /// <summary>
    /// The response body member carrying one token per partition. Internal so OpenAPI assembly publishes
    /// the member this handler writes rather than a second spelling of it.
    /// </summary>
    internal const string PageTokensMember = "pageTokens";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering PartitionRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolved from the per-request scoped service provider, as every other handler resolves its
        // backend seam.
        var partitionQueryHandler =
            requestInfo.ScopedServiceProvider.GetRequiredService<IPartitionQueryHandler>();

        var partitionResult = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "partitions",
            requestInfo.FrontendRequest.TraceId,
            r => IsRetryableResult(r),
            r => r is PartitionSuccess,
            async ct => await partitionQueryHandler.QueryPartitions(CreatePartitionRequest(requestInfo), ct),
            requestInfo
        );

        _logger.LogDebug(
            "PartitionQueryHandler returned {PartitionResult}- {TraceId}",
            partitionResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = partitionResult switch
        {
            PartitionSuccess success => CreateSuccessResponse(success),
            PartitionFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            PartitionFailureSecurityConfiguration failure => CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics
            ),
            PartitionFailureNamespaceNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            // Returns 500 to match the GET-many path: after retries are exhausted for a deadlock, the
            // client receives a generic system error rather than a retryable status code.
            PartitionFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UnknownPartitionFailure failure => CreateUnknownFailureResponse(
                _logger,
                requestInfo,
                failure.FailureMessage
            ),
            _ => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError("Unknown PartitionResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }

    /// <summary>
    /// Encodes each boundary range as a page token. Always <c>application/json</c> and never a profile
    /// media type: the body carries tokens, not documents, so no readable profile can shape it. No
    /// <c>Total-Count</c> and no <c>Next-Page-Token</c>, because a boundary set is not a page and has
    /// no successor.
    /// </summary>
    private static FrontendResponse CreateSuccessResponse(PartitionSuccess success)
    {
        JsonArray pageTokens = [];

        foreach (var range in success.Ranges)
        {
            pageTokens.Add(PageTokenCodec.Encode(range));
        }

        return new FrontendResponse(
            StatusCode: 200,
            Body: new JsonObject { [PageTokensMember] = pageTokens },
            Headers: [],
            ContentType: "application/json"
        );
    }

    /// <summary>
    /// Builds the backend request. The minimum partition size comes from the configured maximum page
    /// size rather than from request state: <see cref="RequestInfo.PaginationParameters" /> is never
    /// assigned on this pipeline, and the size a partition is measured against must be the same page
    /// size a walk of that partition will use.
    /// </summary>
    private IPartitionRequest CreatePartitionRequest(RequestInfo requestInfo)
    {
        var mappingSet = RequireMappingSet(requestInfo, "partitions");

        return new RelationalPartitionRequest(
            ResourceInfo: requestInfo.ResourceInfo,
            AuthorizationContext: RelationalAuthorizationContext.Create(requestInfo.ClientAuthorizations),
            MappingSet: mappingSet,
            QueryElements: requestInfo.QueryElements,
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            RequestedPartitionCount: RequireRequestedPartitionCount(requestInfo),
            MinimumPartitionSize: CursorPagingLimits.MinimumPartitionSize(_maximumPageSize),
            TraceId: requestInfo.FrontendRequest.TraceId,
            ChangeVersionRange: requestInfo.ChangeVersionRange,
            TenantKey: requestInfo.FrontendRequest.Tenant ?? string.Empty
        );
    }

    /// <summary>
    /// The validated count reaching this handler. Absent means the validating middleware did not run or
    /// did not accept the request, which is a pipeline composition fault rather than a client one, so it
    /// throws instead of quietly substituting a default this handler would have no basis to choose.
    /// </summary>
    private static int RequireRequestedPartitionCount(RequestInfo requestInfo) =>
        requestInfo.RequestedPartitionCount
        ?? throw new InvalidOperationException(
            "A validated partition count is required before executing partitions requests. Ensure "
                + "ValidatePartitionQueryMiddleware runs before the partitions handler."
        );
}
