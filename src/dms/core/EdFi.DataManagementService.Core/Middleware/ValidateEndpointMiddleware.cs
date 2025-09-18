// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates resource endpoint exists, adding the corresponding ProjectSchema and ResourceSchemas
/// to the requestData if it does.
/// </summary>
internal class ValidateEndpointMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateEndpointMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        ProjectSchema? projectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            requestInfo.PathComponents.ProjectEndpointName
        );

        if (projectSchema == null)
        {
            _logger.LogDebug(
                "Invalid resource project namespace in '{EndpointName}' - {TraceId}",
                requestInfo.PathComponents.EndpointName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: $"Invalid resource '{requestInfo.PathComponents.EndpointName}'.",
                Headers: []
            );
            return;
        }

        requestInfo.ProjectSchema = projectSchema;

        JsonNode? resourceSchemaNode = requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(
            requestInfo.PathComponents.EndpointName
        );

        if (resourceSchemaNode == null)
        {
            _logger.LogDebug(
                "Invalid resource name in '{EndpointName}' - {TraceId}",
                requestInfo.PathComponents.EndpointName,
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: ForNotFound(
                    "The specified data could not be found.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        requestInfo.ResourceSchema = new(resourceSchemaNode);

        await next();
    }
}
