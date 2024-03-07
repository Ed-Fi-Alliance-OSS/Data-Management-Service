// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Api.Backend.UpdateResult;

namespace EdFi.DataManagementService.Api.Core.Handler;

/// <summary>
/// Handles an update request that has made it through the middleware pipeline steps.
/// </summary>
public class UpdateByIdHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpdateByIdHandler - {TraceId}", context.FrontendRequest.TraceId);

        UpdateResult result = await _documentStoreRepository.UpdateDocumentById(
            new(
                ReferentialId: new(Guid.Empty),
                DocumentUuid: context.PathComponents.DocumentUuid,
                ResourceInfo: context.ResourceInfo,
                DocumentInfo: context.DocumentInfo,
                EdfiDoc: new JsonObject(),
                validateDocumentReferencesExist: false,
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
            UpdateSuccess => new(StatusCode: 204, Body: null),
            UpdateFailureNotExists => new(StatusCode: 404, Body: null),
            UpdateFailureReference failure => new(StatusCode: 409, Body: failure.ReferencingDocumentInfo),
            UpdateFailureIdentityConflict failure
                => new(StatusCode: 400, Body: failure.ReferencingDocumentInfo),
            UpdateFailureWriteConflict failure => new(StatusCode: 409, Body: failure.FailureMessage),
            UpdateFailureImmutableIdentity failure => new(StatusCode: 409, Body: failure.FailureMessage),
            UpdateFailureCascadeRequired => new(StatusCode: 400, Body: null),
            UnknownFailure failure => new(StatusCode: 500, Body: failure.FailureMessage),
            _ => new(StatusCode: 500, Body: "Unknown UpdateResult")
        };
    }
}
