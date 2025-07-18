// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.GetResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles a get by id request that has made it through the middleware pipeline steps.
/// </summary>
internal class GetByIdHandler(
    IDocumentStoreRepository _documentStoreRepository,
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    IAuthorizationServiceFactory authorizationServiceFactory
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByIdHandler - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        var getResult = await _resiliencePipeline.ExecuteAsync(async t =>
            await _documentStoreRepository.GetDocumentById(
                new GetRequest(
                    DocumentUuid: requestInfo.PathComponents.DocumentUuid,
                    ResourceInfo: requestInfo.ResourceInfo,
                    ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                        requestInfo.AuthorizationStrategyEvaluators,
                        requestInfo.AuthorizationSecurableInfo,
                        authorizationServiceFactory,
                        _logger
                    ),
                    TraceId: requestInfo.FrontendRequest.TraceId
                )
            )
        );

        _logger.LogDebug(
            "Document store GetDocumentById returned {GetResult}- {TraceId}",
            getResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId
        );

        requestInfo.FrontendResponse = getResult switch
        {
            GetSuccess success => new FrontendResponse(StatusCode: 200, Body: success.EdfiDoc, Headers: []),
            GetFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            GetFailureNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForForbidden(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    errors: notAuthorized.ErrorMessages,
                    hints: notAuthorized.Hints
                ),
                Headers: []
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
}
