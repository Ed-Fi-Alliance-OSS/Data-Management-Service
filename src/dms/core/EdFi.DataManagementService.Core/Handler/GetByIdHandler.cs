// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
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
        var contentType = requestInfo.ProfileContext?.ResourceProfile.ReadContentType is not null
            ? ProfileHeaderParser.BuildProfileContentType(
                requestInfo.ResourceSchema.ResourceName.Value,
                requestInfo.ProfileContext.ProfileName,
                ProfileUsageType.Readable
            )
            : "application/json";

        string? servedEtag = edfiDoc["_etag"]?.GetValue<string>();

        Dictionary<string, string> headers = !string.IsNullOrEmpty(servedEtag)
            ? new() { ["etag"] = servedEtag }
            : [];

        return new FrontendResponse(
            StatusCode: 200,
            Body: edfiDoc,
            Headers: headers,
            ContentType: contentType
        );
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
            ReadableProfileProjectionContext: CreateReadableProfileProjectionContext(requestInfo)
        );
    }
}
