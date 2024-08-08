// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles an upsert request that has made it through the middleware pipeline steps.
/// </summary>
internal class UpsertHandler(IDocumentStoreRepository _documentStoreRepository, ILogger _logger)
    : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering UpsertHandler - {TraceId}", context.FrontendRequest.TraceId);

        // A document uuid that will be assigned if this is a new document
        DocumentUuid candidateDocumentUuid = new(Guid.NewGuid());

        UpsertResult result = await _documentStoreRepository.UpsertDocument(
            new UpsertRequest(
                ResourceInfo: context.ResourceInfo,
                DocumentInfo: context.DocumentInfo,
                EdfiDoc: context.ParsedBody,
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
            InsertSuccess insertSuccess
                => new FrontendResponse(
                    StatusCode: 201,
                    Body: null,
                    Headers: [],
                    LocationHeaderPath: PathComponents.ToResourcePath(
                        context.PathComponents,
                        insertSuccess.NewDocumentUuid
                    )
                ),
            UpdateSuccess updateSuccess
                => new(
                    StatusCode: 200,
                    Body: null,
                    Headers: [],
                    LocationHeaderPath: PathComponents.ToResourcePath(
                        context.PathComponents,
                        updateSuccess.ExistingDocumentUuid
                    )
                ),
            UpsertFailureDescriptorReference failure
                => new(
                    StatusCode: 400,
                    Body: JsonSerializer.Serialize(
                        ForBadRequest(
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
                        )
                    ),
                    Headers: []
                ),
            UpsertFailureReference failure
                => new(
                    StatusCode: 409,
                    Body: JsonSerializer.Serialize(
                        ForInvalidReferences(failure.ResourceNames, traceId: context.FrontendRequest.TraceId)
                    ),
                    Headers: []
                ),
            UpsertFailureIdentityConflict failure
                => new FrontendResponse(
                    StatusCode: 409,
                    Body: JsonSerializer.Serialize(
                        ForIdentityConflict(
                            [
                                $"A natural key conflict occurred when attempting to create a new resource {failure.ResourceName.Value} with a duplicate key. "
                                    + $"The duplicate keys and values are {string.Join(',', failure.DuplicateIdentityValues.Select(d => $"({d.Key} = {d.Value})"))}"
                            ],
                            traceId: context.FrontendRequest.TraceId
                        )
                    ),
                    Headers: []
                ),
            UpsertFailureWriteConflict => new(StatusCode: 409, Body: null, Headers: []),
            UnknownFailure failure
                => new(StatusCode: 500, Body: failure.FailureMessage.ToJsonError(), Headers: []),
            _ => new(StatusCode: 500, Body: "Unknown UpsertResult".ToJsonError(), Headers: [])
        };
    }
}
