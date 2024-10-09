// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates resource endpoint exists, adding the corresponding ProjectSchema and ResourceSchemas
/// to the context if it does.
/// </summary>
internal class ValidateEndpointMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("Entering ValidateEndpointMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        JsonNode? projectSchemaNode = context.ApiSchemaDocument.FindProjectSchemaNode(
            context.PathComponents.ProjectNamespace
        );
        if (projectSchemaNode == null)
        {
            _logger.LogDebug(
                "Invalid resource project namespace in '{EndpointName}' - {TraceId}",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: $"Invalid resource '{context.PathComponents.EndpointName}'.",
                Headers: []
            );
            return;
        }

        context.ProjectSchema = new(projectSchemaNode, _logger);

        JsonNode? resourceSchemaNode = context.ProjectSchema.FindResourceSchemaNode(
            context.PathComponents.EndpointName
        );

        if (resourceSchemaNode == null)
        {
            _logger.LogDebug(
                "Invalid resource name in '{EndpointName}' - {TraceId}",
                context.PathComponents.EndpointName,
                context.FrontendRequest.TraceId
            );
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 404,
                Body: $"Invalid resource '{context.PathComponents.EndpointName}'.",
                Headers: []
            );
            return;
        }

        context.ResourceSchema = new(resourceSchemaNode);

        await next();
    }
}
