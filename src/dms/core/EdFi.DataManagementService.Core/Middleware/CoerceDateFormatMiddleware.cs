// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that coerces slash-formatted dates (e.g., 5/1/2009) to ISO-8601 format (e.g., 2009-05-01)
/// for all date fields identified by the resource schema's dateJsonPaths and dateTimeJsonPaths.
/// </summary>
internal class CoerceDateFormatMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering CoerceDateFormatMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Paths for Date only
        foreach (string path in requestInfo.ResourceSchema.DateJsonPaths.Select(path => path.Value))
        {
            IEnumerable<JsonNode?> jsonNodes = requestInfo.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceSlashDateToIso8601();
            }
        }

        // Paths for DateTime
        foreach (string path in requestInfo.ResourceSchema.DateTimeJsonPaths.Select(path => path.Value))
        {
            IEnumerable<JsonNode?> jsonNodes = requestInfo.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceSlashDateToIso8601();
            }
        }

        await next();
    }
}
