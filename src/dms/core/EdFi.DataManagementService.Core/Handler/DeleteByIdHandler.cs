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
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles a delete request that has made it through the middleware pipeline steps.
/// </summary>
internal class DeleteByIdHandler(
    IDocumentStoreRepository _documentStoreRepository,
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    IAuthorizationServiceFactory authorizationServiceFactory
) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug("Entering DeleteByIdHandler - {TraceId}", requestData.FrontendRequest.TraceId.Value);

        var deleteResult = await _resiliencePipeline.ExecuteAsync(async t =>
            await _documentStoreRepository.DeleteDocumentById(
                new DeleteRequest(
                    DocumentUuid: requestData.PathComponents.DocumentUuid,
                    ResourceInfo: requestData.ResourceInfo,
                    TraceId: requestData.FrontendRequest.TraceId,
                    ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                        requestData.AuthorizationStrategyEvaluators,
                        requestData.AuthorizationSecurableInfo,
                        authorizationServiceFactory,
                        _logger
                    ),
                    ResourceAuthorizationPathways: requestData.AuthorizationPathways,
                    DeleteInEdOrgHierarchy: (
                        requestData.ProjectSchema.EducationOrganizationTypes.Contains(
                            requestData.ResourceSchema.ResourceName
                        )
                    ),
                    Headers: requestData.FrontendRequest.Headers
                )
            )
        );

        _logger.LogDebug(
            "Document store DeleteDocumentById returned {DeleteResult}- {TraceId}",
            deleteResult.GetType().FullName,
            requestData.FrontendRequest.TraceId.Value
        );

        requestData.FrontendResponse = deleteResult switch
        {
            DeleteSuccess => new FrontendResponse(StatusCode: 204, Body: null, Headers: []),
            DeleteFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            DeleteFailureNotAuthorized notAuthorized => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForForbidden(
                    traceId: requestData.FrontendRequest.TraceId,
                    errors: notAuthorized.ErrorMessages
                ),
                Headers: []
            ),
            DeleteFailureReference failure => new FrontendResponse(
                StatusCode: 409,
                Body: FailureResponse.ForDataConflict(
                    failure.ReferencingDocumentResourceNames,
                    traceId: requestData.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            DeleteFailureWriteConflict => new FrontendResponse(StatusCode: 409, Body: null, Headers: []),
            DeleteFailureETagMisMatch => new FrontendResponse(
                StatusCode: 412,
                Body: FailureResponse.ForETagMisMatch(
                    "The item has been modified by another user.",
                    traceId: requestData.FrontendRequest.TraceId,
                    errors: new[]
                    {
                        "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved.",
                    }
                ),
                Headers: []
            ),
            UnknownFailure failure => new(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, requestData.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown DeleteResult", requestData.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }
}
