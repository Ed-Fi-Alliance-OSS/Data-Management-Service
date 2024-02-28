// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Backend;
using EdFi.DataManagementService.Core.Pipeline;
using static EdFi.DataManagementService.Api.Backend.UpsertResult;

namespace EdFi.DataManagementService.Api.Core.Handler;

/// <summary>
/// Handles an upsert request that has made it through the middleware pipeline steps.
/// </summary>
public class UpsertHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpsertHandler - {TraceId}", context.FrontendRequest.TraceId);

        UpsertResult result = await _documentStoreRepository.UpsertDocument(
            new(
                ReferentialId: new("ReferentialId placeholder"),
                ResourceInfo: context.ResourceInfo,
                DocumentInfo: context.DocumentInfo,
                EdfiDoc: new JsonObject(),
                validateDocumentReferencesExist: false,
                TraceId: context.FrontendRequest.TraceId
            )
        );

        _logger.LogDebug(
            "Document store UpsertDocument returned {UpsetResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            InsertSuccess => new(StatusCode: 201, Body: null),
            UpdateSuccess => new(StatusCode: 200, Body: null),
            UpsertFailureReference failure => new(StatusCode: 409, Body: failure.ReferencingDocumentInfo),
            UpsertFailureIdentityConflict failure
                => new(StatusCode: 400, Body: failure.ReferencingDocumentInfo),
            UpsertFailureWriteConflict failure => new(StatusCode: 409, Body: failure.FailureMessage),
            UnknownFailure failure => new(StatusCode: 500, Body: failure.FailureMessage),
            _ => new(StatusCode: 500, Body: "Unknown UpsertResult")
        };
    }
}
