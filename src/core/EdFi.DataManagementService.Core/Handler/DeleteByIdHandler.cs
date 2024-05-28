// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Model;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles a delete request that has made it through the middleware pipeline steps.
/// </summary>
internal class DeleteByIdHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering DeleteByIdHandler - {TraceId}", context.FrontendRequest.TraceId);

        DeleteResult result = await _documentStoreRepository.DeleteDocumentById(
            new DeleteRequest(
                DocumentUuid: context.PathComponents.DocumentUuid,
                ResourceInfo: context.ResourceInfo,
                validateNoReferencesToDocument: false,
                TraceId: context.FrontendRequest.TraceId
            )
        );

        _logger.LogDebug(
            "Document store DeleteDocumentById returned {DeleteResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            DeleteSuccess => new FrontendResponse(StatusCode: 200, Body: null, Headers: []),
            DeleteFailureNotExists => new FrontendResponse(StatusCode: 404, Body: null, Headers: []),
            DeleteFailureReference failure => new FrontendResponse(StatusCode: 409, Body: failure.ReferencingDocumentInfo, Headers: []),
            DeleteFailureWriteConflict failure => new FrontendResponse(StatusCode: 409, Body: failure.FailureMessage, Headers: []),
            UnknownFailure failure => new(StatusCode: 500, Body: failure.FailureMessage, Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown DeleteResult", Headers: [])
        };
    }
}
