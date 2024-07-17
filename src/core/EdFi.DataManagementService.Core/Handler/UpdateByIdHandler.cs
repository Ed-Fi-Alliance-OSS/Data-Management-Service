// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles an update request that has made it through the middleware pipeline steps.
/// </summary>
internal class UpdateByIdHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpdateByIdHandler - {TraceId}", context.FrontendRequest.TraceId);
        Trace.Assert(context.ParsedBody != null, "Unexpected null Body on Frontend Request from PUT");

        UpdateResult result = await _documentStoreRepository.UpdateDocumentById(
            new UpdateRequest(
                DocumentUuid: context.PathComponents.DocumentUuid,
                ResourceInfo: context.ResourceInfo,
                DocumentInfo: context.DocumentInfo,
                EdfiDoc: context.ParsedBody,
                TraceId: context.FrontendRequest.TraceId
            )
        );

        _logger.LogDebug(
            "Document store UpdateDocumentById returned {UpdateResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            UpdateSuccess updateSuccess
                => new FrontendResponse(
                    StatusCode: 204,
                    Body: null,
                    Headers: [],
                    LocationHeaderPath: PathComponents.ToResourcePath(
                        context.PathComponents,
                        updateSuccess.ExistingDocumentUuid
                    )
                ),
            UpdateFailureNotExists
                => new FrontendResponse(
                    StatusCode: 404,
                    Body: JsonSerializer.Serialize(
                        FailureResponse.ForNotFound("Resource to update was not found")
                    ),
                    Headers: []
                ),
            UpdateFailureReference failure
                => new FrontendResponse(StatusCode: 409, Body: JsonSerializer.Serialize(FailureResponse.ForInvalidReferences(failure.ReferencingDocumentInfo)), Headers: []),
            UpdateFailureIdentityConflict failure
                => new FrontendResponse(StatusCode: 400, Body: failure.ReferencingDocumentInfo, Headers: []),
            UpdateFailureWriteConflict => new FrontendResponse(StatusCode: 409, Body: null, Headers: []),
            UpdateFailureImmutableIdentity failure
                => new FrontendResponse(
                    StatusCode: 400,
                    Body: JsonSerializer.Serialize(
                        FailureResponse.ForImmutableIdentity(failure.FailureMessage)
                    ),
                    Headers: []
                ),
            UpdateFailureCascadeRequired
                => new FrontendResponse(StatusCode: 400, Body: null, Headers: []),
            UnknownFailure failure
                => new FrontendResponse(StatusCode: 500, Body: failure.FailureMessage.ToJsonError(), Headers: []),
            _ => new FrontendResponse(StatusCode: 500, Body: "Unknown UpdateResult".ToJsonError(), Headers: [])
        };
    }
}
