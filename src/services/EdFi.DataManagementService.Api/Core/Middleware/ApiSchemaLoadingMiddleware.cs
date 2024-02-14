// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.ApiSchema;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Middleware;


public class ApiSchemaLoadingMiddleware : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        // Hardcoded and synchronous way to read the API Schema file - every time!
        JsonNode apiSchemaRootNode =
            JsonNode.Parse(File.ReadAllText($"{AppContext.BaseDirectory}/ApiSchema/DataStandard-5.0.0-ApiSchema.json")) ??
            throw new InvalidOperationException("Unable to read and parse Api Schema file.");

        context.ApiSchemaDocument = new ApiSchemaDocument(apiSchemaRootNode);

        await next();
    }
}
