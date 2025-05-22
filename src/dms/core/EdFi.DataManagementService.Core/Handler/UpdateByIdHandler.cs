// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;
using static EdFi.DataManagementService.Core.Handler.Utility;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles an update request that has made it through the middleware pipeline steps.
/// </summary>
internal class UpdateByIdHandler(
    IDocumentStoreRepository _documentStoreRepository,
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    IApiSchemaProvider _apiSchemaProvider,
    IAuthorizationServiceFactory authorizationServiceFactory
) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpdateByIdHandler - {TraceId}", context.FrontendRequest.TraceId.Value);
        Trace.Assert(context.ParsedBody != null, "Unexpected null Body on Frontend Request from PUT");

        var updateCascadeHandler = new UpdateCascadeHandler(_apiSchemaProvider, _logger);

        var updateResult = await _resiliencePipeline.ExecuteAsync(async t =>
            await _documentStoreRepository.UpdateDocumentById(
                new UpdateRequest(
                    DocumentUuid: context.PathComponents.DocumentUuid,
                    ResourceInfo: context.ResourceInfo,
                    DocumentInfo: context.DocumentInfo,
                    EdfiDoc: context.ParsedBody,
                    DocumentSecurityElements: context.DocumentSecurityElements,
                    TraceId: context.FrontendRequest.TraceId,
                    UpdateCascadeHandler: updateCascadeHandler,
                    ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                        context.AuthorizationStrategyEvaluators,
                        context.AuthorizationSecurableInfo,
                        authorizationServiceFactory,
                        _logger
                    ),
                    ResourceAuthorizationPathways: context.AuthorizationPathways
                )
            )
        );

        _logger.LogDebug(
            "Document store UpdateDocumentById returned {UpdateResult}- {TraceId}",
            updateResult.GetType().FullName,
            context.FrontendRequest.TraceId.Value
        );

        context.FrontendResponse = updateResult switch
        {
            UpdateSuccess updateSuccess => new FrontendResponse(
                StatusCode: 204,
                Body: null,
                Headers: new Dictionary<string, string>()
                {
                    { "etag", context.ParsedBody["_etag"]?.ToString() ?? "" },
                },
                LocationHeaderPath: PathComponents.ToResourcePath(
                    context.PathComponents,
                    updateSuccess.ExistingDocumentUuid
                )
            ),
            UpdateFailureETagMisMatch => new FrontendResponse(
                StatusCode: 412,
                Body: FailureResponse.ForETagMisMatch(
                    "The item has been modified by another user.",
                    traceId: context.FrontendRequest.TraceId,
                    errors: new[]
                    {
                        "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                    }
                ),
                Headers: []
             ),
            UpdateFailureNotExists => new FrontendResponse(
                StatusCode: 404,
                Body: FailureResponse.ForNotFound(
                    "Resource to update was not found",
                    traceId: context.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpdateFailureDescriptorReference failure => new(
                StatusCode: 400,
                Body: FailureResponse.ForBadRequest(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: context.FrontendRequest.TraceId,
                    failure.InvalidDescriptorReferences.ToDictionary(
                        d => d.Path.Value,
                        d =>
                            d.DocumentIdentity.DocumentIdentityElements.Select(e =>
                                    $"{d.ResourceInfo.ResourceName.Value} value '{e.IdentityValue}' does not exist."
                                )
                                .ToArray()
                    ),
                    []
                ),
                Headers: []
            ),
            UpdateFailureReference failure => new FrontendResponse(
                StatusCode: 409,
                Body: FailureResponse.ForInvalidReferences(
                    failure.ReferencingDocumentInfo,
                    traceId: context.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpdateFailureIdentityConflict failure => new FrontendResponse(
                StatusCode: 409,
                Body: ForIdentityConflict(
                    [
                        $"A natural key conflict occurred when attempting to update a resource {failure.ResourceName.Value} with a duplicate key. "
                            + $"The duplicate keys and values are {string.Join(',', failure.DuplicateIdentityValues.Select(d => $"({d.Key} = {d.Value})"))}",
                    ],
                    traceId: context.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpdateFailureWriteConflict => new FrontendResponse(StatusCode: 409, Body: null, Headers: []),
            UpdateFailureImmutableIdentity failure => new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForImmutableIdentity(
                    failure.FailureMessage,
                    traceId: context.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpdateFailureNotAuthorized failure => new FrontendResponse(
                StatusCode: 403,
                Body: FailureResponse.ForForbidden(
                    traceId: context.FrontendRequest.TraceId,
                    errors: failure.ErrorMessages
                ),
                Headers: []
            ),
            UnknownFailure failure => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, context.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new FrontendResponse(
                StatusCode: 500,
                Body: ToJsonError("Unknown UpdateResult", context.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }
}
