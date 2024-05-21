// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles an upsert request that has made it through the middleware pipeline steps.
/// </summary>
internal class UpsertHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    private static string ToResourcePath(PathComponents p, IDocumentUuid u)
    {
        return $"/{p.ProjectNamespace.Value}/{p.EndpointName.Value}/{u.Value}";
    }

    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpsertHandler - {TraceId}", context.FrontendRequest.TraceId);

        // A document uuid that will be assigned if this is a new document
        DocumentUuid candidateDocumentUuid = new(Guid.NewGuid().ToString());

        UpsertResult result = await _documentStoreRepository.UpsertDocument(
            new UpsertRequest(
                ReferentialId: new ReferentialId(Guid.Empty),
                ResourceInfo: context.ResourceInfo,
                DocumentInfo: context.DocumentInfo,
                EdfiDoc: context.FrontendRequest.Body ?? new JsonObject(),
                validateDocumentReferencesExist: false,
                TraceId: context.FrontendRequest.TraceId,
                DocumentUuid: candidateDocumentUuid
            )
        );

        _logger.LogDebug(
            "Document store UpsertDocument returned {UpsertResult}- {TraceId}",
            result.GetType().FullName,
            context.FrontendRequest.TraceId
        );

        context.FrontendResponse = result switch
        {
            InsertSuccess
                => new(
                    StatusCode: 201,
                    Body: null,
                    Headers: [],
                    LocationHeaderPath: ToResourcePath(
                        context.PathComponents,
                        ((InsertSuccess)result).NewDocumentUuid
                    )
                ),
            UpdateSuccess
                => new(
                    StatusCode: 200,
                    Body: null,
                    Headers: [],
                    LocationHeaderPath: ToResourcePath(
                        context.PathComponents,
                        ((UpdateSuccess)result).ExistingDocumentUuid
                    )
                ),
            UpsertFailureReference failure
                => new(StatusCode: 409, Body: failure.ReferencingDocumentInfo, Headers: []),
            UpsertFailureIdentityConflict failure
                => new(StatusCode: 400, Body: failure.ReferencingDocumentInfo, Headers: []),
            UpsertFailureWriteConflict failure
                => new(StatusCode: 409, Body: failure.FailureMessage, Headers: []),
            UnknownFailure failure => new(StatusCode: 500, Body: failure.FailureMessage, Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown UpsertResult", Headers: [])
        };
    }
}
