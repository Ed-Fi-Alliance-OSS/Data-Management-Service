// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class CoerceDateTimesMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug("Entering CoerceDateTimesMiddleware - {TraceId}", context.FrontendRequest.TraceId);

        foreach (string path in context.ResourceSchema.DateTimeJsonPaths.Select(path => path.Value))
        {
            IEnumerable<JsonNode?> jsonNodes = context.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceDateToDateTime();
            }
        }

        await next();
    }
}
