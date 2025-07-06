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
/// For boolean and numeric properties that were submitted as strings, i.e. surrounded with double quotes,
/// this middleware tries to coerce those back to their proper type.
/// </summary>
internal class CoerceFromStringsMiddleware(ILogger logger) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering CoerceFromStringsMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        foreach (string path in requestInfo.ResourceSchema.BooleanJsonPaths.Select(path => path.Value))
        {
            IEnumerable<JsonNode?> jsonNodes = requestInfo.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceStringToBoolean();
            }
        }

        var decimalPaths = requestInfo
            .ResourceSchema.DecimalPropertyValidationInfos.Select(i => i.Path.Value)
            .ToList();
        foreach (string path in decimalPaths)
        {
            IEnumerable<JsonNode?> jsonNodes = requestInfo.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceStringToDecimal();
            }
        }

        var numericPaths = requestInfo
            .ResourceSchema.NumericJsonPaths.Select(path => path.Value)
            .Except(decimalPaths);
        foreach (string path in numericPaths)
        {
            IEnumerable<JsonNode?> jsonNodes = requestInfo.ParsedBody.SelectNodesFromArrayPath(path, logger);
            foreach (JsonNode? jsonNode in jsonNodes)
            {
                jsonNode?.TryCoerceStringToNumber();
            }
        }

        await next();
    }
}
