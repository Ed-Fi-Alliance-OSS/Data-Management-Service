// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.GetResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles a get by id request that has made it through the middleware pipeline steps.
/// </summary>
internal class GetByIdHandler(ILogger _logger, ResiliencePipeline _resiliencePipeline) : IPipelineStep
{
    private const string IfNoneMatchHeaderName = "If-None-Match";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByIdHandler - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        // Resolve repository from the per-request scoped service provider
        var documentStoreRepository =
            requestInfo.ScopedServiceProvider.GetRequiredService<IDocumentStoreRepository>();

        var getResult = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "get",
            requestInfo.FrontendRequest.TraceId,
            r => IsRetryableResult(r),
            r => r is GetSuccess,
            async ct => await documentStoreRepository.GetDocumentById(CreateGetRequest(requestInfo)),
            requestInfo
        );
        _logger.LogDebug(
            "Document store GetDocumentById returned {GetResult}- {TraceId}",
            getResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = getResult switch
        {
            GetSuccess success => CreateSuccessResponse(requestInfo, success.EdfiDoc),
            GetFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            GetFailureNotImplemented failure => new FrontendResponse(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            GetFailureSecurityConfiguration failure => CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics
            ),
            // Returns 500 to match ODS/API behavior: after retries are exhausted for a deadlock,
            // the client receives a generic system error rather than a retryable status code.
            GetFailureRetryable => new FrontendResponse(
                StatusCode: 500,
                Body: FailureResponse.ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            GetFailureNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForForbidden(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    errors: notAuthorized.ErrorMessages,
                    hints: notAuthorized.Hints
                ),
                Headers: []
            ),
            GetFailureRelationshipNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForRelationshipAuthorization(
                    requestInfo.FrontendRequest.TraceId,
                    notAuthorized.RelationshipFailure
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            GetFailureNamespaceNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UnknownFailure failure => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown GetResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }

    private static FrontendResponse CreateSuccessResponse(RequestInfo requestInfo, JsonNode edfiDoc)
    {
        string servedEtag = RequireServedEtag(edfiDoc);

        if (TryCreateNotModified(requestInfo, servedEtag, out FrontendResponse notModified))
        {
            return notModified;
        }

        var contentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType is not null
            ? ProfileHeaderParser.BuildProfileContentType(
                requestInfo.ResourceSchema.ResourceName.Value,
                requestInfo.ProfileContext.ProfileName,
                ProfileUsageType.Readable
            )
            : "application/json";

        return new FrontendResponse(
            StatusCode: 200,
            Body: edfiDoc,
            Headers: new() { ["etag"] = servedEtag },
            ContentType: contentType
        );
    }

    private static string RequireServedEtag(JsonNode edfiDoc)
    {
        if (
            edfiDoc["_etag"] is not JsonValue etagValue
            || etagValue.GetValueKind() is not JsonValueKind.String
        )
        {
            throw new InvalidOperationException(
                "A successful get-by-id repository result must contain a non-empty string '_etag' value."
            );
        }

        string servedEtag = etagValue.GetValue<string>();
        if (string.IsNullOrWhiteSpace(servedEtag))
        {
            throw new InvalidOperationException(
                "A successful get-by-id repository result must contain a non-empty string '_etag' value."
            );
        }

        return servedEtag;
    }

    /// <summary>
    /// Determines whether a conditional GET's If-None-Match header is satisfied by the served etag,
    /// meaning the client's cached representation is still current. Returns true and produces a 304
    /// response (no body) when so; otherwise returns false and the caller proceeds with a normal 200.
    /// </summary>
    private static bool TryCreateNotModified(
        RequestInfo requestInfo,
        string servedEtag,
        out FrontendResponse response
    )
    {
        response = null!;

        if (!requestInfo.FrontendRequest.Headers.TryGetValue(IfNoneMatchHeaderName, out var rawHeaderValue))
        {
            return false;
        }

        // RFC 9110 §13.1.2 wildcard: a bare (unquoted) "*" means "if any representation exists" -- and
        // since reaching this method means the resource exists, the precondition is false. This must be
        // detected from the RAW header value before ParseConditionalTagList strips quotes, which would
        // otherwise turn a quoted "*" (an ordinary opaque tag) into the wildcard.
        bool isWildcard = string.Equals(rawHeaderValue, "*", StringComparison.Ordinal);

        IReadOnlyList<string> clientTags = isWildcard
            ? []
            : EtagValue.ParseConditionalTagList(rawHeaderValue);
        if (!isWildcard && clientTags.Count == 0)
        {
            return false;
        }

        // Full-tag comparison against the entire served etag (ContentVersion plus variantKey), not a
        // projection: If-None-Match is representation-sensitive, so a client tag that differs only in
        // the variantKey tail (format/profile/links/content-coding) must not match.
        // EtagMatchProjection is a write-side (If-Match) concern and does not apply here.
        bool matches =
            isWildcard || clientTags.Any(t => string.Equals(t, servedEtag, StringComparison.Ordinal));

        if (!matches)
        {
            return false;
        }

        response = new FrontendResponse(
            StatusCode: 304,
            Body: null,
            Headers: new() { ["etag"] = servedEtag },
            ContentType: null
        );
        return true;
    }

    private static IGetRequest CreateGetRequest(RequestInfo requestInfo)
    {
        var mappingSet = RequireMappingSet(requestInfo, "get by id");

        return new RelationalGetRequest(
            DocumentUuid: requestInfo.PathComponents.DocumentUuid,
            ResourceInfo: requestInfo.ResourceInfo,
            MappingSet: mappingSet,
            AuthorizationContext: RelationalAuthorizationContext.Create(requestInfo.ClientAuthorizations),
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            TraceId: requestInfo.FrontendRequest.TraceId,
            ReadableProfileProjectionContext: CreateReadableProfileProjectionContext(requestInfo),
            ResponseContentCoding: GetServedEtagContentCoding(requestInfo)
        );
    }
}
