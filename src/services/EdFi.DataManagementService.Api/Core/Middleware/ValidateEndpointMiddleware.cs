// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;

/// <summary>
/// Validates resource endpoint exists, adding the corresponding ProjectSchema and ResourceSchemas
/// to the context if it does.
/// </summary>
public class ValidateEndpointMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogInformation("ValidateEndpointMiddleware");

        JsonNode? projectSchemaNode = context.ApiSchemaDocument.FindProjectSchemaNode(context.PathComponents.ProjectNamespace);
        if (projectSchemaNode == null)
        {
            context.FrontendResponse = new(StatusCode: 404, Body: $"Invalid resource '{context.PathComponents.EndpointName}'.");
            return;
        }

        context.ProjectSchema = new(projectSchemaNode);

        JsonNode? resourceSchemaNode = context.ProjectSchema.FindResourceSchemaNode(context.PathComponents.EndpointName);

        if (resourceSchemaNode == null)
        {
            context.FrontendResponse = new(StatusCode: 404, Body: $"Invalid resource '{context.PathComponents.EndpointName}'.");
            return;
        }

        context.ResourceSchema = new(resourceSchemaNode);

        await next();
    }
}
