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
/// to the context if it does.
/// </summary>
internal class ValidateEndpointMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ValidateEndpointMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        ProjectSchema? projectSchema = context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            context.PathComponents.ProjectNamespace
        );

        if (projectSchema == null)
        {
            _logger.LogDebug(
                "Invalid resource project namespace in '{EndpointName}' - {TraceId}",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId.Value
            );
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: $"Invalid resource '{context.PathComponents.EndpointName}'.",
                Headers: []
            );
            return;
        }

        context.ProjectSchema = projectSchema;

        JsonNode? resourceSchemaNode = context.ProjectSchema.FindResourceSchemaNodeByEndpointName(
            context.PathComponents.EndpointName
        );

        if (resourceSchemaNode == null)
        {
            _logger.LogDebug(
                "Invalid resource name in '{EndpointName}' - {TraceId}",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId.Value
            );
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: ForNotFound("The specified data could not be found.", context.FrontendRequest.TraceId),
                Headers: [],
                ContentType: "application/problem+json"
            );
            return;
        }

        context.ResourceSchema = new(resourceSchemaNode);

        await next();
    }
}
