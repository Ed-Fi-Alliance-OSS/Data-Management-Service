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
/// Logs requests and responses, and converts exceptions to 500s
/// </summary>
internal class CoreLoggingMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug("FrontendRequest: {FrontendRequest}", context.FrontendRequest);

        try
        {
            await next();
            _logger.LogDebug("FrontendResponse: {FrontendResponse}", context.FrontendResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown Error - {TraceId}", context.FrontendRequest.TraceId);

            // Replace the frontend response (if any) with a 500 error
            context.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] = "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                    ["traceId"] = context.FrontendRequest.TraceId.Value
                },
                Headers: []
            );
        }
    }
}
