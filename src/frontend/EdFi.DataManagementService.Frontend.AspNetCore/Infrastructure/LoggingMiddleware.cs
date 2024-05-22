// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class LoggingMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, ILogger<LoggingMiddleware> logger)
    {
        try
        {
            logger.LogInformation(
                JsonSerializer.Serialize(
                    new
                    {
                        method = context.Request.Method,
                        path = context.Request.Path.Value,
                        traceId = context.TraceIdentifier,
                        clientId = context.Request.Host
                    }
                )
            );

            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unknown Error - {TraceId}", context.TraceIdentifier);

            var response = context.Response;
            if (!response.HasStarted)
            {
                response.ContentType = "application/json";
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await response.WriteAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            message = "The server encountered an unexpected condition that prevented it from fulfilling the request.",
                            traceId = context.TraceIdentifier
                        }
                    )
                );
            }
        }
    }
}
