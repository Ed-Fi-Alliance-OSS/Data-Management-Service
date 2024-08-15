// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Pipeline;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Middleware
{
    /// <summary>
    /// For boolean and numeric properties that were submitted as strings, i.e. surrounded with double quotes,
    /// this middleware tries to coerce those back to their proper type.
    /// </summary>
    internal class CoerceFromStringsMiddleware(ILogger logger) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            logger.LogDebug("Entering CoerceFromStringsMiddleware - {TraceId}", context.FrontendRequest.TraceId);

            foreach (string path in context.ResourceSchema.BooleanJsonPaths.Select(path => path.Value))
            {
                IEnumerable<JsonNode?> jsonNodes = context.ParsedBody.SelectNodesFromArrayPath(path, logger);
                foreach (JsonNode? jsonNode in jsonNodes)
                {
                    jsonNode?.TryCoerceStringToBoolean();
                }
            }

            foreach (string path in context.ResourceSchema.NumericJsonPaths.Select(path => path.Value))
            {
                IEnumerable<JsonNode?> jsonNodes = context.ParsedBody.SelectNodesFromArrayPath(path, logger);
                foreach (JsonNode? jsonNode in jsonNodes)
                {
                    jsonNode?.TryCoerceStringToNumber();
                }
            }

            await next();
        }
    }
}
