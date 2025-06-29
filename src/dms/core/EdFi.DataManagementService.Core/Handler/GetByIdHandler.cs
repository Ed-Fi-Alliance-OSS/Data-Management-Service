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
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug("Entering GetByIdHandler - {TraceId}", requestData.FrontendRequest.TraceId.Value);

        var getResult = await _resiliencePipeline.ExecuteAsync(async t =>
            await _documentStoreRepository.GetDocumentById(
                new GetRequest(
                    DocumentUuid: requestData.PathComponents.DocumentUuid,
                    ResourceInfo: requestData.ResourceInfo,
                    ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                        requestData.AuthorizationStrategyEvaluators,
                        requestData.AuthorizationSecurableInfo,
                        authorizationServiceFactory,
                        _logger
                    ),
                    TraceId: requestData.FrontendRequest.TraceId
                )
            )
        );

        _logger.LogDebug(
            "Document store GetDocumentById returned {GetResult}- {TraceId}",
            getResult.GetType().FullName,
            requestData.FrontendRequest.TraceId
        );

        requestData.FrontendResponse = getResult switch
        {
            GetSuccess success => new FrontendResponse(StatusCode: 200, Body: success.EdfiDoc, Headers: []),
            GetFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            GetFailureNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForForbidden(
                    traceId: requestData.FrontendRequest.TraceId,
                    errors: notAuthorized.ErrorMessages,
                    hints: notAuthorized.Hints
                ),
                Headers: []
            ),
            UnknownFailure failure => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, requestData.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown GetResult", requestData.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }
}
