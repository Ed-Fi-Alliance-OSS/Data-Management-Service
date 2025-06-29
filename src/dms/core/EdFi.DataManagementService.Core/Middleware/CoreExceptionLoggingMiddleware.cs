// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Logs requests and responses, and converts exceptions to 500s
/// </summary>
internal class CoreExceptionLoggingMiddleware(ILogger _logger) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        try
        {
            _logger.LogDebug(
                "Entering CoreExceptionLoggingMiddleware - {TraceId}",
                requestData.FrontendRequest.TraceId.Value
            );
            await next();
        }
        catch (AuthorizationException ex)
        {
            requestData.FrontendResponse = new FrontendResponse(
                StatusCode: (int)HttpStatusCode.Forbidden,
                Body: FailureResponse.ForForbidden(
                    traceId: requestData.FrontendRequest.TraceId,
                    errors: [ex.Message]
                ),
                Headers: [],
                ContentType: "application/problem+json"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown Error - {TraceId}", requestData.FrontendRequest.TraceId.Value);

            // Replace the frontend response (if any) with a 500 error
            requestData.FrontendResponse = new FrontendResponse(
                StatusCode: 500,
                Body: new JsonObject
                {
                    ["message"] =
                        "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                    ["traceId"] = requestData.FrontendRequest.TraceId.Value,
                },
                Headers: []
            );
        }
    }
}
